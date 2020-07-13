using System;
using System.Buffers;

namespace FiniteStateEntropy
{
    public sealed class FsePipeCompressor : IDisposable
    {
        private readonly IBufferWriter<byte> _writer;

        private FseStreamCompressor? _compressor;
        private FseCompressorState _state;

        public FsePipeCompressor(IBufferWriter<byte> writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _compressor = new FseStreamCompressor();
            _state = FseCompressorState.NeedInput;
        }

        public FseCompressorState State => _state;

        public void Reset()
        {
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FsePipeCompressor));
            }

            _compressor.Reset();
            _state = FseCompressorState.NeedInput;
        }

        public void Process(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FsePipeCompressor));
            }

            if (_state == FseCompressorState.WriteOutput)
            {
                throw new InvalidOperationException();
            }

            if (buffer.IsEmpty)
            {
                consumed = examined = default;
                return;
            }

            int consumedBytes = 0;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                consumedBytes += _compressor.SetInput(segment.Span);

                if (_compressor.State == FseCompressorState.WriteOutput)
                {
                    ArraySegment<byte> output = _compressor.Output;
                    _writer.Write(output.AsSpan());
                    _compressor.Advance(output.Count);

                    _state = FseCompressorState.WriteOutput;
                    break;
                }
            }

            consumed = examined = buffer.Slice(consumedBytes).Start;
        }

        public void NotifyFlushCompleted()
        {
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FsePipeCompressor));
            }

            if (_state == FseCompressorState.WriteOutput)
            {
                _state = _compressor.State;
                return;
            }

            throw new InvalidOperationException();
        }

        public void Flush()
        {
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FsePipeCompressor));
            }

            if (_state != FseCompressorState.NeedInput)
            {
                throw new InvalidOperationException();
            }

            _compressor.Flush();

            if (_compressor.State == FseCompressorState.WriteOutput)
            {
                ArraySegment<byte> output = _compressor.Output;
                _writer.Write(output.AsSpan());
                _compressor.Advance(output.Count);

                _state = FseCompressorState.WriteOutput;
            }
        }

        public void Complete()
        {
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FsePipeCompressor));
            }

            _compressor.Complete();

            if (_compressor.State == FseCompressorState.WriteOutput)
            {
                ArraySegment<byte> output = _compressor.Output;
                _writer.Write(output.AsSpan());
                _compressor.Advance(output.Count);

                _state = FseCompressorState.WriteOutput;
            }
        }

        public void Dispose()
        {
            if (!(_compressor is null))
            {
                _compressor.Dispose();
                _compressor = null;
            }
        }
    }
}
