using System;
using System.Buffers;
using System.Buffers.Binary;

namespace FiniteStateEntropy
{
    public sealed class FsePipeDecompressor
    {
        private readonly IBufferWriter<byte> _writer;
        private FseDecompressorState _state;
        private bool _validateChecksum;
        private XxHash32 _hash;

        private DecodingNextState _nextState;

        private DecodingAlgorithm _algorithm;
        private int _blockSize;
        private int _blockHeader;
        private int _rawSize;
        private int _compressedSize;

        public FsePipeDecompressor(IBufferWriter<byte> writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            Reset();
        }

        public FseDecompressorState State => _state;
        public bool ValidateChecksum { get => _validateChecksum; set => _validateChecksum = value; }

        private void Reset()
        {
            _state = FseDecompressorState.NeedInput;

            _nextState = DecodingNextState.FileHeader;

            _algorithm = default;
            _blockSize = default;
            _blockHeader = default;
            _rawSize = default;
        }

        public void Process(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (_state == FseDecompressorState.InvalidData || _state == FseDecompressorState.InvalidChecksum || _state == FseDecompressorState.WriteOutput)
            {
                throw new InvalidOperationException();
            }
            if (_state == FseDecompressorState.Completed)
            {
                Reset();
            }

            if (buffer.IsEmpty)
            {
                examined = consumed = buffer.Start;
                return;
            }

            while (true)
            {
                ProcessInput(buffer, out consumed, out examined);

                if (_state != FseDecompressorState.NeedInput)
                {
                    return;
                }

                if (!consumed.Equals(buffer.Start) && !examined.Equals(buffer.End))
                {
                    buffer = buffer.Slice(consumed);
                }
                else
                {
                    return;
                }
            }
        }

        public void NotifyFlushCompleted()
        {
            if (_state == FseDecompressorState.WriteOutput)
            {
                _state = FseDecompressorState.NeedInput;
                return;
            }

            throw new InvalidOperationException();
        }

        private void ProcessInput(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            switch (_nextState)
            {
                case DecodingNextState.FileHeader:
                    ProcessFileHeader(buffer, out consumed, out examined);
                    break;
                case DecodingNextState.BlockType:
                    ProcessBlockType(buffer, out consumed, out examined);
                    break;
                case DecodingNextState.ReadRawSize:
                    ProcessReadRawSize(buffer, out consumed, out examined);
                    break;
                case DecodingNextState.ReadCompressedSize:
                    ProcessReadCompresssedSize(buffer, out consumed, out examined);
                    break;
                case DecodingNextState.BlockContent:
                    ProcessBlockContent(buffer, out consumed, out examined);
                    break;
                case DecodingNextState.CrcBlock:
                    ProcessCrcBlock(buffer, out consumed, out examined);
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        private void ProcessFileHeader(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (buffer.Length < 5)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            Span<byte> stackBuffer = stackalloc byte[5];
            buffer = buffer.Slice(0, 5);
            buffer.CopyTo(stackBuffer);
            examined = consumed = buffer.End;

            var algorithm = (DecodingAlgorithm)BinaryPrimitives.ReadInt32LittleEndian(stackBuffer);
            int blockSizeId = stackBuffer[4];

            if (algorithm != DecodingAlgorithm.Fse && algorithm != DecodingAlgorithm.Huff && algorithm != DecodingAlgorithm.Zlib)
            {
                _state = FseDecompressorState.InvalidData;
                return;
            }
            if ((uint)blockSizeId > 6)
            {
                _state = FseDecompressorState.InvalidData;
                return;
            }

            // Only FSE is supported
            if (algorithm != DecodingAlgorithm.Fse)
            {
                _state = FseDecompressorState.InvalidData;
                return;
            }

            _algorithm = algorithm;
            _blockSize = (1 << blockSizeId) * (1 << 10); // KB

            _hash = XxHash32.Initialize();

            _nextState = DecodingNextState.BlockType;
        }

        private void ProcessBlockType(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (buffer.IsEmpty)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            buffer = buffer.Slice(0, 1);
            _blockHeader = buffer.GetFirstSpan()[0];
            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);
            examined = consumed = buffer.End;

            if (blockType == FseFileBlockType.Crc)
            {
                _nextState = DecodingNextState.CrcBlock;
                return;
            }

            int fullBlock = _blockHeader & 0b100000;
            if (fullBlock == 0)
            {
                _nextState = DecodingNextState.ReadRawSize;
                return;
            }

            switch (blockType)
            {
                case FseFileBlockType.Compressed:
                    _rawSize = _blockSize;
                    _nextState = DecodingNextState.ReadCompressedSize;
                    return;
                case FseFileBlockType.Raw:
                    _rawSize = _blockSize;
                    _compressedSize = _blockSize;
                    _nextState = DecodingNextState.BlockContent;
                    break;
                case FseFileBlockType.Rle:
                    _rawSize = _blockSize;
                    _compressedSize = 1;
                    _nextState = DecodingNextState.BlockContent;
                    break;
            }
        }

        private void ProcessReadRawSize(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (buffer.Length < 2)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            Span<byte> stackBuffer = stackalloc byte[2];
            buffer = buffer.Slice(0, 2);
            buffer.CopyTo(stackBuffer);
            examined = consumed = buffer.End;

            _rawSize = BinaryPrimitives.ReadInt16BigEndian(stackBuffer);

            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);
            switch (blockType)
            {
                case FseFileBlockType.Compressed:
                    _nextState = DecodingNextState.ReadCompressedSize;
                    return;
                case FseFileBlockType.Raw:
                    _compressedSize = _rawSize;
                    _nextState = DecodingNextState.BlockContent;
                    break;
                case FseFileBlockType.Rle:
                    _compressedSize = 1;
                    _nextState = DecodingNextState.BlockContent;
                    break;
            }
        }

