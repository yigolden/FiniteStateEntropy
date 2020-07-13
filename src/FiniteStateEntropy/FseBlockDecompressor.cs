using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FiniteStateEntropy
{
    internal static class FseBlockDecompressor
    {
        private const int FSE_MAX_MEMORY_USAGE = 14;
        private const int FSE_DEFAULT_MEMORY_USAGE = 13;
        private const int FSE_MAX_SYMBOL_VALUE = 256;

        private const int FSE_MAX_TABLELOG = (FSE_MAX_MEMORY_USAGE - 2);
        private const int FSE_MAX_TABLESIZE = (1 << FSE_MAX_TABLELOG);
        private const int FSE_MAXTABLESIZE_MASK = (FSE_MAX_TABLESIZE - 1);
        private const int FSE_DEFAULT_TABLELOG = (FSE_DEFAULT_MEMORY_USAGE - 2);
        private const int FSE_MIN_TABLELOG = 5;

        private const int FSE_TABLELOG_ABSOLUTE_MAX = 15;

        private const int FSE_DTABLE_MAX_SIZE_U32 = (1 + (1 << FSE_MAX_TABLELOG));

        private static int GetTableStep(int tableSize) => ((tableSize >> 1) + (tableSize >> 3) + 3);

        public static bool BuildDecodeTable(ReadOnlySpan<short> normalizedCounter, int maxSymbolValue, int tableLog, Span<FseDecompressTable> decodeTable, out FseDecompressTableHeader header)
        {
            //Span<short> symbolNext = stackalloc short[FSE_MAX_SYMBOL_VALUE + 1];
            byte[] stackBuffer = ArrayPool<byte>.Shared.Rent(2 * (FSE_MAX_SYMBOL_VALUE + 1));
            try
            {
                Span<short> symbolNext = MemoryMarshal.Cast<byte, short>(stackBuffer.AsSpan()).Slice(0, FSE_MAX_SYMBOL_VALUE + 1);

                int maxSV1 = maxSymbolValue + 1;
                int tableSize = 1 << tableLog;
                int highThreshold = tableSize - 1;

                if (maxSymbolValue > FSE_MAX_SYMBOL_VALUE)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxSymbolValue));
                }
                if (tableLog > FSE_MAX_TABLELOG)
                {
                    throw new ArgumentOutOfRangeException(nameof(tableLog));
                }

                // Init, lay down lowprob symbols
                {
                    header = default;
                    header.TableLog = (short)tableLog;
                    //header.FastMode = 1;

                    short largeLimit = (short)(1 << (tableLog - 1));
                    for (int s = 0; s < maxSV1; s++)
                    {
                        if (normalizedCounter[s] == -1)
                        {
                            decodeTable[highThreshold--].Symbol = (byte)s;
                            symbolNext[s] = 1;
                        }
                        else
                        {
                            if (normalizedCounter[s] >= largeLimit)
                            {
                                //header.FastMode = 0;
                            }
                            symbolNext[s] = normalizedCounter[s];
                        }
                    }

                }

                /* Spread symbols */
                {
                    int tableMask = tableSize - 1;
                    int step = GetTableStep(tableSize);
                    int position = 0;
                    for (int s = 0; s < maxSV1; s++)
                    {
                        for (int i = 0; i < normalizedCounter[s]; i++)
                        {
                            decodeTable[position].Symbol = (byte)s;
                            position = (position + step) & tableMask;
                            while (position > highThreshold)
                            {
                                position = (position + step) & tableMask;   // lowprob area
                            }
                        }
                    }
                    if (position != 0)
                    {
                        return false;
                    }
                }

                /* Build Decoding table */
                {
                    for (int u = 0; u < tableSize; u++)
                    {
                        byte symbol = decodeTable[u].Symbol;
                        int nextState = symbolNext[symbol]++;
                        decodeTable[u].NumberOfBits = (byte)(tableLog - MathHelper.Log2((uint)nextState));
                        decodeTable[u].NewState = (ushort)((nextState << decodeTable[u].NumberOfBits) - tableSize);
                    }
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stackBuffer);
            }
        }

        public static int DecompressUsingTable(ReadOnlySpan<byte> source, Span<byte> destination, FseDecompressTableHeader header, ReadOnlySpan<FseDecompressTable> decodeTable)
        {
            int destinationLength = destination.Length;
            var reader = new FseBitReader(source);

            var state1 = FseDecompressState.Initialize(ref reader, header, decodeTable);
            var state2 = FseDecompressState.Initialize(ref reader, header, decodeTable);

            while (destination.Length >= 16)
            {
                ref byte destinationRef = ref MemoryMarshal.GetReference(destination);

                destinationRef = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 1) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 2) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 3) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 4) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 5) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 6) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 7) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 8) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 9) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 10) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 11) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 12) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 13) = state2.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 14) = state1.DecodeSymbol(ref reader);
                Unsafe.Add(ref destinationRef, 15) = state2.DecodeSymbol(ref reader);

                destination = destination.Slice(16);
            }

            while (!destination.IsEmpty)
            {
                destination[0] = state1.DecodeSymbol(ref reader);

                destination = destination.Slice(1);

                FseDecompressState temp = state1;
                state1 = state2;
                state2 = temp;
            }

            return destinationLength - destination.Length;
        }

        public static int DecompressWorkspace(ReadOnlySpan<byte> source, Span<byte> destination, Span<FseDecompressTable> workSpace, int maxLog)
        {
            FseDecompressTableHeader header;

            byte[] stackBuffer = ArrayPool<byte>.Shared.Rent(2 * (FSE_MAX_SYMBOL_VALUE + 1));
            try
            {
                Span<short> counting = MemoryMarshal.Cast<byte, short>(stackBuffer.AsSpan()).Slice(0, FSE_MAX_SYMBOL_VALUE + 1);

                int maxSymbolValue = FSE_MAX_SYMBOL_VALUE;

                if (!EntropyCommon.FseReadNCount(source, counting, ref maxSymbolValue, out int tableLog, out int nCountLength))
                {
                    throw new InvalidDataException();
                }
                if (tableLog > maxLog)
                {
                    throw new InvalidDataException();
                }
                source = source.Slice(nCountLength);

                if (!BuildDecodeTable(counting, maxSymbolValue, tableLog, workSpace, out header))
                {
                    throw new InvalidDataException();
                }

            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stackBuffer);
            }

            return DecompressUsingTable(source, destination, header, workSpace);
        }

        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FSE_DTABLE_MAX_SIZE_U32 * Unsafe.SizeOf<FseDecompressTable>());
            try
            {
                Span<FseDecompressTable> dt = MemoryMarshal.Cast<byte, FseDecompressTable>(buffer.AsSpan()).Slice(0, FSE_DTABLE_MAX_SIZE_U32);
                return DecompressWorkspace(source, destination, dt, FSE_MAX_TABLELOG);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    }
}
