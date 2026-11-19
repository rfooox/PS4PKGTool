# PS4 PKG Tool 汉化规则（必须严格遵守）

## 目标语言
简体中文（zh-CN）。语气：简洁、专业、贴近 PS4 玩家圈常用说法，不要翻译腔。

---

## 一、只翻译「用户可见」的文案

需要翻译：
1. 控件显示文本：`.Text`、`.ToolTipText`、`.ToolTip`、`.HeaderText`、`.PlaceholderText`、`.Placeholder`、窗体/对话框标题 `.Text`。
2. 菜单项、树节点、列表项、选项卡标题的显示文本。
3. 消息框 / 对话框 / 提示框里给用户看的文字（`MessageBox.Show`、`MessageBoxHelper.Show*`、`AppMessageBox.Show`、`ShellConfirmForm` 里的提示语等）。
4. 状态栏 / 进度条 / 任务列表 / 日志面板里给用户看的状态文案。
5. 设置项名称、选项名、标签文字。
6. `DataGridView` 显式设置的 `HeaderText`（例如任务列表的 Type / Task / Status / Progress）。

---

## 二、绝对不翻译（改了会出 bug）

1. **文件名、路径、URL、命令行、Shell 命令**（如 `curl -v http://`）。
2. **Windows 特殊目录名**：`System Volume Information` 等。
3. **字体名**：`Segoe UI`；**日期/数字格式串**：`d MMMM yyyy`。
4. **PKG 命名模板关键字**（这些是格式标记，改了会破坏重命名/导出功能）：
   `TITLE`、`TITLE_ID`、`APP_VERSION`、`CONTENT_ID`、`CATEGORY`、`CUSTOM NAME`、`RENAME BY INSTALL PRIORITY`，
   以及由它们组合出来的模板，例如 `TITLE [TITLE_ID] [APP_VERSION]`、`[TITLE_ID] [CATEGORY] [APP_VERSION] TITLE`、
   `CONTENT_ID 2`、`TITLE_ID [TITLE]`、`TITLE [CATEGORY]`。
   → **整个模板字符串保持原样英文，不要只翻译其中一部分。**
5. **Manifest / DataTable 列名**（`Utilities\PkgColumns.cs` 里的常量，以及代码里按这些名字索引 DataRow 的字面量）：
   `Filename`、`Title`、`Title ID`、`Content ID`、`Region`、`Required Firmware`、`Version [App Version]`、
   `PKG Type`、`Category`、`Size`、`PSVR`、`PS4 Pro Enhanced`、`PS5 BC`、`Directory`、`Backported`、
   `Latest Update`、`ShadPS4`、`ShadPS4 Installed`、`Region Name`、`Required Firmware (Num)`。
   → 这些是 manifest 文件的 schema key，改了会导致已保存的 manifest 读不出来。**保持英文。**
6. **PKG 元数据里的固定取值**（来自 PS4 的 param.sfo，改了匹配不上）：
   - 区域：`UK`、`EU`、`US`、`JAPAN`、`HONG KONG`、`ASIA`、`KOREA`
   - 分类：`Game`、`Patch`、`Addon`、`App`、`Uknown`
   - 状态：`Fake`、`Official`、`Addon_Unlocker`
   - 选择类型：`ALL`、`SELECTED`
   - 图标类型：`ALL`、`IMAGE`、`ICON`
   - 固件：`NA` 之类
7. **品牌与协议名**：PS4、PS5、PKG、FPKG、shadPS4、OrbisPkgTool、DarkUI、Remote Package Installer、
   Unity、Unreal、DLC、NPWR、NP Communication ID、CUSA、EP、PPSA、SDK、API、JSON、PNG、ELF、UE。
   → 这些作为词组的一部分保留英文，但周围的说明文字要翻译。
   例如 `Download official update` → `下载官方更新`；`Send PKG to PS4` → `发送 PKG 到 PS4`。
8. **代码标识符**：变量名、属性名、方法名、控件 Name、枚举名、字典 key、switch case、`==`/`!=`/`Contains`/
   `StartsWith`/`EndsWith` 的比较右值。

---

## 三、格式与安全红线

- 只允许修改双引号内的**内容**。不得增删引号、不得改变 C# 语法结构、不得重排代码。
- 占位符必须原样保留，数量与顺序都不能变：`{0}` `{1}` `{2}`、`{ex.Message}`、`{path}` 等。
- 需要引号时用中文引号「」或“”，**不要**用 ASCII 双引号或反斜杠，避免破坏 C# 字符串。
- 插值字符串 `$"..."` 里的 `{变量}` 不要动；只翻译其中的固定文字部分。
- 文件保持 **UTF-8（无 BOM）+ LF** 行尾，不要改变行尾，不要改变缩进。
- 不要修改 `.resx`、`.csproj`、`.sln`、`.settings`、`.manifest` 等非 `.cs` 文件。
- 不要删除或新增代码行。

