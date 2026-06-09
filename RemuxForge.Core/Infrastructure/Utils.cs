using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Metodi utility statici di formattazione
    /// </summary>
    public static class Utils
    {
        #region Metodi pubblici

        /// <summary>
        /// Formatta una dimensione in byte in stringa leggibile
        /// </summary>
        /// <param name="bytes">Dimensione in bytes</param>
        /// <returns>Stringa formattata</returns>
        public static string FormatSize(long bytes)
        {
            string result;
            if (bytes >= 1073741824)
            {
                result = Math.Round(bytes / 1073741824.0, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + " GB";
            }
            else if (bytes >= 1048576)
            {
                result = Math.Round(bytes / 1048576.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
            }
            else if (bytes >= 1024)
            {
                result = Math.Round(bytes / 1024.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }
            else
            {
                result = bytes + " B";
            }

            return result;
        }

        /// <summary>
        /// Formatta una lista di codici lingua come stringa separata da virgola
        /// </summary>
        /// <param name="langs">Lista di codici lingua</param>
        /// <returns>Stringa formattata</returns>
        public static string FormatLangs(List<string> langs)
        {
            string result = "-";

            if (langs != null && langs.Count > 0)
            {
                result = string.Join(",", langs);
            }

            return result;
        }

        /// <summary>
        /// Formatta un delay in millisecondi con segno
        /// </summary>
        /// <param name="delayMs">Delay in millisecondi</param>
        /// <returns>Stringa formattata</returns>
        public static string FormatDelay(int delayMs)
        {
            string result = "0ms";

            if (delayMs > 0)
            {
                result = "+" + delayMs + "ms";
            }
            else if (delayMs < 0)
            {
                result = delayMs + "ms";
            }

            return result;
        }

        /// <summary>
        /// Padding a destra con troncamento se il testo supera la larghezza
        /// </summary>
        /// <param name="text">Testo da formattare</param>
        /// <param name="width">Larghezza colonna</param>
        /// <returns>Stringa con padding</returns>
        public static string PadRight(string text, int width)
        {
            string result;
            if (text.Length >= width)
            {
                result = text.Substring(0, width - 1) + " ";
            }
            else
            {
                result = text + new string(' ', width - text.Length);
            }

            return result;
        }

        /// <summary>
        /// Restituisce la versione dell'applicazione letta dall'assembly
        /// </summary>
        /// <returns>Stringa versione</returns>
        public static string GetVersion()
        {
            string result = "0.0";
            Assembly asm = Assembly.GetEntryAssembly();
            AssemblyInformationalVersionAttribute attr = null;
            if (asm != null)
            {
                attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
            }

            if (attr != null)
            {
                result = attr.InformationalVersion;
            }

            return result;
        }

        /// <summary>
        /// Restituisce il testo di stato per un FileStatus
        /// </summary>
        /// <param name="status">Stato del file</param>
        /// <returns>Testo localizzato dello stato</returns>
        public static string GetStatusText(FileStatus status)
        {
            string result = "";
            if (status == FileStatus.Pending) { result = AppText.T("status.pending"); }
            else if (status == FileStatus.Analyzing) { result = AppText.T("status.analyzing"); }
            else if (status == FileStatus.Analyzed) { result = AppText.T("status.analyzed"); }
            else if (status == FileStatus.Processing) { result = AppText.T("status.processing"); }
            else if (status == FileStatus.Encoding) { result = AppText.T("status.encoding"); }
            else if (status == FileStatus.Done) { result = AppText.T("status.done"); }
            else if (status == FileStatus.Error) { result = AppText.T("status.error"); }
            else if (status == FileStatus.Skipped) { result = AppText.T("status.skipped"); }

            return result;
        }

        /// <summary>
        /// Formatta una traccia in formato compatto per display
        /// Es: "1: ita AC-3 5.1" oppure "3: eng DTS-HD MA 7.1"
        /// </summary>
        /// <param name="track">Traccia da formattare</param>
        /// <returns>Stringa compatta della traccia</returns>
        public static string FormatTrackCompact(TrackInfo track)
        {
            StringBuilder sb = new StringBuilder();
            string lang = track.Language.Length > 0 ? track.Language : "und";
            string channels = AudioChannelHelper.FormatChannels(track.Channels);

            sb.Append(track.Id).Append(": ").Append(lang).Append(" ").Append(track.Codec);

            if (channels.Length > 0)
            {
                sb.Append(" ").Append(channels);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Formatta una lista di tracce in stringa separata da " | "
        /// </summary>
        /// <param name="tracks">Lista tracce da formattare</param>
        /// <returns>Stringa formattata o "-" se vuota</returns>
        public static string FormatTrackList(List<TrackInfo> tracks)
        {
            string result = "-";

            if (tracks != null && tracks.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (i > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(tracks[i]));
                }
                result = sb.ToString();
            }

            return result;
        }

        /// <summary>
        /// Formatta una lista di tracce filtrata per ID in stringa separata da " | "
        /// </summary>
        /// <param name="tracks">Lista tracce completa</param>
        /// <param name="ids">ID tracce da includere</param>
        /// <returns>Stringa formattata o "-" se vuota</returns>
        public static string FormatTrackListByIds(List<TrackInfo> tracks, List<int> ids)
        {
            string result = "-";
            StringBuilder sb = new StringBuilder();
            int count = 0;
            if (tracks != null && ids != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (ids.Contains(tracks[i].Id))
                    {
                        if (count > 0) { sb.Append(" | "); }
                        sb.Append(FormatTrackCompact(tracks[i]));
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                result = sb.ToString();
            }

            return result;
        }

        /// <summary>
        /// Formatta una lista di tracce importate con indicazione processing coerente con le opzioni audio
        /// </summary>
        /// <param name="tracks">Lista tracce importate</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="forceProcessing">True se deep/source-fill richiedono render anche fuori scope generico</param>
        /// <returns>Stringa formattata o "-" se vuota</returns>
        public static string FormatImportedTrackList(List<TrackInfo> tracks, Options options, bool forceProcessing)
        {
            string result = "-";

            if (tracks != null && tracks.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (i > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(tracks[i]));

                    if (ShouldDisplayAudioProcessing(tracks[i], options, false, true, forceProcessing))
                    {
                        sb.Append(" -> ").Append(options.AudioFormat.ToUpperInvariant());
                    }
                }
                result = sb.ToString();
            }

            return result;
        }

        /// <summary>
        /// Estrae il DefaultDurationNs della prima traccia video trovata
        /// </summary>
        /// <param name="tracks">Lista tracce da esaminare</param>
        /// <returns>DefaultDurationNs della prima traccia video, 0 se non trovata</returns>
        public static long GetVideoDefaultDuration(List<TrackInfo> tracks)
        {
            long result = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (string.Equals(tracks[i].Type, "video", StringComparison.OrdinalIgnoreCase) && tracks[i].DefaultDurationNs > 0)
                {
                    result = tracks[i].DefaultDurationNs;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Costruisce la lista tracce risultato finale per display (kept + imported)
        /// </summary>
        /// <param name="sourceTracks">Tracce sorgente (audio o sub)</param>
        /// <param name="keptIds">ID tracce sorgente mantenute</param>
        /// <param name="importedTracks">Tracce importate</param>
        /// <param name="convertFormat">Formato conversione o vuoto</param>
        /// <param name="filterActive">True se il filtro sorgente e' attivo</param>
        /// <returns>Stringa formattata del risultato</returns>
        public static string FormatResultTrackList(List<TrackInfo> sourceTracks, List<int> keptIds, List<TrackInfo> importedTracks, string convertFormat, bool filterActive)
        {
            StringBuilder sb = new StringBuilder();
            int count = 0;
            // Tracce sorgente mantenute
            if (sourceTracks != null)
            {
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    // Se il filtro e' attivo, mostra solo le tracce mantenute
                    if (filterActive && !keptIds.Contains(sourceTracks[i].Id))
                    {
                        continue;
                    }

                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(sourceTracks[i]));

                    // Indica conversione se applicabile anche a tracce sorgente
                    if (convertFormat.Length > 0 && CodecMapping.IsConvertibleLossless(sourceTracks[i], convertFormat))
                    {
                        sb.Append(" -> ").Append(convertFormat.ToUpper());
                    }
                    count++;
                }
            }

            // Tracce importate
            if (importedTracks != null)
            {
                for (int i = 0; i < importedTracks.Count; i++)
                {
                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(importedTracks[i]));

                    if (convertFormat.Length > 0 && CodecMapping.IsConvertibleLossless(importedTracks[i], convertFormat))
                    {
                        sb.Append(" -> ").Append(convertFormat.ToUpper());
                    }
                    count++;
                }
            }

            string result = count > 0 ? sb.ToString() : "-";
            return result;
        }

        /// <summary>
        /// Costruisce la lista tracce audio risultato finale con indicazione processing coerente con le opzioni audio
        /// </summary>
        /// <param name="sourceTracks">Tracce sorgente audio</param>
        /// <param name="keptIds">ID tracce sorgente mantenute</param>
        /// <param name="importedTracks">Tracce importate</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="filterActive">True se il filtro sorgente e' attivo</param>
        /// <param name="hasMerge">True se il record importa tracce da un file lingua</param>
        /// <param name="forceImportedProcessing">True se deep/source-fill richiedono render sulle tracce importate</param>
        /// <returns>Stringa formattata del risultato</returns>
        public static string FormatResultTrackList(List<TrackInfo> sourceTracks, List<int> keptIds, List<TrackInfo> importedTracks, Options options, bool filterActive, bool hasMerge, bool forceImportedProcessing)
        {
            StringBuilder sb = new StringBuilder();
            int count = 0;

            if (sourceTracks != null)
            {
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (filterActive && !keptIds.Contains(sourceTracks[i].Id))
                    {
                        continue;
                    }

                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(sourceTracks[i]));

                    if (ShouldDisplayAudioProcessing(sourceTracks[i], options, true, hasMerge, false))
                    {
                        sb.Append(" -> ").Append(options.AudioFormat.ToUpperInvariant());
                    }
                    count++;
                }
            }

            if (importedTracks != null)
            {
                for (int i = 0; i < importedTracks.Count; i++)
                {
                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(FormatTrackCompact(importedTracks[i]));

                    if (ShouldDisplayAudioProcessing(importedTracks[i], options, false, hasMerge, forceImportedProcessing))
                    {
                        sb.Append(" -> ").Append(options.AudioFormat.ToUpperInvariant());
                    }
                    count++;
                }
            }

            string result = count > 0 ? sb.ToString() : "-";
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// True se la traccia deve mostrare indicazione di processing audio
        /// </summary>
        /// <param name="track">Traccia audio</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="sourceTrack">True se la traccia arriva dal sorgente</param>
        /// <param name="hasMerge">True se il record importa un file lingua</param>
        /// <param name="forceProcessing">True se deep/source-fill richiedono render non generico</param>
        /// <returns>True se va mostrato il formato target</returns>
        private static bool ShouldDisplayAudioProcessing(TrackInfo track, Options options, bool sourceTrack, bool hasMerge, bool forceProcessing)
        {
            bool result = false;

            if (track == null || options == null || options.AudioFormat.Length == 0 || CodecMapping.IsSpatialCodec(track))
            {
                return result;
            }

            if (forceProcessing)
            {
                result = true;
            }
            else if (IsTrackInGenericAudioScope(sourceTrack, hasMerge, options.AudioProcessingScope))
            {
                result = CodecMapping.RequiresGenericAudioRender(track, options);
            }

            return result;
        }

        /// <summary>
        /// True se la traccia rientra nello scope audio generico configurato
        /// </summary>
        /// <param name="sourceTrack">True se la traccia arriva dal sorgente</param>
        /// <param name="hasMerge">True se il record importa un file lingua</param>
        /// <param name="audioScope">Scope audio configurato</param>
        /// <returns>True se lo scope include la traccia</returns>
        private static bool IsTrackInGenericAudioScope(bool sourceTrack, bool hasMerge, string audioScope)
        {
            bool result = false;

            if (audioScope == "all")
            {
                result = true;
            }
            else if (audioScope == "lang")
            {
                result = !sourceTrack || !hasMerge;
            }

            return result;
        }

        #endregion
    }
}
