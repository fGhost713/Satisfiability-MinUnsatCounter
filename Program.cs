using CommandLine;
using MinUnsatPublish.Counters;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

Console.WriteLine("=== MIN-UNSAT k-SAT Counter ===\n");

// Setup cancellation
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[!] Cancellation requested...");
    cts.Cancel();
};

var result = Parser.Default.ParseArguments<MinUnsatOptions, FormulaOptions, UnsatOptions>(args);
result.WithParsed<MinUnsatOptions>(opts => RunMinUnsat(opts, cts.Token))
      .WithParsed<FormulaOptions>(opts => RunFormula(opts))   
      .WithParsed<UnsatOptions>(opts => RunUnsat(opts, cts.Token))
      .WithNotParsed(errors => { /* Help text displayed automatically */ });






// ==================== MIN-UNSAT Counter ====================
void RunMinUnsat(MinUnsatOptions opts, CancellationToken ct)
{
    // Handle benchmark mode
    if (opts.Benchmark)
    {
        RunBenchmark(opts);
        return;
    }

    string mode = opts.UseCpu ? "CPU" : "GPU";
    Console.WriteLine($"Mode: {mode} Brute-Force ({opts.Literals}-SAT)");
    Console.WriteLine($"Variables (v): {opts.Variables}");
    Console.WriteLine($"Literals per clause (l): {opts.Literals}");
    Console.WriteLine($"Clauses (c): {opts.Clauses}");
    Console.WriteLine();





    if (opts.Variables < opts.Literals || opts.Variables > 10)
    {
        Console.WriteLine($"Error: Variables must be between {opts.Literals} and 10");
        return;
    }

    if (opts.Literals < 2 || opts.Literals > 3)
    {
        Console.WriteLine("Error: Literals per clause must be 2 or 3");
        return;
    }


    int minClauses = opts.Literals == 2 ? opts.Variables + 1 : 8; // 2-SAT: v+1, 3-SAT: 8
    if (opts.Clauses < minClauses)
    {
        Console.WriteLine($"Error: Need at least {minClauses} clauses for {opts.Literals}-SAT MIN-UNSAT with {opts.Variables} variables");
        return;
    }

    CountingResult countResult;
    
    if (opts.UseCpu)
    {
        if (opts.Variables <= 6)
        {
            // Use optimized CPU implementation (fastest for v<=6)
            var cpuCounter = new CpuMinUnsatCounterOptimized();
            countResult = cpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
        }
        else
        {
            // Use CPU fallback for v > 6
            var cpuCounter = new CpuMinUnsatCounterManyVars();
            countResult = cpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
        }
    }
    else if (opts.Variables <= 6)
    {
        // Use optimized GPU kernel V2 with chunking (fastest for v<=6)
        using var gpuCounter = new GpuMinUnsatCounterOptimizedV2(preferGpu: true);
        countResult = gpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
    }
    else
    {
        // Use GPU fallback for v > 6 (many-vars kernel with array-based masks)
        using var gpuCounter = new GpuMinUnsatCounterManyVars(preferGpu: true);
        countResult = gpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
    }
    
    Console.WriteLine();
    if (countResult.WasCancelled)
    {
        Console.WriteLine($"[Cancelled] Processed: {countResult.ProcessedCombinations:N0} / {countResult.TotalCombinations:N0}");
        Console.WriteLine($"[Partial] MIN-UNSAT count so far: {countResult.Count:N0}");
    }
    else
    {
        Console.WriteLine($"RESULT: f_all(v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}) = {countResult.Count:N0}");
    }
}

