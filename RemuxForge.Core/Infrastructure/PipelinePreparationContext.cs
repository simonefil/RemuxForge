using RemuxForge.Core.Models;
using System;
using System.Threading;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>Contesto opt-in di preparazione, locale all'ExecutionContext: non cambia i callback globali.</summary>
    internal sealed class PipelinePreparationContext : IDisposable
    {
        private static readonly AsyncLocal<PipelinePreparationContext> s_current = new AsyncLocal<PipelinePreparationContext>();
        private readonly PipelinePreparationContext _previous;
        internal static PipelinePreparationContext Current => s_current.Value;
        internal Action<LogSection, LogLevel, string> Log { get; }
        internal CancellationToken Cancellation { get; }

        internal PipelinePreparationContext(Action<LogSection, LogLevel, string> log, CancellationToken cancellation)
        {
            this._previous = s_current.Value;
            this.Log = log;
            this.Cancellation = cancellation;
            s_current.Value = this;
        }

        public void Dispose() { s_current.Value = this._previous; }
    }
}
