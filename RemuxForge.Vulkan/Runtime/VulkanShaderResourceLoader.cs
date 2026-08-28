using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace RemuxForge.Vulkan.Runtime
{
    /// <summary>
    /// Loads embedded SPIR-V shaders and verifies their identity and ABI against the manifest
    /// </summary>
    internal sealed class VulkanShaderResourceLoader
    {
        #region Static Fields

        /// <summary>
        /// Lazily loads and caches the embedded shader manifest
        /// </summary>
        private static readonly Lazy<ShaderManifest> s_manifest = new Lazy<ShaderManifest>(LoadManifest, true);

        #endregion

        #region Public Methods

        /// <summary>
        /// Loads an embedded shader after validating the host ABI expectations
        /// </summary>
        /// <param name="shaderName">Manifest and embedded-resource name of the shader without the <c>.spv</c> extension</param>
        /// <param name="bindingCount">Number of descriptor bindings expected by the host</param>
        /// <param name="pushConstantSize">Push-constant byte size expected by the host</param>
        /// <param name="sha256">Receives the uppercase hexadecimal SHA-256 hash of the loaded SPIR-V bytes</param>
        /// <returns>The complete embedded SPIR-V binary</returns>
        public byte[] Load(string shaderName, uint bindingCount, uint pushConstantSize, out string sha256)
        {
            ShaderManifestEntry entry = s_manifest.Value.Get(shaderName);
            ValidateAbi(shaderName, entry, bindingCount, pushConstantSize);
            return this.LoadCore(shaderName, entry, out sha256);
        }

        /// <summary>
        /// Gets the manifest SHA-256 hash for a shader after validating its host ABI expectations
        /// </summary>
        /// <param name="shaderName">Manifest name of the shader</param>
        /// <param name="bindingCount">Number of descriptor bindings expected by the host</param>
        /// <param name="pushConstantSize">Push-constant byte size expected by the host</param>
        /// <returns>The uppercase hexadecimal SHA-256 hash recorded for the shader</returns>
        public string GetSha256(string shaderName, uint bindingCount, uint pushConstantSize)
        {
            ShaderManifestEntry entry = s_manifest.Value.Get(shaderName);
            ValidateAbi(shaderName, entry, bindingCount, pushConstantSize);
            return entry.Sha256;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Verifies that host descriptor and push-constant expectations match a manifest entry
        /// </summary>
        /// <param name="shaderName">Name used to identify the shader in an incompatibility message</param>
        /// <param name="entry">Manifest entry containing the expected ABI values</param>
        /// <param name="bindingCount">Descriptor-binding count supplied by the host</param>
        /// <param name="pushConstantSize">Push-constant size supplied by the host</param>
        private static void ValidateAbi(string shaderName, ShaderManifestEntry entry, uint bindingCount, uint pushConstantSize)
        {
            if (entry.BindingCount != bindingCount)
                throw new VulkanShaderIncompatibleException(
                    "Binding ABI non compatibile per " + shaderName + ": manifest=" +
                    entry.BindingCount.ToString(CultureInfo.InvariantCulture) + ", host=" +
                    bindingCount.ToString(CultureInfo.InvariantCulture));
            if (entry.PushConstantSize != pushConstantSize)
                throw new VulkanShaderIncompatibleException(
                    "Push constant ABI non compatibile per " + shaderName + ": manifest=" +
                    entry.PushConstantSize.ToString(CultureInfo.InvariantCulture) + ", host=" +
                    pushConstantSize.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Reads, validates and hashes the embedded SPIR-V resource for a manifest entry
        /// </summary>
        /// <param name="shaderName">Name of the shader and suffix used to locate its embedded resource</param>
        /// <param name="entry">Manifest entry containing the expected SHA-256 hash</param>
        /// <param name="sha256">Receives the uppercase hexadecimal SHA-256 hash of the loaded bytes</param>
        /// <returns>The complete embedded SPIR-V binary</returns>
        private byte[] LoadCore(string shaderName, ShaderManifestEntry entry, out string sha256)
        {
            if (string.IsNullOrEmpty(shaderName))
                throw new ArgumentNullException(nameof(shaderName));
            string resourceName = "RemuxForge.Vulkan.Shaders." + shaderName + ".spv";
            Assembly assembly = typeof(VulkanShaderResourceLoader).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new VulkanShaderIncompatibleException("SPIR-V resource not found: " + resourceName);
                using (MemoryStream destination = new MemoryStream())
                {
                    stream.CopyTo(destination);
                    byte[] result = destination.ToArray();
                    Validate(result, shaderName);
                    sha256 = Convert.ToHexString(SHA256.HashData(result));
                    if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new VulkanShaderIncompatibleException("SPIR-V hash does not match the manifest for " + shaderName);
                    return result;
                }
            }
        }

        /// <summary>
        /// Loads and parses the embedded shader manifest
        /// </summary>
        /// <returns>The validated manifest containing shader entries and build metadata</returns>
        private static ShaderManifest LoadManifest()
        {
            const string RESOURCE_NAME = "RemuxForge.Vulkan.Shaders.shader-manifest.txt";
            Assembly assembly = typeof(VulkanShaderResourceLoader).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(RESOURCE_NAME))
            {
                if (stream == null)
                    throw new VulkanShaderIncompatibleException("Embedded shader manifest not found.");
                using (MemoryStream copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    byte[] bytes = copy.ToArray();
                    using (StreamReader reader = new StreamReader(new MemoryStream(bytes)))
                        return ShaderManifest.Parse(reader, SHA256.HashData(bytes));
                }
            }
        }

        /// <summary>
        /// Verifies the minimum size and magic number required by a SPIR-V binary
        /// </summary>
        /// <param name="code">SPIR-V bytes read from the embedded resource</param>
        /// <param name="shaderName">Name used to identify the shader in an incompatibility message</param>
        private static void Validate(byte[] code, string shaderName)
        {
            if (code.Length < 20 || code.Length % 4 != 0)
                throw new VulkanShaderIncompatibleException("Invalid SPIR-V length for " + shaderName);
            uint magic = BitConverter.ToUInt32(code, 0);
            if (magic != 0x07230203u)
                throw new VulkanShaderIncompatibleException("Invalid SPIR-V magic for " + shaderName);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a copy of the SHA-256 hash of the embedded manifest
        /// </summary>
        public byte[] ManifestHash { get { return (byte[])s_manifest.Value.Hash.Clone(); } }

        /// <summary>
        /// Gets the toolchain and build metadata recorded in the embedded manifest
        /// </summary>
        public IReadOnlyDictionary<string, string> BuildMetadata { get { return s_manifest.Value.Metadata; } }

        #endregion

        #region Nested Types

        /// <summary>
        /// Stores toolchain metadata, ABI values and identities for distributed shaders
        /// </summary>
        private sealed class ShaderManifest
        {
            #region Instance Fields

            /// <summary>
            /// Maps each manifest shader name to its validated entry
            /// </summary>
            private readonly Dictionary<string, ShaderManifestEntry> _entries;

            #endregion

            #region Constructor

            /// <summary>
            /// Initializes a parsed shader manifest
            /// </summary>
            /// <param name="entries">Validated shader entries keyed by shader name</param>
            /// <param name="metadata">Toolchain and build metadata keyed by metadata name</param>
            /// <param name="hash">SHA-256 hash of the manifest bytes</param>
            private ShaderManifest(Dictionary<string, ShaderManifestEntry> entries, Dictionary<string, string> metadata, byte[] hash)
            {
                this._entries = entries;
                this.Metadata = metadata;
                this.Hash = hash;
            }

            #endregion

            #region Public Methods

            /// <summary>
            /// Gets the manifest entry for a shader name
            /// </summary>
            /// <param name="shaderName">Name of the shader to locate</param>
            /// <returns>The manifest entry associated with <paramref name="shaderName"/></returns>
            public ShaderManifestEntry Get(string shaderName)
            {
                if (!this._entries.TryGetValue(shaderName, out ShaderManifestEntry result))
                    throw new VulkanShaderIncompatibleException("Shader missing from the manifest: " + shaderName);
                return result;
            }

            /// <summary>
            /// Parses manifest text into validated shader entries and metadata
            /// </summary>
            /// <param name="reader">Reader containing the manifest text; the reader remains owned by the caller</param>
            /// <param name="hash">SHA-256 hash of the manifest bytes represented by <paramref name="reader"/></param>
            /// <returns>A parsed manifest retaining the supplied hash and the newly parsed entries and metadata</returns>
            public static ShaderManifest Parse(TextReader reader, byte[] hash)
            {
                Dictionary<string, ShaderManifestEntry> entries = new Dictionary<string, ShaderManifestEntry>(StringComparer.Ordinal);
                Dictionary<string, string> metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("tool|", StringComparison.Ordinal))
                    {
                        string[] toolParts = line.Split('|');
                        if (toolParts.Length != 3)
                            throw new VulkanShaderIncompatibleException("Invalid toolchain entry in the manifest.");
                        metadata.Add("tool." + toolParts[1], toolParts[2]);
                        continue;
                    }
                    if (line.StartsWith("options|", StringComparison.Ordinal))
                    {
                        metadata.Add("options", line.Substring(8));
                        continue;
                    }
                    if (!line.StartsWith("shader|", StringComparison.Ordinal))
                        continue;
                    string[] parts = line.Split('|');
                    if (parts.Length != 7 || parts[2] != "entry=main")
                        throw new VulkanShaderIncompatibleException("Invalid shader entry in the manifest.");
                    ShaderManifestEntry entry = new ShaderManifestEntry();
                    entry.BindingCount = ParseUInt(parts[3], "bindings=");
                    entry.PushConstantSize = ParseUInt(parts[4], "push=");
                    if (!parts[5].StartsWith("local=", StringComparison.Ordinal))
                        throw new VulkanShaderIncompatibleException("Local size missing from the manifest for " + parts[1]);
                    if (!parts[6].StartsWith("sha256=", StringComparison.Ordinal) || parts[6].Length != 71)
                        throw new VulkanShaderIncompatibleException("Invalid hash in the manifest for " + parts[1]);
                    entry.Sha256 = parts[6].Substring(7);
                    if (!entries.TryAdd(parts[1], entry))
                        throw new VulkanShaderIncompatibleException("Duplicate shader in the manifest: " + parts[1]);
                }
                if (entries.Count == 0)
                    throw new VulkanShaderIncompatibleException("The shader manifest is empty.");
                return new ShaderManifest(entries, metadata, hash);
            }

            #endregion

            #region Private Methods

            /// <summary>
            /// Parses an unsigned integer value with a required manifest prefix
            /// </summary>
            /// <param name="value">Manifest token to parse</param>
            /// <param name="prefix">Prefix that must precede the numeric portion</param>
            /// <returns>The invariant-culture unsigned integer represented by the token</returns>
            private static uint ParseUInt(string value, string prefix)
            {
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || !uint.TryParse(value.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint result))
                    throw new VulkanShaderIncompatibleException("Invalid numeric value in the manifest: " + value);
                return result;
            }

            #endregion

            #region Properties

            /// <summary>
            /// Gets the SHA-256 hash of the manifest bytes
            /// </summary>
            public byte[] Hash { get; }

            /// <summary>
            /// Gets the toolchain and build metadata parsed from the manifest
            /// </summary>
            public IReadOnlyDictionary<string, string> Metadata { get; }

            #endregion
        }

        /// <summary>
        /// Describes the ABI contract and hash of one embedded shader
        /// </summary>
        private sealed class ShaderManifestEntry
        {
            /// <summary>
            /// Gets or sets the number of descriptor bindings required by the shader
            /// </summary>
            public uint BindingCount { get; set; }

            /// <summary>
            /// Gets or sets the push-constant size required by the shader
            /// </summary>
            public uint PushConstantSize { get; set; }

            /// <summary>
            /// Gets or sets the uppercase hexadecimal SHA-256 hash of the shader binary
            /// </summary>
            public string Sha256 { get; set; }
        }

        #endregion
    }
}
