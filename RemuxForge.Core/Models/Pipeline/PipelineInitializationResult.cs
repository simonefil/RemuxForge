using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>Field = proprietà Options, FfmpegPath per il tool globale, o vuoto se ignoto; Section è un identificatore Core.</summary>
    public sealed record PipelineInitializationIssue(string Code, string Message, string Field,
        string Section);

    public sealed record PipelineInitializationLog(LogSection Section, LogLevel Level, string Message);

    /// <summary>Esito dell'init candidato. Su fallimento il lavoro attivo resta invariato.</summary>
    public sealed class PipelineInitializationResult
    {
        private readonly List<PipelineInitializationIssue> _errors = new List<PipelineInitializationIssue>();
        private readonly List<PipelineInitializationIssue> _warnings = new List<PipelineInitializationIssue>();
        private readonly List<PipelineInitializationLog> _log = new List<PipelineInitializationLog>();

        public bool Success { get; internal set; }
        /// <summary>Copia detached delle opzioni normalizzate; null in caso di fallimento.</summary>
        public Options AppliedOptions { get; internal set; }
        public IReadOnlyList<PipelineInitializationIssue> Errors => this._errors.AsReadOnly();
        public IReadOnlyList<PipelineInitializationIssue> Warnings => this._warnings.AsReadOnly();
        public IReadOnlyList<PipelineInitializationLog> Log => this._log.AsReadOnly();

        internal string CurrentField { get; set; } = "";
        internal string CurrentSection { get; set; } = "Configuration";
        internal void AddError(PipelineInitializationIssue issue) => this._errors.Add(issue);
        internal void AddWarning(PipelineInitializationIssue issue) => this._warnings.Add(issue);
        internal void AddLog(LogSection section, LogLevel level, string message) => this._log.Add(new PipelineInitializationLog(section, level, message));
    }
}
