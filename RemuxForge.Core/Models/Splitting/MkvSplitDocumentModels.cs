using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemuxForge.Core.Splitting;

namespace RemuxForge.Core.Models
{
    /// <summary>Identità del media; non dipende dall'indice della griglia.</summary>
    public class MkvSplitSourceIdentity
    {
        public string FullPath { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }

        public static MkvSplitSourceIdentity FromFile(string path)
        {
            FileInfo file = new FileInfo(path);
            return new MkvSplitSourceIdentity { FullPath = file.FullName, Length = file.Length, LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks };
        }

        public bool Matches(MkvSplitSourceIdentity other)
        {
            return other != null && string.Equals(this.FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase)
                && this.Length == other.Length && this.LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
        }

        public MkvSplitSourceIdentity Clone() { return (MkvSplitSourceIdentity)this.MemberwiseClone(); }
    }

    public enum MkvSplitNameMode { Automatic, Custom }

    /// <summary>Montaggio del solo sorgente; output e occorrenze sono ordinati esplicitamente.</summary>
    public class MkvSplitDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public MkvSplitSourceIdentity Source { get; set; }
        public MkvSplitMode OriginMode { get; set; }
        public List<MkvSplitOutput> Outputs { get; set; } = new List<MkvSplitOutput>();

