using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PS4PKGTool.Utilities.PS4PKGToolHelper;

namespace PS4PKGTool.Utilities.Shadps4
{
    public enum Shadps4LaunchStatus
    {
        /// <summary>The emulator process was started.</summary>
        Started,
        /// <summary>The configured core exe is missing or unusable.</summary>
        ExecutableMissing,
        /// <summary>The Title ID is not a valid CUSA ID.</summary>
        InvalidTitleId,
        /// <summary>The game is not installed in any configured library.</summary>
        GameNotFound,
        /// <summary>The executable path to boot does not exist.</summary>
        ExecutableNotFound,
        /// <summary>A shadPS4 core instance is already running - only one can run at a time.</summary>
        AlreadyRunning,
        /// <summary>Process.Start threw (reported message).</summary>
        StartFailed,
    }

    /// <summary>
    /// Invokes the shadPS4 core executable. Owns the CLI surface so
    /// version-specific differences stay in one place.
    ///
    /// Verified against shadPS4 main (src/main.cpp):
    /// - The positional argument (or -g/--game) accepts a game ID (CUSAxxxxx)
    ///   which is searched in the configured install dirs (depth 5), or an
    ///   existing filesystem path to boot directly.
    /// - Launch returns immediately (PS4PKGTool never blocks on the emulator);
    ///   a background watcher keeps the process handle, classifies the exit
    ///   code when the core terminates and raises <see cref="Terminated"/>
    ///   from a thread-pool thread. UI subscribers marshal to the UI thread.
    /// </summary>
    public sealed class Shadps4Launcher
    {
        private static readonly Regex CusaId = new(@"^CUSA\d{5}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Raised on a thread-pool thread when a launched core process exits.
        /// The launcher never touches WinForms - subscribers own marshaling
        /// (see the Logger_OnLog BeginInvoke pattern in the main form).
        /// </summary>
        public event EventHandler<Shadps4TerminationReport>? Terminated;

        /// <summary>Test seam: time source for the launch timestamp.</summary>
        public IClock Clock { get; set; } = new SystemClock();

        /// <summary>Test seam: diagnostics enrichment (classification, log tail, WER discovery).</summary>
        public Shadps4TerminationAnalyzer Analyzer { get; set; } = new(new SystemClock(), new TaskDelay());

        /// <summary>Test seam: process spawn; when set, replaces Process.Start.</summary>
        public Func<ProcessStartInfo, Process>? ProcessStartOverride { get; set; }

        /// <summary>True when a shadPS4 process appears to be running.</summary>
        public static bool IsEmulatorRunning()
        {
            foreach (var name in new[] { "shadPS4", "shadPS4QtLauncher" })
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            return false;
        }

        /// <summary>Test seam: "is the emulator CORE already running".</summary>
        public static Func<bool> CoreRunningCheck { get; set; }
            = () => Process.GetProcessesByName("shadPS4").Length > 0;

        /// <summary>
        /// True when the emulator core itself is running (a game is booted).
        /// The QtLauncher UI alone does not count - launching a game while
        /// only the launcher window is open is fine.
        /// </summary>
        public static bool IsCoreRunning() => CoreRunningCheck();

        /// <summary>The CLI arguments for launching an installed title by ID.</summary>
        public static string[] BuildTitleArguments(string titleId) => new[] { titleId.Trim() };

        /// <summary>The CLI arguments for booting a specific executable path.</summary>
        public static string[] BuildExecutableArguments(string executablePath) => new[] { "-g", executablePath };

        /// <summary>
        /// Searches every configured library for the game folder and returns
        /// the path to its eboot.bin, or null when not installed. Mirrors the
        /// emulator's search: the CUSA-named folder is located up to 5 levels
        /// under the library root, the eboot up to 5 levels under that.
        /// </summary>
        public static string? FindInstalledEboot(Shadps4Environment env, string titleId)
        {
            foreach (var library in env.InstallDirectories)
            {
                if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library)) continue;
                string? gameFolder = FindFolderByTitleId(library, titleId, depth: 5);
                if (gameFolder == null) continue;
                string? eboot = FindEboot(gameFolder, depth: 5);
                if (eboot != null) return eboot;
            }
            return null;
        }

        /// <summary>The resolved launch plan for an installed title, before anything is started.</summary>
        public sealed record Shadps4LaunchPlan(
            Shadps4LaunchStatus Status, string Message, string? Exe, string[]? Arguments);

