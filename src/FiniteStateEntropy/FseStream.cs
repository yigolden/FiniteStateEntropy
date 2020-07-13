using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace FiniteStateEntropy
{
    public sealed class FseStream : Stream
    {
        private const int BufferSize = 32768;

        private Stream? _stream;
        private readonly CompressionMode _mode;
        private readonly bool _leaveOpen;

        private FseStreamDecompressor? _decompressor;
        private byte[]? _inputBuffer;
        private int _bytesConsumed;
        private int _bytesRead;

        private FseStreamCompressor? _compressor;

        public bool ValidateChecksum { get => _decompressor?.ValidateChecksum ?? false; set => SetValidateChecksum(value); }

        private void SetValidateChecksum(bool value)
        {
            if (_decompressor is null)
            {
                throw new InvalidOperationException();
            }

            _decompressor.ValidateChecksum = value;
        }

        public FseStream(Stream stream, CompressionMode mode, bool leaveOpen)
        {
            _stream = stream;
            _mode = mode;
            _leaveOpen = leaveOpen;

            if (mode == CompressionMode.Decompress)
            {
                _decompressor = new FseStreamDecompressor();
                _decompressor.ValidateChecksum = true;
                _inputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            }
            else if (mode == CompressionMode.Compress)
            {
                _compressor = new FseStreamCompressor();
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public override bool CanRead => _mode == CompressionMode.Decompress;

        public override bool CanSeek => false;

        public override bool CanWrite => _mode == CompressionMode.Compress;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Decompress)
            {
                throw new InvalidOperationException();
            }
            if (_decompressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);
            Debug.Assert(_inputBuffer != null);

            if (buffer is null)
            {
                throw new ArgumentOutOfRangeException(nameof(buffer));
            }
            if ((uint)offset > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || (offset + count) > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            while (true)
            {
                switch (_decompressor.State)
                {
                    case FseDecompressorState.NeedInput:
                        if (_bytesRead > _bytesConsumed)
                        {
                            _bytesConsumed += _decompressor.SetInput(_inputBuffer.AsSpan(_bytesConsumed, _bytesRead - _bytesConsumed));
                            continue;
                        }
                        _bytesRead = _stream!.Read(_inputBuffer!, 0, _inputBuffer!.Length);
                        if (_bytesRead == 0)
                        {
                            return 0;
                        }
                        _bytesConsumed = _decompressor.SetInput(_inputBuffer.AsSpan(0, _bytesRead));
                        continue;
                    case FseDecompressorState.WriteOutput:
                        return _decompressor.WriteOutput(buffer.AsSpan(offset, count));
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        return 0;
                }
            }
        }

#if !NO_FAST_SPAN
        public override int Read(Span<byte> buffer)
        {
            if (_mode != CompressionMode.Decompress)
            {
                throw new InvalidOperationException();
            }
            if (_decompressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);
            Debug.Assert(_inputBuffer != null);

            while (true)
            {
                switch (_decompressor.State)
                {
                    case FseDecompressorState.NeedInput:
                        if (_bytesRead > _bytesConsumed)
                        {
                            _bytesConsumed += _decompressor.SetInput(_inputBuffer.AsSpan(_bytesConsumed, _bytesRead - _bytesConsumed));
                            continue;
                        }
                        _bytesRead = _stream!.Read(_inputBuffer!, 0, _inputBuffer!.Length);
                        if (_bytesRead == 0)
                        {
                            return 0;
                        }
                        _bytesConsumed = _decompressor.SetInput(_inputBuffer.AsSpan(0, _bytesRead));
                        continue;
                    case FseDecompressorState.WriteOutput:
                        return _decompressor.WriteOutput(buffer);
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        return 0;
                }
            }
        }

#endif

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_mode != CompressionMode.Decompress)
            {
                throw new InvalidOperationException();
            }
            if (_decompressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);
            Debug.Assert(_inputBuffer != null);

            if (buffer is null)
            {
                throw new ArgumentOutOfRangeException(nameof(buffer));
            }
            if ((uint)offset > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || (offset + count) > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            while (true)
            {
                switch (_decompressor.State)
                {
                    case FseDecompressorState.NeedInput:
                        if (_bytesRead > _bytesConsumed)
                        {
                            _bytesConsumed += _decompressor.SetInput(_inputBuffer.AsSpan(_bytesConsumed, _bytesRead - _bytesConsumed));
                            continue;
                        }
                        _bytesRead = await _stream!.ReadAsync(_inputBuffer!, 0, _inputBuffer!.Length, cancellationToken).ConfigureAwait(false);
                        if (_bytesRead == 0)
                        {
                            return 0;
                        }
                        _bytesConsumed = _decompressor.SetInput(_inputBuffer.AsSpan(0, _bytesRead));
                        continue;
                    case FseDecompressorState.WriteOutput:
                        return _decompressor.WriteOutput(buffer.AsSpan(offset, count));
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        return 0;
                }
            }
        }

#if !NO_FAST_SPAN
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_mode != CompressionMode.Decompress)
            {
                throw new InvalidOperationException();
            }
            if (_decompressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);
            Debug.Assert(_inputBuffer != null);

            while (true)
            {
                switch (_decompressor.State)
                {
                    case FseDecompressorState.NeedInput:
                        if (_bytesRead > _bytesConsumed)
                        {
                            _bytesConsumed += _decompressor.SetInput(_inputBuffer.AsSpan(_bytesConsumed, _bytesRead - _bytesConsumed));
                            continue;
                        }
                        _bytesRead = await _stream!.ReadAsync(_inputBuffer!, 0, _inputBuffer!.Length, cancellationToken).ConfigureAwait(false);
                        if (_bytesRead == 0)
                        {
                            return 0;
                        }
                        _bytesConsumed = _decompressor.SetInput(_inputBuffer.AsSpan(0, _bytesRead));
                        continue;
                    case FseDecompressorState.WriteOutput:
                        return _decompressor.WriteOutput(buffer.Span);
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        return 0;
                }
            }
        }
