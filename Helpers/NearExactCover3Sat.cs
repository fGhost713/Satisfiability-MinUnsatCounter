using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace MinUnsatPublish.Helpers
{
    /// <summary>
    /// Near-Exact-Cover counter for c=9 3-SAT MIN-UNSAT (v ≤ 6, 64-bit masks).
    ///
    /// Uses:
    ///   - Bit-parallel disjointness-graph pruning (bitset AND of adjacency rows)
    ///   - Incremental coverage tracking (skip clauses that add no new coverage)
    ///   - Coverage feasibility pruning (remaining clauses can't fill uncovered assignments)
    ///   - Multi-threaded: depth-0 parallelized across all CPU cores
    /// </summary>
    public class NearExactCover3Sat
    {
        private int _n, _v, _stride;
        private ulong _allAssign;
        private ulong[] _fals = [];
        private ulong[] _adj = [];
        private List<int[]> _clauses = [];
        private int _clauseCoverage; // 2^(v-3) assignments per clause
        private Stopwatch _progressSw = new();

        public long Count(int v)
        {
            if (v > 6)
                throw new ArgumentOutOfRangeException(nameof(v),
                    "NearExactCover uses 64-bit assignment masks; v ≤ 6 required.");

            var sw = Stopwatch.StartNew();
            _clauses = GenerateClauses(v);
            _n = _clauses.Count;
            _v = v;
            _clauseCoverage = 1 << (v - 3);
            if (_n < 9) return 0;

            int cores = Environment.ProcessorCount;
            Console.WriteLine($"[NearExact c=9] v={v}, {_n} clauses, {cores} threads");

            _fals = BuildFalsificationMasks(_clauses, v);
            int numAssign = 1 << v;
            _allAssign = numAssign >= 64 ? ulong.MaxValue : (1UL << numAssign) - 1;
            _stride = (_n + 63) / 64;
            _adj = new ulong[_n * _stride];

            for (int i = 0; i < _n; i++)
                for (int j = i + 1; j < _n; j++)
                    if ((_fals[i] & _fals[j]) == 0)
                    {
                        _adj[i * _stride + j / 64] |= 1UL << (j % 64);
                        _adj[j * _stride + i / 64] |= 1UL << (i % 64);
                    }

            // Initial candidate mask (all clauses)
            ulong[] initCand = new ulong[_stride];
            for (int w = 0; w < _stride; w++) initCand[w] = ulong.MaxValue;
            if (_n % 64 != 0) initCand[_stride - 1] &= (1UL << (_n % 64)) - 1;

            // Collect depth-0 candidates
            var depth0List = new List<int>();
            for (int w = 0; w < _stride; w++)
            {
                ulong word = initCand[w];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int idx = w * 64 + bit;
                    if (idx >= _n) break;
                    depth0List.Add(idx);
                    word &= word - 1;
                }
            }

            int totalD0 = depth0List.Count;
            Console.WriteLine($"[NearExact c=9] Dispatching {totalD0} depth-0 items across {cores} threads...");

            long grandTotal = 0;
            int done = 0;
            _progressSw = Stopwatch.StartNew();

            Parallel.For(0, totalD0,
                new ParallelOptions { MaxDegreeOfParallelism = cores },
                () => new int[9],
                (d0Idx, _, selected) =>
                {
                    int c0 = depth0List[d0Idx];
                    selected[0] = c0;

                    ulong[] cand1 = new ulong[_stride];
                    for (int cw = 0; cw < _stride; cw++)
                        cand1[cw] = initCand[cw] & _adj[c0 * _stride + cw];

                    ulong coverage0 = _fals[c0];
                    long subtotal = Recurse(1, c0 + 1, selected, coverage0, cand1);
                    Interlocked.Add(ref grandTotal, subtotal);

                    int progress = Interlocked.Increment(ref done);
                    int reportInterval = Math.Max(1, totalD0 / 20);
                    if (progress % reportInterval == 0 || progress == totalD0)
                    {
                        double pct = 100.0 * progress / totalD0;
                        double elapsed = _progressSw.Elapsed.TotalSeconds;
                        long currentCount = Interlocked.Read(ref grandTotal);
                        string etaStr = "";
                        if (progress > 0 && progress < totalD0)
                        {
                            double remaining = elapsed * (totalD0 - progress) / progress;
                            var eta = TimeSpan.FromSeconds(remaining);
                            etaStr = eta.TotalHours >= 1 ? $", ETA: {(int)eta.TotalHours}h {eta.Minutes}m" :
                                     eta.TotalMinutes >= 1 ? $", ETA: {eta.Minutes}m {eta.Seconds}s" :
                                     $", ETA: {eta.Seconds}s";
                        }
                        Console.WriteLine($"[NearExact c=9] Progress: {progress}/{totalD0} ({pct:F0}%), Elapsed: {elapsed:F1}s, Count: {currentCount:N0}{etaStr}");
                    }

                    return selected;
                },
                _ => { });
            sw.Stop();
            Console.WriteLine($"[NearExact c=9] m(9,{_v}) = {grandTotal} ({sw.Elapsed})");
            return grandTotal;
        }

        private long Recurse(int depth, int startIdx, int[] selected,
            ulong coverage, ulong[] disjCandidates)
        {
            if (depth == 9)
            {
                // UNSAT: all assignments must be covered
                if (coverage != _allAssign) return 0;

                // Minimality: each clause must have at least one private assignment
                for (int i = 0; i < 9; i++)
                {
                    ulong others = 0;
                    for (int j = 0; j < 9; j++)
                        if (j != i) others |= _fals[selected[j]];
                    if ((_fals[selected[i]] & ~others) == 0) return 0;
                }

                // AllVars
                if (!CheckAllVars(selected)) return 0;
                return 1;
            }

            // Coverage feasibility: can remaining clauses cover all uncovered assignments?
            int uncovered = BitOperations.PopCount(_allAssign & ~coverage);
            int remaining = 9 - depth;
            if (uncovered > remaining * _clauseCoverage) return 0;

            long total = 0;

            // === PATH A: Disjoint candidates (bitset-pruned) ===
            int startWord = startIdx / 64;
            int startBit = startIdx % 64;

            for (int w = startWord; w < _stride; w++)
            {
                ulong word = disjCandidates[w];
                if (w == startWord)
                    word &= ~((1UL << startBit) - 1);

                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int i = w * 64 + bit;
                    if (i >= _n) break;

                    // Skip if clause adds no new coverage (would be redundant → not minimal)
                    if ((_fals[i] & ~coverage) == 0) { word &= word - 1; continue; }

                    if (_n - i - 1 < remaining - 1) { word &= word - 1; continue; }

                    ulong[] nextDisj = new ulong[_stride];
                    for (int cw = 0; cw < _stride; cw++)
                        nextDisj[cw] = disjCandidates[cw] & _adj[i * _stride + cw];

                    selected[depth] = i;
                    total += Recurse(depth + 1, i + 1, selected,
                        coverage | _fals[i], nextDisj);

                    word &= word - 1;
                }
            }

            // === PATH B: Non-disjoint candidates ===
            // Clauses that overlap with at least one selected clause.
            // These are essential for c=9 (which requires overcoverage).
            if (depth >= 1)
            {
                for (int i = startIdx; i < _n; i++)
                {
                    if (_n - i - 1 < remaining - 1) break;

                    // Skip if already in disjoint set (handled in Path A)
                    if ((disjCandidates[i / 64] & (1UL << (i % 64))) != 0) continue;

                    // Skip if clause adds no new coverage (would be redundant)
                    if ((_fals[i] & ~coverage) == 0) continue;

                    ulong[] nextDisj = new ulong[_stride];
                    for (int cw = 0; cw < _stride; cw++)
                        nextDisj[cw] = disjCandidates[cw] & _adj[i * _stride + cw];

                    selected[depth] = i;
                    total += Recurse(depth + 1, i + 1, selected,
                        coverage | _fals[i], nextDisj);
                }
            }

            return total;
        }

        private bool CheckAllVars(int[] indices)
        {
            int varsFound = 0;
            foreach (int idx in indices)
            {
                int[] c = _clauses[idx];
                varsFound |= (1 << (c[0] / 2)) | (1 << (c[1] / 2)) | (1 << (c[2] / 2));
            }
            return varsFound == ((1 << _v) - 1);
        }

        private static ulong[] BuildFalsificationMasks(List<int[]> clauses, int v)
        {
            ulong[] masks = new ulong[clauses.Count];
            int totalAssignments = 1 << v;
            for (int ci = 0; ci < clauses.Count; ci++)
            {
                int[] clause = clauses[ci];
                int var0 = clause[0] / 2, neg0 = clause[0] & 1;
                int var1 = clause[1] / 2, neg1 = clause[1] & 1;
                int var2 = clause[2] / 2, neg2 = clause[2] & 1;
                ulong mask = 0;
                for (int a = 0; a < totalAssignments; a++)
                {
                    if (((a >> var0) & 1) == neg0 &&
                        ((a >> var1) & 1) == neg1 &&
                        ((a >> var2) & 1) == neg2)
                        mask |= 1UL << a;
                }
                masks[ci] = mask;
            }
            return masks;
        }

        private static List<int[]> GenerateClauses(int v)
        {
            var result = new List<int[]>();
            for (int i = 0; i < v; i++)
                for (int j = i + 1; j < v; j++)
                    for (int k = j + 1; k < v; k++)
                        for (int p = 0; p < 8; p++)
                        {
                            int l1 = (p & 1) == 0 ? 2 * i : 2 * i + 1;
                            int l2 = (p & 2) == 0 ? 2 * j : 2 * j + 1;
                            int l3 = (p & 4) == 0 ? 2 * k : 2 * k + 1;
                            result.Add([l1, l2, l3]);
                        }
            return result;
        }
    }
}
