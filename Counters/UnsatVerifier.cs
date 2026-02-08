using System.Diagnostics;
using MinUnsatPublish.Helpers;

namespace MinUnsatPublish.Counters;

/// <summary>
/// Simple reference UNSAT counter for verification.
/// No optimizations - just straightforward enumeration.
/// Used to verify optimized implementations.
/// </summary>
public static class UnsatVerifier
{
    /// <summary>
    /// Count UNSAT formulas using simple brute-force.
    /// This is slow but guaranteed correct.
    /// </summary>
    public static long CountSimple(int numVariables, int literalsPerClause, int numClauses, bool verbose = true)
    {
        var (_, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        ulong[] clauseMasks = ClauseMaskBuilder.BuildClauseMasks(numVariables, literalsPerClause);
        
        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMask = numAssignments >= 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;
        
        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        
        if (verbose)
        {
            Console.WriteLine($"[Verifier] v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[Verifier] Total clauses: {totalClauses}");
            Console.WriteLine($"[Verifier] Total combinations: {totalCombinations:N0}");
            Console.WriteLine($"[Verifier] allAssignmentsMask: 0x{allAssignmentsMask:X}");
        }
        
        var sw = Stopwatch.StartNew();
        
        int[] indices = new int[numClauses];
        for (int i = 0; i < numClauses; i++)
            indices[i] = i;
        
        long unsatCount = 0;
        long processed = 0;
        
        while (true)
        {
            // Simple UNSAT check: OR all clause masks
            ulong combined = 0;
            for (int i = 0; i < numClauses; i++)
            {
                combined |= clauseMasks[indices[i]];
            }
            
            if (combined == allAssignmentsMask)
                unsatCount++;
            
            processed++;
            
            // Generate next combination
            if (!NextCombination(indices, numClauses, totalClauses))
                break;
        }
        
        sw.Stop();
        
        if (verbose)
        {
            Console.WriteLine($"[Verifier] Processed: {processed:N0}");
            Console.WriteLine($"[Verifier] UNSAT count: {unsatCount:N0}");
            Console.WriteLine($"[Verifier] Time: {sw.Elapsed.TotalSeconds:F2}s");
        }
        
        return unsatCount;
    }
    
    private static bool NextCombination(int[] indices, int k, int n)
    {
        int i = k - 1;
        while (i >= 0 && indices[i] == n - k + i)
            i--;
        if (i < 0) return false;
        indices[i]++;
        for (int j = i + 1; j < k; j++)
            indices[j] = indices[j - 1] + 1;
        return true;
    }
}
