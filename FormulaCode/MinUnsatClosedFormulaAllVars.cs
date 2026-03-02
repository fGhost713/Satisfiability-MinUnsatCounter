namespace MinUnsatPublish.FormulaCode;

using System.Numerics;

/// <summary>
/// Computes the exact number of minimally unsatisfiable 2-SAT formulas (MIN-UNSAT)
/// for a given number of variables (v) and clauses (c).
///
/// <b>What this counts:</b>
/// A 2-SAT formula is a conjunction (AND) of clauses, where each clause contains exactly
/// 2 literals. A literal is either a variable (x) or its negation (¬x). The formula is
/// UNSAT (unsatisfiable) if no truth assignment to the variables can make all clauses true
/// simultaneously. It is MIN-UNSAT if it is UNSAT, but removing any single clause makes
/// it satisfiable — every clause is essential for the contradiction.
///
/// This class counts all distinct MIN-UNSAT formulas that use exactly v variables and
/// exactly c clauses. Two formulas are considered identical if they contain the same set
/// of clauses (clause ordering does not matter — only the unordered set of clauses defines
/// the formula). Each variable must appear in at least one clause (AllVars=true constraint).
///
/// <b>Examples:</b>
///   v=3, c=4 → 6 distinct MIN-UNSAT formulas exist
///   v=5, c=7 → 26,880 distinct MIN-UNSAT formulas exist
///   v=6, c=8 → 725,760 distinct MIN-UNSAT formulas exist
///
/// <b>How it works:</b>
/// Instead of enumerating all possible formulas (which grows astronomically), this class
/// uses a proven closed-form mathematical formula. The formula exploits the structural
/// properties of MIN-UNSAT formulas:
///   · Every MIN-UNSAT formula has a quotient graph with a specific topology (theta-graph
///     for the simplest case, ring or hypercube structure for larger formulas).
///   · The count decomposes into four factors: a symmetry coefficient (A), a variable
///     permutation count (k!), a binomial coefficient for structural placement, and a
///     power-of-2 factor for literal sign choices.
///   · These factors are summed over polarity classes (how many variables appear with
///     unequal positive/negative counts), weighted by 2^u where u is the number of
///     unbalanced variables.
///
/// The formula has been formally proven correct for all deficiency values (d = c - v)
/// and validated against exhaustive GPU computation for v up to 8.
///
/// <b>Technical formula (for reference):</b>
///   m(c, k) = Σ_{j=0..d} 4^j · A(d,j) · k! · C(c-1, 2d-1+j) · 2^(c - B(d,j))
/// where d = c - k (deficiency), A(d,j) is computed via Burnside's lemma over the
/// symmetry group of the quotient graph, and B(d,j) is a power-of-2 offset that depends
/// on whether d is a power of 2.
///
/// Special case d = 1 (theta-graph topology):
///   m(c, c-1) = (c-1)! · (c-2) · (c-3) · 2^(c-5)
/// </summary>
public static class MinUnsatClosedFormulaAllVars
{
    /// <summary>
    /// Compute MIN-UNSAT count for AllVars=true mode using BigInteger.
    /// f(v, c) = m(c, v) where m is the multiplier from the general formula.
    /// </summary>
    public static BigInteger Compute(int numVariables, int numClauses)
    {
        if (numClauses < 4) return 0;
        if (numVariables < 2) return 0;

        if (numVariables == 2 && numClauses != 4) return 0;
        if (numVariables > 2 && numClauses < numVariables + 1) return 0;

        int c = numClauses;
        int k = numVariables;
        int d = c - k;
        if (d < 1) return 0;

        // d=1 uses a special closed-form (theta-graph topology, not decomposable into N terms)
        if (d == 1) return ComputeM_D1(c);

        // d >= 2: general four-factor Burnside formula
        return ComputeM_General(c, k);
    }

    // ════════════════════════════════════════════════════════════════
    //  d = 1 special formula (theta-graph topology)
    //  m(c, c-1) = (c-1)! · (c-2) · (c-3) · 2^(c-5)
    // ════════════════════════════════════════════════════════════════

