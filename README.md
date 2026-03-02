# MIN-UNSAT k-SAT Counter

A GPU-accelerated tool for counting **minimally unsatisfiable k-SAT formulas** (2-SAT and 3-SAT).

Finding no existing MinUnsat counter available, I developed this tool to compute counts for small variable and clause sizes. After extensive performance optimizations, the tool became fast enough to gather sufficient 2-SAT data points, which allowed me to analyze the underlying patterns and derive a closed-form formula — now **fully proven** for all deficiency values and verified across 30 GPU data points — for the 2-SAT MinUnsat count.

## Two Ways to Count

| Approach | Command | Supports | Speed |
|----------|---------|----------|-------|
| **GPU Brute-Force** | `minunsat minunsat` | 2-SAT & 3-SAT | Up to ~19 billion combinations/s (GPU) |
| **Closed-Form Formula** | `minunsat formula` | 2-SAT only | **Instant** — any $v$ and $c$ |

- **Brute-force mode** exhaustively enumerates all clause combinations on GPU (or CPU) and checks each for minimally unsatisfiable properties. Works for both 2-SAT and 3-SAT, but runtime grows combinatorially.
- **Formula mode** uses our [closed-form formula](documents/CLOSED_FORM_FORMULA_PROOF_FOR_2SAT_MINUNSAT_DETAILED.md) — fully proven for all deficiency values — to compute the **exact** MIN-UNSAT count for 2-SAT instantly, for arbitrarily large $v$ and $c$.

## What This Counts

This tool counts k-CNF formulas $\phi$ that satisfy ALL of the following:

1. **Exactly k literals per clause** ($k=2$ for 2-SAT, $k=3$ for 3-SAT)
2. **Every variable appears at least once** (all-variables constraint)
3. **Unsatisfiable** (no truth assignment satisfies all clauses)
4. **Minimal** (removing any single clause makes the formula satisfiable)

We denote this count as $f_{\text{all}}(v, k, c)$ where:
- $v$ = number of Boolean variables
- $k$ = literals per clause (2 or 3)
- $c$ = number of clauses

## Installation

### Prerequisites
- .NET 10.0 SDK or later
- (Optional) CUDA-capable GPU for acceleration

### Build
```bash
dotnet build -c Release
```

### Publish as Single Executable
```bash
# Windows
dotnet publish -c Release -r win-x64

# Linux
dotnet publish -c Release -r linux-x64

# macOS
dotnet publish -c Release -r osx-x64
```

## Usage

### GPU Brute-Force Mode (Default)

Enumerates all possible clause combinations and counts MIN-UNSAT formulas.
The best GPU engine is auto-selected based on k-SAT type and variable count:

- **3-SAT (v ≤ 7):** V3 CPU-prefix GPU-suffix hybrid (~19 billion/s)
- **2-SAT (v ≤ 6):** V2 optimized 64-bit kernel (~14 billion/s)
- **v > 7:** ManyVars fallback (up to v = 10)

```bash
# 2-SAT (default): 4 variables, 5 clauses
minunsat minunsat -v 4 -c 5

# 3-SAT: 5 variables, 12 clauses (auto-selects V3 hybrid engine)
minunsat minunsat -v 5 -l 3 -c 12

# 3-SAT: long computation with checkpoint save/resume
minunsat minunsat -v 6 -l 3 -c 9 --checkpoint

# Force CPU mode (no GPU)
minunsat minunsat -v 4 -c 5 --cpu
```

**Options:**
- `-v, --variables` (required): Number of variables (2-10)
- `-l, --literals` (optional): Literals per clause, default=2 (2-SAT), use 3 for 3-SAT
- `-c, --clauses` (required): Number of clauses
- `--cpu`: Force CPU mode 
- `-p, --prefix-depth`: V3 prefix depth (2 or 3). Default: auto (2 for c ≤ 12, 3 for c > 12)
- `--checkpoint`: Enable checkpoint save/resume for long-running calculations
- `--benchmark`: Run GPU vs CPU performance benchmark

**Performance notes:** 
- 3-SAT: V3 hybrid engine is auto-selected for v ≤ 7 (CPU-prefix pruning + GPU-suffix enumeration)
- 2-SAT: V2 kernel is auto-selected for v ≤ 6; ManyVars for v > 6
- Prefix depth is auto-tuned (2 for c ≤ 12, 3 for c > 12)
- For 2-SAT with any v, use **formula mode** for instant computation

> ⚠️ **Performance Warning:** For v > 6, performance drops significantly (~100x slower) because the fallback code is not yet highly optimized. The optimized kernels use 64-bit bitmasks which can only handle 2⁶ = 64 assignments. For v > 6, we use multi-word arrays which have more memory overhead. **For 2-SAT, always prefer the formula mode** which computes results instantly for any number of variables.

