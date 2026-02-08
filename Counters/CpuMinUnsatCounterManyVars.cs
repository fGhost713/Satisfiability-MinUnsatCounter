using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// CPU MIN-UNSAT counter for larger variable counts (n > 6, up to 10).
/// 
/// Uses multi-ulong bitmasks for fast bitwise operations:
/// - n=7: 128 assignments = 2 ulongs
/// - n=8: 256 assignments = 4 ulongs  
/// - n=9: 512 assignments = 8 ulongs
/// - n=10: 1024 assignments = 16 ulongs
/// 
/// This is optimized but still slower than the n?6 version due to more memory operations.
/// </summary>
public class CpuMinUnsatCounterManyVars
{
    private const int MaxVariables = 10; // 2^10 = 1024 assignments = 16 ulongs
    
    public string AcceleratorName => $"CPU ManyVars ({Environment.ProcessorCount} cores)";

    public long Count(int numVariables, int numClauses, bool verbose = true)
    {
        return Count(numVariables, 2, numClauses, verbose);
    }

    public long Count(int numVariables, int literalsPerClause, int numClauses, bool verbose = true)
    {
        var result = CountInternal(numVariables, literalsPerClause, numClauses, CancellationToken.None, verbose);
        return result.Count;
    }

    public CountingResult CountCancellable(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose = true, bool useCheckpoint = false)
    {
        return CountInternal(numVariables, literalsPerClause, numClauses, ct, verbose, useCheckpoint);
    }

