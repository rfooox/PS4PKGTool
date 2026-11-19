using System;
using System.Collections.Generic;
using System.Text;

namespace PS4PKGTool.Utilities.PS4PKGToolHelper
{
    /// <summary>
    /// Markdown builder for the shadPS4 compatibility issue template.
    /// Kept fully UI-free so the template can be reworded or reordered
    /// without touching any form. The checklist statements mirror the
    /// upstream issue template; every item is OPT-IN: the UI must not
    /// tick them unless the user confirmed them.
    /// </summary>
    public static class CompatibilityReport
    {
        /// <summary>The checklist statements, in template order.</summary>
        public static readonly string[] ChecklistItems =
        {
            "已在要求的正式版本上测试",
            "已确认没有同类报告",
            "使用自己未修改的游戏 dump",
            "已安装所需固件模块",
            "已启用同步日志",
            "使用默认模拟设置",
        };

        /// <summary>
        /// Builds the full Markdown report. checklist[i] maps to
        /// <see cref="ChecklistItems"/>[i] (default false). logFileName is
        /// the session log's file name when known - the Log File section
        /// keeps the GitHub placeholder either way, GitHub generates its own
        /// attachment URL on paste.
        /// </summary>
        public static string BuildMarkdown(GameFeedbackEntry entry, IReadOnlyList<bool>? checklist = null, string? logFileName = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("### 检查清单");
            for (int i = 0; i < ChecklistItems.Length; i++)
            {
                bool done = checklist != null && i < checklist.Count && checklist[i];
                sb.AppendLine($"- [{(done ? "x" : " ")}] {ChecklistItems[i]}");
            }

            AppendSection(sb, "游戏名称", entry.Title);
            AppendSection(sb, "游戏序列号", entry.TitleId);
            AppendSection(sb, "游戏版本", VersionDisplay(entry.GameVersion));
            AppendSection(sb, "使用的模拟器版本", entry.EmulatorVersion);
            AppendSection(sb, "当前状态", NormalizeStatus(entry.Status));
            AppendSection(sb, "操作系统", entry.Os);
            AppendSection(sb, "处理器", entry.Processor);
            AppendSection(sb, "显卡", entry.GraphicsCard);
            AppendSection(sb, "错误", entry.Error);
            AppendSection(sb, "描述", entry.Comment);
            sb.AppendLine("### 截图");
            sb.AppendLine();
            sb.AppendLine("<!-- 在此添加截图 -->");
            sb.AppendLine();
            sb.AppendLine("### 日志文件");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(logFileName)
                ? "<!-- 在此附上日志文件 -->"
                : $"<!-- 在此附上 {logFileName} -->");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// One mapping for status spelling: UI/history uses "In-Game", the
        /// upstream template expects "Ingame". Everything else passes through.
        /// </summary>
        public static string NormalizeStatus(string? status) => (status ?? "").Trim() switch
        {
            "In-Game" => "Ingame",
            "In Game" => "Ingame",
            _ => (status ?? "").Trim(),
        };

        /// <summary>
        /// Game version in the upstream "v01.09" shape (a leading "v" is
        /// added only when absent; an empty version becomes a clear
        /// unknown marker).
        /// </summary>
        public static string VersionDisplay(string gameVersion)
        {
            string v = (gameVersion ?? "").Trim();
            if (string.IsNullOrEmpty(v)) return "(unknown)";
            return v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? v : "v" + v;
        }

        private static void AppendSection(StringBuilder sb, string heading, string value)
        {
            sb.AppendLine();
            sb.AppendLine("### " + heading);
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.TrimEnd());
        }
    }
}
