using System.Diagnostics;
using System.Runtime.CompilerServices;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// Corrected CPU UNSAT counter.
/// Re-implemented with simpler logic to ensure correctness while maintaining performance.
/// </summary>
public class CpuUnsatCounterOptimized
{
    public string AcceleratorName => $"CPU UNSAT Optimized ({Environment.ProcessorCount} cores)";

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
        CancellationToken ct, bool verbose = true)
    {
        return CountInternal(numVariables, literalsPerClause, numClauses, ct, verbose);
    }

    private CountingResult CountInternal(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose)
    {
        var result = new CountingResult();

        int numThreads = Environment.ProcessorCount;
        if (verbose) Console.WriteLine($"[CpuUnsatOptimized] Using {numThreads} CPU cores");

        // Build lookup tables
        var (_, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        ulong[] clauseMasks = ClauseMaskBuilder.BuildClauseMasks(numVariables, literalsPerClause);

        if (verbose) Console.WriteLine($"[CpuUnsatOptimized] Total clause types: {totalClauses}");

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (numClauses > totalClauses)
        {
            result.ProcessedCombinations = result.TotalCombinations;
            return result;
        }

        if (verbose) Console.WriteLine($"[CpuUnsatOptimized] Total combinations: {totalCombinations:N0}");

        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMask = numAssignments >= 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;

        var sw = Stopwatch.StartNew();
        var progressSw = Stopwatch.StartNew();

        // Partition work across threads
        long combinationsPerThread = (totalCombinations + numThreads - 1) / numThreads;
        
        var threads = new Thread[numThreads];
        var threadResults = new long[numThreads];
        var threadProcessed = new long[numThreads];
        bool cancelled = false;

        for (int t = 0; t < numThreads; t++)
        {
            int threadId = t;
            long startIndex = threadId * combinationsPerThread;
            long endIndex = Math.Min(startIndex + combinationsPerThread, totalCombinations);

            threads[t] = new Thread(() =>
            {
                if (startIndex >= endIndex) return;

                int[] indices = new int[numClauses];
                UnrankCombination(startIndex, totalClauses, numClauses, indices);

                long localCount = 0;
                long localProcessed = 0;
                long rangeSize = endIndex - startIndex;

                localCount = ProcessRangeSafe(
                    indices, rangeSize, numClauses, totalClauses,
                    clauseMasks, allAssignmentsMask,
                    ref localProcessed, ref cancelled, ct,
                    threadProcessed, threadId);

                threadResults[threadId] = localCount;
                Volatile.Write(ref threadProcessed[threadId], localProcessed);
            });

            threads[t].Priority = ThreadPriority.Highest;
            threads[t].Start();
        }

        // Progress monitoring
        while (true)
        {
            bool allDone = true;
            for (int t = 0; t < numThreads; t++)
                if (threads[t].IsAlive) { allDone = false; break; }

            if (allDone) break;

            if (verbose && progressSw.Elapsed.TotalSeconds >= 10)
            {
                long currentProcessed = 0;
                for (int t = 0; t < numThreads; t++)
                    currentProcessed += Volatile.Read(ref threadProcessed[t]);

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
                Console.WriteLine($"[CpuUnsatOptimized] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }

            Thread.Sleep(100);
        }

        // Collect results
        long totalCount = 0;
        long processedCount = 0;
        for (int t = 0; t < numThreads; t++)
        {
            threads[t].Join();
            totalCount += threadResults[t];
            processedCount += threadProcessed[t];
        }

        sw.Stop();

        if (cancelled || ct.IsCancellationRequested)
        {
            if (verbose) Console.WriteLine($"\n[CpuUnsatOptimized] Cancelled");
            result.Count = totalCount;
            result.ProcessedCombinations = processedCount;
            result.WasCancelled = true;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        if (verbose)
        {
            Console.WriteLine($"\n=== UNSAT Results (Optimized) ===");
            Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Enumerated: {processedCount:N0}");
            Console.WriteLine($"UNSAT count: {totalCount:N0}");
            Console.WriteLine($"Rate: {processedCount / sw.Elapsed.TotalSeconds:N0}/s");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = processedCount;
        result.WasCancelled = false;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Optimized processing loop using fully unrolled recalculation.
    /// Guarantees correctness by rebuilding mask every time, but uses aggressive unrolling for speed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe long ProcessRangeSafe(
        int[] indices, long rangeSize, int numClauses, int totalClauses,
        ulong[] clauseMasks, ulong allAssignmentsMask,
        ref long localProcessed, ref bool cancelled, CancellationToken ct,
        long[] threadProcessed, int threadId)
    {
        long localCount = 0;
        int k = numClauses;
        
        // Prepare switch jump table logic or unrolled loops based on k
        // Since k is constant for the whole run, branch prediction handles the if(k) check initially.
        
        fixed (int* pIndices = indices)
        fixed (ulong* pMasks = clauseMasks)
        {
            // Specialized inner loop logic based on k would be fastest (template-like),
            // but here we use a general unrolled loop.
            
            for (long i = 0; i < rangeSize; i++)
            {
                ulong combined = 0;
                
                // Manual unroll for up to 16 clauses (typical for k-SAT studies)
                // This avoids loop overhead and dependency chains better than a small loop.
                if (k >= 8) {
                    combined = pMasks[pIndices[0]] | pMasks[pIndices[1]] | pMasks[pIndices[2]] | pMasks[pIndices[3]]
                             | pMasks[pIndices[4]] | pMasks[pIndices[5]] | pMasks[pIndices[6]] | pMasks[pIndices[7]];
                             
                    for (int j = 8; j < k; j++) combined |= pMasks[pIndices[j]];
                }
                else {
                    combined = pMasks[pIndices[0]];
                    for (int j = 1; j < k; j++) combined |= pMasks[pIndices[j]];
                }

                if (combined == allAssignmentsMask)
                {
                    localCount++;
                }

                localProcessed++;
                if ((localProcessed & 0xFFFFF) == 0)
                {
                    Volatile.Write(ref threadProcessed[threadId], localProcessed);
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                }

                if (i + 1 < rangeSize)
                {
                    NextCombinationUnsafe(pIndices, k, totalClauses);
                }
            }
        }

        return localCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void NextCombinationUnsafe(int* indices, int k, int n)
    {
        int i = k - 1;
        while (i >= 0 && indices[i] == n - k + i)
            i--;
        if (i < 0) return;
        indices[i]++;
        for (int j = i + 1; j < k; j++)
            indices[j] = indices[j - 1] + 1;
    }

    private static void UnrankCombination(long index, int n, int k, int[] result)
    {
        long remaining = index;
        int nextStart = 0;

        for (int pos = 0; pos < k; pos++)
        {
            for (int c = nextStart; c < n; c++)
            {
                long count = CombinationGenerator.CountCombinations(n - c - 1, k - pos - 1);
                if (remaining < count)
                {
                    result[pos] = c;
                    nextStart = c + 1;
                    break;
                }
                remaining -= count;
            }
        }
    }
}
