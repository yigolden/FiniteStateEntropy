using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FiniteStateEntropy
{
    internal ref struct FseBitReader
    {
        private ReadOnlySpan<byte> _remaining;
        private int _bitCount;
        private ulong _bits;

        public FseBitReader(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                _remaining = buffer;
                _bitCount = 0;
                _bits = 0;
            }
            else
            {
                uint lastByte = buffer[buffer.Length - 1];
                _remaining = buffer.Slice(0, buffer.Length - 1);
                _bitCount = MathHelper.Log2(lastByte);
                _bits = lastByte;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int LoadBits()
        {
            Debug.Assert(_bitCount < 32);

            ReadOnlySpan<byte> remaining = _remaining;

            switch (remaining.Length)
            {
                case 0:
                    break;
                case 3:
                    _bits = (_bits << 24) | ((ulong)remaining[2] << 16) | ((ulong)remaining[1] << 8) | remaining[0];
                    _bitCount += 24;
                    _remaining = default;
                    break;
                case 2:
                    _bits = (_bits << 16) | ((ulong)remaining[1] << 8) | remaining[0];
                    _bitCount += 16;
                    _remaining = default;
                    break;
                case 1:
                    _bits = (_bits << 8) | remaining[0];
                    _bitCount += 8;
                    _remaining = default;
                    break;
                default:
                    _bits = (_bits << 32) | BinaryPrimitives.ReadUInt32LittleEndian(remaining.Slice(remaining.Length - 4));
                    _bitCount += 32;
                    _remaining = remaining.Slice(0, remaining.Length - 4);
                    break;
            }

            return _bitCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool PeekBits(int bitCount, out uint bits)
        {
            Debug.Assert(bitCount < 32);

            if (bitCount > _bitCount)
            {
                if (bitCount > LoadBits())
                {
                    bits = default;
                    return false;
                }
            }

            bits = (uint)((_bits >> (_bitCount - bitCount)) & (((uint)1 << bitCount) - 1));
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBits(int bitCount, out uint bits)
        {
            Debug.Assert(bitCount < 32);

            if (bitCount > _bitCount)
            {
                if (bitCount > LoadBits())
                {
                    bits = default;
                    return false;
                }
            }

            bits = (uint)((_bits >> (_bitCount - bitCount)) & (((uint)1 << bitCount) - 1));
            _bitCount -= bitCount;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int bitCount)
        {
            Debug.Assert(bitCount < 32);

            int availableBitCount = _bitCount;
            if (bitCount > availableBitCount)
            {
                return ReadBitsSlow(bitCount);
            }

            uint bits = (uint)((_bits >> (availableBitCount - bitCount)) & (((uint)1 << bitCount) - 1));
            _bitCount = availableBitCount - bitCount;
            return bits;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private uint ReadBitsSlow(int bitCount)
        {
            int availableBitCount = LoadBits();
            if (bitCount > availableBitCount)
            {
                return 0;
            }

            uint bits = (uint)((_bits >> (availableBitCount - bitCount)) & (((uint)1 << bitCount) - 1));
            _bitCount = availableBitCount - bitCount;
            return bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SkipBits(int bitCount)
        {
            Debug.Assert(bitCount < 32);

            if (bitCount > _bitCount)
            {
                if (bitCount > LoadBits())
                {
                    return false;
                }
            }

            _bitCount -= bitCount;
            return true;
        }
    }
}
