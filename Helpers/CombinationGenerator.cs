namespace MinUnsatPublish.Helpers;

/// <summary>
/// Handles generation of clause combinations for MIN-UNSAT enumeration.
/// </summary>
public static class CombinationGenerator
{
    /// <summary>
    /// Count total combinations C(n,k) without generating them.
    /// </summary>
    public static long CountCombinations(int n, int k)
    {
        if (k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;
        
        long result = 1;
        for (int i = 0; i < k; i++)
        {
            result = result * (n - i) / (i + 1);
        }
        return result;
    }
}
