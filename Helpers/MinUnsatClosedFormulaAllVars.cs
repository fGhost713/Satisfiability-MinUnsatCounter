namespace MinUnsatPublish.Helpers;

using System.Numerics;

/// <summary>
/// Closed-form formula calculator for MIN-UNSAT 2-SAT counting with AllVars=true constraint.
/// 
/// Key Insight:
/// For AllVars=true, the MIN-UNSAT count is simply m(c,v) where m(c,k) is the multiplier.
/// - m(c,k) counts canonical MIN-UNSAT patterns using exactly k variables
/// - AllVars=true requires exactly v variables to be used
/// - Therefore: f_allvars(v, c) = m(c, v)
/// </summary>
public static class MinUnsatClosedFormulaAllVars
{
    #region Public API

    /// <summary>
    /// Compute MIN-UNSAT count for AllVars=true mode using BigInteger for arbitrary precision.
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

        return d switch
        {
            1 => ComputeM_Diagonal1(c),
            2 => ComputeM_Diagonal2(c),
            3 => ComputeM_Diagonal3(c),
            _ => ComputeM_General(c, k)
        };
    }

    #endregion

    #region Multiplier Computation

    /// <summary>
    /// m(c,c-1) = (c-1)! × (c-2) × (c-3) × 2^(c-5)
    /// </summary>
    private static BigInteger ComputeM_Diagonal1(int c)
    {
        if (c < 4) return 0;

        BigInteger factorial = Factorial(c - 1);
        BigInteger product = (BigInteger)(c - 2) * (c - 3);
        int power = c - 5;

        BigInteger result = factorial * product;
        if (power >= 0)
            result <<= power;
        else
            result >>= (-power);
        return result;
    }

    /// <summary>
    /// m(c,c-2) = N(c,c-2,0) + 4·N(c,c-2,2) + 16·N(c,c-2,4)
    /// </summary>
    private static BigInteger ComputeM_Diagonal2(int c)
    {
        if (c < 4) return 0;

        BigInteger n0 = ComputeN_Diagonal2(c, 0);
        BigInteger n2 = ComputeN_Diagonal2(c, 2);
        BigInteger n4 = ComputeN_Diagonal2(c, 4);

        return n0 + 4 * n2 + 16 * n4;
    }

    private static BigInteger ComputeN_Diagonal2(int c, int u)
    {
        BigInteger factorial = Factorial(c - 2);
        return u switch
        {
            0 => Shift(factorial * Binomial(c - 1, 3), c - 5),
            2 => Shift(factorial * Binomial(c - 1, 4), c - 6),
            4 => Shift(factorial * Binomial(c - 1, 5), c - 9),
            _ => 0
        };
    }

    /// <summary>
    /// m(c,c-3) = N(c,c-3,0) + 4·N(c,c-3,2) + 16·N(c,c-3,4) + 64·N(c,c-3,6)
    /// </summary>
    private static BigInteger ComputeM_Diagonal3(int c)
    {
        if (c < 6) return 0;

        BigInteger n0 = ComputeN_Diagonal3(c, 0);
        BigInteger n2 = ComputeN_Diagonal3(c, 2);
        BigInteger n4 = ComputeN_Diagonal3(c, 4);
        BigInteger n6 = ComputeN_Diagonal3(c, 6);

        return n0 + 4 * n2 + 16 * n4 + 64 * n6;
    }

    private static BigInteger ComputeN_Diagonal3(int c, int u)
    {
        BigInteger factorial = Factorial(c - 3);
        return u switch
        {
            0 => Shift(factorial * Binomial(c - 1, 5), c - 5) / 3,
            2 => Shift(factorial * Binomial(c - 1, 6), c - 7),
            4 => Shift(factorial * Binomial(c - 1, 7), c - 9),
            6 => Shift(factorial * Binomial(c - 1, 8), c - 11) / 3,
            _ => 0
        };
    }

    /// <summary>
    /// General formula for d >= 4.
    /// Dynamically sums over all even u values until the binomial term becomes zero.
    /// </summary>
    private static BigInteger ComputeM_General(int c, int k)
    {
        int d = c - k;
        if (d < 2) return 0;

        BigInteger result = 0;
        BigInteger multiplier = 1;
        for (int u = 0; ; u += 2)
        {
            int binomK = 2 * d - 1 + u / 2;
            if (binomK > c - 1) break;

            BigInteger nu = ComputeN(c, d, u);
            if (nu > 0)
                result += multiplier * nu;

            multiplier *= 4;
        }

        return result;
    }

    #endregion

    #region N(c,k,u) Computation

    /// <summary>
    /// Compute N(c, c-d, u) using Burnside's lemma for the A coefficient.
    /// Theorem: Exactly d+1 nonzero terms exist (j = u/2 ranges from 0 to d).
    /// A(d,j) = C(d,j)/d for non-pow2 d (Burnside over Z_d).
    /// A(d,j) = [C(d,j)+(d-1)*C(d/2,j/2)]/d for pow2 d, even j (Burnside over (Z_2)^m).
    /// A(d,j) is symmetric: A(d,j) = A(d,d-j).
    /// </summary>
    private static BigInteger ComputeN(int c, int d, int u)
    {
        if (d < 1) return 0;

        int j = u / 2;
        if (j > d) return 0; // Theorem: exactly d+1 nonzero terms

        int k = c - d;
        BigInteger termFact = Factorial(k);
        int binomK = 2 * d - 1 + j;
        BigInteger termBinom = Binomial(c - 1, binomK);

        if (termFact == 0 || termBinom == 0) return 0;

        BigInteger numA;
        BigInteger denA;
        int B;

        bool isPowerOf2 = d > 0 && (d & (d - 1)) == 0;

        if (d == 1)
        {
            // Boundary case: d=1 uses polynomial A, only u=0 term
            if (u != 0) return 0;
            numA = c - 3;
            denA = 1;
            B = 4;
        }
        else
        {
            // Burnside A coefficient
            denA = d;
            if (isPowerOf2 && (j % 2 == 0))
                numA = Binomial(d, j) + (d - 1) * Binomial(d / 2, j / 2);
            else
                numA = Binomial(d, j);

            // B offset
            if (!isPowerOf2)
            {
                B = d + 2 * j + 2;
            }
            else if (j == 0)
            {
                B = (3 * d) / 2 + 2;
            }
            else if (j % 2 == 1)
            {
                B = d + 2 * j + 2;
            }
            else
            {
                B = d + 2 * j + 3;
            }
        }

        int power = c - B;
        BigInteger bigResult = termFact * termBinom * numA;

        if (power >= 0)
            bigResult <<= power;
        else
            bigResult >>= (-power);

        bigResult /= denA;

        return bigResult;
    }

    #endregion

    #region Math Helpers

    private static BigInteger Factorial(int n)
    {
        if (n < 0) return 0;
        if (n <= 1) return 1;

        BigInteger result = 1;
        for (int i = 2; i <= n; i++)
            result *= i;
        return result;
    }

    private static BigInteger Binomial(int n, int k)
    {
        if (k > n || k < 0) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;

        BigInteger result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }

    private static BigInteger Shift(BigInteger val, int shift)
    {
        if (shift >= 0) return val << shift;
        return val >> (-shift);
    }

    #endregion
}
