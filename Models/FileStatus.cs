namespace MergeLanguageTracks
{
    /// <summary>
    /// Stato di elaborazione di un file nel pipeline
    /// </summary>
    public enum FileStatus
    {
        Pending,
        Analyzing,
        Analyzed,
        Processing,
        Done,
        Error,
        Skipped
    }
}
