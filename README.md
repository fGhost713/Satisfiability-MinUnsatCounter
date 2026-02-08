# MIN-UNSAT k-SAT Counter

A GPU-accelerated tool for counting **minimally unsatisfiable k-SAT formulas** (2-SAT and 3-SAT).

Finding no existing MinUnsat counter available, I developed this tool to compute counts for small variable and clause sizes. After extensive performance optimizations, the tool became fast enough to gather sufficient 2-SAT data points, which allowed me to analyze the underlying patterns and derive a closed-form formula for the 2-SAT MinUnsat count.
## What This Counts

This tool counts k-CNF formulas $\phi$ that satisfy ALL of the following:

1. **Exactly k literals per clause** ($l=2$ for 2-SAT, $l=3$ for 3-SAT)
2. **Every variable appears at least once** (all-variables constraint)
3. **Unsatisfiable** (no truth assignment satisfies all clauses)
4. **Minimal** (removing any single clause makes the formula satisfiable)

We denote this count as $f_{\text{all}}(v, l, c)$ where:
- $v$ = number of Boolean variables
- $l$ = literals per clause (2 or 3)
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

Enumerates all possible clause combinations and counts MIN-UNSAT formulas:

```bash
# 2-SAT (default): 4 variables, 5 clauses
minunsat minunsat -v 4 -c 5

# 3-SAT: 5 variables, 12 clauses
minunsat minunsat -v 5 -l 3 -c 12

# Force CPU mode (no GPU)
minunsat minunsat -v 4 -c 5 --cpu

# Enable checkpointing for long-running calculations
minunsat minunsat -v 5 -l 3 -c 12 --checkpoint
```

**Options:**
- `-v, --variables` (required): Number of variables (2-10)
- `-l, --literals` (optional): Literals per clause, default=2 (2-SAT), use 3 for 3-SAT
- `-c, --clauses` (required): Number of clauses
- `--cpu`: Force CPU mode 
- `--checkpoint`: Enable checkpoint save/resume for long-running calculations

**Performance notes:** 
- v ≤ 6: Uses optimized GPU/CPU kernels (fastest, ~14 billion/s on GPU)
- v > 6: Uses fallback implementation (up to v = 10)
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

# Large-scale computation (computed in ~1-2 seconds!)
minunsat formula -v 100 -c 150
```

**Example output for large values:**
```
=== MIN-UNSAT k-SAT Counter ===

Mode: Closed-Form Formula (2-SAT only)
Variables (v): 100
Clauses (c): 150

RESULT: f_all(v=100, k=2, c=150) = 302.655.795.792.297.559.643.937.397.980.066.992.082.631.605.482.382.793.871.890.146.731.442.861.406.331.144.939.067.438.757.965.146.142.869.103.108.087.043.692.097.953.728.174.204.248.463.659.897.632.703.795.116.458.254.003.993.440.588.689.891.360.137.143.007.789.134.643.200.000.000.000.000.000.000.000
```

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

### 2-SAT ($l=2$)

| $v$ | $c$ | $f_{\text{all}}(v, 2, c)$ |
|:---:|:---:|-------------------------:|
| 2   | 4   | 1 |
| 3   | 4   | 6 |
| 3   | 5   | 36 |
| 4   | 5   | 144 |
| 4   | 6   | 1,008 |
| 5   | 6   | 2,880 |
| 5   | 7   | 26,880 |
| 6   | 7   | 57,600 |
| 6   | 8   | 725,760 |

### 3-SAT ($l=3$)

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

## The Closed-Form Formula (2-SAT) for immediately computing the amount of Minimal Unsatisfiable 2-SAT formulas for fixed $v$ variables and $c$ clauses.

For diagonal $d = c - v$:

**Diagonal $d = 1$** (where $c = v + 1$):

$$f_{\text{all}}(v, v+1) = v! \cdot (v-1) \cdot (v-2) \cdot 2^{v-4}$$

**Diagonal $d \geq 2$**:

$$f_{\text{all}}(v, c) = \sum_{u \in \{0, 2, 4, ...\}} 2^u \cdot N(c, v, u)$$

where:

$$N(c, k, u) = A(d, u) \cdot k! \cdot \binom{c-1}{2d-1+u/2} \cdot 2^{c - B(d,u)}$$

The coefficients $A(d, u)$ and $B(d, u)$ follow specific patterns based on whether $d$ is a power of 2.

See `MATHEMATICAL_PROOF_2SAT_MINUNSAT_DETAILED.md` in the `documents` folder for the complete proof.

## How It Works

### GPU Brute-Force Algorithm

1. **Generate** all $\binom{\text{totalClauses}}{c}$ combinations of clauses
2. **Check** each combination for:
   - All variables used
   - UNSAT (covers all $2^v$ assignments)
   - Minimal (each clause has unique coverage)
   - Canonical form (for orbit counting)
3. **Sum** orbit sizes using polarity symmetry

### Polarity Symmetry Optimization

The algorithm exploits the polarity symmetry group $(\mathbb{Z}_2)^v$:
- Only counts "canonical" formulas ($p_i^+ \geq p_i^-$ for each variable)
- Multiplies by orbit size $2^u$ where $u$ = number of unbalanced variables
- Reduces search space by up to $2^v$ factor

## Performance

Typical performance on RTX 4060:

| Counter | Rate | Description |
|---------|-----:|-------------|
| **UNSAT** | ~31 billion/s | Counting all unsatisfiable formulas |
| **MIN-UNSAT** | ~14 billion/s | Counting minimal unsatisfiable formulas |

Example runtimes:

| Task | Parameters | Combinations | Time |
|------|------------|-------------:|-----:|
| MIN-UNSAT | $v=5$, $l=3$, $c=12$ | 60 trillion | ~1h 12min |
| MIN-UNSAT | $v=6$, $l=2$, $c=10$ | 75 billion | ~5 seconds |
| UNSAT | $v=5$, $l=3$, $c=12$ | 60 trillion | ~32 minutes |

## License

MIT License

## References

1. Kleine Büning, H., & Kullmann, O. (2009). *Minimal Unsatisfiability and Autarkies*. Handbook of Satisfiability.
2. OEIS A082138: Number of labeled 2-regular simple digraphs on n nodes.




