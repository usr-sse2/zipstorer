// usrsse2's Fork of ZipStorer, by Jaime Olivares
// Website: https://github.com/usr-sse2/zipstorer
// Version: 3.5.0 (February 21, 2019)

using System.Collections.Generic;
using System.Text;


#if NET45 || NETSTANDARD
using System.Linq;
using System.Threading.Tasks;
#endif

namespace System.IO.Compression
{
    class SubStream : Stream
    {
        private readonly Stream baseStream;
        private readonly long length;
        private readonly long offsetInBaseStream;
        private long position = 0;
        public SubStream(Stream baseStream, long offset, long length)
        {
            if (baseStream == null) throw new ArgumentNullException(nameof(baseStream));
            if (!baseStream.CanRead) throw new ArgumentException("can't read base stream");
            if (!baseStream.CanSeek) throw new ArgumentException("can't seek base stream");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

            this.baseStream = baseStream;
            this.length = length;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = length - position;
            if (remaining <= 0) return 0;
            if (remaining < count) count = (int)remaining;
            baseStream.Position = offsetInBaseStream + position;
            int read = baseStream.Read(buffer, offset, count);
            position += read;
            return read;
        }
        public override long Length => length;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => true;
        public override long Position
        {
            get => position;
            set
            {
                if (value > length || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;
                case SeekOrigin.Current:
                    position += offset;
                    break;
                case SeekOrigin.End:
                    position = length - offset;
                    break;
            }
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => baseStream.Flush();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /*
    class WriteStreamWithAction : Stream
    {
        private readonly Stream baseStream;
        private readonly Action disposeAction;
        private readonly long offsetInBaseStream;

        private long writtenBytes = 0;

        public WriteStreamWithAction(Stream baseStream, long offset, long length, Action disposeAction)
        {
            if (baseStream == null) throw new ArgumentNullException(nameof(baseStream));
            if (!baseStream.CanWrite) throw new ArgumentException("can't write base stream");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

            this.baseStream = baseStream;
            this.disposeAction = disposeAction;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => baseStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            baseStream.Write(buffer, offset, count);
            writtenBytes += count;
        }

        protected override void Dispose(bool disposing)
        {
            Flush();
            base.Dispose(disposing);
            disposeAction();
        }
    }
    */

    /// <summary>
    /// Unique class for compression/decompression file. Represents a Zip file.
    /// </summary>
    public class ZipStorer : IDisposable
    {
        /// <summary>
        /// Compression method enumeration
        /// </summary>
        public enum Compression : ushort
        {
            /// <summary>Uncompressed storage</summary> 
            Store = 0,
            /// <summary>Deflate compression method</summary>
            Deflate = 8
        }

        /// <summary>
        /// Represents an entry in Zip file directory
        /// </summary>
        public struct ZipFileEntry
        {
            /// <summary>Compression method</summary>
            public Compression Method;
            /// <summary>Full path and filename as stored in Zip</summary>
            public string FilenameInZip;
            /// <summary>Original file size</summary>
            public ulong FileSize;
            /// <summary>Compressed file size</summary>
            public ulong CompressedSize;
            /// <summary>Offset of header information inside Zip storage</summary>
            public ulong HeaderOffset;
            /// <summary>Offset of file inside Zip storage</summary>
            public ulong FileOffset;
            /// <summary>Size of header information</summary>
            public uint HeaderSize;
            /// <summary>32-bit checksum of entire file</summary>
            public uint Crc32;
            /// <summary>Last modification time of file</summary>
            public DateTime ModifyTime;
            /// <summary>User comment for file</summary>
            public string Comment;
            /// <summary>True if UTF8 encoding for filename and comments, false if default (CP 437)</summary>
            public bool EncodeUTF8;

            /// <summary>Overriden method</summary>
            /// <returns>Filename in Zip</returns>
            public override string ToString()
            {
                return FilenameInZip;
            }
        }

        #region Public fields
        /// <summary>True if UTF8 encoding for filename and comments, false if default (CP 437)</summary>
        public bool EncodeUTF8 = false;
        /// <summary>Force deflate algotithm even if it inflates the stored file. Off by default.</summary>
        public bool ForceDeflating = false;
        #endregion

        #region Private fields
        // List of files to store
        public Dictionary<string, ZipFileEntry> Files = new Dictionary<string, ZipFileEntry>();
        // Filename of storage file
        private string FileName;
        // Stream object of storage file
        private Stream ZipFileStream;
        // General comment
        private string Comment = "";
        // Central dir image
        private byte[] CentralDirImage = null;
        // Existing files in zip
        private ulong ExistingFiles = 0;
        // File access for Open method
        private FileAccess Access;
        // leave the stream open after the ZipStorer object is disposed
        private bool leaveOpen;
        // Static CRC32 Table
        private static UInt32[] CrcTable = null;
        // Default filename encoder
#if !NETSTANDARD
        private static Encoding DefaultEncoding = Encoding.GetEncoding(437);
#else
        private static Encoding DefaultEncoding = Encoding.UTF8;
#endif
        #endregion

        #region Public methods
        // Static constructor. Just invoked once in order to create the CRC32 lookup table.
        static ZipStorer()
        {
            // Generate CRC32 table
            CrcTable = new UInt32[256];
            for (int i = 0; i < CrcTable.Length; i++)
            {
                UInt32 c = (UInt32)i;
                for (int j = 0; j < 8; j++)
                {
                    if ((c & 1) != 0)
                        c = 3988292384 ^ (c >> 1);
                    else
                        c >>= 1;
                }
                CrcTable[i] = c;
            }
        }

        /// <summary>
        /// Method to create a new storage file
        /// </summary>
        /// <param name="_filename">Full path of Zip file to create</param>
        /// <param name="_comment">General comment for Zip file</param>
        /// <returns>A valid ZipStorer object</returns>
        public static ZipStorer Create(string _filename, string _comment)
        {
            Stream stream = new FileStream(_filename, FileMode.Create, FileAccess.ReadWrite);

            ZipStorer zip = Create(stream, _comment);
            zip.Comment = _comment;
            zip.FileName = _filename;

            return zip;
        }

        /// <summary>
        /// Create a new zip storage in a stream
        /// </summary>
        /// <param name="_stream"></param>
        /// <param name="_comment"></param>
        /// <param name="_leaveOpen">true to leave the stream open after the ZipStorer object is disposed; otherwise, false (default).</param>
        /// <returns>A valid ZipStorer object</returns>
        public static ZipStorer Create(Stream _stream, string _comment, bool _leaveOpen = false)
        {
            ZipStorer zip = new ZipStorer
            {
                Comment = _comment,
                ZipFileStream = _stream,
                Access = FileAccess.Write,
                leaveOpen = _leaveOpen
            };
            return zip;
        }

        /// <summary>
        /// Open an existing storage file
        /// </summary>
        /// <param name="_filename">Full path of Zip file to open</param>
        /// <param name="_access">File access mode as used in FileStream constructor</param>
        /// <returns>A valid ZipStorer object</returns>
        public static ZipStorer Open(string _filename, FileAccess _access)
        {
            Stream stream = new FileStream(_filename, FileMode.Open, _access == FileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite);

            ZipStorer zip = Open(stream, _access);
            zip.FileName = _filename;

            return zip;
        }

        /// <summary>
        /// Open an existing storage from stream
        /// </summary>
        /// <param name="_stream">Already opened stream with zip contents</param>
        /// <param name="_access">File access mode for stream operations</param>
        /// <param name="_leaveOpen">true to leave the stream open after the ZipStorer object is disposed; otherwise, false (default).</param>
        /// <returns>A valid ZipStorer object</returns>
        public static ZipStorer Open(Stream _stream, FileAccess _access, bool _leaveOpen = false)
        {
            if (!_stream.CanSeek && _access != FileAccess.Read)
                throw new InvalidOperationException("Stream cannot seek");

            ZipStorer zip = new ZipStorer
            {
                ZipFileStream = _stream,
                Access = _access,
                leaveOpen = _leaveOpen
            };

            if (zip.ReadFileInfo())
            {
                foreach (var file in zip.ReadCentralDir())
                    zip.Files[file.FilenameInZip] = file;
                return zip;
            }

            if (!_leaveOpen)
                zip.Close();

            throw new InvalidDataException();
        }

        /// <summary>
        /// Add full contents of a file into the Zip storage
        /// </summary>
        /// <param name="_method">Compression method</param>
        /// <param name="_pathname">Full path of file to add to Zip storage</param>
        /// <param name="_filenameInZip">Filename and path as desired in Zip directory</param>
        /// <param name="_comment">Comment for stored file</param>        
        public void AddFile(Compression _method, string _pathname, string _filenameInZip, string _comment)
        {
            using (var stream = new FileStream(_pathname, FileMode.Open, FileAccess.Read))
                AddStream(_method, _filenameInZip, stream, File.GetLastWriteTime(_pathname), _comment);
        }


        /// <summary>
        /// Add full contents of a stream into the Zip storage
        /// </summary>
        /// <param name="_method">Compression method</param>
        /// <param name="_filenameInZip">Filename and path as desired in Zip directory</param>
        /// <param name="_source">Stream object containing the data to store in Zip</param>
        /// <param name="_modTime">Modification time of the data to store</param>
        /// <param name="_comment">Comment for stored file</param>
        public void AddStream(Compression _method, string _filenameInZip, Stream _source, DateTime _modTime, string _comment)
        {
            if (Access == FileAccess.Read)
                throw new InvalidOperationException("Writing is not alowed");

            if (Files.ContainsKey(_filenameInZip))
                throw new InvalidOperationException($"File {_filenameInZip} already exists in the archive");

            ZipFileStream.Position = ZipFileStream.Length;

            // Prepare the fileinfo
            ZipFileEntry zfe = new ZipFileEntry
            {
                Method = _method,
                EncodeUTF8 = EncodeUTF8,
                FilenameInZip = NormalizedFilename(_filenameInZip),
                Comment = _comment ?? "",

                // Even though we write the header now, it will have to be rewritten, since we don't know compressed size or crc.
                Crc32 = 0,  // to be updated later
                HeaderOffset = (ulong)ZipFileStream.Position,  // offset within file of the start of this local record
                ModifyTime = _modTime
            };

            // Write local header
            WriteLocalHeader(ref zfe);
            zfe.FileOffset = (ulong)ZipFileStream.Position;

            // Write file to zip (store)
            Store(ref zfe, _source);

            UpdateCrcAndSizes(ref zfe);

            Files[_filenameInZip] = zfe;
        }

        /*
        public Stream GetStoreStream(Compression _method, string _filenameInZip, DateTime _modTime, string _comment)
        {
            if (Access == FileAccess.Read)
                throw new InvalidOperationException("Writing is not alowed");

            if (Files.ContainsKey(_filenameInZip))
                throw new InvalidOperationException($"File {_filenameInZip} already exists in the archive");

            ZipFileStream.Position = ZipFileStream.Length;

            // Prepare the fileinfo
            ZipFileEntry zfe = new ZipFileEntry
            {
                Method = _method,
                EncodeUTF8 = EncodeUTF8,
                FilenameInZip = NormalizedFilename(_filenameInZip),
                Comment = _comment ?? "",

                // Even though we write the header now, it will have to be rewritten, since we don't know compressed size or crc.
                Crc32 = 0,  // to be updated later
                HeaderOffset = (ulong)ZipFileStream.Position,  // offset within file of the start of this local record
                ModifyTime = _modTime
            };

            // Write local header
            WriteLocalHeader(ref zfe);
            zfe.FileOffset = (ulong)ZipFileStream.Position;

            Stream outStream;
            if (zfe.Method == Compression.Store)
                outStream = ZipFileStream;
            else
                outStream = new DeflateStream(ZipFileStream, CompressionMode.Compress, true);
            
            // WriteStreamWithAction should:
            // 1. Calculate the CRC32 of raw data;
            // 2. Calculate the length of raw data;
            // 3. Calculate the length of compressed data;
            // 4. 
            return new WriteStreamWithAction(outStream, ZipFileStream.Position, 0, () => { });
        }
        */

        /// <summary>
        /// Updates central directory (if pertinent) and close the Zip storage
        /// </summary>
        /// <remarks>This is a required step, unless automatic dispose is used</remarks>
        public void Close()
        {
            if (Access != FileAccess.Read)
            {
                var centralOffset = (UInt64)ZipFileStream.Length;

#if NET45
                foreach (var pair in Files.OrderBy(f => f.Value.FileOffset))
                    WriteCentralDirRecord(pair.Value);
#else
                var pairs = new List<KeyValuePair<string, ZipFileEntry>>(Files);
                pairs.Sort((a, b) => a.Value.FileOffset.CompareTo(b.Value.FileOffset));
                foreach (var pair in pairs)
                    WriteCentralDirRecord(pair.Value);
#endif

                var centralSize = (UInt64)(ZipFileStream.Length) - centralOffset;

                WriteEndRecord(centralSize, centralOffset);
            }

            if (ZipFileStream != null && !leaveOpen)
            {
                ZipFileStream.Flush();
                ZipFileStream.Dispose();
                ZipFileStream = null;
            }
        }
        /// <summary>
        /// Read all the file records in the central directory 
        /// </summary>
        /// <returns>List of all entries in directory</returns>
        private IEnumerable<ZipFileEntry> ReadCentralDir()
        {
            if (CentralDirImage == null)
                throw new InvalidOperationException("Central directory currently does not exist");

            for (int pointer = 0; pointer < CentralDirImage.Length;)
            {
                uint signature = BitConverter.ToUInt32(CentralDirImage, pointer);
                if (signature != 0x02014b50)
                {
                    CentralDirImage = null;
                    yield break;
                }

                bool encodeUTF8 = (BitConverter.ToUInt16(CentralDirImage, pointer + 8) & 0x0800) != 0;
                ushort method = BitConverter.ToUInt16(CentralDirImage, pointer + 10);
                uint modifyTime = BitConverter.ToUInt32(CentralDirImage, pointer + 12);
                uint crc32 = BitConverter.ToUInt32(CentralDirImage, pointer + 16);
                ulong comprSize = BitConverter.ToUInt32(CentralDirImage, pointer + 20);
                ulong fileSize = BitConverter.ToUInt32(CentralDirImage, pointer + 24);
                ushort filenameSize = BitConverter.ToUInt16(CentralDirImage, pointer + 28);
                ushort extraSize = BitConverter.ToUInt16(CentralDirImage, pointer + 30);
                ushort commentSize = BitConverter.ToUInt16(CentralDirImage, pointer + 32);
                ulong headerOffset = BitConverter.ToUInt32(CentralDirImage, pointer + 42);
                uint headerSize = (uint)(46 + filenameSize + extraSize + commentSize);

                Encoding encoder = encodeUTF8 ? Encoding.UTF8 : DefaultEncoding;

                if (extraSize >= 4)
                {
                    int extraPtr = pointer + 46 + filenameSize;
                    // Find ZIP64 extra field record
                    while (extraPtr < pointer + 46 + filenameSize + extraSize)
                    {
                        ushort headerId = BitConverter.ToUInt16(CentralDirImage, extraPtr);
                        ushort length = BitConverter.ToUInt16(CentralDirImage, extraPtr + 2);
                        if (headerId == 1)
                        {
                            extraPtr += 4;
                            if (fileSize == 0xffffffff)
                            {
                                fileSize = BitConverter.ToUInt64(CentralDirImage, extraPtr);
                                extraPtr += 8;
                            }
                            if (comprSize == 0xffffffff)
                            {
                                comprSize = BitConverter.ToUInt64(CentralDirImage, extraPtr);
                                extraPtr += 8;
                            }
                            if (headerOffset == 0xffffffff)
                            {
                                headerOffset = BitConverter.ToUInt64(CentralDirImage, extraPtr);
                                extraPtr += 8;
                            }
                            break;
                        }
                        else
                            extraPtr += (4 + length);
                    }
                }

                ZipFileEntry zfe = new ZipFileEntry
                {
                    Method = (Compression)method,
                    FilenameInZip = encoder.GetString(CentralDirImage, pointer + 46, filenameSize),
                    FileSize = fileSize,
                    CompressedSize = comprSize,
                    HeaderOffset = headerOffset,
                    HeaderSize = headerSize,
                    FileOffset = GetFileOffset(headerOffset),
                    Crc32 = crc32,
                    ModifyTime = DosTimeToDateTime(modifyTime) ?? DateTime.Now
                };

                if (commentSize > 0)
                    zfe.Comment = encoder.GetString(CentralDirImage, pointer + 46 + filenameSize + extraSize, commentSize);

                pointer += (46 + filenameSize + extraSize + commentSize);
                yield return zfe;
            }

            CentralDirImage = null;
        }
        /// <summary>
        /// Copy the contents of a stored file into a physical file
        /// </summary>
        /// <param name="_zfe">Entry information of file to extract</param>
        /// <param name="_filename">Name of file to store uncompressed data</param>
        /// <returns>True if success, false if not.</returns>
        /// <remarks>Unique compression methods are Store and Deflate</remarks>
        public bool ExtractFile(in ZipFileEntry _zfe, string _filename)
        {
            // Make sure the parent directory exist
            string path = Path.GetDirectoryName(_filename);

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            // Check it is directory. If so, do nothing
            if (Directory.Exists(_filename))
                return true;

            bool result;
            using (var output = new FileStream(_filename, FileMode.Create, FileAccess.Write))
                result = ExtractFile(_zfe, output);

            if (result)
            {
                File.SetCreationTime(_filename, _zfe.ModifyTime);
                File.SetLastWriteTime(_filename, _zfe.ModifyTime);
            }

            return result;
        }
        /// <summary>
        /// Copy the contents of a stored file into an opened stream
        /// </summary>
        /// <param name="_zfe">Entry information of file to extract</param>
        /// <param name="_stream">Stream to store the uncompressed data</param>
        /// <returns>True if success, false if not.</returns>
        /// <remarks>Unique compression methods are Store and Deflate</remarks>
        public bool ExtractFile(in ZipFileEntry _zfe, Stream _stream)
        {
            if (!_stream.CanWrite)
                throw new InvalidOperationException("Stream cannot be written");

            // check signature
            byte[] signature = new byte[4];
            ZipFileStream.Seek((long)_zfe.HeaderOffset, SeekOrigin.Begin);
            ZipFileStream.Read(signature, 0, 4);
            if (BitConverter.ToUInt32(signature, 0) != 0x04034b50)
                return false;

            // Select input stream for inflating or just reading
            Stream inStream;
            switch (_zfe.Method)
            {
                case Compression.Store:
                    inStream = ZipFileStream;
                    break;
                case Compression.Deflate:
                    inStream = new DeflateStream(ZipFileStream, CompressionMode.Decompress, true);
                    break;
                default:
                    throw new NotSupportedException(_zfe.Method.ToString());
            }

            // Buffered copy
            byte[] buffer = new byte[16384];
            ZipFileStream.Seek((long)_zfe.FileOffset, SeekOrigin.Begin);
            var bytesPending = _zfe.FileSize;
            while (bytesPending > 0)
            {
                int bytesRead = inStream.Read(buffer, 0, (int)Math.Min(bytesPending, (ulong)buffer.Length));
                _stream.Write(buffer, 0, bytesRead);
                bytesPending -= (uint)bytesRead;
            }
            _stream.Flush();

            if (_zfe.Method == Compression.Deflate)
                inStream.Dispose();
            return true;
        }

        /// <summary>
        /// Get the reading stream for the file
        /// </summary>
        /// <param name="_zfe">Entry information of file to extract</param>
        /// <param name="_stream">Stream to store the uncompressed data</param>
        /// <returns>True if success, false if not.</returns>
        /// <remarks>Unique compression methods are Store and Deflate</remarks>
        public Stream LoadFile(in ZipFileEntry _zfe)
        {
            // check signature
            byte[] signature = new byte[4];
            ZipFileStream.Position = (long)_zfe.HeaderOffset;
            ZipFileStream.Read(signature, 0, 4);
            if (BitConverter.ToUInt32(signature, 0) != 0x04034b50)
                throw new InvalidDataException();

            ZipFileStream.Position = (long)_zfe.FileOffset;
            switch (_zfe.Method)
            {
                case Compression.Store:
                    return new SubStream(ZipFileStream, (long)_zfe.FileOffset, (long)_zfe.FileSize);
                case Compression.Deflate:
                    return new DeflateStream(/*new SubStream(*/ZipFileStream/*, (long)_zfe.FileOffset, (long)_zfe.FileSize)*/, CompressionMode.Decompress, true);
                default:
                    throw new NotSupportedException(_zfe.Method.ToString());
            }
        }

#if NET45
        /// <summary>
        /// Copy the contents of a stored file into an opened stream
        /// </summary>
        /// <param name="_zfe">Entry information of file to extract</param>
        /// <param name="_stream">Stream to store the uncompressed data</param>
        /// <returns>True if success, false if not.</returns>
        /// <remarks>Unique compression methods are Store and Deflate</remarks>
        public async Task<bool> ExtractFileAsync(ZipFileEntry _zfe, Stream _stream)
        {
            if (!_stream.CanWrite)
                throw new InvalidOperationException("Stream cannot be written");

            // check signature
            byte[] signature = new byte[4];
            ZipFileStream.Seek((long)_zfe.HeaderOffset, SeekOrigin.Begin);
            await ZipFileStream.ReadAsync(signature, 0, 4).ConfigureAwait(false);
            if (BitConverter.ToUInt32(signature, 0) != 0x04034b50)
                return false;

            // Select input stream for inflating or just reading
            Stream inStream;
            switch (_zfe.Method)
            {
                case Compression.Store:
                    inStream = ZipFileStream;
                    break;
                case Compression.Deflate:
                    inStream = new DeflateStream(ZipFileStream, CompressionMode.Decompress, true);
                    break;
                default:
                    throw new NotSupportedException(_zfe.Method.ToString());
            }

            // Buffered copy
            byte[] buffer = new byte[16384];
            ZipFileStream.Seek((long)_zfe.FileOffset, SeekOrigin.Begin);
            var bytesPending = _zfe.FileSize;
            while (bytesPending > 0)
            {
                int bytesRead = await inStream.ReadAsync(buffer, 0, (int)Math.Min((long)bytesPending, buffer.Length)).ConfigureAwait(false);
                await _stream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                bytesPending -= (uint)bytesRead;
            }
            _stream.Flush();

            if (_zfe.Method == Compression.Deflate)
                inStream.Dispose();
            return true;
        }
#endif
        /// <summary>
        /// Copy the contents of a stored file into a byte array
        /// </summary>
        /// <param name="_zfe">Entry information of file to extract</param>
        /// <param name="_file">Byte array with uncompressed data</param>
        /// <returns>True if success, false if not.</returns>
        /// <remarks>Unique compression methods are Store and Deflate</remarks>
        public bool ExtractFile(in ZipFileEntry _zfe, out byte[] _file)
        {
            using (MemoryStream ms = new MemoryStream())
                if (ExtractFile(_zfe, ms))
                {
                    _file = ms.ToArray();
                    return true;
                }
                else
                {
                    _file = null;
                    return false;
                }
        }
#endregion

#region Private methods
        // Calculate the file offset by reading the corresponding local header
        private ulong GetFileOffset(ulong _headerOffset)
        {
            byte[] buffer = new byte[2];

            ZipFileStream.Seek((long)_headerOffset + 26, SeekOrigin.Begin);
            ZipFileStream.Read(buffer, 0, 2);
            ushort filenameSize = BitConverter.ToUInt16(buffer, 0);
            ZipFileStream.Read(buffer, 0, 2);
            ushort extraSize = BitConverter.ToUInt16(buffer, 0);

            return _headerOffset + 30 + filenameSize + extraSize;
        }
        /* Local file header:
            local file header signature     4 bytes  (0x04034b50)
            version needed to extract       2 bytes
            general purpose bit flag        2 bytes
            compression method              2 bytes
            last mod file time              2 bytes
            last mod file date              2 bytes
            crc-32                          4 bytes
            compressed size                 4 bytes
            uncompressed size               4 bytes
            filename length                 2 bytes
            extra field length              2 bytes

            filename (variable size)
            extra field (variable size)
        */
        private void WriteLocalHeader(ref ZipFileEntry _zfe)
        {
            ZipFileStream.Position = ZipFileStream.Length;
            long pos = ZipFileStream.Position;
            Encoding encoder = _zfe.EncodeUTF8 ? Encoding.UTF8 : DefaultEncoding;
            byte[] encodedFilename = encoder.GetBytes(_zfe.FilenameInZip);

            ZipFileStream.Write(new byte[] { 80, 75, 3, 4, 20, 0 }, 0, 6); // No extra header
            ZipFileStream.Write(BitConverter.GetBytes((ushort)(_zfe.EncodeUTF8 ? 0x0800 : 0)), 0, 2); // filename and comment encoding 
            ZipFileStream.Write(BitConverter.GetBytes((ushort)_zfe.Method), 0, 2);  // zipping method
            ZipFileStream.Write(BitConverter.GetBytes(DateTimeToDosTime(_zfe.ModifyTime)), 0, 4); // zipping date and time
            ZipFileStream.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 12); // unused CRC, un/compressed size, updated later
            ZipFileStream.Write(BitConverter.GetBytes((ushort)encodedFilename.Length), 0, 2); // filename length
            ZipFileStream.Write(BitConverter.GetBytes((ushort)0), 0, 2); // extra length

            ZipFileStream.Write(encodedFilename, 0, encodedFilename.Length);
            _zfe.HeaderSize = (uint)(ZipFileStream.Position - pos);
        }
        /* Central directory's File header:
            central file header signature   4 bytes  (0x02014b50)
            version made by                 2 bytes
            version needed to extract       2 bytes
            general purpose bit flag        2 bytes
            compression method              2 bytes
            last mod file time              2 bytes
            last mod file date              2 bytes
            crc-32                          4 bytes
            compressed size                 4 bytes
            uncompressed size               4 bytes
            filename length                 2 bytes
            extra field length              2 bytes
            file comment length             2 bytes
            disk number start               2 bytes
            internal file attributes        2 bytes
            external file attributes        4 bytes
            relative offset of local header 4 bytes

            filename (variable size)
            extra field (variable size)
            file comment (variable size)
        */
        private void WriteCentralDirRecord(in ZipFileEntry _zfe)
        {
            Encoding encoder = _zfe.EncodeUTF8 ? Encoding.UTF8 : DefaultEncoding;
            byte[] encodedFilename = encoder.GetBytes(_zfe.FilenameInZip);
            byte[] encodedComment = encoder.GetBytes(_zfe.Comment ?? string.Empty);

            ZipFileStream.Position = ZipFileStream.Length;
            ZipFileStream.Write(new byte[] { 80, 75, 1, 2, 45, 0, 45, 0 }, 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes((ushort)(_zfe.EncodeUTF8 ? 0x0800 : 0)), 0, 2); // filename and comment encoding 
            ZipFileStream.Write(BitConverter.GetBytes((ushort)_zfe.Method), 0, 2);  // zipping method
            ZipFileStream.Write(BitConverter.GetBytes(DateTimeToDosTime(_zfe.ModifyTime)), 0, 4);  // zipping date and time
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.Crc32), 0, 4); // file CRC
            ZipFileStream.Write(BitConverter.GetBytes(0xffffffff), 0, 4); // compressed file size
            ZipFileStream.Write(BitConverter.GetBytes(0xffffffff), 0, 4); // uncompressed file size
            ZipFileStream.Write(BitConverter.GetBytes((ushort)encodedFilename.Length), 0, 2); // Filename in zip
            ZipFileStream.Write(BitConverter.GetBytes((ushort)28), 0, 2); // extra length
            ZipFileStream.Write(BitConverter.GetBytes((ushort)encodedComment.Length), 0, 2);

            ZipFileStream.Write(BitConverter.GetBytes((ushort)0), 0, 2); // disk=0
            ZipFileStream.Write(BitConverter.GetBytes((ushort)0), 0, 2); // file type: binary
            ZipFileStream.Write(BitConverter.GetBytes((ushort)0), 0, 2); // Internal file attributes
            ZipFileStream.Write(BitConverter.GetBytes((ushort)0x8100), 0, 2); // External file attributes (normal/readable)
            ZipFileStream.Write(BitConverter.GetBytes(0xffffffff), 0, 4);  // Offset of header

            ZipFileStream.Write(encodedFilename, 0, encodedFilename.Length);

            // ExtraFieldRecord ZIP64
            ZipFileStream.Write(BitConverter.GetBytes((ushort)1), 0, 2); // ZIP64 Extended Information
            ZipFileStream.Write(BitConverter.GetBytes((ushort)24), 0, 2); // Data size

            ZipFileStream.Write(BitConverter.GetBytes(_zfe.FileSize), 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.CompressedSize), 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.HeaderOffset), 0, 8);


