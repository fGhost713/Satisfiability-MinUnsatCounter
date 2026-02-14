using System.Diagnostics;
using CommandLine;
using MinUnsatPublish.Counters;
using MinUnsatPublish.Helpers;
using MinUnsatPublish.Infrastructure;

Console.WriteLine("=== MIN-UNSAT k-SAT Counter ===\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("\n[!] Cancellation requested..."); cts.Cancel(); };

// Known GPU-validated values for formula verification
var KnownValues = new (int v, int c, long expected)[]
{
    (3,4,6), (3,5,36), (3,6,4),
    (4,5,144), (4,6,1008), (4,7,288), (4,8,24),
    (5,6,2880), (5,7,26880), (5,8,14400), (5,9,2880), (5,10,192),
    (6,7,57600), (6,8,725760), (6,9,633600), (6,10,224640), (6,11,34560), (6,12,1920),
};

Parser.Default.ParseArguments<MinUnsatOptions, FormulaOptions, UnsatOptions>(args)
    .WithParsed<MinUnsatOptions>(opts => RunMinUnsat(opts, cts.Token))
    .WithParsed<FormulaOptions>(RunFormula)
    .WithParsed<UnsatOptions>(opts => RunUnsat(opts, cts.Token))
    .WithNotParsed(_ => { });

// ==================== MIN-UNSAT Counter ====================

void RunMinUnsat(MinUnsatOptions opts, CancellationToken ct)
{
    if (opts.Benchmark) { RunBenchmark(opts); return; }

    if (opts.Variables < opts.Literals || opts.Variables > 10)
    { Console.WriteLine($"Error: Variables must be between {opts.Literals} and 10"); return; }
    if (opts.Literals < 2 || opts.Literals > 3)
    { Console.WriteLine("Error: Literals per clause must be 2 or 3"); return; }
    int minClauses = opts.Literals == 2 ? opts.Variables + 1 : 8;
    if (opts.Clauses < minClauses)
    { Console.WriteLine($"Error: Need at least {minClauses} clauses for {opts.Literals}-SAT MIN-UNSAT with {opts.Variables} variables"); return; }

    // Auto-select engine:
    //   3-SAT + GPU + v<=7 -> V3 (CPU-prefix GPU-suffix hybrid)
    //   2-SAT + GPU + v<=6 -> V2 (optimized 64-bit kernel)
    //   GPU + v>6/v>7      -> ManyVars fallback
    //   --cpu              -> CPU optimized (v<=6) or CPU ManyVars (v>6)
    bool useV3 = opts.Literals == 3 && !opts.UseCpu && opts.Variables <= 7;

    string engine = opts.UseCpu ? "CPU" :
                    useV3 ? "GPU V3 Hybrid" :
                    opts.Variables <= 6 ? "GPU V2" : "GPU ManyVars";
    PrintHeader(engine, opts.Literals, opts.Variables, opts.Clauses);

    CountingResult countResult;

    if (useV3)
    {
        using var v3 = new GpuMinUnsatCounterV3(preferGpu: true);
        countResult = v3.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct,
            verbose: true, useCheckpoint: opts.UseCheckpoint, prefixDepth: opts.PrefixDepth);
    }
    else if (opts.UseCpu)
    {
        countResult = opts.Variables <= 6
            ? new CpuMinUnsatCounterOptimized().CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint)
            : new CpuMinUnsatCounterManyVars().CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
    }
    else if (opts.Variables <= 6)
    {
        using var gpu = new GpuMinUnsatCounterOptimizedV2(preferGpu: true);
        countResult = gpu.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
    }
    else
    {
        using var gpu = new GpuMinUnsatCounterManyVars(preferGpu: true);
        countResult = gpu.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true, useCheckpoint: opts.UseCheckpoint);
    }

    PrintResult(countResult, $"f_all(v={opts.Variables}, l={opts.Literals}, c={opts.Clauses})");
}

