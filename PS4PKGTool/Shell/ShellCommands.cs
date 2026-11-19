using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS4PKGTool.Startup;
using PS4PKGTool.Util.Constants;
using PS4PKGTool.Utilities;
using PS4PKGTool.Utilities.Constants;
using PS4PKGTool.Utilities.PkgInspection;
using PS4PKGTool.Utilities.PS4PKGToolHelper;
using PS4PKGTool.Utilities.Settings;
using PS4PKGTool.Utilities.Shadps4;

namespace PS4PKGTool.Shell
{
    /// <summary>
    /// Executes a shell request (Explorer right-click integration) against
    /// the EXISTING PS4PKGTool services - no second pipelines, no main form,
    /// no library scan. Only what the requested command needs is touched.
    /// </summary>
    public static class ShellCommands
    {
        /// <summary>Entry point; runs on the UI thread with a live message pump.</summary>
        public static void Run(ShellRequest request)
        {
            string? pathError = ShellCommandRouter.ValidatePaths(request.PackagePaths);
            if (pathError != null)
            {
                MessageBoxHelper.ShowError(pathError, false);
                return;
            }

            switch (request.Kind)
            {
                case ShellCommandKind.Copy:
                    RunCopy(request.Field, request.PackagePaths);
                    break;
                case ShellCommandKind.Rename:
                    RunRename(request.FormatId, request.PackagePaths);
                    break;
                case ShellCommandKind.Validate:
                    RunValidate(request.PackagePaths);
                    break;
                case ShellCommandKind.Extract:
                    RunExtract(request.PackagePaths);
                    break;
                case ShellCommandKind.InstallShadps4:
                    RunInstallShadps4(request.PackagePaths);
                    break;
            }
        }

        /// <summary>Actions that currently accept one package at a time show this instead of ignoring the rest.</summary>
        private static bool GuardSinglePackage(IReadOnlyList<string> paths)
        {
            if (paths.Count <= 1) return true;
            MessageBoxHelper.ShowInformation(
                "此操作当前一次只支持一个 PKG。\n\n其他选中的文件未被处理。", false);
            return false;
        }

        // â”€â”€ metadata â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static PkgInspectionSnapshot? ReadMetadata(string path)
        {
            try
            {
                return new PkgInspectionService()
                    .InspectAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Shell：无法读取 {path} 的 PKG 元数据：{ex.Message}");
                return null;
            }
        }

        // â”€â”€ Copy â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RunCopy(string field, IReadOnlyList<string> paths)
        {
            var parts = new List<string>();
            foreach (string path in paths)
            {
                var meta = ReadMetadata(path);
                if (meta == null)
                {
                    MessageBoxHelper.ShowError(
                        "无法读取 PKG 元数据。\n\n" + path, false);
                    return;
                }
                parts.Add(ExtractField(field, meta));
            }

            string text = field == "info" && parts.Count == 1 ? parts[0] : string.Join(Environment.NewLine, parts);
            try { Clipboard.SetText(text); }
            catch (Exception ex)
            {
                Logger.LogWarning("Shell：写入剪贴板失败：" + ex.Message);
                MessageBoxHelper.ShowError("无法写入剪贴板。", false);
                return;
            }

            string label = parts.Count == 1 ? parts[0] : parts.Count + " 个值";
            using (var confirm = new ShellConfirmForm(
                (field == "info" ? "PKG 信息已复制" : CopyFieldLabel(field) + " 已复制") + "：\n" + Truncate(label, 60)))
                confirm.ShowDialog();
        }

        internal static string ExtractField(string field, PkgInspectionSnapshot meta) => field switch
        {
            "info" => BuildPackageInfo(meta),
            "title" => meta.Title,
            "titleid" => meta.TitleId,
            "contentid" => meta.ContentId,
            "version" => meta.PackageVersion,
            "appversion" => meta.ApplicationVersion,
            "fullpath" => meta.PackagePath,
            _ => meta.PackagePath,
        };

        internal static string BuildPackageInfo(PkgInspectionSnapshot m)
        {
            var lines = new List<string>
            {
                "标题：" + m.Title,
                "标题 ID：" + m.TitleId,
                "内容 ID：" + m.ContentId,
                "版本：" + m.PackageVersion,
                "应用版本：" + m.ApplicationVersion,
                "类型：" + m.PackageCategory,
                "大小：" + m.PackageSize,
                "路径：" + m.PackagePath,
            };
            return string.Join(Environment.NewLine, lines);
        }

