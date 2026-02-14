using System.Diagnostics;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

namespace MinUnsatPublish.Counters;

/// <summary>
/// V3 CPU-Prefix GPU-Suffix Hybrid MIN-UNSAT counter.
/// 
/// Strategy: Split the c-clause formula into a prefix (first 2 clauses, enumerated on CPU)
/// and a suffix (remaining c-2 clauses, enumerated on GPU). The CPU applies pruning checks
/// to each prefix before dispatching GPU work, skipping entire suffix subtrees that can't
/// possibly produce MIN-UNSAT formulas.
/// 
/// Pruning checks:
///   1. Coverage feasibility — remaining clauses can't cover all 2^v assignments
///   2. Variable feasibility — remaining clauses can't use all v variables
///   3. Capacity feasibility — not enough suffix slots for missing assignments
/// </summary>
public class GpuMinUnsatCounterV3 : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private bool _disposed;

    private const int MaxClausesSupported = 20;
    private const int ChunkSize = 1024;
    private const int BlockSize = 256;

    public bool IsGpu => _accelerator is not CPUAccelerator;
    public string AcceleratorName => _accelerator.Name;

    private const int PrefixStateSize = 10; // longs per prefix (v≤6)
    private const int PrefixStateSize128 = 14; // longs per prefix (v=7, 128-bit masks)

    // V3 Batched Suffix Kernel — all prefixes flattened into one work space (v≤6, 64-bit masks)
    // SpecializedValue: allAssignmentsMask (constant per v), chunksToProcess (≤2 distinct values per run)
    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
        ArrayView<byte>, ArrayView<long>, ArrayView<long>,
        SpecializedValue<ulong>, long, SpecializedValue<int>, ArrayView<int>
    > _kernelV3Batched;

    // V3 Batched Suffix Kernel — 128-bit masks for v=7
    // clauseMasks: 2 ulongs per clause (lo,hi interleaved), pos/neg: ulong (35-bit fields)
    // SpecializedValue: chunksToProcess (≤2 distinct values per run)
    private readonly Action<KernelConfig,
        ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<ulong>, ArrayView<ulong>,
        ArrayView<byte>, ArrayView<long>, ArrayView<long>,
        long, SpecializedValue<int>, ArrayView<int>
    > _kernelV3Batched128;

    public GpuMinUnsatCounterV3(bool preferGpu = true)
    {
        _context = Context.Create(builder => builder
            .Default()
            .Optimize(OptimizationLevel.O2)
            .Inlining(InliningMode.Aggressive));

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

        Console.WriteLine($"[GpuV3] Using accelerator: {_accelerator.Name}");

        _kernelV3Batched = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<uint>, ArrayView<uint>,
            ArrayView<byte>, ArrayView<long>, ArrayView<long>,
            SpecializedValue<ulong>, long, SpecializedValue<int>, ArrayView<int>
        >(V3SuffixKernelBatched);

        _kernelV3Batched128 = _accelerator.LoadStreamKernel<
            ArrayView<ulong>, ArrayView<long>, ArrayView<int>, ArrayView<ulong>, ArrayView<ulong>,
            ArrayView<byte>, ArrayView<long>, ArrayView<long>,
            long, SpecializedValue<int>, ArrayView<int>
        >(V3SuffixKernelBatched128);
    }

    private CPUAccelerator CreateCpuAccelerator()
    {
        int processorCount = Environment.ProcessorCount;
        int numMultiprocessors = Math.Max(1, processorCount / 2);
        var cpuDevice = new CPUDevice(2, 1, numMultiprocessors);
        return cpuDevice.CreateCPUAccelerator(_context);
    }

    public long Count(int numVariables, int literalsPerClause, int numClauses, bool verbose = true)
    {
        var result = CountInternal(numVariables, literalsPerClause, numClauses, CancellationToken.None, verbose);
        return result.Count;
    }

    public CountingResult CountCancellable(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose = true, bool useCheckpoint = false, int prefixDepth = 0)
    {
        return CountInternal(numVariables, literalsPerClause, numClauses, ct, verbose, useCheckpoint, prefixDepth);
    }

    private CountingResult CountInternal(int numVariables, int literalsPerClause, int numClauses,
        CancellationToken ct, bool verbose, bool useCheckpoint = false, int prefixDepth = 0)
    {
        // Special Case: Exact Cover for 3-SAT (c=8)
        if (literalsPerClause == 3 && numClauses == 8)
        {
            if (verbose) Console.WriteLine("[V3] Check: Dedicated Exact Cover Path (c=8, l=3) triggered...");
            try 
            {
                var exactCover = new MinUnsatPublish.Helpers.ExactCover3SatForV3();
                var swExact = Stopwatch.StartNew();
                long exactCount = exactCover.Count(numVariables);
                swExact.Stop();
                return new CountingResult 
                { 
                    Count = exactCount, 
                    ProcessedCombinations = 1, // Represents "done"
                    TotalCombinations = 1,
                    WasCancelled = false,
                    ElapsedMs = swExact.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"[V3] Exact Cover Path failed: {ex.Message}. Falling back.");
            }
        }

        // NearExactCover for c=9 removed — V3 GPU prefix-suffix hybrid handles all clause counts.
        // (NearExactCover3Sat is retained in Helpers for reference/validation.)

        var result = new CountingResult();
        
        if (numVariables > 7)
            throw new ArgumentOutOfRangeException(nameof(numVariables),
                "V3 kernel limited to 7 variables (uses 128-bit assignment mask).");
        if (literalsPerClause != 3)
            throw new ArgumentOutOfRangeException(nameof(literalsPerClause),
                "V3 is designed for 3-SAT only.");
        if (numClauses > MaxClausesSupported)
            throw new ArgumentOutOfRangeException(nameof(numClauses), $"Max supported clauses is {MaxClausesSupported}");

        // Auto-select prefix depth: P=2 for c<=12, P=3 for c>12
        int P = prefixDepth;
        if (P == 0)
            P = numClauses > 12 ? 3 : 2;
        if (P < 2 || P > 3)
            throw new ArgumentOutOfRangeException(nameof(prefixDepth), "Prefix depth must be 2 or 3.");
        if (numClauses < P + 1)
            throw new ArgumentOutOfRangeException(nameof(numClauses), $"V3 needs at least {P + 1} clauses (prefix={P}).");

        int suffixC = numClauses - P; // suffix clause count
        bool use128 = numVariables > 6; // v=7 needs 128-bit assignment masks

        // Build lookup tables (same as V2)
        var (flatLits, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(numVariables, literalsPerClause);
        int[] clauseVarMasks = ClauseLiteralMapper.BuildClauseVariableMasks(numVariables, literalsPerClause);

        // Build clause masks: 2 ulongs per clause (lo,hi) — hi=0 for v≤6
        ulong[] clauseMasks128 = new ulong[totalClauses * 2];
        if (!use128)
        {
            ulong[] masks64 = ClauseMaskBuilder.BuildClauseMasks(numVariables, literalsPerClause);
            for (int c = 0; c < totalClauses; c++)
            {
                clauseMasks128[2 * c] = masks64[c];
                clauseMasks128[2 * c + 1] = 0;
            }
        }
        else
        {
            // Build 128-bit masks from literal data
            int numAssign = 1 << numVariables;
            for (int c = 0; c < totalClauses; c++)
            {
                int bIdx = c * literalsPerClause * 2;
                int[] lVars = new int[literalsPerClause];
                int[] lPols = new int[literalsPerClause];
                for (int l = 0; l < literalsPerClause; l++)
                {
                    lVars[l] = flatLits[bIdx + l * 2];
                    lPols[l] = flatLits[bIdx + l * 2 + 1];
                }
                ulong maskLo = 0, maskHi = 0;
                for (int a = 0; a < numAssign; a++)
                {
                    bool allFalse = true;
                    for (int l = 0; l < literalsPerClause; l++)
                    {
                        int varBit = (a >> lVars[l]) & 1;
                        bool litTrue = (lPols[l] == 1) ? (varBit == 1) : (varBit == 0);
                        if (litTrue) { allFalse = false; break; }
                    }
                    if (allFalse)
                    {
                        if (a < 64) maskLo |= 1UL << a;
                        else maskHi |= 1UL << (a - 64);
                    }
                }
                clauseMasks128[2 * c] = maskLo;
                clauseMasks128[2 * c + 1] = maskHi;
            }
        }

        // Build pos/neg packed: ulong for v=7 (35-bit fields), uint-compatible for v≤6
        ulong[] clausePosPackedWide = new ulong[totalClauses];
        ulong[] clauseNegPackedWide = new ulong[totalClauses];
        uint[] clausePosPacked32 = use128 ? null! : new uint[totalClauses];
        uint[] clauseNegPacked32 = use128 ? null! : new uint[totalClauses];
        for (int c = 0; c < totalClauses; c++)
        {
            int baseIdx = c * literalsPerClause * 2;
            ulong pos = 0, neg = 0;
            for (int lit = 0; lit < literalsPerClause; lit++)
            {
                int varNo = flatLits[baseIdx];
                int polarity = flatLits[baseIdx + 1];
                int shift = varNo * 5;
                if (polarity == 1) pos += 1UL << shift;
                else neg += 1UL << shift;
                baseIdx += 2;
            }
            clausePosPackedWide[c] = pos;
            clauseNegPackedWide[c] = neg;
            if (!use128)
            {
                clausePosPacked32[c] = (uint)pos;
                clauseNegPacked32[c] = (uint)neg;
            }
        }

        int allVariablesMask = (1 << numVariables) - 1;
        int numAssignments = 1 << numVariables;
        ulong allAssignmentsMaskLo, allAssignmentsMaskHi;
        if (numAssignments <= 64)
        {
            allAssignmentsMaskLo = numAssignments == 64 ? ulong.MaxValue : (1UL << numAssignments) - 1;
            allAssignmentsMaskHi = 0;
        }
        else
        {
            allAssignmentsMaskLo = ulong.MaxValue;
            int hiBits = numAssignments - 64;
            allAssignmentsMaskHi = hiBits == 64 ? ulong.MaxValue : (1UL << hiBits) - 1;
        }

        // Build group masks for pruning
        byte[] clauseGroupMask = BuildRequiredClauseMask128(clauseMasks128, numAssignments, totalClauses, verbose);

        // Precompute suffixCoverable[] — backward OR of clauseMasks (lo/hi)
        ulong[] suffixCoverableLo = new ulong[totalClauses];
        ulong[] suffixCoverableHi = new ulong[totalClauses];
        suffixCoverableLo[totalClauses - 1] = 0;
        suffixCoverableHi[totalClauses - 1] = 0;
        for (int i = totalClauses - 2; i >= 0; i--)
        {
            suffixCoverableLo[i] = suffixCoverableLo[i + 1] | clauseMasks128[2 * (i + 1)];
            suffixCoverableHi[i] = suffixCoverableHi[i + 1] | clauseMasks128[2 * (i + 1) + 1];
        }

        // Precompute suffixVarCoverable[] — backward OR of clauseVarMasks
        int[] suffixVarCoverable = new int[totalClauses];
        suffixVarCoverable[totalClauses - 1] = 0;
        for (int i = totalClauses - 2; i >= 0; i--)
            suffixVarCoverable[i] = suffixVarCoverable[i + 1] | clauseVarMasks[i + 1];

        // Total combinations for reporting
        long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, numClauses);
        result.TotalCombinations = totalCombinations;

        // Build combination counts table (needs to support suffix enumeration)
        // Max suffix N = totalClauses - 2, max suffix C = numClauses - 2
        // We need C(n, k) for all n up to totalClauses, k up to numClauses
        int combTableWidth = numClauses + 1;
        long[] combCounts = new long[(totalClauses + 1) * combTableWidth];
        for (int available = 0; available <= totalClauses; available++)
        {
            for (int choose = 0; choose <= numClauses; choose++)
            {
                combCounts[available * combTableWidth + choose] = CombinationGenerator.CountCombinations(available, choose);
            }
        }

        if (verbose)
        {
            Console.WriteLine($"[V3] Configuration: v={numVariables}, l={literalsPerClause}, c={numClauses}");
            Console.WriteLine($"[V3] Total clause types: {totalClauses}");
            Console.WriteLine($"[V3] Total combinations: {totalCombinations:N0}");
            Console.WriteLine($"[V3] Prefix depth: {P}, Suffix clauses: {suffixC}");
        }

        // === Phase 1: Enumerate all prefixes and apply pruning ===
        int coveragePerClause = 1 << (numVariables - literalsPerClause); // 2^(v-k)

        long totalPrefixes = 0;
        long validPrefixes = 0;
        long prunedCoverage = 0, prunedVariable = 0, prunedCapacity = 0;
        long totalEffectiveCombinations = 0;
        long totalPrunedCombinations = 0;

        // Build prefix list using generalized P-level combination enumeration
        var validPrefixList = new List<(long suffixCombinations, long[] prefixState)>();
        int stateSize = use128 ? PrefixStateSize128 : PrefixStateSize;

        // Initialize prefix indices: {0, 1, ..., P-1}
        int[] prefixIndices = new int[P];
        for (int i = 0; i < P; i++) prefixIndices[i] = i;

        // Upper bound for last prefix index: need suffixC clauses after it
        int maxLastPrefixIdx = totalClauses - suffixC - 1;

        while (true)
        {
            int cLast = prefixIndices[P - 1];
            if (cLast <= maxLastPrefixIdx)
            {
                totalPrefixes++;

                int suffixN = totalClauses - cLast - 1;
                long suffixCombinations = CombinationGenerator.CountCombinations(suffixN, suffixC);

                // Compute prefix state by iterating over P clauses (128-bit capable)
                ulong prefixOneLo = 0, prefixOneHi = 0;
                ulong prefixTwoLo = 0, prefixTwoHi = 0;
                int prefixVars = 0;
                ulong prefixPos = 0, prefixNeg = 0;
                byte prefixGrp = 0;

                for (int pi = 0; pi < P; pi++)
                {
                    int ci = prefixIndices[pi];
                    ulong mLo = clauseMasks128[2 * ci];
                    ulong mHi = clauseMasks128[2 * ci + 1];
                    prefixTwoLo |= prefixOneLo & mLo;
                    prefixTwoHi |= prefixOneHi & mHi;
                    prefixOneLo |= mLo;
                    prefixOneHi |= mHi;
                    prefixVars |= clauseVarMasks[ci];
                    prefixPos += clausePosPackedWide[ci];
                    prefixNeg += clauseNegPackedWide[ci];
                    prefixGrp |= clauseGroupMask[ci];
                }

                // Pruning Check 1: Coverage feasibility
                if ((suffixCoverableLo[cLast] | prefixOneLo) != allAssignmentsMaskLo ||
                    (suffixCoverableHi[cLast] | prefixOneHi) != allAssignmentsMaskHi)
                {
                    prunedCoverage++;
                    totalPrunedCombinations += suffixCombinations;
                }
                // Pruning Check 2: Variable feasibility
                else if ((suffixVarCoverable[cLast] | prefixVars) != allVariablesMask)
                {
                    prunedVariable++;
                    totalPrunedCombinations += suffixCombinations;
                }
                // Pruning Check 3: Capacity feasibility
                else if (numAssignments
                         - BitOperations.PopCount(prefixOneLo & allAssignmentsMaskLo)
                         - BitOperations.PopCount(prefixOneHi & allAssignmentsMaskHi)
                         > suffixC * coveragePerClause)
                {
                    prunedCapacity++;
                    totalPrunedCombinations += suffixCombinations;
                }
                else
                {
                    // Valid prefix — pack state for GPU
                    validPrefixes++;
                    totalEffectiveCombinations += suffixCombinations;

                    ulong packedIndices = 0;
                    for (int pi = 0; pi < P; pi++)
                        packedIndices |= ((ulong)(ushort)prefixIndices[pi]) << (pi * 16);

                    long[] prefixState = new long[stateSize];
                    if (use128)
                    {
                        prefixState[0] = (long)prefixOneLo;
                        prefixState[1] = (long)prefixOneHi;
                        prefixState[2] = (long)prefixTwoLo;
                        prefixState[3] = (long)prefixTwoHi;
                        prefixState[4] = (long)prefixVars;
                        prefixState[5] = (long)prefixPos;
                        prefixState[6] = (long)prefixNeg;
                        prefixState[7] = (long)prefixGrp;
                        prefixState[8] = (long)packedIndices;
                        prefixState[9] = cLast + 1;
                        prefixState[10] = suffixN;
                        prefixState[11] = suffixC;
                        prefixState[12] = numVariables | (P << 16);
                        prefixState[13] = 0;
                    }
                    else
                    {
                        prefixState[0] = (long)prefixOneLo;
                        prefixState[1] = (long)prefixTwoLo;
                        prefixState[2] = (long)prefixVars;
                        prefixState[3] = (long)(((ulong)(uint)prefixPos << 32) | (uint)prefixNeg);
                        prefixState[4] = (long)prefixGrp;
                        prefixState[5] = (long)packedIndices;
                        prefixState[6] = cLast + 1;
                        prefixState[7] = suffixN;
                        prefixState[8] = suffixC;
                        prefixState[9] = numVariables | (P << 16);
                    }

                    validPrefixList.Add((suffixCombinations, prefixState));
                }
            }

            // Advance to next P-combination
            int pos = P - 1;
            while (pos >= 0 && prefixIndices[pos] >= maxLastPrefixIdx - (P - 1 - pos))
                pos--;
            if (pos < 0) break;
            prefixIndices[pos]++;
            for (int j = pos + 1; j < P; j++)
                prefixIndices[j] = prefixIndices[j - 1] + 1;
        }

        double prunedFraction = totalCombinations > 0 ? 100.0 * totalPrunedCombinations / totalCombinations : 0;

        if (verbose)
        {
            Console.WriteLine($"[V3] Prefixes: {totalPrefixes:N0} total, {validPrefixes:N0} valid ({prunedFraction:F1}% pruned)");
            Console.WriteLine($"[V3]   Coverage pruned: {prunedCoverage:N0}, Variable pruned: {prunedVariable:N0}, Capacity pruned: {prunedCapacity:N0}");
            Console.WriteLine($"[V3] Effective: {totalEffectiveCombinations:N0} / {totalCombinations:N0} combinations ({prunedFraction:F1}% pruned)");
        }

        // === Phase 2: Work-flattened GPU dispatch ===
        // All prefix suffix spaces are flattened into one global chunk space.
        // Each GPU thread binary-searches cumulativeChunks to find its prefix.
        // This eliminates per-prefix upload/sync/copy overhead entirely.

        // Build flat prefix state array (stateSize longs per prefix)
        int numValid = validPrefixList.Count;
        long[] allPrefixStates = new long[numValid * stateSize];
        for (int p = 0; p < numValid; p++)
        {
            var ps = validPrefixList[p].prefixState;
            for (int j = 0; j < stateSize; j++)
                allPrefixStates[p * stateSize + j] = ps[j];
        }

        // Build cumulative chunk counts for binary search
        // cumulativeChunks[p] = total chunks for prefixes 0..p-1
        // cumulativeChunks[numValid] = total chunks across all prefixes
        long[] cumulativeChunks = new long[numValid + 1];
        cumulativeChunks[0] = 0;
        for (int p = 0; p < numValid; p++)
        {
            long suffixCombs = validPrefixList[p].suffixCombinations;
            long chunks = (suffixCombs + ChunkSize - 1) / ChunkSize;
            cumulativeChunks[p + 1] = cumulativeChunks[p] + chunks;
        }
        long totalEffectiveChunks = cumulativeChunks[numValid];

        if (verbose)
        {
            Console.WriteLine($"[V3] Total effective chunks: {totalEffectiveChunks:N0}");
        }

        // GPU Allocation
        using var gpuClauseMasks = _accelerator.Allocate1D<ulong>(clauseMasks128.Length);
        gpuClauseMasks.CopyFromCPU(clauseMasks128);

        using var gpuCombCounts = _accelerator.Allocate1D<long>(combCounts.Length);
        gpuCombCounts.CopyFromCPU(combCounts);

        using var gpuClauseVarMasks = _accelerator.Allocate1D<int>(clauseVarMasks.Length);
        gpuClauseVarMasks.CopyFromCPU(clauseVarMasks);

        // For v≤6: uint pos/neg; for v=7: ulong pos/neg
        MemoryBuffer1D<uint, Stride1D.Dense>? gpuPosPacked32 = null;
        MemoryBuffer1D<uint, Stride1D.Dense>? gpuNegPacked32 = null;
        MemoryBuffer1D<ulong, Stride1D.Dense>? gpuPosPackedWide = null;
        MemoryBuffer1D<ulong, Stride1D.Dense>? gpuNegPackedWide = null;
        if (use128)
        {
            gpuPosPackedWide = _accelerator.Allocate1D<ulong>(clausePosPackedWide.Length);
            gpuPosPackedWide.CopyFromCPU(clausePosPackedWide);
            gpuNegPackedWide = _accelerator.Allocate1D<ulong>(clauseNegPackedWide.Length);
            gpuNegPackedWide.CopyFromCPU(clauseNegPackedWide);
        }
        else
        {
            gpuPosPacked32 = _accelerator.Allocate1D<uint>(clausePosPacked32.Length);
            gpuPosPacked32.CopyFromCPU(clausePosPacked32);
            gpuNegPacked32 = _accelerator.Allocate1D<uint>(clauseNegPacked32.Length);
            gpuNegPacked32.CopyFromCPU(clauseNegPacked32);
        }

        using var gpuGroupMask = _accelerator.Allocate1D<byte>(clauseGroupMask.Length);
        gpuGroupMask.CopyFromCPU(clauseGroupMask);

        // Upload all prefix states + cumulative chunks (once!)
        using var gpuAllPrefixStates = _accelerator.Allocate1D<long>(allPrefixStates.Length);
        gpuAllPrefixStates.CopyFromCPU(allPrefixStates);

        using var gpuCumulativeChunks = _accelerator.Allocate1D<long>(cumulativeChunks.Length);
        gpuCumulativeChunks.CopyFromCPU(cumulativeChunks);

        int chunksPerBatch = 500_000;
        int maxGridSize = (chunksPerBatch + BlockSize - 1) / BlockSize;
        using var gpuResults = _accelerator.Allocate1D<int>(maxGridSize);
        int[] cpuResults = new int[maxGridSize];

        long totalCount = 0;
        long processedChunks = 0;
        long elapsedMsBeforeResume = 0;
        Task<long>? pendingSumTask = null;

        // Try to resume from checkpoint
        CalculationCheckpoint? checkpoint = null;
        if (useCheckpoint)
        {
            checkpoint = CalculationCheckpoint.TryLoad(numVariables, literalsPerClause, numClauses);
            if (checkpoint != null && checkpoint.ProcessedCombinations > 0 && checkpoint.ProcessedCombinations < totalEffectiveChunks * ChunkSize)
            {
                // Resume from checkpoint — ProcessedCombinations stores effective chunks * ChunkSize
                long resumeFromChunk = checkpoint.ProcessedCombinations / ChunkSize;
                processedChunks = resumeFromChunk;
                totalCount = checkpoint.CurrentCount;
                elapsedMsBeforeResume = checkpoint.ElapsedMsBeforeCheckpoint;

                if (verbose)
                {
                    double resumeProgress = 100.0 * processedChunks / totalEffectiveChunks;
                    Console.WriteLine($"[V3] Resuming from checkpoint: {resumeProgress:F1}% ({checkpoint.ProcessedCombinations:N0} effective combinations)");
                    Console.WriteLine($"[V3] Prior count: {totalCount:N0}, Prior time: {elapsedMsBeforeResume / 1000.0:F1}s");
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
                    TotalCombinations = totalEffectiveChunks * ChunkSize
                };
            }
        }

        var sw = Stopwatch.StartNew();
        var progressSw = Stopwatch.StartNew();
        var checkpointSw = Stopwatch.StartNew();

        while (processedChunks < totalEffectiveChunks)
        {
            if (ct.IsCancellationRequested)
            {
                if (verbose) Console.WriteLine($"\n[V3] Cancelled");

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
                    if (verbose) Console.WriteLine($"[V3] Checkpoint saved: {processedChunks:N0}/{totalEffectiveChunks:N0} chunks, count={totalCount:N0}");
                }

                if (pendingSumTask != null) totalCount += pendingSumTask.Result;
                result.Count = totalCount;
                result.WasCancelled = true;
                result.ProcessedCombinations = processedChunks * ChunkSize;
                result.ElapsedMs = elapsedMsBeforeResume + sw.ElapsedMilliseconds;
                return result;
            }

            long remainingChunks = totalEffectiveChunks - processedChunks;
            int currentBatchChunks = (int)Math.Min(remainingChunks, chunksPerBatch);

            int gridSize = (currentBatchChunks + BlockSize - 1) / BlockSize;
            var config = new KernelConfig(gridSize, BlockSize);

            gpuResults.MemSetToZero();

            if (use128)
            {
                _kernelV3Batched128(config,
                    gpuClauseMasks.View, gpuCombCounts.View, gpuClauseVarMasks.View,
                    gpuPosPackedWide!.View, gpuNegPackedWide!.View,
                    gpuGroupMask.View, gpuAllPrefixStates.View, gpuCumulativeChunks.View,
                    processedChunks, SpecializedValue.New(currentBatchChunks),
                    gpuResults.View);
            }
            else
            {
                _kernelV3Batched(config,
                    gpuClauseMasks.View, gpuCombCounts.View, gpuClauseVarMasks.View,
                    gpuPosPacked32!.View, gpuNegPacked32!.View,
                    gpuGroupMask.View, gpuAllPrefixStates.View, gpuCumulativeChunks.View,
                    SpecializedValue.New(allAssignmentsMaskLo), processedChunks, SpecializedValue.New(currentBatchChunks),
                    gpuResults.View);
            }

            _accelerator.Synchronize();

            // Overlap: sum previous batch while GPU finished
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
                double progress = 100.0 * processedChunks / totalEffectiveChunks;
                double totalElapsedSeconds = (elapsedMsBeforeResume / 1000.0) + sw.Elapsed.TotalSeconds;
                double rate = currentProcessed / Math.Max(0.001, totalElapsedSeconds);
                long remaining = (totalEffectiveChunks - processedChunks) * ChunkSize;
                string etaStr = "";
                if (rate > 0 && remaining > 0)
                {
                    var eta = TimeSpan.FromSeconds(remaining / rate);
                    etaStr = eta.TotalHours >= 1 ? $", ETA: {(int)eta.TotalHours}h {eta.Minutes}m" :
                             eta.TotalMinutes >= 1 ? $", ETA: {eta.Minutes}m {eta.Seconds}s" :
                             $", ETA: {eta.Seconds}s";
                }
                Console.WriteLine($"[V3] Progress: {progress:F1}%, Rate: {rate:N0}/s{etaStr}");
                progressSw.Restart();
            }

            // Save checkpoint every 30 seconds
            if (useCheckpoint && checkpoint != null && checkpointSw.Elapsed.TotalSeconds >= 30)
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

                if (verbose)
                {
                    Console.WriteLine($"[V3] Checkpoint saved: {processedChunks:N0}/{totalEffectiveChunks:N0} chunks, count={totalCount:N0}");
                }
                checkpointSw.Restart();
            }
        }

        if (pendingSumTask != null) totalCount += pendingSumTask.Result;
        sw.Stop();

        gpuPosPacked32?.Dispose();
        gpuNegPacked32?.Dispose();
        gpuPosPackedWide?.Dispose();
        gpuNegPackedWide?.Dispose();

        // Delete checkpoint on successful completion
        if (useCheckpoint && checkpoint != null)
        {
            checkpoint.Delete();
            if (verbose) Console.WriteLine($"[V3] Checkpoint deleted (completed successfully)");
        }

        long totalElapsedMs = elapsedMsBeforeResume + sw.ElapsedMilliseconds;

        if (verbose)
        {
            Console.WriteLine($"\n=== Results V3 ===");
            Console.WriteLine($"Time: {totalElapsedMs / 1000.0:F2}s" + (elapsedMsBeforeResume > 0 ? $" (resumed, this session: {sw.Elapsed.TotalSeconds:F2}s)" : ""));
            Console.WriteLine($"Count: {totalCount:N0}");
            double overallRate = (totalEffectiveChunks * ChunkSize) / Math.Max(0.001, totalElapsedMs / 1000.0);
            Console.WriteLine($"Rate: {overallRate:N0}/s (effective combinations)");
            Console.WriteLine($"Pruned: {totalPrunedCombinations:N0} combinations ({prunedFraction:F1}%)");
        }

        result.Count = totalCount;
        result.ProcessedCombinations = totalCombinations;
        result.ElapsedMs = totalElapsedMs;
        return result;
    }

    private static byte[] BuildRequiredClauseMask128(ulong[] clauseMasks128, int numAssignments, int totalClauses, bool verbose)
    {
        // Build multi-assignment coverage mask per clause (supports 128-bit assignments)
        int[] coverageCount = new int[numAssignments];
        for (int a = 0; a < numAssignments; a++)
        {
            int wordOff = a < 64 ? 0 : 1;
            ulong aBit = 1UL << (a & 63);
            for (int c = 0; c < totalClauses; c++)
                if ((clauseMasks128[2 * c + wordOff] & aBit) != 0) coverageCount[a]++;
        }

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

            int bestWordOff = best < 64 ? 0 : 1;
            ulong bestBit = 1UL << (best & 63);

            for (int a = 0; a < numAssignments; a++)
            {
                if (used[a]) continue;
                int aWordOff = a < 64 ? 0 : 1;
                ulong aBit = 1UL << (a & 63);
                int overlap = 0;
                for (int c = 0; c < totalClauses; c++)
                {
                    bool coversA = (clauseMasks128[2 * c + aWordOff] & aBit) != 0;
                    bool coversBest = (clauseMasks128[2 * c + bestWordOff] & bestBit) != 0;
                    if (coversA && coversBest) overlap++;
                }
                if (overlap * 100 / Math.Max(1, coverageCount[a]) > 80)
                    used[a] = true;
            }
        }

        if (verbose)
        {
            Console.Write($"[V3 Pruning] Multi-assignment: {numGroups} groups, coverage counts: ");
            for (int g = 0; g < numGroups; g++)
                Console.Write($"{coverageCount[selectedAssignments[g]]}/{totalClauses}{(g < numGroups - 1 ? ", " : "")}");
            Console.WriteLine();
        }

        byte unusedGroupBits = (byte)(0xFF & ~((1 << numGroups) - 1));
        byte[] clauseGroupMaskArr = new byte[totalClauses];
        for (int c = 0; c < totalClauses; c++)
        {
            byte mask = unusedGroupBits;
            for (int g = 0; g < numGroups; g++)
            {
                int sa = selectedAssignments[g];
                int wOff = sa < 64 ? 0 : 1;
                ulong sBit = 1UL << (sa & 63);
                if ((clauseMasks128[2 * c + wOff] & sBit) != 0)
                    mask |= (byte)(1 << g);
            }
            clauseGroupMaskArr[c] = mask;
        }

        return clauseGroupMaskArr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accelerator.Dispose();
        _context.Dispose();
    }

    // =================================== V3 BATCHED SUFFIX KERNEL ===================================

    /// <summary>
    /// Work-flattened kernel: all prefixes' suffix spaces are merged into one global chunk space.
    /// Each thread binary-searches cumulativeChunks to find its prefix, then processes
    /// ChunkSize suffix combinations within that prefix's space.
    /// </summary>
    private static void V3SuffixKernelBatched(
        ArrayView<ulong> clauseMasks,
        ArrayView<long> combCounts,
        ArrayView<int> clauseVarMasks,
        ArrayView<uint> clausePosPacked,
        ArrayView<uint> clauseNegPacked,
        ArrayView<byte> clauseGroupMask,
        ArrayView<long> allPrefixStates,
        ArrayView<long> cumulativeChunks,
        SpecializedValue<ulong> allAssignmentsMask,
        long startGlobalChunk,
        SpecializedValue<int> chunksToProcess,
        ArrayView<int> results)
    {
        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        long globalChunkIdx = startGlobalChunk + globalId;

        // Binary search: find which prefix owns this chunk
        // cumulativeChunks[p] = first chunk of prefix p
        // cumulativeChunks[numPrefixes] = total chunks
        int numPrefixes = (int)(cumulativeChunks.Length - 1);
        int lo = 0, hi = numPrefixes - 1;
        int prefixIdx = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cumulativeChunks[mid + 1] <= globalChunkIdx)
                lo = mid + 1;
            else if (cumulativeChunks[mid] > globalChunkIdx)
                hi = mid - 1;
            else
            {
                prefixIdx = mid;
                break;
            }
            prefixIdx = lo;
        }

        // Local chunk index within this prefix's suffix space
        long localChunkIdx = globalChunkIdx - cumulativeChunks[prefixIdx];

        // Read prefix state from flat array
        int stateBase = prefixIdx * PrefixStateSize;
        ulong pOne = (ulong)allPrefixStates[stateBase + 0];
        ulong pTwo = (ulong)allPrefixStates[stateBase + 1];
        int pVars = (int)allPrefixStates[stateBase + 2];
        uint pPos = (uint)((ulong)allPrefixStates[stateBase + 3] >> 32);
        uint pNeg = (uint)((ulong)allPrefixStates[stateBase + 3] & 0xFFFFFFFF);
        byte pGrp = (byte)allPrefixStates[stateBase + 4];
        ulong packedPrefixIndices = (ulong)allPrefixStates[stateBase + 5];
        int suffixStart = (int)allPrefixStates[stateBase + 6];
        int suffixN = (int)allPrefixStates[stateBase + 7];
        int suffixC = (int)allPrefixStates[stateBase + 8];
        // Slot 9 packs: variableCount in low 16 bits, prefixDepth in high 16 bits
        int variableCount = (int)((ulong)allPrefixStates[stateBase + 9] & 0xFFFF);
        int prefixDepthK = (int)(((ulong)allPrefixStates[stateBase + 9] >> 16) & 0xFFFF);

        int allVariablesMask = (1 << variableCount) - 1;
        byte allGroupsMask = 0xFF;
        int combTableWidth = prefixDepthK + suffixC + 1; // numClauses + 1

        // Allocate local indices for suffix combination
        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);

        long combinationStart = localChunkIdx * ChunkSize;

        // Unrank within this prefix's suffix space: C(suffixN, suffixC)
        UnrankCombination(combinationStart, suffixN, suffixC, combCounts, localIndices, combTableWidth);

        int validCount = 0;

        for (int i = 0; i < ChunkSize; i++)
        {
            // Initialize from prefix state
            ulong one = pOne, two = pTwo;
            int varCoverage = pVars;
            uint posPacked = pPos, negPacked = pNeg;
            byte groupCoverage = pGrp;

            // Add suffix clauses
            for (int k = 0; k < suffixC; k++)
            {
                int localIdx = localIndices[k];
                int globalIdx = suffixStart + localIdx;
                ulong m = clauseMasks[2 * globalIdx];
                two |= one & m;
                one |= m;
                varCoverage |= clauseVarMasks[globalIdx];
                posPacked += clausePosPacked[globalIdx];
                negPacked += clauseNegPacked[globalIdx];
                groupCoverage |= clauseGroupMask[globalIdx];
            }

            // Check group coverage first (cheapest filter), then UNSAT + AllVars
            if (groupCoverage == allGroupsMask &&
                one == allAssignmentsMask &&
                varCoverage == allVariablesMask)
            {
                // UNSAT. Check minimality.
                ulong unique = one & ~two;
                bool minimal = true;

                // Check all prefix clauses for minimality (P = 2 or 3)
                for (int pi = 0; pi < prefixDepthK; pi++)
                {
                    int pci = (int)((packedPrefixIndices >> (pi * 16)) & 0xFFFF);
                    if ((clauseMasks[2 * pci] & unique) == 0)
                    {
                        minimal = false;
                        break;
                    }
                }

                // Check suffix clauses
                if (minimal)
                {
                    for (int k = 0; k < suffixC; k++)
                    {
                        int globalIdx = suffixStart + localIndices[k];
                        if ((clauseMasks[2 * globalIdx] & unique) == 0)
                        {
                            minimal = false;
                            break;
                        }
                    }
                }

                if (minimal)
                {
                    // Check canonical (p >= n for all variables)
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

            // Move to next suffix combination
            if (!NextCombination(localIndices, suffixC, suffixN))
                break;
        }

        // Block reduction
        var sharedSum = SharedMemory.Allocate<int>(1);
        if (Group.IdxX == 0) sharedSum[0] = 0;
        Group.Barrier();

        if (validCount > 0) Atomic.Add(ref sharedSum[0], validCount);
        Group.Barrier();

        if (Group.IdxX == 0) results[Grid.IdxX] = sharedSum[0];
    }

    // =================================== V3 BATCHED SUFFIX KERNEL — 128-bit ===================================

    /// <summary>
    /// 128-bit variant for v=7 (128 assignments). clauseMasks are interleaved (2 ulongs per clause).
    /// Pos/neg packed use ulong (35-bit fields for 7 variables × 5 bits).
    /// PrefixStateSize128 = 14.
    /// </summary>
    private static void V3SuffixKernelBatched128(
        ArrayView<ulong> clauseMasks,
        ArrayView<long> combCounts,
        ArrayView<int> clauseVarMasks,
        ArrayView<ulong> clausePosPacked,
        ArrayView<ulong> clauseNegPacked,
        ArrayView<byte> clauseGroupMask,
        ArrayView<long> allPrefixStates,
        ArrayView<long> cumulativeChunks,
        long startGlobalChunk,
        SpecializedValue<int> chunksToProcess,
        ArrayView<int> results)
    {
        int globalId = Grid.GlobalIndex.X;
        if (globalId >= chunksToProcess) return;

        long globalChunkIdx = startGlobalChunk + globalId;

        // Binary search for prefix
        int numPrefixes = (int)(cumulativeChunks.Length - 1);
        int lo = 0, hi = numPrefixes - 1;
        int prefixIdx = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cumulativeChunks[mid + 1] <= globalChunkIdx)
                lo = mid + 1;
            else if (cumulativeChunks[mid] > globalChunkIdx)
                hi = mid - 1;
            else
            {
                prefixIdx = mid;
                break;
            }
            prefixIdx = lo;
        }

        long localChunkIdx = globalChunkIdx - cumulativeChunks[prefixIdx];

        // Read prefix state (PrefixStateSize128 = 14 layout)
        int stateBase = prefixIdx * PrefixStateSize128;
        ulong pOneLo = (ulong)allPrefixStates[stateBase + 0];
        ulong pOneHi = (ulong)allPrefixStates[stateBase + 1];
        ulong pTwoLo = (ulong)allPrefixStates[stateBase + 2];
        ulong pTwoHi = (ulong)allPrefixStates[stateBase + 3];
        int pVars = (int)allPrefixStates[stateBase + 4];
        ulong pPos = (ulong)allPrefixStates[stateBase + 5];
        ulong pNeg = (ulong)allPrefixStates[stateBase + 6];
        byte pGrp = (byte)allPrefixStates[stateBase + 7];
        ulong packedPrefixIndices = (ulong)allPrefixStates[stateBase + 8];
        int suffixStart = (int)allPrefixStates[stateBase + 9];
        int suffixN = (int)allPrefixStates[stateBase + 10];
        int suffixC = (int)allPrefixStates[stateBase + 11];
        int variableCount = (int)((ulong)allPrefixStates[stateBase + 12] & 0xFFFF);
        int prefixDepthK = (int)(((ulong)allPrefixStates[stateBase + 12] >> 16) & 0xFFFF);

        int allVariablesMask = (1 << variableCount) - 1;
        byte allGroupsMask = 0xFF;
        int combTableWidth = prefixDepthK + suffixC + 1;

        var localIndices = LocalMemory.Allocate<int>(MaxClausesSupported);
        long combinationStart = localChunkIdx * ChunkSize;
        UnrankCombination(combinationStart, suffixN, suffixC, combCounts, localIndices, combTableWidth);

        int validCount = 0;

        for (int i = 0; i < ChunkSize; i++)
        {
            ulong oneLo = pOneLo, oneHi = pOneHi;
            ulong twoLo = pTwoLo, twoHi = pTwoHi;
            int varCoverage = pVars;
            ulong posPacked = pPos, negPacked = pNeg;
            byte groupCoverage = pGrp;

            for (int k = 0; k < suffixC; k++)
            {
                int localIdx = localIndices[k];
                int globalIdx = suffixStart + localIdx;
                int mIdx = 2 * globalIdx;
                ulong mLo = clauseMasks[mIdx];
                ulong mHi = clauseMasks[mIdx + 1];
                twoLo |= oneLo & mLo;
                twoHi |= oneHi & mHi;
                oneLo |= mLo;
                oneHi |= mHi;
                varCoverage |= clauseVarMasks[globalIdx];
                posPacked += clausePosPacked[globalIdx];
                negPacked += clauseNegPacked[globalIdx];
                groupCoverage |= clauseGroupMask[globalIdx];
            }

            // UNSAT check: all 128 assignments covered
            if (groupCoverage == allGroupsMask &&
                oneLo == ulong.MaxValue && oneHi == ulong.MaxValue &&
                varCoverage == allVariablesMask)
            {
                ulong uniqueLo = oneLo & ~twoLo;
                ulong uniqueHi = oneHi & ~twoHi;
                bool minimal = true;

                // Check prefix clauses
                for (int pi = 0; pi < prefixDepthK; pi++)
                {
                    int pci = (int)((packedPrefixIndices >> (pi * 16)) & 0xFFFF);
                    int pmIdx = 2 * pci;
                    if ((clauseMasks[pmIdx] & uniqueLo) == 0 && (clauseMasks[pmIdx + 1] & uniqueHi) == 0)
                    {
                        minimal = false;
                        break;
                    }
                }

                // Check suffix clauses
                if (minimal)
                {
                    for (int k = 0; k < suffixC; k++)
                    {
                        int globalIdx = suffixStart + localIndices[k];
                        int smIdx = 2 * globalIdx;
                        if ((clauseMasks[smIdx] & uniqueLo) == 0 && (clauseMasks[smIdx + 1] & uniqueHi) == 0)
                        {
                            minimal = false;
                            break;
                        }
                    }
                }

                if (minimal)
                {
                    bool canonical = true;
                    int stabilizer = 0;
                    for (int v = 0; v < variableCount; v++)
                    {
                        int shift = v * 5;
                        ulong p = (posPacked >> shift) & 0x1F;
                        ulong n = (negPacked >> shift) & 0x1F;
                        if (p < n) { canonical = false; break; }
                        if (p == n) stabilizer++;
                    }

                    if (canonical)
                    {
                        validCount += (1 << variableCount) >> stabilizer;
                    }
                }
            }

            if (!NextCombination(localIndices, suffixC, suffixN))
                break;
        }

        // Block reduction
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
        int i = k - 1;
        while (i >= 0 && indices[i] == n - k + i)
            i--;

        if (i < 0) return false;

        indices[i]++;
        for (int j = i + 1; j < k; j++)
            indices[j] = indices[j - 1] + 1;

        return true;
    }

    private static void UnrankCombination(long index, int n, int k, ArrayView<long> combCounts,
        ArrayView<int> result, int combTableWidth)
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
                    count = combCounts[n_remaining * combTableWidth + k_remaining];

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
