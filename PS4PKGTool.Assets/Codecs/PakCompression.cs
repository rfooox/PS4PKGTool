using System.IO.Compression;
using K4os.Compression.LZ4;
using PS4PKGTool.Assets.Errors;

namespace PS4PKGTool.Assets.Codecs;

/// <summary>
/// PAK entry decompression: None (raw), Zlib (RFC 1950 wrapper over raw
/// deflate), Gzip, LZ4 block (UE4's FLZ4). Oodle is intentionally absent -
/// it surfaces as a missing-dependency error (never auto-downloaded).
/// </summary>
public static class PakCompression
{
    public const long MaxDecompressed = 256L * 1024 * 1024; // guard against decompression bombs

    public static byte[] Decompress(string method, ReadOnlySpan<byte> data, long uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new CorruptAssetException($"条目声明的未压缩大小为负数（{uncompressedSize}）。");
        if (uncompressedSize > MaxDecompressed)
            throw new UnsupportedAssetException(
                $"条目解压后为 {uncompressedSize} 字节，超过 {MaxDecompressed} 的安全上限。");

        switch (method)
        {
            case "None":
                return data.ToArray();
            case "Zlib":
                return DecompressZlib(data, uncompressedSize);
            case "Gzip":
                return DecompressGzip(data, uncompressedSize);
            case "LZ4":
                return DecompressLz4(data, uncompressedSize);
            case "Oodle":
                throw new MissingDependencyException(
                    "条目使用 Oodle 压缩——未随程序附带 Oodle DLL（见 ASSET_FRAMEWORK.md）。仍可导出压缩后的原始数据。");
            default:
                throw new UnsupportedAssetException($"未知的压缩方式 '{method}'。");
        }
    }

    /// <summary>RFC 1950 zlib: 2-byte header (CMF/FLG), raw deflate stream, optional adler32 tail.</summary>
    private static byte[] DecompressZlib(ReadOnlySpan<byte> data, long expected)
    {
        if (data.Length < 6 || (data[0] & 0x0F) != 8)
            throw new CorruptAssetException("zlib 头部无效。");
        using var input = new MemoryStream(data[2..].ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        return ReadAll(deflate, expected);
    }

    private static byte[] DecompressGzip(ReadOnlySpan<byte> data, long expected)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        return ReadAll(gz, expected);
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> data, long expected)
    {
        var output = new byte[expected];
        int written = LZ4Codec.Decode(data.ToArray(), 0, data.Length, output, 0, output.Length);
        if (written != expected)
            throw new CorruptAssetException($"LZ4 解码写入 {written} 字节，预期 {expected} 字节。");
        return output;
    }

    private static byte[] ReadAll(Stream s, long expected)
    {
        // Never trust the declared size: stream through a bounded loop and
        // abort the instant the output exceeds the promised (or safety-capped)
        // length, so a small declared size cannot be used to inflate a
        // decompression bomb into memory.
        long limit = expected > 0 ? expected : MaxDecompressed;
        using var ms = new MemoryStream(limit <= int.MaxValue ? (int)limit : 0);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new CorruptAssetException(
                    $"解压后数据超过声明的 {expected} 字节。");
            ms.Write(buffer, 0, read);
        }
        if (ms.Length != expected)
            throw new CorruptAssetException($"解压后大小不匹配：实际 {ms.Length}，预期 {expected}。");
        return ms.ToArray();
    }
}
