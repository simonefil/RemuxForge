using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Vulkan.ShaderManifestTool
{
    /// <summary>
    /// Generates a reproducible manifest for the compiled compute shaders
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Matches descriptor binding declarations in GLSL source text
        /// </summary>
        private static readonly Regex BindingExpression = new Regex(@"binding\s*=\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Matches the compute shader local workgroup size declaration
        /// </summary>
        private static readonly Regex LocalSizeExpression = new Regex(@"local_size_x\s*=\s*(\d+)\s*,\s*local_size_y\s*=\s*(\d+)\s*,\s*local_size_z\s*=\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Validates the command-line arguments and generates the shader manifest
        /// </summary>
        /// <param name="arguments">The six command-line values required by the manifest generation workflow</param>
        /// <returns><c>0</c> when generation succeeds; otherwise, <c>1</c> after the failure has been reported to standard error</returns>
        private static int Main(string[] arguments)
        {
            try
            {
                if (arguments.Length != 6)
                    throw new ArgumentException("Uso: <shader-dir> <output-dir> <abi-file> <manifest> <target-env> <optimization>");
                Generate(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5]);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Shader manifest generation failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Builds the manifest after validating the declared ABI, shader sources and generated SPIR-V binaries
        /// </summary>
        /// <param name="shaderDirectory">Directory containing the top-level compute shader sources</param>
        /// <param name="outputDirectory">Directory containing the generated SPIR-V binaries</param>
        /// <param name="abiPath">Path to the ABI description file</param>
        /// <param name="manifestPath">Path where the generated manifest is written</param>
        /// <param name="targetEnvironment">Target environment identifier recorded in the manifest</param>
        /// <param name="optimization">Optimization setting recorded in the manifest</param>
        private static void Generate(string shaderDirectory, string outputDirectory, string abiPath, string manifestPath, string targetEnvironment, string optimization)
        {
            Dictionary<string, int> pushSizes = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> abiLines = new List<string>();
            foreach (string rawLine in File.ReadAllLines(abiPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] parts = line.Split('|');
                if (parts[0] == "shader" && parts.Length == 3)
                    pushSizes.Add(parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture));
                else if (parts[0] == "abi" && parts.Length >= 4)
                    abiLines.Add(line);
                else
                    throw new InvalidDataException("Riga ABI non valida: " + line);
            }

            List<string> lines = new List<string>();
            lines.Add("tool|glslc|" + ReadToolVersion("glslc"));
            lines.Add("tool|spirv-val|" + ReadToolVersion("spirv-val"));
            lines.Add("options|target-env=" + Sanitize(targetEnvironment) + "|optimization=" + Sanitize(optimization));
            lines.AddRange(abiLines.OrderBy(value => value, StringComparer.Ordinal));
            string[] sourcePaths = Directory.GetFiles(shaderDirectory, "*.comp", SearchOption.TopDirectoryOnly);
            Array.Sort(sourcePaths, StringComparer.Ordinal);
            foreach (string sourcePath in sourcePaths)
            {
                string name = Path.GetFileNameWithoutExtension(sourcePath);
                if (!pushSizes.TryGetValue(name, out int pushSize))
                    throw new InvalidDataException("Push constant size non dichiarata per " + name);
                string source = File.ReadAllText(sourcePath);
                int bindingCount = ResolveBindingCount(source, name);
                Match localSize = LocalSizeExpression.Match(source);
                if (!localSize.Success)
                    throw new InvalidDataException("Local size non dichiarata per " + name);
                string binaryPath = Path.Combine(outputDirectory, name + ".spv");
                if (!File.Exists(binaryPath))
                    throw new FileNotFoundException("SPIR-V mancante", binaryPath);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binaryPath)));
                lines.Add(string.Format(CultureInfo.InvariantCulture, "shader|{0}|entry=main|bindings={1}|push={2}|local={3},{4},{5}|sha256={6}", name, bindingCount, pushSize, localSize.Groups[1].Value, localSize.Groups[2].Value, localSize.Groups[3].Value, hash));
            }
            if (pushSizes.Count != sourcePaths.Length)
                throw new InvalidDataException("La tabella ABI e i sorgenti shader non hanno la stessa cardinalita'");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");
            File.WriteAllLines(manifestPath, lines, new UTF8Encoding(false));
        }

        /// <summary>
        /// Counts declared bindings and verifies that they form a zero-based contiguous sequence
        /// </summary>
        /// <param name="source">Shader source text to inspect</param>
        /// <param name="name">Shader name used in validation error messages</param>
        /// <returns>The number of distinct binding indices declared in the source</returns>
        private static int ResolveBindingCount(string source, string name)
        {
            HashSet<int> bindings = new HashSet<int>();
            foreach (Match match in BindingExpression.Matches(source))
                bindings.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            if (bindings.Count == 0)
                throw new InvalidDataException("Binding mancanti per " + name);
            for (int i = 0; i < bindings.Count; i++)
            {
                if (!bindings.Contains(i))
                    throw new InvalidDataException("Binding non contigui per " + name);
            }
            return bindings.Count;
        }

        /// <summary>
        /// Reads and normalizes the reproducible version string from a shader toolchain executable
        /// </summary>
        /// <param name="executable">Executable name or path passed to the process launcher</param>
        /// <returns>Single-line, manifest-safe version text</returns>
        private static string ReadToolVersion(string executable)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(executable, "--version");
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            using (Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Impossibile avviare " + executable))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(executable + " --version fallito: " + error);
                return Sanitize(output.Length > 0 ? output : error);
            }
        }

        /// <summary>
        /// Normalizes a value for the manifest's line-oriented text format
        /// </summary>
        /// <param name="value">Text to normalize</param>
        /// <returns>Trimmed text with carriage returns, line feeds and field delimiters replaced</returns>
        private static string Sanitize(string value)
        {
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
        }
    }
}
