namespace MinUnsatPublish.Helpers;

/// <summary>
/// Builds clause bitmasks for MIN-UNSAT checking.
/// Each clause is represented as a bitmask where bit i is set if assignment i falsifies the clause.
/// </summary>
public static class ClauseMaskBuilder
{
    private const int MaxN = 10;

    /// <summary>
    /// Calculate number of ulongs needed for n variables.
    /// </summary>
    public static int GetNumUlongs(int n)
    {
        int numAssignments = 1 << n;
        return (numAssignments + 63) / 64;
    }

    /// <summary>
    /// Build clause masks for n <= 10 (single ulong version for n<=6, multi-ulong flattened for n>6).
    /// For n<=6: returns one ulong per clause.
    /// For n>6: returns numUlongs ulongs per clause (flattened).
    /// </summary>
    public static ulong[] BuildClauseMasks(int numVariables, int literalsPerClause)
    {
        if (numVariables <= 6)
        {
            return BuildClauseMasksSingleUlong(numVariables, literalsPerClause);
        }
        else if (numVariables <= 10)
        {
            return BuildClauseMasksMultiUlong(numVariables, literalsPerClause);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(numVariables),
                "Limited to n <= 10.");
        }
    }

    /// <summary>
    /// Build clause masks for n <= 6 (single ulong version).
    /// </summary>
    private static ulong[] BuildClauseMasksSingleUlong(int numVariables, int literalsPerClause)
    {
        var literals = new List<(int varNo, bool pos)>();
        for (int v = 0; v < numVariables; v++)
        {
            literals.Add((v, true));
            literals.Add((v, false));
        }

        var clauseMasks = new List<ulong>();
        var comb = new List<(int varNo, bool pos)>(literalsPerClause);
        
        void Backtrack(int start)
        {
            if (comb.Count == literalsPerClause)
            {
                ulong mask = 0;
                int maxAssignments = 1 << numVariables;
                
                for (int a = 0; a < maxAssignments; a++)
                {
                    bool sat = false;
                    foreach (var (v, pos) in comb)
                    {
                        bool litValue = ((a >> v) & 1) == 1;
                        bool litIsTrue = pos ? litValue : !litValue;
                        if (litIsTrue) { sat = true; break; }
                    }
                    if (!sat)
                    {
                        mask |= 1UL << a;
                    }
                }
                clauseMasks.Add(mask);
                return;
            }

            for (int i = start; i < literals.Count; i++)
            {
                if (comb.Any(l => l.varNo == literals[i].varNo)) continue;
                comb.Add(literals[i]);
                Backtrack(i + 1);
                comb.RemoveAt(comb.Count - 1);
            }
        }

        Backtrack(0);
        return clauseMasks.ToArray();
    }

    /// <summary>
    /// Build every k-literal clause on n variables (multi-ulong version for n > 6).
    /// Returns flattened array: each clause uses numUlongs consecutive ulongs.
    /// </summary>
    public static ulong[] BuildClauseMasksMultiUlong(int n, int k)
    {
        if (n > MaxN)
            throw new ArgumentOutOfRangeException(nameof(n),
                $"GPU version limited to n <= {MaxN}.");

        int numUlongs = GetNumUlongs(n);
        int numAssignments = 1 << n;

        var literals = new List<(int varNo, bool pos)>();
        for (int v = 0; v < n; v++)
        {
            literals.Add((v, true));
            literals.Add((v, false));
        }

        var clauseMasksList = new List<ulong[]>();
        var comb = new List<(int varNo, bool pos)>(k);
        
        void Backtrack(int start)
        {
            if (comb.Count == k)
            {
                ulong[] mask = new ulong[numUlongs];
                
                for (int a = 0; a < numAssignments; a++)
                {
                    bool sat = false;
                    foreach (var (v, pos) in comb)
                    {
                        bool litValue = ((a >> v) & 1) == 1;
                        bool litIsTrue = pos ? litValue : !litValue;
                        if (litIsTrue) { sat = true; break; }
                    }
                    if (!sat)
                    {
                        int ulongIdx = a / 64;
                        int bitIdx = a % 64;
                        mask[ulongIdx] |= 1UL << bitIdx;
                    }
                }
                clauseMasksList.Add(mask);
                return;
            }

            for (int i = start; i < literals.Count; i++)
            {
                if (comb.Any(l => l.varNo == literals[i].varNo)) continue;
                comb.Add(literals[i]);
                Backtrack(i + 1);
                comb.RemoveAt(comb.Count - 1);
            }
        }

        Backtrack(0);

        ulong[] result = new ulong[clauseMasksList.Count * numUlongs];
        for (int i = 0; i < clauseMasksList.Count; i++)
        {
            for (int j = 0; j < numUlongs; j++)
            {
                result[i * numUlongs + j] = clauseMasksList[i][j];
            }
        }
        return result;
    }
}
