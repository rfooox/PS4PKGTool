using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PS4PKGTool.Utilities.PS4PKGToolHelper;

namespace PS4PKGTool.Utilities.Shadps4
{
    public enum Shadps4SetupStatus
    {
        Success,
        Cancelled,
        AlreadyInstalled,
        InsufficientSpace,
        DownloadFailed,
        VerificationFailed,
        ExtractionFailed,
        Failed,
    }

    public sealed record Shadps4SetupResult(
        Shadps4SetupStatus Status,
        string Message,
        Shadps4InstalledBuild? Build = null);

    /// <summary>
    /// Transactional managed-build installation (spec pipeline):
    ///   resolve asset → free-space check → download to .part → size/hash
    ///   verification → ZIP validation (incl. uncompressed-size estimate) →
    ///   extract to staging → expected-exe verification → atomic commit →
    ///   manifest. Nothing about the active core/launcher is changed here -
    ///   the caller only makes a build active AFTER a Success result, so a
    ///   failed install can never alter the active setup.
    /// Cancellation cleans the .part file and the staging directory; old
    /// builds are never touched.
    /// </summary>
    public sealed class Shadps4SetupService
    {
        /// <summary>Margin on top of the archive sizes (download + extraction).</summary>
        public const long SpaceMarginBytes = 512L * 1024 * 1024;

        public Shadps4Downloader Downloader { get; set; } = new();

        /// <summary>Test seam: free bytes on the managed root volume. (rootPath) -> bytes.</summary>
        public Func<string, long>? FreeSpaceOverride { get; set; }

