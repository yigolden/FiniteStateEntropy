using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace FiniteStateEntropy.Tests
{
    public class TestStreamRoundtrip
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
        public void TestRoundtrip(string filename)
        {
            // Load
            byte[] decompressed = File.ReadAllBytes(filename);
            if (filename.EndsWith(".fse", StringComparison.OrdinalIgnoreCase))
            {
                var ms = new MemoryStream();
                using (var fse = new FseStream(new MemoryStream(decompressed), CompressionMode.Decompress, true))
                {
                    fse.CopyTo(ms);
                }
                decompressed = ms.ToArray();
            }

            // Compress
            var compressedStream = new MemoryStream();
            using (var fse = new FseStream(compressedStream, CompressionMode.Compress, true))
            {
                var ms = new MemoryStream(decompressed);
                ms.CopyTo(fse);
            }

            // Decompress
            compressedStream.Seek(0, SeekOrigin.Begin);
            var decompressedStream = new MemoryStream();
            using (var fse = new FseStream(compressedStream, CompressionMode.Decompress, true))
            {
                fse.CopyTo(decompressedStream);
            }

            // Compare
            decompressedStream.ToArray().AsSpan().SequenceCompareTo(decompressed);
        }
    }
}