// ==================== GPU vs CPU Benchmark ====================
void RunBenchmark(MinUnsatOptions opts)
{
    Console.WriteLine($"=== Benchmark: GPU V2 vs CPU Optimized ===\n");
    Console.WriteLine($"Configuration: v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}");
    Console.WriteLine();

    var (_, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(opts.Variables, opts.Literals);
    long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, opts.Clauses);
    Console.WriteLine($"Total combinations: {totalCombinations:N0}");
    Console.WriteLine();

    // Warmup
    Console.WriteLine("Warming up...");
    {
        var warmupCpu = new CpuMinUnsatCounterOptimized();
        warmupCpu.Count(opts.Variables, opts.Literals, Math.Min(opts.Clauses, 5), verbose: false);
        using var warmupGpu = new GpuMinUnsatCounterOptimizedV2(preferGpu: true);
        warmupGpu.Count(opts.Variables, opts.Literals, Math.Min(opts.Clauses, 5), verbose: false);
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    // Test 1: GPU V2
    Console.WriteLine("\n--- Test 1: GPU V2 (GpuMinUnsatCounterOptimizedV2) ---");
    long resultGpu;
    double rateGpu;
    using (var gpuCounter = new GpuMinUnsatCounterOptimizedV2(preferGpu: true))
    {
        var swGpu = System.Diagnostics.Stopwatch.StartNew();
        resultGpu = gpuCounter.Count(opts.Variables, opts.Literals, opts.Clauses, verbose: false);
        swGpu.Stop();
        rateGpu = totalCombinations / swGpu.Elapsed.TotalSeconds;
        Console.WriteLine($"Result: {resultGpu:N0}");
        Console.WriteLine($"Time: {swGpu.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Rate: {rateGpu:N0}/s");
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    // Test 2: CPU Optimized
    Console.WriteLine("\n--- Test 2: CPU Optimized (CpuMinUnsatCounterOptimized) ---");
    var cpuCounter = new CpuMinUnsatCounterOptimized();
    var swCpu = System.Diagnostics.Stopwatch.StartNew();
    var resultCpu = cpuCounter.Count(opts.Variables, opts.Literals, opts.Clauses, verbose: false);
    swCpu.Stop();
    double rateCpu = totalCombinations / swCpu.Elapsed.TotalSeconds;
    Console.WriteLine($"Result: {resultCpu:N0}");
    Console.WriteLine($"Time: {swCpu.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"Rate: {rateCpu:N0}/s");

    // Summary
    Console.WriteLine("\n=== Summary ===");
    Console.WriteLine($"GPU V2:        {rateGpu:N0}/s");
    Console.WriteLine($"CPU Optimized: {rateCpu:N0}/s");
    double speedup = rateGpu / rateCpu;
    Console.WriteLine($"Speedup:       {speedup:F2}x (GPU vs CPU)");
    
    if (resultGpu != resultCpu)
    {
        Console.WriteLine($"\n*** WARNING: Results differ! GPU={resultGpu}, CPU={resultCpu} ***");
    }
    else
    {
        Console.WriteLine($"\nResults match: {resultGpu:N0}");
    }
}

// ==================== Closed-Form Formula (2-SAT only) ====================
void RunFormula(FormulaOptions opts)
{
    if (opts.Verify)
    {
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("?  Verifying 2-SAT Closed-Form Formulas                        ?");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();
        
        // Known validated values from ValidatedResults.md
        var knownValues = new Dictionary<(int v, int c), long>
        {
            // v=3
            { (3, 4), 6 },
            { (3, 5), 36 },
            { (3, 6), 4 },
            // v=4  
            { (4, 5), 144 },
            { (4, 6), 1008 },
            { (4, 7), 288 },
            { (4, 8), 24 },
            // v=5
            { (5, 6), 2880 },
            { (5, 7), 26880 },
            { (5, 8), 14400 },
            { (5, 9), 2880 },
            { (5, 10), 192 },
            // v=6
            { (6, 7), 57600 },
            { (6, 8), 725760 },
            { (6, 9), 633600 },
            { (6, 10), 224640 },
            { (6, 11), 34560 },
            { (6, 12), 1920 },
        };
        
        Console.WriteLine("Verifying formula against known GPU-computed values:");
        Console.WriteLine();
        Console.WriteLine("| v | c | Expected | Formula | Match |");
        Console.WriteLine("|--:|--:|---------:|--------:|:-----:|");
        
        int passed = 0, failed = 0;
        foreach (var ((v, c), expected) in knownValues.OrderBy(x => x.Key.v).ThenBy(x => x.Key.c))
        {
            var computed = MinUnsatClosedFormulaAllVars.Compute(v, c);
            bool match = computed == expected;
            string matchStr = match ? "?" : "?";
            Console.WriteLine($"| {v} | {c} | {expected,8:N0} | {computed,7:N0} | {matchStr} |");
            if (match) passed++; else failed++;
        }
        
        Console.WriteLine();
        Console.WriteLine($"Results: {passed} passed, {failed} failed");
        return;
    }

    if (opts.Variables <= 0 || opts.Clauses <= 0)
    {
        Console.WriteLine("Usage: formula -v <variables> -c <clauses>");
        Console.WriteLine("       formula --verify");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v, --variables   Number of variables (2-SAT)");
        Console.WriteLine("  -c, --clauses     Number of clauses");
        Console.WriteLine("  -d, --details     Show formula details");
        Console.WriteLine("  --verify          Verify formulas against known values");
        return;
    }

    Console.WriteLine($"Mode: Closed-Form Formula (2-SAT only)");
    Console.WriteLine($"Variables (v): {opts.Variables}");
    Console.WriteLine($"Clauses (c): {opts.Clauses}");
    Console.WriteLine();

    if (opts.Variables < 2)
    {
        Console.WriteLine("Error: Variables must be at least 2");
        return;
    }

    if (opts.Clauses < opts.Variables + 1)
    {
        Console.WriteLine($"Error: Need at least {opts.Variables + 1} clauses for 2-SAT MIN-UNSAT");
        return;
    }

    // Use BigInteger version for large values, otherwise use long version
    object count;
    if (opts.Variables > 12 || opts.Clauses > 20)
    {
        count = MinUnsatClosedFormulaAllVars.ComputeBig(opts.Variables, opts.Clauses);
    }
    else
    {
        count = MinUnsatClosedFormulaAllVars.Compute(opts.Variables, opts.Clauses);
    }
    
    Console.WriteLine($"RESULT: f_all(v={opts.Variables}, k=2, c={opts.Clauses}) = {count:N0}");
    
    if (opts.ShowDetails)
    {
        int d = opts.Clauses - opts.Variables;
        Console.WriteLine();
        Console.WriteLine($"Details:");
        Console.WriteLine($"  Diagonal d = c - v = {d}");
        Console.WriteLine($"  Variables (v) = {opts.Variables}");
        Console.WriteLine($"  Clauses (c) = {opts.Clauses}");
        Console.WriteLine($"  Formula type: Diagonal {d}");
        
        // Show number of digits for large values
        string countStr = count.ToString();
        if (countStr.Length > 15)
        {
            Console.WriteLine($"  (Number has {countStr.Length} digits)");
        }
    }
}



// ==================== UNSAT Counter ====================
void RunUnsat(UnsatOptions opts, CancellationToken ct)
{
    // Verification mode
    if (opts.Verify)
    {
        Console.WriteLine($"=== UNSAT Verification Mode ===");
        Console.WriteLine($"v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}");
        Console.WriteLine();
        
        // Run simple verifier
        Console.WriteLine("Running simple verifier (guaranteed correct)...");
        long verifiedCount = UnsatVerifier.CountSimple(opts.Variables, opts.Literals, opts.Clauses);
        Console.WriteLine($"\nVerified UNSAT count: {verifiedCount:N0}");
        return;
    }
    
    string mode = opts.UseCpu ? "CPU" : (opts.Variables > 6 ? "CPU (v>6)" : "GPU");
    Console.WriteLine($"Mode: {mode} UNSAT Counter ({opts.Literals}-SAT)");
    Console.WriteLine($"Variables (v): {opts.Variables}");
    Console.WriteLine($"Literals per clause (l): {opts.Literals}");
    Console.WriteLine($"Clauses (c): {opts.Clauses}");
    if (!string.IsNullOrEmpty(opts.OutputFile))
        Console.WriteLine($"Output file: {opts.OutputFile}");
    Console.WriteLine();

    if (opts.Variables < opts.Literals || opts.Variables > 10)
    {
        Console.WriteLine($"Error: Variables must be between {opts.Literals} and 10");
        return;
    }

    if (opts.Literals < 2 || opts.Literals > 3)
    {
        Console.WriteLine("Error: Literals per clause must be 2 or 3");
        return;
    }

    if (opts.Clauses < 1)
    {
        Console.WriteLine("Error: Need at least 1 clause");
        return;
    }

    CountingResult countResult;

    // Use CPU for v > 6 (GPU kernel limited to 6 variables)
    if (opts.UseCpu || opts.Variables > 6)
    {
        var cpuCounter = new CpuUnsatCounterOptimized();
        countResult = cpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true);
    }
    else
    {
        using var gpuCounter = new GpuUnsatCounterOptimized(preferGpu: true);
        countResult = gpuCounter.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true);
    }

    Console.WriteLine();
    if (countResult.WasCancelled)
    {
        Console.WriteLine($"[Cancelled] Processed: {countResult.ProcessedCombinations:N0} / {countResult.TotalCombinations:N0}");
        Console.WriteLine($"[Partial] UNSAT count so far: {countResult.Count:N0}");
    }
    else
    {
        Console.WriteLine($"RESULT: UNSAT(v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}) = {countResult.Count:N0}");
        
        // Write results to file if specified
        if (!string.IsNullOrEmpty(opts.OutputFile))
        {
            WriteUnsatResultToFile(opts, countResult, mode);
        }
    }
}

void WriteUnsatResultToFile(UnsatOptions opts, CountingResult countResult, string mode)
{
    try
    {
        bool fileExists = File.Exists(opts.OutputFile);
        
        using var writer = new StreamWriter(opts.OutputFile!, append: true, encoding: System.Text.Encoding.UTF8);
        
        // Write header if new file
        if (!fileExists)
        {
            writer.WriteLine("# UNSAT Counting Results");
            writer.WriteLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("#");
            writer.WriteLine("# v = variables, l = literals per clause, c = clauses");
            writer.WriteLine("# UNSAT = count of unsatisfiable formulas");
            writer.WriteLine("# Combinations = total clause combinations enumerated");
            writer.WriteLine("#");
            writer.WriteLine("v,l,c,UNSAT,Combinations,TimeMs,Mode");
        }
        
        // Write data row
        writer.WriteLine($"{opts.Variables},{opts.Literals},{opts.Clauses},{countResult.Count},{countResult.TotalCombinations},{countResult.ElapsedMs},{mode}");
        
        Console.WriteLine($"[Saved] Results appended to {opts.OutputFile}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Warning] Failed to write to file: {ex.Message}");
    }
}

// ==================== Command Options ====================

[Verb("minunsat", HelpText = "Count MIN-UNSAT formulas using brute-force enumeration (GPU or CPU).")]
class MinUnsatOptions
{
    [Option('v', "variables", Required = true, HelpText = "Number of variables (all must appear in the formula).")]
    public int Variables { get; set; }

    [Option('l', "literals", Required = false, Default = 2, HelpText = "Literals per clause: 2 for 2-SAT, 3 for 3-SAT. Default: 2")]
    public int Literals { get; set; }

    [Option('c', "clauses", Required = true, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }

    [Option("cpu", Required = false, Default = false, HelpText = "Force CPU mode (uses optimized prefix-caching implementation).")]
    public bool UseCpu { get; set; }

    [Option("checkpoint", Required = false, Default = false, HelpText = "Enable checkpoint save/resume for long-running calculations.")]
    public bool UseCheckpoint { get; set; }

    [Option("benchmark", Required = false, Default = false, HelpText = "Run benchmark comparing GPU vs CPU performance.")]
    public bool Benchmark { get; set; }
}


[Verb("formula", HelpText = "Compute MIN-UNSAT count using closed-form formula (2-SAT only, instant).")]
class FormulaOptions
{
    [Option('v', "variables", Required = false, Default = 0, HelpText = "Number of variables (all must appear in the formula).")]
    public int Variables { get; set; }

    [Option('c', "clauses", Required = false, Default = 0, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }

    [Option('d', "details", Required = false, Default = false, HelpText = "Show formula details.")]
    public bool ShowDetails { get; set; }

    [Option("verify", Required = false, Default = false, HelpText = "Verify formulas against known validated values.")]
    public bool Verify { get; set; }
}


[Verb("unsat", HelpText = "Count UNSAT formulas (not MIN-UNSAT) for 2-SAT.")]
class UnsatOptions
{
    [Option('v', "variables", Required = true, HelpText = "Number of variables.")]
    public int Variables { get; set; }

    [Option('l', "literals", Required = false, Default = 2, HelpText = "Literals per clause: 2 for 2-SAT, 3 for 3-SAT. Default: 2")]
    public int Literals { get; set; }

    [Option('c', "clauses", Required = true, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }

    [Option("cpu", Required = false, Default = false, HelpText = "Force CPU mode instead of GPU.")]
    public bool UseCpu { get; set; }

    [Option("verify", Required = false, Default = false, HelpText = "Use simple verifier to get correct count (slow but guaranteed correct).")]
    public bool Verify { get; set; }

    [Option('o', "output", Required = false, Default = null, HelpText = "Output file to append results (CSV format).")]
    public string? OutputFile { get; set; }
}
