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
    /// Compute MIN-UNSAT count for AllVars=true mode.
    /// f(v, c) = m(c, v) where m is the multiplier from the general formula.
    /// </summary>
    public static long Compute(int numVariables, int numClauses)
    {
        if (numClauses < 4) return 0;
        if (numVariables < 2) return 0;
        
        // For AllVars=true, we need exactly v variables
        if (numVariables == 2 && numClauses != 4) return 0;
        if (numVariables > 2 && numClauses < numVariables + 1) return 0;
        
        return ComputeMultiplier(numClauses, numVariables);
    }

    /// <summary>
    /// Compute MIN-UNSAT count using BigInteger for arbitrary precision.
    /// Supports large values of v and c that would overflow long.
    /// </summary>
    public static BigInteger ComputeBig(int numVariables, int numClauses)
    {
        if (numClauses < 4) return 0;
        if (numVariables < 2) return 0;
        
        if (numVariables == 2 && numClauses != 4) return 0;
        if (numVariables > 2 && numClauses < numVariables + 1) return 0;
        
        int d = numClauses - numVariables;
        if (d < 1) return 0;
        
        // Compute m(c, v) = ? 2^u × N(c, v, u) using BigInteger
        BigInteger n0 = ComputeN_Big(numClauses, d, 0);
        BigInteger n2 = ComputeN_Big(numClauses, d, 2);
        BigInteger n4 = ComputeN_Big(numClauses, d, 4);
        
        return n0 + 4 * n2 + 16 * n4;
    }

    #endregion

    #region Multiplier Computation (long version)

    /// <summary>
    /// Computes the multiplier m(c,k) using closed-form formulas.
    /// </summary>
    private static long ComputeMultiplier(int c, int k)
    {
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

    /// <summary>
    /// m(c,c-1) = (c-1)! × (c-2) × (c-3) × 2^(c-5)
    /// </summary>
    private static long ComputeM_Diagonal1(int c)
    {
        if (c < 4) return 0;
        
        long factorial = Factorial(c - 1);
        long product = (long)(c - 2) * (c - 3);
        int power = c - 5;
        
        if (power < 0)
            return factorial * product >> (-power);
        
        return factorial * product * (1L << power);
    }

    /// <summary>
    /// m(c,c-2) computed from N(c,c-2,u) values.
    /// </summary>
    private static long ComputeM_Diagonal2(int c)
    {
        if (c < 4) return 0;
        
        long n0 = ComputeN_Diagonal2_u0(c);
        long n2 = ComputeN_Diagonal2_u2(c);
        long n4 = ComputeN_Diagonal2_u4(c);
        
        return n0 + 4 * n2 + 16 * n4;
    }

    /// <summary>
    /// m(c,c-3) computed from N(c,c-3,u) values.
    /// </summary>
    private static long ComputeM_Diagonal3(int c)
    {
        if (c < 6) return 0;

        long n0 = ComputeN_Diagonal3_u0(c);
        long n2 = ComputeN_Diagonal3_u2(c);
        long n4 = ComputeN_Diagonal3_u4(c);
        long n6 = ComputeN_Diagonal3_u6(c);

        return n0 + 4 * n2 + 16 * n4 + 64 * n6;
    }

    /// <summary>
    /// General formula for d >= 4.
    /// </summary>
    private static long ComputeM_General(int c, int k)
    {
        int d = c - k;
        if (d < 2) return 0;
        
        long n0 = ComputeN_General(c, d, 0);
        long n2 = ComputeN_General(c, d, 2);
        long n4 = ComputeN_General(c, d, 4);
        
        return n0 + 4 * n2 + 16 * n4;
    }

    #endregion

    #region N(c,k,u) Diagonal Formulas (long version)

    private static long ComputeN_Diagonal2_u0(int c)
    {
        if (c < 4) return 0;
        long factorial = Factorial(c - 2);
        long binomial = Binomial(c - 1, 3);
        return Shift(factorial * binomial, c - 5);
    }

    private static long ComputeN_Diagonal2_u2(int c)
    {
        long factorial = Factorial(c - 2);
        long binomial = Binomial(c - 1, 4);
        return Shift(factorial * binomial, c - 6);
    }

    private static long ComputeN_Diagonal2_u4(int c)
    {
        long factorial = Factorial(c - 2);
        long binomial = Binomial(c - 1, 5);
        return Shift(factorial * binomial, c - 9);
    }

    private static long ComputeN_Diagonal3_u0(int c)
    {
        long factorial = Factorial(c - 3);
        long binomial = Binomial(c - 1, 5);
        return Shift(factorial * binomial, c - 5) / 3;
    }

    private static long ComputeN_Diagonal3_u2(int c)
    {
        long factorial = Factorial(c - 3);
        long binomial = Binomial(c - 1, 6);
        return Shift(factorial * binomial, c - 7);
    }

    private static long ComputeN_Diagonal3_u4(int c)
    {
        long factorial = Factorial(c - 3);
        long binomial = Binomial(c - 1, 7);
        return Shift(factorial * binomial, c - 9);
    }

    private static long ComputeN_Diagonal3_u6(int c)
    {
        long factorial = Factorial(c - 3);
        long binomial = Binomial(c - 1, 8);
        return Shift(factorial * binomial, c - 11) / 3;
    }

    /// <summary>
    /// Computes N(c, c-d, u) using the discovered patterns.
    /// </summary>
    private static long ComputeN_General(int c, int d, int u)
    {
        if (d < 2) return 0;
        
        long termFact = Factorial(c - d);
        int binomK = 2 * d - 1 + u / 2;
        long termBinom = Binomial(c - 1, binomK);
        
        if (termFact == 0 || termBinom == 0) return 0;
        
        long numA = 1;
        long denA = 1;
        int B = 0;
        
        bool isPowerOf2 = (d & (d - 1)) == 0;
        
        if (u == 0)
        {
            if (isPowerOf2)
            {
                numA = 1;
                denA = 1;
                B = (3 * d) / 2 + 2;
            }
            else
            {
                numA = 1;
                denA = d;
                B = d + 2;
            }
        }
        else if (u == 2)
        {
            numA = 1;
            denA = 1;
            B = d + 4;
        }
        else if (u == 4)
        {
            if (isPowerOf2)
            {
                numA = 3;
                denA = 1;
                B = d + 7;
            }
            else
            {
                numA = 1;
                denA = 1;
                B = d + 6;
            }
        }
        else
        {
            return 0;
        }
        
        int power = c - B;
        BigInteger bigResult = (BigInteger)termFact * termBinom * numA;
        
        if (power >= 0)
            bigResult <<= power;
        else
            bigResult >>= (-power);
            
        bigResult /= denA;
        
        return (long)bigResult;
    }

    #endregion

    #region BigInteger N(c,k,u) Computation

    /// <summary>
    /// Compute N(c, c-d, u) using BigInteger for arbitrary precision.
    /// </summary>
    private static BigInteger ComputeN_Big(int c, int d, int u)
    {
        if (d < 1) return 0;
        
        int k = c - d;
        BigInteger termFact = FactorialBig(k);
        int binomK = 2 * d - 1 + u / 2;
        BigInteger termBinom = BinomialBig(c - 1, binomK);
        
        if (termFact == 0 || termBinom == 0) return 0;
        
        BigInteger numA = 1;
        BigInteger denA = 1;
        int B = 0;
        
        bool isPowerOf2 = d > 0 && (d & (d - 1)) == 0;
        
        if (u == 0)
        {
            if (d == 1)
            {
                numA = c - 3;
                denA = 1;
                B = 4;
            }
            else if (isPowerOf2)
            {
                numA = 1;
                denA = 1;
                B = (3 * d) / 2 + 2;
            }
            else
            {
                numA = 1;
                denA = d;
                B = d + 2;
            }
        }
        else if (u == 2)
        {
            if (d == 1)
            {
                numA = 1;
                denA = 1;
                B = 7;
            }
            else
            {
                numA = 1;
                denA = 1;
                B = d + 4;
            }
        }
        else if (u == 4)
        {
            if (isPowerOf2)
            {
                numA = 3;
                denA = 1;
                B = d + 7;
            }
            else
            {
                numA = 1;
                denA = 1;
                B = d + 6;
            }
        }
        else
        {
            return 0;
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

    private static long Factorial(int n)
    {
        if (n < 0) return 0;
        if (n <= 1) return 1;
        
        long result = 1;
        for (int i = 2; i <= n; i++)
            result *= i;
        return result;
    }

    private static long Binomial(int n, int k)
    {
        if (k > n || k < 0) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;
        
        long result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }
    
    private static long Shift(long val, int shift)
    {
        if (shift >= 0) return val << shift;
        return val >> (-shift);
    }

    private static BigInteger FactorialBig(int n)
    {
        if (n < 0) return 0;
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

    #endregion
}
