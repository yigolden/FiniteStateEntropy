using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace FiniteStateEntropy
{
    internal static class HufBlockDecompressor
    {
        private const int HUF_TABLELOG_MAX = 12;

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

        public static void HufFillDTableX2(ref HUF_DEltX2 dTable, int targetLog, ReadOnlySpan<sortedSymbol> sortedList, ReadOnlySpan<uint> rankStart, ReadOnlySpan<uint> rankValOrigin, int maxWeight, int nbBitsBaseline)
        {
            Span<uint> rankVal = stackalloc uint[HUF_TABLELOG_MAX + 1];
            int scaleLog = nbBitsBaseline - targetLog;
            int minBits = nbBitsBaseline - maxWeight;

            rankValOrigin.Slice(0, HUF_TABLELOG_MAX + 1).CopyTo(rankVal);

            for (int s = 0; s < sortedList.Length; s++)
            {
                ushort symbol = sortedList[s].symbol;
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
                    DElt.sequence = BitConverter.IsLittleEndian ? symbol : BinaryPrimitives.ReverseEndianness(symbol);
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
    }
}
