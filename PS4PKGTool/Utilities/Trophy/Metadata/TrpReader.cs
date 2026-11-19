#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PS4PKGTool.Utilities.TrophyMetadata
{
    public sealed class TrpReader
    {
        private static readonly byte[] Magic = { 0xDC, 0xA2, 0x4D, 0x00 };
        private const int CommonHeaderSize = 28;
        private const int VersionOneTwoHeaderSize = 64;
        private const int VersionThreeHeaderSize = 96;
        private const int MinimumEntrySize = 64;
        private const int EntryNameSize = 36;

        public TrpArchive Read(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("必须指定 TRP 文件路径。", nameof(filePath));

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream, filePath);
        }

        public TrpArchive Read(Stream stream, string sourceName = "<stream>")
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("TRP 流必须可读且可定位。", nameof(stream));
            if (stream.Length < VersionOneTwoHeaderSize)
                throw new InvalidDataException("TRP 头部尚未完整就被截断。");

            stream.Position = 0;
            byte[] commonHeader = ReadExactly(stream, CommonHeaderSize);
            if (!commonHeader.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidDataException("该文件不含 PS4 TRP 魔数 DCA24D00。");

            uint version = BinaryPrimitives.ReadUInt32BigEndian(commonHeader.AsSpan(4, 4));
            if (version is < 1 or > 3)
                throw new NotSupportedException($"不支持 TRP 版本 {version}。");

            long declaredSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(commonHeader.AsSpan(8, 8)));
            uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(commonHeader.AsSpan(16, 4));
            uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(commonHeader.AsSpan(20, 4));
            uint developmentFlag = BinaryPrimitives.ReadUInt32BigEndian(commonHeader.AsSpan(24, 4));
            int headerSize = version == 3 ? VersionThreeHeaderSize : VersionOneTwoHeaderSize;

            if (declaredSize != stream.Length)
                throw new InvalidDataException($"TRP 头部大小 {declaredSize} 与实际长度 {stream.Length} 不匹配。");
            if (entrySize < MinimumEntrySize || entrySize > 4096)
                throw new InvalidDataException($"TRP 条目大小 {entrySize} 超出支持范围。");
            long tableSize = checked((long)entryCount * entrySize);
            if (headerSize > stream.Length || tableSize > stream.Length - headerSize)
                throw new InvalidDataException("TRP 条目表超出了文件范围。");

            stream.Position = CommonHeaderSize;
            byte[] headerTail = ReadExactly(stream, headerSize - CommonHeaderSize);
            byte[] sha1 = version == 1 ? Array.Empty<byte>() : headerTail.AsSpan(0, 20).ToArray();
            var entries = new List<TrpEntry>(checked((int)entryCount));

            for (int index = 0; index < entryCount; index++)
            {
                byte[] raw = ReadExactly(stream, checked((int)entrySize));
                int zero = raw.AsSpan(0, EntryNameSize).IndexOf((byte)0);
                int nameLength = zero < 0 ? EntryNameSize : zero;
                string name = new UTF8Encoding(false, true).GetString(raw, 0, nameLength);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(36, 4));
                ulong unsignedSize = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(40, 8));
                if (unsignedSize > long.MaxValue)
                    throw new InvalidDataException($"TRP 条目 {index} 的大小不受支持。");
                long size = (long)unsignedSize;
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(48, 4));
                ValidateBounds(index, name, offset, size, stream.Length);

                entries.Add(new TrpEntry
                {
                    Index = index,
                    Name = name,
                    Offset = offset,
                    Size = size,
                    Flags = flags,
                    RawMetadata = raw
                });
            }

            return new TrpArchive
            {
                FilePath = sourceName,
                Version = version,
                DeclaredFileSize = declaredSize,
                EntrySize = entrySize,
                DevelopmentFlag = developmentFlag,
                Sha1 = sha1,
                Entries = entries
            };
        }

        public byte[] ReadEntry(TrpArchive archive, TrpEntry entry)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrEmpty(archive.FilePath) || archive.FilePath == "<stream>")
                throw new InvalidOperationException("该压缩包不是从可复用的文件路径读取的。");
            if (entry.Size > int.MaxValue)
                throw new InvalidDataException($"条目 {entry.Name} 太大，无法在内存中处理。");

            using var stream = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateBounds(entry.Index, entry.Name, entry.Offset, entry.Size, stream.Length);
            stream.Position = entry.Offset;
            return ReadExactly(stream, checked((int)entry.Size));
        }

        public void CopyEntryTo(TrpArchive archive, TrpEntry entry, Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            using var stream = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateBounds(entry.Index, entry.Name, entry.Offset, entry.Size, stream.Length);
            stream.Position = entry.Offset;
            long remaining = entry.Size;
            byte[] buffer = new byte[81920];
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read == 0)
                    throw new EndOfStreamException($"条目 {entry.Name} 在达到声明大小前就结束了。");
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static void ValidateBounds(int index, string name, long offset, long size, long fileLength)
        {
            if (offset < 0 || size < 0 || offset > fileLength || size > fileLength - offset)
                throw new InvalidDataException($"TRP 条目 {index}（{name}）指向文件之外：offset={offset}，size={size}，file={fileLength}。");
        }
    }
}