            ZipFileStream.Write(encodedComment, 0, encodedComment.Length);
        }
        /* End of central dir record:
            end of central dir signature    4 bytes  (0x06054b50)
            number of this disk             2 bytes
            number of the disk with the
            start of the central directory  2 bytes
            total number of entries in
            the central dir on this disk    2 bytes
            total number of entries in
            the central dir                 2 bytes
            size of the central directory   4 bytes
            offset of start of central
            directory with respect to
            the starting disk number        4 bytes
            zipfile comment length          2 bytes
            zipfile comment (variable size)
        */
        private void WriteEndRecord(UInt64 _size, UInt64 _offset)
        {
            // Write ZIP64 End of Central Directory
            ZipFileStream.Position = ZipFileStream.Length;
            var EOCD64Offset = ZipFileStream.Length;
            ZipFileStream.Write(new byte[] { 0x50, 0x4b, 0x06, 0x06 }, 0, 4);
            ZipFileStream.Write(BitConverter.GetBytes((UInt64)56 - 12), 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes((UInt16)45), 0, 2); // created by
            ZipFileStream.Write(BitConverter.GetBytes((UInt16)45), 0, 2); // version for unpack
            ZipFileStream.Write(BitConverter.GetBytes((UInt32)0), 0, 4); // current disk
            ZipFileStream.Write(BitConverter.GetBytes((UInt32)0), 0, 4); // start of central directory disk
            ZipFileStream.Write(BitConverter.GetBytes((UInt64)Files.Count), 0, 8); // number of central directory records
            ZipFileStream.Write(BitConverter.GetBytes((UInt64)Files.Count), 0, 8); // total central directory records number
            ZipFileStream.Write(BitConverter.GetBytes(_size), 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes(_offset), 0, 8);

            // Write ZIP64 End of Central Directory Locator
            ZipFileStream.Write(new byte[] { 0x50, 0x4b, 0x06, 0x07 }, 0, 4);
            ZipFileStream.Write(BitConverter.GetBytes((UInt32)0), 0, 4);
            ZipFileStream.Write(BitConverter.GetBytes(EOCD64Offset), 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes((UInt32)1), 0, 4);

            Encoding encoder = EncodeUTF8 ? Encoding.UTF8 : DefaultEncoding;
            byte[] encodedComment = encoder.GetBytes(Comment ?? string.Empty);

            ZipFileStream.Write(new byte[] { 80, 75, 5, 6, 0, 0, 0, 0 }, 0, 8);
            ZipFileStream.Write(BitConverter.GetBytes(0xffff), 0, 2);
            ZipFileStream.Write(BitConverter.GetBytes(0xffff), 0, 2);
            ZipFileStream.Write(BitConverter.GetBytes(0xffffffff), 0, 4);
            ZipFileStream.Write(BitConverter.GetBytes(0xffffffff), 0, 4);
            ZipFileStream.Write(BitConverter.GetBytes((ushort)encodedComment.Length), 0, 2);
            ZipFileStream.Write(encodedComment, 0, encodedComment.Length);
        }
        // Copies all source file into storage file
        private void Store(ref ZipFileEntry _zfe, Stream _source)
        {
            byte[] buffer = new byte[16384];
            int bytesRead;
            uint totalRead = 0;
            Stream outStream;

            ZipFileStream.Position = ZipFileStream.Length;
            long posStart = ZipFileStream.Length;
            long sourceStart = _source.CanSeek ? _source.Position : 0;

            if (_zfe.Method == Compression.Store)
                outStream = ZipFileStream;
            else
                outStream = new DeflateStream(ZipFileStream, CompressionMode.Compress, true);

            _zfe.Crc32 = 0 ^ 0xffffffff;

            do
            {
                bytesRead = _source.Read(buffer, 0, buffer.Length);
                totalRead += (uint)bytesRead;
                if (bytesRead > 0)
                {
                    outStream.Write(buffer, 0, bytesRead);

                    for (uint i = 0; i < bytesRead; i++)
                        _zfe.Crc32 = CrcTable[(_zfe.Crc32 ^ buffer[i]) & 0xFF] ^ (_zfe.Crc32 >> 8);
                }
            } while (bytesRead > 0);
            outStream.Flush();

            if (_zfe.Method == Compression.Deflate)
                outStream.Dispose();

            _zfe.Crc32 ^= 0xffffffff;
            _zfe.FileSize = totalRead;
            _zfe.CompressedSize = (uint)(ZipFileStream.Position - posStart);

            // Verify for real compression
            if (_zfe.Method == Compression.Deflate && !ForceDeflating && _source.CanSeek && _zfe.CompressedSize > _zfe.FileSize)
            {
                // Start operation again with Store algorithm
                _zfe.Method = Compression.Store;
                ZipFileStream.Position = posStart;
                ZipFileStream.SetLength(posStart);
                _source.Position = sourceStart;
                Store(ref _zfe, _source);
            }
        }

