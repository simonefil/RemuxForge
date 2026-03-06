using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Classe base per servizi di sincronizzazione video tramite confronto frame
    /// </summary>
    public abstract class VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Larghezza frame per confronto MSE
        /// </summary>
        protected const int FRAME_WIDTH = 320;

        /// <summary>
        /// Altezza frame per confronto MSE
        /// </summary>
        protected const int FRAME_HEIGHT = 240;

        /// <summary>
        /// Dimensione in byte di un singolo frame grayscale
        /// </summary>
        protected const int FRAME_SIZE = 76800;

        /// <summary>
        /// Soglia MSE massima per match valido
        /// </summary>
        protected const double MSE_THRESHOLD = 100.0;

        /// <summary>
        /// Soglia MSE minima, sotto cui il match e' ambiguo
        /// </summary>
        protected const double MSE_MIN_THRESHOLD = 0.05;

        /// <summary>
        /// Numero di punti di verifica
        /// </summary>
        protected const int NUM_CHECK_POINTS = 9;

        /// <summary>
        /// Minimo punti verifica riusciti per sync valido
        /// </summary>
        protected const int MIN_VALID_POINTS = 5;

        /// <summary>
        /// Soglia MSE tra frame consecutivi per rilevare taglio di scena
        /// </summary>
        protected const double SCENE_CUT_THRESHOLD = 50.0;

        /// <summary>
        /// Frame prima e dopo il taglio per la firma
        /// </summary>
        protected const int CUT_HALF_WINDOW = 5;

        /// <summary>
        /// Lunghezza firma taglio di scena (2 * CUT_HALF_WINDOW)
        /// </summary>
        protected const int CUT_SIGNATURE_LENGTH = 10;

        /// <summary>
        /// Minimo tagli di scena richiesti per analisi
        /// </summary>
        protected const int MIN_SCENE_CUTS = 3;

        /// <summary>
        /// Distanza minima in frame tra due tagli consecutivi
        /// </summary>
        protected const int MIN_CUT_SPACING_FRAMES = 24;

        /// <summary>
        /// Durata segmento source per verifica punto in secondi
        /// </summary>
        protected const int VERIFY_SOURCE_DURATION_SEC = 10;

        /// <summary>
        /// Durata segmento lang per verifica punto in secondi
        /// </summary>
        protected const int VERIFY_LANG_DURATION_SEC = 15;

        /// <summary>
        /// Durata segmento source per retry verifica in secondi
        /// </summary>
        protected const int VERIFY_SOURCE_RETRY_SEC = 20;

        /// <summary>
        /// Durata segmento lang per retry verifica in secondi
        /// </summary>
        protected const int VERIFY_LANG_RETRY_SEC = 30;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile ffmpeg
        /// </summary>
        protected string _ffmpegPath;

        /// <summary>
        /// Prefisso log per messaggi di warning
        /// </summary>
        private string _logPrefix;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        /// <param name="logPrefix">Prefisso per messaggi di log</param>
        protected VideoSyncServiceBase(string ffmpegPath, string logPrefix)
        {
            this._ffmpegPath = ffmpegPath;
            this._logPrefix = logPrefix;
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Estrae frame di un segmento video come byte array grayscale
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <param name="startMs">Inizio estrazione in millisecondi</param>
        /// <param name="durationSec">Durata estrazione in secondi</param>
        /// <returns>Lista di frame grayscale come byte array</returns>
        protected List<byte[]> ExtractSegment(string filePath, int startMs, double durationSec)
        {
            List<byte[]> frames = new List<byte[]>();
            Process process = null;
            double startSec = 0.0;
            string startFormatted = "";
            string durationFormatted = "";
            string args = "";
            Stream stdoutStream = null;
            bool reading = true;
            byte[] frameData = null;
            int totalRead = 0;
            int bytesRead = 0;

            try
            {
                // Formatta timestamp e durata
                startSec = startMs / 1000.0;
                startFormatted = startSec.ToString("F3", CultureInfo.InvariantCulture);
                durationFormatted = durationSec.ToString("F3", CultureInfo.InvariantCulture);

                // Comando ffmpeg per estrazione raw grayscale via pipe
                args = "-nostdin -hide_banner -ss " + startFormatted + " -i \"" + filePath + "\" -t " + durationFormatted + " -s " + FRAME_WIDTH + "x" + FRAME_HEIGHT + " -pix_fmt gray -f rawvideo -";

                process = new Process();
                process.StartInfo.FileName = this._ffmpegPath;
                process.StartInfo.Arguments = args;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                // Svuota stderr in thread separato
                Thread errThread = new Thread(() =>
                {
                    // Evita deadlock pipe stderr
                    try { process.StandardError.ReadToEnd(); }
                    catch { }
                });
                errThread.Start();

                // Legge frame consecutivi dal flusso binario stdout
                stdoutStream = process.StandardOutput.BaseStream;

                while (reading)
                {
                    frameData = new byte[FRAME_SIZE];
                    totalRead = 0;

                    // Legge esattamente FRAME_SIZE byte per ogni frame
                    while (totalRead < FRAME_SIZE)
                    {
                        bytesRead = stdoutStream.Read(frameData, totalRead, FRAME_SIZE - totalRead);
                        if (bytesRead == 0)
                        {
                            reading = false;
                            break;
                        }
                        totalRead += bytesRead;
                    }

                    // Aggiunge il frame solo se completo
                    if (totalRead == FRAME_SIZE)
                    {
                        frames.Add(frameData);
                    }
                }

                errThread.Join();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteWarning("  [" + this._logPrefix + "] Errore ExtractSegment: " + ex.Message);
            }
            finally
            {
                if (process != null) { process.Dispose(); process = null; }
            }

            return frames;
        }

        /// <summary>
        /// Calcola MSE tra due frame grayscale
        /// </summary>
        /// <param name="frame1">Primo frame grayscale</param>
        /// <param name="frame2">Secondo frame grayscale</param>
        /// <returns>Valore MSE calcolato</returns>
        protected double ComputeMse(byte[] frame1, byte[] frame2)
        {
            double sumSquaredDiff = 0.0;
            int length = frame1.Length;
            double diff = 0.0;

            for (int i = 0; i < length; i++)
            {
                diff = (double)frame1[i] - (double)frame2[i];
                sumSquaredDiff += diff * diff;
            }

            double mse = sumSquaredDiff / length;

            return mse;
        }

        /// <summary>
        /// Calcola MSE medio di una sequenza di frame consecutivi
        /// </summary>
        /// <param name="sourceFrames">Lista frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nei frame sorgente</param>
        /// <param name="langFrames">Lista frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>MSE medio della sequenza o double.MaxValue se insufficienti</returns>
        protected double ComputeSequenceMse(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalMse = 0.0;
            int validFrames = 0;
            double result = double.MaxValue;
            int srcIdx = 0;
            int lngIdx = 0;

            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalMse += this.ComputeMse(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalMse / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi
        /// </summary>
        /// <param name="frames">Lista frame grayscale</param>
        /// <returns>Lista indici frame dove avviene il taglio</returns>
        protected List<int> DetectSceneCuts(List<byte[]> frames)
        {
            List<int> cuts = new List<int>();
            double interMse = 0.0;
            int lastCutIdx = -MIN_CUT_SPACING_FRAMES;

            for (int i = 0; i < frames.Count - 1; i++)
            {
                interMse = this.ComputeMse(frames[i], frames[i + 1]);

                // Taglio se MSE supera soglia e distanza minima dal taglio precedente
                if (interMse > SCENE_CUT_THRESHOLD && (i + 1 - lastCutIdx) >= MIN_CUT_SPACING_FRAMES)
                {
                    cuts.Add(i + 1);
                    lastCutIdx = i + 1;
                }
            }

            return cuts;
        }

        #endregion
    }
}
