using System.Runtime.CompilerServices;
using PS4PKGTool.Assets.Abstractions;
using PS4PKGTool.Assets.Codecs;
using PS4PKGTool.Assets.Containers;
using PS4PKGTool.Assets.Errors;

namespace PS4PKGTool.Assets.Unreal;

/// <summary>
/// IUnrealAssetBackend implemented with the self-written PAK parser (verified
/// against a real UE4 patch pak). A future CUE4Parse adapter would implement
/// the same contract.
/// </summary>
public sealed class UnrealPakBackend : IUnrealAssetBackend
{
    public string Name => "自行实现的 PAK 解析器（已验证 UE4）";

    private sealed class PakData
    {
        public required UnrealPak.PakHeader Header { get; init; }
        public required IReadOnlyList<PakEntryRef> Entries { get; init; }
    }

    private static readonly ConditionalWeakTable<IAssetSource, PakData> Cache = new();

    private static PakData Get(IAssetSource source)
        => Cache.GetValue(source, s =>
        {
            var (header, entries) = UnrealPak.Parse(s);
            return new PakData { Header = header, Entries = entries };
        });

    public IReadOnlyList<PakEntryRef> ListEntries(IAssetSource source) => Get(source).Entries;

    public Stream OpenEntryStream(IAssetSource source, PakEntryRef entry)
    {
        if (entry.Encrypted)
            throw new UnsupportedAssetException($"条目 '{entry.Path}' 使用 AES 加密——暂不支持解密。");

        var pak = Get(source);
        long baseOffset = ResolveOffset(pak, entry);

        if (entry.Compression == "None")
        {
            byte[] data;
            using (var s = source.OpenRead(baseOffset, entry.Size))
            {
                data = new byte[s.Length];
                s.ReadExactly(data);
            }

            // Some patch paks (verified: CODE VEIN update pak, UE4 v4) store
            // uncompressed entry blobs SELF-DESCRIBING: a 53-byte copy of the
            // index record {Offset, Size, UncompressedSize, Method, Hash,
            // Flags, BlockSize} precedes the payload, and entry.Size includes
            // the record. The embedded record's Size and UncompressedSize
            // fields both equal the blob length - a real payload essentially
            // never starts with its own length twice as little-endian int64s.
            int recordSkip = EmbeddedRecordSize(data);
            if (recordSkip > 0)
                return new MemoryStream(data, recordSkip, data.Length - recordSkip, writable: false);
            return new MemoryStream(data, writable: false);
        }

        // Oodle/unknown methods surface their structured errors before block
        // handling (a compressed entry without a block list is a parse problem,
        // not a dependency one).
        if (entry.Compression == "Oodle")
            throw new MissingDependencyException(
                "条目使用 Oodle 压缩——未随程序附带 Oodle DLL（见 ASSET_FRAMEWORK.md）。仍可导出压缩后的原始数据。");
        if (entry.Compression.StartsWith("Unknown", StringComparison.Ordinal))
            throw new UnsupportedAssetException($"未知的压缩方式 '{entry.Compression}'。");

        // Compressed entries are self-describing: their data starts with the
        // record (including the block list we already parsed from the index);
        // the payloads sit at the block ranges. v5+ block starts are relative
        // to the entry offset; v4 block starts are absolute.
        if (entry.Blocks is not { Count: > 0 })
            throw new UnsupportedAssetException($"条目 '{entry.Path}' 已压缩但缺少块列表。");

        var payload = new MemoryStream();
        foreach (var (start, end) in entry.Blocks)
        {
            long blockFilePos = pak.Header.Version >= 5 ? baseOffset + start : start;
            long blockLen = end - start;
            if (blockFilePos < 0 || blockFilePos + blockLen > source.Length)
                throw new CorruptAssetException($"条目 '{entry.Path}' 的 PAK 块位于文件之外。");
            byte[] block;
            using (var s = source.OpenRead(blockFilePos, blockLen))
            {
                block = new byte[s.Length];
                s.ReadExactly(block);
            }
            payload.Write(block);
        }

        byte[] decompressed = PakCompression.Decompress(entry.Compression, payload.ToArray(), entry.UncompressedSize);
        return new MemoryStream(decompressed, writable: false);
    }

    /// <summary>
    /// v4: entry offsets are absolute. v5+ (RelativeChunkOffsets): stored offsets
    /// are relative to the first entry's stored offset.
    /// </summary>
    private static long ResolveOffset(PakData pak, PakEntryRef entry)
    {
        if (pak.Header.Version < 5 || pak.Entries.Count == 0) return entry.Offset;
        return pak.Entries[0].Offset + entry.Offset;
    }

    /// <summary>
    /// Returns the size of the embedded FPakEntry record prefix (53 bytes) when
    /// the blob is self-describing, or 0. Detection: the Size and
    /// UncompressedSize fields (int64 LE at offsets 8 and 16) both equal the
    /// blob length, which only holds for a record copy.
    /// </summary>
    private static int EmbeddedRecordSize(byte[] blob)
    {
        if (blob.Length < 53) return 0;
        long size = BitConverter.ToInt64(blob, 8);
        long uncompressed = BitConverter.ToInt64(blob, 16);
        if (size != blob.Length || uncompressed != blob.Length) return 0;
        return 53;
    }
}
