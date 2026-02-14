using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// Optimized GPU-accelerated MIN-UNSAT counter V2.
/// 
/// Strategy: Sequential Chunk Processing (Chunking).
/// Each GPU thread processes a sequence of N combinations.
/// - Unranking is done only once per chunk (amortized cost).
/// - Indices are updated incrementally (Instruction-level parallelism).
/// - Clause masks are cached in L1/L2 (high coherence).
/// </summary>
public class GpuMinUnsatCounterOptimizedV2 : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private bool _disposed;

    private const int MaxClausesSupported = 20;
    // Chunk size: Number of combinations one thread processes sequentially.
    // Higher = less unranking overhead, but more divergence risk if not careful.
    // 512 is a good balance for occupancy.
    private const int ChunkSize = 1024; 
    private const int BlockSize = 256;
    private const int SharedMemMaxClauses = 512;

    public bool IsGpu => _accelerator is not CPUAccelerator;
    public string AcceleratorName => _accelerator.Name;

    // Standard V2 Kernel
    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
        int, int, int, ulong, int, long, int, ArrayView<int>
    > _kernelV2;

    // Pruning V2 Kernel (3-SAT)
    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
        ArrayView<byte>, int, int, int, ulong, long, int, ArrayView<int>
    > _kernelV2Pruning;

    public GpuMinUnsatCounterOptimizedV2(bool preferGpu = true)
    {
        _context = Context.CreateDefault();

        if (preferGpu)
        {
            try
            {
                _accelerator = _context.GetPreferredDevice(preferCPU: false)
                    .CreateAccelerator(_context);
            }
            catch
            {
                _accelerator = CreateCpuAccelerator();
            }
        }
        else
        {
            _accelerator = CreateCpuAccelerator();
        }

        Console.WriteLine($"[GpuMinUnsatCounterOptimizedV2] Using accelerator: {_accelerator.Name}");

        _kernelV2 = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
            int, int, int, ulong, int, long, int, ArrayView<int>
        >(BatchKernelV2);

        _kernelV2Pruning = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
            ArrayView<byte>, int, int, int, ulong, long, int, ArrayView<int>
        >(BatchKernelV2Pruning);
    }

    private CPUAccelerator CreateCpuAccelerator()
    {
        int processorCount = Environment.ProcessorCount;
        int numMultiprocessors = Math.Max(1, processorCount / 2);
        var cpuDevice = new CPUDevice(2, 1, numMultiprocessors);
        return cpuDevice.CreateCPUAccelerator(_context);
    }

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
        if (numClauses > MaxClausesSupported)
            throw new ArgumentOutOfRangeException(nameof(numClauses), $"Max supported clauses in V2 is {MaxClausesSupported}");

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

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (verbose)
        {
            Console.WriteLine($"[GpuV2] Configuration: v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[GpuV2] Total clause types: {totalClauses}");
            Console.WriteLine($"[GpuV2] Total combinations: {totalCombinations:N0}");
        }

        // Build combination counts for unranking
        long[] combCounts = new long[(totalClauses + 1) * (numClauses + 1)];
        for (int available = 0; available <= totalClauses; available++)
        {
            for (int choose = 0; choose <= numClauses; choose++)
            {
                combCounts[available * (numClauses + 1) + choose] = CombinationGenerator.CountCombinations(available, choose);
            }
        }

        int allVariablesMask = (1 << numVariables) - 1;
        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMask = numAssignments >= 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;

        // Pruning Setup
        bool usePruning = literalsPerClause == 3;
        byte[]? requiredClauseMask = null;
        if (usePruning)
        {
            requiredClauseMask = BuildRequiredClauseMask(clauseMasks, numAssignments, totalClauses, verbose);
        }

        // --- GPU Allocation ---
        using var gpuClauseMasks = _accelerator.Allocate1D<ulong>(clauseMasks.Length);
        gpuClauseMasks.CopyFromCPU(clauseMasks);

        using var gpuCombCounts = _accelerator.Allocate1D<long>(combCounts.Length);
        gpuCombCounts.CopyFromCPU(combCounts);

        using var gpuClauseVarMasks = _accelerator.Allocate1D<int>(clauseVarMasks.Length);
        gpuClauseVarMasks.CopyFromCPU(clauseVarMasks);

        using var gpuClausePosPacked = _accelerator.Allocate1D<uint>(clausePosPacked.Length);
        gpuClausePosPacked.CopyFromCPU(clausePosPacked);

        using var gpuClauseNegPacked = _accelerator.Allocate1D<uint>(clauseNegPacked.Length);
        gpuClauseNegPacked.CopyFromCPU(clauseNegPacked);

        using var gpuRequiredMask = usePruning ? _accelerator.Allocate1D<byte>(requiredClauseMask!.Length) : null;
        if (gpuRequiredMask != null) gpuRequiredMask.CopyFromCPU(requiredClauseMask!);

        // Grid Configuration
        // We split total work into "Chunks". 1 Thread = 1 Chunk.
        long totalChunks = (totalCombinations + ChunkSize - 1) / ChunkSize;
        
        // We limit the active grid size to prevent TDR or huge memory usage if we had output buffers
        // But since we use atomic reduction or block reduction, we can launch large grids.
        // Assuming we iterate over the grid in CPU-side batches to allow progress reporting/cancel.
        
        int chunksPerBatch = 500_000; // 500k chunks * 1024 = 500M items per batch
        int maxGridSize = (chunksPerBatch + BlockSize - 1) / BlockSize;

        using var gpuResults = _accelerator.Allocate1D<int>(maxGridSize); // Block sums
        int[] cpuResults = new int[maxGridSize];

        long totalCount = 0;
        long processedChunks = 0;
        long elapsedMsBeforeResume = 0;
        
        // Try to resume from checkpoint
        CalculationCheckpoint? checkpoint = null;
        if (useCheckpoint)
        {
            checkpoint = CalculationCheckpoint.TryLoad(numVariables, literalsPerClause, numClauses);
            if (checkpoint != null && checkpoint.ProcessedCombinations > 0 && checkpoint.ProcessedCombinations < totalCombinations)
            {
                // Resume from checkpoint
                long resumeFromChunk = checkpoint.ProcessedCombinations / ChunkSize;
                processedChunks = resumeFromChunk;
                totalCount = checkpoint.CurrentCount;
                elapsedMsBeforeResume = checkpoint.ElapsedMsBeforeCheckpoint;
                
                if (verbose)
                {
                    double resumeProgress = 100.0 * checkpoint.ProcessedCombinations / totalCombinations;
                    Console.WriteLine($"[GpuV2] Resuming from checkpoint: {resumeProgress:F1}% ({checkpoint.ProcessedCombinations:N0} combinations)");
                    Console.WriteLine($"[GpuV2] Prior count: {totalCount:N0}, Prior time: {elapsedMsBeforeResume / 1000.0:F1}s");
                }
            }
            else
            {
                // Create new checkpoint
                checkpoint = new CalculationCheckpoint
                {
                    NumVariables = numVariables,
                    LiteralsPerClause = literalsPerClause,
                    NumClauses = numClauses,
                    TotalCombinations = totalCombinations
                };
            }
        }
        
        var sw = Stopwatch.StartNew();
        var progressSw = Stopwatch.StartNew();
        var checkpointSw = Stopwatch.StartNew();

        Task<long>? pendingSumTask = null;

        while (processedChunks < totalChunks)
        {
            if (ct.IsCancellationRequested)
            {
                if (verbose) Console.WriteLine($"\n[GpuV2] Cancelled");
                
                // Save checkpoint on cancellation
                if (useCheckpoint && checkpoint != null)
                {
                    if (pendingSumTask != null)
                    {
                        totalCount += pendingSumTask.Result;
                        pendingSumTask = null;
                    }
                    checkpoint.ProcessedCombinations = processedChunks * ChunkSize;
                    checkpoint.CurrentCount = totalCount;
                    checkpoint.ElapsedMsBeforeCheckpoint = elapsedMsBeforeResume + sw.ElapsedMilliseconds;
                    checkpoint.Save();
                    if (verbose) Console.WriteLine($"[GpuV2] Checkpoint saved on cancel: {checkpoint.ProcessedCombinations:N0} combinations");
                }
                
                result.Count = totalCount + (pendingSumTask?.Result ?? 0);
                result.WasCancelled = true;
                result.ProcessedCombinations = processedChunks * ChunkSize;
                result.ElapsedMs = elapsedMsBeforeResume + sw.ElapsedMilliseconds;
                return result;
            }

            long remainingChunks = totalChunks - processedChunks;
            int currentBatchChunks = (int)Math.Min(remainingChunks, chunksPerBatch);
            
            // Launch Kernel
            int gridSize = (currentBatchChunks + BlockSize - 1) / BlockSize;
            var config = new KernelConfig(gridSize, BlockSize);
            
            long startChunkIndex = processedChunks;
            
            gpuResults.MemSetToZero();

            if (usePruning && gpuRequiredMask != null)
            {
                _kernelV2Pruning(config,
                    gpuClauseMasks.View, gpuCombCounts.View, gpuClauseVarMasks.View,
                    gpuClausePosPacked.View, gpuClauseNegPacked.View,
                    gpuRequiredMask.View,
                    totalClauses, numClauses, numVariables,
                    allAssignmentsMask, startChunkIndex, currentBatchChunks, gpuResults.View);
            }
            else
            {
                _kernelV2(config,
                    gpuClauseMasks.View, gpuCombCounts.View, gpuClauseVarMasks.View,
                    gpuClausePosPacked.View, gpuClauseNegPacked.View,
                    totalClauses, numClauses, numVariables,
                    allAssignmentsMask, allVariablesMask, startChunkIndex, currentBatchChunks, gpuResults.View);
            }

            _accelerator.Synchronize();

            if (pendingSumTask != null) totalCount += pendingSumTask.Result;

            gpuResults.CopyToCPU(cpuResults);
            
            int activeBlocks = gridSize; // Only sum the valid blocks
            int[] buffer = cpuResults; 
            
            pendingSumTask = Task.Run(() =>
            {
                long s = 0;
                for (int i = 0; i < activeBlocks; i++) s += buffer[i];
                return s;
            });

            processedChunks += currentBatchChunks;

            if (verbose && progressSw.Elapsed.TotalSeconds >= 5)
            {
                long currentProcessed = processedChunks * ChunkSize;
                double progress = 100.0 * processedChunks / totalChunks;
                // Use total elapsed time (prior + current session) for accurate rate/ETA
                double totalElapsedSeconds = (elapsedMsBeforeResume / 1000.0) + sw.Elapsed.TotalSeconds;
                double rate = currentProcessed / Math.Max(0.001, totalElapsedSeconds);
                long remaining = totalCombinations - currentProcessed;
                string etaStr = "";
                if (rate > 0 && remaining > 0)
                {
                    var eta = TimeSpan.FromSeconds(remaining / rate);
                    etaStr = eta.TotalHours >= 1 ? $", ETA: {(int)eta.TotalHours}h {eta.Minutes}m" :
                             eta.TotalMinutes >= 1 ? $", ETA: {eta.Minutes}m {eta.Seconds}s" :
                             $", ETA: {eta.Seconds}s";
                }
                Console.WriteLine($"[GpuV2] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }
            
            // Save checkpoint every 30 seconds
            if (useCheckpoint && checkpoint != null && checkpointSw.Elapsed.TotalSeconds >= 30)
            {
                // Wait for pending sum to get accurate count
                if (pendingSumTask != null)
                {
                    totalCount += pendingSumTask.Result;
                    pendingSumTask = null;
                }
                
                checkpoint.ProcessedCombinations = processedChunks * ChunkSize;
                checkpoint.CurrentCount = totalCount;
                checkpoint.ElapsedMsBeforeCheckpoint = elapsedMsBeforeResume + sw.ElapsedMilliseconds;
                checkpoint.Save();
                
                if (verbose)
                {
                    Console.WriteLine($"[GpuV2] Checkpoint saved: {checkpoint.ProcessedCombinations:N0} combinations, count={totalCount:N0}");
                }
                checkpointSw.Restart();
            }
        }

        if (pendingSumTask != null) totalCount += pendingSumTask.Result;
        sw.Stop();
        
        // Delete checkpoint on successful completion
        if (useCheckpoint && checkpoint != null)
        {
            checkpoint.Delete();
            if (verbose) Console.WriteLine($"[GpuV2] Checkpoint deleted (completed successfully)");
        }
        
        long totalElapsedMs = elapsedMsBeforeResume + sw.ElapsedMilliseconds;

        if (verbose)
        {
            Console.WriteLine($"\n=== Results V2 ===");
            Console.WriteLine($"Time: {totalElapsedMs / 1000.0:F2}s" + (elapsedMsBeforeResume > 0 ? $" (resumed, this session: {sw.Elapsed.TotalSeconds:F2}s)" : ""));
            Console.WriteLine($"Count: {totalCount:N0}");
            Console.WriteLine($"Rate: {totalCombinations / (totalElapsedMs / 1000.0):N0}/s");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = totalCombinations;
        result.ElapsedMs = totalElapsedMs;
        return result;
    }

    private static byte[] BuildRequiredClauseMask(ulong[] clauseMasks, int numAssignments, int totalClauses, bool verbose)
    {
        // Build multi-assignment coverage mask per clause.
        // Each byte stores a bitmask of which "hard assignment groups" this clause covers.
        // For v<=6 with 64 assignments, we select up to 8 independent assignments.
        // A combination must cover ALL selected assignments to be UNSAT.
        // This prunes ~(1-coverage_fraction)^K of combinations vs single-assignment pruning.

        // Step 1: For each assignment, count how many clauses cover it
        int[] coverageCount = new int[numAssignments];
        for (int a = 0; a < numAssignments; a++)
        {
            ulong aMask = 1UL << a;
            for (int c = 0; c < totalClauses; c++)
                if ((clauseMasks[c] & aMask) != 0) coverageCount[a]++;
        }

        // Step 2: Select up to 8 assignments greedily (prefer rarest, maximize independence)
        // Independence = each selected assignment is covered by a different set of clauses
        const int MaxGroups = 8;
        int[] selectedAssignments = new int[MaxGroups];
        bool[] used = new bool[numAssignments];
        int numGroups = 0;

        for (int g = 0; g < MaxGroups && g < numAssignments; g++)
        {
            int best = -1;
            int bestScore = int.MaxValue;

            for (int a = 0; a < numAssignments; a++)
            {
                if (used[a]) continue;
                if (coverageCount[a] < bestScore)
                {
                    bestScore = coverageCount[a];
                    best = a;
                }
            }

            if (best < 0) break;
            selectedAssignments[numGroups++] = best;
            used[best] = true;

            // Mark "similar" assignments as used to maximize independence
            // Two assignments are similar if they share many covering clauses
            ulong bestCoverSet = 0;
            for (int c = 0; c < totalClauses; c++)
                if ((clauseMasks[c] & (1UL << best)) != 0) bestCoverSet |= 1UL << (c & 63);

            for (int a = 0; a < numAssignments; a++)
            {
                if (used[a]) continue;
                // Count overlap
                int overlap = 0;
                for (int c = 0; c < totalClauses; c++)
                {
                    bool coversA = (clauseMasks[c] & (1UL << a)) != 0;
                    bool coversBest = (clauseMasks[c] & (1UL << best)) != 0;
                    if (coversA && coversBest) overlap++;
                }
                // If >80% overlap with selected assignment, skip it (not independent)
                if (overlap * 100 / Math.Max(1, coverageCount[a]) > 80)
                    used[a] = true;
            }
        }

        if (verbose)
        {
            Console.Write($"[Pruning] Multi-assignment: {numGroups} groups, coverage counts: ");
            for (int g = 0; g < numGroups; g++)
                Console.Write($"{coverageCount[selectedAssignments[g]]}/{totalClauses}{(g < numGroups - 1 ? ", " : "")}");
            Console.WriteLine();
        }

        // Step 3: For each clause, build a bitmask of which selected assignments it covers
        // Set unused group bits to 1 so that allGroupsMask=0xFF works even with <8 groups
        byte unusedGroupBits = (byte)(0xFF & ~((1 << numGroups) - 1)); // bits for non-existent groups
        byte[] clauseGroupMask = new byte[totalClauses];
        for (int c = 0; c < totalClauses; c++)
        {
            byte mask = unusedGroupBits; // unused bits pre-set to 1
            for (int g = 0; g < numGroups; g++)
            {
                if ((clauseMasks[c] & (1UL << selectedAssignments[g])) != 0)
                    mask |= (byte)(1 << g);
            }
            clauseGroupMask[c] = mask;
        }

        return clauseGroupMask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accelerator.Dispose();
        _context.Dispose();
    }

    // =================================== KERNELS ===================================

    private static void BatchKernelV2(
        ArrayView<ulong> clauseMasks,
        ArrayView<long> combCounts,
        ArrayView<int> clauseVarMasks,
        ArrayView<uint> clausePosPacked,
        ArrayView<uint> clauseNegPacked,
        int totalClauses,
        int clausesPerFormula,
        int variableCount,
        ulong allAssignmentsMask,
        int allVariablesMask,
        long startChunkIndex,
        int chunksToProcess,
        ArrayView<int> results)
    {
        // 1. Calculate which Chunk this thread handles
        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        // 2. Setup Local State (Indices)
        // We use LocalMemory to store current combination indices.
        // Array size fixed to MaxClausesSupported (20).
        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);
        
        long combinationStart = (startChunkIndex + globalId) * ChunkSize;
        
        // 3. Unrank the INITIAL combination for this chunk
        UnrankCombination(combinationStart, totalClauses, clausesPerFormula, combCounts, localIndices);

        int validCount = 0;

        // 4. Sequential Loop
        // We iterate ChunkSize times.
        // Optimized Reuse: We just fetch indices from local memory.
        for (int i = 0; i < ChunkSize; i++)
        {
            // --- CORE LOGIC START ---
            
            // Note: For V2, we don't do complex "Prefix Caching" state (onePre/twoPre)
            // because keeping that state coherent across updates in a flattened loop 
             // is complex (requires stack for partial sums).
            // Instead, we rely on the GPU's L1/L2 cache to make clause mask fetches super fast.
            // Since `indices[0]...indices[k-2]` are constant for checking, they hit cache 100%.

            ulong one = 0, two = 0;
            int varCoverage = 0;
            uint posPacked = 0, negPacked = 0;

            // Loop over k clauses
            for (int k = 0; k < clausesPerFormula; k++)
            {
                int idx = localIndices[k];
                ulong m = clauseMasks[idx];
                two |= one & m;
                one |= m;
                
                varCoverage |= clauseVarMasks[idx];
                posPacked += clausePosPacked[idx];
                negPacked += clauseNegPacked[idx];
            }

            if (one == allAssignmentsMask && varCoverage == allVariablesMask)
            {
                // UNSAT. Check Minimality.
                ulong unique = one & ~two;
                bool minimal = true;
                
                for (int k = 0; k < clausesPerFormula; k++)
                {
                    if ((clauseMasks[localIndices[k]] & unique) == 0)
                    {
                        minimal = false;
                        break;
                    }
                }

                if (minimal)
                {
                    // Check Canonical
                    bool canonical = true;
                    int stabilizer = 0;
                    for (int v = 0; v < variableCount; v++)
                    {
                        int shift = v * 5;
                        uint p = (posPacked >> shift) & 0x1F;
                        uint n = (negPacked >> shift) & 0x1F;
                        if (p < n) { canonical = false; break; }
                        if (p == n) stabilizer++;
                    }

                    if (canonical)
                    {
                        validCount += (1 << variableCount) >> stabilizer;
                    }
                }
            }

            // --- CORE LOGIC END ---

            // 5. Move to next combination (Increment indices)
            if (!NextCombination(localIndices, clausesPerFormula, totalClauses))
                break; // End of valid combinations
        }

        // 6. Block Reduction / Atomic Add
        // Using intermediate block reduction is better, but Atomic is easier to enable first.
        // Given thread count ~ 10k-100k, direct atomic to global can contend.
        // Use Block Shared atomic.
        
        var sharedSum = SharedMemory.Allocate<int>(1);
        if (Group.IdxX == 0) sharedSum[0] = 0;
        Group.Barrier();

        if (validCount > 0) Atomic.Add(ref sharedSum[0], validCount);
        Group.Barrier();

        if (Group.IdxX == 0)
            results[Grid.IdxX] = sharedSum[0];
    }

    private static void BatchKernelV2Pruning(
        ArrayView<ulong> clauseMasks,
        ArrayView<long> combCounts,
        ArrayView<int> clauseVarMasks,
        ArrayView<uint> clausePosPacked,
        ArrayView<uint> clauseNegPacked,
        ArrayView<byte> clauseGroupMask,
        int totalClauses,
        int clausesPerFormula,
        int variableCount,
        ulong allAssignmentsMask,
        long startChunkIndex,
        int chunksToProcess,
        ArrayView<int> results)
    {
        int allVariablesMask = (1 << variableCount) - 1;
        byte allGroupsMask = 0xFF;

        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);
        long combinationStart = (startChunkIndex + globalId) * ChunkSize;
        UnrankCombination(combinationStart, totalClauses, clausesPerFormula, combCounts, localIndices);

        int validCount = 0;

        for (int i = 0; i < ChunkSize; i++)
        {
            // MERGED single-pass: compute ALL data in one loop to avoid warp divergence.
            // The group check is folded in as one extra byte OR per clause (negligible cost).
            // This avoids the two-loop pattern that caused warp divergence on GPU:
            // with 5% pass rate, P(≥1 of 32 warp threads passes) = 79%, so both loops
            // executed for most iterations anyway.
            ulong one = 0, two = 0;
            int varCoverage = 0;
            uint posPacked = 0, negPacked = 0;
            byte groupCoverage = 0;

            for (int k = 0; k < clausesPerFormula; k++)
            {
                int idx = localIndices[k];
                ulong m = clauseMasks[idx];
                two |= one & m;
                one |= m;
                varCoverage |= clauseVarMasks[idx];
                posPacked += clausePosPacked[idx];
                negPacked += clauseNegPacked[idx];
                groupCoverage |= clauseGroupMask[idx];
            }

            // Check group coverage first (cheapest filter — 95% fail here)
            // Then UNSAT + AllVars. No divergence: every thread does the same work above.
            if (groupCoverage == allGroupsMask &&
                one == allAssignmentsMask &&
                varCoverage == allVariablesMask)
            {
                ulong unique = one & ~two;
                bool minimal = true;
                for (int k = 0; k < clausesPerFormula; k++)
                {
                    if ((clauseMasks[localIndices[k]] & unique) == 0)
                    {
                        minimal = false;
                        break;
                    }
                }

                if (minimal)
                {
                    bool canonical = true;
                    int stabilizer = 0;
                    for (int v = 0; v < variableCount; v++)
                    {
                        int shift = v * 5;
                        uint p = (posPacked >> shift) & 0x1F;
                        uint n = (negPacked >> shift) & 0x1F;
                        if (p < n) { canonical = false; break; }
                        if (p == n) stabilizer++;
                    }

                    if (canonical)
                    {
                        validCount += (1 << variableCount) >> stabilizer;
                    }
                }
            }

            if (!NextCombination(localIndices, clausesPerFormula, totalClauses))
                break;
        }

        var sharedSum = SharedMemory.Allocate<int>(1);
        if (Group.IdxX == 0) sharedSum[0] = 0;
        Group.Barrier();

        if (validCount > 0) Atomic.Add(ref sharedSum[0], validCount);
        Group.Barrier();

        if (Group.IdxX == 0) results[Grid.IdxX] = sharedSum[0];
    }

    // --- Device/Inline Helpers ---

    private static bool NextCombination(ArrayView<int> indices, int k, int n)
    {
        // Standard lexicographical next combination logic
        // indices are sorted.
        int i = k - 1;
        while (i >= 0 && indices[i] == n - k + i) 
            i--;
        
        if (i < 0) return false;
        
        indices[i]++;
        for (int j = i + 1; j < k; j++)
            indices[j] = indices[j - 1] + 1;
            
        return true;
    }

    private static void UnrankCombination(long index, int n, int k, ArrayView<long> combCounts, ArrayView<int> result)
    {
        long remaining = index;
        int current = 0;

        for (int pos = 0; pos < k; pos++)
        {
            int r = k - pos - 1; // items remaining to pick after this
            
            // We want to find 'element' such that we skip all combinations starting with < element
            // Count(started with < element) <= remaining
            
            // Using pre-computed Pascals Triangle (combCounts)
            // C(n, k) table layout: N * (K_MAX+1) + K
            
            int element = current;
            while (true)
            {
                // Count combinations if we pick 'element' at 'pos'
                // = C(n - element - 1, k - pos - 1)
                
                int n_remaining = n - element - 1;
                int k_remaining = k - pos - 1;
                
                long count = 0;
                if (k_remaining < 0 || k_remaining > n_remaining) count = 0;
                else count = combCounts[n_remaining * (k + 1) + k_remaining];

                if (remaining < count)
                {
                    // Found it
                    result[pos] = element;
                    current = element + 1;
                    break;
                }
                
                remaining -= count;
                element++;
                if (element >= n) break; // Should not happen if index is valid
            }
        }
    }
}
