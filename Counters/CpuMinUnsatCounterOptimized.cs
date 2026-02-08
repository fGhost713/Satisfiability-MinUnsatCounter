using System.Diagnostics;
using System.Runtime.CompilerServices;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// Optimized CPU MIN-UNSAT counter using UNSAT pruning.
/// 
/// Key optimization: Skip combinations that can't possibly be UNSAT.
/// - Find the "hardest" assignment (covered by fewest clauses)
/// - All UNSAT formulas must include at least one clause covering it
/// - Skip combinations without any such clause
/// 
/// Pruning effectiveness (for combinations to skip):
/// - 2-SAT: ~6-11% reduction
/// - 3-SAT: ~28-88% reduction (MUCH better!)
/// </summary>
public class CpuMinUnsatCounterOptimized
{
    public string AcceleratorName => $"CPU Optimized ({Environment.ProcessorCount} cores)";

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

        if (numVariables > 6)
        throw new ArgumentOutOfRangeException(nameof(numVariables), 
            "Optimized kernel limited to 6 variables (uses 64-bit assignment mask). Use simple counter for n > 6.");
        if (numClauses < 1)
            throw new ArgumentOutOfRangeException(nameof(numClauses), "Need at least 1 clause");
        if (literalsPerClause < 2 || literalsPerClause > 3)
            throw new ArgumentOutOfRangeException(nameof(literalsPerClause), "Literals per clause must be 2 or 3");

        int numThreads = Environment.ProcessorCount;
        if (verbose) Console.WriteLine($"[CpuOptimized] Using {numThreads} CPU cores");

