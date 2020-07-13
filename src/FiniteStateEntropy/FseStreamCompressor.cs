using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace FiniteStateEntropy
{
    public sealed class FseStreamCompressor : IDisposable
    {
        private int _blockSizeId;
        private int _inputBufferSize;
        private int _outputBufferBound;

        private FseCompressorState _state;
        private bool _headerWritten;
        private XxHash32 _hash;

        private byte[]? _inputBuffer;
        private int _bytesRead;

        private byte[]? _outputBuffer;
        private int _outputOffset;
        private int _bytesGenerated;

        public FseStreamCompressor()
        {
            _blockSizeId = 5;
            _inputBufferSize = FioBlockIdToBlockSize(_blockSizeId);
            _outputBufferBound = FseCompressBound(_inputBufferSize);

            Reset();
        }

        public FseCompressorState State => _state;

        public ArraySegment<byte> Output => _outputBuffer is null ? default : new ArraySegment<byte>(_outputBuffer, _outputOffset, _bytesGenerated);

        public void Reset()
        {
            _state = FseCompressorState.NeedInput;
            _headerWritten = false;
            _hash = XxHash32.Initialize();

            _bytesRead = 0;
            _outputOffset = 0;
            _bytesGenerated = 0;
        }

        private void EnsureInputBufferAllocated()
        {
            if (_inputBuffer is null)
            {
                _inputBuffer = ArrayPool<byte>.Shared.Rent(_inputBufferSize);
            }
            Debug.Assert(_inputBuffer.Length >= _inputBufferSize);
        }

        private void EnsureOutputBufferAllocated()
        {
            if (_outputBuffer is null)
            {
                _outputBuffer = ArrayPool<byte>.Shared.Rent(FseCompressBound(_inputBufferSize) + 5 + 5 + 3); // maxFileHeader + maxBlockHeader + endOfFileBlock
            }
            Debug.Assert(_outputBuffer.Length >= (FseCompressBound(_inputBufferSize) + 5 + 5 + 3));
        }

        private static int FioBlockIdToBlockSize(int id) => (1 << id) * (1 << 10); // KB

        private static int FseCompressBound(int size) => 512 + (size + (size >> 7));

        public int SetInput(ReadOnlySpan<byte> input)
        {
            EnsureInputBufferAllocated();

            if (_state == FseCompressorState.WriteOutput)
            {
                throw new InvalidOperationException();
            }
            if (_state == FseCompressorState.Completed)
            {
                Reset();
            }
            Debug.Assert(_state == FseCompressorState.NeedInput);

            Span<byte> inputBuffer = _inputBuffer.AsSpan(_bytesRead, _inputBufferSize - _bytesRead);

            int length = Math.Min(input.Length, inputBuffer.Length);
            input.Slice(0, length).CopyTo(inputBuffer);
            inputBuffer = inputBuffer.Slice(length);
            _bytesRead += length;

            if (inputBuffer.IsEmpty)
            {
                Compress();
            }

            return length;
        }

        private void Compress()
        {
            EnsureOutputBufferAllocated();

            Debug.Assert(_state == FseCompressorState.NeedInput);
            Debug.Assert(_bytesGenerated == 0);

            int inSize = _bytesRead;
            Span<byte> inputBuffer = _inputBuffer.AsSpan(0, inSize);
            Span<byte> outputBuffer = _outputBuffer.AsSpan(0, _outputBufferBound);

            int headerSize;
            if (inSize == 0)
            {
                headerSize = 0;
            }
            else
            {
                _hash.Update(inputBuffer);
                int cSize = FseBlockCompressor.Compress(outputBuffer.Slice(10), inputBuffer);
                if (cSize == 0)
                {
                    // raw
                    if (inSize == _inputBufferSize)
                    {
                        outputBuffer[9] = (0b01 << 6) + 0x20;
                        headerSize = 1;
                    }
                    else
                    {
                        outputBuffer[7] = (byte)(0b01 << 6);
                        outputBuffer[8] = (byte)(inSize >> 8);
                        outputBuffer[9] = (byte)inSize;
                        headerSize = 3;
                    }
                    inputBuffer.CopyTo(outputBuffer.Slice(10));
                    _bytesGenerated = inSize;
                }
                else if (cSize == 1)
                {
                    // rle
                    if (inSize == _inputBufferSize)
                    {
                        outputBuffer[9] = (0b10 << 6) + 0x20;
                        headerSize = 1;
                    }
                    else
                    {
                        outputBuffer[7] = (byte)(0b10 << 6);
                        outputBuffer[8] = (byte)(inSize >> 8);
                        outputBuffer[9] = (byte)inSize;
                        headerSize = 3;
                    }
                    outputBuffer[10] = inputBuffer[0];
                    _bytesGenerated = 1;
                }
                else
                {
                    // compressed
                    if (inSize == _inputBufferSize)
                    {
                        outputBuffer[7] = (0b00 << 6) + 0x20;
                        outputBuffer[8] = (byte)(cSize >> 8);
                        outputBuffer[9] = (byte)cSize;
                        headerSize = 3;
                    }
                    else
                    {
                        outputBuffer[5] = 0b00 << 6;
                        outputBuffer[6] = (byte)(inSize >> 8);
                        outputBuffer[7] = (byte)inSize;
                        outputBuffer[8] = (byte)(cSize >> 8);
                        outputBuffer[9] = (byte)cSize;
                        headerSize = 5;
                    }
                    _bytesGenerated = cSize;
                }
            }

            if (!_headerWritten)
            {
                BinaryPrimitives.WriteInt32LittleEndian(outputBuffer.Slice(5 - headerSize, 4), (int)FseCompressingAlgorithm.Fse);
                outputBuffer[9 - headerSize] = (byte)_blockSizeId;
                headerSize += 5;
                _headerWritten = true;
            }

            _outputOffset = 10 - headerSize;
            _bytesGenerated += headerSize;

            if (_bytesGenerated == 0)
            {
                _state = FseCompressorState.NeedInput;
            }
            else
            {
                _state = FseCompressorState.WriteOutput;
            }
        }

        public void Advance(int count)
        {
            if (_state != FseCompressorState.WriteOutput)
            {
                throw new InvalidOperationException();
            }

            if ((uint)count > (uint)_bytesGenerated)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _outputOffset += count;
            _bytesGenerated -= count;
            if (_bytesGenerated == 0)
            {
                _bytesRead = 0;
                _outputOffset = 0;
                _state = _headerWritten ? FseCompressorState.NeedInput : FseCompressorState.Completed;
            }
        }

        public void Flush()
        {
            if (_state != FseCompressorState.NeedInput)
            {
                throw new InvalidOperationException();
            }

            Compress();
        }

        public void Complete()
        {
            if (_state == FseCompressorState.Completed)
            {
                return;
            }

            if (_state == FseCompressorState.NeedInput)
            {
                Compress();
            }

            _state = FseCompressorState.WriteOutput;
            Debug.Assert(_outputBuffer != null);

            uint checksum = _hash.GetFinalHash();
            checksum = (checksum >> 5) & ((1u << 22) - 1);

            Span<byte> block = _outputBuffer.AsSpan(_outputOffset + _bytesGenerated);
            block[2] = (byte)checksum;
            block[1] = (byte)(checksum >> 8);
            block[0] = (byte)((checksum >> 16) + (0b11 << 6));

            _bytesGenerated += 3;
            _headerWritten = false;
            _hash = XxHash32.Initialize();
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
            Reset();
        }
    }
}
