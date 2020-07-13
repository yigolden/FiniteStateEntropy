using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FiniteStateEntropy
{
    internal ref struct FseBitWriter
    {
        private Span<byte> _destination;
        private int _bitPos;
        private ulong _bits;

        public int RemainingLength => _destination.Length;

        public FseBitWriter(Span<byte> destination)
        {
            _destination = destination;
            _bitPos = 0;
            _bits = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(uint value, int bitCount)
        {
            Debug.Assert(bitCount < 32);

            int bitPos = _bitPos;
            if ((bitPos + bitCount) > 64)
            {
                bitPos = FlushSlow();
            }

            _bitPos = bitPos + bitCount;
            _bits |= (value & GetBitMask(bitCount)) << bitPos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush()
        {
            if (_bitPos >= 32)
            {
                FlushSlow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int FlushSlow()
        {
            int bitPos = _bitPos;
            int nbBytes = bitPos / 8;

            if (_destination.Length >= 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_destination, _bits);
                _destination = _destination.Slice(nbBytes);
                bitPos = bitPos % 8;
                _bits = (_bits >> (nbBytes * 8)) & GetBitMask(bitPos);
                return _bitPos = bitPos;
            }

            if (_destination.Length >= 4)
            {
                nbBytes = Math.Max(nbBytes, 4);
                BinaryPrimitives.WriteUInt32LittleEndian(_destination, (uint)_bits);
                _destination = _destination.Slice(nbBytes);
                _bits >>= nbBytes * 8;
                return _bitPos = _bitPos - nbBytes * 8;
            }

            while (nbBytes-- > 0)
            {
                if (_destination.IsEmpty)
                {
                    throw new InvalidOperationException("Destination is too small.");
                }

                _destination[0] = (byte)_bits;
                _bits >>= 8;
                _bitPos -= 8;

                _destination = _destination.Slice(1);
            }

            return _bitPos;
        }

        public void FlushFinal()
        {
            int bitPos = FlushSlow();
            Debug.Assert(bitPos < 8);

            if (bitPos != 0)
            {
                if (_destination.IsEmpty)
                {
                    throw new InvalidOperationException("Destination is too small.");
                }

                _destination[0] = (byte)_bits;
                _bits >>= 8;
                _bitPos -= 8;

                _destination = _destination.Slice(1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetBitMask(int bitCount)
        {
            return bitCount == 64 ? ulong.MaxValue : (1ul << bitCount) - 1;
        }
    }
}