        private static string CopyFieldLabel(string field) => field switch
        {
            "title" => "标题",
            "titleid" => "标题 ID",
            "contentid" => "内容 ID",
            "version" => "版本",
            "appversion" => "应用版本",
            "fullpath" => "完整路径",
            _ => "值",
        };

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max) + "...";

        // â”€â”€ Rename â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RunRename(int formatId, IReadOnlyList<string> paths)
        {
            // Custom format may come from settings or a small dialog - the
            // same placeholder language as the main app.
            string? format = PkgRenameFormats.GetFormat(formatId, SettingsManager.appSettings_.RenameCustomName);
            if (string.IsNullOrWhiteSpace(format))
            {
                if (formatId == PkgRenameFormats.CustomFormatId)
                {
                    using var dlg = new ShellRenameDialog(SettingsManager.appSettings_.RenameCustomName ?? "");
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    format = dlg.FormatValue;
                    SettingsManager.appSettings_.RenameCustomName = format;
                    SettingsManager.SaveSettings(SettingsManager.appSettings_, SettingsManager.SettingFilePath);
                }
                else
                {
                    MessageBoxHelper.ShowWarning("重命名格式不可用。", false);
                    return;
                }
            }

            string? lastRenamed = null;
            foreach (string path in paths)
            {
                string? renamed = RenameOne(path, format);
                if (renamed == null) return; // error already reported
                lastRenamed = renamed;
            }

            if (lastRenamed != null)
            {
                using var confirm = new ShellConfirmForm(
                    (paths.Count > 1 ? paths.Count + " 个 PKG 已重命名" : "重命名为") + "：\n" + Path.GetFileName(lastRenamed));
                confirm.ShowDialog();
            }
        }

        /// <summary>Renames one package via the same engine as the Mini Viewer; never overwrites silently.</summary>
        private static string? RenameOne(string sourcePath, string format)
        {
            try
            {
                string destinationFolder = Path.GetDirectoryName(sourcePath) + @"\";
                (_, _, string targetPkg) =
                    PS4PKGTool.Utilities.PkgRename.PkgRenameService.GetNewPKGName(sourcePath, destinationFolder, format);

                if (File.Exists(targetPkg))
                {
                    MessageBoxHelper.ShowWarning(
                        "已存在同名文件，该 PKG 未被重命名。\n\n" +
                        Path.GetFileName(targetPkg), false);
                    return null;
                }

                File.Move(sourcePath, targetPkg);
                Logger.LogInformation($"Shell 重命名：{Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPkg)}");
                return targetPkg;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Shell 重命名失败：" + ex.Message);
                MessageBoxHelper.ShowError("无法重命名该 PKG：" + ex.Message, false);
                return null;
            }
        }

