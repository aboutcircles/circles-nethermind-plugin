using System.Numerics;

namespace Circles.Common;

public class Demurrage
{
    private static readonly BigInteger GAMMA_64 = new(18443079296116538654);

    /// <summary>
    /// Applies daily demurrage to a stored (already-discounted) balance.
    ///  
    /// * <paramref name="storedBalance"/> – balance that was last updated on
    ///   <paramref name="storedDay"/> (Q: demurraged already).  
    /// * <paramref name="storedDay"/> – day index when that balance was written.  
    /// * <paramref name="targetDay"/> – day you want the up-to-date balance for.
    ///
    /// Returns a tuple of  
    ///   (<c>newBalance</c>, <c>discountCost</c>)  
    /// where <c>discountCost = storedBalance − newBalance</c>.
    ///
    /// Implementation is **bit-identical** to the Solidity code path:
    ///   discountFactor = γ^(Δdays)   (γ stored in Q64.64)  
    ///   newBalance     = floor(storedBalance · discountFactor)  
    ///   burn           = storedBalance − newBalance
    /// </summary>
    public static (BigInteger newBalance, BigInteger discountCost) ApplyDemurrage(BigInteger storedBalance,
        ulong storedDay, ulong targetDay)
    {
        if (targetDay <= storedDay || storedBalance.IsZero)
            return (storedBalance, BigInteger.Zero);

        ulong delta = targetDay - storedDay;

        // γ^Δ in Q64.64  – uses our Pow() that now matches ABDK.powu 1-for-1
        BigInteger factor = Fixed64.Pow(GAMMA_64, delta);

        // floor(storedBalance · factor / 2⁶⁴)
        BigInteger newBal = Fixed64.MulU(factor, storedBalance);

        BigInteger burned = storedBalance - newBal;
        return (newBal, burned);
    }
}
