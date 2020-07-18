using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

        public static uint HistCountParallelWorkspace(Span<uint> count, ref int maxSymbolValueRef, ReadOnlySpan<byte> source, bool checkMax, Span<byte> workspace)
        {
            Debug.Assert(workspace.Length >= 4 * 4 * 256);

            int maxSymbolValue = maxSymbolValueRef;

            Span<uint> counting1 = MemoryMarshal.Cast<byte, uint>(workspace.Slice(0, 1024));
            Span<uint> counting2 = MemoryMarshal.Cast<byte, uint>(workspace.Slice(1024, 1024));
            Span<uint> counting3 = MemoryMarshal.Cast<byte, uint>(workspace.Slice(2 * 1024, 1024));
            Span<uint> counting4 = MemoryMarshal.Cast<byte, uint>(workspace.Slice(3 * 1024, 1024));

            workspace.Clear();

            /* safety checks */
            if (source.IsEmpty)
            {
                count.Slice(0, maxSymbolValue + 1);
                maxSymbolValueRef = 0;
                return 0;
            }
            if (maxSymbolValue == 0)
            {
                maxSymbolValue = 255;
            }

            ref byte sourceRef = ref MemoryMarshal.GetReference(source);
            int length = source.Length;
            Debug.Assert(length >= 4);

            uint cached = Unsafe.ReadUnaligned<uint>(ref sourceRef);
            sourceRef = ref Unsafe.Add(ref sourceRef, 4);
            length -= 4;
            while (length >= 16)
            {
                uint c = cached;
                cached = Unsafe.ReadUnaligned<uint>(ref sourceRef);
                sourceRef = ref Unsafe.Add(ref sourceRef, 4);
                length -= 4;

                counting1[(byte)c]++;
                counting2[(byte)(c >> 8)]++;
                counting3[(byte)(c >> 16)]++;
                counting4[(byte)(c >> 24)]++;

                c = cached;
                cached = Unsafe.ReadUnaligned<uint>(ref sourceRef);
                sourceRef = ref Unsafe.Add(ref sourceRef, 4);
                length -= 4;

                counting1[(byte)c]++;
                counting2[(byte)(c >> 8)]++;
                counting3[(byte)(c >> 16)]++;
                counting4[(byte)(c >> 24)]++;

                c = cached;
                cached = Unsafe.ReadUnaligned<uint>(ref sourceRef);
                sourceRef = ref Unsafe.Add(ref sourceRef, 4);
                length -= 4;

                counting1[(byte)c]++;
                counting2[(byte)(c >> 8)]++;
                counting3[(byte)(c >> 16)]++;
                counting4[(byte)(c >> 24)]++;

                c = cached;
                cached = Unsafe.ReadUnaligned<uint>(ref sourceRef);
                sourceRef = ref Unsafe.Add(ref sourceRef, 4);
                length -= 4;

                counting1[(byte)c]++;
                counting2[(byte)(c >> 8)]++;
                counting3[(byte)(c >> 16)]++;
                counting4[(byte)(c >> 24)]++;
            }

            sourceRef = ref Unsafe.Add(ref sourceRef, -4);
            length += 4;

            while (length-- != 0)
            {
                counting1[sourceRef]++;
                sourceRef = ref Unsafe.Add(ref sourceRef, 1);
            }

            if (checkMax)
            {
                for (int s = 255; s > maxSymbolValue; s--)
                {
                    counting1[s] += counting2[s] + counting3[s] + counting4[s];
                    if (counting1[s] != 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(maxSymbolValueRef));
                    }
                }
            }

            if (maxSymbolValue > 255)
            {
                maxSymbolValue = 255;
            }
            uint max = 0;
            for (int s = 0; s <= maxSymbolValue; s++)
            {
                count[s] = counting1[s] + counting2[s] + counting3[s] + counting4[s];
                if (count[s] > max)
                {
                    max = count[s];
                }
            }

            while (count[maxSymbolValue] == 0)
            {
                maxSymbolValue--;
            }
            maxSymbolValueRef = maxSymbolValue;
            return max;
        }

        public static uint HistCountParallel(Span<uint> count, ref int maxSymbolValueRef, ReadOnlySpan<byte> source, bool checkMax)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                return HistCountParallelWorkspace(count, ref maxSymbolValueRef, source, checkMax, buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static uint HistCount(Span<uint> count, ref int maxSymbolValueRef, ReadOnlySpan<byte> source)
        {
            if (source.Length < 1500) /* heuristic threshold */
            {
                return HistCountSimple(count, ref maxSymbolValueRef, source);
            }
            if (maxSymbolValueRef < 255)
            {
                return HistCountParallel(count, ref maxSymbolValueRef, source, true);
            }
            else
            {
                return HistCountParallel(count, ref maxSymbolValueRef, source, false);
            }
        }

    }
}