        public async Task<Shadps4SetupResult> InstallBuildAsync(
            Shadps4ReleaseInfo release,
            Shadps4ManagedBuilds store,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            Shadps4Component component = release.Feed == Shadps4FeedKind.QtLauncher
                ? Shadps4Component.QtLauncher
                : Shadps4Component.Core;
            Logger.LogInformation($"Shadps4Setup: installing {release.AssetName} ({release.Feed}, commit {release.Commit})");

            // Same build already installed - never duplicate or overwrite.
            if (store.FindExe(component, release.BuildId) != null)
            {
                Logger.LogInformation($"Shadps4Setup：版本 {release.BuildId} 已安装——跳过。");
                return new Shadps4SetupResult(Shadps4SetupStatus.AlreadyInstalled,
                    $"版本 {release.BuildId} 已安装。");
            }

            string root = store.RootPath;
            string workDir = Path.Combine(root, ".work");
            string zipPath = Path.Combine(workDir, "shadps4_download.zip");
            string staging = Path.Combine(root, "builds", ".staging-" + release.BuildId);

            try
            {
                // 1) free space for the download.
                long free = FreeSpaceOverride != null
                    ? FreeSpaceOverride(root)
                    : FreeSpace(root);
                if (release.SizeBytes > 0 && free < release.SizeBytes + SpaceMarginBytes)
                {
                    Logger.LogWarning($"Shadps4Setup：空间不足以下载（需要 {HelperBytes(release.SizeBytes + SpaceMarginBytes)}，可用 {HelperBytes(free)}）");
                    return new Shadps4SetupResult(Shadps4SetupStatus.InsufficientSpace,
                        $"剩余空间不足以下载 {release.AssetName}（需要约 {HelperBytes(release.SizeBytes + SpaceMarginBytes)}，可用 {HelperBytes(free)}）。");
                }

                // 2) streamed download to .part, then rename.
                progress?.Report($"Downloading {release.AssetName}...");
                var bytes = new Progress<(long Done, long? Total)>(p =>
                {
                    long doneMb = p.Done / 1024 / 1024;
                    string total = p.Total is > 0 ? $" / {p.Total / 1024 / 1024} MB" : "";
                    progress?.Report($"Downloading... {doneMb} MB{total}");
                });
                string? dlError = await Downloader.DownloadAsync(
                    release.AssetUrl, zipPath, release.SizeBytes > 0 ? release.SizeBytes : null,
                    release.AssetDigestSha256, bytes, ct).ConfigureAwait(false);
                if (dlError != null)
                {
                    Logger.LogWarning($"Shadps4Setup：下载失败：{dlError}");
                    return new Shadps4SetupResult(Shadps4SetupStatus.DownloadFailed, dlError);
                }
                Logger.LogInformation($"Shadps4Setup: downloaded {release.AssetName}");

                // 3) validate the ZIP and estimate the extracted size.
                progress?.Report("正在校验压缩包……");
                long uncompressed;
                try
                {
                    using var zipStream = File.OpenRead(zipPath);
                    uncompressed = SafeZipExtractor.TotalUncompressedSize(zipStream);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Shadps4Setup：压缩包无效或已截断：{ex.Message}");
                    return new Shadps4SetupResult(Shadps4SetupStatus.VerificationFailed,
                        $"下载的压缩包无效或已截断：{ex.Message}");
                }

                // 4) free space for the extraction.
                free = FreeSpaceOverride != null ? FreeSpaceOverride(root) : FreeSpace(root);
                if (free < uncompressed + SpaceMarginBytes)
                {
                    Logger.LogWarning($"Shadps4Setup：空间不足以解压（需要 {HelperBytes(uncompressed + SpaceMarginBytes)}，可用 {HelperBytes(free)}）");
                    return new Shadps4SetupResult(Shadps4SetupStatus.InsufficientSpace,
                        $"剩余空间不足以解压 {release.AssetName}（需要约 {HelperBytes(uncompressed + SpaceMarginBytes)}，可用 {HelperBytes(free)}）。");
                }

                // 5) extract to staging (same volume as the final dir).
                progress?.Report("Extracting...");
                try
                {
                    using var zipStream = File.OpenRead(zipPath);
                    SafeZipExtractor.Extract(zipStream, staging);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Shadps4Setup：解压失败：{ex.Message}");
                    return new Shadps4SetupResult(Shadps4SetupStatus.ExtractionFailed,
                        $"解压失败：{ex.Message}");
                }

                // 6) verify the expected executable + atomic commit.
                progress?.Report("正在完成构建……");
                Shadps4InstalledBuild build;
                try
                {
                    build = store.Commit(release, staging);
                }
                catch (InvalidOperationException ex)
                {
                    Logger.LogWarning($"Shadps4Setup：提交被拒绝：{ex.Message}");
                    return new Shadps4SetupResult(Shadps4SetupStatus.VerificationFailed, ex.Message);
                }
                Logger.LogInformation($"Shadps4Setup：已将版本 {build.BuildId} 安装到 {build.DirectoryPath}");

                return new Shadps4SetupResult(Shadps4SetupStatus.Success,
                    $"已将 {release.AssetName} 安装为版本 {build.BuildId}。", build);
            }
            catch (OperationCanceledException)
            {
                CleanupWork(workDir, staging);
                Logger.LogWarning("Shadps4Setup：已取消——工作目录与暂存已清理。");
                return new Shadps4SetupResult(Shadps4SetupStatus.Cancelled, "安装已取消。");
            }
            catch (IOException ex) when ((uint)ex.HResult == 0x80070070) // ERROR_DISK_FULL
            {
                CleanupWork(workDir, staging);
                Logger.LogError("Shadps4Setup：磁盘空间不足——" + ex.Message);
                return new Shadps4SetupResult(Shadps4SetupStatus.InsufficientSpace,
                    $"安装时磁盘空间不足：{ex.Message}");
            }
            catch (Exception ex)
            {
                CleanupWork(workDir, staging);
                Logger.LogError("Shadps4Setup 失败：" + ex);
                return new Shadps4SetupResult(Shadps4SetupStatus.Failed, ex.Message);
            }
            finally
            {
                CleanupWork(workDir, staging);
            }
        }

        private static long FreeSpace(string root)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
            }
            catch
            {
                return long.MaxValue; // unknown - let the extraction surface the real error
            }
        }

        private static void CleanupWork(string workDir, string staging)
        {
            // Safety-net cleanup: a leftover dir never blocks the next install.
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { /* best-effort */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best-effort */ }
        }

        private static string HelperBytes(long bytes)
            => bytes >= 1024 * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
                : $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }
}
