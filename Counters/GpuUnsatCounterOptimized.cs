using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// Optimized GPU-accelerated UNSAT counter using V2 chunking architecture.
/// 
/// Strategy: Sequential Chunk Processing (same as GpuMinUnsatCounterOptimizedV2).
/// Each GPU thread processes a sequence of N combinations.
/// 
/// Key simplification vs MIN-UNSAT:
/// - No minimality check needed
/// - No variable coverage check needed
/// - No canonical/orbit counting needed
/// - Just: OR all masks, check if == allAssignmentsMask
/// 
/// Expected performance: Faster than MIN-UNSAT due to simpler check.
/// </summary>
public class GpuUnsatCounterOptimized : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private bool _disposed;

    private const int MaxClausesSupported = 20;
    private const int ChunkSize = 1024;
    private const int BlockSize = 256;

    public bool IsGpu => _accelerator is not CPUAccelerator;
    public string AcceleratorName => _accelerator.Name;

    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<long>,
        int, int, ulong, long, int, ArrayView<int>
    > _kernelV2;

    public GpuUnsatCounterOptimized(bool preferGpu = true)
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

        Console.WriteLine($"[GpuUnsatOptimized] Using accelerator: {_accelerator.Name}");

        _kernelV2 = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<long>,
            int, int, ulong, long, int, ArrayView<int>
        >(BatchKernelV2);
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
        CancellationToken ct, bool verbose = true)
    {
        return CountInternal(numVariables, literalsPerClause, numClauses, ct, verbose);
    }

    private CountingResult CountInternal(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose)
    {
        var result = new CountingResult();

        if (numVariables > 6)
            throw new ArgumentOutOfRangeException(nameof(numVariables), "GPU kernel limited to n <= 6");
        if (numClauses < 1)
            throw new ArgumentOutOfRangeException(nameof(numClauses), "Need at least 1 clause");
        if (numClauses > MaxClausesSupported)
            throw new ArgumentOutOfRangeException(nameof(numClauses), $"Max supported clauses is {MaxClausesSupported}");

        // Build lookup tables
        var (_, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        ulong[] clauseMasks = ClauseMaskBuilder.BuildClauseMasks(numVariables, literalsPerClause);

        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        if (verbose)
        {
            Console.WriteLine($"[GpuUnsatOptimized] Configuration: v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[GpuUnsatOptimized] Total clause types: {totalClauses}");
            Console.WriteLine($"[GpuUnsatOptimized] Total combinations: {totalCombinations:N0}");
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

        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMask = numAssignments >= 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;

        // --- GPU Allocation ---
        using var gpuClauseMasks = _accelerator.Allocate1D<ulong>(clauseMasks.Length);
        gpuClauseMasks.CopyFromCPU(clauseMasks);

        using var gpuCombCounts = _accelerator.Allocate1D<long>(combCounts.Length);
        gpuCombCounts.CopyFromCPU(combCounts);

        // Grid Configuration
        long totalChunks = (totalCombinations + ChunkSize - 1) / ChunkSize;
        
        int chunksPerBatch = 500_000;
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
                if (verbose) Console.WriteLine($"\n[GpuUnsatOptimized] Cancelled");
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

            _kernelV2(config,
                gpuClauseMasks.View, gpuCombCounts.View,
                totalClauses, numClauses,
                allAssignmentsMask, startChunkIndex, currentBatchChunks, gpuResults.View);

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
                Console.WriteLine($"[GpuUnsatOptimized] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }
        }

        if (pendingSumTask != null) totalCount += pendingSumTask.Result;
        sw.Stop();

        if (verbose)
        {
            Console.WriteLine($"\n=== UNSAT Results (Optimized) ===");
            Console.WriteLine($"Time: {sw.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"Count: {totalCount:N0}");
            Console.WriteLine($"Rate: {totalCombinations / sw.Elapsed.TotalSeconds:N0}/s");
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

    /// <summary>
    /// V2 UNSAT Kernel - Simple OR check with chunking.
    /// Much simpler than MIN-UNSAT: just check if all assignments are killed.
    /// </summary>
    private static void BatchKernelV2(
        ArrayView<ulong> clauseMasks,
        ArrayView<long> combCounts,
        int totalClauses,
        int clausesPerFormula,
        ulong allAssignmentsMask,
        long startChunkIndex,
        int chunksToProcess,
        ArrayView<int> results)
    {
        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);
        long combinationStart = (startChunkIndex + globalId) * ChunkSize;
        
        UnrankCombination(combinationStart, totalClauses, clausesPerFormula, combCounts, localIndices);

        int validCount = 0;

        for (int i = 0; i < ChunkSize; i++)
        {
            // Simple UNSAT check: OR all clause masks
            ulong combined = 0;
            
            for (int k = 0; k < clausesPerFormula; k++)
            {
                combined |= clauseMasks[localIndices[k]];
            }
            
            // Check if UNSAT (all assignments killed)
            if (combined == allAssignmentsMask)
                validCount++;

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

    // =================================== HELPER FUNCTIONS ===================================

    private static void UnrankCombination(
        long index, int n, int k,
        ArrayView<long> combCounts,
        ArrayView<int> result)
    {
        long remaining = index;
        int nextStart = 0;

        for (int pos = 0; pos < k; pos++)
        {
            int r = k - pos - 1;
            int nPrime = n - nextStart;
            long cMax = GetCombCount(combCounts, nPrime, r + 1, k);
            long target = cMax - remaining;

            int low = r, high = nPrime, y = high;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (GetCombCount(combCounts, mid, r + 1, k) >= target)
                { y = mid; high = mid - 1; }
                else low = mid + 1;
            }

            long cY = GetCombCount(combCounts, y, r + 1, k);
            int clauseIdx = n - y;
            result[pos] = clauseIdx;
            remaining = cY - target;
            nextStart = clauseIdx + 1;
        }
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

    private static long GetCombCount(ArrayView<long> combCounts, int n, int k, int maxK)
    {
        if (k < 0 || k > n || n < 0) return 0;
        return combCounts[n * (maxK + 1) + k];
    }
}