### Closed-Form Formula Mode (2-SAT only)

Uses the discovered closed-form formula for instant computation:

```bash
# Compute using formula (2-SAT only)
minunsat formula -v 5 -c 6

# With details
minunsat formula -v 5 -c 6 --details

# Verify formula against known GPU-computed values
minunsat formula --verify

# Instant computation for any number of variables and clauses
minunsat formula -v 20 -c 23
```

**Example output:**
```
=== MIN-UNSAT k-SAT Counter ===

Mode: Closed-Form Formula (2-SAT only)
Variables (v): 20
Clauses (c): 23

RESULT: f_all(v=20, k=2, c=23) = 229.932.268.649.941.076.803.584.000.000
```

This is an **exact** count — not an approximation. It represents the number of all minimally unsatisfiable 2-SAT formulas that have exactly 20 variables and exactly 23 clauses, where each clause contains exactly 2 literals. Only unique formulas are counted: since a formula is defined as an unordered *set* of clauses, the ordering of clauses does not matter.

The implementation is in [`FormulaCode/MinUnsatClosedFormulaAllVars.cs`](FormulaCode/MinUnsatClosedFormulaAllVars.cs).

**Options:**
- `-v, --variables` (required): Number of variables
- `-c, --clauses` (required): Number of clauses
- `-d, --details`: Show formula details (diagonal, digit count)
- `--verify`: Verify formula against known GPU-computed values

**Note:** Closed-form formula is only available for 2-SAT. Use GPU mode for 3-SAT.

### UNSAT Counter Mode

Counts **all unsatisfiable formulas** (not just minimal ones):

```bash
# Count UNSAT 2-SAT formulas
minunsat unsat -v 4 -c 6

# Count UNSAT 3-SAT formulas
minunsat unsat -v 4 -l 3 -c 10

# Save results to CSV file
minunsat unsat -v 4 -c 6 -o results.csv

# Use simple verifier (slow but guaranteed correct)
minunsat unsat -v 3 -c 4 --verify
```

**Options:**
- `-v, --variables` (required): Number of variables
- `-l, --literals` (optional): Literals per clause, default=2
- `-c, --clauses` (required): Number of clauses
- `--cpu`: Force CPU mode
- `-o, --output`: Output file to append results (CSV format)
- `--verify`: Use simple verifier for correctness checking

## Examples

```bash
# 2-SAT: Small case
$ minunsat minunsat -v 3 -c 4
RESULT: f_all(v=3, l=2, c=4) = 6

# 2-SAT: Medium case
$ minunsat minunsat -v 5 -c 7
RESULT: f_all(v=5, l=2, c=7) = 26,880

# 3-SAT: 4 variables, 10 clauses
$ minunsat minunsat -v 4 -l 3 -c 10
RESULT: f_all(v=4, l=3, c=10) = 29,792

# 3-SAT: 5 variables, 12 clauses (long computation)
$ minunsat minunsat -v 5 -l 3 -c 12
RESULT: f_all(v=5, l=3, c=12) = 1,142,018,600
```

## Verified Results

### 2-SAT ($k=2$)

| $v$ | $c$ | $f_{\text{all}}(v, 2, c)$ |
|:---:|:---:|-------------------------:|
| 2   | 4   | 1 |
| 3   | 4   | 6 |
| 3   | 5   | 36 |
| 3   | 6   | 4 |
| 4   | 5   | 144 |
| 4   | 6   | 1,008 |
| 4   | 7   | 288 |
| 4   | 8   | 24 |
| 5   | 6   | 2,880 |
| 5   | 7   | 26,880 |
| 5   | 8   | 14,400 |
| 5   | 9   | 2,880 |
| 5   | 10  | 192 |
| 6   | 7   | 57,600 |
| 6   | 8   | 725,760 |
| 6   | 9   | 633,600 |
| 6   | 10  | 224,640 |
| 6   | 11  | 34,560 |
| 6   | 12  | 1,920 |
| 7   | 8   | 1,209,600 |
| 7   | 9   | 20,321,280 |
| 7   | 10  | 26,611,200 |
| 7   | 11  | 14,676,480 |

### 3-SAT ($k=3$)

| $v$ | $c$ | $f_{\text{all}}(v, 3, c)$ |
|:---:|:---:|-------------------------:|
| 3   | 8   | 1 |
| 4   | 8   | 268 |
| 4   | 9   | 9,408 |
| 4   | 10  | 29,792 |
| 4   | 11  | 10,656 |
| 4   | 12  | 400 |
| 5   | 8   | 3,030 |
| 5   | 9   | 324,000 |
| 5   | 10  | 18,080,760 |
| 5   | 11  | 258,380,800 |
| 5   | 12  | 1,142,018,600 |
| 5   | 13  | 1,452,706,160 |
| 6   | 8   | 7,800 |
| 6   | 9   | 1,913,280 |
| 7   | 8   | 5,040 |

