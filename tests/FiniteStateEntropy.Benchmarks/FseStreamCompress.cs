using System.IO;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;

namespace FiniteStateEntropy.Benchmarks
{
    [MemoryDiagnoser]
    public class FseStreamCompress
    {
        [Params("Ipsum.txt.fse", "raw.dat.fse", "rle.dat.fse")]
        public string FileName { get; set; }

        private byte[] _decompressedData;

        [GlobalSetup]
        public void Setup()
        {
            using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("FiniteStateEntropy.Benchmarks." + FileName);
            var ms = new MemoryStream();
            using var fse = new FseStream(stream, CompressionMode.Decompress, true);
            fse.CopyTo(ms);
            _decompressedData = ms.ToArray();
        }

        [Benchmark]
        public void TestStreamCompress()
        {
            var ms = new MemoryStream(_decompressedData);
            var nullStream = new NullWriteStream();
            using var fse = new FseStream(nullStream, CompressionMode.Compress, true);
            ms.CopyTo(fse);
        }

    }
}
