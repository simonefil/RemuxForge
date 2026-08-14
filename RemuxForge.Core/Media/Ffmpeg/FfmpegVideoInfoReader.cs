using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Legge la durata e il frame rate di un video tramite ffmpeg
    /// </summary>
    public class FfmpegVideoInfoReader
    {
        #region Variabili statiche

        /// <summary>
        /// Riconosce la durata riportata nella riga principale dell'output ffmpeg
        /// </summary>
        private static readonly Regex s_durationRegex = new Regex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// Riconosce il tag DURATION nei metadati dello stream video
        /// </summary>
        private static readonly Regex s_metadataDurationRegex = new Regex(@"DURATION\s*:\s*(\d{2}):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Riconosce il tag NUMBER_OF_FRAMES nei metadati dello stream video
        /// </summary>
        private static readonly Regex s_numberOfFramesRegex = new Regex(@"NUMBER_OF_FRAMES\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Riconosce il frame rate espresso con il suffisso fps nell'output ffmpeg
        /// </summary>
        private static readonly Regex s_fpsRegex = new Regex(@"(\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Configurazione ffmpeg da applicare all'esecuzione
        /// </summary>
        private FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Sezione di log per gli errori di lettura
        /// </summary>
        private LogSection _logSection;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il lettore delle informazioni video ffmpeg
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="ffmpegConfig">Configurazione ffmpeg</param>
        /// <param name="logSection">Sezione log</param>
        public FfmpegVideoInfoReader(string ffmpegPath, FfmpegConfig ffmpegConfig, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath;
            this._ffmpegConfig = ffmpegConfig;
            this._logSection = logSection;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Ottiene la durata e il frame rate del video tramite ffmpeg
        /// </summary>
        /// <param name="filePath">Percorso del file video da analizzare</param>
        /// <param name="durationMs">Durata del video in millisecondi, oppure zero se non viene riconosciuta</param>
        /// <param name="fps">Frame rate rilevato, con valore predefinito 25,0 se non disponibile</param>
        /// <returns>True se la durata viene letta correttamente, altrimenti false</returns>
        public bool TryRead(string filePath, out int durationMs, out double fps)
        {
            durationMs = 0;
            fps = 25.0;
            bool success = false;
            ProcessResult processResult;
            string output;
            Match durationMatch;
            Match fpsMatch;
            double parsedFps = 0.0;
            double observedFps;
            double relativeDiff = 0.0;
            string videoBlock;
            List<string> args = new List<string>();

            try
            {
                // ffmpeg scrive metadati e informazioni sugli stream su stderr quando viene invocato con il solo parametro -i
                args.Add("-nostdin");
                args.Add("-hide_banner");
                if (this._ffmpegConfig.HardwareAcceleration)
                {
                    args.Add("-hwaccel");
                    args.Add(this._ffmpegConfig.HardwareAccelerationMethod);
                }
                args.Add("-i");
                args.Add(filePath);

                processResult = ProcessRunner.Run(this._ffmpegPath, args.ToArray());
                output = processResult.Stdout + processResult.Stderr;

                durationMatch = s_durationRegex.Match(output);
                if (durationMatch.Success)
                {
                    durationMs = this.ParseDurationMs(durationMatch);
                    success = true;
                }

                fpsMatch = s_fpsRegex.Match(output);
                if (fpsMatch.Success)
                {
                    parsedFps = double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (parsedFps > 0.0)
                    {
                        fps = parsedFps;
                    }
                }

                videoBlock = this.ExtractFirstVideoStreamBlock(output);
                observedFps = this.TryComputeObservedVideoFps(videoBlock);
                if (observedFps > 0.0)
                {
                    // Quando il frame rate dichiarato diverge dal rapporto tra numero di frame e durata, preferiamo il valore osservato
                    if (parsedFps > 0.0)
                    {
                        relativeDiff = Math.Abs(parsedFps - observedFps) / Math.Max(parsedFps, observedFps);
                    }

                    if (parsedFps <= 0.0 || relativeDiff > 0.05)
                    {
                        fps = observedFps;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Warning, AppText.F("deep.temporal.ffmpeg.videoInfoError", ex.Message));
            }

            return success;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte il match della durata ffmpeg in millisecondi
        /// </summary>
        /// <param name="match">Match regex durata</param>
        /// <returns>Durata in millisecondi</returns>
        private int ParseDurationMs(Match match)
        {
            int hours = int.Parse(match.Groups[1].Value);
            int minutes = int.Parse(match.Groups[2].Value);
            int seconds = int.Parse(match.Groups[3].Value);
            string fractionText = match.Groups[4].Value;
            double fractionMs = 0.0;
            if (!string.IsNullOrEmpty(fractionText))
            {
                // La frazione ffmpeg può avere precisione variabile: viene normalizzata come parte decimale
                fractionMs = double.Parse("0." + fractionText, CultureInfo.InvariantCulture) * 1000.0;
            }

            return (hours * 3600000) + (minutes * 60000) + (seconds * 1000) + (int)Math.Round(fractionMs);
        }

        /// <summary>
        /// Estrae dall'output ffmpeg il blocco relativo al primo stream video
        /// </summary>
        /// <param name="output">Output completo ffmpeg</param>
        /// <returns>Blocco del primo stream video, oppure stringa vuota se non viene trovato</returns>
        private string ExtractFirstVideoStreamBlock(string output)
        {
            string result = "";
            int videoIndex = output.IndexOf(" Video:", StringComparison.OrdinalIgnoreCase);
            int streamStart;
            int nextStream;

            if (videoIndex < 0)
            {
                return result;
            }

            streamStart = output.LastIndexOf("Stream #", videoIndex, StringComparison.OrdinalIgnoreCase);
            if (streamStart < 0)
            {
                streamStart = videoIndex;
            }

            // Il blocco termina all'inizio dello stream successivo, quando presente
            nextStream = output.IndexOf("Stream #", videoIndex + 1, StringComparison.OrdinalIgnoreCase);
            result = nextStream > streamStart ? output.Substring(streamStart, nextStream - streamStart) : output.Substring(streamStart);

            return result;
        }

        /// <summary>
        /// Calcola il frame rate osservato dai tag NUMBER_OF_FRAMES e DURATION dello stream
        /// </summary>
        /// <param name="videoBlock">Blocco dell'output relativo al primo stream video</param>
        /// <returns>Frame rate osservato, oppure 0 se i metadati non sono disponibili o validi</returns>
        private double TryComputeObservedVideoFps(string videoBlock)
        {
            double result = 0.0;
            Match durationMatch;
            Match framesMatch;
            int durationMs;
            long frameCount;
            if (string.IsNullOrEmpty(videoBlock))
            {
                return result;
            }

            durationMatch = s_metadataDurationRegex.Match(videoBlock);
            framesMatch = s_numberOfFramesRegex.Match(videoBlock);

            if (durationMatch.Success && framesMatch.Success)
            {
                // Questo valore è più robusto del token "fps" su alcuni flussi VFR o contenitori problematici
                durationMs = this.ParseDurationMs(durationMatch);
                long.TryParse(framesMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frameCount);
                if (durationMs > 0 && frameCount > 0)
                {
                    result = frameCount / (durationMs / 1000.0);
                }
            }

            return result;
        }

        #endregion
    }
}
