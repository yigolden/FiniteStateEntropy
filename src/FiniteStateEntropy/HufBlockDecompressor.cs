using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FiniteStateEntropy
{
    internal static class HufBlockDecompressor
    {
        private const int HUF_TABLELOG_MAX = 12;
        private const int HUF_TABLELOG_DEFAULT = 11;
        private const int HUF_SYMBOLVALUE_MAX = 255;

        private const int RankValColTypeSize = (HUF_TABLELOG_MAX + 12) * sizeof(uint);

        // `rankValOrigin` must be a table of at least (HUF_TABLELOG_MAX + 1) U32
        public static void FillDTableX2Level2(ref HUF_DEltX2 dTable, int sizeLog, int consumed, ReadOnlySpan<uint> rankValOrigin, int minWeight, ReadOnlySpan<sortedSymbol> sortedSymbols, int nbBitsBaseline, short baseSeq)
        {
            if (rankValOrigin.Length < HUF_TABLELOG_MAX + 1)
            {
                throw new ArgumentException("`rankValOrigin` must be a table of at least (HUF_TABLELOG_MAX + 1) U32", nameof(rankValOrigin));
            }

            Unsafe.SkipInit(out HUF_DEltX2 DELt);
            Span<uint> rankVal = stackalloc uint[HUF_TABLELOG_MAX + 1];

            // get pre-calculated rankVal
            rankValOrigin.CopyTo(rankVal);

            // fill skipped values
            if (minWeight > 1)
            {
                uint skipSize = rankVal[minWeight];
                DELt.sequence = BitConverter.IsLittleEndian ? (ushort)baseSeq : BinaryPrimitives.ReverseEndianness((ushort)baseSeq);
                DELt.nbBits = (byte)consumed;
                DELt.length = 1;
                for (int i = 0; i < skipSize; i++)
                {
                    Unsafe.Add(ref dTable, i) = DELt;
                }
            }

            // fill DTable
            for (int s = 0; s < sortedSymbols.Length; s++)
            {
                byte symbol = sortedSymbols[s].symbol;
                byte weight = sortedSymbols[s].weight;
                int nbBits = nbBitsBaseline - weight;
                int length = 1 << (sizeLog - nbBits);
                uint start = rankVal[weight];
                int i = (int)start;
                long end = start + length;

                DELt.sequence = BitConverter.IsLittleEndian ? (ushort)(baseSeq + (symbol << 8)) : BinaryPrimitives.ReverseEndianness((ushort)(baseSeq + (symbol << 8)));
                DELt.nbBits = (byte)(nbBits + consumed);
                DELt.length = 2;
                do
                {
                    Unsafe.Add(ref dTable, i++) = DELt;
                } while (i < end);
                rankVal[weight] += (uint)length;
            }
        }

        public static void FillDTableX2(ref HUF_DEltX2 dTable, int targetLog, ReadOnlySpan<sortedSymbol> sortedList, ReadOnlySpan<uint> rankStart, ReadOnlySpan<uint> rankValOrigin, int maxWeight, int nbBitsBaseline)
        {
            Span<uint> rankVal = stackalloc uint[HUF_TABLELOG_MAX + 1];
            int scaleLog = nbBitsBaseline - targetLog;
            int minBits = nbBitsBaseline - maxWeight;

            rankValOrigin.Slice(0, HUF_TABLELOG_MAX + 1).CopyTo(rankVal);

            for (int s = 0; s < sortedList.Length; s++)
            {
                short symbol = sortedList[s].symbol;
                int weight = sortedList[s].weight;
                int nbBits = nbBitsBaseline - weight;
                int start = (int)rankVal[weight];
                int length = 1 << (targetLog - nbBits);

                if ((targetLog - nbBits) >= minBits)
                {
                    // enough room for a second symbol
                    int minWeight = nbBits + scaleLog;
                    if (minWeight < 1)
                    {
                        minWeight = 1;
                    }
                    int sortedRank = (int)rankStart[minWeight];
                    FillDTableX2Level2(ref Unsafe.Add(ref dTable, start), targetLog - nbBits, nbBits, SliceRankVal(rankValOrigin, nbBits), minWeight, sortedList.Slice(sortedRank), nbBitsBaseline, symbol);
                }
                else
                {
                    Unsafe.SkipInit(out HUF_DEltX2 DElt);
                    DElt.sequence = BitConverter.IsLittleEndian ? (ushort)symbol : BinaryPrimitives.ReverseEndianness((ushort)symbol);
                    DElt.nbBits = (byte)nbBits;
                    DElt.length = 1;

                    int end = start + length;
                    for (int u = start; u < end; u++)
                    {
                        Unsafe.Add(ref dTable, u) = DElt;
                    }
                }

                rankVal[weight] += (uint)length;
            }

            static ReadOnlySpan<uint> SliceRankVal(ReadOnlySpan<uint> rankValOrigin, int nbBits)
                => rankValOrigin.Slice(nbBits * (HUF_TABLELOG_MAX + 1), HUF_TABLELOG_MAX + 1);
        }

        public static int ReadDTableX2Workspace(ref uint dTable, ReadOnlySpan<byte> source, Span<byte> workspace)
        {
            DTableDesc dtd = Unsafe.As<uint, DTableDesc>(ref dTable);
            ref uint dtRef = ref Unsafe.Add(ref dTable, 1);
            ref HUF_DEltX2 dt = ref Unsafe.As<uint, HUF_DEltX2>(ref dtRef);

            int maxTableLog = dtd.maxTableLog;
            workspace.Clear();

            int spaceUsed32 = 0;
            Span<uint> rankVal = MemoryMarshal.Cast<byte, uint>(workspace).Slice(spaceUsed32, (RankValColTypeSize * HUF_TABLELOG_MAX) >> 2);
            spaceUsed32 += (RankValColTypeSize * HUF_TABLELOG_MAX) >> 2;
            Span<int> rankStats = MemoryMarshal.Cast<byte, int>(workspace).Slice(spaceUsed32, HUF_TABLELOG_MAX + 1);
            spaceUsed32 += HUF_TABLELOG_MAX + 1;
            Span<uint> rankStart0 = MemoryMarshal.Cast<byte, uint>(workspace).Slice(spaceUsed32, HUF_TABLELOG_MAX + 2);
            spaceUsed32 += HUF_TABLELOG_MAX + 2;
            Span<sortedSymbol> sortedSymbol = MemoryMarshal.Cast<byte, sortedSymbol>(workspace).Slice((spaceUsed32 * sizeof(uint)) / Unsafe.SizeOf<sortedSymbol>());
            spaceUsed32 += HUF_ALIGN(Unsafe.SizeOf<sortedSymbol>() * (HUF_SYMBOLVALUE_MAX + 1), sizeof(uint)) >> 2;
            Span<byte> weightList = MemoryMarshal.AsBytes(MemoryMarshal.Cast<byte, uint>(workspace).Slice(spaceUsed32));
            spaceUsed32 += HUF_ALIGN(HUF_SYMBOLVALUE_MAX + 1, sizeof(uint)) >> 2;

            if ((spaceUsed32 << 2) > workspace.Length)
            {
                throw new InvalidOperationException();
            }

            var rankStart = rankStart0.Slice(1);

            Debug.Assert(Unsafe.SizeOf<HUF_DEltX2>() == Unsafe.SizeOf<uint>());
            if (maxTableLog > HUF_TABLELOG_MAX)
            {
                throw new InvalidDataException();
            }

            int iSize = EntropyCommon.HufReadStats(weightList.Slice(0, HUF_SYMBOLVALUE_MAX + 1), rankStats, source, out int nbSymbols, out int tableLog);

            // check result
            if (tableLog > maxTableLog)
            {
                throw new InvalidDataException();
            }

            // find maxWeight
            int maxW;
            for (maxW = tableLog; rankStats[maxW] == 0; maxW--) { }  /* necessarily finds a solution before 0 */

            // Get start index of each weight
            int sizeOfSort;
            {
                int nextRankStart = 0;
                for (int w = 0; w < maxW + 1; w++)
                {
                    int current = nextRankStart;
                    nextRankStart += rankStats[w];
                    rankStats[w] = current;
                }
                rankStart[0] = (uint)nextRankStart; // put all 0w symbols at the end of sorted list
                sizeOfSort = nextRankStart;
            }

            // sort symbols by weight
            {
                for (int s = 0; s < nbSymbols; s++)
                {
                    byte w = weightList[s];
                    int r = (int)rankStart[w]++;
                    sortedSymbol[r].symbol = (byte)s;
                    sortedSymbol[r].weight = w;
                }
                rankStart[0] = 0; // forget 0w symbols; this is beginning of weight(1)
            }

            //Build rankVal
            {
                ref uint rankVal0 = ref MemoryMarshal.GetReference(rankVal);
                {
                    int rescale = maxTableLog - tableLog - 1;   /* tableLog <= maxTableLog */
                    int nextRankVal = 0;
                    for (int w = 0; w < maxW + 1; w++)
                    {
                        int current = nextRankVal;
                        nextRankVal += rankStats[w] << (w + rescale);
                        Unsafe.Add(ref rankVal0, w) = (uint)current;
                    }
                }
                {
                    int minBits = tableLog + 1 - maxW;
                    for (int consumed = minBits; consumed < maxTableLog - minBits + 1; consumed++)
                    {
                        ref uint rankValRef = ref rankVal[consumed];
                        for (int w = 0; w < maxW+1; w++)
                        {
                            Unsafe.Add(ref rankValRef, w) = Unsafe.Add(ref rankVal0, w) >> consumed;
                        }
                    }
                }
            }

            FillDTableX2(ref dt, maxTableLog, sortedSymbol.Slice(0, sizeOfSort), rankStart, rankVal, maxW, tableLog + 1);

            dtd.tableLog = (byte)maxTableLog;
            dtd.tableType = 1;
            dTable = Unsafe.As<DTableDesc, uint>(ref dtd);
            return iSize;
        }

        private static int HUF_ALIGN_MASK(int x, int mask)
            => (x + (mask)) & ~(mask);

        private static int HUF_ALIGN(int x, int a)
            => HUF_ALIGN_MASK(x, a - 1);
    }
}
