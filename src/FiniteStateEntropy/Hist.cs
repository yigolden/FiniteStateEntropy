using System;

namespace FiniteStateEntropy
{
    internal static class Hist
    {
        public static uint HistCountSimple(Span<uint> count, ref int maxSymbolValueRef, ReadOnlySpan<byte> source)
        {
            if (source.IsEmpty)
            {
                maxSymbolValueRef = 0;
                return 0;
            }

            int maxSymbolValue = maxSymbolValueRef;
            uint largestCount = 0;

            count.Slice(0, maxSymbolValue + 1).Clear();

            for (int i = 0; i < source.Length; i++)
            {
                count[source[i]]++;
            }

            while (count[maxSymbolValue] == 0)
            {
                maxSymbolValue--;
            }
            maxSymbolValueRef = maxSymbolValue;

            for (int i = 0; i <= maxSymbolValue; i++)
            {
                if (count[i] > largestCount)
                {
                    largestCount = count[i];
                }
            }

            return largestCount;
        }

    }
}