        // â”€â”€ Validate â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RunValidate(IReadOnlyList<string> paths)
        {
            if (!GuardSinglePackage(paths)) return;
            string path = paths[0];
            string fileName = Path.GetFileName(path);

            // The passcode for this validation. Starts as the standard
            // all-zero default; a rejected code prompts for the real one and
            // retries (max 5 attempts - a typo'd custom passcode re-prompts).
            string passcode = PkgFileListingService.DefaultPasscode;

            ShellOperationForm.Run("正在验证 PKG", fileName, ReadMetaLine(path), null,
                (progress, ct) => Task.Run(async () =>
                {
                    progress.Report(new ShellOperationProgress("正在读取 PKG 元数据……"));
                    var meta = await new PkgInspectionService()
                        .InspectAsync(path, ct).ConfigureAwait(false);

                    progress.Report(new ShellOperationProgress("正在检查 PKG 结构……"));
                    var listing = await new PkgFileListingService()
                        .ListAsync(path, passcode, ct).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (listing.Succeeded || listing.RestoreFailed
                            || !IsPasscodeFailure(listing.ErrorMessage))
                            break;

                        progress.Report(new ShellOperationProgress("需要通行密码……"));
                        string? custom = await PromptForPasscodeAsync().ConfigureAwait(false);
                        if (custom == null)
                            return ShellOperationResult.CancelledResult(
                                "验证已取消。该 PKG 需要通行密码。"); // prompt cancelled - close all

                        passcode = custom;
                        progress.Report(new ShellOperationProgress("正在检查 PKG 结构……"));
                        listing = await new PkgFileListingService()
                            .ListAsync(path, passcode, ct).ConfigureAwait(false);
                    }

                    if (listing.Succeeded)
                    {
                        string message =
                            $"{fileName}\n\n警告：0\n错误：0\n\n标题：{meta.Title}\n" +
                            $"标题 ID：{meta.TitleId}\n条目：{listing.Entries.Count}";
                        return new ShellOperationResult(true, message, listing.MalformedLineCount > 0
                            ? $"格式错误的清单行：{listing.MalformedLineCount}" : null);
                    }
                    return new ShellOperationResult(false, "验证失败：\n" + listing.ErrorMessage);
                }, ct));
        }

        /// <summary>orbis-pub-cmd reports a rejected passcode in its error
        /// output - same detection as the Mini Viewer.</summary>
        internal static bool IsPasscodeFailure(string errorMessage) =>
            !string.IsNullOrWhiteSpace(errorMessage) &&
            errorMessage.IndexOf("passcode", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Shows the passcode prompt over the running shell operation window.
        /// Returns the entered passcode, PkgFileListingService.NoPasscode for
        /// the "no passcode" option, or null when cancelled.
        /// </summary>
        private static async Task<string?> PromptForPasscodeAsync()
        {
            try
            {
                return await ShellOperationForm.RunOnUiAsync(() =>
                {
                    using var prompt = new PasscodePromptForm();
                    if (prompt.ShowDialog() != DialogResult.OK)
                        return null;
                    if (prompt.IsNoPasscode)
                        return PkgFileListingService.NoPasscode;
                    return string.IsNullOrWhiteSpace(prompt.Passcode) ? null : prompt.Passcode;
                }).ConfigureAwait(false);
            }
            catch
            {
                return null; // a failed prompt must not kill the operation reporting
            }
        }

        // â”€â”€ Extract â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RunExtract(IReadOnlyList<string> paths)
        {
            if (!GuardSinglePackage(paths)) return;
            string path = paths[0];

            var meta = ReadMetadata(path);
            string title = meta?.Title ?? Path.GetFileNameWithoutExtension(path);

            using var fbd = new FolderBrowserDialog
            {
                Description = "选择 PKG 的解压目标文件夹。",
                ShowNewFolderButton = true,
            };
            if (fbd.ShowDialog() != DialogResult.OK) return;

            string extractLocation = Path.Combine(fbd.SelectedPath, PkgExtractionService.SanitizeFolderName(title));
            string fileName = Path.GetFileName(path);

            // Passcode for this extraction: default all-zero first; when the
            // package rejects it, prompt and retry. A wrong custom passcode
            // re-prompts (max 5 attempts) so a typo does not end the run.
            string passcode = PkgFileListingService.DefaultPasscode;

            ShellOperationForm.Run("正在提取 PKG", title, MetaLine(meta), extractLocation,
                (progress, ct) => Task.Run(async () =>
                {
                    var p = new Progress<string>(s => progress.Report(new ShellOperationProgress(s)));
                    var fileProgress = new Progress<(int Current, int Total, string CurrentFile)>(entry =>
                    {
                        if (entry.Total <= 0)
                            return;

                        int completed = Math.Min(entry.Current + 1, entry.Total);
                        int percent = (int)Math.Round(completed * 100.0 / entry.Total);
                        progress.Report(new ShellOperationProgress(
                            $"正在提取 {completed}/{entry.Total}：{entry.CurrentFile}", percent));
                    });
                    var (succeeded, message) = await new PkgExtractionService(passcode: passcode)
                        .ExtractFullAsync(path, extractLocation, p, ct, fileProgress, logCompletion: true,
                            layout: ExtractLayout.Merged).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (succeeded || !IsPasscodeFailure(message))
                            break;

                        progress.Report(new ShellOperationProgress("需要通行密码……"));
                        string? custom = await PromptForPasscodeAsync().ConfigureAwait(false);
                        if (custom == null)
                            return ShellOperationResult.CancelledResult(
                                "提取已取消。该 PKG 需要通行密码。"); // prompt cancelled - close all

                        passcode = custom;
                        progress.Report(new ShellOperationProgress("正在提取游戏文件……"));
                        (succeeded, message) = await new PkgExtractionService(passcode: passcode)
                            .ExtractFullAsync(path, extractLocation, p, ct, fileProgress, logCompletion: true,
                                layout: ExtractLayout.Merged).ConfigureAwait(false);
                    }

                    if (succeeded)
                    {
                        string detail = string.IsNullOrWhiteSpace(message)
                            ? "提取完成"
                            : "提取完成。" + message;
                        return new ShellOperationResult(true, detail, null, extractLocation);
                    }
                    return new ShellOperationResult(false, message);
                }, ct));
        }

        // â”€â”€ Install to shadPS4 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RunInstallShadps4(IReadOnlyList<string> paths)
        {
            if (!GuardSinglePackage(paths)) return;
            string path = paths[0];

            var meta = ReadMetadata(path);
            if (meta == null)
            {
                MessageBoxHelper.ShowError("无法读取 PKG 元数据。", false);
                return;
            }

            string titleId = meta.TitleId;
            string title = meta.Title;
            bool isGame = string.Equals(meta.PackageCategory, PKGCategory.GAME, StringComparison.OrdinalIgnoreCase);
            bool isPatch = string.Equals(meta.PackageCategory, PKGCategory.PATCH, StringComparison.OrdinalIgnoreCase);
            if (!isGame && !isPatch)
            {
                MessageBoxHelper.ShowWarning(
                    "请选择本体游戏或更新（补丁）PKG。暂不支持安装 DLC/附加内容。", false);
                return;
            }

            var settings = SettingsManager.appSettings_;
            bool confirmed = false;

            // No known shadPS4 compatibility status: warn (this dialog also
            // doubles as the install confirmation), same as the main window.
            string compat = Shadps4Compat.Lookup(titleId, settings.Shadps4Os);
            if (string.IsNullOrWhiteSpace(compat))
            {
                var compatChoice = AppMessageBox.Show("shadPS4",
                    $"没有 {title}（{titleId}）的 shadPS4 兼容状态记录。\n\n" +
                    "该游戏可能无法在模拟器中运行。仍要继续安装吗？",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (compatChoice != DialogResult.Yes) return;
                confirmed = true;
            }

            string library = settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (string.IsNullOrEmpty(library) || !Directory.Exists(library))
            {
                using var fbd = new FolderBrowserDialog
                {
                    Description = "选择 shadPS4 游戏安装文件夹。",
                    ShowNewFolderButton = true,
                };
                if (fbd.ShowDialog() != DialogResult.OK) return;
                library = fbd.SelectedPath;
            }

            string finalDir = Path.Combine(library, titleId);

            // Patch without a base game cannot boot - warn, do not block
            // (same policy as Main.InstallToShadps4Library).
            bool hasBase = Directory.Exists(finalDir) && File.Exists(Path.Combine(finalDir, "eboot.bin"));
            if (isPatch && !hasBase)
            {
                var warnBase = AppMessageBox.Show("shadPS4",
                    $"未在以下位置检测到 {titleId} 的本体游戏：\n{library}\n\n" +
                    "单独安装补丁无法启动游戏，它只能更新本体游戏的文件，而本体游戏尚未安装。\n\n" +
                    "仍要安装该补丁吗？",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (warnBase != DialogResult.Yes) return;
                confirmed = true;
            }

            bool replace = false;
            if (Directory.Exists(finalDir))
            {
                string installedVer = ParamSfoReader.ReadAppVersion(Path.Combine(finalDir, "sce_sys", "param.sfo")) ?? "未知";
                string selectedVer = meta.ApplicationVersion;

                if (Shadps4Launcher.IsEmulatorRunning())
                {
                    var warn = AppMessageBox.Show("shadPS4",
                        "shadPS4 正在运行。\n\n在模拟器使用期间修改已安装的游戏可能导致失败或文件不一致。\n\n是否继续？",
                        AppMessageType.Warning, AppMessageButtons.YesNo);
                    if (warn != DialogResult.Yes) return;
                }

                if (isPatch)
                {
                    var mergeChoice = AppMessageBox.Show("shadPS4",
                        $"游戏已安装\n\n已安装版本：{installedVer}\n此补丁：{selectedVer}\n\n该补丁将合并到现有安装中。是否继续？",
                        AppMessageType.Info, AppMessageButtons.YesNo);
                    if (mergeChoice != DialogResult.Yes) return;
                    confirmed = true;
                }
                else
                {
                    var replaceChoice = AppMessageBox.Show("shadPS4",
                        $"游戏已安装\n\n已安装版本：{installedVer}\n所选 PKG：{selectedVer}\n\n是否替换现有安装？",
                        AppMessageType.Info, AppMessageButtons.YesNo);
                    if (replaceChoice != DialogResult.Yes) return;
                    replace = true;
                    confirmed = true;
                }
            }

            if (!confirmed)
            {
                var go = AppMessageBox.Show("shadPS4",
                    $"要将 {title}（{titleId}）安装到\n{library}吗？",
                    AppMessageType.Info, AppMessageButtons.YesNo);
                if (go != DialogResult.Yes) return;
            }

            // All safety decisions are done - the SAME install service the
            // Manager uses (same in-process extraction pipeline), presented
            // through the shell operation window. The Manager is never
            // opened. A passcode-protected package prompts and retries
            // the whole install (max 5 attempts; a wrong custom passcode
            // re-prompts so a typo does not end the run).
            string passcode = PkgFileListingService.DefaultPasscode;
            ShellOperationForm.Run("正在安装到 shadPS4", title,
                $"{titleId} | {(isPatch ? "更新" : "本体游戏")} | v{meta.ApplicationVersion}", finalDir,
                (progress, ct) => Task.Run(() =>
                {
                    var stageProgress = new Progress<string>(s => progress.Report(
                        new ShellOperationProgress(s, null, !s.StartsWith("Finalizing", StringComparison.Ordinal))));
                    var fileProgress = new Progress<(int Current, int Total, string CurrentFile)>(entry =>
                    {
                        if (entry.Total <= 0)
                            return;

                        int completed = Math.Min(entry.Current + 1, entry.Total);
                        int percent = (int)Math.Round(completed * 100.0 / entry.Total);
                        progress.Report(new ShellOperationProgress(
                            $"正在提取 {completed}/{entry.Total}：{entry.CurrentFile}", percent));
                    });
                    var svc = new Shadps4InstallService
                    {
                        Passcode = passcode,
                    };
                    var result = svc.Install(
                        path, titleId, library, replace, stageProgress, ct, mergeIntoExisting: isPatch,
                        fileProgress: fileProgress);

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (result.Status != Shadps4InstallStatus.ExtractionFailed
                            || !IsPasscodeFailure(result.Message))
                            break;

                        progress.Report(new ShellOperationProgress("需要通行密码……"));
                        string? custom = PromptForPasscodeAsync().GetAwaiter().GetResult();
                        if (custom == null)
                            return Task.FromResult(ShellOperationResult.CancelledResult(
                                "安装已取消。该 PKG 需要通行密码。")); // prompt cancelled - close all

                        passcode = custom;
                        svc.Passcode = custom;
                        result = svc.Install(
                            path, titleId, library, replace, stageProgress, ct, mergeIntoExisting: isPatch,
                            fileProgress: fileProgress);
                    }

                    return Task.FromResult(result.Status switch
                    {
                        Shadps4InstallStatus.Success => new ShellOperationResult(true, "安装完成", null, finalDir),
                        Shadps4InstallStatus.Cancelled => new ShellOperationResult(false, "安装已取消"),
                        _ => new ShellOperationResult(false, string.IsNullOrWhiteSpace(result.Message)
                            ? "安装失败。" : result.Message),
                    });
                }, ct));
        }

        // â”€â”€ display helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string? ReadMetaLine(string path)
        {
            var meta = ReadMetadata(path);
            return meta == null ? null : $"{meta.TitleId} | {meta.PackageCategory}";
        }

        private static string MetaLine(PkgInspectionSnapshot? meta)
            => meta == null ? "" : $"{meta.TitleId} | {meta.PackageCategory} | v{meta.ApplicationVersion}";
    }
}
