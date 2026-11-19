using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS4PKGTool.Utilities.PS4PKGToolHelper;
using PS4PKGTool.Utilities.Settings;
using PS4PKGTool.Utilities.Shadps4;
using DarkUI.Forms;

namespace PS4PKGTool
{
    /// <summary>
    /// The shadPS4 Manager hub (Tools > shadPS4 Manager): one tabbed window
    /// for operating shadPS4. Overview = environment summary and quick
    /// actions; Games = installed games, launching and test reports; Builds
    /// = managed core/launcher builds; Saves = save games and backups;
    /// Settings = paths, adoption and detection. Configuration stays in the
    /// manager now; the manager never writes shadPS4-owned config files.
    /// </summary>
    public partial class Shadps4Manager : DarkForm
    {
        public const int OverviewTabIndex = 0;
        public const int GamesTabIndex = 1;
        public const int BuildsTabIndex = 2;
        public const int SavesTabIndex = 3;
        public const int PatchesTabIndex = 4;
        public const int SettingsTabIndex = 5;

        private readonly AppSettings _settings;
        private bool _loadingSettings;

        /// <summary>
        /// One install request handed over from the main window. Every safety
        /// decision (compatibility warning, library pick, patch-without-base,
        /// replace/merge confirmation) already ran there; the manager owns
        /// only the operation itself.
        /// </summary>
        public sealed record InstallRequest(
            string PkgPath, string TitleId, string Title, bool IsPatch,
            string Library, bool Replace, string Version = "", string InstalledVersion = "");

        private InstallRequest? _pendingInstallRequest;
        private InstallRequest? _installRequest;
        private InstallActivityState _installState = InstallActivityState.Idle;
        private CancellationTokenSource? _installCts;
        private string _installStageText = "";
        private string _installFailureDetail = "";

        /// <summary>True while the manager owns an active install (Main uses this as the smallest operation lock).</summary>
        public static bool IsInstallationActive { get; private set; }

        public Shadps4Manager(AppSettings settings, int initialTabIndex = 0, InstallRequest? installRequest = null)
        {
            InitializeComponent();
            _settings = settings;
            _pendingInstallRequest = installRequest;
            shadps4Tabs.SelectedIndex = Math.Clamp(
                installRequest != null ? GamesTabIndex : initialTabIndex, 0, SettingsTabIndex);

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            OfferDetectedSetup();
            RefreshHeaderState();
            RefreshOverview();
            RefreshGames();
            RefreshSaves();
            RefreshPatches();
            RefreshSettingsTab();
            RefreshBuildsTab();
            SetStatus("", false);

            if (_pendingInstallRequest != null)
            {
                InstallRequest request = _pendingInstallRequest;
                _pendingInstallRequest = null;
                StartInstall(request);
            }
        }

        private void shadps4Tabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            // The Games list can change while the manager is open (installs
            // from the Builds tab) - refresh on every visit.
            RefreshHeaderState();
            switch (shadps4Tabs.SelectedIndex)
            {
                case OverviewTabIndex: RefreshOverview(); break;
                case GamesTabIndex: RefreshGames(); break;
                case BuildsTabIndex: RefreshBuildsTab(); break;
                case SavesTabIndex: RefreshSaves(); break;
                case PatchesTabIndex: RefreshPatches(); break;
                case SettingsTabIndex: RefreshSettingsTab(); break;
            }
        }

        /// <summary>
        /// Opens the feedback form for the selected game without launching
        /// it - the game's name/version come from its own param.sfo, the
        /// active core/launcher and OS from the settings, the status from
        /// the compatibility database. Submitting appends a report.
        /// </summary>
        private void miGameAddReport_Click(object sender, EventArgs e) => AddFeedbackForSelectedGame();

        private void btnManagerGameAddFeedback_Click(object sender, EventArgs e) => AddFeedbackForSelectedGame();

        /// <summary>
        /// Opens the feedback form for the selected game without launching
        /// it - the game's name/version come from its own param.sfo, the
        /// active core/launcher and OS from the settings, the status from
        /// the compatibility database. Submitting appends a report.
        /// </summary>
        private void AddFeedbackForSelectedGame()
        {
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            string folder = _gameRows[idx].Folder;

            var context = BuildFeedbackContext(folder, "");
            if (context == null) return;

            using (var form = new GameFeedbackForm(context))
                form.ShowDialog(this);

            // A report may have been submitted - reflect it in the panel.
            UpdateGameDetails();
            RefreshOverview();
        }

        /// <summary>
        /// Opens the compatibility report builder for the selected game,
        /// pre-filled from its metadata and the latest saved result when one
        /// exists. A live session is not required.
        /// </summary>
        private void btnManagerGameCreateReport_Click(object sender, EventArgs e)
        {
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            var row = _gameRows[idx];

            var context = BuildFeedbackContext(row.Folder, "");
            if (context == null) return;

            // Latest saved result (file order = chronological) pre-fills
            // status and description when present.
            string status = context.KnownStatus, comment = "";
            var latest = GameFeedbackStore.ReadAll()
                .Where(f => string.Equals(f.TitleId, row.Folder, StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            if (latest != null)
            {
                if (!string.IsNullOrWhiteSpace(latest.Status)) status = latest.Status;
                if (!string.IsNullOrWhiteSpace(latest.Comment)) comment = latest.Comment;
            }

            var entry = new GameFeedbackEntry
            {
                TitleId = context.TitleId,
                Title = context.Title,
                GameVersion = context.GameVersion,
                Core = context.CoreDisplay,
                Launcher = context.LauncherDisplay,
                Status = status,
                Os = context.OsDisplay,
                EmulatorVersion = context.EmulatorVersion,
                Processor = HostInfo.CpuName,
                GraphicsCard = HostInfo.GpuName,
                Comment = comment,
            };
            using (var report = new ReportBuilderForm(entry))
                report.ShowDialog(this);
        }

        /// <summary>
        /// Feedback context for a game: name/version from its param.sfo in
        /// the install directory, active components and OS from settings,
        /// known compat status. errorPrefill is the emulator log tail on a
        /// crash (empty when the form is opened manually).
        /// </summary>
        private GameFeedbackContext? BuildFeedbackContext(string titleId, string errorPrefill)
        {
            if (string.IsNullOrWhiteSpace(titleId)) return null;

            string title = "", version = "";
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                var info = ParamSfoReader.ReadGameInfo(Path.Combine(installDir, titleId, "sce_sys", "param.sfo"));
                if (info != null)
                {
                    title = info.Value.Title;
                    version = info.Value.AppVersion;
                }
            }

            var coreSetting = Shadps4ActiveCore.Parse(_settings.Shadps4ActiveCore ?? "");
            string coreId = coreSetting.Source == Shadps4ComponentSource.Managed ? coreSetting.Value : "";

            return new GameFeedbackContext(
                titleId, title, version,
                Shadps4Compat.Lookup(titleId, _settings.Shadps4Os),
                Shadps4SetupDisplay.ComponentDisplayName(_settings.Shadps4ActiveCore, Shadps4Component.Core, _settings.Shadps4ManagedRoot),
                Shadps4SetupDisplay.ComponentDisplayName(_settings.Shadps4ActiveLauncher, Shadps4Component.QtLauncher, _settings.Shadps4ManagedRoot),
                Shadps4Compat.OsDisplay(_settings.Shadps4Os),
                Shadps4SetupDisplay.EmulatorVersion(_settings.Shadps4ActiveCore, _settings.Shadps4ManagedRoot),
                errorPrefill);
        }

        /// <summary>
        /// Session context for a launch-session result: the termination
        /// report's runtime, exit classification, session log path and the
        /// exact active core id. Manual results keep the defaults.
        /// </summary>
        private GameFeedbackContext WithSession(
            GameFeedbackContext context, Shadps4TerminationReport report)
        {
            var coreSetting = Shadps4ActiveCore.Parse(_settings.Shadps4ActiveCore ?? "");
            string coreId = coreSetting.Source == Shadps4ComponentSource.Managed ? coreSetting.Value : "";
            return context with
            {
                SessionLogPath = report.LogPath,
                RuntimeSeconds = (long)report.Runtime.TotalSeconds,
                TerminationKind = report.Category.ToString(),
                ExitCode = unchecked((int)report.ExitCode),
                ExitStatusName = string.IsNullOrEmpty(report.StatusName) ? null : report.StatusName,
                CoreId = coreId,
                CoreCommit = Shadps4SetupDisplay.ShortBuildCommit(coreId),
                CoreVersion = report.CoreVersion,
                ResultSource = "LaunchSession",
            };
        }

        /// <summary>
        /// Opens the reports viewer for the selected game: every report is
        /// a profile candidate (status, core, launcher, details) and the user
        /// can mark one as the game's best profile. Reports are recorded
        /// under the folder name (the id the launch used) in
        /// %APPDATA%\PS4PKGTool\game-feedback.jsonl.
        /// </summary>
        private void btnManagerGameReports_Click(object sender, EventArgs e)
        {
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            string folder = _gameRows[idx].Folder;

            var entries = GameFeedbackStore.ReadAll()
                .Where(f => string.Equals(f.TitleId, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0)
            {
                MessageBoxHelper.ShowInformation("该游戏暂无测试结果。", false);
                return;
            }

            using (var viewer = new GameFeedbackViewer(folder, entries))
                viewer.ShowDialog(this);

            // The best profile may have changed - reflect it in the panel.
            UpdateGameDetails();
        }

        /// <summary>UTC "O" timestamp to local "yyyy-MM-dd HH:mm", or "-".</summary>
        private static string FeedbackWhen(string timestampUtc)
        {
            if (DateTime.TryParse(timestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when))
                return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return "-";
        }

        /// <summary>
        /// Offers the managed store's shadPS4 configuration when settings are
        /// fresh or wiped (builds carry their own manifests). Runs on manager
        /// open instead of app startup - the offer is a convenience, never a
        /// launch blocker.
        /// </summary>
        private void OfferDetectedSetup()
        {
            if (!SettingsManager.CanHealShadps4Settings(_settings)) return;

            var store = new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot);
            var core = store.ListBuilds(Shadps4Component.Core).FirstOrDefault();
            var launcher = store.ListBuilds(Shadps4Component.QtLauncher).FirstOrDefault();
            string data = Path.Combine(store.RootPath, "Data");
            string dataLine = Directory.Exists(data) ? data : "(none)";

            var useIt = AppMessageBox.Show("shadPS4",
                "检测到已有的 shadPS4 配置：\n\n" +
                $"  Core:        {core?.DisplayName ?? "(none)"}\n" +
                $"  QtLauncher:  {launcher?.DisplayName ?? "(none)"}\n" +
                $"  Install dir: {dataLine}\n\n" +
                "使用该配置？",
                AppMessageType.Info, AppMessageButtons.YesNo);

            if (useIt == DialogResult.Yes)
            {
                SettingsManager.AutoHealShadps4Settings(_settings);
                SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
                Logger.LogInformation("已从托管库恢复检测到的 shadPS4 配置。");
            }
            else
            {
                _settings.Shadps4ConfigDetectionDismissed = true;
                SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
                Logger.LogInformation("已拒绝检测到的 shadPS4 配置；不会再询问。");
            }
        }

        // ── Header ───────────────────────────────────────────────────────

        private void RefreshHeaderState()
        {
            lblManagerHeaderState.Text =
                "核心：" + Shadps4SetupDisplay.ComponentDisplayName(
                    _settings.Shadps4ActiveCore, Shadps4Component.Core, _settings.Shadps4ManagedRoot) +
                "    Launcher: " + Shadps4SetupDisplay.ComponentDisplayName(
                    _settings.Shadps4ActiveLauncher, Shadps4Component.QtLauncher, _settings.Shadps4ManagedRoot);
        }

        private void btnManagerOpenQtLauncher_Click(object sender, EventArgs e)
        {
            string activeLauncher = _settings.Shadps4ActiveLauncher ?? "";
            string? launcherPath = Shadps4ActiveCore.ResolveExecutable(activeLauncher, ResolveManagedShadps4Build, out string? error);
            if (launcherPath == null)
            {
                MessageBoxHelper.ShowWarning(
                    (string.IsNullOrEmpty(error) ? "未配置 QtLauncher。" : error) +
                    "\n\n请在「设置」选项卡或「版本」选项卡中配置或安装。", false);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(launcherPath) { WorkingDirectory = Path.GetDirectoryName(launcherPath) });
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("打开 shadPS4 QtLauncher 失败：\n" + ex.Message, false);
            }
        }


