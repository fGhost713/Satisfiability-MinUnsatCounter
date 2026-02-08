using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// GPU-accelerated MIN-UNSAT counter for larger variable counts (n > 6, up to 10).
/// 
/// This is a fallback implementation that uses multiple ulongs to handle more assignments.
/// - For n=7: 128 assignments = 2 ulongs
/// - For n=8: 256 assignments = 4 ulongs
/// - For n=9: 512 assignments = 8 ulongs
/// - For n=10: 1024 assignments = 16 ulongs
/// 
/// Trade-off: Slower than the optimized V2 kernel due to array-based masking, but still GPU-accelerated.
/// </summary>
public class GpuMinUnsatCounterManyVars : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private bool _disposed;

    private const int MaxClausesSupported = 20;
    private const int MaxVariables = 10; // 2^10 = 1024 assignments = 16 ulongs
    private const int ChunkSize = 256; // Smaller chunks due to more work per combination
    private const int BlockSize = 128;

    public bool IsGpu => _accelerator is not CPUAccelerator;
    public string AcceleratorName => _accelerator.Name;

    // Kernel for n=7 (2 ulongs)
    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<int>, ArrayView<int>,
        int, int, int, long, int, ArrayView<int>
    > _kernelManyVars;

    public GpuMinUnsatCounterManyVars(bool preferGpu = true)
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

        Console.WriteLine($"[GpuMinUnsatCounterManyVars] Using accelerator: {_accelerator.Name}");

        _kernelManyVars = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<int>, ArrayView<int>,
            int, int, int, long, int, ArrayView<int>
        >(BatchKernelManyVars);
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

        if (numVariables < 7)
            throw new ArgumentOutOfRangeException(nameof(numVariables), 
                "Use GpuMinUnsatCounterOptimizedV2 for n <= 6 (faster).");
        if (numVariables > MaxVariables)
            throw new ArgumentOutOfRangeException(nameof(numVariables), 
                $"Variables limited to {MaxVariables} (2^{MaxVariables} = {1 << MaxVariables} assignments).");
        if (numClauses < 1)
            throw new ArgumentOutOfRangeException(nameof(numClauses), "Need at least 1 clause");
        if (numClauses > MaxClausesSupported)
            throw new ArgumentOutOfRangeException(nameof(numClauses), $"Max supported clauses is {MaxClausesSupported}");

        // Build lookup tables
        var (flatLits, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        int[] clauseVarMasks = ClauseLiteralMapper.BuildClauseVariableMasks(numVariables, literalsPerClause);

        int numAssignments = 1 << numVariables;
        int numMaskWords = (numAssignments + 63) / 64; // Number of ulongs needed
        int allVariablesMask = (1 << numVariables) - 1;

        // Build clause masks as multiple ulongs per clause
        // Layout: clauseMasks[clause * numMaskWords + wordIdx]
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

        // Build polarity data for canonical form (packed: pos in low 4 bits, neg in high 4 bits per variable)
        // For simplicity, use separate arrays
        int[] clausePolPos = new int[totalClauses]; // bit per variable if has positive
        int[] clausePolNeg = new int[totalClauses]; // bit per variable if has negative
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

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (verbose)
        {
            Console.WriteLine($"[GpuManyVars] Configuration: v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[GpuManyVars] Total clause types: {totalClauses}");
            Console.WriteLine($"[GpuManyVars] Total combinations: {totalCombinations:N0}");
            Console.WriteLine($"[GpuManyVars] Assignments: {numAssignments} ({numMaskWords} mask words)");
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

        // --- GPU Allocation ---
        using var gpuClauseMasks = _accelerator.Allocate1D<ulong>(clauseMasks.Length);
        gpuClauseMasks.CopyFromCPU(clauseMasks);

        // Build "all ones" mask for UNSAT check
        ulong[] allOnesMask = new ulong[numMaskWords];
        for (int w = 0; w < numMaskWords; w++)
        {
            int bitsInWord = Math.Min(64, numAssignments - w * 64);
            allOnesMask[w] = bitsInWord == 64 ? ulong.MaxValue : (1UL << bitsInWord) - 1;
        }
        using var gpuAllOnesMask = _accelerator.Allocate1D<ulong>(allOnesMask.Length);
        gpuAllOnesMask.CopyFromCPU(allOnesMask);

        using var gpuCombCounts = _accelerator.Allocate1D<long>(combCounts.Length);
        gpuCombCounts.CopyFromCPU(combCounts);

        using var gpuClauseVarMasks = _accelerator.Allocate1D<int>(clauseVarMasks.Length);
        gpuClauseVarMasks.CopyFromCPU(clauseVarMasks);

        using var gpuClausePolPos = _accelerator.Allocate1D<int>(clausePolPos.Length);
        gpuClausePolPos.CopyFromCPU(clausePolPos);

        using var gpuClausePolNeg = _accelerator.Allocate1D<int>(clausePolNeg.Length);
        gpuClausePolNeg.CopyFromCPU(clausePolNeg);

        // Grid Configuration
        long totalChunks = (totalCombinations + ChunkSize - 1) / ChunkSize;
        int chunksPerBatch = 200_000;
        int maxGridSize = (chunksPerBatch + BlockSize - 1) / BlockSize;

        using var gpuResults = _accelerator.Allocate1D<int>(maxGridSize);
        int[] cpuResults = new int[maxGridSize];

        long totalCount = 0;
        long processedChunks = 0;

        var sw = Stopwatch.StartNew();
        var progressSw = Stopwatch.StartNew();

        Task<long>? pendingSumTask = null;

        while (processedChunks < totalChunks)
        {
            if (ct.IsCancellationRequested)
            {
                if (verbose) Console.WriteLine($"\n[GpuManyVars] Cancelled");
                result.Count = totalCount + (pendingSumTask?.Result ?? 0);
                result.WasCancelled = true;
                result.ProcessedCombinations = processedChunks * ChunkSize;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            long remainingChunks = totalChunks - processedChunks;
            int currentBatchChunks = (int)Math.Min(remainingChunks, chunksPerBatch);

            int gridSize = (currentBatchChunks + BlockSize - 1) / BlockSize;
            var config = new KernelConfig(gridSize, BlockSize);

            long startChunkIndex = processedChunks;

            gpuResults.MemSetToZero();

            _kernelManyVars(config,
                gpuClauseMasks.View, gpuAllOnesMask.View, gpuCombCounts.View, 
                gpuClauseVarMasks.View, gpuClausePolPos.View, gpuClausePolNeg.View,
                totalClauses, numClauses, numVariables,
                startChunkIndex, currentBatchChunks, gpuResults.View);

            _accelerator.Synchronize();

            if (pendingSumTask != null) totalCount += pendingSumTask.Result;

            gpuResults.CopyToCPU(cpuResults);

            int activeBlocks = gridSize;
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
                Console.WriteLine($"[GpuManyVars] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }
        }

        if (pendingSumTask != null) totalCount += pendingSumTask.Result;
        sw.Stop();

        if (verbose)
        {
            Console.WriteLine($"\n=== Results (ManyVars) ===");
            Console.WriteLine($"Time: {sw.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"Count: {totalCount:N0}");
            Console.WriteLine($"Rate: {totalCombinations / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0}/s");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = totalCombinations;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accelerator.Dispose();
        _context.Dispose();
    }

    // =================================== KERNEL ===================================

    private static void BatchKernelManyVars(
        ArrayView<ulong> clauseMasks,      // [totalClauses * numMaskWords]
        ArrayView<ulong> allOnesMask,      // [numMaskWords] - all bits set for UNSAT check
        ArrayView<long> combCounts,
        ArrayView<int> clauseVarMasks,
        ArrayView<int> clausePolPos,       // positive polarity bits per clause
        ArrayView<int> clausePolNeg,       // negative polarity bits per clause
        int totalClauses,
        int clausesPerFormula,
        int variableCount,
        long startChunkIndex,
        int chunksToProcess,
        ArrayView<int> results)
    {
        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        int numMaskWords = (1 << variableCount) / 64;
        if (numMaskWords == 0) numMaskWords = 1;
        
        // For n=7: 2 words, n=8: 4 words, n=9: 8 words, n=10: 16 words
        // Use local memory for mask accumulation (max 16 ulongs)
        var localOne = LocalMemory.Allocate<ulong>(16);
        var localTwo = LocalMemory.Allocate<ulong>(16);
        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);

        long combinationStart = (startChunkIndex + globalId) * ChunkSize;

        // Unrank initial combination
        UnrankCombination(combinationStart, totalClauses, clausesPerFormula, combCounts, localIndices);

        int allVariablesMask = (1 << variableCount) - 1;
        int validCount = 0;

        for (int iter = 0; iter < ChunkSize; iter++)
        {
            // Check all variables used
            int varMask = 0;
            for (int k = 0; k < clausesPerFormula; k++)
                varMask |= clauseVarMasks[localIndices[k]];

            if (varMask == allVariablesMask)
            {
                // Clear local masks
                for (int w = 0; w < numMaskWords; w++)
                {
                    localOne[w] = 0;
                    localTwo[w] = 0;
                }

                // Accumulate clause masks
                for (int k = 0; k < clausesPerFormula; k++)
                {
                    int idx = localIndices[k];
                    int baseOffset = idx * numMaskWords;
                    for (int w = 0; w < numMaskWords; w++)
                    {
                        ulong m = clauseMasks[baseOffset + w];
                        localTwo[w] |= localOne[w] & m;
                        localOne[w] |= m;
                    }
                }

                // Check UNSAT (all assignments covered)
                bool isUnsat = true;
                for (int w = 0; w < numMaskWords && isUnsat; w++)
                {
                    if (localOne[w] != allOnesMask[w])
                        isUnsat = false;
                }

                if (isUnsat)
                {
                    // Check minimality: each clause must have unique coverage
                    bool isMinimal = true;
                    for (int k = 0; k < clausesPerFormula && isMinimal; k++)
                    {
                        int idx = localIndices[k];
                        int baseOffset = idx * numMaskWords;
                        bool hasUnique = false;
                        
                        for (int w = 0; w < numMaskWords && !hasUnique; w++)
                        {
                            ulong clauseMask = clauseMasks[baseOffset + w];
                            ulong unique = localOne[w] & ~localTwo[w];
                            if ((clauseMask & unique) != 0)
                                hasUnique = true;
                        }
                        
                        if (!hasUnique) isMinimal = false;
                    }

                    if (isMinimal)
                    {
                        // Check canonical form and compute orbit size
                        // Count positive and negative occurrences per variable
                        int posAccum = 0, negAccum = 0;
                        for (int k = 0; k < clausesPerFormula; k++)
                        {
                            int idx = localIndices[k];
                            posAccum += clausePolPos[idx];
                            negAccum += clausePolNeg[idx];
                        }

                        // For canonical: need actual counts, not just presence
                        // Recompute properly using PopCount on accumulated bits
                        // Actually we need per-variable counts, let's compute them
                        
                        bool isCanonical = true;
                        int stabilizer = 0;
                        
                        // Simple approach: count occurrences per variable
                        for (int v = 0; v < variableCount && isCanonical; v++)
                        {
                            int posCount = 0, negCount = 0;
                            int vMask = 1 << v;
                            for (int k = 0; k < clausesPerFormula; k++)
                            {
                                int idx = localIndices[k];
                                if ((clausePolPos[idx] & vMask) != 0) posCount++;
                                if ((clausePolNeg[idx] & vMask) != 0) negCount++;
                            }
                            
                            if (posCount < negCount)
                                isCanonical = false;
                            else if (posCount == negCount)
                                stabilizer++;
                        }

                        if (isCanonical)
                        {
                            validCount += (1 << variableCount) >> stabilizer;
                        }
                    }
                }
            }

            // Move to next combination
            if (!NextCombination(localIndices, clausesPerFormula, totalClauses))
                break;
        }

        // Block reduction
        var sharedSum = SharedMemory.Allocate<int>(1);
        if (Group.IdxX == 0) sharedSum[0] = 0;
        Group.Barrier();

        if (validCount > 0) Atomic.Add(ref sharedSum[0], validCount);
        Group.Barrier();

        if (Group.IdxX == 0)
            results[Grid.IdxX] = sharedSum[0];
    }

    private static bool NextCombination(ArrayView<int> indices, int k, int n)
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

    private static void UnrankCombination(long index, int n, int k, ArrayView<long> combCounts, ArrayView<int> result)
    {
        long remaining = index;
        int current = 0;

        for (int pos = 0; pos < k; pos++)
        {
            int r = k - pos - 1;
            int element = current;
            
            while (true)
            {
                int n_remaining = n - element - 1;
                int k_remaining = k - pos - 1;

                long count = 0;
                if (k_remaining < 0 || k_remaining > n_remaining) count = 0;
                else count = combCounts[n_remaining * (k + 1) + k_remaining];

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
