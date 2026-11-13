using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.TrackExtraction
{
    /// <summary>
    /// Formato in cui una traccia si estrae tale e quale: nome del formato, estensione del file e, per ffmpeg, il muxer che lo
    /// scrive. Le tabelle contengono solo i codec provati; un codec che non vi compare non si estrae. Lo stesso formato ha lo
    /// stesso nome qualunque sia il contenitore di partenza
    /// </summary>
    public sealed class TrackExtractionFormat
    {
        #region Variabili statiche

        /// <summary>
        /// Formati scritti da mkvextract, per Codec ID Matroska
        /// </summary>
        private static readonly Dictionary<string, TrackExtractionFormat> s_matroska = new Dictionary<string, TrackExtractionFormat>(StringComparer.OrdinalIgnoreCase)
        {
            // mkvextract aggiunge l'intestazione ADTS a ogni pacchetto AAC
            { "A_AAC", new TrackExtractionFormat("AAC", ".aac", "", null, false) },
            { "A_AC3", new TrackExtractionFormat("AC-3", ".ac3", "", null, false) },
            { "A_EAC3", new TrackExtractionFormat("E-AC-3", ".eac3", "", null, false) },
            { "A_DTS", new TrackExtractionFormat("DTS", ".dts", "", null, false) },
            { "A_TRUEHD", new TrackExtractionFormat("TrueHD", ".thd", "", null, false) },
            { "A_FLAC", new TrackExtractionFormat("FLAC", ".flac", "", null, false) },
            { "A_OPUS", new TrackExtractionFormat("Opus", ".opus", "", null, false) },
            { "A_VORBIS", new TrackExtractionFormat("Vorbis", ".ogg", "", null, false) },
            { "A_MPEG/L3", new TrackExtractionFormat("MP3", ".mp3", "", null, false) },
            { "A_MPEG/L2", new TrackExtractionFormat("MP2", ".mp2", "", null, false) },
            { "A_PCM/INT/LIT", new TrackExtractionFormat("PCM", ".wav", "", null, false) },
            // mkvextract scrive l'ALAC in un Core Audio Format
            { "A_ALAC", new TrackExtractionFormat("ALAC", ".caf", "", null, false) },
            { "S_TEXT/UTF8", new TrackExtractionFormat("SRT", ".srt", "", null, false) },
            { "S_TEXT/ASS", new TrackExtractionFormat("ASS", ".ass", "", null, false) },
            { "S_TEXT/SSA", new TrackExtractionFormat("SSA", ".ssa", "", null, false) },
            { "S_TEXT/WEBVTT", new TrackExtractionFormat("WebVTT", ".vtt", "", null, false) },
            { "S_HDMV/PGS", new TrackExtractionFormat("PGS", ".sup", "", null, false) },
            // Il .sub nasce accanto all'.idx con lo stesso nome
            { "S_VOBSUB", new TrackExtractionFormat("VobSub", ".idx", "", null, false) }
        };

        /// <summary>
        /// Formati scritti da ffmpeg per i contenitori non Matroska, per nome del codec di ffprobe
        /// </summary>
        private static readonly Dictionary<string, TrackExtractionFormat> s_ffmpeg = new Dictionary<string, TrackExtractionFormat>(StringComparer.OrdinalIgnoreCase)
        {
            { "aac", new TrackExtractionFormat("AAC", ".aac", "adts", null, false) },
            { "ac3", new TrackExtractionFormat("AC-3", ".ac3", "ac3", null, false) },
            { "eac3", new TrackExtractionFormat("E-AC-3", ".eac3", "eac3", null, false) },
            { "dts", new TrackExtractionFormat("DTS", ".dts", "dts", null, false) },
            { "truehd", new TrackExtractionFormat("TrueHD", ".thd", "truehd", null, false) },
            { "flac", new TrackExtractionFormat("FLAC", ".flac", "flac", null, false) },
            { "opus", new TrackExtractionFormat("Opus", ".opus", "opus", null, false) },
            // Senza ID3v2 e senza l'intestazione Xing il file contiene solo i fotogrammi della traccia, come quello di mkvextract
            { "mp3", new TrackExtractionFormat("MP3", ".mp3", "mp3", new string[] { "-id3v2_version", "0", "-write_xing", "0" }, false) },
            { "mp2", new TrackExtractionFormat("MP2", ".mp2", "mp2", null, false) },
            { "pcm_s16le", new TrackExtractionFormat("PCM", ".wav", "wav", null, false) },
            { "alac", new TrackExtractionFormat("ALAC", ".m4a", "ipod", null, false) },
            { "hdmv_pgs_subtitle", new TrackExtractionFormat("PGS", ".sup", "sup", null, false) },
            // Il Timed Text degli MP4 non ha un file a sé: si converte in SRT, con lo stesso testo
            { "mov_text", new TrackExtractionFormat("Timed Text → SRT", ".srt", "srt", null, true) }
        };

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome del formato mostrato e usato dal filtro</param>
        /// <param name="extension">Estensione del file, con il punto</param>
        /// <param name="muxer">Muxer di ffmpeg, vuoto per i formati di mkvextract</param>
        /// <param name="muxerOptions">Opzioni del muxer, null se non servono</param>
        /// <param name="convertsToSrt">True se la traccia si converte in SRT invece di copiarla</param>
        private TrackExtractionFormat(string name, string extension, string muxer, string[] muxerOptions, bool convertsToSrt)
        {
            this.Name = name;
            this.Extension = extension;
            this.Muxer = muxer;
            this.MuxerOptions = muxerOptions ?? Array.Empty<string>();
            this.ConvertsToSrt = convertsToSrt;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Formato di una traccia Matroska estratta con mkvextract
        /// </summary>
        /// <param name="codecId">Codec ID Matroska letto da mkvmerge</param>
        /// <returns>Formato, null se il codec non si estrae</returns>
        public static TrackExtractionFormat FromMatroskaCodecId(string codecId)
        {
            TrackExtractionFormat format;
            string id = codecId ?? "";

            // A_AAC/MPEG2/LC, A_AAC/MPEG4/LC/SBR… sono tutti AAC
            if (id.StartsWith("A_AAC", StringComparison.OrdinalIgnoreCase))
                id = "A_AAC";

            return s_matroska.TryGetValue(id, out format) ? format : null;
        }

        /// <summary>
        /// Formato di uno stream estratto con ffmpeg
        /// </summary>
        /// <param name="codecName">Nome del codec di ffprobe</param>
        /// <returns>Formato, null se il codec non si estrae</returns>
        public static TrackExtractionFormat FromFfmpegCodec(string codecName)
        {
            TrackExtractionFormat format;
            return s_ffmpeg.TryGetValue(codecName ?? "", out format) ? format : null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del formato, uguale per lo stesso formato in contenitori diversi
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Estensione del file estratto, con il punto
        /// </summary>
        public string Extension { get; }

        /// <summary>
        /// Muxer di ffmpeg che scrive il file, vuoto per mkvextract
        /// </summary>
        public string Muxer { get; }

        /// <summary>
        /// Opzioni del muxer di ffmpeg
        /// </summary>
        public string[] MuxerOptions { get; }

        /// <summary>
        /// True se la traccia si converte in SRT: l'unica eccezione all'estrazione tale e quale
        /// </summary>
        public bool ConvertsToSrt { get; }

        #endregion
    }
}