        private void ProcessReadCompresssedSize(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (buffer.Length < 2)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            Span<byte> stackBuffer = stackalloc byte[2];
            buffer = buffer.Slice(0, 2);
            buffer.CopyTo(stackBuffer);
            examined = consumed = buffer.End;

            _compressedSize = BinaryPrimitives.ReadInt16BigEndian(stackBuffer);

            _nextState = DecodingNextState.BlockContent;
        }

        private void ProcessBlockContent(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);

            int compressedSize = blockType == FseFileBlockType.Rle ? 1 : _compressedSize;

            if (buffer.Length < compressedSize)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            buffer = buffer.Slice(0, compressedSize);
            examined = consumed = buffer.End;

            byte[]? bufferRent = null;
            ReadOnlySpan<byte> compressed = buffer.GetFirstSpan();
            if (compressed.Length < compressedSize)
            {
                bufferRent = ArrayPool<byte>.Shared.Rent(compressedSize);
                buffer.CopyTo(bufferRent);
                compressed = bufferRent.AsSpan(0, compressedSize);
            }

            int rawSize = _rawSize;
            Span<byte> destination = _writer.GetSpan(rawSize);
            destination = destination.Slice(0, rawSize);

            try
            {
                switch (blockType)
                {
                    case FseFileBlockType.Compressed:
                        int bytesWritten = 0;
                        if (_algorithm == DecodingAlgorithm.Fse)
                        {
                            bytesWritten = FseBlockDecompressor.Decompress(compressed, destination);
                        }
                        if (_validateChecksum)
                        {
                            _hash.Update(destination.Slice(0, bytesWritten));
                        }
                        _writer.Advance(bytesWritten);
                        break;
                    case FseFileBlockType.Raw:
                        compressed.CopyTo(destination);
                        if (_validateChecksum)
                        {
                            _hash.Update(destination);
                        }
                        _writer.Advance(rawSize);
                        break;
                    case FseFileBlockType.Rle:
                        destination.Fill(compressed[0]);
                        if (_validateChecksum)
                        {
                            _hash.Update(destination);
                        }
                        _writer.Advance(rawSize);
                        break;
                }

                _nextState = DecodingNextState.BlockType;

                _state = FseDecompressorState.WriteOutput;
            }
            finally
            {
                if (!(bufferRent is null))
                {
                    ArrayPool<byte>.Shared.Return(bufferRent);
                }
            }
        }

        private void ProcessCrcBlock(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (buffer.Length < 2)
            {
                consumed = buffer.Start;
                examined = buffer.End;
                return;
            }

            Span<byte> stackBuffer = stackalloc byte[2];
            buffer = buffer.Slice(0, 2);
            buffer.CopyTo(stackBuffer);
            examined = consumed = buffer.End;

            if (_validateChecksum)
            {
                uint crc = BinaryPrimitives.ReadUInt16BigEndian(stackBuffer);
                crc += ((uint)_blockHeader & 0x3f) << 16;

                uint calced = (_hash.GetFinalHash() >> 5) & ((1U << 22) - 1);
                if (crc != calced)
                {
                    _state = FseDecompressorState.InvalidChecksum;
                    return;
                }
            }

            _state = FseDecompressorState.Completed;
        }

        private enum DecodingNextState
        {
            FileHeader,
            BlockType,
            ReadRawSize,
            ReadCompressedSize,
            BlockContent,
            CrcBlock,
        }

        private enum DecodingAlgorithm
        {
            Fse = 0x183E2309,
            Huff = 0x183E3309,
            Zlib = 0x183E4309,
        }
    }
}
