using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PS4PKGTool.Utilities.Shadps4
{
    public sealed record Shadps4SaveSlot(
        string Name, long Size, DateTime ModifiedUtc, bool RecognizedSlot);

    public sealed record Shadps4SaveGame(
        string UserId, string TitleId, string Path,
        IReadOnlyList<Shadps4SaveSlot> Slots, long TotalSize, DateTime ModifiedUtc, string? Title);

    public sealed record Shadps4SaveBackup(
        string SnapshotId, string TitleId, string UserId,
        DateTime BackedUpAtUtc, bool LiveUnverified, string Path);

    public enum Shadps4SaveRestoreStatus { Restored, RefusedRunning, SpaceInsufficient, NotFound, Failed }

    public sealed record Shadps4SaveRestoreResult(
        Shadps4SaveRestoreStatus Status, string Message, Shadps4SaveBackup? SafetyBackup);

    /// <summary>
    /// Browse, backup and restore shadPS4 savegames. Save layout (verified on
    /// a real machine): &lt;userDir&gt;\home\&lt;userID&gt;\savedata\&lt;TitleID&gt;\&lt;slot&gt;\...
    /// Saves are plain files, no encryption; backup = copy, restore = swap.
    ///
    /// Restore is journaled and crash-recoverable: staging and the swap use
    /// dot-prefixed sibling directories (".&lt;TitleID&gt;.restore" /
    /// ".&lt;TitleID&gt;.previous") on the same volume, and an interrupted swap
    /// is repaired by RecoverInterruptedRestore on the next Saves-tab visit.
    /// The current save is auto-backed up before any replacement; a failed
    /// auto-backup aborts the restore with the current save untouched.
    ///
    /// Read-only toward shadPS4 except for the restore swap itself, which is
    /// the feature's purpose. Backup snapshots live under the managed backup
    /// root (&lt;root&gt;\saves\&lt;userId&gt;\&lt;TitleID&gt;\&lt;snapshotId&gt;\) and only
    /// count as managed when they carry a valid manifest.
    /// </summary>
    public sealed class Shadps4SaveStore
    {
        private static readonly Regex CusaId = new(@"^CUSA\d{5}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StagingName = new(@"^\.(CUSA\d{5})\.(previous|restore)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const long SpaceMarginBytes = 64L * 1024 * 1024;
        private const string SavesFolderName = "saves";
        private const string DataFolderName = "data";

        private readonly IClock _clock;
        private readonly IFileSystemOps _fs;
        private readonly Func<string, string?> _titleResolver;

        public Shadps4SaveStore(IClock clock, IFileSystemOps fs, Func<string, string?>? titleResolver = null)
        {
            _clock = clock;
            _fs = fs;
            _titleResolver = titleResolver ?? (_ => null);
        }

        /// <summary>Test seam: is the emulator core running (saves are write-live while it is).</summary>
        public Func<bool> IsCoreRunning { get; set; } = () => Shadps4Launcher.IsCoreRunning();

        // ── paths ────────────────────────────────────────────────────────

        public static string SavesRoot(string userDir) => Path.Combine(userDir, "home");
        public static string UserSavesDir(string userDir, string userId) => Path.Combine(SavesRoot(userDir), userId, "savedata");
        public static string GameSavePath(string userDir, string userId, string titleId) => Path.Combine(UserSavesDir(userDir, userId), titleId);
        public static string GameBackupsDir(string backupRoot, string userId, string titleId) => Path.Combine(backupRoot, SavesFolderName, userId, titleId);

        // ── browse ───────────────────────────────────────────────────────

        /// <summary>
        /// All savegames across ALL emulated users that have a savedata
        /// folder. Orphan saves (game not installed) are included - the save
        /// filesystem is the source of truth. Fault-tolerant: a bad param.sfo
        /// only loses the title, one unreadable file never hides the game.
        /// </summary>
        public IReadOnlyList<Shadps4SaveGame> ListSaveGames(string userDir)
        {
            var games = new List<Shadps4SaveGame>();
            string root = SavesRoot(userDir);
            if (!_fs.DirectoryExists(root)) return games;

            foreach (string userPath in SafeEnumerateDirectories(root))
            {
                string userId = Path.GetFileName(userPath);
                string savedata = Path.Combine(userPath, "savedata");
                if (!_fs.DirectoryExists(savedata)) continue;

                foreach (string gamePath in SafeEnumerateDirectories(savedata))
                {
                    string titleId = Path.GetFileName(gamePath);
                    var slots = new List<Shadps4SaveSlot>();
                    string? resolvedTitle = null;
                    try
                    {
                        foreach (string entry in SafeEnumerateFileSystemEntries(gamePath))
                        {
                            string name = Path.GetFileName(entry);
                            if (Directory.Exists(entry))
                            {
                                string sfoPath = Path.Combine(entry, "sce_sys", "param.sfo");
                                bool recognized = _fs.FileExists(sfoPath);
                                if (recognized && resolvedTitle == null)
                                    resolvedTitle = _titleResolver(sfoPath);
                                slots.Add(new Shadps4SaveSlot(name, SafeSize(entry), SafeModifiedUtc(entry), recognized));
                            }
                            else
                            {
                                slots.Add(new Shadps4SaveSlot(name, SafeFileSize(entry), SafeModifiedUtc(entry), false));
                            }
                        }
                    }
                    catch { /* unreadable game folder - degrade, never hide */ }

                    games.Add(new Shadps4SaveGame(
                        userId, titleId, gamePath, slots,
                        SafeSize(gamePath), SafeModifiedUtc(gamePath),
                        string.IsNullOrWhiteSpace(resolvedTitle) ? null : resolvedTitle));
                }
            }

            return games
                .OrderBy(g => g.TitleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.UserId, StringComparer.Ordinal)
                .ToList();
        }

        // ── backup ───────────────────────────────────────────────────────

        /// <summary>
        /// Copies the game's save folder into a timestamped snapshot under the
        /// managed backup root. liveUnverified marks snapshots captured while
        /// the emulator runs (files may mix states); the manifest records it.
        /// Returns null when the source save does not exist or the copy fails
        /// (in which case no manifest is written, so no backup is registered).
        /// </summary>
        public Shadps4SaveBackup? BackupSave(string userDir, string userId, string titleId, string backupRoot, bool liveUnverified)
        {
            string source = GameSavePath(userDir, userId, titleId);
            if (!_fs.DirectoryExists(source)) return null;

            string snapshotId = $"{_clock.UtcNow:yyyyMMdd'T'HHmmss'Z'}_{Guid.NewGuid().ToString("N")[..6]}";
            string snapshotDir = Path.Combine(GameBackupsDir(backupRoot, userId, titleId), snapshotId);
            string dataDir = Path.Combine(snapshotDir, DataFolderName);

            _fs.CopyDirectory(source, dataDir);

            Shadps4SaveManifest.Write(Path.Combine(snapshotDir, "manifest.json"), new Shadps4SaveManifest
            {
                SchemaVersion = Shadps4SaveManifest.CurrentSchemaVersion,
                SnapshotId = snapshotId,
                TitleId = titleId,
                UserId = userId,
                BackedUpAtUtc = _clock.UtcNow,
                OriginalSourcePath = source,
                Consistency = liveUnverified ? Shadps4SaveManifest.ConsistencyLiveUnverified : Shadps4SaveManifest.ConsistencyOffline,
            });

            return new Shadps4SaveBackup(snapshotId, titleId, userId, _clock.UtcNow, liveUnverified, snapshotDir);
        }

        /// <summary>
        /// Managed snapshots for a game, newest first. Only folders with a
        /// valid manifest count; ordering uses the manifest timestamp, never
        /// folder names.
        /// </summary>
        public IReadOnlyList<Shadps4SaveBackup> ListBackups(string backupRoot, string userId, string titleId)
        {
            var backups = new List<Shadps4SaveBackup>();
            string root = GameBackupsDir(backupRoot, userId, titleId);
            if (!_fs.DirectoryExists(root)) return backups;

            foreach (string dir in SafeEnumerateDirectories(root))
            {
                var manifest = Shadps4SaveManifest.TryLoad(Path.Combine(dir, "manifest.json"));
                if (manifest == null) continue; // invalid or foreign folders are never restore sources
                backups.Add(new Shadps4SaveBackup(
                    manifest.SnapshotId, manifest.TitleId, manifest.UserId,
                    manifest.BackedUpAtUtc,
                    manifest.Consistency == Shadps4SaveManifest.ConsistencyLiveUnverified,
                    dir));
            }

            return backups
                .OrderByDescending(b => b.BackedUpAtUtc)
                .ThenByDescending(b => b.SnapshotId, StringComparer.Ordinal)
                .ToList();
        }

        // ── restore (journaled, crash-recoverable) ────────────────────────

        /// <summary>
        /// Restores a snapshot over the live save using the journaled
        /// sequence:
        ///   validate manifest and containment
        ///   check emulator stopped (check 1)
        ///   check space on both volumes
        ///   auto-backup current (hard abort on failure)
        ///   stage copy to ".&lt;TitleID&gt;.restore" beside the target
        ///   check emulator stopped again (check 2, before any rename)
        ///   current → ".&lt;TitleID&gt;.previous"
        ///   ".restore" → current, verify, delete ".previous"
        /// Any failure before the first rename leaves the current save
        /// untouched; a failure after it rolls back synchronously; a crash
        /// mid-swap is repaired by RecoverInterruptedRestore.
        /// </summary>
        public Shadps4SaveRestoreResult RestoreSave(string userDir, string userId, string titleId, Shadps4SaveBackup backup, string backupRoot)
        {
            if (!string.Equals(backup.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(backup.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                    "该快照不属于此存档。未做任何更改。", null);

            // Snapshot identity comes from the manifest, but the actual files
            // are always resolved from the known snapshot directory - never
            // from anything stored inside the manifest.
            string gamesRoot = GameBackupsDir(backupRoot, userId, titleId);
            if (!IsUnder(gamesRoot, backup.Path))
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                    "快照位于托管备份根目录之外。未做任何更改。", null);
            string dataDir = Path.Combine(backup.Path, DataFolderName);
            if (!_fs.DirectoryExists(dataDir))
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.NotFound,
                    "快照内容缺失。未做任何更改。", null);

            string live = GameSavePath(userDir, userId, titleId);
            string staging = Path.Combine(UserSavesDir(userDir, userId), $".{titleId}.restore");
            string previous = Path.Combine(UserSavesDir(userDir, userId), $".{titleId}.previous");
            bool liveExists = _fs.DirectoryExists(live);

            // Check 1.
            if (IsCoreRunning())
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.RefusedRunning,
                    "shadPS4 正在运行。请先关闭它再还原存档。未做任何更改。", null);

            // Space preflight: the swap briefly holds current + snapshot on the
            // live volume; the auto-backup is a full copy on the backup volume.
            long snapshotSize = _fs.GetDirectorySize(dataDir);
            long currentSize = liveExists ? _fs.GetDirectorySize(live) : 0;
            long neededLive = snapshotSize + currentSize + SpaceMarginBytes;
            long neededBackup = currentSize + SpaceMarginBytes;
            if (_fs.GetFreeSpace(live) < neededLive || _fs.GetFreeSpace(backupRoot) < neededBackup)
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.SpaceInsufficient,
                    "磁盘剩余空间不足以安全还原。未做任何更改。", null);

            // Auto-backup the current save - a hard precondition.
            Shadps4SaveBackup? safety = null;
            if (liveExists)
            {
                try
                {
                    safety = BackupSave(userDir, userId, titleId, backupRoot, liveUnverified: false);
                }
                catch (Exception ex)
                {
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                        "当前存档的安全备份失败：\n" + ex.Message +
                        "\n\n当前存档未被改动。", null);
                }
                if (safety == null)
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                        "当前存档的安全备份失败。当前存档未被改动。", null);
            }

            try
            {
                // Stage beside the target (same volume) - never from the
                // backup root, which may live on another volume.
                if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging);
                _fs.CopyDirectory(dataDir, staging);
                if (!_fs.DirectoryExists(staging))
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                        "暂存还原后的存档失败。当前存档未被改动。", safety);

                // Check 2 - immediately before the destructive rename.
                if (IsCoreRunning())
                {
                    try { _fs.DeleteDirectory(staging); } catch { /* best-effort: leftover staging self-heals on next init */ }
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.RefusedRunning,
                        "还原过程中 shadPS4 已启动。当前存档未被改动。", safety);
                }

                // The swap.
                if (liveExists)
                {
                    if (_fs.DirectoryExists(previous)) _fs.DeleteDirectory(previous);
                    _fs.MoveDirectory(live, previous);
                }
                try
                {
                    _fs.MoveDirectory(staging, live);
                }
                catch (Exception ex)
                {
                    // Roll back synchronously. If even the rollback fails, the
                    // previous save stays stranded in the .previous sibling -
                    // RecoverInterruptedRestore picks it up on the next init.
                    string rollbackNote = "";
                    try
                    {
                        if (liveExists && _fs.DirectoryExists(previous))
                        {
                            _fs.MoveDirectory(previous, live);
                            rollbackNote = " 已恢复上一个存档。";
                        }
                    }
                    catch { /* best-effort: stranded .previous self-heals via the journal */ }
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                        "恢复失败：\n" + ex.Message + rollbackNote, safety);
                }

                if (!_fs.DirectoryExists(live))
                {
                    // Verification failed - put the previous save back.
                    string rollbackNote = "";
                    try
                    {
                        if (liveExists && _fs.DirectoryExists(previous))
                        {
                            _fs.MoveDirectory(previous, live);
                            rollbackNote = " 已恢复上一个存档。";
                        }
                    }
                    catch { /* best-effort: stranded .previous self-heals via the journal */ }
                    return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                        "无法校验还原后的存档。" + rollbackNote, safety);
                }

                // The swap is complete - the previous save is no longer needed.
                try { if (_fs.DirectoryExists(previous)) _fs.DeleteDirectory(previous); } catch { /* best-effort: leftover .previous self-heals on next init */ }
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Restored,
                    "存档已还原。还原前已备份上一个存档。", safety);
            }
            catch (Exception ex)
            {
                // A failure before the first rename leaves the current save
                // untouched; discard any partial staging so it self-heals.
                try { if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging); } catch { /* best-effort cleanup */ }
                return new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                    "恢复失败：\n" + ex.Message + "\n\n当前存档未被改动。", safety);
            }
        }

        // ── interrupted-restore recovery ─────────────────────────────────

        /// <summary>
        /// Repairs a restore interrupted by a process or machine crash. The
        /// dot-prefixed staging/previous directories ARE the journal: their
        /// combination with the canonical folder encodes the interrupted
        /// phase. Runs on Saves-tab initialization. Returns a human-readable
        /// report ("" when nothing needed repair).
        /// </summary>
        public string RecoverInterruptedRestore(string userDir)
        {
            var report = new List<string>();
            string root = SavesRoot(userDir);
            if (!_fs.DirectoryExists(root)) return "";

            foreach (string userPath in SafeEnumerateDirectories(root))
            {
                string savedata = Path.Combine(userPath, "savedata");
                if (!_fs.DirectoryExists(savedata)) continue;

                foreach (string entry in SafeEnumerateDirectories(savedata))
                {
                    var match = StagingName.Match(Path.GetFileName(entry));
                    if (!match.Success) continue; // never touch unrelated directories
                    string titleId = match.Groups[1].Value;
                    string live = Path.Combine(savedata, titleId);
                    string staging = Path.Combine(savedata, $".{titleId}.restore");
                    string previous = Path.Combine(savedata, $".{titleId}.previous");
                    bool liveExists = _fs.DirectoryExists(live);
                    bool stagingExists = _fs.DirectoryExists(staging);
                    bool previousExists = _fs.DirectoryExists(previous);

                    try
                    {
                        if (previousExists)
                        {
                            if (!liveExists)
                            {
                                // Crashed between "current → previous" and
                                // ".restore → current": complete the swap.
                                if (stagingExists)
                                {
                                    _fs.MoveDirectory(staging, live);
                                    report.Add($"已完成 {titleId} 被中断的还原。");
                                }
                                else
                                {
                                    _fs.MoveDirectory(previous, live);
                                    report.Add($"已回滚 {titleId} 被中断的还原。");
                                }
                                if (_fs.DirectoryExists(previous)) _fs.DeleteDirectory(previous);
                            }
                            else
                            {
                                // The swap finished; only cleanup was pending.
                                if (stagingExists) _fs.DeleteDirectory(staging);
                                _fs.DeleteDirectory(previous);
                                report.Add($"已清理 {titleId} 已完成的还原。");
                            }
                        }
                        else if (stagingExists)
                        {
                            // Stale staging; the current save was never touched.
                            _fs.DeleteDirectory(staging);
                            report.Add($"已丢弃 {titleId} 未完成的暂存。你的存档完好无损。");
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Add($"无法完成 {titleId} 的恢复：{ex.Message}");
                    }
                }
            }
            return string.Join("\n", report);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static bool IsUnder(string baseDir, string candidate)
        {
            string b = Path.GetFullPath(baseDir).TrimEnd('\\');
            string c = Path.GetFullPath(candidate);
            return c.Equals(b, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeEnumerateFileSystemEntries(string path)
        {
            try { return Directory.EnumerateFileSystemEntries(path).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        private long SafeSize(string path)
        {
            try { return _fs.GetDirectorySize(path); }
            catch { return 0; }
        }

        private long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static DateTime SafeModifiedUtc(string path)
        {
            try { return Directory.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }
    }
}
