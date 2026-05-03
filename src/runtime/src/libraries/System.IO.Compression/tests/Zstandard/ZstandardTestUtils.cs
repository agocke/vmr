// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace System.IO.Compression
{
    public static class ZstandardTestUtils
    {
        private static readonly object s_lock = new();
        private static readonly string s_generatedDataPath = Path.Combine(Path.GetTempPath(), "rbz-zstandard-testdata");

        public static byte[] CreateSampleDictionary()
        {
            // Create a simple dictionary with some sample data
            return "a;owijfawoiefjawfafajzlf zfijf slifljeifa flejf;waiefjwaf"u8.ToArray();
        }

        public static byte[] CreateTestData(int size = 1000)
        {
            // Create test data of specified size
            byte[] data = new byte[size];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256); // Varying pattern
            }
            return data;
        }

        public static string GetCompressedTestFile(string uncompressedPath)
        {
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "ZstandardTestData", Path.GetFileName(uncompressedPath) + ".zst");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            string sourcePath = File.Exists(uncompressedPath) ? uncompressedPath : Path.Combine(AppContext.BaseDirectory, uncompressedPath);
            string generatedPath = Path.Combine(s_generatedDataPath, Path.GetFileName(uncompressedPath) + ".zst");

            lock (s_lock)
            {
                if (!File.Exists(generatedPath))
                {
                    Directory.CreateDirectory(s_generatedDataPath);

                    using FileStream source = File.OpenRead(sourcePath);
                    using FileStream destination = File.Create(generatedPath);
                    using ZstandardStream compressionStream = new(destination, CompressionLevel.Optimal, leaveOpen: false);
                    source.CopyTo(compressionStream);
                }
            }

            return generatedPath;
        }
    }
}
