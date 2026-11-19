#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PS4PKGTool.Utilities.TrophyMetadata
{
    public sealed class NpbindExtractionResult
    {
        public string? NpCommunicationId { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public bool Succeeded => NpCommunicationId != null;
    }

    /// <summary>Reads Sc0/npbind.dat in-process via OrbisPkgTool.PkgReader and
    /// scans it for the NPWRxxxxx_00 token. No orbis-pub-cmd spawn, no temp
    /// file, no ASCII-safe staging - the PKG is opened read-only and the entry
    /// bytes are decrypted directly.</summary>
    public sealed class NpbindExtractor
    {
        private const string DefaultPasscode = "00000000000000000000000000000000";
        private readonly NpCommunicationIdResolver _resolver = new();

        public async Task<NpbindExtractionResult> ExtractAsync(
            string pkgPath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(pkgPath))
                return Failure("未找到所选的 PKG。");

            try
            {
                byte[] npbindBytes = await Task.Run(() =>
                {
                    using var reader = new OrbisPkgTool.PkgReader(pkgPath, DefaultPasscode);
                    return reader.ExtractEntryBytes("Sc0/npbind.dat");
                }, cancellationToken).ConfigureAwait(false);

                string? id = _resolver.ResolveFromBytes(npbindBytes);
                return id == null
                    ? Failure("已读取 npbind.dat，但未找到有效的 NPWRxxxxx_00 值。")
                    : new NpbindExtractionResult { NpCommunicationId = id };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure("NP Communication ID 提取已取消。");
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        private static NpbindExtractionResult Failure(string message) =>
            new() { ErrorMessage = message };
    }
}
