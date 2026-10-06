using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    public sealed record RemuxApplyResult(bool Success, bool ScanRequired, IReadOnlyList<PipelineInitializationIssue> Errors,
        IReadOnlyList<PipelineInitializationIssue> Warnings);
}
