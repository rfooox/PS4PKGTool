using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using PS4PKGTool.Utilities.PS4PKGToolHelper;

namespace PS4PKGTool
{
    /// <summary>
    /// Test History for one game: every local test result (each is a profile
    /// candidate: status, core, launcher, session details). Newest first so
    /// runs are easy to compare; the user can pin one entry as the game's
    /// representative result - at most one per game, persisted in the JSONL.
    /// </summary>
    public partial class GameFeedbackViewer : DarkUI.Forms.DarkForm
    {
        private readonly string _titleId;
        private readonly List<GameFeedbackEntry> _entries;

        public GameFeedbackViewer(string titleId, List<GameFeedbackEntry> entries)
        {
            InitializeComponent();
            _titleId = titleId;
            // Newest first for chronological regression reading.
            _entries = new List<GameFeedbackEntry>(entries);
            _entries.Sort((a, b) => ParseTime(b.TimestampUtc).CompareTo(ParseTime(a.TimestampUtc)));
            this.Text = "测试历史  ·  " + titleId;
            RefreshList();
        }

        private static DateTime ParseTime(string timestampUtc)
            => DateTime.TryParse(timestampUtc, null, DateTimeStyles.RoundtripKind, out DateTime when)
                ? when : DateTime.MinValue;

        private void RefreshList()
        {
            lstFeedbackEntries.Items.Clear();
            foreach (var e in _entries)
            {
                string prefix = e.IsBestProfile ? "✓ " : "  ";
                string runtime = TestResultDisplay.FormatRuntime(e.RuntimeSeconds);
                string crash = e.Crashed ? "  ·  崩溃" : "";
                lstFeedbackEntries.Items.Add(
                    $"{prefix}{TestResultDisplay.FormatWhen(e.TimestampUtc)}  {e.Status}  {runtime}{crash}");
            }
            if (lstFeedbackEntries.Items.Count == 0)
                lstFeedbackEntries.Items.Add("（暂无测试结果）");
            btnFeedbackSetBest.Enabled = false;
            btnFeedbackCopyReport.Enabled = false;
            lblFeedbackViewerDetails.Text = "";
        }

        private void lstFeedbackEntries_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = lstFeedbackEntries.SelectedIndex;
            var entry = idx >= 0 && idx < _entries.Count ? _entries[idx] : null;
            btnFeedbackSetBest.Enabled = entry != null && !entry.IsBestProfile;
            btnFeedbackCopyReport.Enabled = entry != null;
            lblFeedbackViewerDetails.Text = entry == null ? "" : BuildDetails(entry);
        }

        private void btnFeedbackSetBest_Click(object sender, EventArgs e)
        {
            int idx = lstFeedbackEntries.SelectedIndex;
            if (idx < 0 || idx >= _entries.Count) return;

            var entry = _entries[idx];
            GameFeedbackStore.SetBestProfile(_titleId, entry.TimestampUtc);
            foreach (var other in _entries)
                other.IsBestProfile = string.Equals(other.TimestampUtc, entry.TimestampUtc, StringComparison.OrdinalIgnoreCase);

            RefreshList();
            lstFeedbackEntries.SelectedIndex = idx;
        }

        private void btnFeedbackCopyReport_Click(object sender, EventArgs e)
        {
            int idx = lstFeedbackEntries.SelectedIndex;
            if (idx < 0 || idx >= _entries.Count) return;

            // Same report builder as the post-session flow, pre-filled from
            // the saved result (with ITS session log) - no need to replay
            // the session and never a newer unrelated log.
            using (var report = new ReportBuilderForm(_entries[idx]))
                report.ShowDialog(this);
        }

        private void btnFeedbackClose_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string BuildDetails(GameFeedbackEntry e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("状态：" + e.Status);
            if (!string.IsNullOrWhiteSpace(e.Comment)) sb.AppendLine("备注：" + e.Comment);

            sb.AppendLine();
            sb.AppendLine("会话");
            sb.AppendLine("运行时：" + TestResultDisplay.FormatRuntime(e.RuntimeSeconds));
            sb.AppendLine("结束方式：" + TerminationText(e));
            if (e.ExitCode != null) sb.AppendLine("退出码：" + TestResultDisplay.FormatExitCode(e.ExitCode));
            if (!string.IsNullOrWhiteSpace(e.ExitStatusName)) sb.AppendLine("状态：" + e.ExitStatusName);
            if (!string.IsNullOrWhiteSpace(e.Error)) sb.AppendLine("错误：" + TrimForDetails(e.Error, 200));

            sb.AppendLine();
            sb.AppendLine("游戏");
            if (!string.IsNullOrWhiteSpace(e.Title)) sb.AppendLine("标题：" + e.Title);
            if (!string.IsNullOrWhiteSpace(e.TitleId)) sb.AppendLine("标题 ID：" + e.TitleId);
            if (!string.IsNullOrWhiteSpace(e.GameVersion)) sb.AppendLine("应用版本：" + e.GameVersion);

            sb.AppendLine();
            sb.AppendLine("shadPS4");
            if (!string.IsNullOrWhiteSpace(e.EmulatorVersion)) sb.AppendLine("版本：" + e.EmulatorVersion);
            if (!string.IsNullOrWhiteSpace(e.CoreCommit)) sb.AppendLine("提交：" + e.CoreCommit);
            if (!string.IsNullOrWhiteSpace(e.Core)) sb.AppendLine("内核：" + e.Core);
            if (!string.IsNullOrWhiteSpace(e.Launcher)) sb.AppendLine("启动器：" + e.Launcher);

            sb.AppendLine();
            sb.AppendLine("系统");
            if (!string.IsNullOrWhiteSpace(e.Os)) sb.AppendLine("操作系统：" + e.Os);
            if (!string.IsNullOrWhiteSpace(e.Processor)) sb.AppendLine("CPU：" + e.Processor);
            if (!string.IsNullOrWhiteSpace(e.GraphicsCard)) sb.AppendLine("GPU：" + e.GraphicsCard);

            sb.AppendLine();
            sb.AppendLine("日志");
            sb.AppendLine(TestResultLogs.Resolve(e) != null ? "会话日志已保存" : "未保存");
            return sb.ToString().TrimEnd();
        }

        private static string TerminationText(GameFeedbackEntry e)
        {
            if (e.Crashed) return "崩溃";
            return string.IsNullOrEmpty(e.TerminationKind) ? "未记录" : "正常退出";
        }

        private static string TrimForDetails(string text, int max)
        {
            string oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "...";
        }
    }
}
