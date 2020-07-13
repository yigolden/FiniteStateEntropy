using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace FiniteStateEntropy.Tests
{
    public class TestPipeRoudtrip
    {
        public static IEnumerable<object[]> GetTestFiles()
        {
            yield return new object[]
            {
                "Assets/Ipsum.txt.fse",
            };
            yield return new object[]
            {
                "Assets/raw.dat.fse",
            };
            yield return new object[]
            {
                "Assets/rle.dat.fse",
            };
        }


        [Theory]
        [MemberData(nameof(GetTestFiles))]
        public async Task TestRoundtrip(string filename)
        {
            // Load
            byte[] decompressed = await File.ReadAllBytesAsync(filename);
            if (filename.EndsWith(".fse", StringComparison.OrdinalIgnoreCase))
            {
                var ms = new MemoryStream();
                await PipeHelper.DecompressAsync(PipeReader.Create(new MemoryStream(decompressed)), PipeWriter.Create(new NoDisposableStream(ms)));
                decompressed = ms.ToArray();
            }

            // Compress
            var compressedStream = new MemoryStream();
            await PipeHelper.CompressAsync(PipeReader.Create(new MemoryStream(decompressed)), PipeWriter.Create(new NoDisposableStream(compressedStream)));

            // Decompress
            compressedStream.Seek(0, SeekOrigin.Begin);
            var decompressedStream = new MemoryStream();
            await PipeHelper.DecompressAsync(PipeReader.Create(compressedStream), PipeWriter.Create(new NoDisposableStream(decompressedStream)));

            // Compare
            decompressedStream.ToArray().AsSpan().SequenceCompareTo(decompressed);
        }

    }
}
