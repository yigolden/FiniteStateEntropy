using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace FiniteStateEntropy.Tests
{
    public class TestStreamDecompression
    {
        public static IEnumerable<object[]> GetCompressedFiles()
        {
            yield return new object[]
            {
                // file name
                "Assets/Ipsum.txt.fse",
                // SHA256
                "e5ba6adfd34d6f469f82dd81b5500bf6ed34e8c7ec9b92caed6c6d84d3eec064"
            };
            yield return new object[]
            {
                // file name
                "Assets/raw.dat.fse",
                // SHA256
                "dcd9d840efd4373c1e2cc911229f3af42c97d61b6207a4eb3f7c987090959084"
            };
            yield return new object[]
            {
                // file name
                "Assets/rle.dat.fse",
                // SHA256
                "dae0c6ece558ac75fd53983793bb7037f46a33373d2e62805beb7ee364f1b3a5"
            };
        }

        [Theory]
        [MemberData(nameof(GetCompressedFiles))]
        public void TestDecompress(string filename, string sha256)
        {
            using var ms = new MemoryStream();

            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using var fse = new FseStream(fs, CompressionMode.Decompress, true);

            fse.CopyTo(ms);

            ms.Seek(0, SeekOrigin.Begin);
            byte[] actual = SHA256.Create().ComputeHash(ms);

            byte[] expected = HexHelper.StringToByteArray(sha256);

            Assert.True(actual.AsSpan().SequenceEqual(expected));
        }

    }
}
