using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Transcoding
{
    /// <summary>
    /// Servizio per encoding video post-merge tramite ffmpeg
    /// </summary>
    public class VideoEncodingService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        public VideoEncodingService(string ffmpegPath)
        {
            this._ffmpegPath = ffmpegPath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue encoding video di un file secondo il profilo specificato
        /// </summary>
        /// <param name="inputFile">Percorso file MKV sorgente</param>
        /// <param name="outputFile">Percorso file MKV di output</param>
        /// <param name="profile">Profilo di encoding da applicare</param>
        /// <param name="onProgress">Callback per righe di progresso ffmpeg da stderr</param>
        /// <returns>True se encoding completato con successo</returns>
        public bool Encode(string inputFile, string outputFile, EncodingProfile profile, Action<string> onProgress)
        {
            bool success = false;
            int exitCode;
            string tempOutput;
            string passLogFile;
            string outputDir;
            // Percorso temporaneo per output (rinomina alla fine)
            tempOutput = outputFile + ".enc.tmp.mkv";

            // Multi-pass bitrate
            if (profile.RateMode == "bitrate" && profile.Passes == 2 && EncodingDefaults.HasMultiPass(profile.Codec))
            {
                // Passlog nella stessa cartella dell'output
                outputDir = Path.GetDirectoryName(outputFile);
                if (string.IsNullOrEmpty(outputDir))
                {
                    outputDir = Directory.GetCurrentDirectory();
                }
                passLogFile = Path.Combine(outputDir, "ffmpeg2pass");

                // Pass 1: solo analisi, nessun output audio/video
                List<string> pass1Args = this.BuildArguments(inputFile, profile, 1, passLogFile);
                // Pass 1 output va a null
                pass1Args.Add("-an");
                pass1Args.Add("-f");
                pass1Args.Add("null");
                pass1Args.Add(GetNullDevice());

                if (onProgress != null)
                {
                    onProgress("[ENC] Pass 1/2...");
                }

                exitCode = this.RunFfmpeg(pass1Args, onProgress);

                if (exitCode != 0)
                {
                    // Log errore pass 1
                    ConsoleHelper.Write(LogSection.Encode, LogLevel.Error, "  Pass 1 fallito (exit code: " + exitCode + ")");
                    // Cleanup file passlog
                    this.CleanupPasslogFiles(passLogFile);
                    return false;
                }

                // Pass 2: encoding effettivo con audio e sub copiati
                List<string> pass2Args = this.BuildArguments(inputFile, profile, 2, passLogFile);
                pass2Args.Add("-c:a");
                pass2Args.Add("copy");
                pass2Args.Add("-c:s");
                pass2Args.Add("copy");
                pass2Args.Add("-y");
                pass2Args.Add(tempOutput);

                if (onProgress != null)
                {
                    onProgress("[ENC] Pass 2/2...");
                }

                exitCode = this.RunFfmpeg(pass2Args, onProgress);

                // Cleanup file passlog
                this.CleanupPasslogFiles(passLogFile);
            }
            else
            {
                // Single pass: CRF, QP, o bitrate 1-pass
                List<string> args = this.BuildArguments(inputFile, profile, 0, "");
                args.Add("-c:a");
                args.Add("copy");
                args.Add("-c:s");
                args.Add("copy");
                args.Add("-y");
                args.Add(tempOutput);

                exitCode = this.RunFfmpeg(args, onProgress);
            }

            // Verifica risultato
            if (exitCode == 0 && File.Exists(tempOutput))
            {
                // Sostituisci output originale con file encoded
                if (File.Exists(outputFile))
                {
                    File.Delete(outputFile);
                }
                File.Move(tempOutput, outputFile);
                success = true;
            }
            else
            {
                // Log errore encoding
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Error, "  Encoding fallito (exit code: " + exitCode + ")");
                // Cleanup file temporaneo fallito
                FileHelper.DeleteTempFile(tempOutput);
            }

            return success;
        }

        /// <summary>
        /// Costruisce la stringa comando ffmpeg leggibile per il record
        /// </summary>
        /// <param name="inputFile">Percorso file input</param>
        /// <param name="outputFile">Percorso file output</param>
        /// <param name="profile">Profilo di encoding</param>
        /// <returns>Stringa comando ffmpeg</returns>
        public string BuildCommandString(string inputFile, string outputFile, EncodingProfile profile)
        {
            StringBuilder sb = new StringBuilder(256);
            string pixFmt;
            string tuneValue;
            sb.Append("ffmpeg -i \"").Append(inputFile).Append("\" -map 0");

            // Codec
            sb.Append(" -c:v ").Append(profile.Codec);

            // Preset
            sb.Append(" -preset ").Append(profile.Preset);

            // Tune
            tuneValue = this.ExtractTuneValue(profile.Codec, profile.Tune);
            if (tuneValue.Length > 0)
            {
                if (profile.Codec == "libsvtav1")
                {
                    // Tune va nei svtav1-params
                }
                else
                {
                    sb.Append(" -tune ").Append(tuneValue);
                }
            }

            // Profile (solo x264/x265)
            if (EncodingDefaults.HasProfile(profile.Codec) && profile.Profile != "default")
            {
                sb.Append(" -profile:v ").Append(profile.Profile);
            }

            // Pixel format
            pixFmt = this.ExtractPixelFormat(profile.BitDepth);
            if (pixFmt.Length > 0)
            {
                sb.Append(" -pix_fmt ").Append(pixFmt);
            }

            // Rate control
            if (profile.RateMode == "crf")
            {
                sb.Append(" -crf ").Append(profile.CrfQp);
            }
            else if (profile.RateMode == "qp")
            {
                // SVT-AV1 usa -qp nei svtav1-params
            }
            else if (profile.RateMode == "bitrate")
            {
                sb.Append(" -b:v ").Append(profile.Bitrate).Append("k");
            }

            // SVT-AV1 params
            if (profile.Codec == "libsvtav1")
            {
                string svtParams = this.BuildSvtAv1Params(profile, tuneValue);
                if (svtParams.Length > 0)
                {
                    sb.Append(" -svtav1-params ").Append(svtParams);
                }
            }

            // ExtraParams
            if (profile.ExtraParams.Length > 0)
            {
                sb.Append(" ").Append(profile.ExtraParams);
            }

            sb.Append(" -c:a copy -c:s copy -y \"").Append(outputFile).Append("\"");

            return sb.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce la lista argomenti ffmpeg per un pass
        /// </summary>
        /// <param name="inputFile">Percorso file input</param>
        /// <param name="profile">Profilo di encoding</param>
        /// <param name="pass">Numero pass: 0 = singolo, 1 = primo, 2 = secondo</param>
        /// <param name="passLogFile">Percorso base per file passlog (solo multi-pass)</param>
        /// <returns>Lista argomenti (senza output file)</returns>
        private List<string> BuildArguments(string inputFile, EncodingProfile profile, int pass, string passLogFile)
        {
            List<string> args = new List<string>();
            string pixFmt;
            string tuneValue;
            // Input
            args.Add("-i");
            args.Add(inputFile);

            // Mappa tutti gli stream (default ffmpeg ne prende uno per tipo)
            args.Add("-map");
            args.Add("0");
            args.Add("-map_metadata");
            args.Add("0");
            args.Add("-map_chapters");
            args.Add("0");

            // Codec
            args.Add("-c:v");
            args.Add(profile.Codec);

            // Preset
            args.Add("-preset");
            args.Add(profile.Preset);

            // Tune (solo x264/x265, non "default")
            tuneValue = this.ExtractTuneValue(profile.Codec, profile.Tune);
            if (tuneValue.Length > 0 && profile.Codec != "libsvtav1")
            {
                args.Add("-tune");
                args.Add(tuneValue);
            }

            // Profile (solo x264/x265, non "default")
            if (EncodingDefaults.HasProfile(profile.Codec) && profile.Profile != "default")
            {
                args.Add("-profile:v");
                args.Add(profile.Profile);
            }

            // Pixel format
            pixFmt = this.ExtractPixelFormat(profile.BitDepth);
            if (pixFmt.Length > 0)
            {
                args.Add("-pix_fmt");
                args.Add(pixFmt);
            }

            // Rate control
            if (profile.RateMode == "crf")
            {
                args.Add("-crf");
                args.Add(profile.CrfQp.ToString());
            }
            else if (profile.RateMode == "qp")
            {
                // Per SVT-AV1, qp va nei svtav1-params
            }
            else if (profile.RateMode == "bitrate")
            {
                args.Add("-b:v");
                args.Add(profile.Bitrate + "k");
            }

            // Multi-pass
            if (pass > 0)
            {
                args.Add("-pass");
                args.Add(pass.ToString());
                args.Add("-passlogfile");
                args.Add(passLogFile);
            }

            // SVT-AV1 params
            if (profile.Codec == "libsvtav1")
            {
                string svtParams = this.BuildSvtAv1Params(profile, tuneValue);
                if (svtParams.Length > 0)
                {
                    args.Add("-svtav1-params");
                    args.Add(svtParams);
                }
            }

            // Extra params
            if (profile.ExtraParams.Length > 0)
            {
                string[] extraParts = profile.ExtraParams.Split(' ');
                for (int i = 0; i < extraParts.Length; i++)
                {
                    string part = extraParts[i].Trim();
                    if (part.Length > 0)
                    {
                        args.Add(part);
                    }
                }
            }

            return args;
        }

        /// <summary>
        /// Costruisce la stringa svtav1-params per SVT-AV1
        /// </summary>
        /// <param name="profile">Profilo di encoding</param>
        /// <param name="tuneValue">Valore tune estratto</param>
        /// <returns>Stringa parametri svtav1 separati da ':'</returns>
        private string BuildSvtAv1Params(EncodingProfile profile, string tuneValue)
        {
            List<string> parts = new List<string>();

            // Tune
            if (tuneValue.Length > 0)
            {
                parts.Add("tune=" + tuneValue);
            }

            // QP mode
            if (profile.RateMode == "qp")
            {
                parts.Add("qp=" + profile.CrfQp);
            }

            // Film grain
            if (profile.FilmGrain > 0)
            {
                parts.Add("film-grain=" + profile.FilmGrain);
            }

            // Film grain denoise
            if (profile.FilmGrainDenoise && profile.FilmGrain > 0)
            {
                parts.Add("film-grain-denoise=1");
            }

            // Unisci con separatore ':'
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) { sb.Append(':'); }
                sb.Append(parts[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Estrae il pixel format dalla stringa BitDepth (es. "10-bit: yuv420p10le" -> "yuv420p10le")
        /// </summary>
        /// <param name="bitDepth">Stringa bit depth dal profilo</param>
        /// <returns>Nome pixel format</returns>
        private string ExtractPixelFormat(string bitDepth)
        {
            string result = "";
            int colonIdx = bitDepth.IndexOf(": ", StringComparison.Ordinal);

            if (colonIdx >= 0 && colonIdx + 2 < bitDepth.Length)
            {
                result = bitDepth.Substring(colonIdx + 2).Trim();
            }

            return result;
        }

        /// <summary>
        /// Estrae il valore tune dalla stringa UI (es. "0 - VQ (Psychovisual)" -> "0")
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <param name="tune">Stringa tune dal profilo</param>
        /// <returns>Valore tune per ffmpeg, vuoto se "default"</returns>
        private string ExtractTuneValue(string codec, string tune)
        {
            string result = "";
            if (tune == "default" || tune.Length == 0)
            {
                return result;
            }

            if (codec == "libsvtav1")
            {
                // SVT-AV1: "0 - VQ (Psychovisual)" -> "0"
                int dashIdx = tune.IndexOf(" - ", StringComparison.Ordinal);
                result = dashIdx >= 0 ? tune.Substring(0, dashIdx).Trim() : tune;
            }
            else
            {
                // x264/x265: il valore e' il nome stesso
                result = tune;
            }

            return result;
        }

        /// <summary>
        /// Esegue ffmpeg con gli argomenti specificati, leggendo stderr riga per riga per il progresso
        /// </summary>
        /// <param name="args">Lista argomenti ffmpeg</param>
        /// <param name="onProgress">Callback per righe di progresso</param>
        /// <returns>Codice di uscita del processo</returns>
        private int RunFfmpeg(List<string> args, Action<string> onProgress)
        {
            return ProcessRunner.RunWithProgress(this._ffmpegPath, args, onProgress);
        }

        /// <summary>
        /// Restituisce il device null per la piattaforma corrente
        /// </summary>
        /// <returns>/dev/null su Linux, NUL su Windows</returns>
        private static string GetNullDevice()
        {
            string result = "/dev/null";

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                result = "NUL";
            }

            return result;
        }

        /// <summary>
        /// Elimina i file passlog generati da ffmpeg 2-pass
        /// </summary>
        /// <param name="passLogFile">Percorso base passlog</param>
        private void CleanupPasslogFiles(string passLogFile)
        {
            // ffmpeg genera file come ffmpeg2pass-0.log e ffmpeg2pass-0.log.mbtree
            string[] suffixes = new string[] { "-0.log", "-0.log.mbtree", "-0.log.cutree" };

            for (int i = 0; i < suffixes.Length; i++)
            {
                FileHelper.DeleteTempFile(passLogFile + suffixes[i]);
            }
        }

        #endregion
    }
}
