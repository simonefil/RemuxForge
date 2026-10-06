using System;
using System.Collections.Generic;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;

namespace RemuxForge.Web.Services
{
    public enum SplitApplyStatus { Applied, Invalid, Conflict, Busy, Cancelled }

    /// <summary>Copia indipendente per l'editor; le revisioni applicato/opzioni sono possedute dall'orchestrator.</summary>
    public class SplitEditorSnapshot
    {
        public Guid SessionId { get; set; }
        public Guid RecordId { get; set; }
        public MkvSplitSourceIdentity SourceIdentity { get; set; }
        public long ExpectedAppliedRevision { get; set; }
        public long ExpectedOptionsRevision { get; set; }
        public MkvSplitDocument Document { get; set; }
        public MkvSplitAnalysis Analysis { get; set; }
        public MkvFileInfo SourceInfo { get; set; }
        public MkvSplitOptions Options { get; set; }
    }

    public class SplitEditorOpenResult
    {
        public SplitApplyStatus Status { get; set; }
        public SplitEditorSnapshot Snapshot { get; set; }
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();
    }

    public class SplitApplyRequest
    {
        public Guid SessionId { get; set; }
        public long ExpectedAppliedRevision { get; set; }
        public long ExpectedOptionsRevision { get; set; }
        public long DraftRevision { get; set; }
        public MkvSplitDocument Document { get; set; }
    }

    public class SplitApplyResult
    {
        public SplitApplyStatus Status { get; set; }
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();
        public long AppliedRevision { get; set; }
        public long OptionsRevision { get; set; }
        public MkvSplitExecutionPlan Plan { get; set; }
    }
}
