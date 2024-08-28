using System.Runtime.Intrinsics.X86;

namespace Circles.Index.Utils;

public class SimdOperations
{
    public unsafe bool BytesAreEqual(byte[] arr1, byte[] arr2)
    {
        int arr1Length = arr1.Length;

        if (arr1Length != arr2.Length)
            return false;

        fixed (byte* b00 = arr1, b01 = arr2)
        {
            byte* b0 = b00, b1 = b01, last0 = b0 + arr1Length, last1 = b1 + arr1Length, last32 = last0 - 31;

            if (arr1Length > 31)
            {
                while (b0 < last32)
                {
                    if (Avx2.MoveMask(Avx2.CompareEqual(Avx.LoadVector256(b0), Avx.LoadVector256(b1))) != -1)
                        return false;
                    b0 += 32;
                    b1 += 32;
                }
                return Avx2.MoveMask(Avx2.CompareEqual(Avx.LoadVector256(last0 - 32), Avx.LoadVector256(last1 - 32))) == -1;
            }

            if (arr1Length > 15)
            {
                if (Sse2.MoveMask(Sse2.CompareEqual(Sse2.LoadVector128(b0), Sse2.LoadVector128(b1))) != 65535)
                    return false;
                return Sse2.MoveMask(Sse2.CompareEqual(Sse2.LoadVector128(last0 - 16), Sse2.LoadVector128(last1 - 16))) == 65535;
            }

            if (arr1Length > 7)
            {
                if (*(ulong*)b0 != *(ulong*)b1)
                    return false;
                return *(ulong*)(last0 - 8) == *(ulong*)(last1 - 8);
            }

            if (arr1Length > 3)
            {
                if (*(uint*)b0 != *(uint*)b1)
                    return false;
                return *(uint*)(last0 - 4) == *(uint*)(last1 - 4);
            }

            if (arr1Length > 1)
            {
                if (*(ushort*)b0 != *(ushort*)b1)
                    return false;
                return *(ushort*)(last0 - 2) == *(ushort*)(last1 - 2);
            }

            return *b0 == *b1;
        }
    }
}