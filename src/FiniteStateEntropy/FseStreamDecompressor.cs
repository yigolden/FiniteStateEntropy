using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace FiniteStateEntropy
{
    public sealed class FseStreamDecompressor : IDisposable
    {
        private FseDecompressorState _state;
        private bool _validateChecksum;
        private XxHash32 _hash;

        private byte[]? _inputBuffer;
        private int _bytesConsumed;
        private int _bytesRead;
        private int _bytesDesired;

        private DecompressingNextState _nextState;

        private FseCompressingAlgorithm _algorithm;
        private int _blockSize;
        private int _blockHeader;
        private int _rawSize;
        private int _compressedSize;

        private byte[]? _outputBuffer;
        private int _bytesGenerated;


        public FseStreamDecompressor()
        {
            Reset(true);
        }

        public FseDecompressorState State => _state;
        public bool ValidateChecksum { get => _validateChecksum; set => _validateChecksum = value; }

        public void Reset()
        {
            Reset(true);
        }

        private void Reset(bool resetBuffer)
        {
            _state = FseDecompressorState.NeedInput;

            if (resetBuffer)
            {
                AllocateInputArray(64);
            }

            _bytesConsumed = 0;
            _bytesRead = 0;
            _bytesDesired = 5;

            _nextState = DecompressingNextState.FileHeader;

            _algorithm = default;
            _blockSize = default;
            _blockHeader = default;
            _rawSize = default;

            if (resetBuffer)
            {
                AllocateOutputArray(64);
            }
            _bytesGenerated = 0;
        }

        private void AllocateInputArray(int sizeHint)
        {
            if (_inputBuffer is null)
            {
                _inputBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
                return;
            }
            if ((_inputBuffer.Length - _bytesRead) >= sizeHint)
            {
                return;
            }
            int remainingBytes = _bytesRead - _bytesConsumed;
            if ((_inputBuffer.Length - remainingBytes) >= sizeHint)
            {
                if (_bytesConsumed != 0)
                {
                    _inputBuffer.AsSpan(_bytesConsumed, remainingBytes).CopyTo(_inputBuffer);
                    _bytesConsumed = 0;
                    _bytesRead = remainingBytes;
                }
                return;
            }

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(sizeHint + remainingBytes);
            _inputBuffer.AsSpan(_bytesConsumed, remainingBytes).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_inputBuffer);
            _inputBuffer = newBuffer;
            _bytesConsumed = 0;
            _bytesRead = remainingBytes;
        }

        private void AllocateOutputArray(int sizeHint)
        {
            if (_outputBuffer is null)
            {
                _outputBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
                return;
            }
            if (sizeHint > (_outputBuffer.Length - _bytesGenerated))
            {
                byte[] newBuffer = ArrayPool<byte>.Shared.Rent(sizeHint + _bytesGenerated);
                _outputBuffer.AsSpan(0, _bytesGenerated).CopyTo(newBuffer);
                ArrayPool<byte>.Shared.Return(_outputBuffer);
                _outputBuffer = newBuffer;
            }
        }

        public int SetInput(ReadOnlySpan<byte> input)
        {
            if (_state == FseDecompressorState.InvalidData || _state == FseDecompressorState.InvalidChecksum)
            {
                throw new InvalidOperationException();
            }
            if (_state == FseDecompressorState.Completed)
            {
                Reset(false);
            }

            int bytesConsumed = 0;
            while (_state == FseDecompressorState.NeedInput)
            {
                if (input.Length < _bytesDesired)
                {
                    input.CopyTo(_inputBuffer.AsSpan(_bytesRead));
                    _bytesRead += input.Length;
                    _bytesDesired -= input.Length;
                    bytesConsumed += input.Length;
                    return bytesConsumed;
                }

                if (_bytesDesired > 0)
                {
                    input.Slice(0, _bytesDesired).CopyTo(_inputBuffer.AsSpan(_bytesRead));
                    input = input.Slice(_bytesDesired);
                    _bytesRead += _bytesDesired;
                    bytesConsumed += _bytesDesired;
                    _bytesDesired = 0;

                    ProcessInput();
                }
            }

            return bytesConsumed;
        }

        private void ProcessInput()
        {
            while (_state == FseDecompressorState.NeedInput && _bytesDesired == 0)
            {
                switch (_nextState)
                {
                    case DecompressingNextState.FileHeader:
                        ProcessFileHeader();
                        break;
                    case DecompressingNextState.BlockType:
                        ProcessBlockType();
                        break;
                    case DecompressingNextState.ReadRawSize:
                        ProcessReadRawSize();
                        break;
                    case DecompressingNextState.ReadCompressedSize:
                        ProcessReadCompresssedSize();
                        break;
                    case DecompressingNextState.BlockContent:
                        ProcessBlockContent();
                        break;
                    case DecompressingNextState.CrcBlock:
                        ProcessCrcBlock();
                        break;
                }
            }
        }

        private void MoveToNextState(DecompressingNextState state, int size)
        {
            _nextState = state;
            AllocateInputArray(Math.Max(size, 64));
            _bytesDesired = Math.Max(size - (_bytesRead - _bytesConsumed), 0);
        }

        private void ProcessFileHeader()
        {
            Span<byte> header = _inputBuffer.AsSpan(_bytesConsumed, 5);

            var algorithm = (FseCompressingAlgorithm)BinaryPrimitives.ReadInt32LittleEndian(header);
            int blockSizeId = header[4];
            _bytesConsumed += 5;

            if (algorithm != FseCompressingAlgorithm.Fse && algorithm != FseCompressingAlgorithm.Huff && algorithm != FseCompressingAlgorithm.Zlib)
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
            if (algorithm != FseCompressingAlgorithm.Fse)
            {
                _state = FseDecompressorState.InvalidData;
                return;
            }

            _algorithm = algorithm;
            _blockSize = (1 << blockSizeId) * (1 << 10); // KB

            _hash = XxHash32.Initialize();

            MoveToNextState(DecompressingNextState.BlockType, 1);
        }

        private void ProcessBlockType()
        {
            Debug.Assert(_inputBuffer != null);

            _blockHeader = _inputBuffer![_bytesConsumed];
            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);
            _bytesConsumed += 1;

            if (blockType == FseFileBlockType.Crc)
            {
                MoveToNextState(DecompressingNextState.CrcBlock, 2);
                return;
            }

            int fullBlock = _blockHeader & 0b100000;
            if (fullBlock == 0)
            {
                MoveToNextState(DecompressingNextState.ReadRawSize, 2);
                return;
            }

            switch (blockType)
            {
                case FseFileBlockType.Compressed:
                    _rawSize = _blockSize;
                    MoveToNextState(DecompressingNextState.ReadCompressedSize, 2);
                    return;
                case FseFileBlockType.Raw:
                    _rawSize = _blockSize;
                    _compressedSize = _blockSize;
                    MoveToNextState(DecompressingNextState.BlockContent, _compressedSize);
                    break;
                case FseFileBlockType.Rle:
                    _rawSize = _blockSize;
                    _compressedSize = 1;
                    MoveToNextState(DecompressingNextState.BlockContent, 1);
                    break;
            }
        }

        private void ProcessReadRawSize()
        {
            Debug.Assert(_inputBuffer != null);

            _rawSize = BinaryPrimitives.ReadInt16BigEndian(_inputBuffer.AsSpan(_bytesConsumed));
            _bytesConsumed += 2;

            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);
            switch (blockType)
            {
                case FseFileBlockType.Compressed:
                    MoveToNextState(DecompressingNextState.ReadCompressedSize, 2);
                    return;
                case FseFileBlockType.Raw:
                    _compressedSize = _rawSize;
                    MoveToNextState(DecompressingNextState.BlockContent, _compressedSize);
                    break;
                case FseFileBlockType.Rle:
                    _compressedSize = 1;
                    MoveToNextState(DecompressingNextState.BlockContent, 1);
                    break;
            }
        }

        private void ProcessReadCompresssedSize()
        {
            _compressedSize = BinaryPrimitives.ReadInt16BigEndian(_inputBuffer.AsSpan(_bytesConsumed));
            _bytesConsumed += 2;

            MoveToNextState(DecompressingNextState.BlockContent, _compressedSize);
        }

        private void ProcessBlockContent()
        {
            Debug.Assert(_inputBuffer != null);

            AllocateOutputArray(_rawSize);

            FseFileBlockType blockType = (FseFileBlockType)((_blockHeader & (0b11000000)) >> 6);
            switch (blockType)
            {
                case FseFileBlockType.Compressed:
                    int bytesWritten = 0;
                    if (_algorithm == FseCompressingAlgorithm.Fse)
                    {
                        bytesWritten = FseBlockDecompressor.Decompress(_inputBuffer.AsSpan(_bytesConsumed, _compressedSize), _outputBuffer.AsSpan(_bytesGenerated, _rawSize));
                    }
                    if (_validateChecksum)
                    {
                        _hash.Update(_outputBuffer.AsSpan(_bytesGenerated, bytesWritten));
                    }
                    _bytesGenerated += bytesWritten;
                    break;
                case FseFileBlockType.Raw:
                    _inputBuffer.AsSpan(_bytesConsumed, _compressedSize).CopyTo(_outputBuffer.AsSpan(_bytesGenerated, _rawSize));
                    if (_validateChecksum)
                    {
                        _hash.Update(_outputBuffer.AsSpan(_bytesGenerated, _rawSize));
                    }
                    _bytesGenerated += _rawSize;
                    break;
                case FseFileBlockType.Rle:
                    _outputBuffer.AsSpan(_bytesGenerated, _rawSize).Fill(_inputBuffer![_bytesConsumed]);
                    if (_validateChecksum)
                    {
                        _hash.Update(_outputBuffer.AsSpan(_bytesGenerated, _rawSize));
                    }
                    _bytesGenerated += _rawSize;
                    break;
            }

            _bytesConsumed += _compressedSize;

            MoveToNextState(DecompressingNextState.BlockType, 1);
            _state = FseDecompressorState.WriteOutput;
        }

        private void ProcessCrcBlock()
        {
            if (_validateChecksum)
            {
                uint crc = BinaryPrimitives.ReadUInt16BigEndian(_inputBuffer.AsSpan(_bytesConsumed));
                crc += ((uint)_blockHeader & 0x3f) << 16;

                uint calced = (_hash.GetFinalHash() >> 5) & ((1U << 22) - 1);
                if (crc != calced)
                {
                    _bytesConsumed += 2;
                    _state = FseDecompressorState.InvalidChecksum;
                    return;
                }
            }

            _bytesConsumed += 2;
            _state = FseDecompressorState.Completed;
        }

        public int WriteOutput(Span<byte> buffer)
        {
            int bytesToWrite = Math.Min(_bytesGenerated, buffer.Length);
            if (bytesToWrite != 0)
            {
                _outputBuffer.AsSpan(0, bytesToWrite).CopyTo(buffer);
                _outputBuffer.AsSpan(bytesToWrite, _bytesGenerated - bytesToWrite).CopyTo(_outputBuffer);
                _bytesGenerated -= bytesToWrite;
            }

            if (_bytesGenerated == 0 && _state == FseDecompressorState.WriteOutput)
            {
                _state = FseDecompressorState.NeedInput;
                ProcessInput();
            }

            return bytesToWrite;
        }

        public ArraySegment<byte> Output => _outputBuffer is null ? default : new ArraySegment<byte>(_outputBuffer, 0, _bytesGenerated);

        public void Advance(int size)
        {
            if ((uint)size > (uint)_bytesGenerated)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }
            if (size == _bytesGenerated)
            {
                _bytesGenerated = 0;
            }
            else
            {
                _outputBuffer.AsSpan(size, _bytesGenerated - size).CopyTo(_outputBuffer);
                _bytesGenerated -= size;
            }
        }

        private enum DecompressingNextState
        {
            FileHeader,
            BlockType,
            ReadRawSize,
            ReadCompressedSize,
            BlockContent,
            CrcBlock,
        }

        public void Dispose()
        {
            if (!(_inputBuffer is null))
            {
                ArrayPool<byte>.Shared.Return(_inputBuffer);
                _inputBuffer = null;
            }
            if (!(_outputBuffer is null))
            {
                ArrayPool<byte>.Shared.Return(_outputBuffer);
                _inputBuffer = null;
            }
            Reset(false);
        }
    }
}
