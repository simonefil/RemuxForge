using System.Collections.Generic;

namespace RemuxForge.Core
{
    /// <summary>
    /// Opzioni da riga di comando: parsing e validazione dei parametri CLI
    /// </summary>
    public class Options
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public Options()
        {
            this.Help = false;
            this.SourceFolder = "";
            this.LanguageFolder = "";
            this.TargetLanguage = new List<string>();
            this.MatchPattern = @"S(\d+)E(\d+)";
            this.Overwrite = false;
            this.DestinationFolder = "";
            this.AudioDelay = 0;
            this.SubtitleDelay = 0;
            this.SpeedCorrection = true;
            this.FrameSync = false;
            this.DeepAnalysis = false;
            this.CropSourceTo43 = false;
            this.CropLangTo43 = false;
            this.AudioCodec = new List<string>();
            this.SubOnly = false;
            this.AudioOnly = false;
            this.KeepSourceAudioLangs = new List<string>();
            this.KeepSourceAudioCodec = new List<string>();
            this.KeepSourceSubtitleLangs = new List<string>();
            // Usa path salvato in AppSettings se disponibile, altrimenti default
            this.MkvMergePath = (AppSettingsService.Instance.Settings.Tools.MkvMergePath.Length > 0) ? AppSettingsService.Instance.Settings.Tools.MkvMergePath : "mkvmerge";
            this.Recursive = true;
            this.DryRun = false;
            this.FileExtensions = new List<string> { "mkv" };
            this.ConvertFormat = "";
            this.RenameAllTracks = false;
            this.ErrorMessage = "";
            this.EncodingProfileName = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Parsa gli argomenti da riga di comando in un'istanza
        /// </summary>
        /// <param name="args">Array di argomenti da riga di comando</param>
        /// <returns>Istanza Options popolata</returns>
        public static Options Parse(string[] args)
        {
            Options options = new Options();
            int i = 0;
            string key = "";
            bool hasNextValue = false;
            string value = "";
            int delay = 0;
            string[] parts = null;
            string trimmed = "";
            bool recognized = true;

            while (i < args.Length && options.ErrorMessage.Length == 0)
            {
                // Normalizza la chiave argomento in minuscolo, rimuovi trattini iniziali
                key = args[i].ToLower().TrimStart('-');

                // Determina se l'argomento successivo e' un valore o un altro flag
                // Accetta valori negativi numerici (es. -500 per delay)
                hasNextValue = (i + 1 < args.Length) && (!args[i + 1].StartsWith("-") || (args[i + 1].Length > 1 && char.IsDigit(args[i + 1][1])));

                // Gestione switch che non richiedono un valore
                if (key == "h" || key == "help" || key == "?")
                {
                    options.Help = true;
                    i++;
                    continue;
                }
                if (key == "fs" || key == "framesync")
                {
                    options.FrameSync = true;
                    i++;
                    continue;
                }
                if (key == "da" || key == "deep-analysis")
                {
                    options.DeepAnalysis = true;
                    i++;
                    continue;
                }
                if (key == "cs43" || key == "crop-source-43")
                {
                    options.CropSourceTo43 = true;
                    i++;
                    continue;
                }
                if (key == "cl43" || key == "crop-lang-43")
                {
                    options.CropLangTo43 = true;
                    i++;
                    continue;
                }
                if (key == "so" || key == "sub-only")
                {
                    options.SubOnly = true;
                    i++;
                    continue;
                }
                if (key == "ao" || key == "audio-only")
                {
                    options.AudioOnly = true;
                    i++;
                    continue;
                }
                if (key == "r" || key == "recursive")
                {
                    options.Recursive = true;
                    i++;
                    continue;
                }
                if (key == "nr" || key == "no-recursive")
                {
                    options.Recursive = false;
                    i++;
                    continue;
                }
                if (key == "n" || key == "dry-run")
                {
                    options.DryRun = true;
                    i++;
                    continue;
                }
                if (key == "rt" || key == "rename-tracks")
                {
                    options.RenameAllTracks = true;
                    i++;
                    continue;
                }
                if (key == "o" || key == "overwrite")
                {
                    options.Overwrite = true;
                    i++;
                    continue;
                }

                // Se non e' uno switch riconosciuto e non ha valore successivo, parametro sconosciuto
                if (!hasNextValue)
                {
                    options.ErrorMessage = "Parametro sconosciuto: -" + key;
                }
                else
                {
                    value = args[i + 1];
                    recognized = true;

                    if (key == "s" || key == "source")
                    {
                        options.SourceFolder = value;
                    }
                    else if (key == "l" || key == "language")
                    {
                        options.LanguageFolder = value;
                    }
                    else if (key == "t" || key == "target-language")
                    {
                        ParseCsvToList(value, options.TargetLanguage);
                    }
                    else if (key == "m" || key == "match-pattern")
                    {
                        options.MatchPattern = value;
                    }
                    else if (key == "d" || key == "destination")
                    {
                        options.DestinationFolder = value;
                    }
                    else if (key == "ad" || key == "audio-delay")
                    {
                        if (!int.TryParse(value, out delay))
                        {
                            options.ErrorMessage = "Valore non valido per audio-delay: " + value;
                        }
                        options.AudioDelay = delay;
                    }
                    else if (key == "sd" || key == "subtitle-delay")
                    {
                        if (!int.TryParse(value, out delay))
                        {
                            options.ErrorMessage = "Valore non valido per subtitle-delay: " + value;
                        }
                        options.SubtitleDelay = delay;
                    }
                    else if (key == "ac" || key == "audio-codec")
                    {
                        ParseCsvToList(value, options.AudioCodec);
                    }
                    else if (key == "ksa" || key == "keep-source-audio")
                    {
                        ParseCsvToList(value, options.KeepSourceAudioLangs);
                    }
                    else if (key == "ksac" || key == "keep-source-audio-codec")
                    {
                        ParseCsvToList(value, options.KeepSourceAudioCodec);
                    }
                    else if (key == "kss" || key == "keep-source-subs")
                    {
                        ParseCsvToList(value, options.KeepSourceSubtitleLangs);
                    }
                    else if (key == "cf" || key == "convert-format")
                    {
                        string cfLower = value.Trim().ToLower();
                        if (cfLower == "flac" || cfLower == "opus")
                        {
                            options.ConvertFormat = cfLower;
                        }
                        else
                        {
                            options.ErrorMessage = "Formato conversione non valido: " + value + ". Valori validi: flac, opus";
                        }
                    }
                    else if (key == "mkv" || key == "mkvmerge-path")
                    {
                        options.MkvMergePath = value;
                    }
                    else if (key == "ep" || key == "encoding-profile")
                    {
                        options.EncodingProfileName = value;
                    }
                    else if (key == "ext" || key == "extensions")
                    {
                        // Sostituisce il default con le estensioni specificate
                        options.FileExtensions.Clear();
                        parts = value.Split(',');
                        for (int j = 0; j < parts.Length; j++)
                        {
                            trimmed = parts[j].Trim().TrimStart('.');
                            if (trimmed.Length > 0)
                            {
                                options.FileExtensions.Add(trimmed);
                            }
                        }
                    }
                    else
                    {
                        // Parametro con valore non riconosciuto
                        recognized = false;
                        options.ErrorMessage = "Parametro sconosciuto: -" + key;
                    }

                    // Avanza oltre la coppia chiave-valore
                    if (recognized)
                    {
                        i += 2;
                    }
                }
            }

            // Validazione mutua esclusione frame-sync e deep analysis
            if (options.FrameSync && options.DeepAnalysis && options.ErrorMessage.Length == 0)
            {
                options.ErrorMessage = "Le opzioni -fs e -da sono mutuamente esclusive";
            }

            return options;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Parsa una stringa CSV e aggiunge i valori trimmati non vuoti alla lista
        /// </summary>
        /// <param name="value">Stringa CSV da parsare</param>
        /// <param name="target">Lista destinazione</param>
        private static void ParseCsvToList(string value, List<string> target)
        {
            string[] parts = value.Split(',');
            string trimmed = "";

            for (int j = 0; j < parts.Length; j++)
            {
                trimmed = parts[j].Trim();
                if (trimmed.Length > 0)
                {
                    target.Add(trimmed);
                }
            }
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Indica se e' stato richiesto il messaggio di aiuto (-h, --help)
        /// </summary>
        public bool Help { get; set; }

        /// <summary>
        /// Percorso della cartella sorgente contenente i file MKV (-s, --source)
        /// </summary>
        public string SourceFolder { get; set; }

        /// <summary>
        /// Percorso della cartella lingua contenente i file MKV nella lingua alternativa (-l, --language)
        /// </summary>
        public string LanguageFolder { get; set; }

        /// <summary>
        /// Lista di codici lingua ISO 639-2 da estrarre (-t, --target-language). Supporta valori multipli separati da virgola
        /// </summary>
        public List<string> TargetLanguage { get; set; }

        /// <summary>
        /// Pattern regex per il matching degli episodi (-m, --match-pattern). Default: S(\d+)E(\d+)
        /// </summary>
        public string MatchPattern { get; set; }

        /// <summary>
        /// Modalita' overwrite: sovrascrive i file sorgente (-o, --overwrite). Default: false
        /// </summary>
        public bool Overwrite { get; set; }

        /// <summary>
        /// Cartella di destinazione per l'output (-d, --destination).
        /// </summary>
        public string DestinationFolder { get; set; }

        /// <summary>
        /// Ritardo audio manuale in millisecondi (-ad, --audio-delay). Sommato all'offset frame-sync se abilitato
        /// </summary>
        public int AudioDelay { get; set; }

        /// <summary>
        /// Ritardo sottotitoli manuale in millisecondi (-sd, --subtitle-delay). Sommato all'offset frame-sync se abilitato
        /// </summary>
        public int SubtitleDelay { get; set; }

        /// <summary>
        /// Abilita rilevamento automatico mismatch velocita'. Default: true
        /// </summary>
        public bool SpeedCorrection { get; set; }

        /// <summary>
        /// Indica se il raffinamento frame sync e' abilitato (-fs, --framesync)
        /// </summary>
        public bool FrameSync { get; set; }

        /// <summary>
        /// Indica se la deep analysis e' abilitata (-da, --deep-analysis). Mutuamente esclusiva con FrameSync
        /// </summary>
        public bool DeepAnalysis { get; set; }

        /// <summary>
        /// Croppa i frame estratti dal file sorgente a 4:3 centrato per rimuovere pillarbox (-cs43, --crop-source-43)
        /// </summary>
        public bool CropSourceTo43 { get; set; }

        /// <summary>
        /// Croppa i frame estratti dal file lingua a 4:3 centrato per rimuovere pillarbox (-cl43, --crop-lang-43)
        /// </summary>
        public bool CropLangTo43 { get; set; }

        /// <summary>
        /// Lista di codec audio da importare (-ac, --audio-codec). Solo le tracce con questi codec verranno importate
        /// </summary>
        public List<string> AudioCodec { get; set; }

        /// <summary>
        /// Importa solo sottotitoli, ignora tracce audio (-so, --sub-only)
        /// </summary>
        public bool SubOnly { get; set; }

        /// <summary>
        /// Importa solo tracce audio, ignora sottotitoli (-ao, --audio-only)
        /// </summary>
        public bool AudioOnly { get; set; }

        /// <summary>
        /// Lista di codici lingua da mantenere nelle tracce audio sorgente (-ksa, --keep-source-audio)
        /// </summary>
        public List<string> KeepSourceAudioLangs { get; set; }

        /// <summary>
        /// Lista di codec audio da mantenere nelle tracce sorgente (-ksac, --keep-source-audio-codec). Solo le tracce con questi codec verranno mantenute
        /// </summary>
        public List<string> KeepSourceAudioCodec { get; set; }

        /// <summary>
        /// Lista di codici lingua da mantenere nelle tracce sottotitoli sorgente (-kss, --keep-source-subs)
        /// </summary>
        public List<string> KeepSourceSubtitleLangs { get; set; }

        /// <summary>
        /// Percorso dell'eseguibile mkvmerge (-mkv, --mkvmerge-path). Default: cerca nel PATH
        /// </summary>
        public string MkvMergePath { get; set; }

        /// <summary>
        /// Formato conversione tracce lossless (-cf, --convert-format). Valori: "flac", "opus", "" = disabilitato
        /// </summary>
        public string ConvertFormat { get; set; }

        /// <summary>
        /// Forza rinomina di tutte le tracce audio (non solo quelle convertite) (-rt, --rename-tracks)
        /// </summary>
        public bool RenameAllTracks { get; set; }

        /// <summary>
        /// Indica se cercare ricorsivamente nelle sottocartelle (-r, --recursive). Default: true
        /// </summary>
        public bool Recursive { get; set; }

        /// <summary>
        /// Modalita' dry run: mostra cosa verrebbe fatto senza eseguire (-n, --dry-run)
        /// </summary>
        public bool DryRun { get; set; }

        /// <summary>
        /// Lista di estensioni file da cercare (-ext, --extensions). Default: mkv
        /// </summary>
        public List<string> FileExtensions { get; set; }

        /// <summary>
        /// Messaggio di errore dal parsing. Vuoto se nessun errore
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Nome del profilo di encoding video post-merge, vuoto = disabilitato
        /// </summary>
        public string EncodingProfileName { get; set; }

        #endregion
    }
}
