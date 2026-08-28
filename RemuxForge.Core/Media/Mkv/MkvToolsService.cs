using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RemuxForge.Core.Media.Mkv
{
    /// <summary>
    /// Servizio per operazioni su file MKV tramite mkvmerge
    /// </summary>
    public class MkvToolsService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvmerge
        /// </summary>
        private string _mkvMergePath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        public MkvToolsService(string mkvMergePath)
        {
            this._mkvMergePath = mkvMergePath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che mkvmerge sia accessibile e funzionante
        /// </summary>
        /// <returns>True se mkvmerge è funzionante</returns>
        public bool VerifyMkvMerge()
        {
            bool result;

            try
            {
                // Esegue mkvmerge --version per confermare esistenza
                ProcessResult procResult = ProcessRunner.Run(this._mkvMergePath, new string[] { "--version" });
                string output = procResult.Stdout;
                result = !string.IsNullOrEmpty(output);
            }
            catch
            {
                // mkvmerge non trovato o non eseguibile
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "mkvmerge non accessibile");
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Ottiene informazioni complete su un file MKV incluso default_duration
        /// </summary>
        /// <param name="filePath">Percorso del file MKV</param>
        /// <returns>Info complete del file o null in caso di errore</returns>
        public MkvFileInfo GetFileInfo(string filePath)
        {
            return this.GetFileInfo(filePath, 0);
        }

        /// <summary>
        /// Ottiene informazioni complete su un file MKV con un timeout esplicito
        /// </summary>
        public MkvFileInfo GetFileInfo(string filePath, int timeoutMs)
        {
            MkvFileInfo result = null;
            string jsonOutput = "";
            JsonDocument doc = null;
            JsonElement root;
            JsonElement tracksElement;
            try
            {
                ProcessResult procResult = ProcessRunner.Run(this._mkvMergePath, new string[] { "-J", filePath }, timeoutMs);
                jsonOutput = procResult.Stdout;
            }
            catch
            {
                // mkvmerge non ha prodotto output valido
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "Impossibile leggere info file per: " + filePath);
            }

            if (!string.IsNullOrEmpty(jsonOutput))
            {
                try
                {
                    result = new MkvFileInfo();

                    doc = JsonDocument.Parse(jsonOutput);
                    root = doc.RootElement;

                    // Parsing durata container
                    if (root.TryGetProperty("container", out JsonElement containerEl))
                    {
                        if (containerEl.TryGetProperty("properties", out JsonElement containerPropsEl))
                        {
                            if (containerPropsEl.TryGetProperty("duration", out JsonElement durationEl))
                            {
                                result.ContainerDurationNs = durationEl.GetInt64();
                            }

                            // Parsing titolo segmento
                            if (containerPropsEl.TryGetProperty("title", out JsonElement titleEl))
                            {
                                result.ContainerTitle = titleEl.GetString() ?? "";
                            }
                        }
                    }

                    // Parsing tracce
                    if (root.TryGetProperty("tracks", out tracksElement))
                    {
                        foreach (JsonElement trackEl in tracksElement.EnumerateArray())
                        {
                            result.Tracks.Add(ParseTrackFromJson(trackEl));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Errore parsing JSON, info non disponibili
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "Errore parsing JSON file info: " + ex.Message);
                    result = null;
                }
                finally
                {
                    if (doc != null)
                        doc.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica se una traccia corrisponde al codice lingua specificato
        /// </summary>
        /// <param name="track">Traccia da verificare</param>
        /// <param name="language">Codice lingua da confrontare</param>
        /// <returns>True se la traccia corrisponde alla lingua</returns>
        public bool IsLanguageMatch(TrackInfo track, string language)
        {
            bool match = false;
            string trackLanguage;
            string requestedLanguage;

            if (track == null || language == null)
            {
                return match;
            }

            trackLanguage = !string.IsNullOrEmpty(track.Language) ? track.Language : "und";
            requestedLanguage = language.Trim();

            // Verifica lingua ISO 639-2
            if (string.Equals(trackLanguage, requestedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                match = true;
            }
            // Verifica tag IETF
            else if (!string.IsNullOrEmpty(track.LanguageIetf))
            {
                if (track.LanguageIetf.StartsWith(requestedLanguage, StringComparison.OrdinalIgnoreCase) || string.Equals(track.LanguageIetf, requestedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                }
            }

            return match;
        }

        /// <summary>
        /// Verifica se una traccia corrisponde a uno dei codici lingua nella lista
        /// </summary>
        /// <param name="track">Traccia da verificare</param>
        /// <param name="languages">Lista di codici lingua</param>
        /// <returns>True se la traccia corrisponde a una delle lingue</returns>
        public bool IsLanguageInList(TrackInfo track, List<string> languages)
        {
            bool match = false;

            for (int i = 0; i < languages.Count; i++)
            {
                if (this.IsLanguageMatch(track, languages[i]))
                {
                    match = true;
                    break;
                }
            }

            return match;
        }

        /// <summary>
        /// Filtra tracce MKV per tipo, lingua e codec
        /// </summary>
        /// <param name="allTracks">Lista completa delle tracce</param>
        /// <param name="language">Codice lingua da filtrare</param>
        /// <param name="trackType">Tipo traccia (audio, subtitles, video)</param>
        /// <param name="codecPatterns">Pattern codec da confrontare</param>
        /// <returns>Lista delle tracce corrispondenti ai filtri</returns>
        public List<TrackInfo> GetFilteredTracks(List<TrackInfo> allTracks, string language, string trackType, string[] codecPatterns)
        {
            List<TrackInfo> result = new List<TrackInfo>();

            for (int i = 0; i < allTracks.Count; i++)
            {
                TrackInfo track = allTracks[i];

                if (!string.Equals(track.Type, trackType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!this.IsLanguageMatch(track, language))
                {
                    continue;
                }

                if (codecPatterns != null && string.Equals(trackType, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CodecMapping.MatchesCodec(track.Codec, codecPatterns))
                    {
                        continue;
                    }
                }

                result.Add(track);
            }

            return result;
        }

        /// <summary>
        /// Ottiene gli ID traccia sorgente da mantenere in base a filtri lingua/codec
        /// </summary>
        /// <param name="allTracks">Lista completa delle tracce</param>
        /// <param name="trackType">Tipo traccia da filtrare</param>
        /// <param name="keepLanguages">Lingue da mantenere</param>
        /// <param name="codecPatterns">Pattern codec da confrontare</param>
        /// <returns>Lista degli ID traccia corrispondenti</returns>
        public List<int> GetSourceTrackIds(List<TrackInfo> allTracks, string trackType, List<string> keepLanguages, string[] codecPatterns)
        {
            List<int> trackIds = new List<int>();

            for (int i = 0; i < allTracks.Count; i++)
            {
                TrackInfo track = allTracks[i];

                if (!string.Equals(track.Type, trackType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (keepLanguages.Count > 0 && !this.IsLanguageInList(track, keepLanguages))
                {
                    continue;
                }

                if (codecPatterns != null && !CodecMapping.MatchesCodec(track.Codec, codecPatterns))
                {
                    continue;
                }

                trackIds.Add(track.Id);
            }

            return trackIds;
        }

        /// <summary>
        /// Costruisce gli argomenti mkvmerge per unire tracce sorgente e lingua
        /// </summary>
        /// <param name="req">Parametri per la costruzione del comando merge</param>
        /// <returns>Lista degli argomenti per mkvmerge</returns>
        public List<string> BuildMergeArguments(MergeRequest req)
        {
            List<string> mkvArgs = new List<string>();
            List<int> sourceAudioKeep = new List<int>();
            List<int> langAudioKeep = new List<int>();
            List<int> langSubIds = new List<int>();
            string syncValue;
            bool hasConvertedSource = (req.ConvertedSourceTracks != null && req.ConvertedSourceTracks.Count > 0);
            bool hasConvertedLang = (req.ConvertedLangTracks != null && req.ConvertedLangTracks.Count > 0);
            bool hasProcessedSubs = (req.ProcessedLangSubTracks != null && req.ProcessedLangSubTracks.Count > 0);
            bool bypassAudioDelay;

            if (req.RequiredProcessedLangTrackIds != null)
            {
                foreach (int requiredTrackId in req.RequiredProcessedLangTrackIds)
                {
                    if (req.ConvertedLangTracks == null ||
                        !req.ConvertedLangTracks.ContainsKey(requiredTrackId) ||
                        string.IsNullOrEmpty(req.ConvertedLangTracks[requiredTrackId]))
                    {
                        throw new InvalidOperationException("Output audio Language obbligatorio mancante per track " + requiredTrackId);
                    }
                }
            }

            // File output
            mkvArgs.Add("-o");
            mkvArgs.Add(req.OutputFile);

            // Imposta titolo segmento dal file sorgente (previene copia dal file lingua)
            mkvArgs.Add("--title");
            mkvArgs.Add(req.SourceTitle);

            // Separa tracce audio sorgente: non convertite (dal file) vs convertite (file separati)
            if (req.FilterSourceAudio)
            {
                for (int i = 0; i < req.SourceAudioIds.Count; i++)
                {
                    // Se la traccia è stata convertita, non includerla dal file sorgente
                    if (hasConvertedSource && req.ConvertedSourceTracks.ContainsKey(req.SourceAudioIds[i]))
                    {
                        continue;
                    }
                    sourceAudioKeep.Add(req.SourceAudioIds[i]);
                }

                if (sourceAudioKeep.Count > 0)
                {
                    mkvArgs.Add("--audio-tracks");
                    mkvArgs.Add(JoinInts(sourceAudioKeep));
                }
                else if (!hasConvertedSource)
                {
                    // Nessuna traccia audio da sorgente e nessuna convertita
                    mkvArgs.Add("-A");
                }
                else
                {
                    // Tutte convertite, nessuna audio dal file sorgente
                    mkvArgs.Add("-A");
                }
            }

            // Selezione tracce sottotitoli sorgente
            if (req.FilterSourceSubs && req.SourceSubIds.Count > 0)
            {
                mkvArgs.Add("--subtitle-tracks");
                mkvArgs.Add(JoinInts(req.SourceSubIds));
            }
            else if (req.FilterSourceSubs && req.SourceSubIds.Count == 0)
            {
                mkvArgs.Add("-S");
            }

            // File sorgente
            mkvArgs.Add(req.SourceFile);

            // File convertiti da sorgente: aggiunti come input separati (solo audio, no video/sub)
            if (hasConvertedSource)
            {
                for (int i = 0; i < req.SourceAudioIds.Count; i++)
                {
                    int srcId = req.SourceAudioIds[i];
                    if (req.ConvertedSourceTracks.ContainsKey(srcId))
                    {
                        // Recupera TrackInfo originale per lingua e info audio
                        TrackInfo origTrack = req.ProcessedSourceAudioInfo != null && req.ProcessedSourceAudioInfo.TryGetValue(srcId, out TrackInfo processedSourceTrack) ? processedSourceTrack : FindTrackById(req.SourceAudioTracks, srcId);

                        // File convertito: no video, no sottotitoli
                        mkvArgs.Add("-D");
                        mkvArgs.Add("-S");

                        // Imposta lingua e titolo sulla traccia convertita (trackId 0 nel file standalone)
                        if (origTrack != null)
                        {
                            AddStandaloneAudioMetadata(mkvArgs, origTrack);
                        }

                        mkvArgs.Add(req.ConvertedSourceTracks[srcId]);
                    }
                }
            }

            // Sezione file lingua (solo se LanguageFile specificato)
            if (!string.IsNullOrEmpty(req.LanguageFile))
            {
                // File lingua: niente video
                mkvArgs.Add("-D");

                // Separa tracce audio lingua: non convertite vs convertite
                for (int i = 0; i < req.LangAudioTracks.Count; i++)
                {
                    int langId = req.LangAudioTracks[i].Id;

                    if (hasConvertedLang && req.ConvertedLangTracks.ContainsKey(langId))
                    {
                        continue;
                    }
                    langAudioKeep.Add(langId);
                }

                if (langAudioKeep.Count > 0)
                {
                    mkvArgs.Add("--audio-tracks");
                    mkvArgs.Add(JoinInts(langAudioKeep));

                    // Lo stretch audio deve essere materializzato da FFmpeg; mkvmerge applica solo il delay residuo
                    if (req.AudioDelayMs != 0)
                    {
                        for (int i = 0; i < langAudioKeep.Count; i++)
                        {
                            syncValue = langAudioKeep[i] + ":" + req.AudioDelayMs;
                            mkvArgs.Add("--sync");
                            mkvArgs.Add(syncValue);
                        }
                    }

                }
                else if (!hasConvertedLang)
                {
                    mkvArgs.Add("-A");
                }
                else
                {
                    // Tutte convertite, nessuna audio dal file lingua
                    mkvArgs.Add("-A");
                }

                // Tracce sottotitoli lingua: esclude quelle sostituite da timeline edit o canvas rewrite
                for (int i = 0; i < req.LangSubTracks.Count; i++)
                {
                    if (hasProcessedSubs && req.ProcessedLangSubTracks.ContainsKey(req.LangSubTracks[i].Id))
                    {
                        continue;
                    }
                    langSubIds.Add(req.LangSubTracks[i].Id);
                }

                if (langSubIds.Count > 0)
                {
                    mkvArgs.Add("--subtitle-tracks");
                    mkvArgs.Add(JoinInts(langSubIds));

                    // Applica delay e/o stretch ai sottotitoli
                    if (req.SubDelayMs != 0 || !string.IsNullOrEmpty(req.SubtitleStretchFactor))
                    {
                        for (int i = 0; i < langSubIds.Count; i++)
                        {
                            syncValue = langSubIds[i] + ":" + req.SubDelayMs;
                            if (!string.IsNullOrEmpty(req.SubtitleStretchFactor))
                            {
                                syncValue = syncValue + "," + req.SubtitleStretchFactor;
                            }
                            mkvArgs.Add("--sync");
                            mkvArgs.Add(syncValue);
                        }
                    }
                }
                else
                {
                    mkvArgs.Add("-S");
                }

                // Percorso file lingua (senza chapters per evitare duplicati con quelli del source)
                mkvArgs.Add("--no-chapters");
                mkvArgs.Add(req.LanguageFile);

                // File convertiti da lingua: aggiunti come input separati con il solo delay residuo
                if (hasConvertedLang)
                {
                    for (int i = 0; i < req.LangAudioTracks.Count; i++)
                    {
                        int langId = req.LangAudioTracks[i].Id;
                        if (req.ConvertedLangTracks.ContainsKey(langId))
                        {
                            bypassAudioDelay = req.AudioDelayBypassedLangIds != null && req.AudioDelayBypassedLangIds.Contains(langId);
                            // File convertito: no video, no sottotitoli
                            mkvArgs.Add("-D");
                            mkvArgs.Add("-S");

                            // Lo stretch è già incorporato nei campioni del file convertito
                            if (!bypassAudioDelay && req.AudioDelayMs != 0)
                            {
                                syncValue = "0:" + req.AudioDelayMs;
                                mkvArgs.Add("--sync");
                                mkvArgs.Add(syncValue);
                            }

                            // Imposta lingua e titolo sulla traccia (trackId 0 nel file standalone)
                            TrackInfo origLangTrack = req.ProcessedLangAudioInfo != null && req.ProcessedLangAudioInfo.TryGetValue(langId, out TrackInfo processedLangTrack) ? processedLangTrack : FindTrackById(req.LangAudioTracks, langId);
                            if (origLangTrack != null)
                            {
                                AddStandaloneAudioMetadata(mkvArgs, origLangTrack);
                            }

                            mkvArgs.Add(req.ConvertedLangTracks[langId]);
                        }
                    }
                }

                // Sottotitoli processati: aggiunti come input separati con delay/stretch e metadati originali
                if (hasProcessedSubs)
                {
                    for (int i = 0; i < req.LangSubTracks.Count; i++)
                    {
                        int subId = req.LangSubTracks[i].Id;
                        if (req.ProcessedLangSubTracks.ContainsKey(subId))
                        {
                            // File processato: no video, no audio
                            mkvArgs.Add("-D");
                            mkvArgs.Add("-A");

                            // Applica delay/stretch via mkvmerge anche ai sottotitoli standalone
                            if (req.SubDelayMs != 0 || !string.IsNullOrEmpty(req.SubtitleStretchFactor))
                            {
                                syncValue = "0:" + req.SubDelayMs;
                                if (!string.IsNullOrEmpty(req.SubtitleStretchFactor))
                                {
                                    syncValue = syncValue + "," + req.SubtitleStretchFactor;
                                }
                                mkvArgs.Add("--sync");
                                mkvArgs.Add(syncValue);
                            }

                            AddStandaloneSubtitleMetadata(mkvArgs, req.LangSubTracks[i]);

                            mkvArgs.Add(req.ProcessedLangSubTracks[subId]);
                        }
                    }
                }
            }

            return mkvArgs;
        }

        /// <summary>
        /// Formatta argomenti merge come stringa per log
        /// </summary>
        /// <param name="args">Lista degli argomenti mkvmerge</param>
        /// <returns>Stringa formattata del comando completo</returns>
        public string FormatMergeCommand(List<string> args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(this._mkvMergePath);

            for (int i = 0; i < args.Count; i++)
            {
                sb.Append(" ");

                // Quota argomenti con spazi o backslash
                if (args[i].IndexOf(' ') >= 0 || args[i].IndexOf('\\') >= 0)
                {
                    sb.Append("\"" + args[i] + "\"");
                }
                else
                {
                    sb.Append(args[i]);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Esegue mkvmerge con gli argomenti dati
        /// </summary>
        /// <param name="args">Lista degli argomenti mkvmerge</param>
        /// <param name="output">Output combinato stdout+stderr</param>
        /// <returns>Codice di uscita del processo</returns>
        public int ExecuteMerge(List<string> args, out string output)
        {
            StringBuilder sb = new StringBuilder();
            ProcessResult result = ProcessRunner.Run(this._mkvMergePath, args.ToArray());

            sb.Append(result.Stdout);
            if (!string.IsNullOrEmpty(result.Stderr))
            {
                sb.Append(result.Stderr);
            }

            output = sb.ToString();

            return result.ExitCode;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Parsa una singola traccia dal JSON mkvmerge
        /// </summary>
        /// <param name="trackEl">Elemento JSON della traccia</param>
        /// <returns>TrackInfo popolato</returns>
        private static TrackInfo ParseTrackFromJson(JsonElement trackEl)
        {
            TrackInfo track = new TrackInfo();

            if (trackEl.TryGetProperty("id", out JsonElement idEl))
            {
                track.Id = idEl.GetInt32();
            }

            if (trackEl.TryGetProperty("type", out JsonElement typeEl))
            {
                track.Type = typeEl.GetString();
            }

            if (trackEl.TryGetProperty("codec", out JsonElement codecEl))
            {
                track.Codec = codecEl.GetString();
            }

            if (trackEl.TryGetProperty("properties", out JsonElement propsEl))
            {
                if (propsEl.TryGetProperty("language", out JsonElement langEl))
                {
                    track.Language = langEl.GetString();
                }

                if (propsEl.TryGetProperty("language_ietf", out JsonElement langIetfEl))
                {
                    string ietfVal = langIetfEl.ValueKind == JsonValueKind.Null ? "" : langIetfEl.GetString();
                    track.LanguageIetf = ietfVal;
                }

                if (propsEl.TryGetProperty("track_name", out JsonElement nameEl))
                {
                    string nameVal = nameEl.ValueKind == JsonValueKind.Null ? "" : nameEl.GetString();
                    track.Name = nameVal;
                }

                if (propsEl.TryGetProperty("default_track", out JsonElement defaultEl))
                {
                    track.DefaultTrack = defaultEl.ValueKind == JsonValueKind.True;
                }

                if (propsEl.TryGetProperty("forced_track", out JsonElement forcedEl))
                {
                    track.ForcedTrack = forcedEl.ValueKind == JsonValueKind.True;
                }

                if (propsEl.TryGetProperty("default_duration", out JsonElement defDurEl))
                {
                    track.DefaultDurationNs = defDurEl.GetInt64();
                }

                if (propsEl.TryGetProperty("tag_number_of_frames", out JsonElement frameCountEl))
                {
                    long frameCount;
                    string frameCountText = frameCountEl.ValueKind == JsonValueKind.Null ? "" : frameCountEl.GetString();
                    if (long.TryParse(frameCountText, out frameCount))
                    {
                        track.VideoFrameCount = frameCount;
                    }
                }

                if (propsEl.TryGetProperty("tag_duration", out JsonElement durationEl))
                {
                    long durationNs;
                    string durationText = durationEl.ValueKind == JsonValueKind.Null ? "" : durationEl.GetString();
                    if (TryParseMkvDurationNs(durationText, out durationNs))
                    {
                        track.TrackDurationNs = durationNs;
                    }
                }

                if (propsEl.TryGetProperty("audio_channels", out JsonElement chEl))
                {
                    track.Channels = chEl.GetInt32();
                }

                if (propsEl.TryGetProperty("audio_bits_per_sample", out JsonElement bpsEl))
                {
                    track.BitsPerSample = bpsEl.GetInt32();
                }

                if (propsEl.TryGetProperty("audio_sampling_frequency", out JsonElement freqEl))
                {
                    track.SamplingFrequency = freqEl.GetInt32();
                }

                if (propsEl.TryGetProperty("audio_bit_rate", out JsonElement audioBitrateEl))
                {
                    track.Bitrate = ReadIntProperty(audioBitrateEl);
                }
                else if (propsEl.TryGetProperty("bitrate", out JsonElement bitrateEl))
                {
                    track.Bitrate = ReadIntProperty(bitrateEl);
                }
                else if (propsEl.TryGetProperty("tag_bps", out JsonElement tagBpsEl))
                {
                    track.Bitrate = ReadIntProperty(tagBpsEl);
                }
            }

            return track;
        }

        /// <summary>
        /// Legge una proprietà intera da JSON accettando numero o stringa
        /// </summary>
        private static int ReadIntProperty(JsonElement element)
        {
            int result = 0;
            string text;

            if (element.ValueKind == JsonValueKind.Number)
            {
                element.TryGetInt32(out result);
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString();
                int.TryParse(text, out result);
            }

            return result;
        }

        /// <summary>
        /// Parsa durata Matroska nel formato HH:mm:ss.nnnnnnnnn in nanosecondi
        /// </summary>
        /// <param name="text">Durata testuale</param>
        /// <param name="durationNs">Durata in nanosecondi</param>
        /// <returns>True se parsing riuscito</returns>
        private static bool TryParseMkvDurationNs(string text, out long durationNs)
        {
            string[] timeParts;
            string[] secondParts;
            long hours;
            long minutes;
            long seconds;
            string fraction;

            durationNs = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            timeParts = text.Split(':');
            if (timeParts.Length != 3)
            {
                return false;
            }

            secondParts = timeParts[2].Split('.');
            if (secondParts.Length < 1)
            {
                return false;
            }

            if (!long.TryParse(timeParts[0], out hours) ||
                !long.TryParse(timeParts[1], out minutes) ||
                !long.TryParse(secondParts[0], out seconds))
            {
                return false;
            }

            durationNs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000000000L;
            if (secondParts.Length > 1)
            {
                fraction = secondParts[1];
                if (fraction.Length > 9)
                {
                    fraction = fraction.Substring(0, 9);
                }
                while (fraction.Length < 9)
                {
                    fraction = fraction + "0";
                }
                if (long.TryParse(fraction, out long fractionNs))
                {
                    durationNs += fractionNs;
                }
            }

            return true;
        }

        /// <summary>
        /// Cerca una traccia per ID in una lista di TrackInfo
        /// </summary>
        /// <param name="tracks">Lista di tracce</param>
        /// <param name="trackId">ID traccia da cercare</param>
        /// <returns>TrackInfo corrispondente o null se non trovata</returns>
        private static TrackInfo FindTrackById(List<TrackInfo> tracks, int trackId)
        {
            TrackInfo result = null;

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Id == trackId)
                {
                    result = tracks[i];
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Aggiunge i metadati originali per una traccia audio standalone
        /// </summary>
        /// <param name="mkvArgs">Lista argomenti mkvmerge in costruzione</param>
        /// <param name="origTrack">Traccia originale con metadati lingua</param>
        private static void AddStandaloneAudioMetadata(List<string> mkvArgs, TrackInfo origTrack)
        {
            // Lingua: usa IETF se disponibile, altrimenti ISO 639-2
            if (!string.IsNullOrEmpty(origTrack.LanguageIetf))
            {
                mkvArgs.Add("--language");
                mkvArgs.Add("0:" + origTrack.LanguageIetf);
            }
            else if (!string.IsNullOrEmpty(origTrack.Language))
            {
                mkvArgs.Add("--language");
                mkvArgs.Add("0:" + origTrack.Language);
            }

            if (!string.IsNullOrEmpty(origTrack.Name))
            {
                mkvArgs.Add("--track-name");
                mkvArgs.Add("0:" + origTrack.Name);
            }

            mkvArgs.Add("--default-track");
            mkvArgs.Add("0:" + (origTrack.DefaultTrack ? "yes" : "no"));
            mkvArgs.Add("--forced-display-flag");
            mkvArgs.Add("0:" + (origTrack.ForcedTrack ? "yes" : "no"));
        }

        /// <summary>
        /// Aggiunge metadati completi per una traccia sottotitoli standalone (trackId 0)
        /// </summary>
        /// <param name="mkvArgs">Lista argomenti mkvmerge in costruzione</param>
        /// <param name="origTrack">Traccia sottotitoli originale</param>
        private static void AddStandaloneSubtitleMetadata(List<string> mkvArgs, TrackInfo origTrack)
        {
            // Lingua: usa IETF se disponibile, altrimenti ISO 639-2
            if (!string.IsNullOrEmpty(origTrack.LanguageIetf))
            {
                mkvArgs.Add("--language");
                mkvArgs.Add("0:" + origTrack.LanguageIetf);
            }
            else if (!string.IsNullOrEmpty(origTrack.Language))
            {
                mkvArgs.Add("--language");
                mkvArgs.Add("0:" + origTrack.Language);
            }

            if (!string.IsNullOrEmpty(origTrack.Name))
            {
                mkvArgs.Add("--track-name");
                mkvArgs.Add("0:" + origTrack.Name);
            }

            mkvArgs.Add("--default-track");
            mkvArgs.Add("0:" + (origTrack.DefaultTrack ? "yes" : "no"));
            mkvArgs.Add("--forced-display-flag");
            mkvArgs.Add("0:" + (origTrack.ForcedTrack ? "yes" : "no"));
        }

        /// <summary>
        /// Unisce una lista di interi in stringa separata da virgole
        /// </summary>
        /// <param name="values">Lista di interi da unire</param>
        /// <returns>Stringa con valori separati da virgole</returns>
        private static string JoinInts(List<int> values)
        {
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }
                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        #endregion
    }
}