#endif

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException();
            }
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);

            if (buffer is null)
            {
                throw new ArgumentOutOfRangeException(nameof(buffer));
            }
            if ((uint)offset > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || (offset + count) > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            while (count > 0)
            {
                switch (_compressor.State)
                {
                    case FseCompressorState.NeedInput:
                    case FseCompressorState.Completed:
                        int bytesConsumed = _compressor.SetInput(buffer.AsSpan(offset, count));
                        offset += bytesConsumed;
                        count -= bytesConsumed;
                        break;
                    case FseCompressorState.WriteOutput:
                        ArraySegment<byte> output = _compressor.Output;
                        _stream!.Write(output.Array!, output.Offset, output.Count);
                        _compressor.Advance(output.Count);
                        break;
                }
            }
        }

#if !NO_FAST_SPAN

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException();
            }
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);

            while (!buffer.IsEmpty)
            {
                switch (_compressor.State)
                {
                    case FseCompressorState.NeedInput:
                    case FseCompressorState.Completed:
                        int bytesConsumed = _compressor.SetInput(buffer);
                        buffer = buffer.Slice(bytesConsumed);
                        break;
                    case FseCompressorState.WriteOutput:
                        ArraySegment<byte> output = _compressor.Output;
                        _stream!.Write(output.Array!, output.Offset, output.Count);
                        _compressor.Advance(output.Count);
                        break;
                }
            }
        }