// ==================== Closed-Form Formula (2-SAT only) ====================

void RunFormula(FormulaOptions opts)
{
    if (opts.Verify) { RunFormulaVerification(); return; }

    if (opts.Variables <= 0 || opts.Clauses <= 0)
    { Console.WriteLine("Usage: formula -v <variables> -c <clauses>  |  formula --verify"); return; }
    if (opts.Variables < 2)
    { Console.WriteLine("Error: Variables must be at least 2"); return; }
    if (opts.Clauses < opts.Variables + 1)
    { Console.WriteLine($"Error: Need at least {opts.Variables + 1} clauses for 2-SAT MIN-UNSAT"); return; }

    Console.WriteLine($"Mode: Closed-Form Formula (2-SAT only)");
    Console.WriteLine($"Variables (v): {opts.Variables}");
    Console.WriteLine($"Clauses (c): {opts.Clauses}\n");

    var count = MinUnsatClosedFormulaAllVars.Compute(opts.Variables, opts.Clauses);

    Console.WriteLine($"RESULT: f_all(v={opts.Variables}, k=2, c={opts.Clauses}) = {count:N0}");

    if (opts.ShowDetails)
    {
        int d = opts.Clauses - opts.Variables;
        Console.WriteLine($"\nDetails: diagonal d={d}, formula type: Diagonal {d}");
        string s = count.ToString()!;
        if (s.Length > 15) Console.WriteLine($"  ({s.Length} digits)");
    }
}

void RunFormulaVerification()
{
    Console.WriteLine("=== Verifying 2-SAT Closed-Form Formulas ===\n");

    Console.WriteLine("| v | c | Expected |  Formula | Match |");
    Console.WriteLine("|--:|--:|---------:|---------:|:-----:|");
    int passed = 0, failed = 0;
    foreach (var (v, c, expected) in KnownValues)
    {
        var computed = MinUnsatClosedFormulaAllVars.Compute(v, c);
        bool ok = computed == expected;
        Console.WriteLine($"| {v} | {c} | {expected,8:N0} | {computed,8:N0} | {(ok ? "PASS" : "FAIL")} |");
        if (ok) passed++; else failed++;
    }
    Console.WriteLine($"\nResults: {passed} passed, {failed} failed");
}

// ==================== GPU vs CPU Benchmark ====================

void RunBenchmark(MinUnsatOptions opts)
{
    Console.WriteLine($"=== Benchmark: GPU V2 vs CPU Optimized ===\n");
    Console.WriteLine($"Configuration: v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}\n");

    var (_, totalClauses) = ClauseLiteralMapper.BuildClauseLiteralMap(opts.Variables, opts.Literals);
    long totalCombinations = CombinationGenerator.CountCombinations(totalClauses, opts.Clauses);
    Console.WriteLine($"Total combinations: {totalCombinations:N0}\n");

    Console.WriteLine("Warming up...");
    new CpuMinUnsatCounterOptimized().Count(opts.Variables, opts.Literals, Math.Min(opts.Clauses, 5), verbose: false);
    using (var warmup = new GpuMinUnsatCounterOptimizedV2(preferGpu: true))
        warmup.Count(opts.Variables, opts.Literals, Math.Min(opts.Clauses, 5), verbose: false);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

    var (resultGpu, rateGpu) = BenchRun("GPU V2", () =>
    {
        using var gpu = new GpuMinUnsatCounterOptimizedV2(preferGpu: true);
        return gpu.Count(opts.Variables, opts.Literals, opts.Clauses, verbose: false);
    }, totalCombinations);

    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

    var (resultCpu, rateCpu) = BenchRun("CPU Optimized", () =>
        new CpuMinUnsatCounterOptimized().Count(opts.Variables, opts.Literals, opts.Clauses, verbose: false),
        totalCombinations);

    Console.WriteLine($"\n=== Summary ===");
    Console.WriteLine($"GPU V2:        {rateGpu:N0}/s");
    Console.WriteLine($"CPU Optimized: {rateCpu:N0}/s");
    Console.WriteLine($"Speedup:       {rateGpu / rateCpu:F2}x");
    Console.WriteLine(resultGpu == resultCpu
        ? $"\nResults match: {resultGpu:N0}"
        : $"\n*** WARNING: Results differ! GPU={resultGpu}, CPU={resultCpu} ***");
}

