using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace FiniteStateEntropy.Tests
{
    internal static class PipeHelper
    {
        public static async Task DecompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
        {
            var fse = new FsePipeDecompressor(writer);

            while (true)
            {
                ReadResult readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    await writer.CompleteAsync().ConfigureAwait(false);
                    return;
                }

                fse.Process(readResult.Buffer, out SequencePosition consumed, out SequencePosition examined);
                reader.AdvanceTo(consumed, examined);

                switch (fse.State)
                {
                    case FseDecompressorState.WriteOutput:
                        await writer.FlushAsync().ConfigureAwait(false);
                        fse.NotifyFlushCompleted();
                        break;
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        await writer.CompleteAsync().ConfigureAwait(false);
                        return;
                }
            }
        }

        public static async Task CompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
        {
            var fse = new FsePipeCompressor(writer);

            while (true)
            {
                ReadResult readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    fse.Flush();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await writer.CompleteAsync().ConfigureAwait(false);
                    return;
                }

                fse.Process(readResult.Buffer, out SequencePosition consumed, out SequencePosition examined);
                reader.AdvanceTo(consumed, examined);

                switch (fse.State)
                {
                    case FseCompressorState.WriteOutput:
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        fse.NotifyFlushCompleted();
                        break;
                    case FseCompressorState.Completed:
                        fse.Complete();
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await writer.CompleteAsync().ConfigureAwait(false);
                        return;
                }
            }
        }
    }
}