## Closed-Form Formula (2-SAT)

For diagonal $d = c - v$:

**Diagonal $d = 1$** (where $c = v + 1$):

$$f_{\text{all}}(v, v+1) = v! \cdot (v-1) \cdot (v-2) \cdot 2^{v-4}$$

**Diagonal $d \geq 2$**:

$$f_{\text{all}}(v, c) = \sum_{u \in \{0, 2, 4, ...\}} 2^u \cdot N(c, v, u)$$

where:

$$N(c, k, u) = A(d, u) \cdot k! \cdot \binom{c-1}{2d-1+u/2} \cdot 2^{c - B(d,u)}$$

The coefficients $A(d, j)$ follow Burnside's lemma over the cycle automorphism group, and $B(d, j)$ follows patterns based on whether $d$ is a power of 2. The formula works for **any** d and u — no hardcoded special cases.

The formula is **fully proven** for all deficiency values $d$ and **verified** by exhaustive GPU computation across 30 data points ($v = 2$ through $8$). The proof covers prime $d$ (necklace counting via Burnside over $\mathbb{Z}_d$), power-of-2 $d$ (binary group $(\mathbb{Z}_2)^m$), and composite non-power-of-2 $d$ (full Burnside over $\mathbb{Z}_d$).

See [`CLOSED_FORM_FORMULA_PROOF_FOR_2SAT_MINUNSAT_DETAILED.md`](documents/CLOSED_FORM_FORMULA_PROOF_FOR_2SAT_MINUNSAT_DETAILED.md) in the `documents` folder for the complete theorem statement, proofs, and verification data.

## How It Works

### Key Insight: Bitmask Precomputation

The core optimization is representing all $2^v$ truth assignments as bit positions in a 64-bit integer. For each clause, we precompute a **coverage mask** — a bitmask indicating which assignments falsify that clause. At runtime, checking if a formula is UNSAT reduces to a single bitwise OR over clause masks (O(c) instead of O(c × 2^v)). Minimality is checked by tracking single- and double-coverage with two additional bitmasks. See `documents/ALGORITHM_TECHNICAL_DETAILS.md` for the full algorithm specification.

### GPU Brute-Force Algorithm

1. **Precompute** coverage masks for all $\binom{v}{k} \cdot 2^k$ clause types
2. **Enumerate** all $\binom{\text{totalClauses}}{c}$ combinations of clauses (chunked across GPU threads)
3. **Check** each combination using bitwise operations:
   - All variables used (variable mask OR)
   - UNSAT (coverage OR == all-ones mask)
   - Minimal (each clause has unique coverage via one/two tracking)
   - Canonical form (for orbit counting)
4. **Sum** orbit sizes using polarity symmetry

### Polarity Symmetry Optimization

The algorithm exploits the polarity symmetry group $(\mathbb{Z}_2)^v$:
- Only counts "canonical" formulas ($p_i^+ \geq p_i^-$ for each variable)
- Multiplies by orbit size $2^u$ where $u$ = number of unbalanced variables
- Reduces search space by up to $2^v$ factor

## Performance

Typical performance on RTX 4060:

| Counter | Rate | Description |
|---------|-----:|-------------|
| **UNSAT** | ~26 billion/s | Counting all unsatisfiable formulas |
| **MIN-UNSAT** | ~17 billion/s | Counting minimal unsatisfiable formulas |

Example runtimes:

| Task | Parameters | Combinations | Time |
|------|------------|-------------:|-----:|
| MIN-UNSAT | $v=5$, $k=3$, $c=12$ | 60 trillion | ~1h 12min |
| MIN-UNSAT | $v=6$, $k=2$, $c=10$ | 75 billion | ~5 seconds |
| UNSAT | $v=5$, $k=3$, $c=12$ | 60 trillion | ~32 minutes |

## License

MIT License

## Project Structure

| Directory / File | Description |
|------------------|-------------|
| `Program.cs` | CLI entry point with `minunsat`, `formula`, and `unsat` subcommands |
| `Counters/` | GPU and CPU counter implementations (optimized + many-vars fallback) |
| `Helpers/` | Clause mask builder, combination generator, literal mapper |
| `Infrastructure/` | Checkpoint and result types |
| `FormulaCode/` | Closed-form formula implementation (`MinUnsatClosedFormulaAllVars.cs`) |
| `documents/` | Mathematical proof, derivations, and algorithm documentation |

## References

1. Kleine Büning, H., & Kullmann, O. (2009). *Minimal Unsatisfiability and Autarkies*. Handbook of Satisfiability.
2. OEIS [A082138](https://oeis.org/A082138): Number of labeled 2-regular simple digraphs on n nodes (related to 2-SAT MinUnsat diagonal counts).