static (long result, double rate) BenchRun(string name, Func<long> action, long totalCombinations)
{
    Console.WriteLine($"\n--- {name} ---");
    var sw = Stopwatch.StartNew();
    long result = action();
    sw.Stop();
    double rate = totalCombinations / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"Result: {result:N0}  Time: {sw.Elapsed.TotalSeconds:F2}s  Rate: {rate:N0}/s");
    return (result, rate);
}

// ==================== UNSAT Counter ====================

void RunUnsat(UnsatOptions opts, CancellationToken ct)
{
    if (opts.Verify)
    {
        Console.WriteLine($"=== UNSAT Verification Mode ===");
        Console.WriteLine($"v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}\n");
        Console.WriteLine("Running simple verifier (guaranteed correct)...");
        Console.WriteLine($"\nVerified UNSAT count: {UnsatVerifier.CountSimple(opts.Variables, opts.Literals, opts.Clauses):N0}");
        return;
    }

    if (opts.Variables < opts.Literals || opts.Variables > 10)
    { Console.WriteLine($"Error: Variables must be between {opts.Literals} and 10"); return; }
    if (opts.Literals < 2 || opts.Literals > 3)
    { Console.WriteLine("Error: Literals per clause must be 2 or 3"); return; }
    if (opts.Clauses < 1)
    { Console.WriteLine("Error: Need at least 1 clause"); return; }

    string mode = opts.UseCpu ? "CPU" : (opts.Variables > 6 ? "CPU (v>6)" : "GPU");
    Console.WriteLine($"Mode: {mode} UNSAT Counter ({opts.Literals}-SAT)");
    Console.WriteLine($"Variables (v): {opts.Variables}");
    Console.WriteLine($"Literals per clause (l): {opts.Literals}");
    Console.WriteLine($"Clauses (c): {opts.Clauses}");
    if (!string.IsNullOrEmpty(opts.OutputFile)) Console.WriteLine($"Output file: {opts.OutputFile}");
    Console.WriteLine();

    CountingResult countResult;
    if (opts.UseCpu || opts.Variables > 6)
    {
        var cpu = new CpuUnsatCounterOptimized();
        countResult = cpu.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true);
    }
    else
    {
        using var gpu = new GpuUnsatCounterOptimized(preferGpu: true);
        countResult = gpu.CountCancellable(opts.Variables, opts.Literals, opts.Clauses, ct, verbose: true);
    }

    if (countResult.WasCancelled)
    {
        PrintResult(countResult, null);
    }
    else
    {
        Console.WriteLine($"\nRESULT: UNSAT(v={opts.Variables}, l={opts.Literals}, c={opts.Clauses}) = {countResult.Count:N0}");
        if (!string.IsNullOrEmpty(opts.OutputFile))
            WriteUnsatResultToFile(opts, countResult, mode);
    }
}

void WriteUnsatResultToFile(UnsatOptions opts, CountingResult countResult, string mode)
{
    try
    {
        bool isNew = !File.Exists(opts.OutputFile);
        using var w = new StreamWriter(opts.OutputFile!, append: true, encoding: System.Text.Encoding.UTF8);
        if (isNew)
        {
            w.WriteLine("# UNSAT Counting Results");
            w.WriteLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine("v,l,c,UNSAT,Combinations,TimeMs,Mode");
        }
        w.WriteLine($"{opts.Variables},{opts.Literals},{opts.Clauses},{countResult.Count},{countResult.TotalCombinations},{countResult.ElapsedMs},{mode}");
        Console.WriteLine($"[Saved] Results appended to {opts.OutputFile}");
    }
    catch (Exception ex) { Console.WriteLine($"[Warning] Failed to write to file: {ex.Message}"); }
}