        /// <summary>
        /// Decides how to launch an installed title WITHOUT starting anything
        /// (testable):
        ///  - game in a shadPS4-configured library → boot by Title ID;
        ///  - game only in an extra search dir (the tool's own install
        ///    directory, which shadPS4's config does not know) → boot by
        ///    explicit -g &lt;eboot&gt; path, because the core's own ID search
        ///    would not find it either.
        /// </summary>
        public Shadps4LaunchPlan ResolveLaunch(Shadps4Environment env, string titleId, IEnumerable<string>? extraSearchDirs = null)
        {
            if (!CusaId.IsMatch(titleId ?? ""))
                return new Shadps4LaunchPlan(Shadps4LaunchStatus.InvalidTitleId,
                    $"「{titleId}」不是有效的 CUSA Title ID。", null, null);

            string exe = env.CoreExePath ?? "";
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return new Shadps4LaunchPlan(Shadps4LaunchStatus.ExecutableMissing,
                    "未找到 shadPS4 核心可执行文件。请在「程序设置」中配置。", null, null);

            if (IsCoreRunning())
                return new Shadps4LaunchPlan(Shadps4LaunchStatus.AlreadyRunning,
                    "shadPS4 已在运行。同时只能运行一个模拟器实例。请关闭正在运行的 shadPS4 后再启动其他游戏。",
                    null, null);

            string? eboot = FindInstalledEboot(env, titleId);
            bool fromExtraDir = false;
            if (eboot == null && extraSearchDirs != null)
            {
                foreach (string dir in extraSearchDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    string? folder = FindFolderByTitleId(dir, titleId, depth: 5);
                    string? e = folder != null ? FindEboot(folder, depth: 5) : null;
                    if (e != null) { eboot = e; fromExtraDir = true; break; }
                }
            }

            if (eboot == null)
                return new Shadps4LaunchPlan(Shadps4LaunchStatus.GameNotFound,
                    $"游戏 {titleId} 未安装在任何已配置的 shadPS4 库中。", null, null);

            if (fromExtraDir)
            {
                // The core's own ID search only knows shadPS4's configured
                // libraries - boot the found eboot by path instead.
                Logger.LogInformation($"Shadps4Launch：已在工具安装目录中找到 {titleId}（{eboot}）——按路径启动");
                return new Shadps4LaunchPlan(Shadps4LaunchStatus.Started, "正在按路径启动 shadPS4。",
                    exe, BuildExecutableArguments(eboot));
            }

            Logger.LogInformation($"Shadps4Launch：已在已配置的库中找到 {titleId}——按 Title ID 启动");
            return new Shadps4LaunchPlan(Shadps4LaunchStatus.Started, "shadPS4 launching.",
                exe, BuildTitleArguments(titleId));
        }

        /// <summary>Launches an installed title by CUSA ID.</summary>
        public (Shadps4LaunchStatus Status, string Message) LaunchInstalledTitle(
            Shadps4Environment env, string titleId, IEnumerable<string>? extraSearchDirs = null)
        {
            Logger.LogInformation($"Shadps4Launch: title {titleId} via {env.CoreExePath}");
            var plan = ResolveLaunch(env, titleId, extraSearchDirs);
            if (plan.Arguments == null || string.IsNullOrWhiteSpace(plan.Exe))
            {
                Logger.LogWarning($"Shadps4Launch: {plan.Status}: {plan.Message}");
                return (plan.Status, plan.Message);
            }
            return Start(plan.Exe, plan.Arguments, titleId, env.UserDirectory);
        }

        /// <summary>Boots a specific executable (extracted game) directly.</summary>
        public (Shadps4LaunchStatus Status, string Message) LaunchExecutable(Shadps4Environment env, string executablePath)
        {
            Logger.LogInformation($"Shadps4Launch: executable {executablePath} via {env.CoreExePath}");
            string exe = env.CoreExePath ?? "";
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Logger.LogWarning($"Shadps4Launch：缺少核心可执行文件（{exe}）");
                return (Shadps4LaunchStatus.ExecutableMissing, "未找到 shadPS4 核心可执行文件。请在「程序设置」中配置。");
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                Logger.LogWarning($"Shadps4Launch：缺少目标可执行文件（{executablePath}）");
                return (Shadps4LaunchStatus.ExecutableNotFound, $"未找到可执行文件：{executablePath}");
            }

            if (IsCoreRunning())
            {
                Logger.LogWarning("Shadps4Launch：核心已在运行——拒绝启动第二个实例");
                return (Shadps4LaunchStatus.AlreadyRunning,
                    "shadPS4 已在运行。同时只能运行一个模拟器实例。请关闭正在运行的 shadPS4 后再启动其他游戏。");
            }

