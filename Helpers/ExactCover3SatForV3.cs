using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Numerics;

namespace MinUnsatPublish.Helpers
{
    /// <summary>
    /// Exact Cover 3-SAT Counter.
    /// Used when c=8 and l=3 to find 8-cliques in disjointness graph.    
    /// </summary>
    public class ExactCover3SatForV3
    {
        public long Count(int v)
        {
            // 1. Generate clauses
            var allClauses = GenerateClauses(v);
            int n = allClauses.Count;
            int c = 8;
            
            if (n < c) return 0;

            // 2. Build Adjacency
            ulong[] adjacency = BuildAdjacency(allClauses, v);

            // 3. Count Cliques
            return CountCliques(n, c, adjacency, allClauses, v);
        }

        private List<int[]> GenerateClauses(int v)
        {
            var result = new List<int[]>();
            // Iterate all combinations of 3 distinct variables
            for (int i = 0; i < v; i++)
            {
                for (int j = i + 1; j < v; j++)
                {
                    for (int k = j + 1; k < v; k++)
                    {
                        // For each variable set, iterate all 2^3 = 8 polarities
                        for (int p = 0; p < 8; p++)
                        {
                            int l1 = (p & 1) == 0 ? 2 * i : 2 * i + 1;
                            int l2 = (p & 2) == 0 ? 2 * j : 2 * j + 1;
                            int l3 = (p & 4) == 0 ? 2 * k : 2 * k + 1;
                            result.Add(new int[] { l1, l2, l3 });
                        }
                    }
                }
            }
            return result;
        }

        private bool AreDisjoint(int[] c1, int[] c2)
        {
            foreach (int l1 in c1)
            {
                int comp = l1 ^ 1;
                foreach (int l2 in c2)
                {
                    if (l2 == comp) return true;
                }
            }
            return false;
        }

        private ulong[] BuildAdjacency(List<int[]> clauses, int v)
        {
            int n = clauses.Count;
            int stride = (n + 63) / 64;
            ulong[] adj = new ulong[n * stride];

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (AreDisjoint(clauses[i], clauses[j]))
                    {
                        // Set bit j in row i
                        int wordIdx = i * stride + (j / 64);
                        adj[wordIdx] |= (1UL << (j % 64));
                        
                        // Set bit i in row j
                        int wordIdx2 = j * stride + (i / 64);
                        adj[wordIdx2] |= (1UL << (i % 64));
                    }
                }
            }
            return adj;
        }

        private long CountCliques(int n, int k, ulong[] adj, List<int[]> clauses, int v)
        {
            int stride = (n + 63) / 64;
            ulong[] initialCandidates = new ulong[stride];
            for (int i = 0; i < stride; i++) initialCandidates[i] = ~0UL;
            
            // Mask out non-existent bits
            int excess = stride * 64 - n;
            if (excess > 0)
            {
               // Initial candidates are fully set, loop bounds handle n check usually.
            }

            return Recurse(0, 0, k, initialCandidates, stride, adj, clauses, v, new int[k]);
        }

        private long Recurse(int currentSize, int startIndex, int targetSize, ulong[] candidates, int stride, ulong[] adj, List<int[]> clauses, int v, int[] currentCliqueIndices)
        {
            if (currentSize == targetSize)
            {
                if (CheckAllVars(currentCliqueIndices, clauses, v)) return 1;
                return 0;
            }

            long total = 0;
            int n = clauses.Count;

            int startWord = startIndex / 64;
            int startBit = startIndex % 64;

            for (int w = startWord; w < stride; w++)
            {
                ulong word = candidates[w];
                
                if (w == startWord)
                {
                   word &= ~((1UL << startBit) - 1);
                }

                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int i = w * 64 + bit;

                    if (i >= n) break; 

                    ulong[] newCandidates = new ulong[stride];
                    int bitsRemaining = 0;
                    
                    for (int candW = w; candW < stride; candW++)
                    {
                        ulong mask = candidates[candW] & adj[i * stride + candW];
                        newCandidates[candW] = mask;

                        if (candW > w)
                        {
                            bitsRemaining += BitOperations.PopCount(mask);
                        }
                        else 
                        {
                            ulong suffixMask = ~((1UL << (bit + 1)) - 1);
                            bitsRemaining += BitOperations.PopCount(mask & suffixMask);
                        }
                    }

                    if (currentSize == 0)
                    {
                        // Simple progress reporting for top level
                       if (i % 10 == 0) Console.Write($"\r[V3 Exact] Progress: {i}/{n} ({100.0 * i / n:F0}%)...");
                    }

                    if (bitsRemaining >= (targetSize - currentSize - 1))
                    {
                        currentCliqueIndices[currentSize] = i;
                        total += Recurse(currentSize + 1, i + 1, targetSize, newCandidates, stride, adj, clauses, v, currentCliqueIndices);
                    }

                    // Clear bit
                    word &= ~(1UL << bit);
                }
            }
            if (currentSize == 0) Console.Write("\r" + new string(' ', 50) + "\r"); // Clear line
            return total;
        }

        private bool CheckAllVars(int[] indices, List<int[]> clauses, int v)
        {
            int varsFound = 0; 
            for (int i = 0; i < indices.Length; i++)
            {
                int[] c = clauses[indices[i]];
                varsFound |= (1 << (c[0] / 2));
                varsFound |= (1 << (c[1] / 2));
                varsFound |= (1 << (c[2] / 2));
            }
            return varsFound == ((1 << v) - 1);
        }
    }
}