#region DateTime utils
        /* DOS Date and time:
            MS-DOS date. The date is a packed value with the following format. Bits Description 
                0-4 Day of the month (131) 
                5-8 Month (1 = January, 2 = February, and so on) 
                9-15 Year offset from 1980 (add 1980 to get actual year) 
            MS-DOS time. The time is a packed value with the following format. Bits Description 
                0-4 Second divided by 2 
                5-10 Minute (059) 
                11-15 Hour (023 on a 24-hour clock) 
        */
        private uint DateTimeToDosTime(DateTime _dt)
        {
            return (uint)(
                (_dt.Second / 2) | (_dt.Minute << 5) | (_dt.Hour << 11) |
                (_dt.Day << 16) | (_dt.Month << 21) | ((_dt.Year - 1980) << 25));
        }
        private DateTime? DosTimeToDateTime(uint _dt)
        {
            int year = (int)(_dt >> 25) + 1980;
            int month = (int)(_dt >> 21) & 15;
            int day = (int)(_dt >> 16) & 31;
            int hours = (int)(_dt >> 11) & 31;
            int minutes = (int)(_dt >> 5) & 63;
            int seconds = (int)(_dt & 31) * 2;

            if (month == 0 || day == 0)
                return null;

            return new DateTime(year, month, day, hours, minutes, seconds);
        }