    private static BigInteger ComputeM_D1(int c)
    {
        if (c < 4) return 0;

        BigInteger result = FactorialBig(c - 1) * (c - 2) * (c - 3);

        int power = c - 5;
        return power >= 0 ? result << power : result >> -power;
    }

    // ════════════════════════════════════════════════════════════════
    //  d >= 2 general formula (ring/hypercube topology)
    //  m(c, k) = Σ_{j=0}^{d} 4^j · N(c, d, 2j)
    //  Terminates when C(c-1, 2d-1+j) = 0 (i.e., 2d-1+j > c-1)
    // ════════════════════════════════════════════════════════════════

    private static BigInteger ComputeM_General(int c, int k)
    {
        int d = c - k;

        BigInteger result = 0;
        BigInteger weight = 1; // 4^j: 1, 4, 16, 64, ...

        for (int j = 0; j <= d; j++)
        {
            int binomK = 2 * d - 1 + j;
            if (binomK > c - 1) break;

            BigInteger nj = ComputeN(c, d, j);
            if (nj > 0)
                result += weight * nj;

            weight *= 4;
        }

        return result;
    }

    /// <summary>
    /// Compute N(c, c-d, 2j) for d >= 2.
    /// N = A(d,j) · (c-d)! · C(c-1, 2d-1+j) · 2^(c - B(d,j))
    /// </summary>
    private static BigInteger ComputeN(int c, int d, int j)
    {
        if (j > d) return 0;

        BigInteger termFact = FactorialBig(c - d);
        BigInteger termBinom = BinomialBig(c - 1, 2 * d - 1 + j);

        if (termFact == 0 || termBinom == 0) return 0;

        // A coefficient via Burnside's lemma
        bool isPow2 = (d & (d - 1)) == 0;
        BigInteger numA;
        BigInteger denA;

        if (isPow2 && j % 2 == 0)
        {
            numA = BinomialBig(d, j) + (d - 1) * BinomialBig(d / 2, j / 2);
            denA = d;
        }
        else if (isPow2)
        {
            numA = BinomialBig(d, j);
            denA = d;
        }
        else
        {
            // Non-power-of-2: full Burnside over Z_d (handles prime and composite)
            (long num, long den) = BurnsideCyclicGroup(d, j);
            numA = num;
            denA = den;
        }

        // B offset
        int B;
        if (!isPow2)
            B = d + 2 * j + 2;
        else if (j == 0)
            B = 3 * d / 2 + 2;
        else if (j % 2 == 1)
            B = d + 2 * j + 2;
        else
            B = d + 2 * j + 3;

        int power = c - B;
        BigInteger result = termFact * termBinom * numA;

        result = power >= 0 ? result << power : result >> -power;
        result /= denA;

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    //  Burnside A coefficient for non-pow2 d
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes A(d,j) via Burnside's lemma over cyclic group Z_d.
    /// Handles both prime and composite d.
    /// Returns (numerator, denominator) pair.
    /// </summary>
    private static (long numerator, long denominator) BurnsideCyclicGroup(int d, int j)
    {
        // Boundary: A(d,0) = A(d,d) = 1/d for all non-pow2 d
        if (j == 0 || j == d)
            return (1, d);

        // Sum fixed-point counts over all rotations in Z_d
        long fixedSum = BinomialLong(d, j); // identity rotation

        for (int r = 1; r < d; r++)
        {
            int g = Gcd(r, d);
            int period = d / g;
            int cycles = g;

            if (j % period != 0)
                continue;

            int perCycle = j / period;
            if (perCycle > cycles)
                continue;

            fixedSum += BinomialLong(cycles, perCycle);
        }

        return (fixedSum, d);
    }

    // ════════════════════════════════════════════════════════════════
    //  Math helpers
    // ════════════════════════════════════════════════════════════════

    private static BigInteger FactorialBig(int n)
    {
        if (n <= 1) return 1;

        BigInteger result = 1;
        for (int i = 2; i <= n; i++)
            result *= i;
        return result;
    }

    private static BigInteger BinomialBig(int n, int k)
    {
        if (k > n || k < 0) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;

        BigInteger result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }

    /// <summary>
    /// Long binomial — used only for Burnside rotation counts where d is small.
    /// </summary>
    private static long BinomialLong(int n, int k)
    {
        if (k > n || k < 0) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;

        long result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
