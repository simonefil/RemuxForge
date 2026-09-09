using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Compone gli argomenti delle decodifiche di analisi, che leggono una traccia e la riversano
    /// grezza sullo standard output. Gli anelli si montano nell'ordine in cui ffmpeg li vuole:
    /// prima quelli che riguardano l'ingresso, poi quelli che riguardano l'uscita
    /// </summary>
    public class FfmpegCommand
    {
        #region Campi privati

        /// <summary>
        /// Argomenti nell'ordine in cui il chiamante li ha montati
        /// </summary>
        private readonly List<string> _arguments;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore privato: si parte da uno dei due livelli di diagnostica
        /// </summary>
        private FfmpegCommand()
        {
            this._arguments = new List<string>();
        }

        #endregion

        #region Metodi statici

        /// <summary>
        /// Apre una decodifica che riporta i soli errori, o anche la diagnostica quando servono i
        /// tempi di presentazione che solo showinfo sa dare
        /// </summary>
        /// <param name="withDiagnostics">True per chiedere il livello info invece di error</param>
        /// <returns>Comando aperto</returns>
        public static FfmpegCommand Decode(bool withDiagnostics)
        {
            FfmpegCommand result = new FfmpegCommand();
            result._arguments.Add("-nostdin");
            result._arguments.Add("-v");
            result._arguments.Add(withDiagnostics ? "info" : "error");
            return result;
        }

        /// <summary>
        /// Apre una decodifica che tace l'intestazione invece di abbassare il livello
        /// </summary>
        /// <returns>Comando aperto</returns>
        public static FfmpegCommand DecodeWithoutBanner()
        {
            FfmpegCommand result = new FfmpegCommand();
            result._arguments.Add("-nostdin");
            result._arguments.Add("-hide_banner");
            return result;
        }

        /// <summary>
        /// Interroga ffprobe su una traccia e ne riporta i campi richiesti in JSON
        /// </summary>
        /// <param name="streamSelector">Selettore della traccia</param>
        /// <param name="entries">Campi da riportare</param>
        /// <param name="filePath">File multimediale</param>
        /// <returns>Argomenti nell'ordine atteso da ffprobe</returns>
        public static string[] Probe(string streamSelector, string entries, string filePath)
        {
            return new string[] { "-v", "error", "-select_streams", streamSelector, "-show_entries", entries, "-of", "json", filePath };
        }

        #endregion

        #region Metodi pubblici - Ingresso

        /// <summary>
        /// Chiede la decodifica accelerata quando la configurazione la prevede e la nomina in modo
        /// riconoscibile
        /// </summary>
        /// <param name="config">Configurazione ffmpeg</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand HardwareAcceleration(FfmpegConfig config)
        {
            if (!config.HardwareAcceleration || !FfmpegConfig.IsValidHardwareAccelerationMethod(config.HardwareAccelerationMethod))
                return this;
            this._arguments.Add("-hwaccel");
            this._arguments.Add(config.HardwareAccelerationMethod);
            return this;
        }

        /// <summary>
        /// Conserva i tempi del contenitore invece di riportarli a zero
        /// </summary>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand CopyTimestamps()
        {
            this._arguments.Add("-copyts");
            return this;
        }

        /// <summary>
        /// Salta all'istante richiesto, contato dall'inizio del file
        /// </summary>
        /// <param name="seconds">Istante già formattato in secondi</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand Seek(string seconds)
        {
            this._arguments.Add("-ss");
            this._arguments.Add(seconds);
            return this;
        }

        /// <summary>
        /// Indica il file da leggere: da qui in poi gli argomenti riguardano l'uscita
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand Input(string filePath)
        {
            this._arguments.Add("-i");
            this._arguments.Add(filePath);
            return this;
        }

        #endregion

        #region Metodi pubblici - Uscita

        /// <summary>
        /// Ferma la decodifica dopo la durata richiesta
        /// </summary>
        /// <param name="seconds">Durata già formattata in secondi</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand Duration(string seconds)
        {
            this._arguments.Add("-t");
            this._arguments.Add(seconds);
            return this;
        }

        /// <summary>
        /// Ferma la decodifica all'istante richiesto sull'orologio del contenitore
        /// </summary>
        /// <param name="seconds">Istante già formattato in secondi</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand EndTime(string seconds)
        {
            this._arguments.Add("-to");
            this._arguments.Add(seconds);
            return this;
        }

        /// <summary>
        /// Ferma la decodifica dopo il numero di fotogrammi richiesto
        /// </summary>
        /// <param name="frames">Fotogrammi da decodificare</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand FrameLimit(int frames)
        {
            this._arguments.Add("-frames:v");
            this._arguments.Add(frames.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Tiene la sola prima traccia video e scarta audio, sottotitoli e dati
        /// </summary>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand VideoStreamOnly()
        {
            this._arguments.Add("-an");
            this._arguments.Add("-sn");
            this._arguments.Add("-dn");
            this._arguments.Add("-map");
            this._arguments.Add("0:v:0");
            return this;
        }

        /// <summary>
        /// Tiene la traccia audio indicata
        /// </summary>
        /// <param name="streamSelector">Selettore relativo al primo ingresso</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand AudioStream(string streamSelector)
        {
            this._arguments.Add("-map");
            this._arguments.Add("0:" + streamSelector);
            return this;
        }

        /// <summary>
        /// Decide se i fotogrammi escono come sono arrivati o riallineati alla frequenza
        /// </summary>
        /// <param name="mode">Modo atteso da ffmpeg</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand FrameMode(string mode)
        {
            this._arguments.Add("-fps_mode");
            this._arguments.Add(mode);
            return this;
        }

        /// <summary>
        /// Applica la catena di filtri video
        /// </summary>
        /// <param name="chain">Catena composta</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand Filter(FfmpegFilterChain chain)
        {
            this._arguments.Add("-vf");
            this._arguments.Add(chain.Build());
            return this;
        }

        /// <summary>
        /// Applica una catena di filtri audio
        /// </summary>
        /// <param name="chain">Catena nella forma attesa da -af</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand AudioFilter(string chain)
        {
            this._arguments.Add("-af");
            this._arguments.Add(chain);
            return this;
        }

        /// <summary>
        /// Riduce l'audio a un canale alla frequenza richiesta
        /// </summary>
        /// <param name="sampleRate">Frequenza di campionamento</param>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand MonoResampled(int sampleRate)
        {
            this._arguments.Add("-ac");
            this._arguments.Add("1");
            this._arguments.Add("-ar");
            this._arguments.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Riversa i fotogrammi grezzi sullo standard output
        /// </summary>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand ToRawVideo()
        {
            this._arguments.Add("-f");
            this._arguments.Add("rawvideo");
            this._arguments.Add("-");
            return this;
        }

        /// <summary>
        /// Riversa i campioni in virgola mobile sullo standard output
        /// </summary>
        /// <returns>Il comando stesso</returns>
        public FfmpegCommand ToFloatPcm()
        {
            this._arguments.Add("-f");
            this._arguments.Add("f32le");
            this._arguments.Add("-");
            return this;
        }

        /// <summary>
        /// Chiude il comando nella forma attesa dall'esecutore
        /// </summary>
        /// <returns>Argomenti nell'ordine di montaggio</returns>
        public string[] Build()
        {
            return this._arguments.ToArray();
        }

        #endregion
    }
}
