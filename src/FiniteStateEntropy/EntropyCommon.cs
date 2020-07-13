using System;
using System.Buffers.Binary;
using System.Diagnostics;

namespace FiniteStateEntropy
{
    public static class EntropyCommon
    {
        private const int FSE_MIN_TABLELOG = 5;
        private const int FSE_TABLELOG_ABSOLUTE_MAX = 15;

        public static bool FseReadNCount(ReadOnlySpan<byte> headerBuffer, Span<short> normalizedCounter, ref int maxSymbolValue, out int tableLog, out int nCountLength)
        {
            int bufferLength = headerBuffer.Length;
            if (headerBuffer.Length < 4)
            {
                Span<byte> stackBuffer = stackalloc byte[4];
                stackBuffer.Clear();
                headerBuffer.CopyTo(stackBuffer);

                return FseReadNCount(stackBuffer, normalizedCounter, ref maxSymbolValue, out tableLog, out nCountLength);
            }

            uint bitStream = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);
            int nbBits = (int)(bitStream & 0xF) + FSE_MIN_TABLELOG;
            if (nbBits > FSE_TABLELOG_ABSOLUTE_MAX)
            {
                tableLog = default;
                nCountLength = default;
                return false;
            }
            tableLog = nbBits;

            bitStream >>= 4;
            int bitCount = 4;
            int remaining = (1 << nbBits) + 1;
            int threshold = 1 << nbBits;
            nbBits++;

            int charnum = 0;
            bool previous0 = false;
            while ((remaining > 1) && (charnum < maxSymbolValue))
            {
                if (previous0)
                {
                    int n0 = charnum;
                    while ((bitStream & 0xFFFF) == 0xFFFF)
                    {
                        n0 += 24;
                        if (headerBuffer.Length >= 5)
                        {
                            headerBuffer = headerBuffer.Slice(2);
                            bitStream = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer) >> bitCount;
                        }
                        else
                        {
                            bitStream >>= 16;
                            bitCount += 16;
                        }
                    }
                    while ((bitStream & 3) == 3)
                    {
                        n0 += 3;
                        bitStream >>= 2;
                        bitCount += 2;
                    }
                    n0 += (int)(bitStream & 3);
                    bitCount += 2;
                    if (n0 > maxSymbolValue)
                    {
                        //return ERROR(maxSymbolValue_tooSmall);
                        nCountLength = default;
                        return false;
                    }
                    while (charnum < n0) normalizedCounter[charnum++] = 0;
                    if (headerBuffer.Length >= 7 || headerBuffer.Length >= (bitCount / 8 + 4))
                    {
                        Debug.Assert((bitCount / 8) <= 3);
                        headerBuffer = headerBuffer.Slice(bitCount / 8);
                        bitCount &= 7;
                        bitStream = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer) >> bitCount;
                    }
                    else
                    {
                        bitStream >>= 2;
                    }
                }
                {
                    int max = (2 * threshold - 1) - remaining;
                    int count;

                    if ((bitStream & (threshold - 1)) < (uint)max)
                    {
                        count = (int)bitStream & (threshold - 1);
                        bitCount += nbBits - 1;
                    }
                    else
                    {
                        count = (int)bitStream & (2 * threshold - 1);
                        if (count >= threshold) count -= max;
                        bitCount += nbBits;
                    }

                    count--;   /* extra accuracy */
                    remaining -= count < 0 ? -count : count;   /* -1 means +1 */
                    normalizedCounter[charnum++] = (short)count;
                    previous0 = count == 0;
                    while (remaining < threshold)
                    {
                        nbBits--;
                        threshold >>= 1;
                    }

                    if (headerBuffer.Length >= 7 || headerBuffer.Length >= (bitCount / 8 + 4))
                    {
                        headerBuffer = headerBuffer.Slice(bitCount / 8);
                        bitCount &= 7;
                    }
                    else
                    {
                        bitCount -= 8 * (headerBuffer.Length - 4);
                        headerBuffer = headerBuffer.Slice(headerBuffer.Length - 4, 4);
                    }
                    bitStream = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer) >> (bitCount & 31);
                }
            }

            if (remaining != 1)
            {
                nCountLength = default;
                return false;
            }
            if (bitCount > 32)
            {
                nCountLength = default;
                return false;
            }
            {
                for (int symbNb = charnum; symbNb <= maxSymbolValue; symbNb++)
                {
                    normalizedCounter[symbNb] = 0;
                }
            }
            maxSymbolValue = charnum - 1;

            headerBuffer = headerBuffer.Slice((bitCount + 7) / 8);
            nCountLength = bufferLength - headerBuffer.Length;
            return true;
        }

    }
}