// ==================== Shared Helpers ====================

static void PrintHeader(string engine, int literals, int variables, int clauses)
{
    Console.WriteLine($"Mode: {engine} ({literals}-SAT)");
    Console.WriteLine($"Variables (v): {variables}");
    Console.WriteLine($"Literals per clause (l): {literals}");
    Console.WriteLine($"Clauses (c): {clauses}\n");
}

static void PrintResult(CountingResult r, string? label)
{
    Console.WriteLine();
    if (r.WasCancelled)
    {
        Console.WriteLine($"[Cancelled] Processed: {r.ProcessedCombinations:N0} / {r.TotalCombinations:N0}");
        Console.WriteLine($"[Partial] MIN-UNSAT count so far: {r.Count:N0}");
    }
    else
    {
        Console.WriteLine($"RESULT: {label} = {r.Count:N0}");
    }
}

// ==================== Command Options ====================

[Verb("minunsat", HelpText = "Count MIN-UNSAT formulas using brute-force enumeration (GPU or CPU).")]
class MinUnsatOptions
{
    [Option('v', "variables", Required = true, HelpText = "Number of variables (all must appear).")]
    public int Variables { get; set; }
    [Option('l', "literals", Required = false, Default = 2, HelpText = "Literals per clause: 2 or 3. Default: 2")]
    public int Literals { get; set; }
    [Option('c', "clauses", Required = true, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }
    [Option("cpu", Default = false, HelpText = "Force CPU mode.")]
    public bool UseCpu { get; set; }
    [Option("checkpoint", Default = false, HelpText = "Enable checkpoint save/resume.")]
    public bool UseCheckpoint { get; set; }
    [Option("benchmark", Default = false, HelpText = "Run GPU vs CPU benchmark.")]
    public bool Benchmark { get; set; }
    [Option("v3", Default = false, Hidden = true, HelpText = "(Legacy) V3 is now default for 3-SAT.")]
    public bool UseV3 { get; set; }
    [Option('p', "prefix-depth", Default = 0, HelpText = "V3 prefix depth (2 or 3). 0 = auto. 3-SAT only.")]
    public int PrefixDepth { get; set; }
}

[Verb("formula", HelpText = "Compute MIN-UNSAT count using closed-form formula (2-SAT only, instant).")]
class FormulaOptions
{
    [Option('v', "variables", Default = 0, HelpText = "Number of variables.")]
    public int Variables { get; set; }
    [Option('c', "clauses", Default = 0, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }
    [Option('d', "details", Default = false, HelpText = "Show formula details.")]
    public bool ShowDetails { get; set; }
    [Option("verify", Default = false, HelpText = "Verify formulas against known values.")]
    public bool Verify { get; set; }
}

[Verb("unsat", HelpText = "Count UNSAT formulas (not MIN-UNSAT).")]
class UnsatOptions
{
    [Option('v', "variables", Required = true, HelpText = "Number of variables.")]
    public int Variables { get; set; }
    [Option('l', "literals", Default = 2, HelpText = "Literals per clause: 2 or 3. Default: 2")]
    public int Literals { get; set; }
    [Option('c', "clauses", Required = true, HelpText = "Number of clauses.")]
    public int Clauses { get; set; }
    [Option("cpu", Default = false, HelpText = "Force CPU mode.")]
    public bool UseCpu { get; set; }
    [Option("verify", Default = false, HelpText = "Use simple verifier (slow but correct).")]
    public bool Verify { get; set; }
    [Option('o', "output", Default = null, HelpText = "Output CSV file to append results.")]
    public string? OutputFile { get; set; }
}
