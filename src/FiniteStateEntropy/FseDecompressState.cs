using System;
using System.Runtime.CompilerServices;

namespace FiniteStateEntropy
{
    internal ref struct FseDecompressState
    {
        public ReadOnlySpan<FseDecompressTable> Table;
        public int State;

        public static FseDecompressState Initialize(ref FseBitReader reader, FseDecompressTableHeader header, ReadOnlySpan<FseDecompressTable> dt)
        {
            FseDecompressState fse = default;
            fse.Table = dt;
            fse.State = (int)reader.ReadBits(header.TableLog);
            return fse;
        }

        public void UpdateState(ref FseBitReader reader)
        {
            FseDecompressTable dInfo = Table[State];
            uint lowBits = reader.ReadBits(dInfo.NumberOfBits);
            State = (int)(dInfo.NewState + lowBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte DecodeSymbol(ref FseBitReader reader)
        {
            FseDecompressTable dInfo = Table[State];
            uint lowBits = reader.ReadBits(dInfo.NumberOfBits);
            State = (int)(dInfo.NewState + lowBits);
            return dInfo.Symbol;
        }

        public void EndOfState()
        {
            State = 0;
        }
    }
}