    private CountingResult CountInternal(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose, bool useCheckpoint = false)
    {
        var result = new CountingResult();

        if (numVariables > MaxVariables)
            throw new ArgumentOutOfRangeException(nameof(numVariables), 
                $"Variables limited to {MaxVariables} (2^{MaxVariables} = {1 << MaxVariables} assignments).");
        if (numVariables < 2)
            throw new ArgumentOutOfRangeException(nameof(numVariables), "Need at least 2 variables");
        if (numClauses < 1)
            throw new ArgumentOutOfRangeException(nameof(numClauses), "Need at least 1 clause");
        if (literalsPerClause < 2 || literalsPerClause > 3)
            throw new ArgumentOutOfRangeException(nameof(literalsPerClause), "Literals per clause must be 2 or 3");

        int numThreads = Environment.ProcessorCount;
        if (verbose) Console.WriteLine($"[CpuManyVars] Using {numThreads} CPU cores");

        // Build lookup tables
        var (flatLits, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        int[] clauseVarMasks = ClauseLiteralMapper.BuildClauseVariableMasks(numVariables, literalsPerClause);
        
        int numAssignments = 1 << numVariables;
        int numMaskWords = (numAssignments + 63) / 64;
        int allVariablesMask = (1 << numVariables) - 1;

        // Build clause masks as flat array of ulongs [totalClauses * numMaskWords]
        ulong[] clauseMasks = new ulong[totalClauses * numMaskWords];
        for (int c = 0; c < totalClauses; c++)
        {
            int baseIdx = c * literalsPerClause * 2;
            
            for (int a = 0; a < numAssignments; a++)
            {
                bool clauseFalse = true;
                for (int lit = 0; lit < literalsPerClause && clauseFalse; lit++)
                {
                    int varNo = flatLits[baseIdx + lit * 2];
                    int polarity = flatLits[baseIdx + lit * 2 + 1];
                    bool varValue = ((a >> varNo) & 1) == 1;
                    bool litValue = polarity == 1 ? varValue : !varValue;
                    if (litValue) clauseFalse = false;
                }
                
                if (clauseFalse)
                {
                    int wordIdx = a / 64;
                    int bitIdx = a % 64;
                    clauseMasks[c * numMaskWords + wordIdx] |= 1UL << bitIdx;
                }
            }
        }

        // Build polarity data for canonical form
        int[] clausePolPos = new int[totalClauses]; // bits per variable if has positive
        int[] clausePolNeg = new int[totalClauses]; // bits per variable if has negative
        for (int c = 0; c < totalClauses; c++)
        {
            int baseIdx = c * literalsPerClause * 2;
            for (int lit = 0; lit < literalsPerClause; lit++)
            {
                int varNo = flatLits[baseIdx + lit * 2];
                int polarity = flatLits[baseIdx + lit * 2 + 1];
                if (polarity == 1)
                    clausePolPos[c] |= 1 << varNo;
                else
                    clausePolNeg[c] |= 1 << varNo;
            }
        }

        // Build "all ones" mask for UNSAT check
        ulong[] allOnesMask = new ulong[numMaskWords];
        for (int w = 0; w < numMaskWords; w++)
        {
            int bitsInWord = Math.Min(64, numAssignments - w * 64);
            allOnesMask[w] = bitsInWord == 64 ? ulong.MaxValue : (1UL << bitsInWord) - 1;
        }

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (verbose)
        {
            Console.WriteLine($"[CpuManyVars] Configuration: v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[CpuManyVars] Total clause types: {totalClauses}");
            Console.WriteLine($"[CpuManyVars] Total combinations: {totalCombinations:N0}");
            Console.WriteLine($"[CpuManyVars] Mask words per clause: {numMaskWords}");
        }

        var sw = Stopwatch.StartNew();
        long totalCount = 0;

        // Calculate work distribution
        long combinationsPerThread = (totalCombinations + numThreads - 1) / numThreads;
        long[] threadCounts = new long[numThreads];
        long[] threadProcessed = new long[numThreads];
        
        // Progress reporting
        long sharedProcessed = 0;
        var progressSw = Stopwatch.StartNew();
        object progressLock = new object();

        Parallel.For(0, numThreads, threadId =>
        {
            long startIdx = threadId * combinationsPerThread;
            long endIdx = Math.Min(startIdx + combinationsPerThread, totalCombinations);
            if (startIdx >= totalCombinations) return;

            // Thread-local arrays - stack allocated for small sizes, heap for larger
            int[] indices = new int[numClauses];
            ulong[] one = new ulong[numMaskWords];
            ulong[] two = new ulong[numMaskWords];
            int[] posCounts = new int[numVariables];
            int[] negCounts = new int[numVariables];
            
            long localCount = 0;
            long localProcessed = 0;
            long lastReported = 0;
            const long reportInterval = 500_000;

            // Unrank starting combination
            UnrankCombination(startIdx, totalClauses, numClauses, indices);

            for (long combIdx = startIdx; combIdx < endIdx; combIdx++)
            {
                if (ct.IsCancellationRequested) break;

                // Check all variables used (fast bitmask check)
                int varMask = 0;
                for (int k = 0; k < numClauses; k++)
                    varMask |= clauseVarMasks[indices[k]];

                if (varMask == allVariablesMask)
                {
                    // Clear masks
                    Array.Clear(one);
                    Array.Clear(two);

                    // Accumulate clause masks with one/two tracking
                    for (int k = 0; k < numClauses; k++)
                    {
                        int clauseOffset = indices[k] * numMaskWords;
                        for (int w = 0; w < numMaskWords; w++)
                        {
                            ulong m = clauseMasks[clauseOffset + w];
                            two[w] |= one[w] & m;
                            one[w] |= m;
                        }
                    }

                    // Check UNSAT (all assignments covered)
                    bool isUnsat = true;
                    for (int w = 0; w < numMaskWords; w++)
                    {
                        if (one[w] != allOnesMask[w])
                        {
                            isUnsat = false;
                            break;
                        }
                    }

                    if (isUnsat)
                    {
                        // Check minimality: each clause must have unique coverage
                        bool isMinimal = true;
                        for (int k = 0; k < numClauses && isMinimal; k++)
                        {
                            int clauseOffset = indices[k] * numMaskWords;
                            bool hasUnique = false;
                            
                            for (int w = 0; w < numMaskWords; w++)
                            {
                                ulong clauseMask = clauseMasks[clauseOffset + w];
                                ulong unique = one[w] & ~two[w];
                                if ((clauseMask & unique) != 0)
                                {
                                    hasUnique = true;
                                    break;
                                }
                            }
                            
                            if (!hasUnique) isMinimal = false;
                        }

                        if (isMinimal)
                        {
                            // Check canonical form and compute orbit size
                            Array.Clear(posCounts);
                            Array.Clear(negCounts);
                            
                            for (int k = 0; k < numClauses; k++)
                            {
                                int idx = indices[k];
                                int pos = clausePolPos[idx];
                                int neg = clausePolNeg[idx];
                                for (int v = 0; v < numVariables; v++)
                                {
                                    if ((pos & (1 << v)) != 0) posCounts[v]++;
                                    if ((neg & (1 << v)) != 0) negCounts[v]++;
                                }
                            }

                            bool isCanonical = true;
                            int stabilizer = 0;
                            for (int v = 0; v < numVariables; v++)
                            {
                                if (posCounts[v] < negCounts[v])
                                {
                                    isCanonical = false;
                                    break;
                                }
                                if (posCounts[v] == negCounts[v])
                                    stabilizer++;
                            }

                            if (isCanonical)
                            {
                                localCount += 1 << (numVariables - stabilizer);
                            }
                        }
                    }
                }

                localProcessed++;

                // Periodic progress update
                if (localProcessed - lastReported >= reportInterval)
                {
                    Interlocked.Add(ref sharedProcessed, localProcessed - lastReported);
                    lastReported = localProcessed;
                    
                    if (threadId == 0 && verbose && progressSw.Elapsed.TotalSeconds >= 5)
                    {
                        lock (progressLock)
                        {
                            if (progressSw.Elapsed.TotalSeconds >= 5)
                            {
                                long currentProcessed = Interlocked.Read(ref sharedProcessed);
                                double progress = 100.0 * currentProcessed / totalCombinations;
                                double rate = currentProcessed / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                                long remaining = totalCombinations - currentProcessed;
                                string etaStr = "";
                                if (rate > 0 && remaining > 0)
                                {
                                    var eta = TimeSpan.FromSeconds(remaining / rate);
                                    etaStr = eta.TotalHours >= 1 ? $", ETA: {(int)eta.TotalHours}h {eta.Minutes}m" :
                                             eta.TotalMinutes >= 1 ? $", ETA: {eta.Minutes}m {eta.Seconds}s" :
                                             $", ETA: {eta.Seconds}s";
                                }
                                Console.WriteLine($"[CpuManyVars] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                                progressSw.Restart();
                            }
                        }
                    }
                }

                // Move to next combination
                if (combIdx + 1 < endIdx)
                    NextCombination(indices, numClauses, totalClauses);
            }

            // Final update
            Interlocked.Add(ref sharedProcessed, localProcessed - lastReported);
            threadCounts[threadId] = localCount;
            threadProcessed[threadId] = localProcessed;
        });

        long processedCombinations = 0;
        for (int i = 0; i < numThreads; i++)
        {
            totalCount += threadCounts[i];
            processedCombinations += threadProcessed[i];
        }

        sw.Stop();

        if (ct.IsCancellationRequested)
        {
            result.Count = totalCount;
            result.WasCancelled = true;
            result.ProcessedCombinations = processedCombinations;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        if (verbose)
        {
            Console.WriteLine($"\n=== Results (CpuManyVars) ===");
            Console.WriteLine($"Time: {sw.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"Count: {totalCount:N0}");
            Console.WriteLine($"Rate: {totalCombinations / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0}/s");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = totalCombinations;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    private static void UnrankCombination(long index, int n, int k, int[] result)
    {
        long remaining = index;
        int current = 0;

        for (int pos = 0; pos < k; pos++)
        {
            int element = current;
            while (true)
            {
                int n_remaining = n - element - 1;
                int k_remaining = k - pos - 1;

                long count = 0;
                if (k_remaining >= 0 && k_remaining <= n_remaining)
                    count = CombinationGenerator.CountCombinations(n_remaining, k_remaining);

                if (remaining < count)
                {
                    result[pos] = element;
                    current = element + 1;
                    break;
                }

                remaining -= count;
                element++;
                if (element >= n) break;
            }
        }
    }
}
