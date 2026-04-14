using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RemuxForge.Core;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RemuxForge.Cli
{
    /// <summary>
    /// Applicazione TUI interattiva per RemuxForge
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

        /// <summary>
        /// DropDownList con colori personalizzati per la lista del popover
        /// Accede alla ListView interna via reflection per impostare lo schema
        /// </summary>
        private class StyledDropDownList : DropDownList
        {
            /// <summary>
            /// Campo reflection per accedere al popover privato di DropDownList
            /// </summary>
            private static readonly System.Reflection.FieldInfo s_popoverField =
                typeof(DropDownList).GetField("_listPopover", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            /// <summary>
            /// Imposta i colori per le voci della lista dropdown
            /// </summary>
            /// <param name="itemsAttr">Attributo per voci non selezionate</param>
            /// <param name="selectedAttr">Attributo per voce selezionata/evidenziata</param>
            public void SetListColors(Terminal.Gui.Drawing.Attribute itemsAttr, Terminal.Gui.Drawing.Attribute selectedAttr)
            {
                // Accede al campo privato _listPopover via reflection
                if (s_popoverField == null)
                {
                    return;
                }

                object popover = s_popoverField.GetValue(this);
                if (popover == null)
                {
                    return;
                }

                // Ottiene la ContentView (ListView) dal popover
                System.Reflection.PropertyInfo contentViewProp = popover.GetType().GetProperty("ContentView");
                if (contentViewProp == null)
                {
                    return;
                }

                View listView = (View)contentViewProp.GetValue(popover);
                if (listView == null)
                {
                    return;
                }

                // Schema per la lista: Normal = voci normali, Focus = voce selezionata
                Scheme listScheme = new Scheme()
                {
                    Normal = itemsAttr,
                    Focus = selectedAttr,
                    HotNormal = itemsAttr,
                    HotFocus = selectedAttr,
                    Active = selectedAttr,
                    Editable = itemsAttr,
                    ReadOnly = itemsAttr
                };
                listView.SetScheme(listScheme);
            }
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
            "Sorgente: cartella contenente i file MKV originali.\n" +
            "Lingua: cartella contenente i file MKV nella lingua alternativa da importare. I file vengono abbinati per episodio tramite il pattern regex.\n" +
            "Destinazione: cartella output per i file mergiati. Se vuota, i file vengono creati accanto ai sorgenti.\n" +
            "Sovrascrivi sorgente: se attivo, il file risultante sostituisce il sorgente originale.\n" +
            "Ricorsivo: cerca file anche nelle sottocartelle.\n" +
            "\n-- Lingua e Tracce --\n" +
            "Lingua target: codici ISO 639-2 delle lingue da importare dal file lingua, separati da virgola. Esempio: ita oppure ita,eng\n" +
            "Codec audio: importa solo tracce audio con questi codec dal file lingua. Vuoto = tutti i codec. Esempio: aac oppure aac,ac3\n" +
            "Mantieni audio: lingue delle tracce audio da MANTENERE nel file sorgente. Le altre vengono rimosse. Vuoto = mantiene tutte. Esempio: jpn oppure jpn,eng\n" +
            "Mantieni codec: mantiene solo le tracce audio sorgente con questi codec. Vuoto = tutti.\n" +
            "Mantieni sub: lingue dei sottotitoli da mantenere nel file sorgente. Vuoto = mantiene tutti.\n" +
            "Solo sottotitoli: importa solo sottotitoli dal file lingua, ignora le tracce audio.\n" +
            "Solo audio: importa solo tracce audio dal file lingua, ignora i sottotitoli.\n" +
            "\n-- Sincronizzazione --\n" +
            "Frame-sync: sincronizzazione tramite confronto visivo dei frame. Trova il delay iniziale confrontando i tagli scena nei primi minuti, poi verifica in 9 punti lungo il video.\n" +
            "Deep analysis: analisi completa per file con montaggio diverso (scene aggiunte o rimosse). Mutuamente esclusiva con Frame-sync.\n" +
            "Delay audio: offset manuale in ms da sommare al risultato della sincronizzazione per le tracce audio importate. Valori negativi anticipano, positivi ritardano.\n" +
            "Delay sub: offset manuale in ms per i sottotitoli. Indipendente dal delay audio.\n" +
            "\n-- Matching --\n" +
            "Pattern match: regex per abbinare episodi tra le due cartelle. I gruppi catturati vengono usati come identificativo episodio. Default: S(\\d+)E(\\d+) (formato SxxExx)\n" +
            "Estensioni: estensioni file da cercare, senza punto. Default: mkv\n" +
            "\n-- Post-Processing --\n" +
            "Converti audio: formato di conversione per tracce audio lossless. Valori: flac, opus. Vuoto = nessuna conversione.\n" +
            "Rinomina tutte le tracce audio: rinomina le tracce audio nel file risultante, non solo quelle convertite.\n" +
            "Encoding video: profilo encoding video post-merge. I profili sono gestibili dal menu Impostazioni > Profili encoding.\n" +
            "\n=== MENU IMPOSTAZIONI ===\n" +
            "Percorsi tool: percorsi di mkvmerge, ffmpeg, mediainfo e cartella file temporanei. I tool vengono cercati automaticamente all'avvio. ffmpeg puo' essere scaricato direttamente dall'interfaccia.\n" +
            "Conversione audio: impostazioni compressione FLAC e bitrate Opus per layout canali.\n" +
            "Profili encoding: gestione profili di encoding video (aggiungi, modifica, elimina).\n" +
            "Avanzate: soglie e parametri di sincronizzazione. Reset defaults disponibile per ciascuna sezione.\n" +
            "\n=== SINCRONIZZAZIONE ===\n" +
            "RemuxForge offre tre sistemi di sincronizzazione automatica, tutti basati sull'analisi visiva dei frame video tramite ffmpeg.\n" +
            "\n" +
            "Correzione velocita' (automatica):\n" +
            "Compensa differenze di velocita' tra release PAL (25 fps) e NTSC (23.976 fps), comuni con serie TV e film europei. La correzione viene applicata alle tracce importate senza ricodifica. Se i due file hanno la stessa velocita', non interviene. Sempre attiva, non richiede parametri.\n" +
            "\n" +
            "Frame-sync (checkbox in configurazione):\n" +
            "Calcola un delay fisso per riallineare le tracce quando sorgente e lingua hanno un taglio iniziale diverso (intro piu' lunga, secondi di nero, crediti differenti all'inizio). Confronta i tagli scena nei primi minuti dei file, seleziona il delay migliore e lo verifica in 9 punti distribuiti nel video. Non funziona se le differenze sono a meta' episodio.\n" +
            "\n" +
            "Deep analysis (checkbox in configurazione):\n" +
            "Sincronizzazione avanzata per file con montaggio diverso: scene aggiunte, rimosse o sostituite. Analizza l'intera traccia video e genera operazioni di taglia-cuci sulle tracce audio e sottotitoli. L'analisi puo' richiedere diversi minuti. I codec senza encoder ffmpeg (TrueHD, DTS-HD MA, DTS:X) non possono essere tagliati e vengono importati con il solo delay iniziale. Mutuamente esclusiva con Frame-sync.\n" +
            "\n" +
            "Il delay manuale (globale o per-episodio) viene sommato all'offset calcolato automaticamente.\n" +
            "\n=== CONVERSIONE AUDIO ===\n" +
            "Converte le tracce audio lossless in FLAC o Opus durante il merge tramite ffmpeg.\n" +
            "Attivabile dal campo 'Converti audio' in configurazione.\n" +
            "\n" +
            "Codec convertibili: DTS-HD Master Audio, DTS-HD High Resolution, TrueHD, PCM, ALAC, MLP, FLAC.\n" +
            "Esclusi: TrueHD Atmos e DTS:X perche' contengono informazioni spaziali.\n" +
            "La conversione si applica sia alle tracce sorgente mantenute (KSA/KSAC) sia a quelle importate.\n" +
            "Se il formato target e' FLAC e la traccia e' gia' FLAC, la conversione viene saltata.\n" +
            "\n" +
            "Impostazioni configurabili dal menu Impostazioni > Conversione audio.\n" +
            "Default FLAC: compression level 8 (range 0-12).\n" +
            "Default Opus: Mono 128, Stereo 256, 5.1 510, 7.1 768 kbps (range 64-768).\n" +
            "\n=== ENCODING VIDEO ===\n" +
            "Dopo il merge e' possibile ricodificare il video con un profilo personalizzato.\n" +
            "Codec supportati: libx264, libx265, libsvtav1.\n" +
            "Rate control: CRF (qualita' costante), QP, Bitrate (1 o 2 passaggi).\n" +
            "I profili sono gestibili dal menu Impostazioni > Profili encoding.\n" +
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
        private volatile bool _isProcessing;

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

        /// <summary>
        /// Schema colori per dropdown non focused (come input normal)
        /// </summary>
        private Scheme _schemeDropdown;

        /// <summary>
        /// Schema colori per dropdown focused (colori invertiti)
        /// </summary>
        private Scheme _schemeDropdownFocus;

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
            this._currentTheme = AppSettingsService.Instance.Settings.Ui.Theme;

            // Auto-find tool all'avvio
            this.AutoFindTools();
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

                // Applica tema grafico salvato
                this.ApplyTheme(this._currentTheme);

                // Crea finestra principale
                this._mainWindow = new Window()
                {
                    Title = " RemuxForge v" + Utils.GetVersion() + " ",
                    BorderStyle = LineStyle.Double,
                    SchemeName = "Base"
                };

                // Collega eventi pipeline
                this._pipeline.OnLogMessage += (LogSection section, LogLevel level, string text) =>
                {
                    // Formatta testo con prefisso sezione
                    string prefix = ConsoleHelper.FormatSectionPrefix(section);
                    string formatted = prefix.Length > 0 ? prefix + text : text;
                    this._app.Invoke(() =>
                    {
                        this.AppendLog(formatted);
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

            // Fallback a dos-blue se tema non trovato
            if (!s_themes.ContainsKey(themeName))
            {
                themeName = "dos-blue";
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

            // Schema dropdown non focused: stessi colori delle textbox
            this._schemeDropdown = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.DlgDisabledFg, tc.InputBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.InputFg, tc.InputBg)
            };

            // Schema dropdown focused: colori invertiti
            this._schemeDropdownFocus = new Scheme()
            {
                Normal = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                Focus = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                HotNormal = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                HotFocus = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                Disabled = new Terminal.Gui.Drawing.Attribute(tc.DlgDisabledFg, tc.InputBg),
                Active = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                Editable = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg),
                ReadOnly = new Terminal.Gui.Drawing.Attribute(tc.InputBg, tc.InputFg)
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
            schemes["Dropdown"] = this._schemeDropdown;
            schemes["DropdownFocus"] = this._schemeDropdownFocus;

            // Salva tema corrente e persiste in appsettings
            this._currentTheme = themeName;
            AppSettingsService.Instance.Settings.Ui.Theme = themeName;
            AppSettingsService.Instance.Save();

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
            themes["nord"] = tc;

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
            themes["matrix"] = tc;

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
            themes["cyberpunk"] = tc;

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
            themes["solarized-dark"] = tc;

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
            themes["solarized-light"] = tc;

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
            themes["cybergum"] = tc;

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
            themes["everforest"] = tc;

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
            themes["dos-blue"] = tc;

            return themes;
        }

        /// <summary>
        /// Configura i colori della lista dropdown (popover) su una StyledDropDownList
        /// Voci non selezionate: stessi colori delle textbox
        /// Voce selezionata: colori invertiti
        /// </summary>
        /// <param name="dd">StyledDropDownList a cui impostare i colori</param>
        private void SetupDropdownFocusColors(StyledDropDownList dd)
        {
            // Schema trigger: stessi colori delle textbox
            dd.SetScheme(this._schemeInput);

            // Colori lista popover: non selezionate = InputFg/InputBg, selezionata = invertiti
            dd.SetListColors(this._schemeDropdown.Normal, this._schemeDropdownFocus.Normal);
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

        /// <summary>
        /// Apre il dialog per selezionare un file e lo assegna al TextField
        /// </summary>
        /// <param name="tf">TextField destinazione</param>
        private void BrowseFile(TextField tf)
        {
            OpenDialog dlg = new OpenDialog()
            {
                Title = "Seleziona file",
                OpenMode = OpenMode.File
            };

            // Imposta cartella iniziale dal percorso corrente nel campo
            string currentPath = tf.Text;
            if (currentPath.Length > 0)
            {
                string dir = Path.GetDirectoryName(currentPath);
                if (dir != null && dir.Length > 0 && Directory.Exists(dir))
                {
                    dlg.Path = dir;
                }
                else
                {
                    dlg.Path = "/";
                }
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
            dt.Columns.Add("Deep", typeof(string));
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
                    new MenuBarItem("_Impostazioni", new MenuItem[]
                    {
                        new MenuItem("_Percorsi tool", "", () => this.ShowToolPathsDialog()),
                        new MenuItem("_Conversione audio", "", () => this.ShowAudioSettingsDialog()),
                        new MenuItem("P_rofili encoding", "", () => this.ShowEncodingProfilesDialog()),
                        new MenuItem("_Avanzate", "", () => this.ShowAdvancedSettingsDialog())
                    }),
                    new MenuBarItem("_Vista", new MenuItem[]
                    {
                        new MenuItem("_Pipeline...", "", () => this.ShowPipelineDialog())
                    }),
                    new MenuBarItem("_Tema", new MenuItem[]
                    {
                        new MenuItem("_Nord", "", () => this.ApplyTheme("nord")),
                        new MenuItem("DOS _Blue", "", () => this.ApplyTheme("dos-blue")),
                        new MenuItem("_Matrix", "", () => this.ApplyTheme("matrix")),
                        new MenuItem("_Cyberpunk", "", () => this.ApplyTheme("cyberpunk")),
                        new MenuItem("Solarized _Dark", "", () => this.ApplyTheme("solarized-dark")),
                        new MenuItem("Solarized _Light", "", () => this.ApplyTheme("solarized-light")),
                        new MenuItem("C_ybergum", "", () => this.ApplyTheme("cybergum")),
                        new MenuItem("_Everforest", "", () => this.ApplyTheme("everforest"))
                    }),
                    new MenuBarItem("A_iuto", new MenuItem[]
                    {
                        new MenuItem("_Guida", "F1", () => this.ShowHelp()),
                        new MenuItem("_Info", "", () => this.ShowInfo())
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
            this._tableView.Style.GetOrCreateColumnStyle(6).MinWidth = 4;
            this._tableView.Style.GetOrCreateColumnStyle(6).MaxWidth = 8;
            this._tableView.Style.GetOrCreateColumnStyle(7).MinWidth = 6;
            this._tableView.Style.GetOrCreateColumnStyle(7).MaxWidth = 10;

            // Enter: mostra context menu
            this._tableView.CellActivated += (object sender, CellActivatedEventArgs e) =>
            {
                if (e.Row >= 0 && e.Row < this._records.Count)
                {
                    this.UpdateDetail(this._records[e.Row]);
                    this.ShowEpisodeContextMenu(this._records[e.Row]);
                }
            };

            // Right-click: mostra context menu
            this._tableView.MouseEvent += (object sender, Mouse e) =>
            {
                if (e.Flags.HasFlag(MouseFlags.RightButtonClicked))
                {
                    // Converte coordinate mouse in cella tabella
                    int mouseX = e.Position.HasValue ? e.Position.Value.X : 0;
                    int mouseY = e.Position.HasValue ? e.Position.Value.Y : 0;
                    System.Drawing.Point? cell = this._tableView.ScreenToCell(mouseX, mouseY);
                    if (cell.HasValue && cell.Value.Y >= 0 && cell.Value.Y < this._records.Count)
                    {
                        this._tableView.SelectedRow = cell.Value.Y;
                        this.UpdateDetail(this._records[cell.Value.Y]);
                        this.ShowEpisodeContextMenu(this._records[cell.Value.Y]);
                    }
                    e.Handled = true;
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
            Shortcut scF1 = new Shortcut() { Key = Key.F1, Title = "Aiuto", BindKeyToApplication = true, Action = () => this.ShowHelp() };
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
                string audio = Utils.FormatLangs(r.ResultAudioLangs);
                string sub = Utils.FormatLangs(r.ResultSubLangs);
                string delay = Utils.FormatDelay(r.AudioDelayApplied);
                string stretch = r.StretchFactor.Length > 0 ? r.StretchFactor : "-";
                string deep = r.DeepAnalysisApplied && r.DeepAnalysisMap != null ? r.DeepAnalysisMap.Operations.Count + " ops" : "-";
                string size = Utils.FormatSize(r.SourceSize);

                this._dataTable.Rows.Add(r.EpisodeId, stato, audio, sub, delay, stretch, deep, size);
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

            // Intestazione con stato
            sb.Append("--- ").Append(record.EpisodeId).Append(" [").Append(this.GetStatusText(record.Status)).Append("] ---\n\n");

            // File coinvolti
            sb.Append("FILE SORGENTE\n");
            sb.Append("  ").Append(record.SourceFileName).Append('\n');
            sb.Append("  Dimensione: ").Append(Utils.FormatSize(record.SourceSize)).Append('\n');
            sb.Append('\n');
            sb.Append("FILE LINGUA\n");
            sb.Append("  ").Append(record.LangFileName.Length > 0 ? record.LangFileName : "(nessuno)").Append('\n');
            if (record.LangSize > 0)
            {
                sb.Append("  Dimensione: ").Append(Utils.FormatSize(record.LangSize)).Append('\n');
            }

            // Tracce sorgente (tutte)
            sb.Append("\nTRACCE SORGENTE\n");
            sb.Append("  Audio: ").Append(Utils.FormatTrackList(record.SourceAudioTracks)).Append('\n');
            sb.Append("  Sub:   ").Append(Utils.FormatTrackList(record.SourceSubTracks)).Append('\n');

            // Tracce sorgente da tenere (solo se filtri attivi)
            if (record.KeptSourceAudioIds.Count > 0 || record.KeptSourceSubIds.Count > 0)
            {
                sb.Append("\nTRACCE SORGENTE DA TENERE\n");
                sb.Append("  Audio: ").Append(Utils.FormatTrackListByIds(record.SourceAudioTracks, record.KeptSourceAudioIds)).Append('\n');
                sb.Append("  Sub:   ").Append(Utils.FormatTrackListByIds(record.SourceSubTracks, record.KeptSourceSubIds)).Append('\n');
            }

            // Tracce da importare dal file lingua
            if (record.ImportedAudioTracks.Count > 0 || record.ImportedSubTracks.Count > 0)
            {
                sb.Append("\nTRACCE DA IMPORTARE\n");
                sb.Append("  Audio: ").Append(Utils.FormatImportedTrackList(record.ImportedAudioTracks, record.DisplayConvertFormat)).Append('\n');
                sb.Append("  Sub:   ").Append(Utils.FormatTrackList(record.ImportedSubTracks)).Append('\n');
            }

            // Risultato finale
            bool filterAudio = (record.KeptSourceAudioIds.Count > 0);
            bool filterSub = (record.KeptSourceSubIds.Count > 0);
            if (record.ImportedAudioTracks.Count > 0 || record.ImportedSubTracks.Count > 0 || filterAudio || filterSub)
            {
                sb.Append("\nRISULTATO FINALE\n");
                sb.Append("  Audio: ").Append(Utils.FormatResultTrackList(record.SourceAudioTracks, record.KeptSourceAudioIds, record.ImportedAudioTracks, record.DisplayConvertFormat, filterAudio)).Append('\n');
                sb.Append("  Sub:   ").Append(Utils.FormatResultTrackList(record.SourceSubTracks, record.KeptSourceSubIds, record.ImportedSubTracks, "", filterSub)).Append('\n');
            }

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
            if (record.DeepAnalysisApplied && record.DeepAnalysisMap != null)
            {
                sb.Append("  Deep analysis:          applicata\n");
                sb.Append("  Operazioni edit:        ").Append(record.DeepAnalysisMap.Operations.Count).Append('\n');
                for (int i = 0; i < record.DeepAnalysisMap.Operations.Count; i++)
                {
                    EditOperation op = record.DeepAnalysisMap.Operations[i];
                    sb.Append("    ").Append(i + 1).Append(". ");
                    sb.Append(op.Type).Append(" @ lang ");
                    sb.Append((op.LangTimestampMs / 1000.0).ToString("F1")).Append("s, ");
                    sb.Append("durata ").Append(op.DurationMs).Append("ms\n");
                }
                sb.Append("  Match scene cuts:       ");
                sb.Append(record.DeepAnalysisMap.MatchedCuts).Append('/');
                sb.Append(record.DeepAnalysisMap.SourceCutsAnalyzed).Append('\n');
                sb.Append("  MSE baseline:           ");
                sb.Append(record.DeepAnalysisMap.BaselineMse.ToString("F1")).Append('\n');
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
            if (record.SpeedCorrectionTimeMs > 0 || record.FrameSyncTimeMs > 0 || record.DeepAnalysisTimeMs > 0 || record.MergeTimeMs > 0)
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
                if (record.DeepAnalysisTimeMs > 0)
                {
                    sb.Append("  Deep analysis:        ").Append(record.DeepAnalysisTimeMs).Append(" ms\n");
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
                sb.Append("  Dimensione: ").Append(Utils.FormatSize(record.ResultSize)).Append('\n');
            }

            // Encoding post-merge
            if (record.EncodingProfileName.Length > 0)
            {
                sb.Append("\nENCODING\n");
                sb.Append("  Profilo: ").Append(record.EncodingProfileName).Append('\n');
                if (record.EncodedSize > 0 && record.ResultSize > 0)
                {
                    long riduzione = 100 - (record.EncodedSize * 100 / record.ResultSize);
                    sb.Append("  Dimensione: ").Append(Utils.FormatSize(record.ResultSize)).Append(" -> ").Append(Utils.FormatSize(record.EncodedSize));
                    sb.Append(" (riduzione ").Append(riduzione).Append("%)\n");
                }
                if (record.EncodingTimeMs > 0)
                {
                    sb.Append("  Tempo: ").Append(record.EncodingTimeMs).Append(" ms\n");
                }
                if (record.EncodingCommand.Length > 0)
                {
                    sb.Append("  Comando: ").Append(record.EncodingCommand).Append('\n');
                }
            }

            // Comando mkvmerge risultante
            if (record.MergeCommand.Length > 0)
            {
                sb.Append("\nCOMANDO MERGE\n");
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
            else if (status == FileStatus.Encoding) result = "Encoding...";
            else if (status == FileStatus.Done) result = "Completato";
            else if (status == FileStatus.Error) result = "Errore";
            else if (status == FileStatus.Skipped) result = "Saltato";

            return result;
        }

        /// <summary>
        /// Formatta il delay audio completo per il pannello dettaglio
        /// </summary>
        /// <param name="record">Record file</param>
        /// <returns>Stringa dettagliata del delay audio</returns>
        private string FormatDelayFull(FileProcessingRecord record)
        {
            string result = Utils.FormatDelay(record.AudioDelayApplied);

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
            string result = Utils.FormatDelay(record.SubDelayApplied);

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

            // Verifica che le opzioni siano configurate (source obbligatorio, almeno un'operazione)
            if (!done && this._opts.SourceFolder.Length == 0)
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
                    // Unskip: in merge mode consenti solo se c'e' un file lingua associato
                    if (this._opts.TargetLanguage.Count == 0 || record.LangFilePath.Length > 0)
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

            Label lblSource = new Label() { Text = "Sorgente:", X = 1, Y = y, SchemeName = "Dialog" };
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

            Label lblKsa = new Label() { Text = "Mantieni audio:", X = 1, Y = y, SchemeName = "Dialog" };
            string ksaStr = string.Join(",", this._opts.KeepSourceAudioLangs);
            TextField tfKsa = new TextField() { Text = ksaStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblKsac = new Label() { Text = "Mantieni codec:", X = 1, Y = y, SchemeName = "Dialog" };
            string ksacStr = string.Join(",", this._opts.KeepSourceAudioCodec);
            TextField tfKsac = new TextField() { Text = ksacStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            Label lblKss = new Label() { Text = "Mantieni sub:", X = 1, Y = y, SchemeName = "Dialog" };
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

            bool[] stSpeedCorrection;
            Button cbSpeedCorrection = this.CreateToggleLabel("Speed correction (auto-detect)", this._opts.SpeedCorrection, 1, y, "Dialog", out stSpeedCorrection);
            y++;

            bool[] stFrameSync;
            Button cbFrameSync = this.CreateToggleLabel("Frame-sync (confronto visivo)", this._opts.FrameSync, 1, y, "Dialog", out stFrameSync);
            y++;

            bool[] stDeepAnalysis;
            Button cbDeepAnalysis = this.CreateToggleLabel("Deep analysis (analisi completa)", this._opts.DeepAnalysis, 1, y, "Dialog", out stDeepAnalysis);
            y++;

            bool[] stCropSourceTo43;
            Button cbCropSourceTo43 = this.CreateToggleLabel("Crop source to 4:3 (pillarbox)", this._opts.CropSourceTo43, 1, y, "Dialog", out stCropSourceTo43);
            y++;

            bool[] stCropLangTo43;
            Button cbCropLangTo43 = this.CreateToggleLabel("Crop lang to 4:3 (pillarbox)", this._opts.CropLangTo43, 1, y, "Dialog", out stCropLangTo43);
            y++;

            // Mutua esclusione tra frame-sync e deep analysis
            cbFrameSync.Accepting += (sender, e) =>
            {
                if (stFrameSync[0] && stDeepAnalysis[0])
                {
                    stDeepAnalysis[0] = false;
                    cbDeepAnalysis.Text = "[ ] Deep analysis (analisi completa)";
                }
            };
            cbDeepAnalysis.Accepting += (sender, e) =>
            {
                if (stDeepAnalysis[0] && stFrameSync[0])
                {
                    stFrameSync[0] = false;
                    cbFrameSync.Text = "[ ] Frame-sync (confronto visivo)";
                }
            };

            Label lblAudioDelay = new Label() { Text = "Delay audio:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfAudioDelay = new TextField() { Text = this._opts.AudioDelay.ToString(), X = 16, Y = y, Width = 8, SchemeName = "Input" };
            Label lblMs2 = new Label() { Text = "ms", X = 25, Y = y, SchemeName = "Dialog" };
            y++;

            Label lblSubDelay = new Label() { Text = "Delay sub:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSubDelay = new TextField() { Text = this._opts.SubtitleDelay.ToString(), X = 16, Y = y, Width = 8, SchemeName = "Input" };
            Label lblMs3 = new Label() { Text = "ms", X = 25, Y = y, SchemeName = "Dialog" };
            y++;

            // --- Matching ---
            Label lblSection4 = new Label() { Text = "== Matching ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblPattern = new Label() { Text = "Pattern match:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfPattern = new TextField() { Text = this._opts.MatchPattern, X = 16, Y = y, Width = 30, SchemeName = "Input" };
            y++;

            Label lblExt = new Label() { Text = "Estensioni:", X = 1, Y = y, SchemeName = "Dialog" };
            string extStr = string.Join(",", this._opts.FileExtensions);
            TextField tfExt = new TextField() { Text = extStr, X = 16, Y = y, Width = 20, SchemeName = "Input" };
            y++;

            // --- Post-Processing ---
            y++;
            Label lblSection5 = new Label() { Text = "== Post-Processing ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // Dropdown converti audio
            Label lblConvert = new Label() { Text = "Converti audio:", X = 1, Y = y, SchemeName = "Dialog" };
            List<string> convertItems = new List<string>() { "No", "FLAC", "Opus" };
            int convertSelectedIdx = 0;
            if (string.Equals(this._opts.ConvertFormat, "flac", StringComparison.OrdinalIgnoreCase)) { convertSelectedIdx = 1; }
            else if (string.Equals(this._opts.ConvertFormat, "opus", StringComparison.OrdinalIgnoreCase)) { convertSelectedIdx = 2; }
            StyledDropDownList ddConvert = new StyledDropDownList()
            {
                X = 16,
                Y = y,
                Width = 12,
                ReadOnly = true,
                Source = new ListWrapper<string>(new ObservableCollection<string>(convertItems)),
                Text = convertItems[convertSelectedIdx]
            };
            this.SetupDropdownFocusColors(ddConvert);
            y++;

            bool[] stRenameTracks;
            Button cbRenameTracks = this.CreateToggleLabel("Rinomina tutte le tracce audio", this._opts.RenameAllTracks, 1, y, "Dialog", out stRenameTracks);
            y++;

            Label lblEncProfile = new Label() { Text = "Encoding video:", X = 1, Y = y, SchemeName = "Dialog" };

            // Costruisci lista nomi profili per il dropdown
            List<string> encProfileNames = new List<string>();
            encProfileNames.Add("(nessuno)");
            int encSelectedIdx = 0;
            for (int ep = 0; ep < AppSettingsService.Instance.Settings.EncodingProfiles.Count; ep++)
            {
                encProfileNames.Add(AppSettingsService.Instance.Settings.EncodingProfiles[ep].Name);
                if (AppSettingsService.Instance.Settings.EncodingProfiles[ep].Name == this._opts.EncodingProfileName)
                {
                    encSelectedIdx = ep + 1;
                }
            }

            // Dropdown per selezione profilo encoding
            StyledDropDownList ddEncProfile = new StyledDropDownList()
            {
                X = 16,
                Y = y,
                Width = 30,
                ReadOnly = true,
                Source = new ListWrapper<string>(new ObservableCollection<string>(encProfileNames)),
                Text = encProfileNames[encSelectedIdx]
            };
            this.SetupDropdownFocusColors(ddEncProfile);

            dialog.Add(
                lblSection1, lblSource, tfSource, btnBrowseSource, lblLang, tfLang, btnBrowseLang, lblDest, tfDest, btnBrowseDest, cbOverwrite, cbRecursive,
                lblSection2, lblTarget, tfTarget, lblCodec, tfCodec, lblKsa, tfKsa, lblKsac, tfKsac, lblKss, tfKss, cbSubOnly, cbAudioOnly,
                lblSection3, cbSpeedCorrection, cbFrameSync, cbDeepAnalysis, cbCropSourceTo43, cbCropLangTo43, lblAudioDelay, tfAudioDelay, lblMs2, lblSubDelay, tfSubDelay, lblMs3,
                lblSection4, lblPattern, tfPattern, lblExt, tfExt,
                lblSection5, lblConvert, ddConvert, cbRenameTracks, lblEncProfile, ddEncProfile
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
                this._opts.SpeedCorrection = stSpeedCorrection[0];
                this._opts.FrameSync = stFrameSync[0];
                this._opts.DeepAnalysis = stDeepAnalysis[0];
                this._opts.CropSourceTo43 = stCropSourceTo43[0];
                this._opts.CropLangTo43 = stCropLangTo43[0];

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

                // Formato conversione audio
                string convertSel = ddConvert.Text;
                if (string.Equals(convertSel, "FLAC", StringComparison.OrdinalIgnoreCase)) { this._opts.ConvertFormat = "flac"; }
                else if (string.Equals(convertSel, "Opus", StringComparison.OrdinalIgnoreCase)) { this._opts.ConvertFormat = "opus"; }
                else { this._opts.ConvertFormat = ""; }

                this._opts.RenameAllTracks = stRenameTracks[0];

                // Aggiorna path mkvmerge da AppSettings (gestito nel dialog Percorsi tool)
                this._opts.MkvMergePath = AppSettingsService.Instance.Settings.Tools.MkvMergePath.Length > 0 ? AppSettingsService.Instance.Settings.Tools.MkvMergePath : "mkvmerge";

                // Profilo encoding
                string selectedProfile = ddEncProfile.Text;
                if (selectedProfile.Length > 0 && selectedProfile != "(nessuno)")
                {
                    this._opts.EncodingProfileName = selectedProfile;
                }
                else
                {
                    this._opts.EncodingProfileName = "";
                }

                this.AppendLog("Configurazione aggiornata");
            }

            } // if (!this._isProcessing)
        }

        /// <summary>
        /// Risolve automaticamente i percorsi tool (mkvmerge, ffmpeg, mediainfo) senza UI
        /// Salva in AppSettings se trova nuovi percorsi
        /// </summary>
        private void AutoFindTools()
        {
            bool changed = false;

            // mkvmerge
            if (this._opts.MkvMergePath == "mkvmerge" || !File.Exists(this._opts.MkvMergePath))
            {
                MkvMergeProvider mkvProvider = new MkvMergeProvider();
                if (mkvProvider.Resolve(false))
                {
                    this._opts.MkvMergePath = mkvProvider.MkvMergePath;
                    if (AppSettingsService.Instance.Settings.Tools.MkvMergePath != mkvProvider.MkvMergePath)
                    {
                        AppSettingsService.Instance.Settings.Tools.MkvMergePath = mkvProvider.MkvMergePath;
                        changed = true;
                    }
                }
            }

            // ffmpeg
            if (AppSettingsService.Instance.Settings.Tools.FfmpegPath.Length == 0 || !File.Exists(AppSettingsService.Instance.Settings.Tools.FfmpegPath))
            {
                FfmpegProvider ffProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                if (ffProvider.Resolve(false, false))
                {
                    AppSettingsService.Instance.Settings.Tools.FfmpegPath = ffProvider.FfmpegPath;
                    changed = true;
                }
            }

            // mediainfo
            if (AppSettingsService.Instance.Settings.Tools.MediaInfoPath.Length == 0 || !File.Exists(AppSettingsService.Instance.Settings.Tools.MediaInfoPath))
            {
                MediaInfoProvider miProvider = new MediaInfoProvider();
                if (miProvider.Resolve(false))
                {
                    AppSettingsService.Instance.Settings.Tools.MediaInfoPath = miProvider.MediaInfoPath;
                    changed = true;
                }
            }

            if (changed)
            {
                AppSettingsService.Instance.Save();
            }
        }

        /// <summary>
        /// Mostra il dialog per configurazione percorsi tool (mkvmerge, ffmpeg, mediainfo)
        /// Salva direttamente in AppSettings
        /// </summary>
        private void ShowToolPathsDialog()
        {
            bool accepted = false;
            int y = 0;

            // Leggi percorsi da AppSettings (auto-find gia' fatto all'avvio)
            string mkvPathValue = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
            bool mkvFound = mkvPathValue.Length > 0 && mkvPathValue != "mkvmerge" && File.Exists(mkvPathValue);

            string ffmpegPathValue = AppSettingsService.Instance.Settings.Tools.FfmpegPath;
            bool ffmpegFound = ffmpegPathValue.Length > 0 && File.Exists(ffmpegPathValue);

            string mediaInfoPathValue = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
            bool mediaInfoFound = mediaInfoPathValue.Length > 0 && File.Exists(mediaInfoPathValue);

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Percorsi Tool ",
                Width = Dim.Fill(8),
                Height = 14,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // mkvmerge
            Label lblMkv = new Label() { Text = "mkvmerge:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMkv = new TextField() { Text = mkvPathValue, X = 12, Y = y, Width = Dim.Fill(18), SchemeName = "Input" };
            tfMkv.InvokeCommand(Command.LeftStart);
            Button btnBrowseMkv = new Button() { Text = "..", X = Pos.Right(tfMkv) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseMkv.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFile(tfMkv); e.Handled = true; };
            Label lblMkvStatus = new Label() { Text = mkvFound ? "[OK]" : "[NON TROVATO]", X = Pos.Right(btnBrowseMkv) + 1, Y = y, SchemeName = mkvFound ? "Highlight" : "Error" };
            y++;

            // ffmpeg
            Label lblFfmpeg = new Label() { Text = "ffmpeg:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFfmpeg = new TextField() { Text = ffmpegPathValue, X = 12, Y = y, Width = Dim.Fill(28), SchemeName = "Input" };
            tfFfmpeg.InvokeCommand(Command.LeftStart);
            Button btnBrowseFfmpeg = new Button() { Text = "..", X = Pos.Right(tfFfmpeg) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseFfmpeg.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFile(tfFfmpeg); e.Handled = true; };
            Label lblFfmpegStatus = new Label() { Text = ffmpegFound ? "[OK]" : "[NON TROVATO]", X = Pos.Right(btnBrowseFfmpeg) + 1, Y = y, SchemeName = ffmpegFound ? "Highlight" : "Error" };
            Button btnDownloadFfmpeg = new Button() { Text = "Scarica", X = Pos.Right(lblFfmpegStatus) + 1, Y = y, SchemeName = "Dialog", Visible = !ffmpegFound };
            btnDownloadFfmpeg.Accepting += (object sender, CommandEventArgs e) =>
            {
                e.Handled = true;
                btnDownloadFfmpeg.Enabled = false;
                lblFfmpegStatus.Text = "[Download...]";
                lblFfmpegStatus.SchemeName = "Dialog";

                Thread dlThread = new Thread(() =>
                {
                    FfmpegProvider dlProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                    bool dlResult = dlProvider.Resolve(true, true);

                    this._app.Invoke(() =>
                    {
                        if (dlResult)
                        {
                            tfFfmpeg.Text = dlProvider.FfmpegPath;
                            lblFfmpegStatus.Text = "[OK]";
                            lblFfmpegStatus.SchemeName = "Highlight";
                            btnDownloadFfmpeg.Visible = false;
                        }
                        else
                        {
                            lblFfmpegStatus.Text = "[ERRORE]";
                            lblFfmpegStatus.SchemeName = "Error";
                            btnDownloadFfmpeg.Enabled = true;
                        }
                    });
                });
                dlThread.IsBackground = true;
                dlThread.Start();
            };
            y++;

            // mediainfo
            Label lblMediaInfo = new Label() { Text = "mediainfo:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMediaInfo = new TextField() { Text = mediaInfoPathValue, X = 12, Y = y, Width = Dim.Fill(18), SchemeName = "Input" };
            tfMediaInfo.InvokeCommand(Command.LeftStart);
            Button btnBrowseMediaInfo = new Button() { Text = "..", X = Pos.Right(tfMediaInfo) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseMediaInfo.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFile(tfMediaInfo); e.Handled = true; };
            Label lblMediaInfoStatus = new Label() { Text = mediaInfoFound ? "[OK]" : "[NON TROVATO]", X = Pos.Right(btnBrowseMediaInfo) + 1, Y = y, SchemeName = mediaInfoFound ? "Highlight" : "Error" };
            y += 2;

            // cartella temp
            string tempFolderValue = AppSettingsService.Instance.Settings.Tools.TempFolder;
            Label lblTemp = new Label() { Text = "temp:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfTemp = new TextField() { Text = tempFolderValue, X = 12, Y = y, Width = Dim.Fill(18), SchemeName = "Input" };
            tfTemp.InvokeCommand(Command.LeftStart);
            Button btnBrowseTemp = new Button() { Text = "..", X = Pos.Right(tfTemp) + 1, Y = y, SchemeName = "Dialog" };
            btnBrowseTemp.Accepting += (object sender, CommandEventArgs e) => { this.BrowseFolder(tfTemp); e.Handled = true; };

            dialog.Add(
                lblMkv, tfMkv, btnBrowseMkv, lblMkvStatus,
                lblFfmpeg, tfFfmpeg, btnBrowseFfmpeg, lblFfmpegStatus, btnDownloadFfmpeg,
                lblMediaInfo, tfMediaInfo, btnBrowseMediaInfo, lblMediaInfoStatus,
                lblTemp, tfTemp, btnBrowseTemp
            );

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            if (accepted)
            {
                // Aggiorna model
                AppSettingsService.Instance.Settings.Tools.MkvMergePath = tfMkv.Text;
                AppSettingsService.Instance.Settings.Tools.FfmpegPath = tfFfmpeg.Text;
                AppSettingsService.Instance.Settings.Tools.MediaInfoPath = tfMediaInfo.Text;
                AppSettingsService.Instance.Settings.Tools.TempFolder = tfTemp.Text;
                this._opts.MkvMergePath = tfMkv.Text;

                // Valida percorsi
                string toolPathError;
                if (!AppSettingsService.Instance.ValidateToolPaths(out toolPathError))
                {
                    MessageBox.ErrorQuery(this._app, "Errore validazione", toolPathError, "Ok");
                }

                // Salva comunque (i percorsi potrebbero essere volutamente vuoti)
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Percorsi tool aggiornati");
                }
            }
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
        /// Mostra il context menu per l'episodio selezionato
        /// </summary>
        /// <param name="record">Record dell'episodio</param>
        private void ShowEpisodeContextMenu(FileProcessingRecord record)
        {
            if (this._isProcessing) { return; }

            // Verifica disponibilita' mediainfo
            bool mediaInfoAvailable = (AppSettingsService.Instance.Settings.Tools.MediaInfoPath.Length > 0 && File.Exists(AppSettingsService.Instance.Settings.Tools.MediaInfoPath));

            // Costruisci voci e azioni corrispondenti
            List<string> labels = new List<string>();
            List<Action> actions = new List<Action>();

            // Delay: sempre visibile
            labels.Add("  Delay");
            actions.Add(() => { this.ShowDelayDialog(record); });

            // MediaInfo sorgente
            if (mediaInfoAvailable && record.SourceFilePath.Length > 0 && File.Exists(record.SourceFilePath))
            {
                labels.Add("  MediaInfo sorgente");
                actions.Add(() => { this.ShowMediaInfoDialog(record.SourceFilePath, "Sorgente: " + record.SourceFileName); });
            }

            // MediaInfo lingua
            if (mediaInfoAvailable && record.LangFilePath.Length > 0 && File.Exists(record.LangFilePath))
            {
                labels.Add("  MediaInfo lingua");
                actions.Add(() => { this.ShowMediaInfoDialog(record.LangFilePath, "Lingua: " + record.LangFileName); });
            }

            // MediaInfo risultato
            if (mediaInfoAvailable && record.ResultFilePath.Length > 0 && File.Exists(record.ResultFilePath))
            {
                labels.Add("  MediaInfo risultato");
                actions.Add(() => { this.ShowMediaInfoDialog(record.ResultFilePath, "Risultato: " + record.ResultFileName); });
            }

            // Dialog come context menu con ListView
            int selectedAction = -1;
            ObservableCollection<string> items = new ObservableCollection<string>(labels);

            Dialog menuDialog = new Dialog()
            {
                Title = "",
                Width = 28,
                Height = labels.Count + 2,
                BorderStyle = LineStyle.Single,
                SchemeName = "Menu"
            };

            ListView listView = new ListView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                SchemeName = "Menu"
            };
            listView.SetSource(items);

            // Enter/doppio click su voce selezionata: esegui azione
            listView.Accepting += (object sender, CommandEventArgs e) =>
            {
                selectedAction = (int)listView.SelectedItem;
                this._app.RequestStop();
                e.Handled = true;
            };

            menuDialog.Add(listView);
            this._app.Run(menuDialog);
            menuDialog.Dispose();

            // Esegui azione dopo chiusura dialog
            if (selectedAction >= 0 && selectedAction < actions.Count)
            {
                actions[selectedAction]();
            }
        }

        /// <summary>
        /// Mostra il dialog con il report mediainfo di un file
        /// </summary>
        /// <param name="filePath">Percorso file da analizzare</param>
        /// <param name="title">Titolo del dialog</param>
        private void ShowMediaInfoDialog(string filePath, string title)
        {
            Button btnCopy = new Button() { Text = "Copia", SchemeName = "Dialog" };
            Button btnClose = new Button() { Text = "Chiudi", IsDefault = true, SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " MediaInfo: " + title + " ",
                Width = Dim.Fill(4),
                Height = Dim.Fill(2),
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnCopy);
            dialog.AddButton(btnClose);

            btnClose.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // Genera report
            MediaInfoService miService = new MediaInfoService(AppSettingsService.Instance.Settings.Tools.MediaInfoPath);
            string report = miService.GetReport(filePath);

            // Mostra report in TextView read-only scrollabile
            TextView textView = new TextView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                Text = report.Length > 0 ? report : "Nessun report disponibile",
                SchemeName = "Dialog",
                WordWrap = false
            };

            // Copia nella clipboard
            btnCopy.Accepting += (object sender, CommandEventArgs e) =>
            {
                this._app.Clipboard.TrySetClipboardData(report);
                e.Handled = true;
            };

            dialog.Add(textView);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();
        }

        /// <summary>
        /// Mostra il dialog per modifica impostazioni conversione audio (FLAC/Opus)
        /// </summary>
        private void ShowAudioSettingsDialog()
        {
            bool accepted = false;
            int y = 0;
            int tempValue = 0;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Impostazioni Conversione Audio ",
                Width = 55,
                Height = 18,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- FLAC ---
            Label lblFlac = new Label() { Text = "== FLAC ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblFlacComp = new Label() { Text = "Compressione (0-12):", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFlacComp = new TextField() { Text = AppSettingsService.Instance.Settings.Flac.CompressionLevel.ToString(), X = 22, Y = y, Width = 6, SchemeName = "Input" };
            y += 2;

            // --- Opus ---
            Label lblOpus = new Label() { Text = "== Opus (bitrate kbps) ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            Label lblMono = new Label() { Text = "Mono (1ch):", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMono = new TextField() { Text = AppSettingsService.Instance.Settings.Opus.Bitrate.Mono.ToString(), X = 22, Y = y, Width = 6, SchemeName = "Input" };
            y++;

            Label lblStereo = new Label() { Text = "Stereo (2ch):", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfStereo = new TextField() { Text = AppSettingsService.Instance.Settings.Opus.Bitrate.Stereo.ToString(), X = 22, Y = y, Width = 6, SchemeName = "Input" };
            y++;

            Label lblS51 = new Label() { Text = "Surround 5.1:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfS51 = new TextField() { Text = AppSettingsService.Instance.Settings.Opus.Bitrate.Surround51.ToString(), X = 22, Y = y, Width = 6, SchemeName = "Input" };
            y++;

            Label lblS71 = new Label() { Text = "Surround 7.1:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfS71 = new TextField() { Text = AppSettingsService.Instance.Settings.Opus.Bitrate.Surround71.ToString(), X = 22, Y = y, Width = 6, SchemeName = "Input" };
            y++;

            Label lblRange = new Label() { Text = "Range: " + AppSettingsModel.OPUS_BITRATE_MIN + "-" + AppSettingsModel.OPUS_BITRATE_MAX + " kbps", X = 1, Y = y, SchemeName = "Dialog" };

            dialog.Add(lblFlac, lblFlacComp, tfFlacComp, lblOpus, lblMono, tfMono, lblStereo, tfStereo, lblS51, tfS51, lblS71, tfS71, lblRange);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            if (accepted)
            {
                // Parsing valori nel model
                if (int.TryParse(tfFlacComp.Text, out tempValue))
                {
                    AppSettingsService.Instance.Settings.Flac.CompressionLevel = tempValue;
                }
                if (int.TryParse(tfMono.Text, out tempValue))
                {
                    AppSettingsService.Instance.Settings.Opus.Bitrate.Mono = tempValue;
                }
                if (int.TryParse(tfStereo.Text, out tempValue))
                {
                    AppSettingsService.Instance.Settings.Opus.Bitrate.Stereo = tempValue;
                }
                if (int.TryParse(tfS51.Text, out tempValue))
                {
                    AppSettingsService.Instance.Settings.Opus.Bitrate.Surround51 = tempValue;
                }
                if (int.TryParse(tfS71.Text, out tempValue))
                {
                    AppSettingsService.Instance.Settings.Opus.Bitrate.Surround71 = tempValue;
                }

                // Valida e salva
                string audioError;
                if (!AppSettingsService.Instance.ValidateAudio(out audioError))
                {
                    MessageBox.ErrorQuery(this._app, "Errore validazione", audioError, "Ok");
                }
                else if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni audio salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni audio");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per gestione profili di encoding video
        /// </summary>
        private void ShowEncodingProfilesDialog()
        {
            bool accepted = false;
            int tempVal = 0;
            List<EncodingProfile> editProfiles = new List<EncodingProfile>();
            int currentIdx = -1;

            // Clona profili per editing locale
            for (int i = 0; i < AppSettingsService.Instance.Settings.EncodingProfiles.Count; i++)
            {
                editProfiles.Add(AppSettingsService.Instance.Settings.EncodingProfiles[i].Clone());
            }

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Profili Encoding Video ",
                Width = 58,
                Height = 22,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // === Sezione profilo ===
            Label lblTitle = new Label() { Text = "== Profilo ==", X = 1, Y = 0, SchemeName = "Highlight" };

            Label lblProfiles = new Label() { Text = "Profilo:", X = 1, Y = 1, SchemeName = "Dialog" };

            ObservableCollection<string> profileNames = new ObservableCollection<string>();
            for (int i = 0; i < editProfiles.Count; i++)
            {
                profileNames.Add(editProfiles[i].Name);
            }

            string initialProfileText = editProfiles.Count > 0 ? editProfiles[0].Name : "";
            StyledDropDownList ddProfiles = new StyledDropDownList() { X = 10, Y = 1, Width = 24, ReadOnly = true, Source = new ListWrapper<string>(profileNames), Text = initialProfileText };
            this.SetupDropdownFocusColors(ddProfiles);
            Button btnNuovo = new Button() { Text = "[ Nuovo ]", X = 35, Y = 1, NoDecorations = true, NoPadding = true, ShadowStyle = ShadowStyle.None, SchemeName = "Dialog" };
            Button btnElimina = new Button() { Text = "[ Elimina ]", X = 45, Y = 1, NoDecorations = true, NoPadding = true, ShadowStyle = ShadowStyle.None, SchemeName = "Dialog" };

            // Messaggio nessun profilo
            Label lblNoProfili = new Label() { Text = "Nessun profilo. Premere [ Nuovo ] per crearne uno.", X = 1, Y = 3, SchemeName = "Dialog", Visible = false };

            // === Sezione impostazioni ===
            int sectionBaseY = 3;
            Label lblSection2 = new Label() { Text = "== Impostazioni ==", X = 1, Y = sectionBaseY, SchemeName = "Highlight" };

            // Tutti i controlli creati con Y = 0 iniziale, verranno riposizionati
            Label lblName = new Label() { Text = "Nome:", X = 1, Y = 0, SchemeName = "Dialog" };
            TextField tfName = new TextField() { Text = "", X = 18, Y = 0, Width = 30, SchemeName = "Input" };

            Label lblCodec = new Label() { Text = "Codec:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddCodec = new StyledDropDownList() { X = 18, Y = 0, Width = 20, ReadOnly = true, Source = new ListWrapper<string>(new ObservableCollection<string>(EncodingDefaults.CODECS)), Text = "libx265" };
            this.SetupDropdownFocusColors(ddCodec);

            Label lblPreset = new Label() { Text = "Preset:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddPreset = new StyledDropDownList() { X = 18, Y = 0, Width = 20, ReadOnly = true };
            this.SetupDropdownFocusColors(ddPreset);

            Label lblTune = new Label() { Text = "Tune:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddTune = new StyledDropDownList() { X = 18, Y = 0, Width = 30, ReadOnly = true };
            this.SetupDropdownFocusColors(ddTune);

            Label lblProfile = new Label() { Text = "Profile:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddProfile = new StyledDropDownList() { X = 18, Y = 0, Width = 20, ReadOnly = true };
            this.SetupDropdownFocusColors(ddProfile);

            Label lblBitDepth = new Label() { Text = "Bit depth:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddBitDepth = new StyledDropDownList() { X = 18, Y = 0, Width = 30, ReadOnly = true };
            this.SetupDropdownFocusColors(ddBitDepth);

            Label lblRateMode = new Label() { Text = "Rate mode:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddRateMode = new StyledDropDownList() { X = 18, Y = 0, Width = 12, ReadOnly = true };
            this.SetupDropdownFocusColors(ddRateMode);

            Label lblCrfQp = new Label() { Text = "CRF:", X = 1, Y = 0, SchemeName = "Dialog" };
            TextField tfCrfQp = new TextField() { Text = "28", X = 18, Y = 0, Width = 6, SchemeName = "Input" };
            Label lblCrfRange = new Label() { Text = "", X = 26, Y = 0, SchemeName = "Dialog" };

            Label lblBitrate = new Label() { Text = "Bitrate (kbps):", X = 1, Y = 0, SchemeName = "Dialog" };
            TextField tfBitrate = new TextField() { Text = "0", X = 18, Y = 0, Width = 10, SchemeName = "Input" };

            Label lblPasses = new Label() { Text = "Passate:", X = 1, Y = 0, SchemeName = "Dialog" };
            StyledDropDownList ddPasses = new StyledDropDownList() { X = 18, Y = 0, Width = 6, ReadOnly = true, Source = new ListWrapper<string>(new ObservableCollection<string>(new string[] { "1", "2" })), Text = "1" };
            this.SetupDropdownFocusColors(ddPasses);

            Label lblFilmGrain = new Label() { Text = "Film grain:", X = 1, Y = 0, SchemeName = "Dialog" };
            TextField tfFilmGrain = new TextField() { Text = "0", X = 18, Y = 0, Width = 6, SchemeName = "Input" };
            Label lblFgHint = new Label() { Text = "0 = disabilitato, 1-50", X = 26, Y = 0, SchemeName = "Dialog" };

            // Checkbox film grain denoise
            bool[] stFgDenoise = new bool[] { false };
            Button btnFgDenoise = new Button() { Text = "[ ] Film grain denoise", X = 1, Y = 0, NoDecorations = true, NoPadding = true, ShadowStyle = ShadowStyle.None, SchemeName = "Dialog" };
            btnFgDenoise.Accepting += (object sender, CommandEventArgs e) =>
            {
                stFgDenoise[0] = !stFgDenoise[0];
                btnFgDenoise.Text = (stFgDenoise[0] ? "[X] " : "[ ] ") + "Film grain denoise";
                e.Handled = true;
            };

            Label lblExtra = new Label() { Text = "Extra params:", X = 1, Y = 0, SchemeName = "Dialog" };
            TextView tvExtra = new TextView() { Text = "", X = 1, Y = 0, Width = Dim.Fill(2), Height = 3, SchemeName = "Input" };

            // Aggiorna le source dei dropdown in base al codec
            Action<string> updateDropdownSources = (string codec) =>
            {
                string[] presets = EncodingDefaults.GetPresets(codec);
                ddPreset.Source = new ListWrapper<string>(new ObservableCollection<string>(presets));

                string[] tunes = EncodingDefaults.GetTunes(codec);
                ddTune.Source = new ListWrapper<string>(new ObservableCollection<string>(tunes));

                if (EncodingDefaults.HasProfile(codec))
                {
                    string[] profiles = EncodingDefaults.GetProfiles(codec);
                    ddProfile.Source = new ListWrapper<string>(new ObservableCollection<string>(profiles));
                }

                string[] bitDepths = EncodingDefaults.GetBitDepths(codec);
                ddBitDepth.Source = new ListWrapper<string>(new ObservableCollection<string>(bitDepths));

                string[] rateModes = EncodingDefaults.GetRateModes(codec);
                ddRateMode.Source = new ListWrapper<string>(new ObservableCollection<string>(rateModes));
            };

            // Riposiziona e mostra/nascondi campi in base a codec e rate mode
            Action repositionFields = () =>
            {
                string codec = ddCodec.Text;
                string rateMode = ddRateMode.Text;
                bool hasProfile = editProfiles.Count > 0;
                int cy = sectionBaseY + 1;

                // Se non ci sono profili, nascondi tutto
                lblSection2.Visible = hasProfile;
                lblNoProfili.Visible = !hasProfile;

                if (!hasProfile)
                {
                    lblName.Visible = false; tfName.Visible = false;
                    lblCodec.Visible = false; ddCodec.Visible = false;
                    lblPreset.Visible = false; ddPreset.Visible = false;
                    lblTune.Visible = false; ddTune.Visible = false;
                    lblProfile.Visible = false; ddProfile.Visible = false;
                    lblBitDepth.Visible = false; ddBitDepth.Visible = false;
                    lblRateMode.Visible = false; ddRateMode.Visible = false;
                    lblCrfQp.Visible = false; tfCrfQp.Visible = false; lblCrfRange.Visible = false;
                    lblBitrate.Visible = false; tfBitrate.Visible = false;
                    lblPasses.Visible = false; ddPasses.Visible = false;
                    lblFilmGrain.Visible = false; tfFilmGrain.Visible = false; lblFgHint.Visible = false;
                    btnFgDenoise.Visible = false;
                    lblExtra.Visible = false; tvExtra.Visible = false;

                    return;
                }

                // Nome - sempre visibile
                lblName.Y = cy; tfName.Y = cy; lblName.Visible = true; tfName.Visible = true;
                cy++;

                // Codec - sempre visibile
                lblCodec.Y = cy; ddCodec.Y = cy; lblCodec.Visible = true; ddCodec.Visible = true;
                cy++;

                // Preset - sempre visibile
                lblPreset.Y = cy; ddPreset.Y = cy; lblPreset.Visible = true; ddPreset.Visible = true;
                cy++;

                // Tune - sempre visibile
                lblTune.Y = cy; ddTune.Y = cy; lblTune.Visible = true; ddTune.Visible = true;
                cy++;

                // Profile - solo x264/x265
                bool showProfile = EncodingDefaults.HasProfile(codec);
                lblProfile.Visible = showProfile; ddProfile.Visible = showProfile;
                if (showProfile) { lblProfile.Y = cy; ddProfile.Y = cy; cy++; }

                // Bit depth - sempre visibile
                lblBitDepth.Y = cy; ddBitDepth.Y = cy; lblBitDepth.Visible = true; ddBitDepth.Visible = true;
                cy++;

                // Rate mode - sempre visibile
                lblRateMode.Y = cy; ddRateMode.Y = cy; lblRateMode.Visible = true; ddRateMode.Visible = true;
                cy++;

                // CRF/QP - solo se rateMode = crf o qp
                bool showCrf = (rateMode == "crf" || rateMode == "qp");
                lblCrfQp.Visible = showCrf; tfCrfQp.Visible = showCrf; lblCrfRange.Visible = showCrf;
                if (showCrf)
                {
                    lblCrfQp.Text = (rateMode == "qp") ? "QP:" : "CRF:";
                    lblCrfRange.Text = "Range: 0-" + EncodingDefaults.GetMaxCrf(codec);
                    lblCrfQp.Y = cy; tfCrfQp.Y = cy; lblCrfRange.Y = cy;
                    cy++;
                }

                // Bitrate - solo se rateMode = bitrate
                bool showBitrate = (rateMode == "bitrate");
                lblBitrate.Visible = showBitrate; tfBitrate.Visible = showBitrate;
                if (showBitrate) { lblBitrate.Y = cy; tfBitrate.Y = cy; cy++; }

                // Passate - solo se bitrate e codec supporta multi-pass
                bool showPasses = showBitrate && EncodingDefaults.HasMultiPass(codec);
                lblPasses.Visible = showPasses; ddPasses.Visible = showPasses;
                if (showPasses) { lblPasses.Y = cy; ddPasses.Y = cy; cy++; }

                // Film grain - solo svtav1
                bool showFg = EncodingDefaults.HasFilmGrain(codec);
                lblFilmGrain.Visible = showFg; tfFilmGrain.Visible = showFg; lblFgHint.Visible = showFg;
                btnFgDenoise.Visible = showFg;
                if (showFg)
                {
                    lblFilmGrain.Y = cy; tfFilmGrain.Y = cy; lblFgHint.Y = cy;
                    cy++;
                    btnFgDenoise.Y = cy;
                    cy++;
                }

                // Extra params - sempre visibile, label sopra e textview sotto
                lblExtra.Y = cy; lblExtra.Visible = true;
                cy++;
                tvExtra.Y = cy; tvExtra.Visible = true;

                dialog.SetNeedsDraw();
            };

            // Flag per sopprimere il salvataggio durante operazioni batch
            bool suppressSave = false;

            // Salva campi nel profilo corrente
            Action saveCurrentToProfile = () =>
            {
                if (suppressSave) { return; }
                if (currentIdx >= 0 && currentIdx < editProfiles.Count)
                {
                    EncodingProfile p = editProfiles[currentIdx];
                    p.Name = tfName.Text;
                    p.Codec = ddCodec.Text.Length > 0 ? ddCodec.Text : "libx265";
                    p.Preset = ddPreset.Text;
                    p.Tune = ddTune.Text;
                    p.Profile = ddProfile.Text;
                    p.BitDepth = ddBitDepth.Text;
                    p.RateMode = ddRateMode.Text;
                    if (int.TryParse(tfCrfQp.Text, out tempVal)) { p.CrfQp = tempVal; }
                    if (int.TryParse(tfBitrate.Text, out tempVal)) { p.Bitrate = tempVal; }
                    p.Passes = (ddPasses.Text == "2") ? 2 : 1;
                    if (int.TryParse(tfFilmGrain.Text, out tempVal)) { p.FilmGrain = tempVal; }
                    p.FilmGrainDenoise = stFgDenoise[0];
                    p.ExtraParams = tvExtra.Text;
                }
            };

            // Carica profilo nei campi
            Action<int> loadProfileToFields = (int idx) =>
            {
                if (idx >= 0 && idx < editProfiles.Count)
                {
                    EncodingProfile p = editProfiles[idx];
                    tfName.Text = p.Name;

                    // Aggiorna source dropdown in base al codec del profilo
                    updateDropdownSources(p.Codec);
                    ddCodec.Text = p.Codec;
                    ddPreset.Text = p.Preset;
                    ddTune.Text = p.Tune;
                    ddProfile.Text = p.Profile;
                    ddBitDepth.Text = p.BitDepth;
                    ddRateMode.Text = p.RateMode;
                    tfCrfQp.Text = p.CrfQp.ToString();
                    tfBitrate.Text = p.Bitrate.ToString();
                    ddPasses.Text = p.Passes.ToString();
                    tfFilmGrain.Text = p.FilmGrain.ToString();
                    stFgDenoise[0] = p.FilmGrainDenoise;
                    btnFgDenoise.Text = (stFgDenoise[0] ? "[X] " : "[ ] ") + "Film grain denoise";
                    tvExtra.Text = p.ExtraParams;

                    // Riposiziona campi in base al codec e rate mode caricati
                    repositionFields();
                }
            };

            // Aggiorna dropdown profili
            Action refreshProfilesDropdown = () =>
            {
                profileNames.Clear();
                for (int ri = 0; ri < editProfiles.Count; ri++)
                {
                    profileNames.Add(editProfiles[ri].Name);
                }

                ddProfiles.Source = new ListWrapper<string>(profileNames);

                if (currentIdx >= 0 && currentIdx < editProfiles.Count)
                {
                    ddProfiles.Text = editProfiles[currentIdx].Name;
                }
            };

            // Cambio profilo selezionato
            ddProfiles.ValueChanged += (object sender, ValueChangedEventArgs<string> e) =>
            {
                saveCurrentToProfile();
                string selName = ddProfiles.Text;
                for (int pi = 0; pi < editProfiles.Count; pi++)
                {
                    if (editProfiles[pi].Name == selName) { currentIdx = pi; break; }
                }

                loadProfileToFields(currentIdx);
            };

            // Cambio codec: resetta tutti i campi ai default del nuovo codec
            ddCodec.ValueChanged += (object sender, ValueChangedEventArgs<string> e) =>
            {
                string newCodec = ddCodec.Text;

                // Aggiorna source dropdown
                updateDropdownSources(newCodec);

                // Resetta ai default del codec
                string[] presets = EncodingDefaults.GetPresets(newCodec);
                ddPreset.Text = presets[0];

                string[] tunes = EncodingDefaults.GetTunes(newCodec);
                ddTune.Text = tunes[0];

                string[] bitDepths = EncodingDefaults.GetBitDepths(newCodec);
                ddBitDepth.Text = bitDepths[0];

                string[] rateModes = EncodingDefaults.GetRateModes(newCodec);
                ddRateMode.Text = rateModes[0];

                int defaultCrf = EncodingDefaults.GetDefaultCrf(newCodec);
                tfCrfQp.Text = defaultCrf.ToString();
                tfBitrate.Text = "0";
                ddPasses.Text = "1";
                tfFilmGrain.Text = "0";
                stFgDenoise[0] = false;
                btnFgDenoise.Text = "[ ] Film grain denoise";

                if (EncodingDefaults.HasProfile(newCodec))
                {
                    ddProfile.Text = "default";
                }
                else
                {
                    ddProfile.Text = "";
                }

                repositionFields();
            };

            // Cambio rate mode: resetta valori correlati
            ddRateMode.ValueChanged += (object sender, ValueChangedEventArgs<string> e) =>
            {
                string newMode = ddRateMode.Text;
                string codec = ddCodec.Text;

                if (newMode == "crf" || newMode == "qp")
                {
                    int defaultCrf = EncodingDefaults.GetDefaultCrf(codec);
                    tfCrfQp.Text = defaultCrf.ToString();
                }
                else
                {
                    tfBitrate.Text = "0";
                    ddPasses.Text = "1";
                }

                repositionFields();
            };

            // Pulsante Nuovo
            btnNuovo.Accepting += (object sender, CommandEventArgs e) =>
            {
                // Salva profilo corrente (indice vecchio)
                saveCurrentToProfile();

                // Crea nuovo profilo con defaults
                EncodingProfile newProfile = new EncodingProfile();
                newProfile.Name = "Nuovo profilo " + (editProfiles.Count + 1);
                editProfiles.Add(newProfile);
                currentIdx = editProfiles.Count - 1;

                // Sopprime il save durante refresh per non sovrascrivere i defaults
                // del nuovo profilo con campi vuoti (ValueChanged trigger saveCurrentToProfile)
                suppressSave = true;
                refreshProfilesDropdown();
                loadProfileToFields(currentIdx);
                suppressSave = false;
                dialog.SetNeedsDraw();

                e.Handled = true;
            };

            // Pulsante Elimina
            btnElimina.Accepting += (object sender, CommandEventArgs e) =>
            {
                if (editProfiles.Count == 0)
                {
                    e.Handled = true;

                    return;
                }

                editProfiles.RemoveAt(currentIdx);

                if (editProfiles.Count == 0)
                {
                    currentIdx = -1;
                    refreshProfilesDropdown();
                    ddProfiles.Text = "";
                    repositionFields();
                }
                else
                {
                    if (currentIdx >= editProfiles.Count) { currentIdx = editProfiles.Count - 1; }
                    refreshProfilesDropdown();
                    loadProfileToFields(currentIdx);
                }

                dialog.SetNeedsDraw();
                e.Handled = true;
            };

            // Aggiungi tutti i controlli al dialog
            dialog.Add(
                lblTitle, lblProfiles, ddProfiles, btnNuovo, btnElimina, lblNoProfili,
                lblSection2, lblName, tfName, lblCodec, ddCodec,
                lblPreset, ddPreset, lblTune, ddTune, lblProfile, ddProfile,
                lblBitDepth, ddBitDepth, lblRateMode, ddRateMode,
                lblCrfQp, tfCrfQp, lblCrfRange, lblBitrate, tfBitrate,
                lblPasses, ddPasses, lblFilmGrain, tfFilmGrain, lblFgHint,
                btnFgDenoise, lblExtra, tvExtra
            );

            // Carica primo profilo se esiste
            if (editProfiles.Count > 0)
            {
                currentIdx = 0;
                loadProfileToFields(0);
            }
            else
            {
                repositionFields();
            }

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            if (accepted)
            {
                saveCurrentToProfile();
                AppSettingsService.Instance.Settings.EncodingProfiles.Clear();
                for (int i = 0; i < editProfiles.Count; i++)
                {
                    if (editProfiles[i].Name.Length > 0)
                    {
                        AppSettingsService.Instance.Settings.EncodingProfiles.Add(editProfiles[i]);
                    }
                }

                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Profili encoding salvati (" + AppSettingsService.Instance.Settings.EncodingProfiles.Count + " profili)");
                }
                else
                {
                    this.AppendLog("Errore salvataggio profili encoding");
                }
            }
        }

        /// <summary>
        /// Mostra il dialogo con gli step della pipeline corrente
        /// </summary>
        private void ShowPipelineDialog()
        {
            // Ottieni gli step dalla configurazione corrente
            List<string> steps = ProcessingPipeline.GetPipelineSteps(this._opts);

            // Costruisci testo con numerazione
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                sb.AppendLine((i + 1).ToString() + ". " + steps[i]);
            }

            // Crea dialog
            Dialog pipelineDialog = new Dialog()
            {
                Title = " Pipeline ",
                Width = 50,
                Height = steps.Count + 6,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };

            Button btnClose = new Button() { Text = "Chiudi", IsDefault = true, SchemeName = "Dialog" };
            btnClose.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };
            pipelineDialog.AddButton(btnClose);

            TextView pipelineView = new TextView()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = Dim.Fill(1),
                ReadOnly = true,
                CanFocus = false,
                WordWrap = false,
                Text = sb.ToString(),
                SchemeName = "Dialog"
            };

            pipelineDialog.Add(pipelineView);

            this._app.Run(pipelineDialog);
            pipelineDialog.Dispose();
        }

        /// <summary>
        /// Mostra il dialogo guida con help dettagliato
        /// </summary>
        private void ShowHelp()
        {
            // Crea dialog scrollabile
            Dialog helpDialog = new Dialog()
            {
                Title = " Guida - RemuxForge ",
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

        /// <summary>
        /// Mostra il dialogo info con versione e dettagli applicazione
        /// </summary>
        private void ShowInfo()
        {
            // Recupera versione dall'assembly
            Assembly assembly = Assembly.GetExecutingAssembly();
            AssemblyInformationalVersionAttribute infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = infoVersionAttr != null ? infoVersionAttr.InformationalVersion : assembly.GetName().Version.ToString();

            // Componi testo informativo
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("  Nome          RemuxForge");
            sb.AppendLine("  Versione      " + version);
            sb.AppendLine("  Licenza       GNU General Public License v3.0");
            sb.AppendLine("  Repository    https://github.com/simonefil/RemuxForge");
            sb.AppendLine("  Runtime       " + RuntimeInformation.FrameworkDescription);
            sb.AppendLine("  Piattaforma   " + RuntimeInformation.OSDescription);
            string infoText = sb.ToString();

            // Crea dialog info
            Dialog infoDialog = new Dialog()
            {
                Title = " Info ",
                Width = 70,
                Height = 12,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };

            // Pulsante chiudi
            Button btnClose = new Button() { Text = "Chiudi", IsDefault = true, SchemeName = "Dialog" };
            btnClose.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };
            infoDialog.AddButton(btnClose);

            // Label con testo informativo
            Label infoLabel = new Label()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                Text = infoText,
                SchemeName = "Dialog"
            };

            infoDialog.Add(infoLabel);

            this._app.Run(infoDialog);
            infoDialog.Dispose();
        }

        /// <summary>
        /// Mostra il dialog principale impostazioni avanzate con menu sezioni
        /// </summary>
        private void ShowAdvancedSettingsDialog()
        {
            // Pulsanti sezione
            Button btnVideoSync = new Button() { Text = "Video Sync base", X = Pos.Center(), Y = 1, SchemeName = "Dialog" };
            Button btnSpeedCorr = new Button() { Text = "Speed Correction", X = Pos.Center(), Y = 3, SchemeName = "Dialog" };
            Button btnFrameSync = new Button() { Text = "Frame Sync", X = Pos.Center(), Y = 5, SchemeName = "Dialog" };
            Button btnDeepAnal = new Button() { Text = "Deep Analysis", X = Pos.Center(), Y = 7, SchemeName = "Dialog" };
            Button btnTrackSplit = new Button() { Text = "Track Split", X = Pos.Center(), Y = 9, SchemeName = "Dialog" };
            Button btnFfmpeg = new Button() { Text = "Ffmpeg", X = Pos.Center(), Y = 11, SchemeName = "Dialog" };
            Button btnClose = new Button() { Text = "Chiudi", IsDefault = true, SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Impostazioni Avanzate ",
                Width = 40,
                Height = 18,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnClose);

            // Apertura sotto-dialoghi
            btnVideoSync.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedVideoSyncDialog(); };
            btnSpeedCorr.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedSpeedCorrectionDialog(); };
            btnFrameSync.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedFrameSyncDialog(); };
            btnDeepAnal.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedDeepAnalysisDialog(); };
            btnTrackSplit.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedTrackSplitDialog(); };
            btnFfmpeg.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; this.ShowAdvancedFfmpegDialog(); };
            btnClose.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            dialog.Add(btnVideoSync, btnSpeedCorr, btnFrameSync, btnDeepAnal, btnTrackSplit, btnFfmpeg);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati VideoSync
        /// </summary>
        private void ShowAdvancedVideoSyncDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            int tempInt = 0;
            double tempDouble = 0.0;
            VideoSyncConfig cfg = AppSettingsService.Instance.Settings.Advanced.VideoSync;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Video Sync base ",
                Width = 65,
                Height = 28,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Video Sync ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            Label lblFrameWidth = new Label() { Text = "FrameWidth:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFrameWidth = new TextField() { Text = cfg.FrameWidth.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblFrameHeight = new Label() { Text = "FrameHeight:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFrameHeight = new TextField() { Text = cfg.FrameHeight.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMseThreshold = new Label() { Text = "MseThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMseThreshold = new TextField() { Text = cfg.MseThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMseMinThreshold = new Label() { Text = "MseMinThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMseMinThreshold = new TextField() { Text = cfg.MseMinThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSsimThreshold = new Label() { Text = "SsimThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSsimThreshold = new TextField() { Text = cfg.SsimThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSsimMaxThreshold = new Label() { Text = "SsimMaxThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSsimMaxThreshold = new TextField() { Text = cfg.SsimMaxThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblNumCheckPoints = new Label() { Text = "NumCheckPoints:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfNumCheckPoints = new TextField() { Text = cfg.NumCheckPoints.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinValidPoints = new Label() { Text = "MinValidPoints:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinValidPoints = new TextField() { Text = cfg.MinValidPoints.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSceneCutThreshold = new Label() { Text = "SceneCutThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSceneCutThreshold = new TextField() { Text = cfg.SceneCutThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblCutHalfWindow = new Label() { Text = "CutHalfWindow:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfCutHalfWindow = new TextField() { Text = cfg.CutHalfWindow.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblCutSignatureLength = new Label() { Text = "CutSignatureLength:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfCutSignatureLength = new TextField() { Text = cfg.CutSignatureLength.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblFpCorrThreshold = new Label() { Text = "FingerprintCorrThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFpCorrThreshold = new TextField() { Text = cfg.FingerprintCorrelationThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinSceneCuts = new Label() { Text = "MinSceneCuts:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinSceneCuts = new TextField() { Text = cfg.MinSceneCuts.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinCutSpacingFrames = new Label() { Text = "MinCutSpacingFrames:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinCutSpacingFrames = new TextField() { Text = cfg.MinCutSpacingFrames.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifySrcDur = new Label() { Text = "VerifySourceDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifySrcDur = new TextField() { Text = cfg.VerifySourceDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifyLangDur = new Label() { Text = "VerifyLangDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifyLangDur = new TextField() { Text = cfg.VerifyLangDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifySrcRetry = new Label() { Text = "VerifySourceRetrySec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifySrcRetry = new TextField() { Text = cfg.VerifySourceRetrySec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifyLangRetry = new Label() { Text = "VerifyLangRetrySec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifyLangRetry = new TextField() { Text = cfg.VerifyLangRetrySec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };

            dialog.Add(lblHeader, lblFrameWidth, tfFrameWidth, lblFrameHeight, tfFrameHeight, lblMseThreshold, tfMseThreshold, lblMseMinThreshold, tfMseMinThreshold, lblSsimThreshold, tfSsimThreshold, lblSsimMaxThreshold, tfSsimMaxThreshold, lblNumCheckPoints, tfNumCheckPoints, lblMinValidPoints, tfMinValidPoints, lblSceneCutThreshold, tfSceneCutThreshold, lblCutHalfWindow, tfCutHalfWindow, lblCutSignatureLength, tfCutSignatureLength, lblFpCorrThreshold, tfFpCorrThreshold, lblMinSceneCuts, tfMinSceneCuts, lblMinCutSpacingFrames, tfMinCutSpacingFrames, lblVerifySrcDur, tfVerifySrcDur, lblVerifyLangDur, tfVerifyLangDur, lblVerifySrcRetry, tfVerifySrcRetry, lblVerifyLangRetry, tfVerifyLangRetry);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.VideoSync = new VideoSyncConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Video Sync: valori di default ripristinati");
                this.ShowAdvancedVideoSyncDialog();
                return;
            }

            if (accepted)
            {
                // Parsing valori int
                if (int.TryParse(tfFrameWidth.Text, out tempInt))
                {
                    cfg.FrameWidth = tempInt;
                }
                if (int.TryParse(tfFrameHeight.Text, out tempInt))
                {
                    cfg.FrameHeight = tempInt;
                }
                if (int.TryParse(tfNumCheckPoints.Text, out tempInt))
                {
                    cfg.NumCheckPoints = tempInt;
                }
                if (int.TryParse(tfMinValidPoints.Text, out tempInt))
                {
                    cfg.MinValidPoints = tempInt;
                }
                if (int.TryParse(tfCutHalfWindow.Text, out tempInt))
                {
                    cfg.CutHalfWindow = tempInt;
                }
                if (int.TryParse(tfCutSignatureLength.Text, out tempInt))
                {
                    cfg.CutSignatureLength = tempInt;
                }
                if (int.TryParse(tfMinSceneCuts.Text, out tempInt))
                {
                    cfg.MinSceneCuts = tempInt;
                }
                if (int.TryParse(tfMinCutSpacingFrames.Text, out tempInt))
                {
                    cfg.MinCutSpacingFrames = tempInt;
                }
                if (int.TryParse(tfVerifySrcDur.Text, out tempInt))
                {
                    cfg.VerifySourceDurationSec = tempInt;
                }
                if (int.TryParse(tfVerifyLangDur.Text, out tempInt))
                {
                    cfg.VerifyLangDurationSec = tempInt;
                }
                if (int.TryParse(tfVerifySrcRetry.Text, out tempInt))
                {
                    cfg.VerifySourceRetrySec = tempInt;
                }
                if (int.TryParse(tfVerifyLangRetry.Text, out tempInt))
                {
                    cfg.VerifyLangRetrySec = tempInt;
                }

                // Parsing valori double
                if (double.TryParse(tfMseThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.MseThreshold = tempDouble;
                }
                if (double.TryParse(tfMseMinThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.MseMinThreshold = tempDouble;
                }
                if (double.TryParse(tfSsimThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.SsimThreshold = tempDouble;
                }
                if (double.TryParse(tfSsimMaxThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.SsimMaxThreshold = tempDouble;
                }
                if (double.TryParse(tfSceneCutThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.SceneCutThreshold = tempDouble;
                }
                if (double.TryParse(tfFpCorrThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.FingerprintCorrelationThreshold = tempDouble;
                }

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Video Sync salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Video Sync");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati SpeedCorrection
        /// </summary>
        private void ShowAdvancedSpeedCorrectionDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            int tempInt = 0;
            double tempDouble = 0.0;
            SpeedCorrectionConfig cfg = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Speed Correction ",
                Width = 65,
                Height = 15,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Speed Correction ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            Label lblSourceStartSec = new Label() { Text = "SourceStartSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSourceStartSec = new TextField() { Text = cfg.SourceStartSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSourceDurationSec = new Label() { Text = "SourceDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSourceDurationSec = new TextField() { Text = cfg.SourceDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblLangDurationSec = new Label() { Text = "LangDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfLangDurationSec = new TextField() { Text = cfg.LangDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinSpeedRatioDiff = new Label() { Text = "MinSpeedRatioDiff:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinSpeedRatioDiff = new TextField() { Text = cfg.MinSpeedRatioDiff.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMaxDurDiffTelecine = new Label() { Text = "MaxDurationDiffTelecine:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMaxDurDiffTelecine = new TextField() { Text = cfg.MaxDurationDiffTelecine.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };

            dialog.Add(lblHeader, lblSourceStartSec, tfSourceStartSec, lblSourceDurationSec, tfSourceDurationSec, lblLangDurationSec, tfLangDurationSec, lblMinSpeedRatioDiff, tfMinSpeedRatioDiff, lblMaxDurDiffTelecine, tfMaxDurDiffTelecine);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.SpeedCorrection = new SpeedCorrectionConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Speed Correction: valori di default ripristinati");
                this.ShowAdvancedSpeedCorrectionDialog();
                return;
            }

            if (accepted)
            {
                // Parsing valori int
                if (int.TryParse(tfSourceStartSec.Text, out tempInt))
                {
                    cfg.SourceStartSec = tempInt;
                }
                if (int.TryParse(tfSourceDurationSec.Text, out tempInt))
                {
                    cfg.SourceDurationSec = tempInt;
                }
                if (int.TryParse(tfLangDurationSec.Text, out tempInt))
                {
                    cfg.LangDurationSec = tempInt;
                }

                // Parsing valori double
                if (double.TryParse(tfMinSpeedRatioDiff.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.MinSpeedRatioDiff = tempDouble;
                }
                if (double.TryParse(tfMaxDurDiffTelecine.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.MaxDurationDiffTelecine = tempDouble;
                }

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Speed Correction salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Speed Correction");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati FrameSync
        /// </summary>
        private void ShowAdvancedFrameSyncDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            int tempInt = 0;
            FrameSyncConfig cfg = AppSettingsService.Instance.Settings.Advanced.FrameSync;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Frame Sync ",
                Width = 65,
                Height = 15,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Frame Sync ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            Label lblMinDurationMs = new Label() { Text = "MinDurationMs:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinDurationMs = new TextField() { Text = cfg.MinDurationMs.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSourceStartSec = new Label() { Text = "SourceStartSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSourceStartSec = new TextField() { Text = cfg.SourceStartSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSourceDurationSec = new Label() { Text = "SourceDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSourceDurationSec = new TextField() { Text = cfg.SourceDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblLangDurationSec = new Label() { Text = "LangDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfLangDurationSec = new TextField() { Text = cfg.LangDurationSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinValidPoints = new Label() { Text = "MinValidPoints:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinValidPoints = new TextField() { Text = cfg.MinValidPoints.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };

            dialog.Add(lblHeader, lblMinDurationMs, tfMinDurationMs, lblSourceStartSec, tfSourceStartSec, lblSourceDurationSec, tfSourceDurationSec, lblLangDurationSec, tfLangDurationSec, lblMinValidPoints, tfMinValidPoints);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.FrameSync = new FrameSyncConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Frame Sync: valori di default ripristinati");
                this.ShowAdvancedFrameSyncDialog();
                return;
            }

            if (accepted)
            {
                // Parsing valori int
                if (int.TryParse(tfMinDurationMs.Text, out tempInt))
                {
                    cfg.MinDurationMs = tempInt;
                }
                if (int.TryParse(tfSourceStartSec.Text, out tempInt))
                {
                    cfg.SourceStartSec = tempInt;
                }
                if (int.TryParse(tfSourceDurationSec.Text, out tempInt))
                {
                    cfg.SourceDurationSec = tempInt;
                }
                if (int.TryParse(tfLangDurationSec.Text, out tempInt))
                {
                    cfg.LangDurationSec = tempInt;
                }
                if (int.TryParse(tfMinValidPoints.Text, out tempInt))
                {
                    cfg.MinValidPoints = tempInt;
                }

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Frame Sync salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Frame Sync");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati DeepAnalysis
        /// </summary>
        private void ShowAdvancedDeepAnalysisDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            int tempInt = 0;
            double tempDouble = 0.0;
            DeepAnalysisConfig cfg = AppSettingsService.Instance.Settings.Advanced.DeepAnalysis;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Deep Analysis ",
                Width = 75,
                Height = 38,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Deep Analysis ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            Label lblCoarseFps = new Label() { Text = "CoarseFps:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfCoarseFps = new TextField() { Text = cfg.CoarseFps.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblDenseScanFps = new Label() { Text = "DenseScanFps:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfDenseScanFps = new TextField() { Text = cfg.DenseScanFps.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblDenseScanSsim = new Label() { Text = "DenseScanSsimThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfDenseScanSsim = new TextField() { Text = cfg.DenseScanSsimThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblDenseScanMinDip = new Label() { Text = "DenseScanMinDipFrames:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfDenseScanMinDip = new TextField() { Text = cfg.DenseScanMinDipFrames.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblLinearScanWindow = new Label() { Text = "LinearScanWindowSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfLinearScanWindow = new TextField() { Text = cfg.LinearScanWindowSec.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblLinearScanConfirm = new Label() { Text = "LinearScanConfirmFrames:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfLinearScanConfirm = new TextField() { Text = cfg.LinearScanConfirmFrames.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifyDipSsim = new Label() { Text = "VerifyDipSsimThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifyDipSsim = new TextField() { Text = cfg.VerifyDipSsimThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            // Campi array - separati da virgola, campo piu' largo
            string probeMargins = string.Join(", ", cfg.ProbeMultiMarginsSec.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)));
            Label lblProbeMargins = new Label() { Text = "ProbeMultiMarginsSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfProbeMargins = new TextField() { Text = probeMargins, X = 30, Y = y, Width = 30, SchemeName = "Input" };
            y++;

            Label lblProbeMinConsistent = new Label() { Text = "ProbeMinConsistentPoints:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfProbeMinConsistent = new TextField() { Text = cfg.ProbeMinConsistentPoints.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblOffsetProbeDur = new Label() { Text = "OffsetProbeDurationSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfOffsetProbeDur = new TextField() { Text = cfg.OffsetProbeDurationSec.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            // Array int - separato da virgola, campo piu' largo
            string probeDeltas = string.Join(", ", cfg.OffsetProbeDeltas.ConvertAll(v => v.ToString()));
            Label lblOffsetProbeDeltas = new Label() { Text = "OffsetProbeDeltas:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfOffsetProbeDeltas = new TextField() { Text = probeDeltas, X = 30, Y = y, Width = 40, SchemeName = "Input" };
            y++;

            Label lblOffsetProbeMinSsim = new Label() { Text = "OffsetProbeMinSsim:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfOffsetProbeMinSsim = new TextField() { Text = cfg.OffsetProbeMinSsim.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinOffsetChangeMs = new Label() { Text = "MinOffsetChangeMs:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinOffsetChangeMs = new TextField() { Text = cfg.MinOffsetChangeMs.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMinConsecStable = new Label() { Text = "MinConsecutiveStable:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMinConsecStable = new TextField() { Text = cfg.MinConsecutiveStable.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSceneThreshold = new Label() { Text = "SceneThreshold:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSceneThreshold = new TextField() { Text = cfg.SceneThreshold.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblMatchToleranceMs = new Label() { Text = "MatchToleranceMs:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfMatchToleranceMs = new TextField() { Text = cfg.MatchToleranceMs.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblWideProbe = new Label() { Text = "WideProbeToleranceSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfWideProbe = new TextField() { Text = cfg.WideProbeToleranceSec.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblSceneExtract = new Label() { Text = "SceneExtractTimeoutMs:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfSceneExtract = new TextField() { Text = cfg.SceneExtractTimeoutMs.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblGlobalVerifyPts = new Label() { Text = "GlobalVerifyPoints:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfGlobalVerifyPts = new TextField() { Text = cfg.GlobalVerifyPoints.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblGlobalVerifyRatio = new Label() { Text = "GlobalVerifyMinRatio:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfGlobalVerifyRatio = new TextField() { Text = cfg.GlobalVerifyMinRatio.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblVerifyMseMult = new Label() { Text = "VerifyMseMultiplier:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfVerifyMseMult = new TextField() { Text = cfg.VerifyMseMultiplier.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblInitOffsetRange = new Label() { Text = "InitialOffsetRangeSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfInitOffsetRange = new TextField() { Text = cfg.InitialOffsetRangeSec.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblInitOffsetStep = new Label() { Text = "InitialOffsetStepSec:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfInitOffsetStep = new TextField() { Text = cfg.InitialOffsetStepSec.ToString(CultureInfo.InvariantCulture), X = 30, Y = y, Width = 10, SchemeName = "Input" };
            y++;

            Label lblInitVotingCuts = new Label() { Text = "InitialVotingCuts:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfInitVotingCuts = new TextField() { Text = cfg.InitialVotingCuts.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };

            dialog.Add(lblHeader, lblCoarseFps, tfCoarseFps, lblDenseScanFps, tfDenseScanFps, lblDenseScanSsim, tfDenseScanSsim, lblDenseScanMinDip, tfDenseScanMinDip, lblLinearScanWindow, tfLinearScanWindow, lblLinearScanConfirm, tfLinearScanConfirm, lblVerifyDipSsim, tfVerifyDipSsim, lblProbeMargins, tfProbeMargins, lblProbeMinConsistent, tfProbeMinConsistent, lblOffsetProbeDur, tfOffsetProbeDur, lblOffsetProbeDeltas, tfOffsetProbeDeltas, lblOffsetProbeMinSsim, tfOffsetProbeMinSsim, lblMinOffsetChangeMs, tfMinOffsetChangeMs, lblMinConsecStable, tfMinConsecStable, lblSceneThreshold, tfSceneThreshold, lblMatchToleranceMs, tfMatchToleranceMs, lblWideProbe, tfWideProbe, lblSceneExtract, tfSceneExtract, lblGlobalVerifyPts, tfGlobalVerifyPts, lblGlobalVerifyRatio, tfGlobalVerifyRatio, lblVerifyMseMult, tfVerifyMseMult, lblInitOffsetRange, tfInitOffsetRange, lblInitOffsetStep, tfInitOffsetStep, lblInitVotingCuts, tfInitVotingCuts);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.DeepAnalysis = new DeepAnalysisConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Deep Analysis: valori di default ripristinati");
                this.ShowAdvancedDeepAnalysisDialog();
                return;
            }

            if (accepted)
            {
                // Parsing valori double
                if (double.TryParse(tfCoarseFps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.CoarseFps = tempDouble;
                }
                if (double.TryParse(tfDenseScanFps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.DenseScanFps = tempDouble;
                }
                if (double.TryParse(tfDenseScanSsim.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.DenseScanSsimThreshold = tempDouble;
                }
                if (double.TryParse(tfLinearScanWindow.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.LinearScanWindowSec = tempDouble;
                }
                if (double.TryParse(tfVerifyDipSsim.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.VerifyDipSsimThreshold = tempDouble;
                }
                if (double.TryParse(tfOffsetProbeDur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.OffsetProbeDurationSec = tempDouble;
                }
                if (double.TryParse(tfOffsetProbeMinSsim.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.OffsetProbeMinSsim = tempDouble;
                }
                if (double.TryParse(tfSceneThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.SceneThreshold = tempDouble;
                }
                if (double.TryParse(tfWideProbe.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.WideProbeToleranceSec = tempDouble;
                }
                if (double.TryParse(tfGlobalVerifyRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.GlobalVerifyMinRatio = tempDouble;
                }
                if (double.TryParse(tfVerifyMseMult.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.VerifyMseMultiplier = tempDouble;
                }
                if (double.TryParse(tfInitOffsetStep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tempDouble))
                {
                    cfg.InitialOffsetStepSec = tempDouble;
                }

                // Parsing valori int
                if (int.TryParse(tfDenseScanMinDip.Text, out tempInt))
                {
                    cfg.DenseScanMinDipFrames = tempInt;
                }
                if (int.TryParse(tfLinearScanConfirm.Text, out tempInt))
                {
                    cfg.LinearScanConfirmFrames = tempInt;
                }
                if (int.TryParse(tfProbeMinConsistent.Text, out tempInt))
                {
                    cfg.ProbeMinConsistentPoints = tempInt;
                }
                if (int.TryParse(tfMinOffsetChangeMs.Text, out tempInt))
                {
                    cfg.MinOffsetChangeMs = tempInt;
                }
                if (int.TryParse(tfMinConsecStable.Text, out tempInt))
                {
                    cfg.MinConsecutiveStable = tempInt;
                }
                if (int.TryParse(tfMatchToleranceMs.Text, out tempInt))
                {
                    cfg.MatchToleranceMs = tempInt;
                }
                if (int.TryParse(tfSceneExtract.Text, out tempInt))
                {
                    cfg.SceneExtractTimeoutMs = tempInt;
                }
                if (int.TryParse(tfGlobalVerifyPts.Text, out tempInt))
                {
                    cfg.GlobalVerifyPoints = tempInt;
                }
                if (int.TryParse(tfInitOffsetRange.Text, out tempInt))
                {
                    cfg.InitialOffsetRangeSec = tempInt;
                }
                if (int.TryParse(tfInitVotingCuts.Text, out tempInt))
                {
                    cfg.InitialVotingCuts = tempInt;
                }

                // Parsing array ProbeMultiMarginsSec (List<double> separati da virgola)
                string[] marginParts = tfProbeMargins.Text.Split(',');
                List<double> parsedMargins = new List<double>();
                bool marginsValid = true;
                for (int i = 0; i < marginParts.Length; i++)
                {
                    string trimmed = marginParts[i].Trim();
                    if (trimmed.Length > 0)
                    {
                        double val = 0.0;
                        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        {
                            parsedMargins.Add(val);
                        }
                        else
                        {
                            marginsValid = false;
                        }
                    }
                }
                if (marginsValid && parsedMargins.Count > 0)
                {
                    cfg.ProbeMultiMarginsSec = parsedMargins;
                }

                // Parsing array OffsetProbeDeltas (List<int> separati da virgola)
                string[] deltaParts = tfOffsetProbeDeltas.Text.Split(',');
                List<int> parsedDeltas = new List<int>();
                bool deltasValid = true;
                for (int i = 0; i < deltaParts.Length; i++)
                {
                    string trimmed = deltaParts[i].Trim();
                    if (trimmed.Length > 0)
                    {
                        int val = 0;
                        if (int.TryParse(trimmed, out val))
                        {
                            parsedDeltas.Add(val);
                        }
                        else
                        {
                            deltasValid = false;
                        }
                    }
                }
                if (deltasValid && parsedDeltas.Count > 0)
                {
                    cfg.OffsetProbeDeltas = parsedDeltas;
                }

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Deep Analysis salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Deep Analysis");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati TrackSplit
        /// </summary>
        private void ShowAdvancedTrackSplitDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            int tempInt = 0;
            TrackSplitConfig cfg = AppSettingsService.Instance.Settings.Advanced.TrackSplit;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Track Split ",
                Width = 65,
                Height = 10,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Track Split ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            Label lblFfmpegTimeout = new Label() { Text = "FfmpegTimeoutMs:", X = 1, Y = y, SchemeName = "Dialog" };
            TextField tfFfmpegTimeout = new TextField() { Text = cfg.FfmpegTimeoutMs.ToString(), X = 30, Y = y, Width = 10, SchemeName = "Input" };

            dialog.Add(lblHeader, lblFfmpegTimeout, tfFfmpegTimeout);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.TrackSplit = new TrackSplitConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Track Split: valori di default ripristinati");
                this.ShowAdvancedTrackSplitDialog();
                return;
            }

            if (accepted)
            {
                // Parsing valore int
                if (int.TryParse(tfFfmpegTimeout.Text, out tempInt))
                {
                    cfg.FfmpegTimeoutMs = tempInt;
                }

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Track Split salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Track Split");
                }
            }
        }

        /// <summary>
        /// Mostra il dialog per modifica parametri avanzati Ffmpeg
        /// </summary>
        private void ShowAdvancedFfmpegDialog()
        {
            bool accepted = false;
            bool resetDefaults = false;
            int y = 0;
            FfmpegConfig cfg = AppSettingsService.Instance.Settings.Advanced.Ffmpeg;

            // Pulsanti dialog
            Button btnOk = new Button() { Text = "OK", IsDefault = true, SchemeName = "Dialog" };
            Button btnReset = new Button() { Text = "Reset Default", SchemeName = "Dialog" };
            Button btnCancel = new Button() { Text = "Annulla", SchemeName = "Dialog" };

            Dialog dialog = new Dialog()
            {
                Title = " Ffmpeg ",
                Width = 65,
                Height = 10,
                BorderStyle = LineStyle.Double,
                SchemeName = "Dialog"
            };
            dialog.AddButton(btnOk);
            dialog.AddButton(btnReset);
            dialog.AddButton(btnCancel);

            btnOk.Accepting += (object sender, CommandEventArgs e) => { accepted = true; this._app.RequestStop(); e.Handled = true; };
            btnReset.Accepting += (object sender, CommandEventArgs e) => { resetDefaults = true; this._app.RequestStop(); e.Handled = true; };
            btnCancel.Accepting += (object sender, CommandEventArgs e) => { this._app.RequestStop(); e.Handled = true; };

            // --- Intestazione ---
            Label lblHeader = new Label() { Text = "== Parametri Ffmpeg ==", X = 1, Y = y, SchemeName = "Highlight" };
            y++;

            // --- Campi ---
            bool[] hwAccelState = null;
            Button cbHwAccel = this.CreateToggleLabel("Hardware Acceleration", cfg.HardwareAcceleration, 1, y, "Dialog", out hwAccelState);

            dialog.Add(lblHeader, cbHwAccel);

            // Esegui dialog modale
            this._app.Run(dialog);
            dialog.Dispose();

            // Gestione reset default
            if (resetDefaults)
            {
                AppSettingsService.Instance.Settings.Advanced.Ffmpeg = new FfmpegConfig();
                AppSettingsService.Instance.Save();
                this.AppendLog("Ffmpeg: valori di default ripristinati");
                this.ShowAdvancedFfmpegDialog();
                return;
            }

            if (accepted)
            {
                // Salva valore checkbox
                cfg.HardwareAcceleration = hwAccelState[0];

                // Salva impostazioni
                if (AppSettingsService.Instance.Save())
                {
                    this.AppendLog("Impostazioni avanzate Ffmpeg salvate");
                }
                else
                {
                    this.AppendLog("Errore salvataggio impostazioni Ffmpeg");
                }
            }
        }

        #endregion
    }
}
