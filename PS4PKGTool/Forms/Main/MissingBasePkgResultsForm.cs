using DarkUI.Forms;
using System;
using System.Collections.Generic;
using System.Data;

namespace PS4PKGTool
{
    internal sealed record MissingBasePatch(
        string Filename,
        string Title,
        string TitleId,
        string Version,
        string Directory);

    /// <summary>Read-only report of loaded patch PKGs whose base-game PKG is absent.</summary>
    internal sealed partial class MissingBasePkgResultsForm : DarkForm
    {
        public MissingBasePkgResultsForm(int checkedPatchCount, IReadOnlyList<MissingBasePatch> missingPatches)
        {
            InitializeComponent();
            lblSummary.Text = $"已勾选 {checkedPatchCount} 个补丁 PKG。缺少 {missingPatches.Count} 个本体 PKG。";

            var table = new DataTable();
            table.Columns.Add("状态");
            table.Columns.Add("补丁 PKG");
            table.Columns.Add("标题");
            table.Columns.Add("标题 ID");
            table.Columns.Add("补丁版本");
            table.Columns.Add("位置");
            foreach (var patch in missingPatches)
                table.Rows.Add("缺少本体", patch.Filename, patch.Title, patch.TitleId, patch.Version, patch.Directory);

            dgvMissingPatches.DataSource = table;
            dgvMissingPatches.Columns["状态"].FillWeight = 75;
            dgvMissingPatches.Columns["补丁 PKG"].FillWeight = 160;
            dgvMissingPatches.Columns["标题"].FillWeight = 150;
            dgvMissingPatches.Columns["标题 ID"].FillWeight = 75;
            dgvMissingPatches.Columns["补丁版本"].FillWeight = 85;
            dgvMissingPatches.Columns["位置"].FillWeight = 220;
        }

        private void btnClose_Click(object sender, EventArgs e) => Close();
    }
}