#endregion

        /* CRC32 algorithm
          The 'magic number' for the CRC is 0xdebb20e3.  
          The proper CRC pre and post conditioning is used, meaning that the CRC register is
          pre-conditioned with all ones (a starting value of 0xffffffff) and the value is post-conditioned by
          taking the one's complement of the CRC residual.
          If bit 3 of the general purpose flag is set, this field is set to zero in the local header and the correct
          value is put in the data descriptor and in the central directory.
        */
        private void UpdateCrcAndSizes(ref ZipFileEntry _zfe)
        {
            long lastPos = ZipFileStream.Position;  // remember position

            ZipFileStream.Position = (long)_zfe.HeaderOffset + 8;
            ZipFileStream.Write(BitConverter.GetBytes((ushort)_zfe.Method), 0, 2);  // zipping method

            ZipFileStream.Position = (long)_zfe.HeaderOffset + 14;
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.Crc32), 0, 4);  // Update CRC
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.CompressedSize), 0, 4);  // Compressed size
            ZipFileStream.Write(BitConverter.GetBytes(_zfe.FileSize), 0, 4);  // Uncompressed size

            ZipFileStream.Position = lastPos;  // restore position
        }
        // Replaces backslashes with slashes to store in zip header
        private string NormalizedFilename(string _filename)
        {
            string filename = _filename.Replace('\\', '/');

            int pos = filename.IndexOf(':');
            if (pos >= 0)
                filename = filename.Remove(0, pos + 1);

            return filename.Trim('/');
        }
        // Reads the end-of-central-directory record
        private bool ReadFileInfo()
        {
            if (ZipFileStream.Length < 22)
                return false;

            try
            {
                // End of Central Directory size is 22
                // We go to -17 and then -5, then read 4 bytes. If not found, go one byte before.
                ZipFileStream.Seek(-17, SeekOrigin.End);
                BinaryReader br = new BinaryReader(ZipFileStream);
                do
                {
                    ZipFileStream.Seek(-5, SeekOrigin.Current);
                    UInt32 sig = br.ReadUInt32();
                    if (sig == 0x06054b50)
                    {
                        var EOCDPosition = ZipFileStream.Position - 4;

                        ZipFileStream.Seek(6, SeekOrigin.Current);

                        UInt64 entries = br.ReadUInt16();
                        UInt64 centralSize = br.ReadUInt32();
                        Int64 centralDirOffset = br.ReadUInt32();
                        UInt16 commentSize = br.ReadUInt16();

                        var commentPosition = ZipFileStream.Position;

                        if (centralDirOffset == 0xffffffff)
                        {
                            // it's a ZIP64 archive
                            ZipFileStream.Position = EOCDPosition - 20;
                            sig = br.ReadUInt32();
                            if (sig != 0x07064b50)
                                return false;

                            ZipFileStream.Seek(4, SeekOrigin.Current);

                            Int64 EOCD64Position = br.ReadInt64();

                            ZipFileStream.Position = EOCD64Position;

                            sig = br.ReadUInt32();
                            if (sig != 0x06064b50)
                                return false;

                            ZipFileStream.Seek(28, SeekOrigin.Current);
                            entries = br.ReadUInt64();
                            centralSize = br.ReadUInt64();
                            centralDirOffset = br.ReadInt64();
                        }

                        // check if comment field is the very last data in file
                        if (commentPosition + commentSize != ZipFileStream.Length)
                            return false;

                        // Copy entire central directory to a memory buffer
                        CentralDirImage = new byte[centralSize];
                        ZipFileStream.Position = centralDirOffset;
                        ZipFileStream.Read(CentralDirImage, 0, (int)centralSize);

                        if (Access == FileAccess.ReadWrite || Access == FileAccess.Write)
                            ZipFileStream.SetLength(centralDirOffset);
                        return true;
                    }
                } while (ZipFileStream.Position > 0);
            }
            catch { }

            return false;
        }
#endregion

#region IDisposable Members
        /// <summary>
        /// Closes the Zip file stream
        /// </summary>
        public void Dispose()
        {
            Close();
        }
#endregion
    }
}
