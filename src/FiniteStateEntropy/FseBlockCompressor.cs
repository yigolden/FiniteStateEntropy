using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FiniteStateEntropy
{
    internal static class FseBlockCompressor
    {
        internal const int FSE_MAX_SYMBOL_VALUE = 255;

        internal const int FSE_MAX_MEMORY_USAGE = 14;
        internal const int FSE_DEFAULT_MEMORY_USAGE = 13;

        internal const int FSE_MAX_TABLELOG = FSE_MAX_MEMORY_USAGE - 2;
        internal const int FSE_MAX_TABLESIZE = (1 << FSE_MAX_TABLELOG);
        internal const int FSE_MAXTABLESIZE_MASK = FSE_MAX_TABLESIZE - 1;
        internal const int FSE_DEFAULT_TABLELOG = FSE_DEFAULT_MEMORY_USAGE - 2;
        internal const int FSE_MIN_TABLELOG = 5;
        internal const int FSE_TABLELOG_ABSOLUTE_MAX = 15;

        internal const int FSE_NCOUNTBOUND = 512;


        internal static int FseTableStep(int tableSize) => ((tableSize >> 1) + (tableSize >> 3) + 3);

        internal static int FseBlockBound(int size) => (size + (size >> 7));

        internal static int FseCompressedBound(int size) => (FSE_NCOUNTBOUND + FseBlockBound(size));

        public static void FseBuildCTableWksp(ref FseCompressTable ct, ReadOnlySpan<short> normalizedCounter, int maxSymbolValue, int tableLog, Span<byte> workSpace)
        {
            int tableSize = 1 << tableLog;
            int tableMask = tableSize - 1;
            int step = FseTableStep(tableSize);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(4 * (FSE_MAX_SYMBOL_VALUE + 2));
            try
            {
                Span<int> cumul = MemoryMarshal.Cast<byte, int>(buffer.AsSpan()).Slice(0, FSE_MAX_SYMBOL_VALUE + 2);

                Span<byte> tableSymbol = workSpace;
                int highThreshold = tableSize - 1;

                if ((1 << tableLog) > workSpace.Length)
                {
                    throw new ArgumentException("Workspace is too small.", nameof(workSpace));
                }
                ct.tableLog = (ushort)tableLog;
                ct.maxSymbolValue = (ushort)maxSymbolValue;
                Debug.Assert(tableLog < 16);

                /* symbol start positions */
                {
                    cumul[0] = 0;
                    for (int u = 1; u <= maxSymbolValue + 1; u++)
                    {
                        if (normalizedCounter[u - 1] == -1) /* Low proba symbol */
                        {
                            cumul[u] = cumul[u - 1] + 1;
                            tableSymbol[highThreshold--] = (byte)(u - 1);
                        }
                        else
                        {
                            cumul[u] = cumul[u - 1] + normalizedCounter[u - 1];
                        }

                    }
                    cumul[maxSymbolValue + 1] = tableSize + 1;
                }

                /* Spread symbols */
                {
                    int position = 0;
                    for (int symbol = 0; symbol <= maxSymbolValue; symbol++)
                    {
                        for (int nbOccurences = 0; nbOccurences < normalizedCounter[symbol]; nbOccurences++)
                        {
                            tableSymbol[position] = (byte)symbol;
                            position = (position + step) & tableMask;
                            while (position > highThreshold) /* Low proba area */
                            {
                                position = (position + step) & tableMask;
                            }
                        }
                    }

                    if (position != 0) /* Must have gone through all positions */
                    {
                        throw new InvalidDataException();
                    }
                }

                /* Build table */
                var nextStateNumber = ct.nextStateNumber;
                {
                    for (int u = 0; u < tableSize; u++)
                    {
                        byte s = tableSymbol[u];
                        nextStateNumber[cumul[s]++] = (ushort)(tableSize + u);
                    }
                }

                /* Build Symbol Transformation Table */
                var symbolTT = ct.symbolTT;
                {
                    int total = 0;
                    for (int s = 0; s <= maxSymbolValue; s++)
                    {
                        switch (normalizedCounter[s])
                        {
                            case 0:
                                /* filling nonetheless, for compatibility with FSE_getMaxNbBits() */
                                symbolTT[s].deltaNbBits = (uint)(((tableLog + 1) << 16) - (1 << tableLog));
                                break;

                            case -1:
                            case 1:
                                symbolTT[s].deltaNbBits = (uint)((tableLog << 16) - (1 << tableLog));
                                symbolTT[s].deltaFindState = total - 1;
                                total++;
                                break;

                            default:
                                uint maxBitsOut = (uint)tableLog - (uint)MathHelper.Log2((uint)(normalizedCounter[s] - 1));
                                uint minStatePlus = (uint)normalizedCounter[s] << (int)maxBitsOut;
                                symbolTT[s].deltaNbBits = (maxBitsOut << 16) - minStatePlus;
                                symbolTT[s].deltaFindState = total - normalizedCounter[s];
                                total += normalizedCounter[s];
                                break;
                        }

                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static void FseBuildCTable(ref FseCompressTable ct, ReadOnlySpan<short> normalizedCounter, int maxSymbolValue, int tableLog)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FSE_MAX_TABLESIZE);
            try
            {
                FseBuildCTableWksp(ref ct, normalizedCounter, maxSymbolValue, tableLog, buffer.AsSpan(0, FSE_MAX_TABLESIZE));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static int FseNCountWriteBound(int maxSymbolValue, int tableLog)
        {
            int maxHeaderSize = (((maxSymbolValue + 1) * tableLog) >> 3) + 3;
            return maxSymbolValue != 0 ? maxHeaderSize : FSE_NCOUNTBOUND;
        }

        public static bool FseWriteNCountGeneric(Span<byte> buffer, ReadOnlySpan<short> normalizedCounter, int maxSymbolValue, int tableLog, out int bytesWritten)
        {
            int tableSize = 1 << tableLog;
            int charnum = 0;
            bool previous0 = false;

            bytesWritten = 0;

            /* Table Size */
            uint bitStream = (uint)(tableLog - FSE_MIN_TABLELOG);
            int bitCount = 4;

            /* Init */
            int remaining = tableSize + 1;  /* +1 for extra accuracy */
            int threshold = tableSize;
            int nbBits = tableLog + 1;

            while (remaining > 1) /* stops at 1 */
            {
                if (previous0)
                {
                    int start = charnum;
                    while (normalizedCounter[charnum] == 0)
                    {
                        charnum++;
                    }
                    while (charnum >= start + 24)
                    {
                        start += 24;
                        bitStream += 0xFFFFu << bitCount;
                        if (buffer.Length < 2)
                        {
                            return false;
                        }
                        buffer[0] = (byte)bitStream;
                        buffer[1] = (byte)(bitStream >> 8);
                        buffer = buffer.Slice(2);
                        bytesWritten += 2;
                        bitStream >>= 16;
                    }
                    while (charnum >= start + 3)
                    {
                        start += 3;
                        bitStream += 3u << bitCount;
                        bitCount += 2;
                    }
                    bitStream += (uint)(charnum - start) << bitCount;
                    bitCount += 2;
                    if (bitCount > 16)
                    {
                        if (buffer.Length < 2)
                        {
                            return false;
                        }
                        buffer[0] = (byte)bitStream;
                        buffer[1] = (byte)(bitStream >> 8);
                        buffer = buffer.Slice(2);
                        bytesWritten += 2;
                        bitStream >>= 16;
                        bitCount -= 16;
                    }
                }
                {
                    int count = normalizedCounter[charnum++];
                    int max = (2 * threshold - 1) - remaining;
                    remaining -= count < 0 ? -count : count;
                    count++;   /* +1 for extra accuracy */
                    if (count >= threshold) /* [0..max[ [max..threshold[ (...) [threshold+max 2*threshold[ */
                    {
                        count += max;
                    }
                    bitStream += (uint)count << bitCount;
                    bitCount += nbBits;
                    bitCount -= (count < max) ? 1 : 0;
                    previous0 = (count == 1);
                    if (remaining < 1)
                    {
                        return false;
                    }
                    while (remaining < threshold)
                    {
                        nbBits--;
                        threshold >>= 1;
                    }
                }
                if (bitCount > 16)
                {
                    if (buffer.Length < 2)
                    {
                        return false;
                    }
                    buffer[0] = (byte)bitStream;
                    buffer[1] = (byte)(bitStream >> 8);
                    buffer = buffer.Slice(2);
                    bytesWritten += 2;
                    bitStream >>= 16;
                    bitCount -= 16;
                }
            }

            /* flush remaining bitStream */
            if (buffer.Length < 2)
            {
                return false;
            }
            buffer[0] = (byte)bitStream;
            buffer[1] = (byte)(bitStream >> 8);
            bytesWritten += (bitCount + 7) / 8;

            if (charnum > maxSymbolValue + 1)
            {
                return false;
            }

            return true;
        }

        public static bool FseWriteNCount(Span<byte> buffer, ReadOnlySpan<short> normalizedCounter, int maxSymbolValue, int tableLog, out int bytesWritten)
        {
            if (tableLog > FSE_MAX_TABLELOG || tableLog < FSE_MIN_TABLELOG)
            {
                throw new ArgumentOutOfRangeException(nameof(tableLog));
            }

            return FseWriteNCountGeneric(buffer, normalizedCounter, maxSymbolValue, tableLog, out bytesWritten);
        }

        private static int FseCompressTableSizeU32(int maxTableLog, int maxSymbolValue) => (1 + (1 << (maxTableLog - 1)) + ((maxSymbolValue + 1) * 2));

        public static void FseNormalizeM2(Span<short> norm, int tableLog, ReadOnlySpan<uint> count, int total, int maxSymbolValue)
        {
            const short NOT_YET_ASSIGNED = -2;

            int distributed = 0;

            /* Init */
            int lowThreshold = total >> tableLog;
            int lowOne = (total * 3) >> (tableLog + 1);

            for (int s = 0; s <= maxSymbolValue; s++)
            {
                if (count[s] == 0)
                {
                    norm[s] = 0;
                    continue;
                }
                if (count[s] <= lowThreshold)
                {
                    norm[s] = -1;
                    distributed++;
                    total -= (int)count[s];
                    continue;
                }
                if (count[s] <= lowOne)
                {
                    norm[s] = 1;
                    distributed++;
                    total -= (int)count[s];
                    continue;
                }

                norm[s] = NOT_YET_ASSIGNED;
            }
            int toDistribute = (1 << tableLog) - distributed;

            if ((total / toDistribute) > lowOne)
            {
                /* risk of rounding to zero */
                lowOne = (total * 3) / (toDistribute * 2);
                for (int s = 0; s <= maxSymbolValue; s++)
                {
                    if ((norm[s] == NOT_YET_ASSIGNED) && (count[s] <= lowOne))
                    {
                        norm[s] = 1;
                        distributed++;
                        total -= (int)count[s];
                        continue;
                    }
                }
                toDistribute = (1 << tableLog) - distributed;
            }

            if (distributed == maxSymbolValue + 1)
            {
                /* all values are pretty poor;
                   probably incompressible data (should have already been detected);
                   find max, then give all remaining points to max */
                int maxV = 0, maxC = 0;
                for (int s = 0; s <= maxSymbolValue; s++)
                {
                    if (count[s] > maxC)
                    {
                        maxV = s;
                        maxC = (int)count[s];
                    }
                }
                norm[maxV] += (short)toDistribute;
            }

            if (total == 0)
            {
                /* all of the symbols were low enough for the lowOne or lowThreshold */
                for (int s = 0; toDistribute > 0; s = (s + 1) % (maxSymbolValue + 1))
                {
                    if (norm[s] > 0)
                    {
                        toDistribute--;
                        norm[s]++;
                    }
                }

                return;
            }

            {
                int vStepLog = 62 - tableLog;
                long mid = (1L << (vStepLog - 1)) - 1;
                long rStep = (((1L << vStepLog) * toDistribute) + mid) / total;
                long tmpTotal = mid;
                for (int s = 0; s <= maxSymbolValue; s++)
                {
                    if (norm[s] == NOT_YET_ASSIGNED)
                    {
                        long end = tmpTotal + (count[s] * rStep);
                        int sStart = (int)(tmpTotal >> vStepLog);
                        int sEnd = (int)(end >> vStepLog);
                        int weight = sEnd - sStart;
                        if (weight < 1)
                        {
                            throw new InvalidDataException();
                        }
                        norm[s] = (short)weight;
                        tmpTotal = end;
                    }
                }

            }

        }

        private static int FseMinTableLog(int srcSize, int maxSymbolValue)
        {
            int minBitsSrc = MathHelper.Log2((uint)(srcSize - 1)) + 1;
            int minBitsSymbols = MathHelper.Log2((uint)maxSymbolValue) + 2;
            int minBits = minBitsSrc < minBitsSymbols ? minBitsSrc : minBitsSymbols;
            Debug.Assert(srcSize > 1); /* Not supported, RLE should be used instead */
            return minBits;
        }

        private static readonly uint[] s_rtbTable = new uint[] { 0, 473195, 504333, 520860, 550000, 700000, 750000, 830000 };

        public static int FseNormalizeCount(Span<short> normalizedCounter, int tableLog, ReadOnlySpan<uint> count, int total, int maxSymbolValue)
        {
            if (tableLog == 0)
            {
                tableLog = FSE_DEFAULT_TABLELOG;
            }
            if (tableLog < FSE_MIN_TABLELOG || tableLog > FSE_MAX_TABLELOG)
            {
                throw new ArgumentOutOfRangeException(nameof(tableLog));
            }

            if (tableLog < FseMinTableLog(total, maxSymbolValue))
            {
                throw new ArgumentException("Too small tableLog, compression potentially impossible.", nameof(tableLog));
            }

            uint[] rtbTable = s_rtbTable;
            int scale = 62 - tableLog;
            long step = (1L << 62) / total;
            long vStep = 1L << (scale - 20);
            int stillToDistribute = 1 << tableLog;

            int largest = 0;
            short largestP = 0;
            int lowThreshold = total >> tableLog;

            for (int s = 0; s <= maxSymbolValue; s++)
            {
                if (count[s] == total)
                {
                    /* rle special case */
                    return 0;
                }
                if (count[s] == 0)
                {
                    normalizedCounter[s] = 0;
                    continue;
                }
                if (count[s] <= lowThreshold)
                {
                    normalizedCounter[s] = -1;
                    stillToDistribute--;
                }
                else
                {
                    short proba = (short)((count[s] * step) >> scale);
                    if (proba < 8)
                    {
                        long restToBeat = vStep * rtbTable[proba];
                        proba += (count[s] * step) - ((long)proba << scale) > restToBeat ? (short)1 : (short)0;
                    }
                    if (proba > largestP)
                    {
                        largestP = proba;
                        largest = s;
                    }
                    normalizedCounter[s] = proba;
                    stillToDistribute -= proba;
                }
            }
            if (-stillToDistribute >= (normalizedCounter[largest] >> 1))
            {
                /* corner case, need another normalization method */
                FseNormalizeM2(normalizedCounter, tableLog, count, total, maxSymbolValue);
            }
            else
            {
                normalizedCounter[largest] += (short)stillToDistribute;
            }

            return tableLog;
        }

        public static void FseBuildCTableRaw(ref FseCompressTable ct, int nbBits)
        {
            int tableSize = 1 << nbBits;
            int tableMask = tableSize - 1;
            int maxSymbolValue = tableMask;

            Span<FseSymbolCompressionTransform> symbolTT = ct.symbolTT;

            /* Sanity checks */
            if (nbBits < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(nbBits));
            }

            /* header */
            ct.tableLog = (ushort)nbBits;
            ct.maxSymbolValue = (ushort)maxSymbolValue;

            /* Build table */
            Span<ushort> nextStateNumber = ct.nextStateNumber;
            for (int s = 0; s < tableSize; s++)
            {
                nextStateNumber[s] = (ushort)(tableSize + s);
            }

            /* Build Symbol Transformation Table */
            int deltaNbBits = (nbBits << 16) - (1 << nbBits);
            for (int s = 0; s <= maxSymbolValue; s++)
            {
                symbolTT[s].deltaNbBits = (uint)deltaNbBits;
                symbolTT[s].deltaFindState = s - 1;
            }
        }

        public static void FseBuildCTableRle(ref FseCompressTable ct, byte symbolValue)
        {
            /* header */
            ct.tableLog = 0;
            ct.maxSymbolValue = symbolValue;

            /* Build table */
            Span<ushort> nextStateNumber = ct.nextStateNumber;
            nextStateNumber[0] = 0;
            nextStateNumber[1] = 0;  /* just in case */

            /* Build Symbol Transformation Table */
            Span<FseSymbolCompressionTransform> symbolTT = ct.symbolTT;
            symbolTT[symbolValue].deltaFindState = 0;
            symbolTT[symbolValue].deltaFindState = 0;
        }

        public static int FseCompressUsingCTableGeneric(Span<byte> destination, ReadOnlySpan<byte> source, in FseCompressTable ct)
        {
            if (source.Length <= 2)
            {
                return 0;
            }

            int destinationLength = destination.Length;
            var writer = new FseBitWriter(destination);

            FseCompressState state1, state2;

            if ((source.Length & 1) != 0)
            {
                state1 = new FseCompressState(in ct, source[source.Length - 1]);
                state2 = new FseCompressState(in ct, source[source.Length - 2]);
                state1.EncodeSymbol(ref writer, source[source.Length - 3]);

                source = source.Slice(0, source.Length - 3);
            }
            else
            {
                state2 = new FseCompressState(in ct, source[source.Length - 1]);
                state1 = new FseCompressState(in ct, source[source.Length - 2]);

                source = source.Slice(0, source.Length - 2);
            }

            Debug.Assert((source.Length & 1) == 0);

            while (source.Length >= 16)
            {
                ref byte sourceRef = ref MemoryMarshal.GetReference(source);
                sourceRef = ref Unsafe.Add(ref sourceRef, source.Length - 16);

                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 15));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 14));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 13));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 12));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 11));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 10));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 9));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 8));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 7));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 6));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 5));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 4));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 3));
                state1.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 2));
                state2.EncodeSymbol(ref writer, Unsafe.Add(ref sourceRef, 1));
                state1.EncodeSymbol(ref writer, sourceRef);

                source = source.Slice(0, source.Length - 16);
            }

            while (!source.IsEmpty)
            {
                state2.EncodeSymbol(ref writer, source[source.Length - 1]);
                state1.EncodeSymbol(ref writer, source[source.Length - 2]);

                source = source.Slice(0, source.Length - 2);
            }

            state2.Flush(ref writer);
            state1.Flush(ref writer);
            writer.WriteBits(1, 1);
            writer.FlushFinal();

            return destinationLength - writer.RemainingLength;
        }

        public static int FseCompressUsingCTable(Span<byte> destination, ReadOnlySpan<byte> source, in FseCompressTable ct)
        {
            return FseCompressUsingCTableGeneric(destination, source, in ct);
        }

        private static int FseOptimizeTableLogInternal(int maxTableLog, int srcSize, int maxSymbolValue, int minus)
        {
            int maxBitsSrc = MathHelper.Log2((uint)(srcSize - 1)) - minus;
            int tableLog = maxTableLog;
            int minBits = FseMinTableLog(srcSize, maxSymbolValue);
            Debug.Assert(srcSize > 1); /* Not supported, RLE should be used instead */
            if (tableLog == 0)
            {
                tableLog = FSE_DEFAULT_TABLELOG;
            }
            if (maxBitsSrc < tableLog)
            {
                tableLog = maxBitsSrc;
            }
            if (minBits > tableLog)
            {
                tableLog = minBits;
            }
            if (tableLog < FSE_MIN_TABLELOG)
            {
                tableLog = FSE_MIN_TABLELOG;
            }
            if (tableLog > FSE_MAX_TABLELOG)
            {
                tableLog = FSE_MAX_TABLELOG;
            }
            return tableLog;
        }

        private static int FseOptimizeTableLog(int maxTableLog, int srcSize, int maxSymbolValue)
        {
            return FseOptimizeTableLogInternal(maxTableLog, srcSize, maxSymbolValue, 2);
        }

        private static int FseWorkspaceSizeU32(int maxTableLog, int maxSymbolValue) => (FseCompressTableSizeU32(maxTableLog, maxSymbolValue) + ((maxTableLog > 12) ? (1 << (maxTableLog - 2)) : 1024));

        private static FseCompressTable AllocateCTable(Span<byte> buffer, int maxTableLog, int maxSymbolValue)
        {
            int offset = (1 << (maxTableLog - 1)) * 4;
            return new FseCompressTable
            {
                nextStateNumber = MemoryMarshal.Cast<byte, ushort>(buffer.Slice(0, offset)),
                symbolTT = MemoryMarshal.Cast<byte, FseSymbolCompressionTransform>(buffer.Slice(offset, (maxSymbolValue + 1) * 8))
            };
        }

        public static int FseCompressWorkspace(Span<byte> destination, ReadOnlySpan<byte> source, int maxSymbolValue, int tableLog, Span<byte> workspace)
        {
            int destinationLength = destination.Length;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(4 * (FSE_MAX_SYMBOL_VALUE + 1) + 2 * (FSE_MAX_SYMBOL_VALUE + 1));
            try
            {
                Span<uint> count = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, 4 * (FSE_MAX_SYMBOL_VALUE + 1)));
                Span<short> norm = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(4 * (FSE_MAX_SYMBOL_VALUE + 1), 2 * (FSE_MAX_SYMBOL_VALUE + 1)));

                int CTableSize = 4 * FseCompressTableSizeU32(tableLog, maxSymbolValue);
                FseCompressTable CTable = AllocateCTable(workspace, tableLog, maxSymbolValue);
                Span<byte> scratchBuffer = workspace.Slice(CTableSize);

                /* init conditions */
                if (workspace.Length < FseWorkspaceSizeU32(tableLog, maxSymbolValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(tableLog), "Table log is too large.");
                }
                if (source.Length <= 1)
                {
                    return 0; /* Not compressible */
                }
                if (maxSymbolValue == 0)
                {
                    maxSymbolValue = FSE_MAX_SYMBOL_VALUE;
                }
                if (tableLog == 0)
                {
                    tableLog = FSE_DEFAULT_TABLELOG;
                }

                /* Scan input and build symbol stats */
                {
                    int maxCount = (int)Hist.HistCount(count, ref maxSymbolValue, source);
                    if (maxCount == source.Length)
                    {
                        return 1; /* only a single symbol in src : rle */
                    }
                    if (maxCount == 1)
                    {
                        return 0; /* each symbol present maximum once => not compressible */
                    }
                    if (maxCount < (source.Length >> 7))
                    {
                        return 0; /* Heuristic : not compressible enough */
                    }
                }

                tableLog = FseOptimizeTableLog(tableLog, source.Length, maxSymbolValue);
                FseNormalizeCount(norm, tableLog, count, source.Length, maxSymbolValue);

                /* Write table description header */
                if (!FseWriteNCount(destination, norm, maxSymbolValue, tableLog, out int bytesWritten))
                {
                    throw new InvalidDataException();
                }
                destination = destination.Slice(bytesWritten);

                /* Compress */
                FseBuildCTableWksp(ref CTable, norm, maxSymbolValue, tableLog, scratchBuffer);

                bytesWritten = FseCompressUsingCTable(destination, source, in CTable);
                destination = destination.Slice(bytesWritten);

                /* check compressibility */
                if ((destinationLength - destination.Length) >= (source.Length - 1))
                {
                    return 0;
                }

                return destinationLength - destination.Length;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static int FseCompress2(Span<byte> destination, ReadOnlySpan<byte> source, int maxSymbolValue, int tableLog)
        {
            byte[] scratchBuffer = ArrayPool<byte>.Shared.Rent(4 * FseCompressTableSizeU32(FSE_MAX_TABLELOG, FSE_MAX_SYMBOL_VALUE) + (1 << FSE_MAX_TABLELOG));
            try
            {
                if (tableLog > FSE_MAX_TABLELOG)
                {
                    throw new ArgumentOutOfRangeException(nameof(tableLog));
                }
                return FseCompressWorkspace(destination, source, maxSymbolValue, tableLog, scratchBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratchBuffer);
            }
        }

        public static int Compress(Span<byte> destination, ReadOnlySpan<byte> source)
        {
            return FseCompress2(destination, source, FSE_MAX_SYMBOL_VALUE, FSE_DEFAULT_TABLELOG);
        }
    }
}