---

## 四、术语表（统一用词，务必照用）

| English | 中文 |
|---|---|
| Open | 打开 |
| Open Folder | 打开文件夹 |
| Save | 保存 |
| Save As | 另存为 |
| Cancel | 取消 |
| Close | 关闭 |
| Delete | 删除 |
| Remove | 移除 |
| Rename | 重命名 |
| Extract | 提取 |
| Install / Uninstall | 安装 / 卸载 |
| Update / Download | 更新 / 下载 |
| Settings | 设置 |
| Tools | 工具 |
| Help | 帮助 |
| About | 关于 |
| Refresh | 刷新 |
| Search | 搜索 |
| Filter | 筛选 |
| Import / Export | 导入 / 导出 |
| Start / Stop / Resume / Retry | 开始 / 停止 / 恢复 / 重试 |
| Yes / No / OK | 是 / 否 / 确定 |
| Error / Warning / Information | 错误 / 警告 / 信息 |
| Success / Failed / Ready / Idle | 成功 / 失败 / 就绪 / 空闲 |
| Progress / Overall progress | 进度 / 总进度 |
| Name / Size / Path / Folder / File | 名称 / 大小 / 路径 / 文件夹 / 文件 |
| Version / Title / Description | 版本 / 标题 / 描述 |
| Game / Application / Package / Content | 游戏 / 应用 / 安装包 / 内容 |
| Language / Region / Category / Type / Status | 语言 / 区域 / 分类 / 类型 / 状态 |
| Expand All / Collapse All / Select All / Clear | 全部展开 / 全部折叠 / 全选 / 清除 |
| Back / Next / Previous / Finish / Done | 返回 / 下一步 / 上一步 / 完成 / 完成 |
| Apply | 应用 |
| PKG Viewer | PKG 查看器 |
| Mini PKG Viewer | 迷你 PKG 查看器 |
| View PKG Change Info | 查看 PKG 变更信息 |
| Rename PKG | 重命名 PKG |
| Save artwork | 保存图片 |
| Backgrounds only | 仅背景 |
| Icon only | 仅图标 |
| Backgrounds + Icon | 背景 + 图标 |
| Required Firmware | 所需固件 |
| App Version | 应用版本 |
| Title ID | 标题 ID |
| Content ID | 内容 ID |
| PKG Type | PKG 类型 |
| Base Game | 本体游戏 |
| Latest Update | 最新更新 |
| PS4 SDK Version | PS4 SDK 版本 |
| ShadPS4 Installed | 已安装 shadPS4 |
| Patches & Cheats | 补丁与作弊码 |
| Managed builds | 托管版本 |
| Not set / (not set) | 未设置 /（未设置）|
| Not available | 不可用 |
| (unknown) | （未知）|
| (no backups yet) | （暂无备份）|
| (no save selected) | （未选择存档）|
| No PKG selected. | 未选择 PKG。|
| No PKG file selected. | 未选择 PKG 文件。|
| PKG file not found. | 未找到 PKG 文件。|
| Uninstall game | 卸载游戏 |
| Send PKG to PS4 | 发送 PKG 到 PS4 |
| Passcode required... | 需要通行密码……|
| A shadPS4 installation is in progress. | shadPS4 正在安装中。|
| Extracting PKG | 正在提取 PKG |
| Extracting game files... | 正在提取游戏文件……|
| Extraction failed: | 提取失败：|
| Extraction cancelled. | 提取已取消。|
| Installation cancelled. | 安装已取消。|
| Preview already in progress... | 预览已在进行中……|
| Preview failed: | 预览失败：|
| Uninstall failed. | 卸载失败。|
| PKG uninstalled. | PKG 已卸载。|
| An error occurred: | 发生错误：|
| Remote PKG Installer \| Status : Idle | 远程安装包安装器 \| 状态：空闲 |

> 术语表不是穷尽的。表里没有的，按中文习惯自然翻译即可，但同一句话在不同文件里必须译法一致。

---

## 五、工作方式

- 你负责的文件范围是**独占**的，不要改范围外的文件。
- 优先用脚本批量替换（见下方工具），少量零散位置用精确编辑。
- 完成后必须自查：
  1. 没有任何一行 C# 语法被破坏（引号数量配平）。
  2. 没有翻译到「绝对不翻译」清单里的东西。
  3. 所有用户可见文案都已覆盖，没有漏网的英文句子。
- 输出报告：改了哪些文件、每个文件改了多少条、列出你**特意保留英文**的条目及原因、列出你不确定的地方。
