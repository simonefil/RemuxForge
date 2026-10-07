using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemuxForge.Core.Splitting;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Identità del media; non dipende dall'indice della griglia.
    /// </summary>
    public class MkvSplitSourceIdentity
    {
        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string FullPath { get; set; } = "";

        /// <summary>
        /// Dimensione del file in byte
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Data di ultima modifica UTC in tick
        /// </summary>
        public long LastWriteTimeUtcTicks { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge l'identità di un file su disco
        /// </summary>
        /// <param name="path">Percorso del file</param>
        /// <returns>Identità del file</returns>
        public static MkvSplitSourceIdentity FromFile(string path)
        {
            FileInfo file = new FileInfo(path);
            return new MkvSplitSourceIdentity
            {
                FullPath = file.FullName,
                Length = file.Length,
                LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks
            };
        }

        /// <summary>
        /// Vero se l'altra identità indica lo stesso file non modificato
        /// </summary>
        /// <param name="other">Identità da confrontare</param>
        /// <returns>Vero se percorso, dimensione e data coincidono</returns>
        public bool Matches(MkvSplitSourceIdentity other)
        {
            return other != null && string.Equals(this.FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase)
                && this.Length == other.Length && this.LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
        }

        /// <summary>
        /// Copia dell'identità
        /// </summary>
        /// <returns>Nuova istanza con gli stessi valori</returns>
        public MkvSplitSourceIdentity Clone()
        {
            return (MkvSplitSourceIdentity)this.MemberwiseClone();
        }

        #endregion
    }

    /// <summary>
    /// Modalità di denominazione di un output
    /// </summary>
    public enum MkvSplitNameMode
    {
        /// <summary>
        /// Nome generato dal template delle opzioni
        /// </summary>
        Automatic,

        /// <summary>
        /// Nome scelto dall'utente
        /// </summary>
        Custom
    }

    /// <summary>
    /// Montaggio del solo sorgente; output e occorrenze sono ordinati esplicitamente.
    /// </summary>
    public class MkvSplitDocument
    {
        #region Proprietà

        /// <summary>
        /// Identificativo del documento
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Identità del sorgente
        /// </summary>
        public MkvSplitSourceIdentity Source { get; set; }

        /// <summary>
        /// Modalità Split da cui è nato il documento
        /// </summary>
        public MkvSplitMode OriginMode { get; set; }

        /// <summary>
        /// Output nell'ordine di esportazione
        /// </summary>
        public List<MkvSplitOutput> Outputs { get; set; } = new List<MkvSplitOutput>();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia profonda del documento
        /// </summary>
        /// <returns>Nuovo documento con gli stessi identificativi</returns>
        public MkvSplitDocument Clone()
        {
            return new MkvSplitDocument
            {
                Id = this.Id,
                Source = this.Source?.Clone(),
                OriginMode = this.OriginMode,
                Outputs = this.Outputs?.Select(output => output?.Clone()).ToList()
            };
        }

        #endregion
    }

    /// <summary>
    /// Output del montaggio: sequenza ordinata di clip del sorgente
    /// </summary>
    public class MkvSplitOutput
    {
        #region Proprietà

        /// <summary>
        /// Identificativo dell'output
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Modalità di denominazione
        /// </summary>
        public MkvSplitNameMode NameMode { get; set; }

        /// <summary>
        /// Nome file scelto dall'utente, usato con NameMode Custom
        /// </summary>
        public string CustomFileName { get; set; } = "";

        /// <summary>
        /// Clip nell'ordine del risultato
        /// </summary>
        public List<MkvSplitClip> Clips { get; set; } = new List<MkvSplitClip>();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia profonda dell'output
        /// </summary>
        /// <returns>Nuovo output con gli stessi identificativi</returns>
        public MkvSplitOutput Clone()
        {
            return new MkvSplitOutput
            {
                Id = this.Id,
                NameMode = this.NameMode,
                CustomFileName = this.CustomFileName,
                Clips = this.Clips?.Select(clip => clip?.Clone()).ToList()
            };
        }

        #endregion
    }

    /// <summary>
    /// Intervallo semiaperto di frame sorgente. Nessuno stato escluso.
    /// </summary>
    public class MkvSplitClip
    {
        #region Proprietà

        /// <summary>
        /// Identificativo dell'occorrenza
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Primo frame sorgente incluso
        /// </summary>
        public int StartFrame { get; set; }

        /// <summary>
        /// Primo frame sorgente escluso
        /// </summary>
        public int EndFrameExclusive { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia della clip
        /// </summary>
        /// <returns>Nuova clip con lo stesso identificativo</returns>
        public MkvSplitClip Clone()
        {
            return (MkvSplitClip)this.MemberwiseClone();
        }

        #endregion
    }

    /// <summary>
    /// Gravità di una diagnostica
    /// </summary>
    public enum MkvSplitDiagnosticSeverity
    {
        /// <summary>
        /// Avviso non bloccante
        /// </summary>
        Warning,

        /// <summary>
        /// Errore bloccante
        /// </summary>
        Error
    }

    /// <summary>
    /// Diagnostica del documento o del piano
    /// </summary>
    public class MkvSplitDiagnostic
    {
        #region Proprietà

        /// <summary>
        /// Codice della diagnostica
        /// </summary>
        public string Code { get; set; } = "";

        /// <summary>
        /// Messaggio localizzato
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Gravità
        /// </summary>
        public MkvSplitDiagnosticSeverity Severity { get; set; }

        /// <summary>
        /// Output coinvolto
        /// </summary>
        public Guid? OutputId { get; set; }

        /// <summary>
        /// Clip coinvolta
        /// </summary>
        public Guid? ClipId { get; set; }

        #endregion
    }

    /// <summary>
    /// Proiezione di una clip nelle coordinate sorgente e risultato
    /// </summary>
    public class MkvSplitClipProjection
    {
        #region Proprietà

        /// <summary>
        /// Identificativo della clip
        /// </summary>
        public Guid ClipId { get; set; }

        /// <summary>
        /// Primo frame sorgente incluso
        /// </summary>
        public int StartFrame { get; set; }

        /// <summary>
        /// Primo frame sorgente escluso
        /// </summary>
        public int EndFrameExclusive { get; set; }

        /// <summary>
        /// Primo frame della clip nel risultato
        /// </summary>
        public int ResultStartFrame { get; set; }

        /// <summary>
        /// Numero di frame della clip
        /// </summary>
        public int FrameCount { get { return this.EndFrameExclusive - this.StartFrame; } }

        /// <summary>
        /// Inizio nel sorgente in secondi
        /// </summary>
        public double SourceStartSeconds { get; set; }

        /// <summary>
        /// Fine nel sorgente in secondi
        /// </summary>
        public double SourceEndSeconds { get; set; }

        /// <summary>
        /// Inizio nel risultato in secondi
        /// </summary>
        public double ResultStartSeconds { get; set; }

        /// <summary>
        /// Durata della clip in secondi
        /// </summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Fine nel risultato in secondi
        /// </summary>
        public double ResultEndSeconds { get { return this.ResultStartSeconds + this.DurationSeconds; } }

        #endregion
    }

    /// <summary>
    /// Proiezione di un output: clip, capitoli, nome e stato del file di destinazione
    /// </summary>
    public class MkvSplitOutputProjection
    {
        #region Proprietà

        /// <summary>
        /// Identificativo dell'output
        /// </summary>
        public Guid OutputId { get; set; }

        /// <summary>
        /// Nome file relativo alla cartella di output
        /// </summary>
        public string FileName { get; set; } = "";

        /// <summary>
        /// Percorso completo del file di output
        /// </summary>
        public string FullPath { get; set; } = "";

        /// <summary>
        /// Clip proiettate nell'ordine del risultato
        /// </summary>
        public List<MkvSplitClipProjection> Clips { get; set; } = new List<MkvSplitClipProjection>();

        /// <summary>
        /// Capitoli nel tempo del risultato
        /// </summary>
        public List<ChapterMark> Chapters { get; set; } = new List<ChapterMark>();

        /// <summary>
        /// Durata del risultato in secondi
        /// </summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Numero di frame del risultato
        /// </summary>
        public int FrameCount { get; set; }

        /// <summary>
        /// Stato del file di destinazione
        /// </summary>
        public MkvSplitOutputState OutputState { get; set; }

        #endregion
    }

    /// <summary>
    /// Proiezione dell'intero documento sulla timeline
    /// </summary>
    public class MkvSplitTimelineProjection
    {
        #region Proprietà

        /// <summary>
        /// Identificativo del documento proiettato
        /// </summary>
        public Guid DocumentId { get; set; }

        /// <summary>
        /// Output proiettati
        /// </summary>
        public List<MkvSplitOutputProjection> Outputs { get; set; } = new List<MkvSplitOutputProjection>();

        /// <summary>
        /// Diagnostiche della proiezione
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();

        /// <summary>
        /// Frame sorgente coperti da almeno una clip
        /// </summary>
        public int CoveredSourceFrames { get; set; }

        /// <summary>
        /// Frame totali dei risultati
        /// </summary>
        public long ResultFrameCount { get; set; }

        /// <summary>
        /// Durata totale dei risultati in secondi
        /// </summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Vero se non ci sono diagnostiche di errore
        /// </summary>
        public bool IsValid { get { return !this.Diagnostics.Any(item => item.Severity == MkvSplitDiagnosticSeverity.Error); } }

        #endregion
    }

    /// <summary>
    /// Risoluzione preview e confine. SourceFrame è sempre un frame incluso; BoundaryFrame può essere EOF.
    /// </summary>
    public class MkvSplitFrameResolution
    {
        #region Proprietà

        /// <summary>
        /// Vero se la risoluzione è riuscita
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Output risolto
        /// </summary>
        public Guid? OutputId { get; set; }

        /// <summary>
        /// Clip risolta
        /// </summary>
        public Guid? ClipId { get; set; }

        /// <summary>
        /// Frame sorgente incluso
        /// </summary>
        public int SourceFrame { get; set; }

        /// <summary>
        /// Confine sorgente, fino al numero di frame incluso
        /// </summary>
        public int BoundaryFrame { get; set; }

        /// <summary>
        /// Frame nel risultato
        /// </summary>
        public int ResultFrame { get; set; }

        /// <summary>
        /// Tempo sorgente in secondi
        /// </summary>
        public double SourceSeconds { get; set; }

        /// <summary>
        /// Tempo nel risultato in secondi
        /// </summary>
        public double ResultSeconds { get; set; }

        #endregion
    }

    /// <summary>
    /// Tipo di comando di modifica del documento
    /// </summary>
    public enum MkvSplitEditKind
    {
        /// <summary>
        /// Crea un output vuoto
        /// </summary>
        CreateEmptyOutput,

        /// <summary>
        /// Crea un output con un intervallo del sorgente
        /// </summary>
        CreateOutputFromSource,

        /// <summary>
        /// Cambia la denominazione di un output
        /// </summary>
        RenameOutput,

        /// <summary>
        /// Rimuove gli output selezionati
        /// </summary>
        RemoveOutputs,

        /// <summary>
        /// Sposta gli output selezionati
        /// </summary>
        ReorderOutputs,

        /// <summary>
        /// Divide una clip in un frame del risultato
        /// </summary>
        SplitClip,

        /// <summary>
        /// Divide un output in due output in un frame del risultato
        /// </summary>
        SplitOutput,

        /// <summary>
        /// Inserisce un intervallo del sorgente in un frame del risultato
        /// </summary>
        InsertSource,

        /// <summary>
        /// Accoda un intervallo del sorgente all'output
        /// </summary>
        AppendSource,

        /// <summary>
        /// Rimuove le clip selezionate
        /// </summary>
        RemoveClips,

        /// <summary>
        /// Rimuove un intervallo del risultato
        /// </summary>
        RemoveResultRange,

        /// <summary>
        /// Riordina le clip selezionate nell'output
        /// </summary>
        ReorderClips,

        /// <summary>
        /// Sposta le clip selezionate in un output di destinazione
        /// </summary>
        MoveClips,

        /// <summary>
        /// Copia le clip selezionate in un output di destinazione
        /// </summary>
        CopyClips,

        /// <summary>
        /// Unisce gli output selezionati nel primo
        /// </summary>
        MergeOutputs,

        /// <summary>
        /// Unisce due clip contigue rimuovendo la divisione
        /// </summary>
        RemoveDivision,

        /// <summary>
        /// Cambia l'intervallo sorgente di una clip
        /// </summary>
        TrimClip,

        /// <summary>
        /// Sposta il confine condiviso fra due clip contigue
        /// </summary>
        MoveSharedBoundary
    }

    /// <summary>
    /// Comando atomico. ResultFrame/ResultEndFrameExclusive sono confini nella sequenza risultato;
    /// InsertIndex si riferisce alla lista dopo la rimozione delle occorrenze spostate.
    /// ReorderClips usa ClipIds come sequenza esplicita; MoveClips/CopyClips li usano come selezione
    /// e conservano l'ordine relativo nella timeline origine (non l'ordine temporale del sorgente).
    /// Per CopyClips non avviene rimozione: InsertIndex si riferisce alla destinazione corrente.
    /// </summary>
    public class MkvSplitEditCommand
    {
        #region Proprietà

        /// <summary>
        /// Tipo di comando
        /// </summary>
        public MkvSplitEditKind Kind { get; set; }

        /// <summary>
        /// Output su cui opera il comando
        /// </summary>
        public Guid OutputId { get; set; }

        /// <summary>
        /// Output di destinazione per MoveClips e CopyClips
        /// </summary>
        public Guid DestinationOutputId { get; set; }

        /// <summary>
        /// Clip su cui opera il comando
        /// </summary>
        public Guid ClipId { get; set; }

        /// <summary>
        /// Clip successiva attesa per RemoveDivision e MoveSharedBoundary
        /// </summary>
        public Guid NextClipId { get; set; }

        /// <summary>
        /// Output selezionati
        /// </summary>
        public List<Guid> OutputIds { get; set; } = new List<Guid>();

        /// <summary>
        /// Clip selezionate
        /// </summary>
        public List<Guid> ClipIds { get; set; } = new List<Guid>();

        /// <summary>
        /// Primo frame sorgente incluso
        /// </summary>
        public int StartFrame { get; set; }

        /// <summary>
        /// Primo frame sorgente escluso
        /// </summary>
        public int EndFrameExclusive { get; set; }

        /// <summary>
        /// Confine nella sequenza risultato
        /// </summary>
        public int ResultFrame { get; set; }

        /// <summary>
        /// Confine finale esclusivo nella sequenza risultato
        /// </summary>
        public int ResultEndFrameExclusive { get; set; }

        /// <summary>
        /// Posizione di inserimento nella lista di destinazione
        /// </summary>
        public int InsertIndex { get; set; }

        /// <summary>
        /// Modalità di denominazione per RenameOutput
        /// </summary>
        public MkvSplitNameMode NameMode { get; set; }

        /// <summary>
        /// Nome file scelto per RenameOutput
        /// </summary>
        public string CustomFileName { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Esito di un comando di modifica
    /// </summary>
    public class MkvSplitEditResult
    {
        #region Proprietà

        /// <summary>
        /// Documento risultante, l'input se il comando non ha avuto effetto
        /// </summary>
        public MkvSplitDocument Document { get; set; }

        /// <summary>
        /// Vero se il documento è cambiato
        /// </summary>
        public bool Changed { get; set; }

        /// <summary>
        /// Output da selezionare dopo il comando
        /// </summary>
        public Guid? SelectedOutputId { get; set; }

        /// <summary>
        /// Clip da selezionare dopo il comando
        /// </summary>
        public Guid? SelectedClipId { get; set; }

        /// <summary>
        /// Diagnostiche del comando
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();

        #endregion
    }

    /// <summary>
    /// Clip del piano eseguibile
    /// </summary>
    public class MkvSplitExecutionClip
    {
        #region Proprietà

        /// <summary>
        /// Identificativo della clip
        /// </summary>
        public Guid ClipId { get; set; }

        /// <summary>
        /// Segmento da estrarre dal sorgente
        /// </summary>
        public MkvSplitSegment Segment { get; set; }

        /// <summary>
        /// Vero se la clip inizia e finisce su keyframe e si copia senza ricodifica
        /// </summary>
        public bool UsesFastPath { get; set; }

        #endregion
    }

    /// <summary>
    /// Output del piano eseguibile
    /// </summary>
    public class MkvSplitExecutionOutput
    {
        #region Proprietà

        /// <summary>
        /// Identificativo dell'output
        /// </summary>
        public Guid OutputId { get; set; }

        /// <summary>
        /// Proiezione dell'output
        /// </summary>
        public MkvSplitOutputProjection Projection { get; set; }

        /// <summary>
        /// Clip da eseguire
        /// </summary>
        public List<MkvSplitExecutionClip> Clips { get; set; } = new List<MkvSplitExecutionClip>();

        /// <summary>
        /// Tracce da rimuxare dal sorgente senza clipping per questo output equivalente al sorgente intero.
        /// </summary>
        public List<int> NativeSubtitleTrackIds { get; set; } = new List<int>();

        #endregion
    }

    /// <summary>
    /// Piano eseguibile di un documento
    /// </summary>
    public class MkvSplitExecutionPlan
    {
        #region Proprietà

        /// <summary>
        /// Copia del documento da eseguire
        /// </summary>
        public MkvSplitDocument Document { get; set; }

        /// <summary>
        /// Analisi del sorgente
        /// </summary>
        public MkvSplitAnalysis Analysis { get; set; }

        /// <summary>
        /// Proiezione del documento
        /// </summary>
        public MkvSplitTimelineProjection Projection { get; set; }

        /// <summary>
        /// Output da eseguire
        /// </summary>
        public List<MkvSplitExecutionOutput> Outputs { get; set; } = new List<MkvSplitExecutionOutput>();

        /// <summary>
        /// Tracce del sorgente
        /// </summary>
        public List<TrackInfo> Tracks { get; set; } = new List<TrackInfo>();

        /// <summary>
        /// Sottotitoli letti dal sorgente da ritagliare
        /// </summary>
        public List<MkvSplitSubtitleTrack> Subtitles { get; set; } = new List<MkvSplitSubtitleTrack>();

        /// <summary>
        /// Vero se la proiezione esiste ed è valida
        /// </summary>
        public bool IsValid { get { return this.Projection != null && this.Projection.IsValid; } }

        #endregion
    }

    /// <summary>
    /// Eventi estratti dal sorgente, mai dagli intermedi tagliati.
    /// </summary>
    public class MkvSplitSubtitleTrack
    {
        #region Proprietà

        /// <summary>
        /// Traccia sorgente
        /// </summary>
        public TrackInfo Track { get; set; }

        /// <summary>
        /// Estensione del file estratto
        /// </summary>
        public string Extension { get; set; }

        /// <summary>
        /// Eventi nel tempo del sorgente
        /// </summary>
        public List<MkvSplitSubtitleEvent> Events { get; set; } = new List<MkvSplitSubtitleEvent>();

        /// <summary>
        /// Intestazione PCS del primo display-set PGS, usata per una traccia vuota
        /// </summary>
        public byte[] BitmapPresentationHeader { get; set; }

        /// <summary>
        /// Contenuto testuale del file estratto
        /// </summary>
        public string SourceContent { get; set; }

        #endregion
    }

    /// <summary>
    /// Evento di sottotitolo nel tempo del sorgente
    /// </summary>
    public class MkvSplitSubtitleEvent
    {
        #region Proprietà

        /// <summary>
        /// Inizio in secondi
        /// </summary>
        public double StartSeconds { get; set; }

        /// <summary>
        /// Fine in secondi
        /// </summary>
        public double EndSeconds { get; set; }

        /// <summary>
        /// Testo dell'evento SRT
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Campi della riga Dialogue ASS/SSA
        /// </summary>
        public string[] Fields { get; set; }

        /// <summary>
        /// Vero se l'evento ha effetti temporizzati che non si possono tagliare
        /// </summary>
        public bool HasTimedEffects { get; set; }

        /// <summary>
        /// Segmenti del display-set PGS
        /// </summary>
        public List<MkvSplitBitmapSegment> BitmapSegments { get; set; }

        /// <summary>
        /// Payload SPU VobSub
        /// </summary>
        public byte[] BinaryPayload { get; set; }

        /// <summary>
        /// Offset della sequenza di controllo di inizio nello SPU
        /// </summary>
        public int StartControlOffset { get; set; }

        /// <summary>
        /// Offset della sequenza di controllo di fine nello SPU
        /// </summary>
        public int EndControlOffset { get; set; }

        /// <summary>
        /// Indice della riga di origine nel file sorgente
        /// </summary>
        public int SourceLineIndex { get; set; }

        /// <summary>
        /// Colonna del tempo di inizio nei campi ASS/SSA
        /// </summary>
        public int StartColumn { get; set; }

        /// <summary>
        /// Colonna del tempo di fine nei campi ASS/SSA
        /// </summary>
        public int EndColumn { get; set; }

        /// <summary>
        /// Intestazione pack MPEG del pacchetto VobSub
        /// </summary>
        public byte[] PacketPackHeader { get; set; }

        /// <summary>
        /// Intestazione PES del pacchetto VobSub
        /// </summary>
        public byte[] PacketPesHeader { get; set; }

        /// <summary>
        /// Substream DVD del pacchetto VobSub
        /// </summary>
        public byte SubstreamId { get; set; }

        #endregion
    }

    /// <summary>
    /// Segmento di un display-set PGS
    /// </summary>
    public class MkvSplitBitmapSegment
    {
        #region Proprietà

        /// <summary>
        /// Tipo di segmento
        /// </summary>
        public byte Type { get; set; }

        /// <summary>
        /// Dati del segmento senza intestazione SUP
        /// </summary>
        public byte[] Data { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato di esecuzione di un output
    /// </summary>
    public enum MkvSplitOutputExecutionStatus
    {
        /// <summary>
        /// In attesa
        /// </summary>
        Pending,

        /// <summary>
        /// Completato
        /// </summary>
        Done,

        /// <summary>
        /// Saltato perché il file esiste già
        /// </summary>
        ExistsSkipped,

        /// <summary>
        /// Fallito
        /// </summary>
        Failed,

        /// <summary>
        /// Annullato
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// Esito dell'esecuzione di un output
    /// </summary>
    public class MkvSplitOutputExecutionResult
    {
        #region Proprietà

        /// <summary>
        /// Identificativo dell'output
        /// </summary>
        public Guid OutputId { get; set; }

        /// <summary>
        /// Percorso completo del file di output
        /// </summary>
        public string FullPath { get; set; } = "";

        /// <summary>
        /// Stato di esecuzione
        /// </summary>
        public MkvSplitOutputExecutionStatus Status { get; set; }

        /// <summary>
        /// Messaggio di errore, vuoto se non fallito
        /// </summary>
        public string ErrorMessage { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Esito dell'esecuzione di un montaggio
    /// </summary>
    public class MkvSplitMontageExecutionResult
    {
        #region Proprietà

        /// <summary>
        /// Esiti per output
        /// </summary>
        public List<MkvSplitOutputExecutionResult> Outputs { get; set; } = new List<MkvSplitOutputExecutionResult>();

        /// <summary>
        /// Codice di uscita complessivo
        /// </summary>
        public int ExitCode { get; set; }

        #endregion
    }
}
