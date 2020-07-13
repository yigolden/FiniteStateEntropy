using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FiniteStateEntropy
{
    internal unsafe struct XxHash32
    {
        private const uint Prime1 = 0x9E3779B1U;
        private const uint Prime2 = 0x85EBCA77U;
        private const uint Prime3 = 0xC2B2AE3DU;
        private const uint Prime4 = 0x27D4EB2FU;
        private const uint Prime5 = 0x165667B1U;

        private uint _acc1;
        private uint _acc2;
        private uint _acc3;
        private uint _acc4;

        private long _totalLength;
        private fixed byte _buffer[16];

        private int _bufferLength;
        private uint _seed;

        public static XxHash32 Initialize()
        {
            return Initialize(0);
        }

        public static XxHash32 Initialize(uint seed)
        {
            return new XxHash32
            {
                _acc1 = seed + Prime1 + Prime2,
                _acc2 = seed + Prime2,
                _acc3 = seed + 0,
                _acc4 = seed - Prime1,
                _totalLength = 0,
                _bufferLength = 0,
                _seed = seed,
            };
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            uint acc;

            if (_bufferLength != 0)
            {
                fixed (byte* pBuffer = _buffer)
                {
                    Span<byte> buffer = new Span<byte>(pBuffer, 16);
                    Span<byte> availableSpace = buffer.Slice(_bufferLength);

                    if ((_bufferLength + data.Length) < 16)
                    {
                        data.CopyTo(availableSpace);

                        _bufferLength += data.Length;
                        return;
                    }

                    data.Slice(0, 16 - _bufferLength).CopyTo(availableSpace);

                    acc = _acc1 + BinaryPrimitives.ReadUInt32LittleEndian(buffer) * Prime2;
                    acc = RotateLeft13(acc);
                    _acc1 = acc * Prime1;

                    acc = _acc2 + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4)) * Prime2;
                    acc = RotateLeft13(acc);
                    _acc2 = acc * Prime1;

                    acc = _acc3 + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8)) * Prime2;
                    acc = RotateLeft13(acc);
                    _acc3 = acc * Prime1;

                    acc = _acc4 + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12)) * Prime2;
                    acc = RotateLeft13(acc);
                    _acc4 = acc * Prime1;
                }

                data = data.Slice(16 - _bufferLength);
                _bufferLength = 0;
                _totalLength += 16;
            }

            while (data.Length >= 16)
            {
                acc = _acc1 + BinaryPrimitives.ReadUInt32LittleEndian(data) * Prime2;
                acc = RotateLeft13(acc);
                _acc1 = acc * Prime1;

                acc = _acc2 + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) * Prime2;
                acc = RotateLeft13(acc);
                _acc2 = acc * Prime1;

                acc = _acc3 + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)) * Prime2;
                acc = RotateLeft13(acc);
                _acc3 = acc * Prime1;

                acc = _acc4 + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)) * Prime2;
                acc = RotateLeft13(acc);
                _acc4 = acc * Prime1;

                data = data.Slice(16);
                _totalLength += 16;
            }

            if (!data.IsEmpty)
            {
                fixed (byte* pBuffer = _buffer)
                {
                    Span<byte> buffer = new Span<byte>(pBuffer, 16);
                    data.CopyTo(buffer);
                }

                _bufferLength = data.Length;
            }
        }


        public readonly uint GetFinalHash()
        {
            uint acc;
            if (_totalLength == 0)
            {
                acc = _seed + Prime5;
                acc = acc + (uint)_bufferLength;
            }
            else
            {
                acc = RotateLeft1(_acc1) + RotateLeft7(_acc2) + RotateLeft12(_acc3) + RotateLeft18(_acc4);
                acc = acc + (uint)(_totalLength + _bufferLength);
            }

            acc = CalculateRemaining(acc);
            return CalculateFinalMix(acc);
        }

        public readonly override int GetHashCode() => (int)GetFinalHash();

        private readonly uint CalculateRemaining(uint acc)
        {
            fixed (byte* pBuffer = _buffer)
            {
                Span<byte> remaining = new Span<byte>(pBuffer, _bufferLength);

                while (BinaryPrimitives.TryReadUInt32LittleEndian(remaining, out uint lane))
                {
                    acc = acc + lane * Prime3;
                    acc = RotateLeft17(acc) * Prime4;
                    remaining = remaining.Slice(4);
                }

                while (!remaining.IsEmpty)
                {
                    acc = acc + remaining[0] * Prime5;
                    acc = RotateLeft11(acc) * Prime1;
                    remaining = remaining.Slice(1);
                }

                return acc;
            }
        }

        private readonly uint CalculateFinalMix(uint acc)
        {
            acc = acc ^ (acc >> 15);
            acc = acc * Prime2;
            acc = acc ^ (acc >> 13);
            acc = acc * Prime3;
            acc = acc ^ (acc >> 16);
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft1(uint value) => (value << 1) | (value >> (32 - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft7(uint value) => (value << 7) | (value >> (32 - 7));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft11(uint value) => (value << 11) | (value >> (32 - 11));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft12(uint value) => (value << 12) | (value >> (32 - 12));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft13(uint value) => (value << 13) | (value >> (32 - 13));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft17(uint value) => (value << 17) | (value >> (32 - 17));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft18(uint value) => (value << 18) | (value >> (32 - 18));
    }
}