        public MkvSplitDocument Clone()
        {
            return new MkvSplitDocument { Id = this.Id, Source = this.Source?.Clone(), OriginMode = this.OriginMode,
                Outputs = this.Outputs?.Select(output => output?.Clone()).ToList() };
        }
    }

    public class MkvSplitOutput
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public MkvSplitNameMode NameMode { get; set; }
        public string CustomFileName { get; set; } = "";
        public List<MkvSplitClip> Clips { get; set; } = new List<MkvSplitClip>();

        public MkvSplitOutput Clone()
        {
            return new MkvSplitOutput { Id = this.Id, NameMode = this.NameMode, CustomFileName = this.CustomFileName,
                Clips = this.Clips?.Select(clip => clip?.Clone()).ToList() };
        }
    }

    /// <summary>Intervallo semiaperto di frame sorgente. Nessuno stato escluso.</summary>
    public class MkvSplitClip
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int StartFrame { get; set; }
        public int EndFrameExclusive { get; set; }
        public MkvSplitClip Clone() { return (MkvSplitClip)this.MemberwiseClone(); }
    }

    public enum MkvSplitDiagnosticSeverity { Warning, Error }

    public class MkvSplitDiagnostic
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public MkvSplitDiagnosticSeverity Severity { get; set; }
        public Guid? OutputId { get; set; }
        public Guid? ClipId { get; set; }
    }

    public class MkvSplitClipProjection
    {
        public Guid ClipId { get; set; }
        public int StartFrame { get; set; }
        public int EndFrameExclusive { get; set; }
        public int ResultStartFrame { get; set; }
        public int FrameCount { get { return this.EndFrameExclusive - this.StartFrame; } }
        public double SourceStartSeconds { get; set; }
        public double SourceEndSeconds { get; set; }
        public double ResultStartSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public double ResultEndSeconds { get { return this.ResultStartSeconds + this.DurationSeconds; } }
    }

    public class MkvSplitOutputProjection
    {
        public Guid OutputId { get; set; }
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public List<MkvSplitClipProjection> Clips { get; set; } = new List<MkvSplitClipProjection>();
        public List<MkvSplitChapter> Chapters { get; set; } = new List<MkvSplitChapter>();
        public double DurationSeconds { get; set; }
        public int FrameCount { get; set; }
        public MkvSplitOutputState OutputState { get; set; }
    }

    public class MkvSplitTimelineProjection
    {
        public Guid DocumentId { get; set; }
        public List<MkvSplitOutputProjection> Outputs { get; set; } = new List<MkvSplitOutputProjection>();
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();
        public int CoveredSourceFrames { get; set; }
        public long ResultFrameCount { get; set; }
        public double DurationSeconds { get; set; }
        public bool IsValid { get { return !this.Diagnostics.Any(item => item.Severity == MkvSplitDiagnosticSeverity.Error); } }
    }

    /// <summary>Risoluzione preview e confine. SourceFrame è sempre un frame incluso; BoundaryFrame può essere EOF.</summary>
    public class MkvSplitFrameResolution
    {
        public bool IsValid { get; set; }
        public Guid? OutputId { get; set; }
        public Guid? ClipId { get; set; }
        public int SourceFrame { get; set; }
        public int BoundaryFrame { get; set; }
        public int ResultFrame { get; set; }
        public double SourceSeconds { get; set; }
        public double ResultSeconds { get; set; }
    }

    public enum MkvSplitEditKind
    {
        CreateEmptyOutput, CreateOutputFromSource, RenameOutput, RemoveOutputs, ReorderOutputs,
        SplitClip, SplitOutput, InsertSource, AppendSource, RemoveClips, RemoveResultRange,
        ReorderClips, MoveClips, CopyClips, MergeOutputs, RemoveDivision, TrimClip, MoveSharedBoundary
    }

    /// <summary>Comando atomico. ResultFrame/ResultEndFrameExclusive sono confini nella sequenza risultato;
    /// InsertIndex si riferisce alla lista dopo la rimozione delle occorrenze spostate.
    /// ReorderClips usa ClipIds come sequenza esplicita; MoveClips/CopyClips li usano come selezione
    /// e conservano l'ordine relativo nella timeline origine (non l'ordine temporale del sorgente).
    /// Per CopyClips non avviene rimozione: InsertIndex si riferisce alla destinazione corrente.</summary>
    public class MkvSplitEditCommand
    {
        public MkvSplitEditKind Kind { get; set; }
        public Guid OutputId { get; set; }
        public Guid DestinationOutputId { get; set; }
        public Guid ClipId { get; set; }
        public Guid NextClipId { get; set; }
        public List<Guid> OutputIds { get; set; } = new List<Guid>();
        public List<Guid> ClipIds { get; set; } = new List<Guid>();
        public int StartFrame { get; set; }
        public int EndFrameExclusive { get; set; }
        public int ResultFrame { get; set; }
        public int ResultEndFrameExclusive { get; set; }
        public int InsertIndex { get; set; }
        public MkvSplitNameMode NameMode { get; set; }
        public string CustomFileName { get; set; } = "";
    }

    public class MkvSplitEditResult
    {
        public MkvSplitDocument Document { get; set; }
        public bool Changed { get; set; }
        public Guid? SelectedOutputId { get; set; }
        public Guid? SelectedClipId { get; set; }
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();
    }

    public class MkvSplitExecutionClip
    {
        public Guid ClipId { get; set; }
        public MkvSplitSegment Segment { get; set; }
        public bool UsesFastPath { get; set; }
    }

    public class MkvSplitExecutionOutput
    {
        public Guid OutputId { get; set; }
        public MkvSplitOutputProjection Projection { get; set; }
        public List<MkvSplitExecutionClip> Clips { get; set; } = new List<MkvSplitExecutionClip>();
        /// <summary>Tracce da rimuxare dal sorgente senza clipping per questo output equivalente al sorgente intero.</summary>
        public List<int> NativeSubtitleTrackIds { get; set; } = new List<int>();
    }

    public class MkvSplitExecutionPlan
    {
        public MkvSplitDocument Document { get; set; }
        public MkvSplitAnalysis Analysis { get; set; }
        public MkvSplitTimelineProjection Projection { get; set; }
        public List<MkvSplitExecutionOutput> Outputs { get; set; } = new List<MkvSplitExecutionOutput>();
        public List<TrackInfo> Tracks { get; set; } = new List<TrackInfo>();
        public List<MkvSplitSubtitleTrack> Subtitles { get; set; } = new List<MkvSplitSubtitleTrack>();
        public bool IsValid { get { return this.Projection != null && this.Projection.IsValid; } }
    }

    /// <summary>Eventi estratti dal sorgente, mai dagli intermedi tagliati.</summary>
    public class MkvSplitSubtitleTrack
    {
        public TrackInfo Track { get; set; }
        public string Extension { get; set; }
        public List<MkvSplitSubtitleEvent> Events { get; set; } = new List<MkvSplitSubtitleEvent>();
        public byte[] BitmapPresentationHeader { get; set; }
        public string SourceContent { get; set; }
    }

    public class MkvSplitSubtitleEvent
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Text { get; set; }
        public string[] Fields { get; set; }
        public bool HasTimedEffects { get; set; }
        public List<MkvSplitBitmapSegment> BitmapSegments { get; set; }
        public byte[] BinaryPayload { get; set; }
        public int StartControlOffset { get; set; }
        public int EndControlOffset { get; set; }
        public int SourceLineIndex { get; set; }
        public int StartColumn { get; set; }
        public int EndColumn { get; set; }
        public byte[] PacketPackHeader { get; set; }
        public byte[] PacketPesHeader { get; set; }
        public byte SubstreamId { get; set; }
    }

    public class MkvSplitBitmapSegment
    {
        public byte Type { get; set; }
        public byte[] Data { get; set; }
    }

    public enum MkvSplitOutputExecutionStatus { Pending, Done, ExistsSkipped, Failed, Cancelled }

    public class MkvSplitOutputExecutionResult
    {
        public Guid OutputId { get; set; }
        public string FullPath { get; set; } = "";
        public MkvSplitOutputExecutionStatus Status { get; set; }
        public string ErrorMessage { get; set; } = "";
    }

    public class MkvSplitMontageExecutionResult
    {
        public List<MkvSplitOutputExecutionResult> Outputs { get; set; } = new List<MkvSplitOutputExecutionResult>();
        public int ExitCode { get; set; }
    }
}
