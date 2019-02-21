using System;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZipStorerTests
{
    [TestClass]
    public class ZipStorerTests
    {
        private class UnclosableStream : Stream
        {
            private readonly Stream _stream;
            public UnclosableStream(Stream stream) => _stream = stream;

            public override bool CanRead => _stream.CanRead;
            public override bool CanSeek => _stream.CanSeek;
            public override bool CanWrite => _stream.CanWrite;
            public override long Length => _stream.Length;
            public override long Position { get => _stream.Position; set => _stream.Position = value; }
            public override void Flush() => _stream.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
            public override void SetLength(long value) => _stream.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        }

        private static readonly string[] filesToStore =
        {
            typeof(ZipStorerTests).Assembly.Location,
            typeof(object).Assembly.Location,
            typeof(TestMethodAttribute).Assembly.Location
        };

        [TestMethod]
        public void ZipAppend()
        {
            using (var stream = new MemoryStream())
            {
                using (var systemArchive = new ZipArchive(new UnclosableStream(stream), ZipArchiveMode.Create))
                {
                    var entry = systemArchive.CreateEntry(Path.GetFileName(filesToStore[0]));
                    using (var storeStream = entry.Open())
                    using (var fileStream = File.OpenRead(filesToStore[0]))
                        fileStream.CopyTo(storeStream);
                }

                for (int i = 1; i <= 2; i++)
                {
                    stream.Position = 0;

                    using (var zipStorerArchive = ZipStorer.Open(stream, FileAccess.ReadWrite, true))
                        zipStorerArchive.AddFile(ZipStorer.Compression.Deflate, filesToStore[i], Path.GetFileName(filesToStore[i]), string.Empty);
                }

                stream.Position = 0;
                using (var systemArchive = new ZipArchive(new UnclosableStream(stream), ZipArchiveMode.Read))
                    foreach (var file in filesToStore)
                    {
                        var entry = systemArchive.GetEntry(Path.GetFileName(file));
                        using (var fileStreamFromArchive = entry.Open())
                        {
                            var bytes = new byte[entry.Length];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }
                    }

                stream.Position = 0;
                using (var zipStorerArchive = ZipStorer.Open(stream, FileAccess.Read, true))
                    foreach (var file in filesToStore.Reverse())
                        using (var fileStreamFromArchive = new MemoryStream())
                        {
                            zipStorerArchive.ExtractFile(zipStorerArchive.Files[Path.GetFileName(file)], fileStreamFromArchive);
                            fileStreamFromArchive.Position = 0;

                            var bytes = new byte[fileStreamFromArchive.Length];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }

                stream.Position = 0;
                using (var zipStorerArchive = ZipStorer.Open(stream, FileAccess.Read, true))
                    foreach (var file in filesToStore.Reverse())
                    {
                        var entry = zipStorerArchive.Files[Path.GetFileName(file)];
                        using (var fileStreamFromArchive = zipStorerArchive.LoadFile(entry))
                        {
                            var bytes = new byte[entry.FileSize];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }
                    }
            }
        }

        [TestMethod]
        public void ZipAppend2()
        {
            using (var stream = new MemoryStream())
            {
                using (var zipStorerArchive = ZipStorer.Create(stream, string.Empty, true))
                    zipStorerArchive.AddFile(ZipStorer.Compression.Deflate, filesToStore[0], Path.GetFileName(filesToStore[0]), string.Empty);

                for (int i = 1; i <= 2; i++)
                {
                    stream.Position = 0;
                    using (var systemArchive = new ZipArchive(new UnclosableStream(stream), ZipArchiveMode.Update))
                    {
                        var entry = systemArchive.CreateEntry(Path.GetFileName(filesToStore[i]));
                        using (var storeStream = entry.Open())
                        using (var fileStream = File.OpenRead(filesToStore[i]))
                            fileStream.CopyTo(storeStream);
                    }
                }

                stream.Position = 0;
                using (var systemArchive = new ZipArchive(new UnclosableStream(stream), ZipArchiveMode.Read))
                    foreach (var file in filesToStore)
                    {
                        var entry = systemArchive.GetEntry(Path.GetFileName(file));
                        using (var fileStreamFromArchive = entry.Open())
                        {
                            var bytes = new byte[entry.Length];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }
                    }

                stream.Position = 0;
                using (var zipStorerArchive = ZipStorer.Open(stream, FileAccess.Read, true))
                    foreach (var file in filesToStore.Reverse())
                        using (var fileStreamFromArchive = new MemoryStream())
                        {
                            zipStorerArchive.ExtractFile(zipStorerArchive.Files[Path.GetFileName(file)], fileStreamFromArchive);
                            fileStreamFromArchive.Position = 0;

                            var bytes = new byte[fileStreamFromArchive.Length];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }

                stream.Position = 0;
                using (var zipStorerArchive = ZipStorer.Open(stream, FileAccess.Read, true))
                    foreach (var file in filesToStore.Reverse())
                    {
                        var entry = zipStorerArchive.Files[Path.GetFileName(file)];
                        using (var fileStreamFromArchive = zipStorerArchive.LoadFile(entry))
                        {
                            var bytes = new byte[entry.FileSize];
                            fileStreamFromArchive.Read(bytes, 0, bytes.Length);
                            Assert.IsTrue(bytes.SequenceEqual(File.ReadAllBytes(file)));
                        }
                    }
            }
        }
    }
}
