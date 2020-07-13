using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace FiniteStateEntropy.Benchmarks
{
    [MemoryDiagnoser]
    public class FsePipeDecompress
    {
        [Params("Ipsum.txt.fse", "raw.dat.fse", "rle.dat.fse")]
        public string FileName { get; set; }

        [Params(true, false)]
        public bool ValidateChecksum { get; set; }

        private IBufferWriter<byte> _writer;

        private byte[] _compressedData;

        [GlobalSetup]
        public void Setup()
        {
            _writer = new NullBufferWriter();
            _writer.GetMemory(32768);

            using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("FiniteStateEntropy.Benchmarks." + FileName);
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            _compressedData = ms.ToArray();
        }

        [Benchmark]
        public async Task TestPipeDecompress()
        {
            var ms = new MemoryStream(_compressedData, false);
            var reader = PipeReader.Create(ms);
            var fse = new FsePipeDecompressor(_writer);

            while (true)
            {
                ReadResult readResult = await reader.ReadAsync().ConfigureAwait(false);
                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    return;
                }

                fse.Process(readResult.Buffer, out SequencePosition consumed, out SequencePosition examined);
                reader.AdvanceTo(consumed, examined);

                switch (fse.State)
                {
                    case FseDecompressorState.WriteOutput:
                        fse.NotifyFlushCompleted();
                        break;
                    case FseDecompressorState.InvalidData:
                        throw new InvalidDataException();
                    case FseDecompressorState.InvalidChecksum:
                        throw new InvalidDataException();
                    case FseDecompressorState.Completed:
                        return;
                }
            }

        }

    }
}