            return Start(exe, BuildExecutableArguments(executablePath), executablePath, env.UserDirectory);
        }

        /// <summary>Locates eboot.bin inside an extracted game folder (depth-limited).</summary>
        public static string? FindEboot(string folder, int depth)
        {
            if (!Directory.Exists(folder)) return null;
            try
            {
                var stack = new Stack<(string Dir, int Depth)>();
                stack.Push((folder, 0));
                string? deepest = null;
                int deepestDepth = int.MaxValue;
                while (stack.Count > 0)
                {
                    var (dir, d) = stack.Pop();
                    if (d > depth) continue;
                    foreach (var entry in Directory.GetFileSystemEntries(dir))
                    {
                        if (Directory.Exists(entry))
                        {
                            if (d < depth) stack.Push((entry, d + 1));
                        }
                        else if (Path.GetFileName(entry).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            if (d < deepestDepth) { deepestDepth = d; deepest = entry; }
                        }
                    }
                }
                return deepest;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindFolderByTitleId(string root, string titleId, int depth)
        {
            try
            {
                var stack = new Stack<(string Dir, int Depth)>();
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    var (dir, d) = stack.Pop();
                    if (d > depth) continue;
                    foreach (var entry in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(entry);
                        if (name.Equals(titleId, StringComparison.OrdinalIgnoreCase))
                            return entry;
                        if (d < depth) stack.Push((entry, d + 1));
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the process info for spawning the emulator. The working
        /// directory is set to the executable's folder: shadPS4 resolves
        /// caches/logs relative to where it is started, and the Qt launcher
        /// always runs the core from its own directory - starting it from
        /// PS4 PKG Tool's folder instead makes games crash that boot fine
        /// from the launcher.
        /// </summary>
        public static ProcessStartInfo BuildProcessStartInfo(string exe, string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
            return psi;
        }

        private (Shadps4LaunchStatus Status, string Message) Start(string exe, string[] arguments, string? target, string? userDirectory)
        {
            try
            {
                var psi = BuildProcessStartInfo(exe, arguments);
                Logger.LogInformation($"Shadps4Launch: starting {exe} {string.Join(" ", arguments)} (cwd: {psi.WorkingDirectory})");
                Process proc = ProcessStartOverride != null
                    ? ProcessStartOverride(psi)
                    : Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回了 null。");
                string version = CoreVersion(exe);
                // The watcher owns the process handle from here on: it waits
                // for the exit, classifies it, and disposes the handle.
                var context = new Shadps4LaunchContext(target, exe, arguments,
                    string.IsNullOrWhiteSpace(version) ? null : version, userDirectory, Clock.UtcNow);
                _ = WatchAsync(proc, context);
                return (Shadps4LaunchStatus.Started,
                    string.IsNullOrWhiteSpace(version) ? "shadPS4 launched." : $"shadPS4 launched [v{version}]");
            }
            catch (Exception ex)
            {
                Logger.LogError("Shadps4Launch：Process.Start 失败：" + ex);
                return (Shadps4LaunchStatus.StartFailed, ex.Message);
            }
        }

        private sealed record Shadps4LaunchContext(
            string? Target, string Executable, string[] Arguments, string? CoreVersion,
            string? UserDirectory, DateTime StartTimeUtc);

        /// <summary>
        /// Background watcher: waits for the core to exit, builds a structured
        /// termination report (exit code, runtime, log tail, WER evidence),
        /// logs it and raises <see cref="Terminated"/>. Fire and forget - the
        /// task closure keeps the launcher instance alive until the exit.
        /// </summary>
        private async Task WatchAsync(Process proc, Shadps4LaunchContext context)
        {
            try
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
                DateTime exitTimeUtc = DateTime.UtcNow;
                var report = await Analyzer.AnalyzeAsync(context.Target, context.Executable,
                    context.Arguments, context.CoreVersion, context.UserDirectory,
                    context.StartTimeUtc, exitTimeUtc, proc.ExitCode).ConfigureAwait(false);

                string targetPart = string.IsNullOrEmpty(context.Target) ? "" : $", target {context.Target}";
                Logger.LogInformation(
                    $"Shadps4Launch：shadPS4 运行 {report.RuntimeText} 后以代码 {report.ExitCodeText} 退出{targetPart}");
                if (!string.IsNullOrWhiteSpace(report.WerReportFolder))
                    Logger.LogInformation($"Shadps4Launch：WER 报告目录：{report.WerReportFolder}");
                if (!string.IsNullOrWhiteSpace(report.LogTail))
                    Logger.LogInformation("Shadps4Launch：模拟器日志末尾：\n" + report.LogTail);

                Terminated?.Invoke(this, report);
            }
            catch (Exception ex)
            {
                // Never an unobserved task exception; a broken watch is a log
                // line, not a user-facing failure.
                Logger.LogWarning("Shadps4Launch：进程退出监控失败：" + ex.Message);
            }
            finally
            {
                proc.Dispose();
            }
        }

        /// <summary>File version of the core exe ("0.17.0.0" -&gt; "0.17.0"), or "" when unavailable.</summary>
        private static string CoreVersion(string exe)
        {
            try
            {
                string v = FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim() ?? "";
                if (v.EndsWith(".0")) v = v[..^2]; // trim the trailing ".0" of 4-part versions
                return v;
            }
            catch
            {
                return "";
            }
        }
    }
}
