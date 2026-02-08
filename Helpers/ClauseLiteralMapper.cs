namespace MinUnsatPublish.Helpers;

/// <summary>
/// Maps clause indices to their literal components for GPU processing.
/// Provides flat array format suitable for GPU kernels.
/// </summary>
public static class ClauseLiteralMapper
{
    /// <summary>
    /// Build a flat array mapping each clause to its literals.
    /// Format: clauseIdx * k * 2 + litIdx * 2 + 0 = varNo, + 1 = polarity (1=positive, 0=negative)
    /// </summary>
    /// <param name="n">Number of variables</param>
    /// <param name="k">Literals per clause</param>
    /// <returns>Flat int array for GPU, and the number of clauses</returns>
    public static (int[] flatMap, int numClauses) BuildClauseLiteralMap(int n, int k)
    {
        var literals = new List<(int varNo, bool pos)>();
        for (int v = 0; v < n; v++)
        {
            literals.Add((v, true));
            literals.Add((v, false));
        }

        var clauseLiteralsList = new List<List<(int varNo, bool pos)>>();
        var comb = new List<(int varNo, bool pos)>(k);
        
        void Backtrack(int start)
        {
            if (comb.Count == k)
            {
                clauseLiteralsList.Add(new List<(int varNo, bool pos)>(comb));
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

        int numClauses = clauseLiteralsList.Count;
        
        // Build flat array: clauseIdx * k * 2 + litIdx * 2 + 0=varNo, 1=polarity
        int[] flatMap = new int[numClauses * k * 2];
        
        for (int clauseIdx = 0; clauseIdx < numClauses; clauseIdx++)
        {
            var clause = clauseLiteralsList[clauseIdx];
            // Sort by variable number for consistent ordering
            var sortedClause = clause.OrderBy(l => l.varNo).ToList();
            
            for (int litIdx = 0; litIdx < k; litIdx++)
            {
                int baseIdx = clauseIdx * k * 2 + litIdx * 2;
                flatMap[baseIdx] = sortedClause[litIdx].varNo;
                flatMap[baseIdx + 1] = sortedClause[litIdx].pos ? 1 : 0;
            }
        }
        
        return (flatMap, numClauses);
    }

    /// <summary>
    /// Build a bitmask for each clause indicating which variables it covers.
    /// clauseVarMasks[clauseIdx] has bit v set if clause uses variable v.
    /// </summary>
    public static int[] BuildClauseVariableMasks(int n, int k)
    {
        var literals = new List<(int varNo, bool pos)>();
        for (int v = 0; v < n; v++)
        {
            literals.Add((v, true));
            literals.Add((v, false));
        }

        var clauseMasks = new List<int>();
        var comb = new List<(int varNo, bool pos)>(k);
        
        void Backtrack(int start)
        {
            if (comb.Count == k)
            {
                // Build bitmask of variables used in this clause
                int mask = 0;
                foreach (var (varNo, _) in comb)
                {
                    mask |= (1 << varNo);
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
}