#endif

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException();
            }
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);

            if (buffer is null)
            {
                throw new ArgumentOutOfRangeException(nameof(buffer));
            }
            if ((uint)offset > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || (offset + count) > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            while (count > 0)
            {
                switch (_compressor.State)
                {
                    case FseCompressorState.NeedInput:
                    case FseCompressorState.Completed:
                        int bytesConsumed = _compressor.SetInput(buffer.AsSpan(offset, count));
                        offset += bytesConsumed;
                        count -= bytesConsumed;
                        break;
                    case FseCompressorState.WriteOutput:
                        ArraySegment<byte> output = _compressor.Output;
                        await _stream!.WriteAsync(output.Array!, output.Offset, output.Count, cancellationToken).ConfigureAwait(false);
                        _compressor.Advance(output.Count);
                        break;
                }
            }
        }

#if !NO_FAST_SPAN

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException();
            }
            if (_compressor is null)
            {
                throw new ObjectDisposedException(nameof(FseStream));
            }
            Debug.Assert(_stream != null);

            while (!buffer.IsEmpty)
            {
                switch (_compressor.State)
                {
                    case FseCompressorState.NeedInput:
                    case FseCompressorState.Completed:
                        int bytesConsumed = _compressor.SetInput(buffer.Span);
                        buffer = buffer.Slice(bytesConsumed);
                        break;
                    case FseCompressorState.WriteOutput:
                        ArraySegment<byte> output = _compressor.Output;
                        await _stream!.WriteAsync(output.Array!, output.Offset, output.Count, cancellationToken).ConfigureAwait(false);
                        _compressor.Advance(output.Count);
                        break;
                }
            }
        }
#endif


        public override void Flush()
        {
            if (!(_compressor is null))
            {
                Flush(false);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!(_compressor is null))
            {
                return FlushAsync(false, cancellationToken);
            }
            return Task.CompletedTask;
        }

        private void Flush(bool complete)
        {
            Debug.Assert(_stream != null);
            Debug.Assert(_compressor != null);

            if (_compressor!.State == FseCompressorState.NeedInput)
            {
                _compressor.Flush();
            }

            if (complete)
            {
                _compressor.Complete();
            }

            if (_compressor.State == FseCompressorState.WriteOutput)
            {
                ArraySegment<byte> output = _compressor.Output;

                _stream!.Write(output.Array!, output.Offset, output.Count);

                _compressor.Advance(output.Count);
            }

            _stream!.Flush();
        }

        private async Task FlushAsync(bool complete, CancellationToken cancellationToken = default)
        {
            Debug.Assert(_stream != null);
            Debug.Assert(_compressor != null);

            if (_compressor!.State == FseCompressorState.NeedInput)
            {
                _compressor.Flush();
            }

            if (complete)
            {
                _compressor.Complete();
            }

            if (_compressor.State == FseCompressorState.WriteOutput)
            {
                ArraySegment<byte> output = _compressor.Output;

                await _stream!.WriteAsync(output.Array!, output.Offset, output.Count, cancellationToken).ConfigureAwait(false);

                _compressor.Advance(output.Count);
            }

            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_leaveOpen && !(_stream is null))
            {
                _stream.Dispose();
                _stream = null;
            }
            if (!(_decompressor is null))
            {
                _decompressor.Dispose();
                _decompressor = null;
            }
            if (!(_compressor is null))
            {
                Flush(true);
                _compressor = null;
            }
            if (!(_inputBuffer is null))
            {
                ArrayPool<byte>.Shared.Return(_inputBuffer);
                _inputBuffer = null;
            }
            base.Dispose(disposing);
        }

#if !NO_ASYNC_DISPOSABLE
        public override async ValueTask DisposeAsync()
        {
            if (!_leaveOpen && !(_stream is null))
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
            if (!(_decompressor is null))
            {
                _decompressor.Dispose();
                _decompressor = null;
            }
            if (!(_compressor is null))
            {
                await FlushAsync(true).ConfigureAwait(false);
                _compressor = null;
            }
            if (!(_inputBuffer is null))
            {
                ArrayPool<byte>.Shared.Return(_inputBuffer);
                _inputBuffer = null;
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

    }
}
