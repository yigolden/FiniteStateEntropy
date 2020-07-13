using System;

namespace FiniteStateEntropy
{
    internal ref struct FseCompressState
    {
        public long Value;
        public Span<ushort> StateTable;
        public Span<FseSymbolCompressionTransform> SymbolTT;
        public int StateLog;

        public FseCompressState(in FseCompressTable ct)
        {
            Value = 1 << ct.tableLog;
            StateTable = ct.nextStateNumber;
            SymbolTT = ct.symbolTT;
            StateLog = ct.tableLog;
        }

        public FseCompressState(in FseCompressTable ct, uint symbol) : this(in ct)
        {
            FseSymbolCompressionTransform symbolTT = SymbolTT[(int)symbol];
            uint nbBitsOut = (symbolTT.deltaNbBits + (1 << 15)) >> 16;
            uint value = (nbBitsOut << 16) - symbolTT.deltaNbBits;
            Value = StateTable[(int)(value >> (int)nbBitsOut) + symbolTT.deltaFindState];
        }

        public void EncodeSymbol(ref FseBitWriter writer, uint symbol)
        {
            FseSymbolCompressionTransform symbolTT = SymbolTT[(int)symbol];
            long value = Value;
            int nbBitsOut = (int)((value + symbolTT.deltaNbBits) >> 16);
            writer.WriteBits((uint)value, nbBitsOut);
            Value = StateTable[(int)(value >> nbBitsOut) + symbolTT.deltaFindState];
        }

        public void Flush(ref FseBitWriter writer)
        {
            writer.WriteBits((uint)Value, StateLog);
            writer.Flush();
        }
    }
}
