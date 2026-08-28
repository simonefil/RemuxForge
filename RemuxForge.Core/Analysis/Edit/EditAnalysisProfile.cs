namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Soglie della catena di analisi, misurate sul corpus e non tarate a occhio
    /// </summary>
    internal static class EditAnalysisProfile
    {
        #region Hash

        /// <summary>
        /// Distanza di Hamming massima perché due fotogrammi siano lo stesso, in rilevazione
        /// </summary>
        public const int DETECTION_THRESHOLD = 14;

        /// <summary>
        /// Distanza di Hamming massima in verifica: trovare un confine e verificare l'aggancio sono due mestieri
        /// </summary>
        public const int VERIFICATION_THRESHOLD = 20;

        /// <summary>
        /// Fotogrammi lang di tolleranza quando si cerca un confine
        /// </summary>
        public const int DETECTION_RADIUS = 1;

        /// <summary>
        /// Fotogrammi lang di tolleranza quando si verifica che il film resti agganciato
        /// </summary>
        public const int VERIFICATION_RADIUS = 2;

        #endregion

        #region Changepoint

        /// <summary>
        /// Incremento iniziale e adattivo del bracket di raffinamento
        /// </summary>
        public const double CHANGEPOINT_MARGIN_MS = 8000.0;

        /// <summary>
        /// Millisecondi su cui si media il margine fra le due distanze
        /// </summary>
        public const double CHANGEPOINT_SMOOTH_MS = 400.0;

        /// <summary>
        /// Sotto questo salto il margine è rumore e non si usa per spareggiare
        /// </summary>
        public const double CHANGEPOINT_MIN_JUMP_MS = 800.0;

        /// <summary>
        /// Sotto questa larghezza il pianoro di costo è un punto, non un'ambiguità
        /// </summary>
        public const double CHANGEPOINT_MIN_PLATEAU_MS = 500.0;

        #endregion

        #region Luminanza

        /// <summary>
        /// Sotto questa luminanza media il fotogramma è nero pieno
        /// </summary>
        public const double BLACK_LUMA = 2.0;

        /// <summary>
        /// Fotogrammi neri consecutivi perché sia una run e non interlacciamento
        /// </summary>
        public const int BLACK_CONSECUTIVE = 3;

        /// <summary>
        /// Quanto guardare a sinistra della stima dell'hash per trovare l'inizio della run
        /// </summary>
        public const double BLACK_LOOKBEHIND_MS = 2500.0;

        /// <summary>
        /// Fotogrammi letti attorno alla stima quando si cerca la run
        /// </summary>
        public const int BLACK_FRAMES = 160;

        /// <summary>
        /// Sotto questa escursione di luminanza non c'è nessuna transizione da inseguire
        /// </summary>
        public const double BLACK_EXCURSION = 20.0;

        /// <summary>
        /// Deviazione standard della miniatura sotto cui un fotogramma è scuro
        /// </summary>
        public const double DARK_STD = 8.0;

        #endregion

        #region Audio

        /// <summary>
        /// dB sopra il fondo file entro cui una traccia è considerata muta
        /// </summary>
        public const double AUDIO_MUTE_MARGIN_DB = 10.0;

        /// <summary>
        /// Campioni da 10 ms di violazione consecutiva perché la rottura conti
        /// </summary>
        public const int AUDIO_HOLD_SAMPLES = 30;

        /// <summary>
        /// Finestra di correlazione dell'inviluppo, in millisecondi
        /// </summary>
        public const double AUDIO_WINDOW_MS = 20000.0;

        /// <summary>
        /// Quanto stare lontani dal confine quando si misura lo scalino audio
        /// </summary>
        public const double AUDIO_GUARD_MS = 3000.0;

        /// <summary>
        /// Semiampiezza della scansione attorno all'offset video
        /// </summary>
        public const double AUDIO_SCAN_RADIUS_MS = 1200.0;

        /// <summary>
        /// Passo della scansione dei ritardi audio
        /// </summary>
        public const double AUDIO_SCAN_STEP_MS = 5.0;

        /// <summary>
        /// Due finestre dello stesso pianoro devono dare lo stesso offset entro questo scarto
        /// </summary>
        public const double AUDIO_AGREEMENT_MS = 60.0;

        /// <summary>
        /// Sotto questa frazione del dichiarato lo scalino audio non c'è
        /// </summary>
        public const double AUDIO_MIN_STEP_RATIO = 0.5;

        #endregion

        #region Durata e giudizio

        /// <summary>
        /// Quanto stare lontani dai confini del pianoro quando si misura un offset
        /// </summary>
        public const double DURATION_GUARD_MS = 500.0;

        /// <summary>
        /// Sotto questa durata il pianoro non basta a misurare un offset
        /// </summary>
        public const double DURATION_MIN_PLATEAU_MS = 4000.0;

        /// <summary>
        /// Un fotogramma ogni quanti viene campionato nelle misure di copertura
        /// </summary>
        public const int SAMPLING_STRIDE = 5;

        /// <summary>
        /// Passo della prima passata di ricerca dell'offset di pianoro
        /// </summary>
        public const double DURATION_COARSE_STEP_MS = 20.0;

        /// <summary>
        /// Di quanto si cerca intorno all'offset del profilo
        /// </summary>
        public const double DURATION_RADIUS_MS = 400.0;

        /// <summary>
        /// Semiampiezza della seconda passata, a 1 millisecondo
        /// </summary>
        public const double DURATION_FINE_RADIUS_MS = 30.0;

        /// <summary>
        /// Oltre questo stacco fra due fotogrammi solo-A non è più la stessa run
        /// </summary>
        public const double EXCLUSIVE_GAP_MS = 250.0;

        /// <summary>
        /// Quanto si concede di spostare in avanti il confine senza prove contigue
        /// </summary>
        public const double EXTREME_FORWARD_MS = 600.0;

        #endregion

        #region Copertura

        /// <summary>
        /// Entro quanto cercare l'offset del primo tratto
        /// </summary>
        public const double COVERAGE_INITIAL_RADIUS_MS = 30000.0;

        /// <summary>
        /// Campo di ricerca della costante di ancoraggio della scala
        /// </summary>
        public const double COVERAGE_ANCHOR_FIELD_MS = 200.0;

        /// <summary>
        /// Copertura sotto la quale l'EditMap non descrive più il film e va rifiutata
        /// Il corpus verificato sta sopra 0,96 e le coppie che falliscono sotto 0,40: in mezzo
        /// c'è il materiale difficile, che si consegna, e non la mappa sbagliata
        /// </summary>
        public const double COVERAGE_MINIMUM = 0.70;

        /// <summary>
        /// Campo della prima passata sulla costante di ancoraggio
        /// L'offset iniziale si misura sulla testa del film, che è il punto in cui le due copie
        /// differiscono di più: la costante va cercata su tutto il film e da lontano
        /// </summary>
        public const double COVERAGE_ANCHOR_SWEEP_MS = 5000.0;

        /// <summary>
        /// Passo della prima passata sulla costante di ancoraggio
        /// </summary>
        public const double COVERAGE_ANCHOR_SWEEP_STEP_MS = 50.0;

        #endregion
    }
}
