using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MergeLanguageTracks
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
        /// <returns>True se mkvmerge e funzionante</returns>
        public bool VerifyMkvMerge()
        {
            bool result = false;

            try
            {
                // Esegue mkvmerge --version per confermare esistenza
                string output = this.RunProcess(this._mkvMergePath, "--version");
                result = (output.Length > 0);
            }
            catch
            {
                // mkvmerge non trovato o non eseguibile
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Ottiene le informazioni sulle tracce da un file MKV
        /// </summary>
        /// <param name="filePath">Percorso del file MKV</param>
        /// <returns>Lista delle tracce o null in caso di errore</returns>
        public List<TrackInfo> GetTrackInfo(string filePath)
        {
            List<TrackInfo> tracks = null;
            string jsonOutput = "";
            JsonDocument doc = null;
            JsonElement root;
            JsonElement tracksElement;
            TrackInfo track = null;

            try
            {
                jsonOutput = this.RunProcess(this._mkvMergePath, "-J", filePath);
            }
            catch
            {
                // mkvmerge non ha prodotto output valido
                ConsoleHelper.WriteWarning("Impossibile leggere info tracce per: " + filePath);
            }

            if (jsonOutput.Length > 0)
            {
                try
                {
                    tracks = new List<TrackInfo>();

                    // Parsing del documento JSON
                    doc = JsonDocument.Parse(jsonOutput);
                    root = doc.RootElement;

                    if (root.TryGetProperty("tracks", out tracksElement))
                    {
                        foreach (JsonElement trackEl in tracksElement.EnumerateArray())
                        {
                            track = new TrackInfo();

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
                            }

                            tracks.Add(track);
                        }
                    }

                    doc.Dispose();
                }
                catch (Exception ex)
                {
                    // Errore parsing JSON, tracce non disponibili
                    ConsoleHelper.WriteWarning("Errore parsing JSON tracce: " + ex.Message);
                    tracks = null;
                }
            }

            return tracks;
        }

        /// <summary>
        /// Ottiene informazioni complete su un file MKV incluso default_duration
        /// </summary>
        /// <param name="filePath">Percorso del file MKV</param>
        /// <returns>Info complete del file o null in caso di errore</returns>
        public MkvFileInfo GetFileInfo(string filePath)
        {
            MkvFileInfo result = null;
            string jsonOutput = "";
            JsonDocument doc = null;
            JsonElement root;
            JsonElement tracksElement;
            TrackInfo track = null;

            try
            {
                jsonOutput = this.RunProcess(this._mkvMergePath, "-J", filePath);
            }
            catch
            {
                // mkvmerge non ha prodotto output valido
                ConsoleHelper.WriteWarning("Impossibile leggere info file per: " + filePath);
            }

            if (jsonOutput.Length > 0)
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
                        }
                    }

                    // Parsing tracce
                    if (root.TryGetProperty("tracks", out tracksElement))
                    {
                        foreach (JsonElement trackEl in tracksElement.EnumerateArray())
                        {
                            track = new TrackInfo();

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

                                if (propsEl.TryGetProperty("default_duration", out JsonElement defDurEl))
                                {
                                    track.DefaultDurationNs = defDurEl.GetInt64();
                                }
                            }

                            result.Tracks.Add(track);
                        }
                    }

                    doc.Dispose();
                }
                catch (Exception ex)
                {
                    // Errore parsing JSON, info non disponibili
                    ConsoleHelper.WriteWarning("Errore parsing JSON file info: " + ex.Message);
                    result = null;
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

            // Verifica lingua ISO 639-2
            if (string.Equals(track.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                match = true;
            }
            // Verifica tag IETF
            else if (track.LanguageIetf.Length > 0)
            {
                if (track.LanguageIetf.StartsWith(language, StringComparison.OrdinalIgnoreCase) || string.Equals(track.LanguageIetf, language, StringComparison.OrdinalIgnoreCase))
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
            List<int> langAudioIds = new List<int>();
            List<int> langSubIds = new List<int>();
            string syncValue = "";

            // File output
            mkvArgs.Add("-o");
            mkvArgs.Add(req.OutputFile);

            // Selezione tracce audio sorgente
            if (req.FilterSourceAudio && req.SourceAudioIds.Count > 0)
            {
                mkvArgs.Add("--audio-tracks");
                mkvArgs.Add(JoinInts(req.SourceAudioIds));
            }
            else if (req.FilterSourceAudio && req.SourceAudioIds.Count == 0)
            {
                mkvArgs.Add("-A");
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

            // File lingua: niente video
            mkvArgs.Add("-D");

            // Tracce audio lingua
            for (int i = 0; i < req.LangAudioTracks.Count; i++)
            {
                langAudioIds.Add(req.LangAudioTracks[i].Id);
            }

            if (langAudioIds.Count > 0)
            {
                mkvArgs.Add("--audio-tracks");
                mkvArgs.Add(JoinInts(langAudioIds));

                // Applica delay e/o stretch
                if (req.AudioDelayMs != 0 || req.StretchFactor.Length > 0)
                {
                    for (int i = 0; i < langAudioIds.Count; i++)
                    {
                        syncValue = langAudioIds[i].ToString() + ":" + req.AudioDelayMs.ToString();
                        if (req.StretchFactor.Length > 0)
                        {
                            syncValue = syncValue + "," + req.StretchFactor;
                        }
                        mkvArgs.Add("--sync");
                        mkvArgs.Add(syncValue);
                    }
                }
            }
            else
            {
                mkvArgs.Add("-A");
            }

            // Tracce sottotitoli lingua
            for (int i = 0; i < req.LangSubTracks.Count; i++)
            {
                langSubIds.Add(req.LangSubTracks[i].Id);
            }

            if (langSubIds.Count > 0)
            {
                mkvArgs.Add("--subtitle-tracks");
                mkvArgs.Add(JoinInts(langSubIds));

                // Applica delay e/o stretch
                if (req.SubDelayMs != 0 || req.StretchFactor.Length > 0)
                {
                    for (int i = 0; i < langSubIds.Count; i++)
                    {
                        syncValue = langSubIds[i].ToString() + ":" + req.SubDelayMs.ToString();
                        if (req.StretchFactor.Length > 0)
                        {
                            syncValue = syncValue + "," + req.StretchFactor;
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

            // Percorso file lingua
            mkvArgs.Add(req.LanguageFile);

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
            int exitCode = -1;
            StringBuilder sb = new StringBuilder();
            Process proc = null;
            string stdout = "";
            string stderr = "";

            try
            {
                proc = new Process();
                proc.StartInfo.FileName = this._mkvMergePath;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                proc.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // Usa ArgumentList per encoding corretto su Linux (UTF-8)
                for (int i = 0; i < args.Count; i++)
                {
                    proc.StartInfo.ArgumentList.Add(args[i]);
                }

                proc.Start();

                // Legge stdout e stderr in parallelo per prevenire deadlock
                Thread convergence = new Thread(() => { stdout = proc.StandardOutput.ReadToEnd(); });
                convergence.Start();
                stderr = proc.StandardError.ReadToEnd();
                convergence.Join();

                proc.WaitForExit();

                exitCode = proc.ExitCode;
                sb.Append(stdout);
                if (stderr.Length > 0)
                {
                    sb.Append(stderr);
                }
            }
            catch (Exception ex)
            {
                // Errore avvio processo mkvmerge
                sb.Append("Eccezione durante l'esecuzione di mkvmerge: " + ex.Message);
            }
            finally
            {
                if (proc != null) { proc.Dispose(); proc = null; }
            }

            output = sb.ToString();

            return exitCode;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue un processo e restituisce solo stdout (stderr loggato come warning)
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Argomenti individuali da passare al processo</param>
        /// <returns>Output stdout del processo</returns>
        private string RunProcess(string fileName, params string[] arguments)
        {
            Process proc = null;
            string stdout = "";
            string stderr = "";

            try
            {
                proc = new Process();
                proc.StartInfo.FileName = fileName;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                proc.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // Usa ArgumentList per encoding corretto su Linux (UTF-8)
                for (int i = 0; i < arguments.Length; i++)
                {
                    proc.StartInfo.ArgumentList.Add(arguments[i]);
                }

                proc.Start();

                // Legge stdout e stderr in parallelo per prevenire deadlock
                Thread convergence = new Thread(() => { stdout = proc.StandardOutput.ReadToEnd(); });
                convergence.Start();
                stderr = proc.StandardError.ReadToEnd();
                convergence.Join();

                proc.WaitForExit();

                // Logga stderr come warning se presente (non contamina stdout)
                if (stderr.Length > 0)
                {
                    ConsoleHelper.WriteWarning("stderr: " + stderr.TrimEnd());
                }
            }
            finally
            {
                if (proc != null) { proc.Dispose(); proc = null; }
            }

            return stdout;
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
