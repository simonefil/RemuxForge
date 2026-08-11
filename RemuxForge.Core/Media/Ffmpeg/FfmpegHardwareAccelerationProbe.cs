using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Verifica i device hardware realmente inizializzabili dalla sessione corrente
    /// </summary>
    public class FfmpegHardwareAccelerationProbe
    {
        #region Costanti

        /// <summary>
        /// Timeout per ciascun comando del probe
        /// </summary>
        private const int PROBE_TIMEOUT_MS = 10000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso ffmpeg da verificare
        /// </summary>
        private readonly string _ffmpegPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        public FfmpegHardwareAccelerationProbe(string ffmpegPath)
        {
            this._ffmpegPath = ffmpegPath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i metodi ffmpeg e mantiene solo quelli che creano un device
        /// </summary>
        /// <returns>Metodi disponibili ed eventuale errore</returns>
        public FfmpegHardwareAccelerationProbeResult Probe()
        {
            FfmpegHardwareAccelerationProbeResult result = new FfmpegHardwareAccelerationProbeResult();
            ProcessResult listResult;
            List<string> candidates;

            if (string.IsNullOrEmpty(this._ffmpegPath) || !File.Exists(this._ffmpegPath))
            {
                result.ErrorMessage = "Percorso ffmpeg non valido";
                return result;
            }

            listResult = ProcessRunner.Run(this._ffmpegPath, new string[] { "-hide_banner", "-hwaccels" }, PROBE_TIMEOUT_MS);
            if (listResult.ExitCode != 0)
            {
                result.ErrorMessage = "Impossibile leggere i metodi hardware da ffmpeg";
                return result;
            }

            candidates = ParseMethods(listResult.Stdout + "\n" + listResult.Stderr);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (this.CanInitializeDevice(candidates[i]))
                    result.Methods.Add(candidates[i]);
            }

            if (result.Methods.Count == 0)
                result.ErrorMessage = "Nessun metodo di accelerazione hardware ffmpeg inizializzabile";

            return result;
        }

        /// <summary>
        /// Parsa l'output di ffmpeg -hwaccels mantenendo identificatori espliciti validi
        /// </summary>
        /// <param name="output">Output stdout e stderr</param>
        /// <returns>Metodi candidati senza duplicati</returns>
        public static List<string> ParseMethods(string output)
        {
            List<string> result = new List<string>();
            string[] lines = (output ?? "").Replace("\r", "").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string method = lines[i].Trim().ToLowerInvariant();
                if (FfmpegConfig.IsValidHardwareAccelerationMethod(method) && !result.Contains(method))
                    result.Add(method);
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Verifica la creazione reale del device senza dipendere da un file utente
        /// </summary>
        /// <param name="method">Metodo ffmpeg da provare</param>
        /// <returns>True se ffmpeg inizializza il device</returns>
        private bool CanInitializeDevice(string method)
        {
            string deviceDefinition = method + "=remuxforge_probe";
            string[] arguments = new string[]
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-init_hw_device", deviceDefinition,
                "-f", "lavfi",
                "-i", "nullsrc=s=16x16:d=0.04",
                "-frames:v", "1",
                "-f", "null",
                "-"
            };
            ProcessResult processResult = ProcessRunner.Run(this._ffmpegPath, arguments, PROBE_TIMEOUT_MS);
            return processResult.ExitCode == 0;
        }

        #endregion
    }
}
