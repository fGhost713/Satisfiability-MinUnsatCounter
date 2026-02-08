namespace MinUnsatPublish.Infrastructure;

/// <summary>
/// Result from a counting operation.
/// </summary>
public class CountingResult
{
    public long Count { get; set; }
    public long ProcessedCombinations { get; set; }
    public long TotalCombinations { get; set; }
    public bool WasCancelled { get; set; }
    public long ElapsedMs { get; set; }
}