        private void miHeaderConfigFolder_Click(object sender, EventArgs e) => OpenConfigFolderIfExists();

        private void miHeaderManagedBuilds_Click(object sender, EventArgs e) => OpenManagedBuildsFolder();

        private void miHeaderGameLibrary_Click(object sender, EventArgs e) => OpenInstallDirectory();

        private void miHeaderCopyEnvironmentInfo_Click(object sender, EventArgs e) => CopyEnvironmentInfo();

        private void OpenConfigFolderIfExists()
        {
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4");
            if (!Directory.Exists(configDir))
            {
                MessageBoxHelper.ShowWarning(
                    "shadPS4 配置目录尚不存在：\n" + configDir +
                    "\n\n该目录由 shadPS4 首次运行时创建。", false);
                return;
            }
            Process.Start("explorer.exe", configDir);
        }

        private void OpenManagedBuildsFolder()
        {
            string root = _settings.Shadps4ManagedRoot?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(root)) root = Shadps4ManagedBuilds.DefaultRootPath();
            if (!Directory.Exists(root))
            {
                MessageBoxHelper.ShowWarning(
                    "托管版本目录尚不存在：\n" + root, false);
                return;
            }
            Process.Start("explorer.exe", root);
        }

        private void OpenInstallDirectory()
        {
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                MessageBoxHelper.ShowWarning(
                    "尚未配置安装目录。\n\n请在「设置」选项卡中设置。", false);
                return;
            }
            Process.Start("explorer.exe", installDir);
        }

        private string? ResolveManagedShadps4Build(string buildId)
            => new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot).ResolveManagedExecutable(buildId);

        // ── Overview tab ─────────────────────────────────────────────────

        private void RefreshOverview()
        {
            RefreshComponentCard(lblOverviewCoreInfo, btnOverviewCoreOpenFolder,
                _settings.Shadps4ActiveCore, Shadps4Component.Core);
            RefreshComponentCard(lblOverviewLauncherInfo, btnOverviewLauncherOpenFolder,
                _settings.Shadps4ActiveLauncher, Shadps4Component.QtLauncher);

            int coreBuilds = 0, launcherBuilds = 0;
            try
            {
                var store = new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot);
                coreBuilds = store.ListBuilds(Shadps4Component.Core).Count;
                launcherBuilds = store.ListBuilds(Shadps4Component.QtLauncher).Count;
            }
            catch { /* best-effort: unconfigured root shows zero builds */ }

            lblOverviewCounts.Text =
                $"已安装游戏：{CountGameFolders()}    " +
                $"检测到存档：{CountSaveGames()}    " +
                $"托管核心版本：{coreBuilds}    " +
                $"托管启动器版本：{launcherBuilds}";

            RefreshOverviewReports();

            lblOverviewEnvDetails.Text = BuildEnvironmentInfoText();
        }

        private void RefreshComponentCard(DarkUI.Controls.DarkLabel info, DarkUI.Controls.DarkButton openFolder,
            string? setting, Shadps4Component component)
        {
            var parsed = Shadps4ActiveCore.Parse(setting ?? "");
            string dir = "";
            switch (parsed.Source)
            {
                case Shadps4ComponentSource.Managed:
                    var store = new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot);
                    var build = store.ListBuilds(component)
                        .FirstOrDefault(b => string.Equals(b.BuildId, parsed.Value, StringComparison.OrdinalIgnoreCase));
                    if (build != null)
                    {
                        string commit = string.IsNullOrWhiteSpace(build.Manifest.Commit) ? ""
                            : "\nCommit: " + (build.Manifest.Commit.Length > 8 ? build.Manifest.Commit.Substring(0, 8) : build.Manifest.Commit);
                        info.Text = build.DisplayName + commit + "\nActive";
                        dir = build.DirectoryPath;
                    }
                    else
                    {
                        info.Text = parsed.Value + "\n\n未设置";
                    }
                    break;
                case Shadps4ComponentSource.Adopted:
                    info.Text = Path.GetFileName(parsed.Value) + "\n" + parsed.Value + "\nActive";
                    dir = Path.GetDirectoryName(parsed.Value) ?? "";
                    break;
                default:
                    info.Text = "（未设置）\n\n未设置";
                    break;
            }
            openFolder.Enabled = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
            openFolder.Tag = dir;
        }

        private void btnOverviewCoreOpenFolder_Click(object sender, EventArgs e)
            => OpenFolderFromTag(btnOverviewCoreOpenFolder);

        private void btnOverviewLauncherOpenFolder_Click(object sender, EventArgs e)
            => OpenFolderFromTag(btnOverviewLauncherOpenFolder);

        private static void OpenFolderFromTag(DarkUI.Controls.DarkButton button)
        {
            string dir = button.Tag as string ?? "";
            if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
        }

        private int CountGameFolders()
        {
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return 0;
            try { return Directory.GetDirectories(installDir).Length; } catch { return 0; }
        }

        private int CountSaveGames()
        {
            try
            {
                string? userDir = CurrentUserDir();
                if (string.IsNullOrWhiteSpace(userDir) || !Directory.Exists(userDir)) return 0;
                return SaveStore().ListSaveGames(userDir).Count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Latest report per game (file order is chronological), most recent
        /// first, top 8. There is no separate run-history store - this is the
        /// honest activity signal we have.
        /// </summary>
        private void RefreshOverviewReports()
        {
            dgvOverviewReports.Rows.Clear();
            _overviewTitleIds.Clear();
            var all = GameFeedbackStore.ReadAll()
                .Where(f => !string.IsNullOrWhiteSpace(f.TitleId))
                .ToList();
            if (all.Count == 0)
            {
                //lblOverviewReportsHint.Text =
                //    "No reports yet. Reports appear after a game session or when you add one from the Games tab.";
                return;
            }
            var latest = all
                .GroupBy(f => f.TitleId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .OrderByDescending(f => f.TimestampUtc)
                .Take(8);
            foreach (var f in latest)
            {
                string title = string.IsNullOrWhiteSpace(f.Title) ? f.TitleId : f.Title;
                dgvOverviewReports.Rows.Add(title, FeedbackWhen(f.TimestampUtc), f.Status, f.Core);
                _overviewTitleIds.Add(f.TitleId);
            }
            //lblOverviewReportsHint.Text = "Latest report per game (most recent first).";
        }

        /// <summary>Double-clicking an Overview report row opens that game's results viewer.</summary>
        private void dgvOverviewReports_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _overviewTitleIds.Count) return;
            string titleId = _overviewTitleIds[e.RowIndex];
            var entries = GameFeedbackStore.ReadAll()
                .Where(f => string.Equals(f.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0) return;

            using (var viewer = new GameFeedbackViewer(titleId, entries))
                viewer.ShowDialog(this);
        }

        private string BuildEnvironmentInfoText()
        {
            string root = _settings.Shadps4ManagedRoot ?? "";
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4");

            int coreBuilds = 0, launcherBuilds = 0;
            try
            {
                var store = new Shadps4ManagedBuilds(root);
                coreBuilds = store.ListBuilds(Shadps4Component.Core).Count;
                launcherBuilds = store.ListBuilds(Shadps4Component.QtLauncher).Count;
            }
            catch { /* best-effort: unconfigured root shows zero builds */ }

            string userMode = "";
            try { userMode = Shadps4EnvironmentResolver.Resolve(_settings.Shadps4ExecutablePath).UserDirectoryMode.ToString(); }
            catch { /* best-effort: unresolved env shows an empty user mode */ }

            return
                $"托管版本根目录：  {Describe(root)}\n" +
                $"游戏库：         {Describe(installDir)}\n" +
                $"shadPS4 数据：         {Describe(configDir)}   （用户模式：{Describe(userMode)}）\n" +
                $"托管核心版本：  {coreBuilds}   托管启动器版本：{launcherBuilds}\n" +
                "「运行」只会启动启用的核心；PS4 PKG Tool 绝不写入 shadPS4 自身的配置文件。";
        }

        private void CopyEnvironmentInfo()
        {
            try
            {
                Clipboard.SetText(BuildEnvironmentInfoText());
                SetStatus("环境信息已复制到剪贴板。", false);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message, false);
            }
        }

        private void btnManagerCopyEnvironmentInfo_Click(object sender, EventArgs e) => CopyEnvironmentInfo();

        private void btnOverviewInstallUpdate_Click(object sender, EventArgs e)
        {
            using var wizard = new Shadps4SetupWizard(_settings);
            wizard.ShowDialog(this);
            if (wizard.Tag as string == "use-existing")
            {
                // The settings live in the manager now.
                shadps4Tabs.SelectedIndex = SettingsTabIndex;
                return;
            }
            RefreshBuildsTab();
            RefreshHeaderState();
            RefreshOverview();
        }

        private void btnOverviewOpenLibrary_Click(object sender, EventArgs e) => OpenInstallDirectory();

        private void btnOverviewOpenShadps4Folder_Click(object sender, EventArgs e) => OpenConfigFolderIfExists();

        // ── Games tab ────────────────────────────────────────────────────

        /// <summary>Display row per game folder (list order), mapped to disk paths.</summary>
        private sealed record ManagerGameRow(string Folder, string Title, string TitleId, string Version, string LastTest);

        private readonly List<ManagerGameRow> _gameRows = new();
        private sealed record PatchGameChoice(ManagerGameRow Row)
        {
            public override string ToString() => $"{Row.Title}   {Row.TitleId}   {Row.Version}";
        }
        private readonly Shadps4PatchCheatStore _patchCheatStore = new();
        private bool _loadingPatchGames;
        private bool _loadingPatchRows;
        private sealed record PatchWorkingRow(string FilePath, int MetadataIndex, string TargetVersion, bool OriginalEnabled);
        private readonly Dictionary<string, long> _gameSizeCache = new(StringComparer.OrdinalIgnoreCase);
        private int _gameDetailsVersion;

        /// <summary>Title id per Overview reports row, so double-clicking a row opens its viewer.</summary>
        private readonly List<string> _overviewTitleIds = new();

        private void RefreshGames()
        {
            string selectedFolder = SelectedGameIndex() is int selectedIndex
                && selectedIndex >= 0
                && selectedIndex < _gameRows.Count
                ? _gameRows[selectedIndex].Folder
                : "";
            lvManagerGames.BeginUpdate();
            try
            {
                lvManagerGames.Items.Clear();
                imageListManagerGames.Images.Clear();
                _gameRows.Clear();
                _gameSizeCache.Clear();
                ClearGameIcon();
                ResetGameDetails();
                btnManagerGameLaunch.Enabled = false;
                btnManagerGameReports.Enabled = false;
                btnManagerGameOpenFolder.Enabled = false;
                btnManagerGameMore.Enabled = false;

                string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                {
                    lblManagerGamesHint.Text = "尚未配置安装目录。请在「设置」选项卡中设置。";
                    return;
                }

                var folders = Directory.GetDirectories(installDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Feedback loaded once per refresh; last report per title id.
                var lastReport = GameFeedbackStore.ReadAll()
                    .Where(f => !string.IsNullOrWhiteSpace(f.TitleId))
                    .GroupBy(f => f.TitleId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

                foreach (string folder in folders)
                {
                    string path = Path.Combine(installDir, folder);
                    if (!TryReadInstalledGame(path, out string title, out string titleId, out string version))
                    {
                        Logger.LogWarning($"已忽略 shadPS4 库中的非游戏目录：{path}");
                        continue;
                    }

                    string lastTest = "";
                    if (lastReport.TryGetValue(folder, out var report))
                        lastTest = FeedbackWhen(report.TimestampUtc);

                    _gameRows.Add(new ManagerGameRow(folder, title, titleId, version, lastTest));

                    var item = new ListViewItem(title);
                    item.SubItems.Add(titleId);
                    item.SubItems.Add(version);
                    item.SubItems.Add(lastTest);
                    int imageIndex = -1;
                    try
                    {
                        string iconPath = Path.Combine(path, "sce_sys", "icon0.png");
                        if (File.Exists(iconPath))
                        {
                            using var ms = new MemoryStream(File.ReadAllBytes(iconPath));
                            imageListManagerGames.Images.Add(folder.ToUpperInvariant(), new Bitmap(Image.FromStream(ms)));
                            imageIndex = imageListManagerGames.Images.Count - 1;
                        }
                    }
                    catch { imageIndex = -1; }
                    item.ImageIndex = imageIndex;
                    lvManagerGames.Items.Add(item);
                }

                lblManagerGamesHint.Text = _gameRows.Count == 0
                    ? "在安装目录中未找到已安装的游戏。"
                    : $"已安装的游戏位于：{installDir}";

                if (!string.IsNullOrWhiteSpace(selectedFolder))
                    SelectGameByTitleId(selectedFolder);
            }
            catch (Exception ex)
            {
                lblManagerGamesHint.Text = ex.Message;
            }
            finally
            {
                lvManagerGames.EndUpdate();
            }
        }

        private static bool TryReadInstalledGame(string path, out string title, out string titleId, out string version)
        {
            title = "";
            titleId = "";
            version = "";
            if (string.IsNullOrWhiteSpace(path)
                || Path.GetFileName(path).StartsWith(".ps4pkgtool-", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(path, "eboot.bin")))
                return false;

            string sfoPath = Path.Combine(path, "sce_sys", "param.sfo");
            if (!File.Exists(sfoPath)) return false;
            try
            {
                var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(File.ReadAllBytes(sfoPath));
                title = SfoString(sfo, "TITLE");
                titleId = SfoString(sfo, "TITLE_ID");
                version = SfoString(sfo, "APP_VER");
                if (string.IsNullOrWhiteSpace(version))
                    version = SfoString(sfo, "VERSION");
                return !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(titleId);
            }
            catch
            {
                title = titleId = version = "";
                return false;
            }
        }

        private void btnManagerGamesRefresh_Click(object sender, EventArgs e)
        {
            if (IsInstallationActive)
            {
                MessageBoxHelper.ShowWarning("shadPS4 正在安装中。安装完成后会刷新游戏列表。", false);
                return;
            }

            RefreshGames();
            RefreshPatches();
            RefreshOverview();
            SetStatus("游戏列表已刷新。", false);
        }

        // ── Patches & Cheats tab ───────────────────────────────────────

        private void RefreshPatches()
        {
            _loadingPatchGames = true;
            try
            {
                string selectedId = (cmbPatchesGame.SelectedItem as PatchGameChoice)?.Row.Folder ?? "";
                cmbPatchesGame.Items.Clear();
                foreach (var game in _gameRows) cmbPatchesGame.Items.Add(new PatchGameChoice(game));
                int selected = _gameRows.FindIndex(row => string.Equals(row.Folder, selectedId, StringComparison.OrdinalIgnoreCase));
                if (selected < 0 && cmbPatchesGame.Items.Count > 0) selected = 0;
                cmbPatchesGame.SelectedIndex = selected;
                if (selected < 0) PopulatePatchGrids(null);
            }
            finally { _loadingPatchGames = false; }
            if (cmbPatchesGame.SelectedIndex >= 0) LoadLocalDefinitions();
        }

        private Shadps4GamePatchContext? SelectedPatchGame()
        {
            if (cmbPatchesGame.SelectedItem is not PatchGameChoice selected) return null;
            string? userDirectory = CurrentUserDir();
            if (string.IsNullOrWhiteSpace(userDirectory)) return null;
            return new Shadps4GamePatchContext(selected.Row.Title, selected.Row.TitleId, selected.Row.Version,
                Path.Combine(_settings.Shadps4InstallDirectory ?? "", selected.Row.Folder), userDirectory);
        }

        private void cmbPatchesGame_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingPatchGames) LoadLocalDefinitions();
        }

        private void LoadLocalDefinitions()
        {
            var game = SelectedPatchGame();
            if (game == null)
            {
                PopulatePatchGrids(null);
                lblPatchesHint.Text = _gameRows.Count == 0 ? "没有可用于补丁管理的已安装游戏。" : "shadPS4 用户目录不可用。";
                return;
            }
            try
            {
                var definitions = _patchCheatStore.ListLocal(game);
                PopulatePatchGrids((game, definitions));
                lblPatchesHint.Text = definitions.Diagnostics.Count == 0
                    ? "来源：官方 shadPS4。此处仅可查看定义。启用状态由 QtLauncher 管理。"
                    : $"来源：官方 shadPS4。已跳过 {definitions.Diagnostics.Count} 条格式错误的定义。启用状态由 QtLauncher 管理。";
            }
            catch (Exception ex)
            {
                PopulatePatchGrids(null);
                lblPatchesHint.Text = "无法读取本地定义：" + ex.Message;
            }
        }

        private void PopulatePatchGrids((Shadps4GamePatchContext Game, Shadps4LocalDefinitions Definitions)? data)
        {
            _loadingPatchRows = true;
            dgvPatchesCheats.Rows.Clear();
            dgvPatches.Rows.Clear();
            btnPatchesApply.Enabled = false;
            btnPatchesRevert.Enabled = false;
            if (data == null) { _loadingPatchRows = false; return; }
            foreach (var cheat in data.Value.Definitions.Cheats)
            {
                string author = string.Join(", ", cheat.Authors);
                string name = cheat.Mods.Count == 0 ? (string.IsNullOrWhiteSpace(cheat.Game) ? Path.GetFileName(cheat.FilePath) : cheat.Game) : string.Join(", ", cheat.Mods.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
                int operations = cheat.Mods.Sum(m => m.OperationCount);
                dgvPatchesCheats.Rows.Add(name, cheat.Version, author, operations, Shadps4DefinitionMatcher.Display(Shadps4DefinitionMatcher.Version(data.Value.Game.AppVersion, cheat.Version)));
            }
            foreach (var patch in data.Value.Definitions.Patches)
                foreach (var entry in patch.Entries)
                {
                    int row = dgvPatches.Rows.Add(entry.Enabled == true, entry.Name, entry.AppVersion, entry.Author, entry.AppElf,
                        entry.OperationCount + (string.IsNullOrWhiteSpace(entry.OperationSummary) ? "" : "  " + entry.OperationSummary),
                        Shadps4DefinitionMatcher.Display(Shadps4DefinitionMatcher.Version(data.Value.Game.AppVersion, entry.AppVersion)));
                    dgvPatches.Rows[row].Tag = new PatchWorkingRow(patch.FilePath, entry.MetadataIndex, entry.AppVersion, entry.Enabled == true);
                }
            _loadingPatchRows = false;
        }

        private void dgvPatches_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dgvPatches.IsCurrentCellDirty) dgvPatches.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void dgvPatches_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_loadingPatchRows || e.RowIndex < 0 || e.ColumnIndex != colPatchEnabled.Index) return;
            var row = dgvPatches.Rows[e.RowIndex];
            if (row.Tag is not PatchWorkingRow working) return;
            bool enabled = row.Cells[colPatchEnabled.Index].Value is true;
            if (enabled && Shadps4DefinitionMatcher.Version((SelectedPatchGame()?.AppVersion) ?? "", working.TargetVersion) == Shadps4DefinitionMatch.VersionMismatch)
            {
                var result = AppMessageBox.Show("Patches & Cheats",
                    "该补丁针对游戏版本 " + working.TargetVersion + ".\nThe installed game version is " + (SelectedPatchGame()?.AppVersion ?? "") + "。\n\n该补丁可能与此版本不兼容。\n\n仍要启用吗？",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (result != DialogResult.Yes)
                {
                    _loadingPatchRows = true;
                    row.Cells[colPatchEnabled.Index].Value = false;
                    _loadingPatchRows = false;
                    return;
                }
            }
            UpdatePatchDirtyState();
        }

        private void UpdatePatchDirtyState()
        {
            bool dirty = dgvPatches.Rows.Cast<DataGridViewRow>().Any(row => row.Tag is PatchWorkingRow working && (row.Cells[colPatchEnabled.Index].Value is true) != working.OriginalEnabled);
            btnPatchesApply.Enabled = dirty;
            btnPatchesRevert.Enabled = dirty;
            if (dirty) lblPatchesHint.Text = "有未保存的补丁更改。作弊码由 QtLauncher 在运行时应用。";
        }

        private void btnPatchesRevert_Click(object sender, EventArgs e) => LoadLocalDefinitions();

        private void btnPatchesApply_Click(object sender, EventArgs e)
        {
            if (Process.GetProcessesByName("shadPS4").Length != 0 || Process.GetProcessesByName("shadPS4QtLauncher").Length != 0)
            {
                MessageBoxHelper.ShowWarning("应用补丁前请先关闭 shadPS4 和 QtLauncher。", false);
                return;
            }
            var changes = dgvPatches.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Tag is PatchWorkingRow working && (row.Cells[colPatchEnabled.Index].Value is true) != working.OriginalEnabled)
                .Select(row =>
                {
                    var working = (PatchWorkingRow)row.Tag;
                    return new Shadps4PatchStateChange(working.FilePath, working.MetadataIndex, row.Cells[colPatchEnabled.Index].Value is true);
                }).ToArray();
            try
            {
                // Recheck immediately before atomic replacement to avoid racing either upstream process.
                if (Process.GetProcessesByName("shadPS4").Length != 0 || Process.GetProcessesByName("shadPS4QtLauncher").Length != 0)
                    throw new InvalidOperationException("在准备变更期间启动了 shadPS4 或 QtLauncher。");
                _patchCheatStore.SetPatchEnabledStates(changes);
                string? userDirectory = CurrentUserDir();
                if (!string.IsNullOrWhiteSpace(userDirectory)) _patchCheatStore.RebuildPatchIndex(userDirectory);
                LoadLocalDefinitions();
                AppMessageBox.Show("Patches & Cheats", "补丁变更已应用。", AppMessageType.Info, AppMessageButtons.OK);
            }
            catch (Exception ex) { MessageBoxHelper.ShowWarning("无法应用补丁变更：\n" + ex.Message, false); }
        }

        private async void btnPatchesDownload_Click(object sender, EventArgs e)
        {
            var game = SelectedPatchGame();
            if (game == null) { MessageBoxHelper.ShowWarning("请先选择一个已安装的游戏并配置 shadPS4。", false); return; }
            btnPatchesDownload.Enabled = false;
            SetStatus("正在下载官方补丁与作弊码定义……", true);
            try
            {
                var result = await new Shadps4OfficialDefinitionRepository().DownloadForGameAsync(game, _patchCheatStore);
                if (!result.Succeeded) MessageBoxHelper.ShowWarning(result.Message, false);
                else AppMessageBox.Show("Patches & Cheats", result.Message, AppMessageType.Info, AppMessageButtons.OK);
                LoadLocalDefinitions();
            }
            catch (Exception ex) { MessageBoxHelper.ShowWarning("定义下载失败：\n" + ex.Message, false); }
            finally { btnPatchesDownload.Enabled = true; SetStatus("", false); }
        }

        private void btnPatchesOpenData_Click(object sender, EventArgs e)
        {
            string? userDirectory = CurrentUserDir();
            if (string.IsNullOrWhiteSpace(userDirectory)) { MessageBoxHelper.ShowWarning("shadPS4 用户目录不可用。", false); return; }
            if (!Directory.Exists(userDirectory)) { MessageBoxHelper.ShowWarning("shadPS4 用户目录尚不存在：\n" + userDirectory, false); return; }
            Process.Start("explorer.exe", userDirectory);
        }

        private void btnPatchesOpenQtLauncher_Click(object sender, EventArgs e) => OpenQtLauncher();

        private int SelectedGameIndex()
        {
            if (lvManagerGames.SelectedItems.Count == 0) return -1;
            return lvManagerGames.SelectedItems[0].Index;
        }

        private void lvManagerGames_SelectedIndexChanged(object sender, EventArgs e) => UpdateGameDetails();

        private void ResetGameDetails()
        {
            lblGameDetailTitle.Text = "选择一个游戏";
            lblGameDetailMeta.Text = "";
            lblGameDetailCompat.Text = "";
            lblGameDetailLastTest.Text = "";
            lblGameDetailReports.Text = "";
            lblGameDetailBest.Text = "";
            lblGameDetailSize.Text = "";
            lblGameDetailRecent.Text = "";
            lblGameDetailPath.Text = "";
        }

        private void UpdateGameDetails()
        {
            int detailVersion = Interlocked.Increment(ref _gameDetailsVersion);
            int idx = SelectedGameIndex();
            bool valid = idx >= 0 && idx < _gameRows.Count;
            btnManagerGameLaunch.Enabled = valid;
            btnManagerGameOpenFolder.Enabled = valid;
            btnManagerGameMore.Enabled = valid;
            ResetGameDetails();
            ClearGameIcon();
            if (!valid)
            {
                btnManagerGameReports.Enabled = false;
                return;
            }

            var row = _gameRows[idx];
            string path = SelectedGamePath(row.Folder);

            lblGameDetailTitle.Text = row.Title;
            lblGameDetailMeta.Text = $"{row.TitleId}   v{row.Version}";

            string status = Shadps4Compat.Lookup(row.TitleId, _settings.Shadps4Os);
            if (string.IsNullOrWhiteSpace(status)) status = "Unknown";
            lblGameDetailCompat.Text = "兼容性：" + status;
            try { lblGameDetailCompat.ForeColor = Shadps4Compat.StatusColor(status); }
            catch { /* best-effort: theme color fallback stays at default */ }

            lblGameDetailLastTest.Text = string.IsNullOrEmpty(row.LastTest) ? "" : "上次测试：" + row.LastTest;

            var feedback = GameFeedbackStore.ReadAll()
                .Where(f => string.Equals(f.TitleId, row.Folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (feedback.Count == 0)
            {
                lblGameDetailReports.Text = "暂无测试结果";
                btnManagerGameReports.Enabled = false;
                lblGameDetailRecent.Text = "暂无测试结果";
            }
            else
            {
                lblGameDetailReports.Text = $"{feedback.Count} 条测试结果";
                btnManagerGameReports.Enabled = true;
                var best = feedback.FirstOrDefault(f => f.IsBestProfile);
                if (best != null)
                    lblGameDetailBest.Text = $"最佳结果：{best.Status}（{best.Core}）（{FeedbackWhen(best.TimestampUtc)}）";

                // Recent results: newest first, one short line each (the
                // Results button opens the full list).
                var recent = feedback.AsEnumerable().Reverse().Take(3);
                lblGameDetailRecent.Text = string.Join(Environment.NewLine,
                    recent.Select(f => $"{FeedbackWhen(f.TimestampUtc)}  {f.Status}"));
            }

            if (_gameSizeCache.TryGetValue(path, out long cachedSize))
                lblGameDetailSize.Text = "大小：" + Helper.RoundBytes(cachedSize);
            else
            {
                lblGameDetailSize.Text = "大小：正在计算……";
                LoadGameSizeAsync(path, detailVersion);
            }
            lblGameDetailPath.Text = path;

            // Game icon (sce_sys/icon0.png) - copied into an independent
            // Bitmap so the source file is not locked.
            string iconPath = Path.Combine(path, "sce_sys", "icon0.png");
            try
            {
                if (File.Exists(iconPath))
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(iconPath));
                    picManagerGameIcon.Image = new Bitmap(Image.FromStream(ms));
                }
            }
            catch { /* unreadable icon - show nothing */ }
        }

        private void OpenQtLauncher() => btnManagerOpenQtLauncher_Click(this, EventArgs.Empty);

        private async void LoadGameSizeAsync(string path, int detailVersion)
        {
            try
            {
                long size = await Task.Run(() => ComputeFolderSize(path));
                _gameSizeCache[path] = size;
                if (detailVersion == _gameDetailsVersion)
                    lblGameDetailSize.Text = "大小：" + Helper.RoundBytes(size);
            }
            catch
            {
                if (detailVersion == _gameDetailsVersion)
                    lblGameDetailSize.Text = "大小：不可用";
            }
        }

        private void ClearGameIcon()
        {
            if (picManagerGameIcon.Image != null)
            {
                picManagerGameIcon.Image.Dispose();
                picManagerGameIcon.Image = null;
            }
        }

        private static string SfoString(OrbisPkgTool.Sfo.ParamSfo sfo, string name)
        {
            try
            {
                return sfo.GetString(name).Trim();
            }
            catch { return ""; }
        }

        private string SelectedGamePath(string folder)
            => Path.Combine(_settings.Shadps4InstallDirectory?.Trim() ?? "", folder);

        private static long ComputeFolderSize(string path)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { /* best-effort: file vanished mid-scan */ }
                }
            }
            catch { /* best-effort: unreadable tree returns the partial total */ }
            return total;
        }

        private void btnManagerGameLaunch_Click(object sender, EventArgs e)
        {
            if (IsInstallationActive)
            {
                MessageBoxHelper.ShowWarning("shadPS4 正在安装中。", false);
                return;
            }
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            string titleId = _gameRows[idx].Folder;
            string title = _gameRows[idx].Title;

            // Launch uses ONLY the explicitly configured Active Core (same
            // rule as the main window) - an external candidate needs approval.
            string? corePath = Shadps4ActiveCore.ResolveExecutable(_settings.Shadps4ActiveCore ?? "", ResolveManagedShadps4Build, out string? error);
            if (corePath == null)
            {
                var envForCandidate = Shadps4EnvironmentResolver.Resolve(_settings.Shadps4ExecutablePath);
                string? candidate = envForCandidate.CoreExePath;
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    var adopt = AppMessageBox.Show("shadPS4",
                        $"{error}\n\n" +
                        $"检测到外部 shadPS4 核心：\n{candidate}\n\n" +
                        "未验证该核心与所选 QtLauncher 版本属于同一套。\n\n" +
                        "采用这个核心？",
                        AppMessageType.Warning, AppMessageButtons.YesNoCancel);
                    if (adopt != DialogResult.Yes) return;
                    _settings.Shadps4ActiveCore = Shadps4ActiveCore.ForAdopted(candidate);
                    SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
                    corePath = candidate;
                }
                else
                {
                    MessageBoxHelper.ShowWarning(
                        (string.IsNullOrEmpty(error) ? "当前没有启用 shadPS4 核心。" : error) +
                        "\n\n请在「版本」选项卡或「设置」选项卡中安装或配置核心。", false);
                    return;
                }
            }

            var env = Shadps4EnvironmentResolver.Resolve(_settings.Shadps4ExecutablePath);
            var launchEnv = new Shadps4Environment
            {
                CoreExePath = corePath,
                InstallDirectories = env.InstallDirectories,
            };
            // Games in the tool's own install directory are found via the
            // extra-search dirs and booted by explicit -g path.
            var extraSearchDirs = new List<string>();
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(installDir)) extraSearchDirs.Add(installDir);

            var launcher = new Shadps4Launcher();
            launcher.Terminated += Shadps4Manager_Terminated;
            var (status, message) = launcher.LaunchInstalledTitle(launchEnv, titleId, extraSearchDirs);
            switch (status)
            {
                case Shadps4LaunchStatus.Started:
                    // No success dialog - the status bar already says the
                    // game is running.
                    SetStatus($"{title} running with {Shadps4SetupDisplay.ComponentDisplayName(_settings.Shadps4ActiveCore, Shadps4Component.Core, _settings.Shadps4ManagedRoot)}", false);
                    break;
                case Shadps4LaunchStatus.GameNotFound:
                    MessageBoxHelper.ShowWarning(message + "\n\n请先将游戏安装到安装目录。", false);
                    break;
                default:
                    MessageBoxHelper.ShowWarning(message, false);
                    break;
            }
        }

        private void Shadps4Manager_Terminated(object? sender, Shadps4TerminationReport report)
        {
            // The watcher reports from a thread-pool thread - marshal to the
            // UI thread and silently abandon delivery when the form is gone.
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    // Same post-session feedback as the main window. The
                    // game's name and version come from its own param.sfo in
                    // the install directory; a crash pre-fills the error box.
                    string titleId = report.Target ?? "";
                    GameFeedbackContext? feedback = null;
                    if (!string.IsNullOrWhiteSpace(titleId))
                    {
                        feedback = WithSession(BuildFeedbackContext(titleId,
                            Shadps4ExitStatus.IsErrorStatus(report.Category) ? report.LogTail ?? "" : ""), report);
                    }
                    Shadps4TerminationUi.ShowIfNeeded(report, feedback);

                    // Clear the "running with ..." status now that the
                    // session is over (the user has closed the result flow).
                    string displayTitle = _gameRows
                        .FirstOrDefault(r => string.Equals(r.Folder, titleId, StringComparison.OrdinalIgnoreCase))?.Title
                        ?? titleId;
                    SetStatus(Shadps4ExitStatus.IsErrorStatus(report.Category)
                        ? $"{displayTitle} 因错误退出，退出码 {report.ExitCodeText}。"
                        : $"{displayTitle} closed.", false);

                    RefreshGames();
                    RefreshOverview();
                }));
            }
            catch { /* best-effort: form gone - feedback delivery abandoned */ }
        }

        private void btnManagerGameMore_Click(object sender, EventArgs e)
            => ctxGameMore.Show(btnManagerGameMore, new Point(0, btnManagerGameMore.Height));

        private void miGameCopyInfo_Click(object sender, EventArgs e)
        {
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            var row = _gameRows[idx];
            string path = SelectedGamePath(row.Folder);

            var feedback = GameFeedbackStore.ReadAll()
                .Where(f => string.Equals(f.TitleId, row.Folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            string status = Shadps4Compat.Lookup(row.TitleId, _settings.Shadps4Os);
            if (string.IsNullOrWhiteSpace(status)) status = "Unknown";

            var sb = new StringBuilder();
            sb.AppendLine("Title: " + row.Title);
            sb.AppendLine("Title ID：" + row.TitleId);
            if (!string.IsNullOrWhiteSpace(row.Version)) sb.AppendLine("Version: " + row.Version);
            sb.AppendLine("Compatibility: " + status);
            if (!string.IsNullOrWhiteSpace(row.LastTest)) sb.AppendLine("上次测试：" + row.LastTest);
            sb.AppendLine("测试结果：" + feedback.Count);
            var best = feedback.FirstOrDefault(f => f.IsBestProfile);
            if (best != null) sb.AppendLine("最佳结果：" + best.Status + " on " + best.Core);
            sb.AppendLine("Size: " + Helper.RoundBytes(ComputeFolderSize(path)));
            sb.AppendLine("Path: " + path);

            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd('\r', '\n'));
                SetStatus("游戏信息已复制到剪贴板。", false);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message, false);
            }
        }

        private void miGameUninstall_Click(object sender, EventArgs e)
        {
            if (IsInstallationActive)
            {
                MessageBoxHelper.ShowWarning("shadPS4 正在安装中。", false);
                return;
            }
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            string folder = _gameRows[idx].Folder;
            string path = SelectedGamePath(folder);

            string installDir = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_settings.Shadps4InstallDirectory ?? ""));
            string resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!string.Equals(Path.GetDirectoryName(resolvedPath), installDir, StringComparison.OrdinalIgnoreCase)
                || !TryReadInstalledGame(resolvedPath, out _, out _, out _))
            {
                MessageBoxHelper.ShowWarning("所选目录不是经过校验的已安装游戏。未删除任何内容。", false);
                return;
            }
            path = resolvedPath;

            if (Shadps4Launcher.IsEmulatorRunning())
            {
                var warn = AppMessageBox.Show("shadPS4",
                    "shadPS4 当前正在运行。\n\n" +
                    "在模拟器使用游戏时删除它，可能失败或留下不一致的文件。\n\n" +
                    "Continue?",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (warn != DialogResult.Yes) return;
            }

            var choice = AppMessageBox.Show("卸载游戏",
                $"Uninstall '{folder}'?\n\n" +
                $"将永久删除已安装的游戏目录：\n{path}\n\n" +
                "此操作无法撤销。",
                AppMessageType.Warning, AppMessageButtons.YesNo);
            if (choice != DialogResult.Yes) return;

            try
            {
                Directory.Delete(path, true);
                RefreshGames();
                RefreshOverview();
                AppMessageBox.Show("Uninstall", $"「{folder}」已卸载。", AppMessageType.Info, AppMessageButtons.OK);
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("卸载失败：\n" + ex.Message, false);
            }
        }

        private void btnManagerGameOpenFolder_Click(object sender, EventArgs e)
        {
            int idx = SelectedGameIndex();
            if (idx < 0 || idx >= _gameRows.Count) return;
            string folder = _gameRows[idx].Folder;
            string path = SelectedGamePath(folder);
            if (!Directory.Exists(path))
            {
                MessageBoxHelper.ShowWarning("该目录已不存在：\n" + path, false);
                RefreshGames();
                return;
            }
            Process.Start("explorer.exe", path);
        }

        // ── Installation activity (Games tab) ───────────────────────────

        /// <summary>Switches to the given tab. Used by Main for the Settings entry points.</summary>
        public void ShowTab(int tabIndex)
        {
            if (IsDisposed) return;
            shadps4Tabs.SelectedIndex = Math.Clamp(tabIndex, 0, SettingsTabIndex);
        }

        /// <summary>
        /// Entry point from the main window when the manager is already open.
        /// One install at a time: a second request focuses the existing
        /// activity instead of starting another operation.
        /// </summary>
        public void RequestInstall(InstallRequest request)
        {
            if (IsDisposed) return;
            if (IsInstallationActive)
            {
                shadps4Tabs.SelectedIndex = GamesTabIndex;
                MessageBoxHelper.ShowWarning("已有另一个 shadPS4 安装正在进行。", false);
                return;
            }
            StartInstall(request);
        }

        /// <summary>Refreshes the Games tab after a successful central queue install.</summary>
        public void RefreshGamesFromQueuedInstall()
        {
            if (!IsDisposed) RefreshGames();
        }

        private void StartInstall(InstallRequest request)
        {
            if (IsInstallationActive) return; // re-entry guard (RequestInstall checks, but be explicit)

            shadps4Tabs.SelectedIndex = GamesTabIndex;
            _installRequest = request;
            _installStageText = "";
            _installFailureDetail = "";
            SetInstallState(InstallActivityState.Preparing);

            lblInstallTitle.Text = request.Title;
            string meta = request.IsPatch ? "Update" : "本体游戏";
            meta += " • " + request.TitleId;
            if (!string.IsNullOrWhiteSpace(request.Version))
                meta += request.IsPatch
                    ? $" • {request.InstalledVersion} → {request.Version}"
                    : $" • v{request.Version}";
            lblInstallMeta.Text = meta;
            lblInstallStage.Text = "正在准备安装……";

            prgInstallProgress.Value = 0;
            prgInstallProgress.Visible = true;
            btnInstallCancel.Visible = true;
            btnInstallCancel.Enabled = true;
            btnInstallOpenFolder.Visible = false;
            btnInstallDismiss.Visible = false;
            grpInstallActivity.Visible = true;

            prgInstallProgress.Marquee = false;
            prgInstallProgress.Minimum = 0;
            prgInstallProgress.Maximum = 1;
            prgInstallProgress.Value = 0;
            _installCts = new CancellationTokenSource();
            CancellationTokenSource cts = _installCts;
            InstallRequest installRequest = request;
            AppSettings settings = _settings;

            // Progress<T> is created here (UI thread) so stage callbacks land
            // on the UI thread; completion marshals explicitly.
            var progress = new Progress<string>(OnInstallStage);
            var fileProgress = new UiProgress<(int Current, int Total, string CurrentFile)>(OnInstallFileProgress);
            _ = Task.Run(() =>
            {
                // The worker must ALWAYS reach OnInstallCompleted: an
                // unexpected throw would leave IsInstallationActive stuck
                // true and Main permanently locked.
                try
                {
                    var svc = new Shadps4InstallService();
                    Logger.LogInformation($"Shadps4Manager 安装：正在将 {installRequest.TitleId} 安装到 {installRequest.Library}（replace={installRequest.Replace}，patch={installRequest.IsPatch}）");
                    var result = svc.Install(installRequest.PkgPath, installRequest.TitleId, installRequest.Library,
                        installRequest.Replace, progress, cts.Token, mergeIntoExisting: installRequest.IsPatch,
                        fileProgress: fileProgress);
                    Logger.LogInformation($"Shadps4Manager 安装：已完成 {result.Status} - {result.Message}");
                    if (IsDisposed) return;
                    try { BeginInvoke((MethodInvoker)(() =>
                    {
                        fileProgress.Dispose();
                        OnInstallCompleted(result, installRequest);
                    })); }
                    catch (InvalidOperationException) { /* form closing raced the completion */ }
                }
                catch (Exception ex)
                {
                    Logger.LogError("Shadps4Manager 安装崩溃：" + ex);
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            fileProgress.Dispose();
                            OnInstallCompleted(
                                new Shadps4InstallResult(Shadps4InstallStatus.Failed,
                                    "安装意外失败：" + ex.Message), installRequest);
                        }));
                    }
                    catch (InvalidOperationException) { /* form closing raced the completion */ }
                }
            });
        }

        /// <summary>Progress stage callback (always on the UI thread via Progress&lt;T&gt;).</summary>
        private void OnInstallStage(string stage)
        {
            if (_installState == InstallActivityState.Preparing)
                SetInstallState(InstallActivityState.Running);

            _installStageText = stage;
            if (stage.Contains("Finalizing"))
            {
                // The final commit (merge/move of the staged dump) cannot be
                // interrupted safely - the service performs no cancellation
                // checks there. Disable Cancel and make the boundary
                // unambiguous.
                btnInstallCancel.Enabled = false;
                lblInstallStage.Text = "正在完成安装……此时已无法取消安装。";
                return;
            }
            if (_installState != InstallActivityState.Cancelling)
                lblInstallStage.Text = stage;
        }

        /// <summary>Per-file extraction progress callback, marshalled to the UI thread by Progress&lt;T&gt;.</summary>
        private void OnInstallFileProgress((int Current, int Total, string CurrentFile) progress)
        {
            if (_installState == InstallActivityState.Cancelling || progress.Total <= 0)
                return;

            if (_installState == InstallActivityState.Preparing)
                SetInstallState(InstallActivityState.Running);

            int completed = Math.Min(progress.Current + 1, progress.Total);
            prgInstallProgress.Marquee = false;
            prgInstallProgress.Minimum = 0;
            prgInstallProgress.Maximum = progress.Total;
            prgInstallProgress.Value = completed;
            lblInstallStage.Text = $"Extracting {completed}/{progress.Total}: {progress.CurrentFile}";
        }

        private void OnInstallCompleted(Shadps4InstallResult result, InstallRequest request)
        {
            prgInstallProgress.Marquee = false; // stops the control's animation timer
            prgInstallProgress.Visible = false;
            btnInstallCancel.Visible = false;
            _installCts?.Dispose();
            _installCts = null;

            switch (result.Status)
            {
                case Shadps4InstallStatus.Success:
                    SetInstallState(InstallActivityState.Completed);
                    RefreshGames();
                    SelectGameByTitleId(request.TitleId);
                    lblInstallTitle.Text = "✓ " + request.Title + (request.IsPatch ? " 更新成功" : " 安装成功");
                    lblInstallMeta.Text = request.TitleId;
                    if (!string.IsNullOrWhiteSpace(request.Version))
                        lblInstallMeta.Text += request.IsPatch
                            ? $" • {request.InstalledVersion} → {request.Version}"
                            : $" • v{request.Version}";
                    lblInstallStage.Text = result.Message;
                    btnInstallOpenFolder.Visible = true;
                    btnInstallDismiss.Visible = true;
                    break;

                case Shadps4InstallStatus.Cancelled:
                    SetInstallState(InstallActivityState.Cancelled);
                    lblInstallTitle.Text = "✕ " + request.Title + " 安装已取消";
                    lblInstallMeta.Text = request.TitleId;
                    // Cancellation can only be reported before the final
                    // commit ran, and staging is removed on cancel - the
                    // library is guaranteed untouched at this point.
                    lblInstallStage.Text = "未更改任何游戏文件。";
                    btnInstallOpenFolder.Visible = false;
                    btnInstallDismiss.Visible = true;
                    break;

                default:
                    SetInstallState(InstallActivityState.Failed);
                    _installFailureDetail = result.Message;
                    lblInstallTitle.Text = "✕ " + request.Title + " 安装失败";
                    lblInstallMeta.Text = request.TitleId;
                    string failedAt = string.IsNullOrWhiteSpace(_installStageText) ? "installation" : _installStageText;
                    lblInstallStage.Text = "在以下步骤失败：" + failedAt + "。查看「详细信息」了解更多。";
                    btnInstallOpenFolder.Visible = false;
                    btnInstallDismiss.Visible = true;
                    break;
            }
        }

        private void SetInstallState(InstallActivityState state)
        {
            if (_installState == state) return;
            if (!InstallActivityStateMachine.IsValidTransition(_installState, state))
            {
                Logger.LogWarning($"Shadps4Manager：已丢弃非法的安装状态转换 {_installState} -> {state}");
                return;
            }
            _installState = state;
            IsInstallationActive = state is InstallActivityState.Preparing
                or InstallActivityState.Running or InstallActivityState.Cancelling;
        }

        private void SelectGameByTitleId(string titleId)
        {
            for (int i = 0; i < _gameRows.Count && i < lvManagerGames.Items.Count; i++)
            {
                if (string.Equals(_gameRows[i].Folder, titleId, StringComparison.OrdinalIgnoreCase))
                {
                    // DarkListView has no EnsureVisible; a focused item is
                    // scrolled into view by the native ListView.
                    lvManagerGames.Items[i].Selected = true;
                    lvManagerGames.Items[i].Focused = true;
                    break;
                }
            }
        }

        private void btnInstallCancel_Click(object sender, EventArgs e)
        {
            if (_installState != InstallActivityState.Running) return;
            SetInstallState(InstallActivityState.Cancelling);
            lblInstallStage.Text = "正在取消……";
            btnInstallCancel.Enabled = false;
            _installCts?.Cancel();
        }

        private void btnInstallPrimary_Click(object sender, EventArgs e)
        {
            if (_installState == InstallActivityState.Completed)
            {
                int idx = SelectedGameIndex();
                if (idx >= 0) lvManagerGames.Items[idx].Focused = true;
            }
            else if (_installState == InstallActivityState.Failed)
            {
                AppMessageBox.Show("安装失败",
                    _installFailureDetail, AppMessageType.Error, AppMessageButtons.OK);
            }
        }

        private void btnInstallOpenFolder_Click(object sender, EventArgs e)
        {
            if (_installRequest == null) return;
            string path = Path.Combine(_installRequest.Library, _installRequest.TitleId);
            if (!Directory.Exists(path))
            {
                MessageBoxHelper.ShowWarning("该目录已不存在：\n" + path, false);
                return;
            }
            Process.Start("explorer.exe", path);
        }

        private void btnInstallDismiss_Click(object sender, EventArgs e)
        {
            SetInstallState(InstallActivityState.Idle);
            _installRequest = null;
            grpInstallActivity.Visible = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // OS-initiated shutdown (Windows shutdown, Task Manager, app
            // exit) must never be blocked by the install guard - nothing
            // can be done to save the operation at that point.
            if (e.CloseReason is CloseReason.WindowsShutDown
                or CloseReason.TaskManagerClosing
                or CloseReason.ApplicationExitCall)
            {
                base.OnFormClosing(e);
                return;
            }

            if (IsInstallationActive)
            {
                var choice = AppMessageBox.Show("shadPS4",
                    "当前有安装正在进行。\n\n" +
                    "关闭管理器无法安全中止安装。\n\n" +
                    "选择「是」保持管理器打开，选择「否」取消安装 " +
                    "，并在清理完成前保持管理器打开。",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                e.Cancel = true; // keep open either way
                if (choice == DialogResult.No)
                    _installCts?.Cancel(); // state resolves to Cancelled, then closing works
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            bool wasActive = IsInstallationActive;
            IsInstallationActive = false;
            base.OnFormClosed(e);
            if (!wasActive)
            {
                // A modeless Show() form is NOT auto-disposed on close;
                // dispose it here so Main's Disposed handler clears the
                // reference and the next open creates a fresh instance.
                Dispose();
            }
        }

        // ── Saves tab ────────────────────────────────────────────────────

        /// <summary>Save games with saves (list order), mapped to store records.</summary>
        private readonly List<Shadps4SaveGame> _saveGames = new();
        /// <summary>Backups of the selected save (list order).</summary>
        private readonly List<Shadps4SaveBackup> _saveBackups = new();
        private Shadps4SaveStore? _saveStore;

        private Shadps4SaveStore SaveStore()
        {
            _saveStore ??= new Shadps4SaveStore(new SystemClock(), new RealFileSystemOps(), ResolveSaveTitle);
            return _saveStore;
        }

        /// <summary>
        /// Status-strip feedback for operations. The strip's progress bar was
        /// removed (install progress lives in the Games page's Installation
        /// panel), so busy is accepted for call-site compatibility but only
        /// the status text is shown.
        /// </summary>
        private void SetStatus(string message, bool busy)
        {
            toolStripManagerStatus.Text = message;
        }

        private string? ResolveSaveTitle(string sfoPath)
        {
            try
            {
                using var fs = File.OpenRead(sfoPath);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(ms.ToArray());
                string title = SfoString(sfo, "TITLE");
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }
            catch { return null; }
        }

        private string? CurrentUserDir()
        {
            try { return Shadps4EnvironmentResolver.Resolve(_settings.Shadps4ExecutablePath).UserDirectory; }
            catch { return null; }
        }

        private bool TryGetBackupRoot(out string root)
        {
            root = _settings.Shadps4ManagedRoot?.Trim() ?? "";
            return !string.IsNullOrEmpty(root);
        }

        private void RefreshSaves()
        {
            lstManagerSaves.Items.Clear();
            lstManagerSavesSlots.Items.Clear();
            lstManagerSavesBackups.Items.Clear();
            _saveGames.Clear();
            _saveBackups.Clear();
            btnSavesBackup.Enabled = false;
            btnSavesOpenFolder.Enabled = false;
            btnSavesRestore.Enabled = false;

            string? userDir = CurrentUserDir();
            if (string.IsNullOrWhiteSpace(userDir) || !Directory.Exists(userDir))
            {
                // Placeholder item kept only as the DarkListBox empty-list draw guard.
                lstManagerSaves.Items.Add("（未检测到 shadPS4 用户目录）");
                lstManagerSavesSlots.Items.Add("（未选择存档）");
                lstManagerSavesBackups.Items.Add("（暂无备份）");
                lblManagerSavesHint.Text = "先启动一次 shadPS4 让它创建用户目录，或在「设置」选项卡中采用核心或启动器。";
                return;
            }

            try
            {
                // Repair any restore interrupted by a crash - idempotent, and
                // normally reports nothing.
                string recovery = SaveStore().RecoverInterruptedRestore(userDir);
                if (!string.IsNullOrWhiteSpace(recovery))
                    AppMessageBox.Show("shadPS4 Saves", recovery, AppMessageType.Info, AppMessageButtons.OK);

                var games = SaveStore().ListSaveGames(userDir);
                _saveGames.AddRange(games);
                foreach (var g in games)
                {
                    string label = string.IsNullOrEmpty(g.Title)
                        ? $"{g.TitleId}  (user {g.UserId})"
                        : $"{g.Title}   {g.TitleId}  (user {g.UserId})";
                    lstManagerSaves.Items.Add(label);
                }
                if (games.Count == 0)
                {
                    lstManagerSaves.Items.Add("（未找到存档）");
                    lstManagerSavesSlots.Items.Add("（未选择存档）");
                    lstManagerSavesBackups.Items.Add("（暂无备份）");
                    lblManagerSavesHint.Text = "游戏在 shadPS4 中保存后，存档会显示在这里。";
                }
                else
                {
                    lblManagerSavesHint.Text = $"存档位于：{userDir}";
                }
            }
            catch (Exception ex)
            {
                lstManagerSaves.Items.Add("（读取存档失败）");
                lstManagerSavesSlots.Items.Add("（未选择存档）");
                lstManagerSavesBackups.Items.Add("（暂无备份）");
                lblManagerSavesHint.Text = ex.Message;
            }
        }

        private void lstManagerSaves_SelectedIndexChanged(object sender, EventArgs e) => UpdateSavesSelection();

        private bool IsGameInstalled(string titleId)
        {
            string installDir = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return false;
            try { return Directory.Exists(Path.Combine(installDir, titleId)); } catch { return false; }
        }

        private void UpdateSavesSelection()
        {
            lstManagerSavesSlots.Items.Clear();
            lstManagerSavesBackups.Items.Clear();
            _saveBackups.Clear();
            btnSavesRestore.Enabled = false;
            lblSaveGameTitle.Text = "未选择存档";
            lblSaveGameUser.Text = "";
            lblSaveGamePath.Text = "";
            lblSaveNotInstalled.Text = "";

            int idx = lstManagerSaves.SelectedIndex;
            bool valid = idx >= 0 && idx < _saveGames.Count;
            btnSavesBackup.Enabled = valid;
            btnSavesOpenFolder.Enabled = valid;
            if (!valid)
            {
                // Placeholder items kept only as the DarkListBox empty-list draw guard.
                lstManagerSavesSlots.Items.Add("（未选择存档）");
                lstManagerSavesBackups.Items.Add("（暂无备份）");
                return;
            }

            var game = _saveGames[idx];
            lblSaveGameTitle.Text = string.IsNullOrEmpty(game.Title) ? game.TitleId : game.Title;
            lblSaveGameUser.Text = "用户：" + game.UserId + "    " + game.TitleId;
            lblSaveGamePath.Text = "存档位置：" + game.Path;
            lblSaveNotInstalled.Text = IsGameInstalled(game.TitleId)
                ? ""
                : "游戏未安装。存档数据仍可使用。";

            foreach (var slot in game.Slots)
                lstManagerSavesSlots.Items.Add($"{slot.Name}   {Helper.RoundBytes(slot.Size)}   {slot.ModifiedUtc:yyyy-MM-dd}");
            if (game.Slots.Count == 0)
                lstManagerSavesSlots.Items.Add("（无存档位）");

            if (!TryGetBackupRoot(out string root)) return;
            try
            {
                foreach (var b in SaveStore().ListBackups(root, game.UserId, game.TitleId))
                {
                    _saveBackups.Add(b);
                    string live = b.LiveUnverified ? "  (live)" : "";
                    var m = Shadps4BackupMetrics.Measure(b.Path);
                    lstManagerSavesBackups.Items.Add(
                        $"{b.BackedUpAtUtc:yyyy-MM-dd HH:mm}{live}   {Helper.RoundBytes(m.TotalSize)}   {m.SlotCount} 个存档位");
                }
                if (_saveBackups.Count == 0)
                    lstManagerSavesBackups.Items.Add("（暂无备份）");
            }
            catch
            {
                lstManagerSavesBackups.Items.Add("（暂无备份）");
            }
        }

        private void lstManagerSavesBackups_SelectedIndexChanged(object sender, EventArgs e)
            => btnSavesRestore.Enabled = lstManagerSavesBackups.SelectedIndex >= 0
                && lstManagerSavesBackups.SelectedIndex < _saveBackups.Count;

        private void btnSavesBackup_Click(object sender, EventArgs e)
        {
            int idx = lstManagerSaves.SelectedIndex;
            if (idx < 0 || idx >= _saveGames.Count) return;
            var game = _saveGames[idx];
            if (!TryGetBackupRoot(out string root))
            {
                MessageBoxHelper.ShowWarning("尚未设置托管版本目录。\n\n请先在「版本」选项卡中使用「安装 shadPS4」。", false);
                return;
            }

            bool live = Shadps4Launcher.IsCoreRunning();
            if (live)
            {
                var warn = AppMessageBox.Show("备份存档",
                    "shadPS4 当前正在运行。\n\n备份可能包含来自不同存档状态的文件。\n\n是否继续？",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (warn != DialogResult.Yes) return;
            }

            try
            {
                string? userDir = CurrentUserDir();
                if (string.IsNullOrWhiteSpace(userDir)) return;
                SetStatus($"正在备份 {game.TitleId}……", true);
                var backup = SaveStore().BackupSave(userDir, game.UserId, game.TitleId, root, live);
                if (backup == null)
                {
                    SetStatus("备份失败。", false);
                    MessageBoxHelper.ShowWarning("存档目录不存在：\n" + game.Path, false);
                    return;
                }
                SetStatus("备份已创建。", false);
                AppMessageBox.Show("备份存档", "存档已备份。\n\n" + backup.Path, AppMessageType.Info, AppMessageButtons.OK);
                UpdateSavesSelection();
            }
            catch (Exception ex)
            {
                SetStatus("备份失败。", false);
                MessageBoxHelper.ShowWarning("备份失败：\n" + ex.Message, false);
            }
        }

        private void btnSavesRestore_Click(object sender, EventArgs e)
        {
            int gameIdx = lstManagerSaves.SelectedIndex;
            int backupIdx = lstManagerSavesBackups.SelectedIndex;
            if (gameIdx < 0 || gameIdx >= _saveGames.Count || backupIdx < 0 || backupIdx >= _saveBackups.Count) return;
            var game = _saveGames[gameIdx];
            var backup = _saveBackups[backupIdx];
            if (!TryGetBackupRoot(out string root)) return;

            var confirm = AppMessageBox.Show("恢复备份",
                $"从 {backup.BackedUpAtUtc:yyyy-MM-dd HH:mm} 的备份中恢复 {game.TitleId}（用户 {game.UserId}）的存档？\n\n" +
                "恢复前会自动备份当前存档。\n\n" +
                "必须先关闭 shadPS4。",
                AppMessageType.Warning, AppMessageButtons.YesNo);
            if (confirm != DialogResult.Yes) return;

            string? userDir = CurrentUserDir();
            if (string.IsNullOrWhiteSpace(userDir)) return;

            Shadps4SaveRestoreResult result;
            try
            {
                SetStatus($"Restoring {game.TitleId}...", true);
                result = SaveStore().RestoreSave(userDir, game.UserId, game.TitleId, backup, root);
            }
            catch (Exception ex)
            {
                result = new Shadps4SaveRestoreResult(Shadps4SaveRestoreStatus.Failed,
                    "恢复失败：\n" + ex.Message, null);
            }
            SetStatus(result.Status == Shadps4SaveRestoreStatus.Restored ? "恢复完成。" : "恢复失败。", false);

            switch (result.Status)
            {
                case Shadps4SaveRestoreStatus.Restored:
                    AppMessageBox.Show("恢复备份", result.Message, AppMessageType.Info, AppMessageButtons.OK);
                    break;
                case Shadps4SaveRestoreStatus.RefusedRunning:
                    MessageBoxHelper.ShowWarning(result.Message, false);
                    break;
                default:
                    MessageBoxHelper.ShowWarning(result.Message, false);
                    break;
            }
            RefreshSaves();
        }

        private void btnSavesOpenFolder_Click(object sender, EventArgs e)
        {
            int idx = lstManagerSaves.SelectedIndex;
            if (idx < 0 || idx >= _saveGames.Count) return;
            string path = _saveGames[idx].Path;
            if (!Directory.Exists(path))
            {
                MessageBoxHelper.ShowWarning("存档目录已不存在：\n" + path, false);
                RefreshSaves();
                return;
            }
            Process.Start("explorer.exe", path);
        }

        // ── Builds tab ─────────────────────────────────────────────────

        private Shadps4ManagedBuilds? _buildsStore;

        // DarkUI's DarkListBox crashes (OnDrawItem, index -1) when drawing an
        // EMPTY list - always keep at least one placeholder item.
        private const string NoCoreBuildsHint = "（尚未安装任何核心版本，请使用「安装版本……」添加。）";
        private const string NoLauncherBuildsHint = "（尚未安装任何启动器版本，请使用「安装版本……」添加。）";

        private static readonly Regex DateInTag = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

        private static bool LooksLikeSha(string id) => id.Length == 40 && id.All(Uri.IsHexDigit);

        /// <summary>
        /// Two logical identity values for a build: a nightly shows its date
        /// and short commit, a tagged release shows the tag and "Release" -
        /// so release builds never look like malformed date builds.
        /// </summary>
        private static (string Primary, string Secondary) BuildIdentity(Shadps4InstalledBuild build)
        {
            if (!LooksLikeSha(build.BuildId))
            {
                string tag = string.IsNullOrWhiteSpace(build.Manifest.Release) ? build.BuildId : build.Manifest.Release;
                return (tag, "Release");
            }
            string date = DateInTag.Match(build.Manifest.Release ?? "").Value;
            string commit = string.IsNullOrWhiteSpace(build.Manifest.Commit) ? build.BuildId : build.Manifest.Commit;
            string shortSha = commit.Length > 8 ? commit.Substring(0, 8) : commit;
            return (string.IsNullOrEmpty(date) ? shortSha : date, shortSha);
        }

        private void RefreshBuildsTab()
        {
            _buildsStore = new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot);

            var activeCore = Shadps4ActiveCore.Parse(_settings.Shadps4ActiveCore);
            var activeLauncher = Shadps4ActiveCore.Parse(_settings.Shadps4ActiveLauncher);

            FillCurrentSetupCard(lblCoreCardState, lblCoreCardDate, lblCoreCardSha,
                btnCoreCardOpenFolder, activeCore, Shadps4Component.Core);
            FillCurrentSetupCard(lblLauncherCardState, lblLauncherCardDate, lblLauncherCardSha,
                btnLauncherCardOpenFolder, activeLauncher, Shadps4Component.QtLauncher);

            FillBuildList(lstCoreBuilds, _buildsStore.ListBuilds(Shadps4Component.Core), activeCore, NoCoreBuildsHint);
            FillBuildList(lstLauncherBuilds, _buildsStore.ListBuilds(Shadps4Component.QtLauncher), activeLauncher, NoLauncherBuildsHint);

            UpdateActionButtons();

            // The exact storage location is a tooltip away, not primary UI.
            toolTipBuilds.SetToolTip(lblStatus, "托管版本位于：\n" + _buildsStore.RootPath);
        }

        private void FillCurrentSetupCard(DarkUI.Controls.DarkLabel state, DarkUI.Controls.DarkLabel date,
            DarkUI.Controls.DarkLabel sha, DarkUI.Controls.DarkButton openFolder,
            Shadps4ComponentRef active, Shadps4Component component)
        {
            string dir = "";
            if (active.Source == Shadps4ComponentSource.Managed)
            {
                var build = _buildsStore.ListBuilds(component)
                    .FirstOrDefault(b => string.Equals(b.BuildId, active.Value, StringComparison.OrdinalIgnoreCase));
                if (build != null)
                {
                    var (primary, secondary) = BuildIdentity(build);
                    state.Text = "● ACTIVE · " + BuildType(build);
                    date.Text = primary;
                    sha.Text = secondary;
                    dir = build.DirectoryPath;
                }
                else
                {
                    state.Text = "未设置";
                    date.Text = "（未设置）";
                    sha.Text = "";
                }
            }
            else if (active.Source == Shadps4ComponentSource.Adopted)
            {
                state.Text = "● 当前启用";
                date.Text = Path.GetFileName(active.Value);
                sha.Text = "已采用";
                dir = Path.GetDirectoryName(active.Value) ?? "";
            }
            else
            {
                state.Text = "未设置";
                date.Text = "（未设置）";
                sha.Text = "";
            }
            openFolder.Enabled = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
            openFolder.Tag = dir;
        }

        private void btnCoreCardOpenFolder_Click(object sender, EventArgs e)
            => OpenFolderFromTag(btnCoreCardOpenFolder);

        private void btnLauncherCardOpenFolder_Click(object sender, EventArgs e)
            => OpenFolderFromTag(btnLauncherCardOpenFolder);

        private void FillBuildList(DarkUI.Controls.DarkListBox list, IReadOnlyList<Shadps4InstalledBuild> builds,
            Shadps4ComponentRef active, string emptyHint)
        {
            list.Items.Clear();
            list.SelectedIndex = -1;
            foreach (var b in builds)
            {
                bool isActive = active.Source == Shadps4ComponentSource.Managed
                    && string.Equals(active.Value, b.BuildId, StringComparison.OrdinalIgnoreCase);
                string primary = BuildIdentity(b).Primary;
                // Release vs Nightly is explicit: the compatibility template
                // only accepts official release builds, so the report-eligible
                // builds are recognizable at a glance.
                string type = BuildType(b);
                // The ACTIVE marker rides in the row text itself so it stays
                // recognizable when another row is selected (selection = the
                // highlight background, active = the marker - two concepts).
                list.Items.Add(isActive ? $"● {primary} · {type}    ACTIVE" : $"  {primary} · {type}");
            }
            if (builds.Count == 0) list.Items.Add(emptyHint);
        }

        /// <summary>Release vs Nightly for a managed build (compat reports only accept releases).</summary>
        private static string BuildType(Shadps4InstalledBuild build)
            => Shadps4ReleaseVersions.IsReleaseTag(build.Manifest.Release) || !LooksLikeSha(build.BuildId)
                ? "Release"
                : "Nightly";

        /// <summary>The list of the currently visible Core/Launcher tab.</summary>
        private DarkUI.Controls.DarkListBox ActiveBuildList => tabsBuilds.SelectedIndex == 1 ? lstLauncherBuilds : lstCoreBuilds;

        private Shadps4Component ActiveBuildComponent
            => tabsBuilds.SelectedIndex == 1 ? Shadps4Component.QtLauncher : Shadps4Component.Core;

        private Shadps4InstalledBuild? SelectedBuild()
        {
            var builds = _buildsStore.ListBuilds(ActiveBuildComponent);
            int idx = ActiveBuildList.SelectedIndex;
            return idx >= 0 && idx < builds.Count ? builds[idx] : null;
        }

        /// <summary>Enables the contextual actions for the selected build.</summary>
        private void UpdateActionButtons()
        {
            var build = SelectedBuild();
            var active = Shadps4ActiveCore.Parse(ActiveBuildComponent == Shadps4Component.Core
                ? _settings.Shadps4ActiveCore
                : _settings.Shadps4ActiveLauncher);
            bool isActive = build != null && active.Source == Shadps4ComponentSource.Managed
                && string.Equals(active.Value, build.BuildId, StringComparison.OrdinalIgnoreCase);

            // The active build is the rollback point - it can be looked at
            // but neither re-activated nor removed.
            btnBuildActivate.Enabled = build != null && !isActive;
            btnBuildOpenFolder.Enabled = build != null && Directory.Exists(build.DirectoryPath);
            btnBuildRemove.Enabled = build != null && !isActive;
        }

        private void tabsBuilds_SelectedIndexChanged(object sender, EventArgs e) => UpdateActionButtons();

        private void lstCoreBuilds_SelectedIndexChanged(object sender, EventArgs e) => UpdateActionButtons();

        private void lstLauncherBuilds_SelectedIndexChanged(object sender, EventArgs e) => UpdateActionButtons();

        private void btnBuildActivate_Click(object sender, EventArgs e)
        {
            var build = SelectedBuild();
            if (build == null)
            {
                MessageBoxHelper.ShowWarning(ActiveBuildComponent == Shadps4Component.Core
                    ? "请先选择一个核心版本。"
                    : "请先选择一个启动器版本。", false);
                return;
            }

            // Remember the selection so the row stays visible after the refresh.
            int priorIndex = ActiveBuildList.SelectedIndex;
            if (ActiveBuildComponent == Shadps4Component.Core)
                _settings.Shadps4ActiveCore = Shadps4ActiveCore.ForManaged(build.BuildId);
            else
                _settings.Shadps4ActiveLauncher = Shadps4ActiveCore.ForManaged(build.BuildId);
            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
            RefreshBuildsTab();
            if (priorIndex >= 0 && priorIndex < ActiveBuildList.Items.Count)
                ActiveBuildList.SelectedIndex = priorIndex;
            RefreshHeaderState();
            RefreshOverview();
        }

        private void btnBuildOpenFolder_Click(object sender, EventArgs e)
        {
            var build = SelectedBuild();
            if (build == null)
            {
                MessageBoxHelper.ShowWarning(ActiveBuildComponent == Shadps4Component.Core
                    ? "请先选择一个核心版本。"
                    : "请先选择一个启动器版本。", false);
                return;
            }
            if (!Directory.Exists(build.DirectoryPath))
            {
                MessageBoxHelper.ShowWarning("该目录已不存在：\n" + build.DirectoryPath, false);
                RefreshBuildsTab();
                return;
            }
            Process.Start("explorer.exe", build.DirectoryPath);
        }

        private void btnBuildRemove_Click(object sender, EventArgs e)
        {
            var build = SelectedBuild();
            if (build == null)
            {
                MessageBoxHelper.ShowWarning(ActiveBuildComponent == Shadps4Component.Core
                    ? "请先选择一个核心版本。"
                    : "请先选择一个启动器版本。", false);
                return;
            }

            var active = Shadps4ActiveCore.Parse(ActiveBuildComponent == Shadps4Component.Core
                ? _settings.Shadps4ActiveCore
                : _settings.Shadps4ActiveLauncher);
            bool isActive = active.Source == Shadps4ComponentSource.Managed
                && string.Equals(active.Value, build.BuildId, StringComparison.OrdinalIgnoreCase);
            if (isActive)
            {
                MessageBoxHelper.ShowWarning(
                    "启用的版本无法移除。\n\n请先启用另一个版本。", false);
                return;
            }

            if (Shadps4Launcher.IsEmulatorRunning())
            {
                var warn = AppMessageBox.Show("移除版本",
                    "shadPS4 当前正在运行。\n\n" +
                    "在模拟器使用该版本时删除它，可能失败或留下不一致的文件。\n\n" +
                    "Continue?",
                    AppMessageType.Warning, AppMessageButtons.YesNo);
                if (warn != DialogResult.Yes) return;
            }

            var (primary, secondary) = BuildIdentity(build);
            var choice = AppMessageBox.Show("移除版本",
                $"移除该版本？\n\n" +
                $"  {primary} · {secondary}\n\n" +
                "将永久删除其所在目录：\n" + build.DirectoryPath + "\n\n" +
                "旧版本用于回滚。已删除的版本无法恢复。",
                AppMessageType.Warning, AppMessageButtons.YesNo);
            if (choice != DialogResult.Yes) return;

            int priorIndex = ActiveBuildList.SelectedIndex;
            try
            {
                _buildsStore.DeleteBuild(build.Component, build.BuildId);
                RefreshBuildsTab();
                int next = Math.Min(priorIndex, ActiveBuildList.Items.Count - 1);
                if (next >= 0) ActiveBuildList.SelectedIndex = next;
                RefreshHeaderState();
                RefreshOverview();
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("移除失败：\n" + ex.Message, false);
            }
        }

        private void btnInstallShadps4_Click(object sender, EventArgs e)
        {
            using var wizard = new Shadps4SetupWizard(_settings);
            wizard.ShowDialog(this);
            RefreshBuildsTab();
            RefreshHeaderState();
            RefreshOverview();
        }

        private async void btnChooseCoreVersion_Click(object sender, EventArgs e)
        {
            btnChooseCoreVersion.Enabled = false;
            lblStatus.Text = "正在从 GitHub 加载可用的核心版本……";
            try
            {
                var feed = new Shadps4ReleaseFeed();
                var stableResult = await feed.GetReleasesAsync(Shadps4FeedKind.CoreStable);
                var nightlyResult = await feed.GetReleasesAsync(Shadps4FeedKind.CoreNightly);

                var releases = new List<Shadps4ReleaseInfo>();
                if (stableResult.Releases != null) releases.AddRange(stableResult.Releases);
                if (nightlyResult.Releases != null) releases.AddRange(nightlyResult.Releases);
                releases = releases.OrderByDescending(r => r.PublishedUtc).ToList();

                if (releases.Count == 0)
                {
                    MessageBoxHelper.ShowWarning("无法加载任何核心版本。\n\n" + (stableResult.Error ?? nightlyResult.Error ?? "未知错误。"), false);
                    return;
                }

                using var picker = new Shadps4VersionPicker(releases);
                if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;

                var selected = picker.Selected;
                if (_buildsStore.FindExe(Shadps4Component.Core, selected.BuildId) != null)
                {
                    MessageBoxHelper.ShowWarning($"版本 {selected.Tag} 已安装。使用「启用」切换过去。", false);
                    RefreshBuildsTab();
                    return;
                }

                await RunInstallAsync(selected);
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("加载核心版本失败：" + ex.Message, false);
            }
            finally
            {
                btnChooseCoreVersion.Enabled = true;
                lblStatus.Text = "托管版本保存在本地，方便你切换回旧版本。";
            }
        }

        private async Task RunInstallAsync(Shadps4ReleaseInfo release)
        {
            btnChooseCoreVersion.Enabled = false;
            btnInstallShadps4.Enabled = false;
            try
            {
                var svc = new Shadps4SetupService();
                var progress = new Progress<string>(s => lblStatus.Text = s);
                using var cts = new CancellationTokenSource();

                var result = await svc.InstallBuildAsync(release, _buildsStore, progress, cts.Token);
                lblStatus.Text = result.Message;
                switch (result.Status)
                {
                    case Shadps4SetupStatus.Success:
                        var askActive = AppMessageBox.Show("shadPS4 Builds",
                            $"{result.Message}\n\n现在将该版本设为启用的核心？",
                            AppMessageType.Info, AppMessageButtons.YesNo);
                        if (askActive == DialogResult.Yes && result.Build != null)
                        {
                            _settings.Shadps4ActiveCore = Shadps4ActiveCore.ForManaged(result.Build.BuildId);
                            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
                        }
                        RefreshBuildsTab();
                        RefreshHeaderState();
                        RefreshOverview();
                        break;
                    case Shadps4SetupStatus.Cancelled:
                        break;
                    default:
                        MessageBoxHelper.ShowWarning(result.Message, false);
                        break;
                }
            }
            finally
            {
                btnChooseCoreVersion.Enabled = true;
                btnInstallShadps4.Enabled = true;
                lblStatus.Text = "托管版本保存在本地，方便你切换回旧版本。";
            }
        }

        private void btnMaintenance_Click(object sender, EventArgs e)
            => ctxMaintenance.Show(btnMaintenance, new Point(0, btnMaintenance.Height));

        private void miMaintenanceOpenFolder_Click(object sender, EventArgs e) => OpenManagedBuildsFolder();

        private void miMaintenanceReset_Click(object sender, EventArgs e)
        {
            var choice = AppMessageBox.Show("重置 shadPS4 配置",
                "重置 shadPS4 配置？\n\n" +
                "将移除：\n" +
                "  - PS4 PKG Tool 下载的所有 shadPS4 托管版本\n" +
                "  - PS4 PKG Tool 中的 shadPS4 设置（启用的核心、启动器、安装目录）\n\n" +
                "不会影响：\n" +
                "  - shadPS4 自身的配置（%APPDATA%\\shadPS4）\n" +
                "  - 你的存档、游戏或已安装的库\n\n" +
                "之后可以用「安装版本……」重新执行配置……\n\n" +
                "Reset?",
                AppMessageType.Warning, AppMessageButtons.YesNo);
            if (choice != DialogResult.Yes) return;

            Shadps4SetupReset.Reset(_buildsStore, _settings);
            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
            RefreshBuildsTab();
            RefreshHeaderState();
            RefreshOverview();
            MessageBoxHelper.ShowWarning(
                "shadPS4 配置已重置。\n\n使用「安装版本……」重新开始。", false);
        }

        private void miMaintenanceRemoveAll_Click(object sender, EventArgs e)
        {
            if (Process.GetProcessesByName("shadPS4").Length != 0 || Process.GetProcessesByName("shadPS4QtLauncher").Length != 0)
            {
                MessageBoxHelper.ShowWarning("移除数据前请先关闭 shadPS4 和 QtLauncher。", false);
                return;
            }

            var targets = ResolveFullRemovalTargets();
            if (targets.Count == 0)
            {
                MessageBoxHelper.ShowWarning("未配置可移除的 shadPS4 数据目录。", false);
                return;
            }

            string list = string.Join("\n", targets.Select(path => "  " + path));
            var choice = AppMessageBox.Show("移除全部 shadPS4 数据",
                "将永久移除以下目录及其内容：\n\n" + list +
                "\n\nThis includes installed games, shadPS4 user data, saves, cheats, patches and managed builds.\n" +
                "这也会清除 PS4 PKG Tool 中的 shadPS4 设置。\n\n移除全部数据？",
                AppMessageType.Warning, AppMessageButtons.YesNo);
            if (choice != DialogResult.Yes) return;

            try
            {
                // Recheck immediately before the irreversible filesystem operation.
                if (Process.GetProcessesByName("shadPS4").Length != 0 || Process.GetProcessesByName("shadPS4QtLauncher").Length != 0)
                    throw new InvalidOperationException("在准备移除期间启动了 shadPS4 或 QtLauncher。");
                foreach (string target in targets.OrderByDescending(path => path.Length))
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                Shadps4SetupReset.Reset(_buildsStore, _settings);
                SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
                RefreshBuildsTab();
                RefreshGames();
                RefreshSaves();
                RefreshPatches();
                RefreshSettingsTab();
                RefreshHeaderState();
                RefreshOverview();
                AppMessageBox.Show("移除全部 shadPS4 数据", "所有已配置的 shadPS4 数据均已移除。", AppMessageType.Info, AppMessageButtons.OK);
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("无法移除全部 shadPS4 数据：\n" + ex.Message, false);
            }
        }

        private List<string> ResolveFullRemovalTargets()
        {
            var candidates = new List<string>();
            string? userDirectory = CurrentUserDir();
            if (!string.IsNullOrWhiteSpace(userDirectory)) candidates.Add(userDirectory);
            string installDirectory = _settings.Shadps4InstallDirectory?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(installDirectory)) candidates.Add(installDirectory);
            string managedRoot = _settings.Shadps4ManagedRoot?.Trim();
            if (string.IsNullOrWhiteSpace(managedRoot)) managedRoot = Shadps4ManagedBuilds.DefaultRootPath();
            candidates.Add(managedRoot);

            return candidates.Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(path => !string.IsNullOrWhiteSpace(path) && !string.Equals(path, Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── Settings tab (moved from Program Settings) ───────────────────

        private void RefreshSettingsTab()
        {
            _loadingSettings = true;
            tbSettingsCore.Text = Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveCore);
            tbSettingsLauncher.Text = Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveLauncher);
            tbSettingsInstallDir.Text = _settings.Shadps4InstallDirectory ?? "";
            _loadingSettings = false;
            RefreshSettingsDetection();
        }

        /// <summary>
        /// Re-runs environment detection and shows the resolved paths plus any
        /// detection warnings. Never throws - detection problems must not
        /// break the manager. Auto-fills the install directory from shadPS4's
        /// own config (first enabled library) when the user has not set one.
        /// </summary>
        private void RefreshSettingsDetection()
        {
            try
            {
                var env = Shadps4EnvironmentResolver.Resolve(_settings.Shadps4ExecutablePath);

                if (string.IsNullOrWhiteSpace(tbSettingsInstallDir.Text)
                    && env.InstallDirectories.Count > 0)
                {
                    tbSettingsInstallDir.Text = env.InstallDirectories[0];
                }

                var lines = new List<string>
                {
                    $"启用的核心：{Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveCore)}",
                    $"QtLauncher: {Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveLauncher)}",
                    $"Status: {(env.IsValid ? "Configuration detected successfully" : "shadPS4 not configured or not found")}",
                    $"Mode: {env.UserDirectoryMode}{(env.DetectionConfidence == Shadps4DetectionConfidence.Low ? " (low confidence)" : "")}",
                    $"Distribution: {env.DistributionType}",
                    $"Config: {Shadps4SetupDisplay.ShortenPath(env.ConfigPath)} ({env.ConfigFormat})",
                    $"Libraries: {(env.InstallDirectories.Count == 0 ? "(none enabled)" : string.Join(" | ", env.InstallDirectories.Select(Shadps4SetupDisplay.ShortenPath)))}",
                    $"Addon/DLC: {Shadps4SetupDisplay.ShortenPath(env.AddonInstallDirectory)}",
                    $"Home: {Shadps4SetupDisplay.ShortenPath(env.HomeDirectory)}",
                    $"Fonts: {Shadps4SetupDisplay.ShortenPath(env.FontDirectory)}",
                    $"Sys modules：{Shadps4SetupDisplay.ShortenPath(env.SysModulesDirectory)}",
                };
                foreach (var warning in env.Warnings)
                    lines.Add("⚠ " + warning);

                lblSettingsDetect.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                lblSettingsDetect.Text = "检测失败：" + ex.Message;
            }
        }

        private void btnSettingsCoreBrowse_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "选择 shadPS4 核心（shadPS4.exe）",
                Filter = "shadPS4 core (shadPS4.exe)|shadPS4.exe|Executables (*.exe)|*.exe",
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            // Adopt the chosen installation - reference only, never modified.
            _settings.Shadps4ActiveCore = Shadps4ActiveCore.ForAdopted(ofd.FileName);
            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
            AfterSettingsChange();
        }

        private void btnSettingsLauncherBrowse_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "选择 shadPS4 QtLauncher（shadPS4QtLauncher.exe）",
                Filter = "shadPS4 QtLauncher (shadPS4QtLauncher.exe)|shadPS4QtLauncher.exe|Executables (*.exe)|*.exe",
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            _settings.Shadps4ActiveLauncher = Shadps4ActiveCore.ForAdopted(ofd.FileName);
            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
            AfterSettingsChange();
        }

        private void AfterSettingsChange()
        {
            RefreshSettingsTab();
            RefreshHeaderState();
            RefreshOverview();
            RefreshBuildsTab();
        }

        private void btnSettingsLauncherOpen_Click(object sender, EventArgs e)
        {
            var store = new Shadps4ManagedBuilds(_settings.Shadps4ManagedRoot);
            string? path = Shadps4ActiveCore.ResolveExecutable(
                _settings.Shadps4ActiveLauncher, store.ResolveManagedExecutable, out string? error);
            if (path == null)
            {
                MessageBoxHelper.ShowWarning(
                    (error ?? "未配置 QtLauncher。") +
                    "\n\n请使用「浏览可用版本」，或在「版本」选项卡中安装启动器。", false);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                });
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("打开 shadPS4 QtLauncher 失败：\n" + ex.Message, false);
            }
        }

        private void btnSettingsInstallDirBrowse_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "选择 shadPS4 游戏安装目录",
                ShowNewFolderButton = true,
            };
            string current = tbSettingsInstallDir.Text.Trim();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                fbd.SelectedPath = current;
            if (fbd.ShowDialog() != DialogResult.OK) return;
            tbSettingsInstallDir.Text = fbd.SelectedPath;
            Logger.LogInformation($"shadPS4 安装目录已设为「{fbd.SelectedPath}」");
        }

        /// <summary>
        /// The manager has no Save & Close - every edit persists immediately.
        /// _loadingSettings suppresses the save while the tab is populated.
        /// </summary>
        private void tbSettingsInstallDir_TextChanged(object sender, EventArgs e)
        {
            if (_loadingSettings) return;
            _settings.Shadps4InstallDirectory = tbSettingsInstallDir.Text.Trim();
            SettingsManager.SaveSettings(_settings, SettingsManager.SettingFilePath);
        }

        private void btnSettingsConfigFolder_Click(object sender, EventArgs e) => OpenConfigFolderIfExists();

        private async void btnSettingsCheckUpdates_Click(object sender, EventArgs e)
        {
            btnSettingsCheckUpdates.Enabled = false;
            btnSettingsCheckUpdates.Text = "正在检查……";
            try
            {
                var feed = new Shadps4ReleaseFeed();
                var stable = await feed.GetReleasesAsync(Shadps4FeedKind.CoreStable);
                var nightly = await feed.GetReleasesAsync(Shadps4FeedKind.CoreNightly);
                var launcher = await feed.GetReleasesAsync(Shadps4FeedKind.QtLauncher);

                string? latestStable = stable.Releases?.OrderByDescending(r => r.PublishedUtc).FirstOrDefault()?.Tag;
                string? latestNightly = nightly.Releases?.OrderByDescending(r => r.PublishedUtc).FirstOrDefault()?.BuildId;
                string? latestLauncher = launcher.Releases?.OrderByDescending(r => r.PublishedUtc).FirstOrDefault()?.BuildId;

                string coreError = stable.Error ?? nightly.Error;
                if (coreError != null)
                {
                    MessageBoxHelper.ShowWarning("shadPS4 更新检查失败：\n" + coreError, false);
                    return;
                }

                var message =
                    "Core\n" +
                    $"  Installed: {Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveCore)}\n" +
                    $"  Latest stable: {latestStable ?? "unknown"}\n" +
                    $"  Latest nightly: {latestNightly ?? "unknown"}\n\n" +
                    "QtLauncher\n" +
                    $"  Installed: {Shadps4SetupDisplay.DescribeActiveSetting(_settings.Shadps4ActiveLauncher)}\n" +
                    $"  Latest: {latestLauncher ?? "unknown"}\n\n" +
                    "更新永远不会自动安装或启用。请在「版本」选项卡中使用「浏览可用版本」安装指定版本。";

                var choice = AppMessageBox.Show("shadPS4 Updates", message,
                    AppMessageType.Info, AppMessageButtons.YesNo);
                if (choice == DialogResult.Yes)
                    Helper.Tool.OpenWebLink("https://github.com/shadps4-emu/shadPS4/releases");
            }
            catch (Exception ex)
            {
                MessageBoxHelper.ShowWarning("shadPS4 更新检查失败：" + ex.Message, false);
            }
            finally
            {
                btnSettingsCheckUpdates.Enabled = true;
                btnSettingsCheckUpdates.Text = "检查更新";
            }
        }

        private static string Describe(string value)
            => string.IsNullOrWhiteSpace(value) ? "（未设置）" : value;

        private void btncopyDetectioninfo_Click(object sender, EventArgs e) => CopyDetectionInfoToClipboard();

        private void CopyDetectionInfoToClipboard()
        {
            try
            {
                Clipboard.SetText(lblSettingsDetect.Text);
                SetStatus("已复制到剪贴板。", false);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message, false);
            }
        }
    }
}
