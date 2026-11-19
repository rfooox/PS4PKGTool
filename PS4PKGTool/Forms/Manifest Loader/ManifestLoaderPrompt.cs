using PS4PKGTool.Utilities.PS4PKGToolHelper;
using System;
using System.Windows.Forms;

namespace PS4PKGTool
{
    public enum StartupChoice
    {
        Manifest,
        Directory,
        Empty
    }

    public partial class ManifestLoaderPrompt : DarkUI.Forms.DarkForm
    {
        public StartupChoice Choice { get; private set; } = StartupChoice.Empty;

        public ManifestLoaderPrompt(bool manifestAvailable, int manifestEntryCount,
            bool directoriesAvailable, int directoryCount)
        {
            InitializeComponent();

            // Build description text
            string info = "";
            if (manifestAvailable)
                info += $"• 已找到清单（缓存了 {manifestEntryCount} 个 PKG）\r\n";
            else
                info += "• 未找到清单\r\n";

            if (directoriesAvailable)
                info += $"• 已配置 {directoryCount} 个目录\r\n";
            else
                info += "• 未配置任何目录\r\n";

            darkLabelInfo.Text = info.TrimEnd();

            btnLoadFromManifest.Enabled = manifestAvailable;
            btnScanFromDirectory.Enabled = directoriesAvailable;
            btnLaunchEmpty.Enabled = true;
        }

        private void btnLoadFromManifest_Click(object sender, EventArgs e)
        {
            Choice = StartupChoice.Manifest;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btnScanFromDirectory_Click(object sender, EventArgs e)
        {
            Choice = StartupChoice.Directory;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btnLaunchEmpty_Click(object sender, EventArgs e)
        {
            Choice = StartupChoice.Empty;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
