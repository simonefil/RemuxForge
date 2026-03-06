using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Applicazione TUI interattiva per MergeLanguageTracks
    /// </summary>
    public class TuiApp
    {
        #region Classi interne

        /// <summary>
        /// Definizione colori per uno schema tema completo
        /// </summary>
        private class ThemeColors
        {
            public Color BaseFg, BaseBg, BaseFocusFg, BaseFocusBg, BaseHotFg, BaseHotFocusFg, BaseDisabledFg;
            public Color MenuFg, MenuBg, MenuFocusFg, MenuFocusBg, MenuHotFg, MenuHotFocusFg, MenuDisabledFg;
            public Color DlgFg, DlgBg, DlgFocusFg, DlgFocusBg, DlgHotFg, DlgHotFocusFg, DlgDisabledFg;
            public Color ErrFg, ErrBg, ErrFocusFg, ErrHotFg;
            public Color HlFg, HlBg, HlFocusFg, HlFocusBg, HlHotFg, HlHotFocusFg;
            public Color InputFg, InputBg, InputFocusFg, InputFocusBg;
        }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Dizionario statico definizioni temi
        /// </summary>
        private static Dictionary<string, ThemeColors> s_themes;

        /// <summary>
        /// Testo help completo per dialog informativo
        /// </summary>
        private const string HELP_TEXT =
            "=== TASTI ===\n" +
            "F1         Help\n" +
            "F2         Apre configurazione\n" +
            "F5         Scan cartelle e matching episodi\n" +
            "F6         Analizza episodio selezionato\n" +
            "F7         Analizza tutti gli episodi pendenti\n" +
            "F8         Skip/Unskip episodio selezionato\n" +
            "F9         Merge episodio selezionato\n" +
            "F10        Merge tutti gli episodi analizzati\n" +
            "Enter      Modifica delay manuale episodio\n" +
            "Ctrl+Q     Esci\n" +
            "\n=== CONFIGURAZIONE ===\n" +
            "\n-- Cartelle --\n" +
            "Source: cartella contenente i file MKV originali.\n" +
            "Lingua: cartella contenente i file MKV nella lingua alternativa da importare. I file vengono abbinati per episodio tramite il pattern regex.\n" +
            "Destinazione: cartella output per i file mergiati. Se vuota, i file vengono creati accanto ai sorgenti.\n" +
            "Sovrascrivi sorgente: se attivo, il file risultante sostituisce il sorgente originale.\n" +
            "Ricorsivo: cerca file anche nelle sottocartelle.\n" +
            "\n-- Lingua e Tracce --\n" +
            "Lingua target: codici ISO 639-2 delle lingue da importare dal file lingua, separati da virgola. Esempio: ita oppure ita,eng\n" +
            "Codec audio: importa solo tracce audio con questi codec dal file lingua. Vuoto = tutti i codec. Esempio: aac oppure aac,ac3\n" +
            "Keep src audio: lingue delle tracce audio da MANTENERE nel file sorgente. Le altre vengono rimosse. Vuoto = mantiene tutte. Esempio: jpn oppure jpn,eng\n" +
            "Keep src codec: mantiene solo le tracce audio sorgente con questi codec. Vuoto = tutti.\n" +
            "Keep src sub: lingue dei sottotitoli da mantenere nel file sorgente. Vuoto = mantiene tutti.\n" +
            "Solo sottotitoli: importa solo sottotitoli dal file lingua, ignora le tracce audio.\n" +
            "Solo audio: importa solo tracce audio dal file lingua, ignora i sottotitoli.\n" +
            "\n-- Sincronizzazione --\n" +
            "Frame-sync: sincronizzazione tramite rilevamento tagli scena (scene-cut). Trova il delay iniziale confrontando i tagli nei primi minuti, poi verifica a 9 punti.\n" +
            "Delay audio: offset manuale in ms da sommare al risultato frame-sync per le tracce audio importate. Valori negativi anticipano, positivi ritardano.\n" +
            "Delay sub: offset manuale in ms per i sottotitoli. Indipendente dal delay audio.\n" +
            "\n-- Avanzate --\n" +
            "Pattern match: regex per abbinare episodi tra le due cartelle. I gruppi catturati vengono usati come identificativo episodio. Default: S(\\d+)E(\\d+) (formato SxxExx)\n" +
            "Estensioni: estensioni file da cercare, senza punto. Default: mkv\n" +
            "Cartella tools: percorso dove cercare ffmpeg se non presente nel PATH di sistema.\n" +
            "Percorso mkv: percorso completo di mkvmerge se non presente nel PATH. Default: mkvmerge (cerca nel PATH).\n" +
            "\n=== NOTE FRAME-SYNC ===\n" +
            "Il frame-sync trova l'offset rilevando i tagli scena (scene-cut) nei frame video di sorgente e lingua. Opera in due fasi:\n" +
            "\n" +
            "Fase 1 - Ricerca delay iniziale: estrae frame grayscale 320x240 dai primi minuti di entrambi i file. Rileva i tagli scena (variazioni MSE > soglia tra frame consecutivi). Per ogni coppia di tagli (sorgente, lingua) calcola il delay candidato. I candidati vengono raggruppati per votazione: il delay con piu' voti coerenti viene selezionato e verificato tramite firma MSE attorno ai tagli.\n" +
            "\n" +
            "Fase 2 - Verifica a 9 punti: conferma il delay a 9 posizioni distribuite nel video (10%, 20%, ..., 90%). Per ogni punto estrae segmenti di 10s (sorgente) e 15s (lingua), rileva i tagli scena e cerca corrispondenze. Se un punto fallisce, viene riprovato con finestra allargata (20s/30s). Servono almeno 5/9 punti validi e coerenti.\n" +
            "\n" +
            "Il delay manuale (globale o per-episodio) viene SOMMATO all'offset frame-sync calcolato.\n" +
            "\n=== RILEVAMENTO SCENE-CUT ===\n" +
            "I tagli scena vengono rilevati confrontando frame consecutivi via Mean Squared Error (MSE). Ogni frame e' estratto in grayscale 320x240 (76.800 pixel).\n" +
            "\n" +
            "Formula MSE: MSE = (1/N) * somma((pixel[i] - pixel[i+1])^2), con N = 76.800. Se la MSE tra due frame consecutivi supera la soglia (50.0), viene rilevato un taglio scena.\n" +
            "\n" +
            "La verifica dei match avviene confrontando la firma visiva attorno al taglio: 10 frame centrati sul punto di taglio (5 prima, 5 dopo). La MSE della firma tra sorgente e lingua deve essere < 100 e > 0.05 (quest'ultima soglia scarta scene nere/statiche ambigue).\n" +
            "\n=== RILEVAMENTO STRETCH ===\n" +
            "Lo stretch detection rileva e corregge automaticamente differenze di velocita' tra sorgente e lingua causate da FPS diversi (es. 23.976 vs 25 fps, PAL speed-up).\n" +
            "\n" +
            "Fase 1 - Rilevamento: legge il default_duration (nanosecondi) dalle tracce video di entrambi i file tramite mkvmerge. Calcola il rapporto velocita': stretchRatio = sourceDuration / langDuration. Se la differenza dal rapporto 1.0 e' inferiore a 0.001 (0.1%), non c'e' stretch.\n" +
            "\n" +
            "Fase 2 - Ricerca delay iniziale: estrae frame dai primi secondi del sorgente e dalla lingua. Rileva i tagli scena in entrambi. Per ogni coppia di tagli, calcola il delay candidato compensando il drift dovuto alla differenza di velocita': langDelay = (lngCutMs - srcCutMs) - srcCutMs * (inverseRatio - 1). Il delay iniziale e' selezionato tramite votazione e verificato con firma MSE.\n" +
            "\n" +
            "Fase 3 - Correzione via mkvmerge: calcola lo stretch factor come rapporto dei default_duration e il sync delay corretto. La correzione avviene interamente in mkvmerge tramite il parametro --sync TID:delay,num/den che applica time-stretching senza ricodifica alle tracce audio e sottotitoli importate.\n" +
            "\n" +
            "Fase 4 - Verifica: controlla la correzione a 9 punti (10%-90% del video) tramite scene-cut matching con retry adattivo. Per ogni punto rileva i tagli scena e verifica la corrispondenza compensando il drift. Un punto e' valido se l'errore e' inferiore a 1 frame. Servono almeno 5/9 punti validi per confermare la correzione.\n" +
            "\n=== CODEC AUDIO ===\n" +
            "I codec specificati nel campo 'Codec audio' filtrano le tracce audio importate dal file lingua. Il matching e' ESATTO, non parziale.\n" +
            "\n" +
            "Dolby:\n" +
            "  AC-3        Dolby Digital (DD, AC3)\n" +
            "  E-AC-3      Dolby Digital Plus (DD+, EAC3, include Atmos lossy)\n" +
            "  TrueHD      Dolby TrueHD (include Atmos lossless)\n" +
            "  MLP         Meridian Lossless Packing (base di TrueHD)\n" +
            "\n" +
            "DTS:\n" +
            "  DTS         DTS Core / Digital Surround\n" +
            "  DTS-HD MA   DTS-HD Master Audio (lossless)\n" +
            "  DTS-HD HR   DTS-HD High Resolution\n" +
            "  DTS-ES      DTS Extended Surround\n" +
            "  DTS:X       DTS:X (object-based, estensione di DTS-HD MA)\n" +
            "\n" +
            "Lossless:\n" +
            "  FLAC        Free Lossless Audio Codec\n" +
            "  PCM         Audio non compresso (LPCM, WAV)\n" +
            "  ALAC        Apple Lossless\n" +
            "\n" +
            "Lossy:\n" +
            "  AAC         Advanced Audio Coding (LC, HE-AAC, HE-AACv2)\n" +
            "  MP3         MPEG Audio Layer 3\n" +
            "  MP2         MPEG Audio Layer 2\n" +
            "  Opus        Opus (WebM, alta qualita' a basso bitrate)\n" +
            "  Vorbis      Ogg Vorbis\n" +
            "\n" +
            "Alias accettati:\n" +
            "  EAC3, DDP, DD+  -> E-AC-3\n" +
            "  AC3, DD         -> AC-3\n" +
            "  DTSX            -> DTS:X\n" +
            "  LPCM, WAV       -> PCM\n" +
            "  ATMOS           -> TrueHD e E-AC-3\n" +
            "\n" +
            "IMPORTANTE: -ac DTS matcha SOLO DTS core, NON DTS-HD MA. Usare -ac DTS-HD per matchare DTS-HD MA e HR.\n" +
            "\n=== CODICI LINGUA ===\n" +
            "I campi lingua accettano codici ISO 639-2 (3 lettere).\n" +
            "\n" +
            "Comuni: ita, eng, jpn, ger/deu, fra/fre, spa, por, rus, chi/zho, kor\n" +
            "Altri:  ara, hin, pol, tur, nld/dut, swe, nor, dan, fin, hun, ces/cze\n" +
            "Speciali: und (undefined), mul (multiple), zxx (no language)\n";

        /// <summary>
        /// Pipeline di elaborazione
        /// </summary>
        private ProcessingPipeline _pipeline;

        /// <summary>
        /// Lista record file correnti
        /// </summary>
        private List<FileProcessingRecord> _records;

        /// <summary>
        /// DataTable per la tabella file
        /// </summary>
        private DataTable _dataTable;

        /// <summary>
        /// Vista tabella file
        /// </summary>
        private TableView _tableView;

        /// <summary>
        /// Vista dettaglio file selezionato
        /// </summary>
        private TextView _detailView;

        /// <summary>
        /// Vista log
        /// </summary>
        private TextView _logView;

        /// <summary>
        /// Flag: elaborazione in corso
        /// </summary>
        private bool _isProcessing;

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        private Options _opts;

        /// <summary>
        /// Istanza applicazione Terminal.Gui
        /// </summary>
        private IApplication _app;

        /// <summary>
        /// Finestra principale
        /// </summary>
        private Window _mainWindow;

        /// <summary>
        /// Nome del tema grafico corrente
        /// </summary>
        private string _currentTheme;

        /// <summary>
        /// Schema colore base per pannelli principali
        /// </summary>
        private Scheme _schemeBase;

        /// <summary>
        /// Schema colore per menu e status bar
        /// </summary>
        private Scheme _schemeMenu;

        /// <summary>
        /// Schema colore per dialog e popup
        /// </summary>
        private Scheme _schemeDialog;

        /// <summary>
        /// Schema colore per messaggi errore
        /// </summary>
        private Scheme _schemeError;

        /// <summary>
        /// Schema colore per intestazioni sezioni
        /// </summary>
        private Scheme _schemeHighlight;

        /// <summary>
        /// Schema colori per campi input (fg/bg invertiti rispetto a dialog)
        /// </summary>
        private Scheme _schemeInput;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TuiApp()
        {
            this._pipeline = new ProcessingPipeline();
            this._records = new List<FileProcessingRecord>();
            this._isProcessing = false;
            this._opts = new Options();
            this._currentTheme = "Nord";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Avvia l'applicazione TUI
        /// </summary>
        public void Run()
        {
            try
            {
                // Inizializza applicazione v2
                this._app = Application.Create().Init();

                // Configura QuitKey su Ctrl+Q
                Application.QuitKey = Key.Q.WithCtrl;

                // Glifi ASCII per massima compatibilita terminale
                Glyphs.LeftBracket = new System.Text.Rune('[');
                Glyphs.RightBracket = new System.Text.Rune(']');
                Glyphs.LeftDefaultIndicator = new System.Text.Rune('[');
                Glyphs.RightDefaultIndicator = new System.Text.Rune(']');
                Glyphs.CheckStateChecked = new System.Text.Rune('X');
                Glyphs.CheckStateUnChecked = new System.Text.Rune(' ');

                // Disabilita shadow per estetica pulita
                Button.DefaultShadow = ShadowStyle.None;
                Dialog.DefaultShadow = ShadowStyle.None;

                // Applica tema grafico di default
                this.ApplyTheme("Nord");

                // Crea finestra principale con bordo doppio stile DOS
                this._mainWindow = new Window()
                {
                    Title = " MergeLanguageTracks v2.0 ",
                    BorderStyle = LineStyle.Double,
                    SchemeName = "Base"
                };

                // Collega eventi pipeline
                this._pipeline.OnLogMessage += (string text, ConsoleColor color) =>
                {
                    this._app.Invoke(() =>
                    {
                        this.AppendLog(text);
                    });
                };

                this._pipeline.OnFileUpdated += (FileProcessingRecord record) =>
                {
                    this._app.Invoke(() =>
                    {
                        this.UpdateTable();
                    });
                };

                // Costruisci layout
                this.BuildMainLayout();

                // Messaggio iniziale
                this.AppendLog("Pronto. Premere F2 per configurare, F5 per scan.");

                // Avvia applicazione
                this._app.Run(this._mainWindow);

                // Cleanup
                this._mainWindow.Dispose();
                this._app.Dispose();
            }
            catch (Exception ex)
            {
                // Cleanup terminale in caso di errore
                try
                {
                    this._app.Dispose();
                }
                catch
                {
                    // Ignora errori durante cleanup
                }

                // Ripristina terminale e stampa errore
                Console.ResetColor();
                Console.Clear();
                Console.WriteLine("ERRORE TUI: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Console.WriteLine();
                Console.WriteLine("Premi un tasto per uscire...");
                Console.ReadKey();
            }
        }

        #endregion

        #region Metodi privati - Helpers

        /// <summary>
        /// Crea un Button toggle stile [X]/[ ] come checkbox ASCII
        /// </summary>
        /// <param name="label">Testo della label</param>
        /// <param name="initialState">Stato iniziale</param>
        /// <param name="x">Posizione X</param>
        /// <param name="y">Posizione Y</param>
        /// <param name="schemeName">Schema colori</param>
        /// <param name="stateRef">Riferimento allo stato corrente</param>
        private Button CreateToggleLabel(string label, bool initialState, int x, int y, string schemeName, out bool[] stateRef)
        {
            bool[] state = new bool[] { initialState };
            stateRef = state;

            Button btn = new Button()
            {
                Text = (initialState ? "[X] " : "[ ] ") + label,
                X = x,
                Y = y,
                NoDecorations = true,
                NoPadding = true,
                ShadowStyle = ShadowStyle.None,
                SchemeName = schemeName
            };

            // Toggle al click/space/enter
            btn.Accepting += (object sender, CommandEventArgs e) =>
            {
                state[0] = !state[0];
                btn.Text = (state[0] ? "[X] " : "[ ] ") + label;
                e.Handled = true;
            };

            return btn;
        }

        /// <summary>
        /// Parsa una stringa CSV e popola la lista destinazione con i valori trimmati non vuoti
        /// </summary>
        /// <param name="csvText">Testo CSV da parsare</param>
        /// <param name="target">Lista destinazione da popolare</param>
        private void ParseCsvToList(string csvText, List<string> target)
        {
            target.Clear();
            string[] parts = csvText.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string trimmed = parts[i].Trim();
                if (trimmed.Length > 0)
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// Applica il tema grafico specificato salvando gli schemi nei campi classe
        /// </summary>
        /// <param name="themeName">Nome del tema da applicare</param>
        private void ApplyTheme(string themeName)
        {
            ThemeColors tc = null;
            Dictionary<string, Scheme> schemes = null;

            // Inizializza dizionario temi al primo utilizzo
            if (s_themes == null)
            {
                s_themes = InitializeThemes();
            }

            // Fallback a DOS Blue se tema non trovato
            if (!s_themes.ContainsKey(themeName))
            {
                themeName = "DOS Blue";
            }

            tc = s_themes[themeName];

            // Crea i 6 schemi colore dai colori tema
            // Editable e ReadOnly espliciti: evita auto-derivazione bg da fg
            this._schemeBase = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.BaseFg, tc.BaseBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.BaseFocusFg, tc.BaseFocusBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.BaseHotFg, tc.BaseBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.BaseHotFocusFg, tc.BaseFocusBg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.BaseDisabledFg, tc.BaseBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.BaseFocusFg, tc.BaseFocusBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.BaseFg, tc.BaseBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.BaseFg, tc.BaseBg)
            };

            this._schemeMenu = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.MenuFg, tc.MenuBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.MenuFocusFg, tc.MenuFocusBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.MenuHotFg, tc.MenuBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.MenuHotFocusFg, tc.MenuFocusBg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.MenuDisabledFg, tc.MenuBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.MenuFocusFg, tc.MenuFocusBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.MenuFg, tc.MenuBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.MenuFg, tc.MenuBg)
            };

            this._schemeDialog = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.DlgFg, tc.DlgBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.DlgFocusFg, tc.DlgFocusBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.DlgHotFg, tc.DlgBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.DlgHotFocusFg, tc.DlgFocusBg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.DlgDisabledFg, tc.DlgBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.DlgFocusFg, tc.DlgFocusBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.DlgFg, tc.DlgBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.DlgFg, tc.DlgBg)
            };

            this._schemeError = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.ErrFg, tc.ErrBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.ErrFocusFg, tc.ErrBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.ErrHotFg, tc.ErrBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.ErrHotFg, tc.ErrBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.ErrFocusFg, tc.ErrBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.ErrFg, tc.ErrBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.ErrFg, tc.ErrBg)
            };

            this._schemeHighlight = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.HlFg, tc.HlBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.HlFocusFg, tc.HlFocusBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.HlHotFg, tc.HlBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.HlHotFocusFg, tc.HlFocusBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.HlFocusFg, tc.HlFocusBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.HlFg, tc.HlBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.HlFg, tc.HlBg)
            };

            this._schemeInput = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.InputFocusFg, tc.InputFocusBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.InputFocusFg, tc.InputFocusBg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.DlgDisabledFg, tc.InputBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.InputFocusFg, tc.InputFocusBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg)
            };

            // Sovrascrive gli schemi nel dizionario del tema corrente
            // Workaround: AddScheme ha un bug (chiama GetSchemes due volte creando copie diverse)
            schemes = SchemeManager.GetSchemesForCurrentTheme();
            schemes["Base"] = this._schemeBase;
            schemes["Menu"] = this._schemeMenu;
            schemes["Dialog"] = this._schemeDialog;
            schemes["Error"] = this._schemeError;
            schemes["Highlight"] = this._schemeHighlight;
            schemes["Input"] = this._schemeInput;

            // Salva tema corrente
            this._currentTheme = themeName;

            // Ridisegna se la finestra esiste gia
            if (this._mainWindow != null)
            {
                this._mainWindow.SetNeedsDraw();
            }
        }

        /// <summary>
        /// Crea il dizionario statico delle definizioni temi
        /// </summary>
        /// <returns>Dizionario nome tema e colori</returns>
        private static Dictionary<string, ThemeColors> InitializeThemes()
        {
            Dictionary<string, ThemeColors> themes = new Dictionary<string, ThemeColors>();
            ThemeColors tc = null;

            // Nord - toni polari freddi (IntelliJ Nord)
            tc = new ThemeColors();
            Color n0 = new Color(46, 52, 64); Color n1 = new Color(59, 66, 82); Color n2 = new Color(67, 76, 94); Color n3 = new Color(76, 86, 106);
            Color n4 = new Color(216, 222, 233); Color n6 = new Color(236, 239, 244);
            Color n8 = new Color(136, 192, 208); Color n9 = new Color(129, 161, 193); Color n11 = new Color(191, 97, 106); Color n13 = new Color(235, 203, 139);
            tc.BaseFg = n4; tc.BaseBg = n0; tc.BaseFocusFg = n6; tc.BaseFocusBg = n2; tc.BaseHotFg = n8; tc.BaseHotFocusFg = n8; tc.BaseDisabledFg = n3;
            tc.MenuFg = n0; tc.MenuBg = n9; tc.MenuFocusFg = n0; tc.MenuFocusBg = n8; tc.MenuHotFg = n6; tc.MenuHotFocusFg = n6; tc.MenuDisabledFg = n3;
            tc.DlgFg = n4; tc.DlgBg = n1; tc.DlgFocusFg = n0; tc.DlgFocusBg = n8; tc.DlgHotFg = n13; tc.DlgHotFocusFg = n13; tc.DlgDisabledFg = n3;
            tc.ErrFg = n6; tc.ErrBg = n11; tc.ErrFocusFg = n13; tc.ErrHotFg = n13;
            tc.HlFg = n8; tc.HlBg = n0; tc.HlFocusFg = n8; tc.HlFocusBg = n2; tc.HlHotFg = n13; tc.HlHotFocusFg = n13;
            tc.InputFg = n0; tc.InputBg = n4; tc.InputFocusFg = n0; tc.InputFocusBg = n6;
            themes["Nord"] = tc;

            // Matrix - verde su nero, stile terminale hacker
            tc = new ThemeColors();
            Color mBlack = new Color(0, 0, 0); Color mGreen = new Color(51, 255, 51); Color mDarkGreen = new Color(0, 85, 0);
            Color mBrightGreen = new Color(150, 255, 150); Color mWhite = new Color(255, 255, 255); Color mDimGreen = new Color(0, 50, 0);
            tc.BaseFg = mGreen; tc.BaseBg = mBlack; tc.BaseFocusFg = mWhite; tc.BaseFocusBg = mDarkGreen; tc.BaseHotFg = mBrightGreen; tc.BaseHotFocusFg = mWhite; tc.BaseDisabledFg = mDimGreen;
            tc.MenuFg = mBlack; tc.MenuBg = mDarkGreen; tc.MenuFocusFg = mBlack; tc.MenuFocusBg = mGreen; tc.MenuHotFg = mBrightGreen; tc.MenuHotFocusFg = mWhite; tc.MenuDisabledFg = mDimGreen;
            tc.DlgFg = mGreen; tc.DlgBg = mBlack; tc.DlgFocusFg = mBlack; tc.DlgFocusBg = mGreen; tc.DlgHotFg = mWhite; tc.DlgHotFocusFg = mWhite; tc.DlgDisabledFg = mDimGreen;
            tc.ErrFg = mWhite; tc.ErrBg = new Color(170, 0, 0); tc.ErrFocusFg = mGreen; tc.ErrHotFg = mGreen;
            tc.HlFg = mWhite; tc.HlBg = mBlack; tc.HlFocusFg = mWhite; tc.HlFocusBg = mDarkGreen; tc.HlHotFg = mGreen; tc.HlHotFocusFg = mBrightGreen;
            tc.InputFg = mBlack; tc.InputBg = mGreen; tc.InputFocusFg = mBlack; tc.InputFocusBg = mBrightGreen;
            themes["Matrix"] = tc;

            // Cyberpunk - neon fucsia e ciano, viola/indaco scuri
            tc = new ThemeColors();
            Color cIndaco = new Color(20, 10, 40); Color cPetrolio = new Color(10, 30, 45); Color cFucsia = new Color(255, 0, 200);
            Color cCyan = new Color(0, 255, 255); Color cBrightFucsia = new Color(255, 130, 230); Color cDarkIndaco = new Color(40, 20, 70);
            Color cDimCyan = new Color(0, 100, 100); Color cWhite = new Color(255, 255, 255);
            tc.BaseFg = cCyan; tc.BaseBg = cIndaco; tc.BaseFocusFg = cWhite; tc.BaseFocusBg = cDarkIndaco; tc.BaseHotFg = cFucsia; tc.BaseHotFocusFg = cBrightFucsia; tc.BaseDisabledFg = cDimCyan;
            tc.MenuFg = cIndaco; tc.MenuBg = cFucsia; tc.MenuFocusFg = cIndaco; tc.MenuFocusBg = cCyan; tc.MenuHotFg = cWhite; tc.MenuHotFocusFg = cWhite; tc.MenuDisabledFg = cDimCyan;
            tc.DlgFg = cBrightFucsia; tc.DlgBg = cPetrolio; tc.DlgFocusFg = cIndaco; tc.DlgFocusBg = cCyan; tc.DlgHotFg = cCyan; tc.DlgHotFocusFg = cWhite; tc.DlgDisabledFg = cDimCyan;
            tc.ErrFg = cWhite; tc.ErrBg = new Color(200, 0, 80); tc.ErrFocusFg = cCyan; tc.ErrHotFg = cCyan;
            tc.HlFg = cFucsia; tc.HlBg = cIndaco; tc.HlFocusFg = cBrightFucsia; tc.HlFocusBg = cDarkIndaco; tc.HlHotFg = cCyan; tc.HlHotFocusFg = cCyan;
            tc.InputFg = cIndaco; tc.InputBg = cCyan; tc.InputFocusFg = cIndaco; tc.InputFocusBg = cBrightFucsia;
            themes["Cyberpunk"] = tc;

            // Solarized Dark - palette Ethan Schoonover variante scura
            tc = new ThemeColors();
            Color sd03 = new Color(0, 43, 54); Color sd02 = new Color(7, 54, 66); Color sd01 = new Color(88, 110, 117);
            Color sd0 = new Color(131, 148, 150); Color sd1 = new Color(147, 161, 161); Color sd2 = new Color(238, 232, 213);
            Color sdYellow = new Color(181, 137, 0); Color sdOrange = new Color(203, 75, 22); Color sdRed = new Color(220, 50, 47); Color sdCyan = new Color(42, 161, 152);
            tc.BaseFg = sd0; tc.BaseBg = sd03; tc.BaseFocusFg = sd2; tc.BaseFocusBg = sd02; tc.BaseHotFg = sdYellow; tc.BaseHotFocusFg = sdYellow; tc.BaseDisabledFg = sd01;
            tc.MenuFg = sd01; tc.MenuBg = sd2; tc.MenuFocusFg = sd03; tc.MenuFocusBg = sdCyan; tc.MenuHotFg = sdOrange; tc.MenuHotFocusFg = sdOrange; tc.MenuDisabledFg = sd1;
            tc.DlgFg = sd0; tc.DlgBg = sd02; tc.DlgFocusFg = sd03; tc.DlgFocusBg = sdCyan; tc.DlgHotFg = sdYellow; tc.DlgHotFocusFg = sdYellow; tc.DlgDisabledFg = sd01;
            tc.ErrFg = sd2; tc.ErrBg = sdRed; tc.ErrFocusFg = sdYellow; tc.ErrHotFg = sdYellow;
            tc.HlFg = sdYellow; tc.HlBg = sd03; tc.HlFocusFg = sdYellow; tc.HlFocusBg = sd02; tc.HlHotFg = sdCyan; tc.HlHotFocusFg = sdCyan;
            tc.InputFg = sd03; tc.InputBg = sd2; tc.InputFocusFg = sd03; tc.InputFocusBg = sdCyan;
            themes["Solarized Dark"] = tc;

            // Solarized Light - palette Solarized variante chiara
            tc = new ThemeColors();
            Color sl3 = new Color(253, 246, 227); Color sl2 = new Color(238, 232, 213); Color sl1 = new Color(147, 161, 161);
            Color sl00 = new Color(101, 123, 131); Color sl01 = new Color(88, 110, 117); Color sl02 = new Color(7, 54, 66); Color sl03 = new Color(0, 43, 54);
            Color slBlue = new Color(38, 139, 210); Color slRed = new Color(220, 50, 47); Color slCyan = new Color(42, 161, 152);
            tc.BaseFg = sl00; tc.BaseBg = sl3; tc.BaseFocusFg = sl03; tc.BaseFocusBg = sl2; tc.BaseHotFg = slBlue; tc.BaseHotFocusFg = slBlue; tc.BaseDisabledFg = sl1;
            tc.MenuFg = sl3; tc.MenuBg = sl02; tc.MenuFocusFg = sl3; tc.MenuFocusBg = slCyan; tc.MenuHotFg = slBlue; tc.MenuHotFocusFg = slBlue; tc.MenuDisabledFg = sl1;
            tc.DlgFg = sl00; tc.DlgBg = sl2; tc.DlgFocusFg = sl03; tc.DlgFocusBg = slCyan; tc.DlgHotFg = slBlue; tc.DlgHotFocusFg = slBlue; tc.DlgDisabledFg = sl1;
            tc.ErrFg = sl3; tc.ErrBg = slRed; tc.ErrFocusFg = sl2; tc.ErrHotFg = sl2;
            tc.HlFg = slBlue; tc.HlBg = sl3; tc.HlFocusFg = slBlue; tc.HlFocusBg = sl2; tc.HlHotFg = slCyan; tc.HlHotFocusFg = slCyan;
            tc.InputFg = sl02; tc.InputBg = sl3; tc.InputFocusFg = sl03; tc.InputFocusBg = slCyan;
            themes["Solarized Light"] = tc;

            // Cybergum - palette Cybergum6 di Naym, teal e rosa
            tc = new ThemeColors();
            Color cgDarkPurple = new Color(58, 43, 59); Color cgDarkTeal = new Color(45, 74, 84); Color cgTeal = new Color(12, 116, 117);
            Color cgHotPink = new Color(188, 74, 155); Color cgSoftPink = new Color(235, 141, 156); Color cgPeach = new Color(255, 216, 186); Color cgDimmed = new Color(80, 65, 75);
            tc.BaseFg = cgPeach; tc.BaseBg = cgDarkPurple; tc.BaseFocusFg = cgPeach; tc.BaseFocusBg = cgDarkTeal; tc.BaseHotFg = cgHotPink; tc.BaseHotFocusFg = cgSoftPink; tc.BaseDisabledFg = cgDimmed;
            tc.MenuFg = cgDarkPurple; tc.MenuBg = cgTeal; tc.MenuFocusFg = cgDarkPurple; tc.MenuFocusBg = cgSoftPink; tc.MenuHotFg = cgPeach; tc.MenuHotFocusFg = cgPeach; tc.MenuDisabledFg = cgDimmed;
            tc.DlgFg = cgPeach; tc.DlgBg = cgDarkTeal; tc.DlgFocusFg = cgDarkPurple; tc.DlgFocusBg = cgSoftPink; tc.DlgHotFg = cgHotPink; tc.DlgHotFocusFg = cgHotPink; tc.DlgDisabledFg = cgDimmed;
            tc.ErrFg = cgPeach; tc.ErrBg = cgHotPink; tc.ErrFocusFg = cgPeach; tc.ErrHotFg = cgPeach;
            tc.HlFg = cgSoftPink; tc.HlBg = cgDarkPurple; tc.HlFocusFg = cgPeach; tc.HlFocusBg = cgDarkTeal; tc.HlHotFg = cgHotPink; tc.HlHotFocusFg = cgHotPink;
            tc.InputFg = cgDarkPurple; tc.InputBg = cgPeach; tc.InputFocusFg = cgDarkPurple; tc.InputFocusBg = cgSoftPink;
            themes["Cybergum"] = tc;

            // Everforest - palette Everforest dark medium
            tc = new ThemeColors();
            Color efBg0 = new Color(0x2D, 0x35, 0x3B); Color efBg1 = new Color(0x34, 0x3F, 0x44); Color efBg2 = new Color(0x3D, 0x48, 0x4D);
            Color efFg = new Color(0xD3, 0xC6, 0xAA); Color efGreen = new Color(0xA7, 0xC0, 0x80); Color efAqua = new Color(0x83, 0xC0, 0x92);
            Color efYellow = new Color(0xDB, 0xBC, 0x7F); Color efRed = new Color(0xE6, 0x7E, 0x80); Color efGrey0 = new Color(0x7A, 0x84, 0x78);
            tc.BaseFg = efFg; tc.BaseBg = efBg0; tc.BaseFocusFg = new Color(0xEC, 0xEF, 0xF4); tc.BaseFocusBg = efBg2; tc.BaseHotFg = efGreen; tc.BaseHotFocusFg = efAqua; tc.BaseDisabledFg = efGrey0;
            tc.MenuFg = efFg; tc.MenuBg = efBg1; tc.MenuFocusFg = efBg0; tc.MenuFocusBg = efAqua; tc.MenuHotFg = efGreen; tc.MenuHotFocusFg = efGreen; tc.MenuDisabledFg = efGrey0;
            tc.DlgFg = efFg; tc.DlgBg = efBg1; tc.DlgFocusFg = efBg0; tc.DlgFocusBg = efAqua; tc.DlgHotFg = efYellow; tc.DlgHotFocusFg = efYellow; tc.DlgDisabledFg = efGrey0;
            tc.ErrFg = efFg; tc.ErrBg = efRed; tc.ErrFocusFg = efYellow; tc.ErrHotFg = efYellow;
            tc.HlFg = efGreen; tc.HlBg = efBg0; tc.HlFocusFg = efAqua; tc.HlFocusBg = efBg2; tc.HlHotFg = efYellow; tc.HlHotFocusFg = efYellow;
            tc.InputFg = efBg0; tc.InputBg = efFg; tc.InputFocusFg = efBg0; tc.InputFocusBg = efAqua;
            themes["Everforest"] = tc;

            // DOS Blue - tema retro classico (default)
            tc = new ThemeColors();
            Color dBlue = new Color(0, 0, 168); Color dCyan = new Color(0, 170, 170); Color dBrightCyan = new Color(85, 255, 255);
            Color dWhite = new Color(255, 255, 255); Color dYellow = new Color(255, 255, 85); Color dBlack = new Color(0, 0, 0);
            Color dGray = new Color(170, 170, 170); Color dDarkGray = new Color(85, 85, 85); Color dBrightBlue = new Color(85, 85, 255); Color dRed = new Color(170, 0, 0);
            tc.BaseFg = dBrightCyan; tc.BaseBg = dBlue; tc.BaseFocusFg = dWhite; tc.BaseFocusBg = dBrightBlue; tc.BaseHotFg = dYellow; tc.BaseHotFocusFg = dYellow; tc.BaseDisabledFg = dDarkGray;
            tc.MenuFg = dBlack; tc.MenuBg = dCyan; tc.MenuFocusFg = dBlack; tc.MenuFocusBg = dGray; tc.MenuHotFg = dRed; tc.MenuHotFocusFg = dRed; tc.MenuDisabledFg = dDarkGray;
            tc.DlgFg = dWhite; tc.DlgBg = dDarkGray; tc.DlgFocusFg = dBlack; tc.DlgFocusBg = dCyan; tc.DlgHotFg = dYellow; tc.DlgHotFocusFg = dYellow; tc.DlgDisabledFg = dGray;
            tc.ErrFg = dWhite; tc.ErrBg = dRed; tc.ErrFocusFg = dYellow; tc.ErrHotFg = dYellow;
            tc.HlFg = dYellow; tc.HlBg = dBlue; tc.HlFocusFg = dYellow; tc.HlFocusBg = dBrightBlue; tc.HlHotFg = dBrightCyan; tc.HlHotFocusFg = dBrightCyan;
            tc.InputFg = dBlack; tc.InputBg = dGray; tc.InputFocusFg = dBlack; tc.InputFocusBg = dWhite;
            themes["DOS Blue"] = tc;

            return themes;
        }

        /// <summary>
        /// Apre un FileDialog per selezionare una cartella e scrive il percorso nel TextField
        /// </summary>
        /// <param name="tf">TextField destinazione</param>
        private void BrowseFolder(TextField tf)
        {
            OpenDialog dlg = new OpenDialog()
            {
                Title = "Seleziona cartella",
                OpenMode = OpenMode.Directory
            };

            // Imposta cartella iniziale
            string currentPath = tf.Text;
            if (currentPath.Length > 0 && Directory.Exists(currentPath))
            {
                dlg.Path = currentPath;
            }
            else
            {
                dlg.Path = "/";
            }

            this._app.Run(dlg);

            // Controlla se ha selezionato qualcosa
            if (!dlg.Canceled && dlg.FilePaths.Count > 0)
            {
                tf.Text = dlg.FilePaths[0];
            }

            dlg.Dispose();
        }

        #endregion

        #region Metodi privati - Layout

        /// <summary>
        /// Crea il DataTable per la tabella file
        /// </summary>
        /// <returns>DataTable con le colonne definite</returns>
        private DataTable CreateDataTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Ep", typeof(string));
            dt.Columns.Add("Stato", typeof(string));
            dt.Columns.Add("Audio", typeof(string));
            dt.Columns.Add("Sub", typeof(string));
            dt.Columns.Add("Delay", typeof(string));
            dt.Columns.Add("Stretch", typeof(string));
            dt.Columns.Add("Size", typeof(string));

            return dt;
        }

        /// <summary>
        /// Costruisce il layout principale nella finestra
        /// </summary>
        private void BuildMainLayout()
        {
            MenuBar menuBar = this.BuildMenuBar();
            FrameView tablePanel = this.BuildTablePanel();
            FrameView detailPanel = this.BuildDetailPanel(tablePanel);
            FrameView logPanel = this.BuildLogPanel(tablePanel);
            StatusBar statusBar = this.BuildStatusBar();

            this._mainWindow.Add(menuBar, tablePanel, detailPanel, logPanel, statusBar);
        }

        /// <summary>
        /// Crea la barra menu principale
        /// </summary>
        /// <returns>MenuBar configurata</returns>
        private MenuBar BuildMenuBar()
        {
            MenuBar menuBar = new MenuBar()
            {
                SchemeName = "Menu",
                Menus = new MenuBarItem[]
                {
                    new MenuBarItem("_File", new MenuItem[]
                    {
                        new MenuItem("_Configurazione", "F2", () => this.ShowConfigDialog()),
                        new MenuItem("_Esci", "Ctrl+Q", () => this._app.RequestStop())
                    }),
                    new MenuBarItem("_Azioni", new MenuItem[]
                    {
                        new MenuItem("_Scan file", "F5", () => this.DoScan()),
                        new MenuItem("_Analizza selezionato", "F6", () => this.DoAnalyzeSelected()),
                        new MenuItem("Analizza _tutti", "F7", () => this.DoAnalyzeAll()),
                        new MenuItem("S_kip/Unskip", "F8", () => this.ToggleSkip()),
                        new MenuItem("_Processa selezionato", "F9", () => this.DoProcessSelected()),
                        new MenuItem("Processa t_utti", "F10", () => this.DoProcessAll())
                    }),
                    new MenuBarItem("_Tema", new MenuItem[]
                    {
                        new MenuItem("_Nord", "", () => this.ApplyTheme("Nord")),
                        new MenuItem("DOS _Blue", "", () => this.ApplyTheme("DOS Blue")),
                        new MenuItem("_Matrix", "", () => this.ApplyTheme("Matrix")),
                        new MenuItem("_Cyberpunk", "", () => this.ApplyTheme("Cyberpunk")),
                        new MenuItem("Solarized _Dark", "", () => this.ApplyTheme("Solarized Dark")),
                        new MenuItem("Solarized _Light", "", () => this.ApplyTheme("Solarized Light")),
                        new MenuItem("C_ybergum", "", () => this.ApplyTheme("Cybergum")),
                        new MenuItem("_Everforest", "", () => this.ApplyTheme("Everforest"))
                    }),
                    new MenuBarItem("A_iuto", new MenuItem[]
                    {
                        new MenuItem("_Info", "F1", () => this.ShowAbout())
                    })
                }
            };

            return menuBar;
        }

        /// <summary>
        /// Crea il pannello tabella episodi con colonne e eventi
        /// </summary>
        /// <returns>FrameView contenente la TableView</returns>
        private FrameView BuildTablePanel()
        {
            FrameView tablePanel = new FrameView()
            {
                Title = " Episodi ",
                X = 0,
                Y = 1,
                Width = Dim.Percent(55),
                Height = Dim.Percent(60),
                BorderStyle = LineStyle.Double,
                SchemeName = "Base"
            };

            this._dataTable = this.CreateDataTable();
            this._tableView = new TableView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                FullRowSelect = true,
                SchemeName = "Base"
            };
            this._tableView.Table = new DataTableSource(this._dataTable);

            // Larghezza colonne proporzionata
            this._tableView.Style.GetOrCreateColumnStyle(0).MinWidth = 6;
            this._tableView.Style.GetOrCreateColumnStyle(0).MaxWidth = 20;
            this._tableView.Style.GetOrCreateColumnStyle(1).MinWidth = 8;
            this._tableView.Style.GetOrCreateColumnStyle(1).MaxWidth = 12;
            this._tableView.Style.GetOrCreateColumnStyle(2).MinWidth = 4;
            this._tableView.Style.GetOrCreateColumnStyle(2).MaxWidth = 8;
            this._tableView.Style.GetOrCreateColumnStyle(3).MinWidth = 4;
            this._tableView.Style.GetOrCreateColumnStyle(3).MaxWidth = 8;
            this._tableView.Style.GetOrCreateColumnStyle(4).MinWidth = 6;
            this._tableView.Style.GetOrCreateColumnStyle(4).MaxWidth = 10;
            this._tableView.Style.GetOrCreateColumnStyle(5).MinWidth = 5;
            this._tableView.Style.GetOrCreateColumnStyle(5).MaxWidth = 8;
            this._tableView.Style.GetOrCreateColumnStyle(6).MinWidth = 6;
            this._tableView.Style.GetOrCreateColumnStyle(6).MaxWidth = 10;

            // Enter: aggiorna dettaglio e mostra dialog delay
            this._tableView.CellActivated += (object sender, CellActivatedEventArgs e) =>
            {
                if (e.Row >= 0 && e.Row < this._records.Count)
                {
                    this.UpdateDetail(this._records[e.Row]);
                    this.ShowDelayDialog(this._records[e.Row]);
                }
            };

            // Navigazione: aggiorna pannello dettaglio
            this._tableView.SelectedCellChanged += (object sender, SelectedCellChangedEventArgs e) =>
            {
                if (e.NewRow >= 0 && e.NewRow < this._records.Count)
                {
                    this.UpdateDetail(this._records[e.NewRow]);
                }
            };

            tablePanel.Add(this._tableView);

            return tablePanel;
        }

        /// <summary>
        /// Crea il pannello dettaglio file selezionato
        /// </summary>
        /// <param name="tablePanel">Pannello tabella per posizionamento relativo</param>
        /// <returns>FrameView contenente la TextView dettaglio</returns>
        private FrameView BuildDetailPanel(FrameView tablePanel)
        {
            FrameView detailPanel = new FrameView()
            {
                Title = " Dettaglio ",
                X = Pos.Right(tablePanel),
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Percent(60),
                BorderStyle = LineStyle.Double,
                SchemeName = "Base"
            };

            this._detailView = new TextView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                CanFocus = true,
                WordWrap = true,
                ScrollBars = true,
                SchemeName = "Base"
            };

            detailPanel.Add(this._detailView);

            return detailPanel;
        }

        /// <summary>
        /// Crea il pannello log
        /// </summary>
        /// <param name="tablePanel">Pannello tabella per posizionamento relativo</param>
        /// <returns>FrameView contenente la TextView log</returns>
        private FrameView BuildLogPanel(FrameView tablePanel)
        {
            FrameView logPanel = new FrameView()
            {
                Title = " Log ",
                X = 0,
                Y = Pos.Bottom(tablePanel),
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1,
                BorderStyle = LineStyle.Rounded,
                SchemeName = "Base"
            };

            this._logView = new TextView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                CanFocus = true,
                WordWrap = true,
                ScrollBars = true,
                Text = "",
                SchemeName = "Base"
            };

            logPanel.Add(this._logView);

            return logPanel;
        }

        /// <summary>
        /// Crea la barra di stato con shortcut globali
        /// </summary>
        /// <returns>StatusBar configurata</returns>
        private StatusBar BuildStatusBar()
        {
            StatusBar statusBar = new StatusBar()
            {
                Y = Pos.AnchorEnd(1),
                SchemeName = "Menu"
            };

            // Shortcut: Action viene invocata su Activate (pressione tasto), NON su Accepting (Enter)
            Shortcut scF1 = new Shortcut() { Key = Key.F1, Title = "Help", BindKeyToApplication = true, Action = () => this.ShowAbout() };
            Shortcut scF2 = new Shortcut() { Key = Key.F2, Title = "Config", BindKeyToApplication = true, Action = () => this.ShowConfigDialog() };
            Shortcut scF5 = new Shortcut() { Key = Key.F5, Title = "Scan", BindKeyToApplication = true, Action = () => this.DoScan() };
            Shortcut scF6 = new Shortcut() { Key = Key.F6, Title = "Analizza", BindKeyToApplication = true, Action = () => this.DoAnalyzeSelected() };
            Shortcut scF7 = new Shortcut() { Key = Key.F7, Title = "Tutti", BindKeyToApplication = true, Action = () => this.DoAnalyzeAll() };
            Shortcut scF8 = new Shortcut() { Key = Key.F8, Title = "Skip", BindKeyToApplication = true, Action = () => this.ToggleSkip() };
            Shortcut scF9 = new Shortcut() { Key = Key.F9, Title = "Processa", BindKeyToApplication = true, Action = () => this.DoProcessSelected() };
            Shortcut scF10 = new Shortcut() { Key = Key.F10, Title = "Tutti", BindKeyToApplication = true, Action = () => this.DoProcessAll() };
            Shortcut scCtrlQ = new Shortcut() { Key = Key.Q.WithCtrl, Title = "Esci", BindKeyToApplication = true, Action = () => this._app.RequestStop() };

            statusBar.Add(scF1, scF2, scF5, scF6, scF7, scF8, scF9, scF10, scCtrlQ);

            return statusBar;
        }

        #endregion

        #region Metodi privati - Aggiornamento UI

        /// <summary>
        /// Aggiorna la tabella con i dati correnti dei record
        /// </summary>
        private void UpdateTable()
        {
            // Salva riga selezionata
            int selectedRow = this._tableView.SelectedRow;

            this._dataTable.Rows.Clear();

            for (int i = 0; i < this._records.Count; i++)
            {
                FileProcessingRecord r = this._records[i];
                string stato = this.GetStatusText(r.Status);
                string audio = FileProcessingRecord.FormatLangs(r.LangAudioLangs);
                string sub = FileProcessingRecord.FormatLangs(r.LangSubLangs);
                string delay = this.FormatDelayShort(r.AudioDelayApplied);
                string stretch = r.StretchFactor.Length > 0 ? r.StretchFactor : "-";
                string size = FileProcessingRecord.FormatSize(r.SourceSize);

                this._dataTable.Rows.Add(r.EpisodeId, stato, audio, sub, delay, stretch, size);
            }

            this._tableView.Table = new DataTableSource(this._dataTable);

            // Ripristina riga selezionata
            if (selectedRow >= 0 && selectedRow < this._records.Count)
            {
                this._tableView.SelectedRow = selectedRow;
            }

            this._tableView.SetNeedsDraw();
        }

        /// <summary>
        /// Aggiorna il pannello dettaglio per il record specificato
        /// </summary>
        /// <param name="record">Record da visualizzare</param>
        private void UpdateDetail(FileProcessingRecord record)
        {
            StringBuilder sb = new StringBuilder(512);
            string srcAudio = "";
            string srcSub = "";
            string impAudioStr = "";
            string impSubStr = "";
            List<string> importAudio = new List<string>();
            List<string> importSub = new List<string>();

            // Intestazione con stato
            sb.Append("--- ").Append(record.EpisodeId).Append(" [").Append(this.GetStatusText(record.Status)).Append("] ---\n\n");

            // File coinvolti
            sb.Append("FILE SORGENTE\n");
            sb.Append("  ").Append(record.SourceFileName).Append('\n');
            sb.Append("  Dimensione: ").Append(FileProcessingRecord.FormatSize(record.SourceSize)).Append('\n');
            sb.Append('\n');
            sb.Append("FILE LINGUA\n");
            sb.Append("  ").Append(record.LangFileName.Length > 0 ? record.LangFileName : "(nessuno)").Append('\n');
            if (record.LangSize > 0)
            {
                sb.Append("  Dimensione: ").Append(FileProcessingRecord.FormatSize(record.LangSize)).Append('\n');
            }

            // Tracce sorgente
            sb.Append("\nTRACCE SORGENTE\n");
            srcAudio = FileProcessingRecord.FormatLangs(record.SourceAudioLangs);
            srcSub = FileProcessingRecord.FormatLangs(record.SourceSubLangs);
            sb.Append("  Audio: ").Append(srcAudio.Length > 0 ? srcAudio : "nessuna").Append('\n');
            sb.Append("  Sub:   ").Append(srcSub.Length > 0 ? srcSub : "nessuno").Append('\n');

            // Tracce da tenere (filtri attivi)
            if (this._opts.KeepSourceAudioLangs.Count > 0 || this._opts.KeepSourceSubtitleLangs.Count > 0)
            {
                sb.Append("\nTRACCE DA TENERE\n");
                if (this._opts.KeepSourceAudioLangs.Count > 0)
                {
                    sb.Append("  Audio: ").Append(string.Join(",", this._opts.KeepSourceAudioLangs)).Append('\n');
                }
                if (this._opts.KeepSourceSubtitleLangs.Count > 0)
                {
                    sb.Append("  Sub:   ").Append(string.Join(",", this._opts.KeepSourceSubtitleLangs)).Append('\n');
                }
            }

            // Tracce da importare (filtrate per target language)
            sb.Append("\nTRACCE DA IMPORTARE\n");
            for (int i = 0; i < this._opts.TargetLanguage.Count; i++)
            {
                string tl = this._opts.TargetLanguage[i];
                if (!this._opts.SubOnly && record.LangAudioLangs.Contains(tl) && !importAudio.Contains(tl))
                {
                    importAudio.Add(tl);
                }
                if (!this._opts.AudioOnly && record.LangSubLangs.Contains(tl) && !importSub.Contains(tl))
                {
                    importSub.Add(tl);
                }
            }
            impAudioStr = FileProcessingRecord.FormatLangs(importAudio);
            impSubStr = FileProcessingRecord.FormatLangs(importSub);
            sb.Append("  Audio: ").Append(impAudioStr.Length > 0 ? impAudioStr : "nessuna").Append('\n');
            sb.Append("  Sub:   ").Append(impSubStr.Length > 0 ? impSubStr : "nessuno").Append('\n');

            // Sincronizzazione
            sb.Append("\nSINCRONIZZAZIONE\n");
            sb.Append("  Offset audio calcolato: ").Append(this.FormatDelayFull(record)).Append('\n');
            sb.Append("  Offset sottotitoli:     ").Append(this.FormatSubDelayFull(record)).Append('\n');
            if (record.StretchFactor.Length > 0)
            {
                sb.Append("  Fattore stretch:        ").Append(record.StretchFactor).Append('\n');
            }
            if (record.SpeedCorrectionApplied)
            {
                sb.Append("  Correzione velocita':   applicata\n");
            }

            // Errore o skip
            if (record.ErrorMessage.Length > 0)
            {
                sb.Append("\nERRORE\n");
                sb.Append("  ").Append(record.ErrorMessage).Append('\n');
            }
            if (record.SkipReason.Length > 0)
            {
                sb.Append("\nSALTATO\n");
                sb.Append("  ").Append(record.SkipReason).Append('\n');
            }

            // Tempi di elaborazione
            if (record.SpeedCorrectionTimeMs > 0 || record.FrameSyncTimeMs > 0 || record.MergeTimeMs > 0)
            {
                sb.Append("\nTEMPI ELABORAZIONE\n");
                if (record.SpeedCorrectionTimeMs > 0)
                {
                    sb.Append("  Correzione velocita': ").Append(record.SpeedCorrectionTimeMs).Append(" ms\n");
                }
                if (record.FrameSyncTimeMs > 0)
                {
                    sb.Append("  Frame-sync video:     ").Append(record.FrameSyncTimeMs).Append(" ms\n");
                }
                if (record.MergeTimeMs > 0)
                {
                    sb.Append("  Merge finale:         ").Append(record.MergeTimeMs).Append(" ms\n");
                }
            }

            // Risultato
            if (record.ResultSize > 0)
            {
                sb.Append("\nRISULTATO\n");
                sb.Append("  Dimensione: ").Append(FileProcessingRecord.FormatSize(record.ResultSize)).Append('\n');
            }

            // Comando mkvmerge risultante
            if (record.MergeCommand.Length > 0)
            {
                sb.Append("\nCOMANDO\n");
                sb.Append("  ").Append(record.MergeCommand).Append('\n');
            }

            this._detailView.Text = sb.ToString();
            this._detailView.SetNeedsDraw();
        }

        /// <summary>
        /// Aggiunge un messaggio al pannello log
        /// </summary>
        /// <param name="message">Messaggio da aggiungere</param>
        private void AppendLog(string message)
        {
            // Accoda il messaggio con newline
            string current = this._logView.Text;
            if (current.Length > 0)
            {
                this._logView.Text = current + "\n" + message;
            }
            else
            {
                this._logView.Text = message;
            }

            // Scroll alla fine
            this._logView.MoveEnd();
            this._logView.SetNeedsDraw();
        }

        /// <summary>
        /// Converte lo stato file in testo visualizzabile
        /// </summary>
        /// <param name="status">Stato file</param>
        /// <returns>Testo stato</returns>
        private string GetStatusText(FileStatus status)
        {
            string result = "";

            if (status == FileStatus.Pending) result = "In attesa";
            else if (status == FileStatus.Analyzing) result = "Analisi...";
            else if (status == FileStatus.Analyzed) result = "Pronto";
            else if (status == FileStatus.Processing) result = "Merge...";
            else if (status == FileStatus.Done) result = "Completato";
            else if (status == FileStatus.Error) result = "Errore";
            else if (status == FileStatus.Skipped) result = "Saltato";

            return result;
        }

        /// <summary>
        /// Formatta il delay audio in formato breve per la tabella
        /// </summary>
        /// <param name="delayMs">Delay in millisecondi</param>
        /// <returns>Stringa formattata</returns>
        private string FormatDelayShort(int delayMs)
        {
            string result = "-";

            if (delayMs > 0) result = "+" + delayMs + "ms";
            else if (delayMs < 0) result = delayMs + "ms";
            else if (delayMs == 0) result = "0ms";

            return result;
        }

        /// <summary>
        /// Formatta il delay audio completo per il pannello dettaglio
        /// </summary>
        /// <param name="record">Record file</param>
        /// <returns>Stringa dettagliata del delay audio</returns>
        private string FormatDelayFull(FileProcessingRecord record)
        {
            string result = this.FormatDelayShort(record.AudioDelayApplied);

            if (record.ManualAudioDelayMs != 0)
            {
                result += " (sync:" + record.SyncOffsetMs + " man:" + record.ManualAudioDelayMs + ")";
            }

            return result;
        }

        /// <summary>
        /// Formatta il delay sottotitoli completo per il pannello dettaglio
        /// </summary>
        /// <param name="record">Record file</param>
        /// <returns>Stringa dettagliata del delay sottotitoli</returns>
        private string FormatSubDelayFull(FileProcessingRecord record)
        {
            string result = this.FormatDelayShort(record.SubDelayApplied);

            if (record.ManualSubDelayMs != 0)
            {
                result += " (sync:" + record.SyncOffsetMs + " man:" + record.ManualSubDelayMs + ")";
            }

            return result;
        }

        #endregion

        #region Metodi privati - Azioni

        /// <summary>
        /// Esegue la scansione dei file
        /// </summary>
        private void DoScan()
        {
            bool done = false;
            int pending = 0;
            int skipped = 0;

            if (this._isProcessing)
            {
                done = true;
            }

            // Verifica che le opzioni siano configurate
            if (!done && (this._opts.SourceFolder.Length == 0 || this._opts.TargetLanguage.Count == 0))
            {
                this.AppendLog("Configurare prima source e lingua target (F2)");
                done = true;
            }

            // Inizializza pipeline con opzioni correnti
            if (!done && !this._pipeline.Initialize(this._opts))
            {
                this.AppendLog("Errore inizializzazione pipeline");
                done = true;
            }

            if (!done)
            {
                // Scan e ordina per EpisodeId
                this._records = this._pipeline.ScanFiles();
                this._records.Sort((FileProcessingRecord a, FileProcessingRecord b) => string.Compare(a.EpisodeId, b.EpisodeId, StringComparison.OrdinalIgnoreCase));
                this.UpdateTable();

                // Conta file pronti
                for (int i = 0; i < this._records.Count; i++)
                {
                    if (this._records[i].Status == FileStatus.Pending) pending++;
                    else if (this._records[i].Status == FileStatus.Skipped) skipped++;
                }

                this.AppendLog("Scan completato: " + this._records.Count + " file trovati, " + pending + " pronti, " + skipped + " saltati");

                // Seleziona prima riga e mostra dettaglio
                if (this._records.Count > 0)
                {
                    this._tableView.SelectedRow = 0;
                    this.UpdateDetail(this._records[0]);
                }
            }
        }

        /// <summary>
        /// Avvia l'analisi del file selezionato su thread background
        /// </summary>
        private void DoAnalyzeSelected()
        {
            bool done = false;
            int row = this._tableView.SelectedRow;
            FileProcessingRecord record = null;

            if (this._isProcessing)
            {
                done = true;
            }

            if (!done && (row < 0 || row >= this._records.Count))
            {
                this.AppendLog("Nessun file selezionato");
                done = true;
            }

            if (!done)
            {
                record = this._records[row];
                if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
                {
                    this.AppendLog("File non analizzabile (stato: " + this.GetStatusText(record.Status) + ")");
                    done = true;
                }
            }

            if (!done)
            {
                this._isProcessing = true;
                this.AppendLog("Analisi: " + record.EpisodeId + "...");

                Thread workerThread = new Thread(() =>
                {
                    this._pipeline.AnalyzeFile(record);
                    this._app.Invoke(() =>
                    {
                        this._isProcessing = false;
                        this.UpdateTable();
                        this.UpdateDetail(record);
                        string stato = record.Status == FileStatus.Analyzed ? "completata" : "errore";
                        this.AppendLog("Analisi " + record.EpisodeId + ": " + stato);
                    });
                });
                workerThread.IsBackground = true;
                workerThread.Start();
            }
        }

        /// <summary>
        /// Avvia l'analisi di tutti i file pendenti su thread background
        /// </summary>
        private void DoAnalyzeAll()
        {
            bool done = false;

            if (this._isProcessing)
            {
                done = true;
            }

            if (!done && this._records.Count == 0)
            {
                this.AppendLog("Nessun file da analizzare. Eseguire prima Scan (F5)");
                done = true;
            }

            if (!done)
            {
                this._isProcessing = true;
                this.AppendLog("Avvio analisi...");

                Thread workerThread = new Thread(this.AnalyzeWorker);
                workerThread.IsBackground = true;
                workerThread.Start();
            }
        }

        /// <summary>
        /// Worker thread per analisi file
        /// </summary>
        private void AnalyzeWorker()
        {
            int analyzed = 0;
            int errors = 0;

            for (int i = 0; i < this._records.Count; i++)
            {
                FileProcessingRecord record = this._records[i];

                // Solo file pendenti o in errore
                if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
                {
                    continue;
                }

                this._pipeline.AnalyzeFile(record);

                // Aggiorna UI dal thread principale
                int idx = i;
                this._app.Invoke(() =>
                {
                    this.UpdateTable();
                    if (this._tableView.SelectedRow == idx)
                    {
                        this.UpdateDetail(record);
                    }
                });

                if (record.Status == FileStatus.Analyzed) analyzed++;
                else if (record.Status == FileStatus.Error) errors++;
            }

            // Fine analisi
            int totalAnalyzed = analyzed;
            int totalErrors = errors;
            this._app.Invoke(() =>
            {
                this._isProcessing = false;
                this.AppendLog("Analisi completata: " + totalAnalyzed + " analizzati, " + totalErrors + " errori");
            });
        }

        /// <summary>
        /// Avvia il merge del file selezionato su thread background
        /// </summary>
        private void DoProcessSelected()
        {
            bool done = false;
            int row = this._tableView.SelectedRow;
            FileProcessingRecord record = null;

            if (this._isProcessing)
            {
                done = true;
            }

            if (!done && (row < 0 || row >= this._records.Count))
            {
                this.AppendLog("Nessun file selezionato");
                done = true;
            }

            if (!done)
            {
                record = this._records[row];
                if (record.Status != FileStatus.Analyzed)
                {
                    this.AppendLog("File non pronto per il merge (stato: " + this.GetStatusText(record.Status) + ")");
                    done = true;
                }
            }

            if (!done)
            {
                this._isProcessing = true;
                this.AppendLog("Merge: " + record.EpisodeId + "...");

                Thread workerThread = new Thread(() =>
                {
                    this._pipeline.MergeFile(record);
                    this._app.Invoke(() =>
                    {
                        this._isProcessing = false;
                        this.UpdateTable();
                        this.UpdateDetail(record);
                        string stato = record.Status == FileStatus.Done ? "completato" : "errore";
                        this.AppendLog("Merge " + record.EpisodeId + ": " + stato);
                    });
                });
                workerThread.IsBackground = true;
                workerThread.Start();
            }
        }

        /// <summary>
        /// Avvia il merge di tutti i file analizzati su thread background
        /// </summary>
        private void DoProcessAll()
        {
            bool done = false;
            int ready = 0;

            if (this._isProcessing)
            {
                done = true;
            }

            if (!done)
            {
                // Conta file pronti
                for (int i = 0; i < this._records.Count; i++)
                {
                    if (this._records[i].Status == FileStatus.Analyzed) ready++;
                }

                if (ready == 0)
                {
                    this.AppendLog("Nessun file pronto per il merge. Eseguire prima Analizza (F6)");
                    done = true;
                }
            }

            if (!done)
            {
                this._isProcessing = true;
                this.AppendLog("Avvio merge di " + ready + " file...");

                Thread workerThread = new Thread(this.ProcessWorker);
                workerThread.IsBackground = true;
                workerThread.Start();
            }
        }

        /// <summary>
        /// Worker thread per merge file
        /// </summary>
        private void ProcessWorker()
        {
            int processed = 0;
            int errors = 0;

            for (int i = 0; i < this._records.Count; i++)
            {
                FileProcessingRecord record = this._records[i];

                // Solo file analizzati
                if (record.Status != FileStatus.Analyzed)
                {
                    continue;
                }

                this._pipeline.MergeFile(record);

                // Aggiorna UI dal thread principale
                int idx = i;
                this._app.Invoke(() =>
                {
                    this.UpdateTable();
                    if (this._tableView.SelectedRow == idx)
                    {
                        this.UpdateDetail(record);
                    }
                });

                if (record.Status == FileStatus.Done) processed++;
                else if (record.Status == FileStatus.Error) errors++;
            }

            // Fine merge
            int totalProcessed = processed;
            int totalErrors = errors;
            this._app.Invoke(() =>
            {
                this._isProcessing = false;
                this.AppendLog("Merge completato: " + totalProcessed + " elaborati, " + totalErrors + " errori");
            });
        }

        /// <summary>
        /// Toggle skip/unskip per il file selezionato
        /// </summary>
        private void ToggleSkip()
        {
            bool done = false;
            int row = this._tableView.SelectedRow;
            FileProcessingRecord record = null;

            if (this._isProcessing)
            {
                done = true;
            }

            if (!done && (row < 0 || row >= this._records.Count))
            {
                done = true;
            }

            if (!done)
            {
                record = this._records[row];

                if (record.Status == FileStatus.Skipped)
                {
                    // Unskip: riporta a Pending se ha un match
                    if (record.LangFilePath.Length > 0)
                    {
                        record.Status = FileStatus.Pending;
                        record.SkipReason = "";
                    }
                }
                else if (record.Status == FileStatus.Pending || record.Status == FileStatus.Analyzed || record.Status == FileStatus.Error)
                {
                    // Skip
                    record.Status = FileStatus.Skipped;
                    record.SkipReason = "Saltato dall'utente";
                }

                this.UpdateTable();
                this.UpdateDetail(record);
            }
        }

        #endregion

        #region Metodi privati - Dialogs

        /// <summary>
        /// Mostra il dialog di configurazione
        /// </summary>
        private void ShowConfigDialog()
        {
            bool accepted = false;
            int y = 0;
            int audioDelay = 0;
            int subDelay = 0;
            string[] exts = null;
            string trimmed = "";

            if (!this._isProcessing)
            {

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Configurazione ",
                Width = Dim.Fill(4),
                Height = Dim.Fill(2),
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Cartelle ---
            Label lblSection1 = new Label() { Text = "== Cartelle ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblSource = new Label() { Text = "Source:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSource = new TextField() { Text = this._opts.SourceFolder, X = 14, Y = y, Width = Dim.Fill(10), SchemeName = "Input" };
            Button btnBrowseSource = new Button() { Text = "..", X = Pos.Right(tfSource) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseSource.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfSource); e.Handled = true; };
            y++;

            Label lblLang = new Label() { Text = "Lingua:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfLang = new TextField() { Text = this._opts.LanguageFolder, X = 14, Y = y, Width = Dim.Fill(10), SchemeName = "Input" };
            Button btnBrowseLang = new Button() { Text = "..", X = Pos.Right(tfLang) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseLang.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfLang); e.Handled = true; };
            y++;

            Label lblDest = new Label() { Text = "Destinazione:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfDest = new TextField() { Text = this._opts.DestinationFolder, X = 14, Y = y, Width = Dim.Fill(10), SchemeName = "Input" };
            Button btnBrowseDest = new Button() { Text = "..", X = Pos.Right(tfDest) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseDest.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfDest); e.Handled = true; };
            y++;

            bool[] stOverwrite;
            Button cbOverwrite = this.CreateToggleLabel("Sovrascrivi sorgente", this._opts.Overwrite, 1, y, "Dialog", out stOverwrite);
            y++;

            bool[] stRecursive;
            Button cbRecursive = this.CreateToggleLabel("Ricorsivo", this._opts.Recursive, 1, y, "Dialog", out stRecursive);
            y++;

            // --- Lingua e Tracce ---
            Label lblSection2 = new Label() { Text = "== Lingua e Tracce ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblTarget = new Label() { Text = "Lingua target:", X = 1, Y = y, SchemeName = "Dialog" };
            string targetStr = string.Join(",", this._opts.TargetLanguage);
            TextField tfTarget = new TextField() { Text = targetStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblCodec = new Label() { Text = "Codec audio:", X = 1, Y = y, SchemeName = "Dialog" };
            string codecStr = string.Join(",", this._opts.AudioCodec);
            TextField tfCodec = new TextField() { Text = codecStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblKsa = new Label() { Text = "Keep src audio:", X = 1, Y = y, SchemeName = "Dialog" };
            string ksaStr = string.Join(",", this._opts.KeepSourceAudioLangs);
            TextField tfKsa = new TextField() { Text = ksaStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblKsac = new Label() { Text = "Keep src codec:", X = 1, Y = y, SchemeName = "Dialog" };
            string ksacStr = string.Join(",", this._opts.KeepSourceAudioCodec);
            TextField tfKsac = new TextField() { Text = ksacStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblKss = new Label() { Text = "Keep src sub:", X = 1, Y = y, SchemeName = "Dialog" };
            string kssStr = string.Join(",", this._opts.KeepSourceSubtitleLangs);
            TextField tfKss = new TextField() { Text = kssStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            bool[] stSubOnly;
            Button cbSubOnly = this.CreateToggleLabel("Solo sottotitoli", this._opts.SubOnly, 1, y, "Dialog", out stSubOnly);
            bool[] stAudioOnly;
            Button cbAudioOnly = this.CreateToggleLabel("Solo audio", this._opts.AudioOnly, 25, y, "Dialog", out stAudioOnly);
            y++;

            // --- Sincronizzazione ---
            Label lblSection3 = new Label() { Text = "== Sincronizzazione ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            bool[] stFrameSync;
            Button cbFrameSync = this.CreateToggleLabel("Frame-sync (confronto visivo)", this._opts.FrameSync, 1, y, "Dialog", out stFrameSync);
            y++;

            Label lblAudioDelay = new Label() { Text = "Delay audio:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfAudioDelay = new TextField() { Text = this._opts.AudioDelay.ToString(), X = 16, Y = y, Width = 8, SchemeName = "Input" };
            Label lblMs2 = new Label() { Text = "ms", X = 25, Y = y, SchemeName = "Dialog" };
            y++;

            Label lblSubDelay = new Label() { Text = "Delay sub:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSubDelay = new TextField() { Text = this._opts.SubtitleDelay.ToString(), X = 16, Y = y, Width = 8, SchemeName = "Input" };
            Label lblMs3 = new Label() { Text = "ms", X = 25, Y = y, SchemeName = "Dialog" };
            y++;

            // --- Avanzate ---
            Label lblSection4 = new Label() { Text = "== Avanzate ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblPattern = new Label() { Text = "Pattern match:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfPattern = new TextField() { Text = this._opts.MatchPattern, X = 16, Y = y, Width = 30, SchemeName = "Input" };
            y++;

            Label lblExt = new Label() { Text = "Estensioni:", X = 1, Y = y, SchemeName = "Dialog" };
            string extStr = string.Join(",", this._opts.FileExtensions);
            TextField tfExt = new TextField() { Text = extStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblTools = new Label() { Text = "Cartella tools:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfTools = new TextField() { Text = this._opts.ToolsFolder, X = 16, Y = y, Width = Dim.Fill(10), SchemeName = "Input" };
            Button btnBrowseTools = new Button() { Text = "..", X = Pos.Right(tfTools) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseTools.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfTools); e.Handled = true; };
            y++;

            Label lblMkv = new Label() { Text = "Percorso mkv:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMkv = new TextField() { Text = this._opts.MkvMergePath, X = 16, Y = y, Width = Dim.Fill(10), SchemeName = "Input" };
            Button btnBrowseMkv = new Button() { Text = "..", X = Pos.Right(tfMkv) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseMkv.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfMkv); e.Handled = true; };

            dialog.Add(
                lblSection1, lblSource, tfSource, btnBrowseSource, lblLang, tfLang, btnBrowseLang, lblDest, tfDest, btnBrowseDest, cbOverwrite, cbRecursive,
                lblSection2, lblTarget, tfTarget, lblCodec, tfCodec, lblKsa, tfKsa, lblKsac, tfKsac, lblKss, tfKss, cbSubOnly, cbAudioOnly,
                lblSection3, cbFrameSync, lblAudioDelay, tfAudioDelay, lblMs2, lblSubDelay, tfSubDelay, lblMs3,
                lblSection4, lblPattern, tfPattern, lblExt, tfExt, lblTools, tfTools, btnBrowseTools, lblMkv, tfMkv, btnBrowseMkv
            );

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            if (accepted)
            {
                // Salva configurazione
                this._opts.SourceFolder = tfSource.Text;
                this._opts.LanguageFolder = tfLang.Text;
                this._opts.DestinationFolder = tfDest.Text;
                this._opts.Overwrite = stOverwrite[0];
                this._opts.Recursive = stRecursive[0];

                // Parsing campi CSV
                this.ParseCsvToList(tfTarget.Text, this._opts.TargetLanguage);
                this.ParseCsvToList(tfCodec.Text, this._opts.AudioCodec);
                this.ParseCsvToList(tfKsa.Text, this._opts.KeepSourceAudioLangs);
                this.ParseCsvToList(tfKsac.Text, this._opts.KeepSourceAudioCodec);
                this.ParseCsvToList(tfKss.Text, this._opts.KeepSourceSubtitleLangs);

                this._opts.SubOnly = stSubOnly[0];
                this._opts.AudioOnly = stAudioOnly[0];
                this._opts.FrameSync = stFrameSync[0];

                int.TryParse(tfAudioDelay.Text, out audioDelay);
                this._opts.AudioDelay = audioDelay;

                int.TryParse(tfSubDelay.Text, out subDelay);
                this._opts.SubtitleDelay = subDelay;

                this._opts.MatchPattern = tfPattern.Text;

                // Estensioni
                this._opts.FileExtensions.Clear();
                exts = tfExt.Text.Split(',');
                for (int i = 0; i < exts.Length; i++)
                {
                    trimmed = exts[i].Trim().TrimStart('.');
                    if (trimmed.Length > 0)
                    {
                        this._opts.FileExtensions.Add(trimmed);
                    }
                }

                this._opts.ToolsFolder = tfTools.Text;
                this._opts.MkvMergePath = tfMkv.Text;

                this.AppendLog("Configurazione aggiornata");
            }

            } // if (!this._isProcessing)
        }

        /// <summary>
        /// Mostra il dialog per modifica delay per-file
        /// </summary>
        /// <param name="record">Record del file da modificare</param>
        private void ShowDelayDialog(FileProcessingRecord record)
        {
            bool accepted = false;
            int audioVal = 0;
            int subVal = 0;

            if (!this._isProcessing)
            {

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Delay: " + record.EpisodeId + " ",
                Width = 50,
                Height = 14,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            Label lblInfo = new Label() { Text = "Sync offset auto: " + record.SyncOffsetMs + "ms", X = 1, Y = 0, SchemeName = "Dialog" };

            Label lblAudio = new Label() { Text = "Delay audio (ms):", X = 1, Y = 2, SchemeName = "Dialog" };
            TextField tfAudio = new TextField() { Text = record.ManualAudioDelayMs.ToString(), X = 20, Y = 2, Width = 10, SchemeName = "Dialog" };

            Label lblSub = new Label() { Text = "Delay sub (ms):", X = 1, Y = 4, SchemeName = "Dialog" };
            TextField tfSub = new TextField() { Text = record.ManualSubDelayMs.ToString(), X = 20, Y = 4, Width = 10, SchemeName = "Dialog" };

            Label lblPreview = new Label() { Text = "Delay effettivo audio: " + record.AudioDelayApplied + "ms, sub: " + record.SubDelayApplied + "ms", X = 1, Y = 6, SchemeName = "Dialog" };

            dialog.Add(lblInfo, lblAudio, tfAudio, lblSub, tfSub, lblPreview);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            if (accepted)
            {
                // Salva delay
                int.TryParse(tfAudio.Text, out audioVal);
                record.ManualAudioDelayMs = audioVal;

                int.TryParse(tfSub.Text, out subVal);
                record.ManualSubDelayMs = subVal;

                // Ricalcola delay effettivi
                this._pipeline.RecalculateDelays(record);

                this.UpdateTable();
                this.UpdateDetail(record);
            }

            } // if (!this._isProcessing)
        }

        /// <summary>
        /// Mostra il dialog informativo con help dettagliato
        /// </summary>
        private void ShowAbout()
        {
            // Crea dialog scrollabile
            Dialog helpDialog = new Dialog()
            {
                Title = " Help - MergeLanguageTracks ",
                Width = Dim.Fill(4),
                Height = Dim.Fill(2),
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };

            Button btnClose = new Button() { Text = "Chiudi", IsDefault = true, SchemeName = "Dialog" };
            btnClose.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };
            helpDialog.AddButton(btnClose);

            TextView helpView = new TextView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                CanFocus = true,
                WordWrap = true,
                ScrollBars = true,
                Text = HELP_TEXT,
                SchemeName = "Dialog"
            };

            helpDialog.Add(helpView);

            this._app.Run(helpDialog);
            helpDialog.Dispose();
        }

        #endregion
    }
}
