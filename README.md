# MemoPad Enhanced

Windows 桌面记事本，基于 .NET 9（主窗口 Windows Forms，对话框 WPF），编辑器内核使用 Scintilla5.NET。
在原版 MemoPad 之上做了一轮自用增强：多标签、彩虹着色、文字标记、折叠展开、会话恢复与云盘目录适配等。

## 功能特性

- **多标签**：打开文件新增标签页，标签显示文件名或正文首行预览，支持未保存脏标。
- **彩虹着色**
  - 按字符分组换色（每 4 / 7 / 10 / 20 字一色），或**每行一色**（整行同色，循环七色）。
  - 颜色流动（跑马灯）、可选彩色底块。
- **文字标记高亮**：选中文本打五色高亮，标记随笔记保存到同名侧车文件。
- **折叠 / 展开**：段落折叠收起与展开。
- **外观**：明暗主题、背景/前景配色、字体字号、加粗斜体、行间距、缩放比例记忆。
- **编辑**：查找跳转、撤销/重做、剪切/复制/粘贴、拖拽打开文件。
- **打印**：页面设置、页眉页脚、边距、横纵版。
- **快捷键**：常用快捷键可在设置里自定义。
- **会话恢复**：异常退出后按快照恢复上次打开的标签页，保留多份备份兜底。
- **数据目录适配（自用）**：检测到坚果云同步盘时把配置/快照/笔记目录指向云盘；无云盘则回退本机 `%APPDATA%\MemoPad`。该适配为个人环境定制，不影响其他用户使用。

## 运行环境

- Windows x64
- .NET 9 SDK（开发构建用）；发布版为自包含单文件 exe，目标机器无需预装运行时。

## 构建

```powershell
dotnet build memopad.csproj -c Release
```

## 发布单文件便携版

```powershell
dotnet publish memopad.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

> Scintilla 原生库（`native/Scintilla.dll`、`native/Lexilla.dll`）作为内嵌资源打进程序集，首次启动缺库时自动释放到 exe 同级 `runtimes\win-x64\native\`。

## 目录结构

| 目录 / 文件 | 用途 |
| --- | --- |
| `Program.cs` | 程序入口、云盘联接自愈、启动预热 |
| `Views/` | 主窗口、标签栏、编辑器控件、标题栏、状态栏、菜单 |
| `Dialogs/` | 各 WPF / WinForms 对话框（配色、字体、跳转、页面设置、快捷键等） |
| `Models/` | 文档模型（标题、标签预览、脏标状态） |
| `Services/` | 设置持久化、配色方案、标记存储、会话恢复、打印、主题、文本读写等 |
| `native/` | Scintilla / Lexilla 原生库（内嵌资源） |

## 数据存放

- 设置与恢复快照：`%APPDATA%\MemoPad`（在坚果云环境下被联接指向云盘 `MemoPad` 目录）。
- 笔记正文：云盘「笔记」目录，回退目录为 `%APPDATA%\MemoPad\Notes`。
- 标记高亮：与笔记同名的 `.marks.json` 侧车文件。

## 来源与许可

- 基于原版 MemoPad v0.10.1（Copyright mrgarita）二次开发，原版许可证以上游为准。
- 编辑器使用 [Scintilla](https://www.scintilla.org/) 及其 .NET 封装 Scintilla5.NET；Lexilla 为其词法分析组件，遵循各自开源许可。
- 仓库内保留有移植期残留的日文注释，后续逐步中文化。

## 已知限制

- 编辑器为代码编辑器内核，不支持 Word 式整段水平居中（仅有左右边距内缩）。
- 不做 Markdown 语法高亮，按纯文本读取 `.md` 文件。
