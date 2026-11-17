// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Text;
using XliffTasks.Model;
using Xunit;

namespace XliffTasks.Tests;

public class DocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavePublishesFromWriteOnlyTemporaryFile(bool replaceExisting)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("xliff-document-");
        try
        {
            string path = Path.Combine(directory.FullName, "translated.txt");
            if (replaceExisting)
            {
                File.WriteAllText(path, "old content that must be replaced");
            }

            var document = new WriteOnlyDocument { Encoding = new UTF8Encoding(false) };
            document.Save(new FileInfo(path));

            Assert.Equal("translated content", File.ReadAllText(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class WriteOnlyDocument : Document
    {
        public override bool HasContent => true;

        public override void Load(TextReader reader) => throw new System.NotSupportedException();

        public override void Save(TextWriter writer)
        {
            Stream stream = Assert.IsType<StreamWriter>(writer).BaseStream;
            Assert.True(stream.CanWrite);
            Assert.False(stream.CanRead);
            writer.Write("translated content");
        }
    }
}
