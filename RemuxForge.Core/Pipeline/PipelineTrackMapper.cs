using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Mapping tracce e lingue per ProcessingPipeline
    /// </summary>
    public class PipelineTrackMapper
    {
        #region Metodi pubblici

        /// <summary>
        /// Filtra le tracce per tipo
        /// </summary>
        /// <param name="tracks">Tracce da filtrare</param>
        /// <param name="trackType">Tipo traccia richiesto</param>
        /// <returns>Tracce del tipo richiesto</returns>
        public List<TrackInfo> FilterTracksByType(List<TrackInfo> tracks, string trackType)
        {
            List<TrackInfo> result = new List<TrackInfo>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, trackType, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(tracks[i]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Filtra le tracce usando una lista di ID
        /// </summary>
        /// <param name="allTracks">Tutte le tracce disponibili</param>
        /// <param name="ids">ID da mantenere</param>
        /// <returns>Tracce con ID richiesti</returns>
        public List<TrackInfo> FilterTracksByIds(List<TrackInfo> allTracks, List<int> ids)
        {
            List<TrackInfo> result = new List<TrackInfo>();

            if (allTracks != null)
            {
                for (int i = 0; i < allTracks.Count; i++)
                {
                    if (ids.Contains(allTracks[i].Id))
                    {
                        result.Add(allTracks[i]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Estrae le lingue audio da una lista di tracce
        /// </summary>
        /// <param name="tracks">Tracce audio</param>
        /// <returns>Lingue audio</returns>
        public List<string> GetAudioLanguages(List<TrackInfo> tracks)
        {
            return this.GetLanguagesByType(tracks, "audio");
        }

        /// <summary>
        /// Estrae le lingue sottotitolo da una lista di tracce
        /// </summary>
        /// <param name="tracks">Tracce sottotitolo</param>
        /// <returns>Lingue sottotitolo</returns>
        public List<string> GetSubtitleLanguages(List<TrackInfo> tracks)
        {
            return this.GetLanguagesByType(tracks, "subtitles");
        }

        /// <summary>
        /// Popola nel record il riepilogo lingue risultanti
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="sourceTracks">Tracce sorgente</param>
        /// <param name="sourceAudioIds">ID audio sorgente mantenuti</param>
        /// <param name="audioTracks">Tracce audio lingua importate</param>
        /// <param name="subtitleTracks">Tracce sottotitolo lingua importate</param>
        /// <param name="filterSourceAudio">True se audio sorgente filtrato</param>
        /// <param name="filterSourceSubs">True se sottotitoli sorgente filtrati</param>
        /// <param name="options">Opzioni operative</param>
        public void PopulateResultLanguages(FileProcessingRecord record, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, List<TrackInfo> subtitleTracks, bool filterSourceAudio, bool filterSourceSubs, Options options)
        {
            List<string> resultAudioLangs = new List<string>();
            List<string> resultSubLangs = new List<string>();
            string lang;
            string srcLang;
            bool keepThis;
            if (!filterSourceAudio)
            {
                for (int i = 0; i < record.SourceAudioLangs.Count; i++)
                {
                    if (!resultAudioLangs.Contains(record.SourceAudioLangs[i]))
                    {
                        resultAudioLangs.Add(record.SourceAudioLangs[i]);
                    }
                }
            }
            else if (sourceTracks != null)
            {
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (!string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!sourceAudioIds.Contains(sourceTracks[i].Id))
                    {
                        continue;
                    }
                    lang = !string.IsNullOrEmpty(sourceTracks[i].Language) ? sourceTracks[i].Language : "und";
                    if (!resultAudioLangs.Contains(lang))
                    {
                        resultAudioLangs.Add(lang);
                    }
                }
            }

            for (int i = 0; i < audioTracks.Count; i++)
            {
                lang = !string.IsNullOrEmpty(audioTracks[i].Language) ? audioTracks[i].Language : "und";
                if (!resultAudioLangs.Contains(lang))
                {
                    resultAudioLangs.Add(lang);
                }
            }

            if (!filterSourceSubs)
            {
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    if (!resultSubLangs.Contains(record.SourceSubLangs[i]))
                    {
                        resultSubLangs.Add(record.SourceSubLangs[i]);
                    }
                }
            }
            else
            {
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    srcLang = record.SourceSubLangs[i];
                    keepThis = false;
                    for (int k = 0; k < options.KeepSourceSubtitleLangs.Count; k++)
                    {
                        if (string.Equals(srcLang, options.KeepSourceSubtitleLangs[k], StringComparison.OrdinalIgnoreCase))
                        {
                            keepThis = true;
                            break;
                        }
                    }
                    if (keepThis && !resultSubLangs.Contains(srcLang))
                    {
                        resultSubLangs.Add(srcLang);
                    }
                }
            }

            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                lang = !string.IsNullOrEmpty(subtitleTracks[i].Language) ? subtitleTracks[i].Language : "und";
                if (!resultSubLangs.Contains(lang))
                {
                    resultSubLangs.Add(lang);
                }
            }

            record.ResultAudioLangs = resultAudioLangs;
            record.ResultSubLangs = resultSubLangs;
        }

        /// <summary>
        /// Raccoglie le tracce lingua da importare
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="langTracks">Tracce del file lingua</param>
        /// <param name="mkvService">Servizio mkvmerge</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="codecPatterns">Pattern codec audio richiesti</param>
        /// <param name="audioTracks">Tracce audio selezionate</param>
        /// <param name="subtitleTracks">Tracce sottotitolo selezionate</param>
        /// <returns>Tracce lingua selezionate</returns>
        public List<TrackInfo> CollectLanguageTracks(FileProcessingRecord record, List<TrackInfo> langTracks, MkvToolsService mkvService, Options options, string[] codecPatterns, out List<TrackInfo> audioTracks, out List<TrackInfo> subtitleTracks)
        {
            List<TrackInfo> foundAudio;
            List<TrackInfo> foundSubs;
            string targetLanguage;
            audioTracks = new List<TrackInfo>();
            subtitleTracks = new List<TrackInfo>();

            if (langTracks == null)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  Impossibile leggere info tracce file lingua");
                record.ErrorMessage = "Impossibile leggere tracce file lingua";
                record.Status = FileStatus.Error;
            }
            else
            {
                for (int t = 0; t < options.TargetLanguage.Count; t++)
                {
                    targetLanguage = options.TargetLanguage[t];

                    if (!options.SubOnly)
                    {
                        foundAudio = mkvService.GetFilteredTracks(langTracks, targetLanguage, "audio", codecPatterns);
                        for (int a = 0; a < foundAudio.Count; a++)
                        {
                            audioTracks.Add(foundAudio[a]);
                        }
                    }

                    if (!options.AudioOnly)
                    {
                        foundSubs = mkvService.GetFilteredTracks(langTracks, targetLanguage, "subtitles", null);
                        for (int s = 0; s < foundSubs.Count; s++)
                        {
                            subtitleTracks.Add(foundSubs[s]);
                        }
                    }
                }
            }

            return langTracks;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Raccoglie le lingue presenti per uno specifico tipo traccia
        /// </summary>
        /// <param name="tracks">Tracce da analizzare</param>
        /// <param name="trackType">Tipo traccia richiesto</param>
        /// <returns>Lingue distinte trovate, con fallback und</returns>
        private List<string> GetLanguagesByType(List<TrackInfo> tracks, string trackType)
        {
            List<string> langs = new List<string>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, trackType, StringComparison.OrdinalIgnoreCase))
                    {
                        // Matroska può omettere la lingua: in quel caso usiamo il codice standard und
                        string lang = !string.IsNullOrEmpty(tracks[i].Language) ? tracks[i].Language : "und";
                        if (!langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }
            }

            return langs;
        }

        #endregion
    }
}