        // Build lookup tables
        var (flatLits, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        ulong[] clauseMasks = ClauseMaskBuilder.BuildClauseMasks(numVariables, literalsPerClause);
        int[] clauseVarMasks = ClauseLiteralMapper.BuildClauseVariableMasks(numVariables, literalsPerClause);

        uint[] clausePosPacked = new uint[totalClauses];
        uint[] clauseNegPacked = new uint[totalClauses];
        for (int c = 0; c < totalClauses; c++)
        {
            int baseIdx = c * literalsPerClause * 2;
            uint pos = 0, neg = 0;
            for (int lit = 0; lit < literalsPerClause; lit++)
            {
                int varNo = flatLits[baseIdx];
                int polarity = flatLits[baseIdx + 1];
                int shift = varNo * 5;
                if (polarity == 1) pos += 1u << shift;
                else neg += 1u << shift;
                baseIdx += 2;
            }
            clausePosPacked[c] = pos;
            clauseNegPacked[c] = neg;
        }

        if (verbose) Console.WriteLine($"[CpuOptimized] Total clause types: {totalClauses}");

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (numClauses > totalClauses)
        {
            result.ProcessedCombinations = result.TotalCombinations;
            return result;
        }

        if (verbose) Console.WriteLine($"[CpuOptimized] Total combinations: {totalCombinations:N0}");

        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMask = numAssignments >= 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;

        // === PRUNING: Only enable for 3-SAT where it's effective (28-88% skip) ===
        // For 2-SAT, pruning only skips ~6-11% but adds overhead to every combination
        bool usePruning = literalsPerClause == 3;
        byte[] isRequired = null!;
        
        if (usePruning)
        {
            // Find clauses that cover the hardest-to-cover assignment
            isRequired = BuildRequiredClauseMask(clauseMasks, allAssignmentsMask, numAssignments, totalClauses, verbose);
            
            // Count how many clauses are required
            int requiredCount = 0;
            for (int c = 0; c < totalClauses; c++)
                if (isRequired[c] != 0) requiredCount++;

            long excludedCombinations = CombinationGenerator.CountCombinations(totalClauses - requiredCount, numClauses);
            double pruningPercent = 100.0 * excludedCombinations / totalCombinations;

            if (verbose)
            {
                Console.WriteLine($"[CpuOptimized] Required clauses: {requiredCount} of {totalClauses}");
                Console.WriteLine($"[CpuOptimized] Pruning: {pruningPercent:F2}% of combinations will be skipped");
            }
        }
        else
        {
            if (verbose) Console.WriteLine($"[CpuOptimized] Pruning disabled for {literalsPerClause}-SAT (overhead > benefit)");
        }

        int allVariablesMask = (1 << numVariables) - 1;
        int orbitMultiplier = 1 << numVariables;

        var sw = Stopwatch.StartNew();
        var progressSw = Stopwatch.StartNew();
        
        // Checkpoint support (limited for CPU - saves on cancel but cannot resume mid-calculation)
        CalculationCheckpoint? checkpoint = null;
        if (useCheckpoint)
        {
            checkpoint = CalculationCheckpoint.TryLoad(numVariables, literalsPerClause, numClauses);
            if (checkpoint != null && checkpoint.ProcessedCombinations > 0 && checkpoint.ProcessedCombinations < totalCombinations)
            {
                if (verbose)
                {
                    Console.WriteLine($"[CPU-Opt] Warning: CPU mode cannot resume from checkpoint. Starting fresh.");
                    Console.WriteLine($"[CPU-Opt] (Previous progress: {100.0 * checkpoint.ProcessedCombinations / totalCombinations:F1}%)");
                }
            }
            
            // Create new checkpoint for saving on cancel
            checkpoint = new CalculationCheckpoint
            {
                NumVariables = numVariables,
                LiteralsPerClause = literalsPerClause,
                NumClauses = numClauses,
                TotalCombinations = totalCombinations
            };
        }

        // Partition work across threads
        long combinationsPerThread = (totalCombinations + numThreads - 1) / numThreads;
        
        var threads = new Thread[numThreads];
        var threadResults = new long[numThreads];
        var threadProcessed = new long[numThreads];
        var threadSkipped = new long[numThreads];
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
                long localSkipped = 0;
                long rangeSize = endIndex - startIndex;

                // Use pruning only for 3-SAT where it's effective
                if (usePruning)
                {
                    localCount = ProcessRangeWithPruning(
                        indices, rangeSize, numClauses, numVariables, totalClauses,
                        clauseMasks, clauseVarMasks, clausePosPacked, clauseNegPacked,
                        isRequired, allVariablesMask, allAssignmentsMask, orbitMultiplier,
                        ref localProcessed, ref localSkipped, ref cancelled, ct,
                        threadProcessed, threadId);
                }
                else
                {
                    localCount = ProcessRangeNoPruning(
                        indices, rangeSize, numClauses, numVariables, totalClauses,
                        clauseMasks, clauseVarMasks, clausePosPacked, clauseNegPacked,
                        allVariablesMask, allAssignmentsMask, orbitMultiplier,
                        ref localProcessed, ref cancelled, ct,
                        threadProcessed, threadId);
                }

                threadResults[threadId] = localCount;
                threadSkipped[threadId] = localSkipped;
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
                Console.WriteLine($"[CPU-Opt] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }

            Thread.Sleep(100);
        }

        // Collect results
        long totalCount = 0;
        long processedCount = 0;
        long skippedCount = 0;
        for (int t = 0; t < numThreads; t++)
        {
            threads[t].Join();
            totalCount += threadResults[t];
            processedCount += threadProcessed[t];
            skippedCount += threadSkipped[t];
        }

        sw.Stop();

        if (cancelled || ct.IsCancellationRequested)
        {
            if (verbose) Console.WriteLine($"\n[CpuOptimized] Cancelled");
            
            // Save checkpoint on cancel (for reference, though CPU can't resume)
            if (useCheckpoint && checkpoint != null)
            {
                checkpoint.ProcessedCombinations = processedCount;
                checkpoint.CurrentCount = totalCount;
                checkpoint.ElapsedMsBeforeCheckpoint = sw.ElapsedMilliseconds;
                checkpoint.Save();
                if (verbose) Console.WriteLine($"[CPU-Opt] Checkpoint saved: {processedCount:N0} combinations (note: CPU cannot resume)");
            }
            
            result.Count = totalCount;
            result.ProcessedCombinations = processedCount;
            result.WasCancelled = true;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }
        
        // Delete checkpoint on successful completion
        if (useCheckpoint && checkpoint != null)
        {
            checkpoint.Delete();
        }

        if (verbose)
        {
            Console.WriteLine($"\n=== Optimized Results ===");
            Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Enumerated: {processedCount:N0}, Skipped: {skippedCount:N0}");
            Console.WriteLine($"Actual skip rate: {100.0 * skippedCount / (processedCount + skippedCount):F2}%");
            Console.WriteLine($"MIN-UNSAT count: {totalCount:N0}");
            Console.WriteLine($"Rate: {(processedCount + skippedCount) / sw.Elapsed.TotalSeconds:N0}/s");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = processedCount + skippedCount;
        result.WasCancelled = false;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Build a byte array marking which clauses are "required" (cover the hardest assignment).
    /// </summary>
    private byte[] BuildRequiredClauseMask(ulong[] clauseMasks, ulong allAssignmentsMask, 
        int numAssignments, int totalClauses, bool verbose)
    {
        // Build coverage: how many clauses cover each assignment
        int[] coverageCount = new int[numAssignments];
        for (int a = 0; a < numAssignments; a++)
        {
            ulong aMask = 1UL << a;
            for (int c = 0; c < totalClauses; c++)
            {
                if ((clauseMasks[c] & aMask) != 0)
                    coverageCount[a]++;
            }
        }

        // Find the hardest-to-cover assignment
        int hardestAssignment = 0;
        int minCovering = int.MaxValue;
        for (int a = 0; a < numAssignments; a++)
        {
            if (coverageCount[a] < minCovering)
            {
                minCovering = coverageCount[a];
                hardestAssignment = a;
            }
        }

        if (verbose)
        {
            Console.WriteLine($"[Pruning] Hardest assignment {hardestAssignment} is covered by {minCovering} clauses");
        }

        // Mark clauses that cover the hardest assignment
        byte[] isRequired = new byte[totalClauses];
        ulong hardestMask = 1UL << hardestAssignment;
        for (int c = 0; c < totalClauses; c++)
        {
            if ((clauseMasks[c] & hardestMask) != 0)
                isRequired[c] = 1;
        }

        return isRequired;
    }

    /// <summary>
    /// Process combinations with pruning - skip combinations without required clauses.
    /// Uses unsafe pointers for maximum performance.
    /// OPTIMIZED with Prefix Caching (reuse computation for first k-1 clauses).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe long ProcessRangeWithPruning(
        int[] indices, long rangeSize, int numClauses, int numVariables, int totalClauses,
        ulong[] clauseMasks, int[] clauseVarMasks, uint[] clausePosPacked, uint[] clauseNegPacked,
        byte[] isRequired, int allVariablesMask, ulong allAssignmentsMask, int orbitMultiplier,
        ref long localProcessed, ref long localSkipped, ref bool cancelled, CancellationToken ct,
        long[] threadProcessed, int threadId)
    {
        long localCount = 0;
        long processed = 0;

        // Pre-compute orbit lookup
        int[] orbitLookup = new int[numVariables + 1];
        for (int s = 0; s <= numVariables; s++)
            orbitLookup[s] = orbitMultiplier >> s;

        int limitIdx = numClauses - 1;
        int maxLastIndex = totalClauses - 1;

        fixed (int* pIndices = indices)
        fixed (ulong* pMasks = clauseMasks)
        fixed (int* pVarMasks = clauseVarMasks)
        fixed (uint* pPosPacked = clausePosPacked)
        fixed (uint* pNegPacked = clauseNegPacked)
        fixed (byte* pRequired = isRequired)
        fixed (int* pOrbitLookup = orbitLookup)
        {
            while (processed < rangeSize)
            {
                // Calculate run length: how many iterations we can do with just the last index changing
                int currentLast = pIndices[limitIdx];
                long remainingInRange = rangeSize - processed;
                int potentialRun = maxLastIndex - currentLast + 1;
                int runLength = (int)(potentialRun < remainingInRange ? potentialRun : remainingInRange);
                int loopEnd = currentLast + runLength;

                // 1. Check if prefix has a required clause
                bool prefixHasRequired = false;
                for (int j = 0; j < limitIdx; j++)
                {
                    if (pRequired[pIndices[j]] != 0)
                    {
                        prefixHasRequired = true;
                        break;
                    }
                }

                if (prefixHasRequired)
                {
                    // === OPTIMIZED PATH A: Prefix already satisfied requirement ===
                    // We don't need to check pRequired[idx] inside the inner loop!
                    
                    // Compute prefix aggregates
                    int varCoveragePre = 0;
                    ulong onePre = 0, twoPre = 0;
                    uint posPackedPre = 0, negPackedPre = 0;

                    for (int j = 0; j < limitIdx; j++)
                    {
                        int idx = pIndices[j];
                        varCoveragePre |= pVarMasks[idx];
                        ulong m = pMasks[idx];
                        twoPre |= onePre & m;
                        onePre |= m;
                        posPackedPre += pPosPacked[idx];
                        negPackedPre += pNegPacked[idx];
                    }

                    for (int idx = currentLast; idx < loopEnd; idx++)
                    {
                        // Standard checks (variable coverage -> UNSAT -> minimality -> canonical)
                        if ((varCoveragePre | pVarMasks[idx]) == allVariablesMask)
                        {
                            ulong m = pMasks[idx];
                            if ((onePre | m) == allAssignmentsMask)
                            {
                                ulong currentTwo = twoPre | (onePre & m);
                                ulong unique = allAssignmentsMask & ~currentTwo; 
                                
                                if ((m & unique) != 0)
                                {
                                    bool minimal = true;
                                    for (int j = 0; j < limitIdx; j++)
                                    {
                                        if ((pMasks[pIndices[j]] & unique) == 0) { minimal = false; break; }
                                    }

                                    if (minimal)
                                    {
                                        uint posPacked = posPackedPre + pPosPacked[idx];
                                        uint negPacked = negPackedPre + pNegPacked[idx];
                                        
                                        int stabilizerExponent = 0;
                                        bool canonical = true;
                                        for (int v = 0; v < numVariables; v++)
                                        {
                                            int shift = v * 5;
                                            uint pCount = (posPacked >> shift) & 0x1F;
                                            uint nCount = (negPacked >> shift) & 0x1F;
                                            if (pCount < nCount) { canonical = false; break; }
                                            if (pCount == nCount) stabilizerExponent++;
                                        }

                                        if (canonical) localCount += pOrbitLookup[stabilizerExponent];
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // === OPTIMIZED PATH B: Prefix does NOT satisfy requirement ===
                    // We MUST check pRequired[idx] inside the inner loop.
                    
                    // Compute prefix aggregates
                    int varCoveragePre = 0;
                    ulong onePre = 0, twoPre = 0;
                    uint posPackedPre = 0, negPackedPre = 0;

                    for (int j = 0; j < limitIdx; j++)
                    {
                        int idx = pIndices[j];
                        varCoveragePre |= pVarMasks[idx];
                        ulong m = pMasks[idx];
                        twoPre |= onePre & m;
                        onePre |= m;
                        posPackedPre += pPosPacked[idx];
                        negPackedPre += pNegPacked[idx];
                    }

                    for (int idx = currentLast; idx < loopEnd; idx++)
                    {
                        // Pruning check first!
                        if (pRequired[idx] == 0)
                        {
                            localSkipped++;
                            continue;
                        }

                        // Standard checks
                        if ((varCoveragePre | pVarMasks[idx]) == allVariablesMask)
                        {
                            ulong m = pMasks[idx];
                            if ((onePre | m) == allAssignmentsMask)
                            {
                                ulong currentTwo = twoPre | (onePre & m);
                                ulong unique = allAssignmentsMask & ~currentTwo; 
                                
                                if ((m & unique) != 0)
                                {
                                    bool minimal = true;
                                    for (int j = 0; j < limitIdx; j++)
                                    {
                                        if ((pMasks[pIndices[j]] & unique) == 0) { minimal = false; break; }
                                    }

                                    if (minimal)
                                    {
                                        uint posPacked = posPackedPre + pPosPacked[idx];
                                        uint negPacked = negPackedPre + pNegPacked[idx];
                                        
                                        int stabilizerExponent = 0;
                                        bool canonical = true;
                                        for (int v = 0; v < numVariables; v++)
                                        {
                                            int shift = v * 5;
                                            uint pCount = (posPacked >> shift) & 0x1F;
                                            uint nCount = (negPacked >> shift) & 0x1F;
                                            if (pCount < nCount) { canonical = false; break; }
                                            if (pCount == nCount) stabilizerExponent++;
                                        }

                                        if (canonical) localCount += pOrbitLookup[stabilizerExponent];
                                    }
                                }
                            }
                        }
                    }
                }

                // Update state
                processed += runLength;
                localProcessed += runLength;
                
                // Update indices for the next batch
                pIndices[limitIdx] = loopEnd - 1;

                if (processed < rangeSize)
                {
                    NextCombinationUnsafe(pIndices, numClauses, totalClauses);
                }

                // Progress reporting
                if ((localProcessed & 0xFFFFF) == 0)
                {
                    Volatile.Write(ref threadProcessed[threadId], localProcessed);
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                }
            }
        }

        return localCount;
    }

    /// <summary>
    /// Process combinations without pruning - optimized with Prefix Caching.
    /// Reuses computation for the first k-1 clauses while iterating the k-th clause.
    /// Uses batch processing to maximize prefix reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe long ProcessRangeNoPruning(
        int[] indices, long rangeSize, int numClauses, int numVariables, int totalClauses,
        ulong[] clauseMasks, int[] clauseVarMasks, uint[] clausePosPacked, uint[] clauseNegPacked,
        int allVariablesMask, ulong allAssignmentsMask, int orbitMultiplier,
        ref long localProcessed, ref bool cancelled, CancellationToken ct,
        long[] threadProcessed, int threadId)
    {
        long localCount = 0;
        long processed = 0;

        // Pre-compute orbit lookup
        int[] orbitLookup = new int[numVariables + 1];
        for (int s = 0; s <= numVariables; s++)
            orbitLookup[s] = orbitMultiplier >> s;

        int limitIdx = numClauses - 1;
        int maxLastIndex = totalClauses - 1;

        fixed (int* pIndices = indices)
        fixed (ulong* pMasks = clauseMasks)
        fixed (int* pVarMasks = clauseVarMasks)
        fixed (uint* pPosPacked = clausePosPacked)
        fixed (uint* pNegPacked = clauseNegPacked)
        fixed (int* pOrbitLookup = orbitLookup)
        {
            while (processed < rangeSize)
            {
                // Calculate run length: how many iterations we can do with just the last index changing
                int currentLast = pIndices[limitIdx];
                long remainingInRange = rangeSize - processed;
                int potentialRun = maxLastIndex - currentLast + 1;
                int runLength = (int)(potentialRun < remainingInRange ? potentialRun : remainingInRange);

                // Compute prefix aggregates (first k-1 clauses)
                int varCoveragePre = 0;
                ulong onePre = 0, twoPre = 0;
                uint posPackedPre = 0, negPackedPre = 0;

                for (int j = 0; j < limitIdx; j++)
                {
                    int idx = pIndices[j];
                    varCoveragePre |= pVarMasks[idx];
                    ulong m = pMasks[idx];
                    twoPre |= onePre & m; // Track double/multi coverage
                    onePre |= m;
                    posPackedPre += pPosPacked[idx];
                    negPackedPre += pNegPacked[idx];
                }

                // Inner loop optimization: iterate only the last index
                // This replaces O(k) inner loop with O(1) mostly
                
                // Pre-calculate loop limit for safety and performance
                int loopEnd = currentLast + runLength;
                
                for (int idx = currentLast; idx < loopEnd; idx++)
                {
                    // Combined check using prefix values
                    ulong m = pMasks[idx];
                    
                    // Check variable coverage first (fastest filter)
                    if ((varCoveragePre | pVarMasks[idx]) == allVariablesMask)
                    {
                        // Check UNSAT (all assignments covered)
                        // one = onePre | m
                        if ((onePre | m) == allAssignmentsMask)
                        {
                            // It is UNSAT. Now check minimality.
                            // unique = one & ~two
                            // two = twoPre | (onePre & m)
                            ulong currentTwo = twoPre | (onePre & m);
                            ulong unique = allAssignmentsMask & ~currentTwo; 

                            // 1. Is the current clause redundant? (Minimal check for last clause)
                            // If (m & unique) == 0, then 'm' doesn't cover anything uniquely.
                            if ((m & unique) != 0)
                            {
                                // 2. Are any prefix clauses redundant?
                                // We must check all k-1 previous clauses
                                bool minimal = true;
                                for (int j = 0; j < limitIdx; j++)
                                {
                                    if ((pMasks[pIndices[j]] & unique) == 0)
                                    {
                                        minimal = false;
                                        break;
                                    }
                                }

                                if (minimal)
                                {
                                    // 3. Canonical check
                                    uint posPacked = posPackedPre + pPosPacked[idx];
                                    uint negPacked = negPackedPre + pNegPacked[idx];
                                    
                                    // Inline Stabilizer Check
                                    int stabilizerExponent = 0;
                                    bool canonical = true;
                                    // Manually unrolled 3-variable check for speed? Or just loop.
                                    // Given v <= 6, small loop is fine.
                                    for (int v = 0; v < numVariables; v++)
                                    {
                                        int shift = v * 5;
                                        uint pCount = (posPacked >> shift) & 0x1F;
                                        uint nCount = (negPacked >> shift) & 0x1F;
                                        if (pCount < nCount) { canonical = false; break; }
                                        if (pCount == nCount) stabilizerExponent++;
                                    }

                                    if (canonical)
                                    {
                                        localCount += pOrbitLookup[stabilizerExponent];
                                    }
                                }
                            }
                        }
                    }
                }

                // Update state
                processed += runLength;
                localProcessed += runLength;
                
                // Update indices for the next batch
                // The loop effectively ran until `limitEnd - 1`
                pIndices[limitIdx] = loopEnd - 1;

                if (processed < rangeSize)
                {
                    // Move to next valid combination (standard carry logic)
                    NextCombinationUnsafe(pIndices, numClauses, totalClauses);
                }

                // Progress reporting (approximate, every batch)
                if ((localProcessed & 0xFFFFF) == 0)
                {
                    Volatile.Write(ref threadProcessed[threadId], localProcessed);
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                }
            }
        }

        return localCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void NextCombinationUnsafe(int* indices, int k, int n)
    {
        int i = k - 1;
        while (i >= 0 && indices[i] == n - k + i) i--;
        if (i < 0) return;
        indices[i]++;
        for (int j = i + 1; j < k; j++)
            indices[j] = indices[j - 1] + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeStabilizer(uint posPacked, uint negPacked, int numVariables)
    {
        int stabilizerExp = 0;
        for (int v = 0; v < numVariables; v++)
        {
            int shift = v * 5;
            uint pCount = (posPacked >> shift) & 0x1F;
            uint nCount = (negPacked >> shift) & 0x1F;
            if (pCount < nCount) return -1;
            if (pCount == nCount) stabilizerExp++;
        }
        return stabilizerExp;
    }

    private static void UnrankCombination(long index, int n, int k, int[] result)
    {
        if (k == 0) return;
        long remaining = index;
        int current = 0;

        for (int pos = 0; pos < k; pos++)
        {
            int element = current;
            while (element < n)
            {
                long count = BinomialCoefficient(n - element - 1, k - pos - 1);
                if (remaining < count) break;
                remaining -= count;
                element++;
            }
            result[pos] = element;
            current = element + 1;
        }
    }

    private static long BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;
        long result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }
}
