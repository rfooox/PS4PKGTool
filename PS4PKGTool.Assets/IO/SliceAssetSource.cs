using PS4PKGTool.Assets.Abstractions;

namespace PS4PKGTool.Assets.IO;

/// <summary>
/// A named slice over a parent IAssetSource — the building block for nested
/// containers (PKG → PAK → uasset). Never reads outside [Offset, Offset+Length).
/// </summary>
public sealed class SliceAssetSource : IAssetSource
{
    private readonly IAssetSource _parent;

    public SliceAssetSource(IAssetSource parent, long offset, long length, string? name = null, string? sourceDescription = null)
    {
        if (offset < 0 || length < 0 || offset + length > parent.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "切片必须完全位于父级源内。");
        _parent = parent;
        Offset = offset;
        Length = length;
        Name = name ?? parent.Name;
        SourceDescription = sourceDescription ?? $"{parent.SourceDescription ?? "source"} slice";
    }

    public long Offset { get; }
    public string Name { get; }
    public long Length { get; }
    public string? SourceDescription { get; }

    public Stream OpenRead() => _parent.OpenRead(Offset, Length);

    public Stream OpenRead(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(length), "嵌套切片必须完全位于本切片内。");
        return _parent.OpenRead(Offset + offset, length);
    }
}
