using System.Text.Json;

namespace MinUnsatPublish.Infrastructure;

/// <summary>
/// Manages saving and loading calculation progress for resumable operations.
/// </summary>
public class CalculationCheckpoint
{
    private const string DefaultCheckpointFolder = "Checkpoints";

    /// <summary>
    /// Custom checkpoint folder (used for testing).
    /// </summary>
    public static string? CustomCheckpointFolder { get; set; }

    private static string CheckpointFolder => CustomCheckpointFolder ?? DefaultCheckpointFolder;

    /// <summary>
    /// Parameters that identify a unique calculation.
    /// </summary>
    public int NumVariables { get; set; }
    public int LiteralsPerClause { get; set; }
    public int NumClauses { get; set; }

    /// <summary>
    /// Progress state.
    /// </summary>
    public long ProcessedCombinations { get; set; }
    public long TotalCombinations { get; set; }
    public long CurrentCount { get; set; }
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Elapsed time in milliseconds before checkpoint was saved (for accurate ETA on resume).
    /// </summary>
    public long ElapsedMsBeforeCheckpoint { get; set; }

    /// <summary>
    /// Generate a unique filename for this calculation's checkpoint.
    /// </summary>
    public string GetCheckpointFilename()
    {
        return Path.Combine(CheckpointFolder, $"checkpoint_v{NumVariables}_l{LiteralsPerClause}_c{NumClauses}.json");
    }

    /// <summary>
    /// Save checkpoint to file.
    /// </summary>
    public void Save()
    {
        if (!Directory.Exists(CheckpointFolder))
        {
            Directory.CreateDirectory(CheckpointFolder);
        }

        LastUpdated = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetCheckpointFilename(), json);
    }

    /// <summary>
    /// Try to load a checkpoint for the given parameters.
    /// </summary>
    public static CalculationCheckpoint? TryLoad(int numVariables, int literalsPerClause, int numClauses)
    {
        var checkpoint = new CalculationCheckpoint
        {
            NumVariables = numVariables,
            LiteralsPerClause = literalsPerClause,
            NumClauses = numClauses
        };

        string filename = checkpoint.GetCheckpointFilename();
        return TryLoadFromFile(filename);
    }

    private static CalculationCheckpoint? TryLoadFromFile(string filename)
    {
        if (!File.Exists(filename))
            return null;

        try
        {
            string json = File.ReadAllText(filename);
            return JsonSerializer.Deserialize<CalculationCheckpoint>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete the checkpoint file.
    /// </summary>
    public void Delete()
    {
        string filename = GetCheckpointFilename();
        if (File.Exists(filename))
        {
            File.Delete(filename);
        }
    }

    /// <summary>
    /// Check if parameters match.
    /// </summary>
    public bool MatchesParameters(int numVariables, int literalsPerClause, int numClauses)
    {
        return NumVariables == numVariables &&
               LiteralsPerClause == literalsPerClause &&
               NumClauses == numClauses;
    }
}
