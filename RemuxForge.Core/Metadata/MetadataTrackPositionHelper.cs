using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Posiziona le tracce dentro il proprio tipo: video, audio e sottotitoli non si mescolano mai
    /// </summary>
    public static class MetadataTrackPositionHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Restituisce la chiave del campo posizione per un tipo di traccia
        /// </summary>
        /// <param name="trackKind">Tipo traccia runtime</param>
        /// <returns>Chiave campo, stringa vuota se il tipo non ha posizione</returns>
        public static string GetPositionFieldKey(string trackKind)
        {
            if (trackKind == "video")
                return "video_index";
            if (trackKind == "audio")
                return "audio_index";
            if (trackKind == "subtitles")
                return "subtitle_index";

            return "";
        }

        /// <summary>
        /// Indica se un campo e' la posizione della traccia nel suo tipo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Vero per video_index, audio_index e subtitle_index</returns>
        public static bool IsPositionField(string fieldKey)
        {
            string key = fieldKey != null ? fieldKey.Trim() : "";

            return string.Equals(key, "video_index", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "audio_index", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "subtitle_index", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Riordina le tracce di un tipo secondo le posizioni richieste
        /// </summary>
        /// <param name="fileInfo">Stato file da riordinare</param>
        /// <param name="trackKind">Tipo traccia da riordinare</param>
        /// <param name="requested">Posizioni richieste per selector originale, 1-based</param>
        /// <param name="removedTracks">Tracce che non arriveranno nell'output</param>
        public static void Apply(MkvMetadataFileInfo fileInfo, string trackKind, Dictionary<string, int> requested, List<MkvMetadataTrackInfo> removedTracks)
        {
            List<MkvMetadataTrackInfo> kindTracks = new List<MkvMetadataTrackInfo>();
            List<MkvMetadataTrackInfo> assigned = new List<MkvMetadataTrackInfo>();
            List<MkvMetadataTrackInfo> overflow = new List<MkvMetadataTrackInfo>();
            List<int> listSlots = new List<int>();
            MkvMetadataTrackInfo[] slots;
            string fieldKey = GetPositionFieldKey(trackKind);
            int free;

            if (fileInfo == null || fieldKey.Length == 0)
                return;

            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                MkvMetadataTrackInfo track = fileInfo.Tracks[i];
                if (track.TrackKind != trackKind || (removedTracks != null && removedTracks.Contains(track)))
                    continue;

                listSlots.Add(i);
                kindTracks.Add(track);
            }

            // Le tracce senza posizione mantengono l'ordine che hanno nel file sorgente,
            // qualunque spostamento abbiano gia' subito da richieste precedenti
            kindTracks.Sort(delegate (MkvMetadataTrackInfo left, MkvMetadataTrackInfo right) { return left.TypeIndex.CompareTo(right.TypeIndex); });
            slots = new MkvMetadataTrackInfo[kindTracks.Count];

            for (int i = 0; i < kindTracks.Count; i++)
            {
                int position;
                if (requested != null && requested.TryGetValue(kindTracks[i].TrackSelector, out position))
                    assigned.Add(kindTracks[i]);
            }

            assigned.Sort(delegate (MkvMetadataTrackInfo left, MkvMetadataTrackInfo right) { return requested[left.TrackSelector].CompareTo(requested[right.TrackSelector]); });
            for (int i = 0; i < assigned.Count; i++)
            {
                int position = requested[assigned[i].TrackSelector];
                if (position < 1)
                    throw new InvalidOperationException(AppText.F("metadata.error.trackPositionInvalid", assigned[i].TrackSelector, position));

                if (i > 0 && requested[assigned[i - 1].TrackSelector] == position)
                    throw new InvalidOperationException(AppText.F("metadata.error.trackPositionConflict", assigned[i - 1].TrackSelector, assigned[i].TrackSelector, position));

                if (position <= slots.Length)
                    slots[position - 1] = assigned[i];
                else
                    overflow.Add(assigned[i]);
            }

            // Una posizione oltre il numero di tracce vale "in fondo": le tracce che la
            // chiedono prendono gli ultimi posti liberi, nell'ordine delle loro richieste
            free = slots.Length - 1;
            for (int i = overflow.Count - 1; i >= 0; i--)
            {
                while (slots[free] != null)
                    free--;

                slots[free] = overflow[i];
            }

            free = 0;
            for (int i = 0; i < kindTracks.Count; i++)
            {
                if (assigned.Contains(kindTracks[i]))
                    continue;

                while (slots[free] != null)
                    free++;

                slots[free] = kindTracks[i];
            }

            // Il tipo resta negli stessi punti della lista: cambia solo chi occupa ciascuno
            for (int i = 0; i < slots.Length; i++)
            {
                fileInfo.Tracks[listSlots[i]] = slots[i];
                slots[i].Fields[fieldKey] = (i + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Costruisce l'ordine delle tracce da passare al remux
        /// </summary>
        /// <param name="current">Stato file dopo le modifiche</param>
        /// <param name="original">Stato file letto dal disco</param>
        /// <returns>Selector originali nell'ordine voluto, vuoto se l'ordine non cambia</returns>
        public static List<string> BuildTrackOrder(MkvMetadataFileInfo current, MkvMetadataFileInfo original)
        {
            List<string> result = new List<string>();
            bool moved = false;

            if (current == null || original == null)
                return result;

            for (int i = 0; i < current.Tracks.Count; i++)
            {
                result.Add(current.Tracks[i].TrackSelector);
                if (i >= original.Tracks.Count || !string.Equals(current.Tracks[i].TrackSelector, original.Tracks[i].TrackSelector, StringComparison.OrdinalIgnoreCase))
                    moved = true;
            }

            if (!moved)
                result.Clear();

            return result;
        }

        #endregion
    }
}
