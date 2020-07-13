using System.IO;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;

namespace FiniteStateEntropy.Benchmarks
{
    [MemoryDiagnoser]
    public class FseStreamDecompress
    {

        [Params("Ipsum.txt.fse", "raw.dat.fse", "rle.dat.fse")]
        public string FileName { get; set; }

        [Params(true, false)]
        public bool ValidateChecksum { get; set; }

        private byte[] _compressedData;

        private byte[] _buffer;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new byte[32768];

            using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("FiniteStateEntropy.Benchmarks." + FileName);
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            _compressedData = ms.ToArray();
        }

        [Benchmark]
        public void TestStreamDecompress()
        {
            byte[] buffer = _buffer;
            var ms = new MemoryStream(_compressedData, false);
            var fse = new FseStream(ms, CompressionMode.Decompress, true);
            fse.ValidateChecksum = ValidateChecksum;

            int readSize;
            do
            {
                readSize = fse.Read(buffer);
            } while (readSize != 0);
        }


    }
}
