# EricWorkHub

Microsoft Office 与 Windows 文档处理应用启动器、最近文档聚合工具。自动识别已安装应用，集中查看本机记录过的文档，无需配置。

## 使用

从 [Releases](https://github.com/EricZhang233/EricWorkHub/releases) 下载 ZIP，解压后运行 `EricWorkHub.exe`。系统要求为 Windows 10 21H2 或更新的 x64 Windows，并已安装 [.NET 10 x64 运行库](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)和 [Windows App SDK 1.8 x64 运行库](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)。与 EGL 一样，ZIP 使用共享运行库布局，避免重复携带整套运行环境；安装 Office 不等于已具备这两项依赖。

左侧点击应用启动，右侧点击文档打开；应用检测完成即显示，文档在后台补齐。前台每 7.5 秒刷新应用与文档，切到后台即停止计时器，重新聚焦时立即刷新，无需手动操作。单一来源暂不可读时保留其上次记录，成功返回空结果时清空，过期、已删除或不再有可用应用的记录继续过滤。

右键文档可打开所在位置、复制路径或链接；网络目标交给资源管理器，网页目标可打开网页目录。文档变化时后台更新可见缩略图，等待期间保留旧图。重复启动会唤醒已有窗口，后续进程退出。

Notepad、Paint、WordPad 排在 Word 之前，均使用英文应用名与系统图标；仅显示实际存在且可用的应用，系统已移除 WordPad 时自动隐藏。TXT 最近文档统一标为 Notepad，其他来源不会再为同一 TXT 添加 Word 标签。Paint 仅接入自身系统最近记录中的图片，不将所有图片误认为 Paint 历史。

列表末尾固定 **Office Setup**，可打开已安装 Office 的官方修改／修复界面，或从微软下载并运行最新图形安装器。已有 Office 安装或维护进程时直接唤醒，不重复下载；正常运行的后台 Office 服务不会占用该入口。下载只在你选择安装操作后发生，安装器不随 ZIP 分发。

标题栏的“更多”菜单收纳日志、项目主页、发布记录和应用版本信息，布局与 EricGameLauncher 一致。

拖动文档区左侧边框调整两栏宽度，悬停时显示水平调整光标，两栏之间不保留独立拖动柄或空白列。没有文档时保留空白文档区。窗口位置、普通窗口大小、最大化状态和栏宽会自动记忆；最小化关闭不会覆盖普通边界，兼容负坐标多屏与 DPI 变化。

## 最近文档

所有来源自动启用，合并同一文档并保留其关联的应用和来源：

- Office 的常规、账户和应用专用最近记录。
- Office 最近文件缓存和 Windows 最近项目。
- 各应用向 Windows 提供的最近文档跳转列表。
- 全部 Edge 用户配置中访问过的 PDF。

覆盖 Word、Excel、PowerPoint、Access、Publisher、Visio、Project、OneNote 的文档类型，以及保存到本机的 Outlook 邮件、模板、联系人和日历文件。OneDrive 目录内的最近文件会保留 OneDrive 关联。Teams、Microsoft 365 Copilot、Clipchamp、Skype for Business 等应用通过其可用的系统最近文档记录接入；是否有结果取决于应用是否向 Windows 提供了文档目标。

本工具不扫描全部文档目录、不读取邮箱内容、不登录云账户。仅存在于云端应用首页、没有写入本机记录的最近项不在本次覆盖范围内。OneNote 的已打开笔记本与带时间的最近访问记录会分别标明来源；没有访问时间时明确显示修改时间或时间未知。

本地已删除文件自动过滤；网络或暂不可访问的目标保留，打开时由系统检查。网址的路径、参数大小写保持不变，显示位置省略查询参数。

## 日志与权限

普通用户运行，无需管理员权限。Office 注册表和浏览器历史仅作读取，Edge 数据库使用只读连接，不复制完整浏览历史库。

日志位于 `%TEMP%\eric\ericworkhub\log`，与 EricGameLauncher 使用相同的日志模块和规则：异步写入、分级、调用位置、操作耗时、分卷、保留清理及退出刷新。异常提示中的“查看日志”可直接打开文件夹。

开发与验证说明见 [实现与验证](docs/implementation-notes.md)。

开发规范入口为 [AGENTS.md](AGENTS.md)。本地使用 Debug，测试统一运行根目录 `dotnet test`，正式 ZIP 由 GitHub 工作流按发布标签构建。

## 许可证

见 [LICENSE](LICENSE)。作者 [EricZhang233](https://github.com/EricZhang233)。
