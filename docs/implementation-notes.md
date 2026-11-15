# 实现与验证

## 范围与数据来源

目标是聚合本机可读取的记录，不把文件存在、文件修改或云端同步当作真实访问。所有采集器均在后台执行，应用检测完成即发布可启动应用，最近文档随后补齐，文档来源耗时不阻塞应用首屏。

来源快照保存在 Core 的采集范围内，按 Office 应用／版本／注册表视图、Office 缓存目录、Windows Recent、应用 Jump List ID 分开保存；Edge 使用既有的逐配置缓存与退避机制。读取成功以本轮结果替换对应快照，包括空结果；失败时只保留该来源上次的成功记录。停用或移除的来源清除快照，保留项仍逐轮过滤过期、已删除或没有可用关联应用的目标，不用旧的聚合列表覆盖已成功清空的来源。未知文档时间的失败快照也按上次成功读取时间到期。存在检查不吞掉权限异常，避免把失败错误地当成空记录。

| 应用 | 本机来源 | 说明 |
| --- | --- | --- |
| Notepad | Windows Recent、应用 Jump List，以及其它来源中的 TXT 记录 | `.txt` 统一归属 Notepad；Notepad 不可用时过滤，不回退为 Word 标签 |
| Paint | 应用 Jump List | 仅收该应用提供的常用图片目标，不把其它应用打开的图片全部归为 Paint |
| WordPad | Windows 提供的应用 Jump List | 兼容仍保留 WordPad 的系统；只检测实际 `wordpad.exe`，不使用 `write.exe` 占位 |
| Word、Excel、PowerPoint | Office File MRU、User MRU、Office 缓存、Windows Recent、应用 Jump List | 读取账户分支及 32/64 位视图，兼容 Office 14/15/16 系列键 |
| Access | 标准 MRU、旧版 Settings/MRU 兼容项、Windows Recent、Jump List | 数据库、模板及旧版数据库类型 |
| Publisher | 标准 MRU、Windows Recent、Jump List | `.pub` |
| Visio | 标准 MRU、Application/LastFile 兼容项、Windows Recent、Jump List | 绘图、模具、模板，含启用宏的类型 |
| Project | MS Project 的最近文件兼容项、Windows Recent、Jump List | `.mpp`、`.mpt`；不连接 Project Server |
| OneNote | 标准 MRU、OpenNotebooks 兼容项、Windows Recent、Jump List | `.one`、`.onetoc2`、`.onepkg` 及合法 `onenote:` 目标；OpenNotebooks 不伪造访问日期 |
| Outlook | Windows Recent、Jump List | 已保存的 `.msg/.eml/.oft/.ics/.vcf`；不枚举邮箱或临时附件目录 |
| OneDrive | Windows Recent 中的已访问同步目录文件、Jump List | 只关联本机记录；不遍历整个同步目录或把同步时间当访问时间 |
| Teams、Microsoft 365 Copilot、Skype for Business | 应用提供的 Jump List | 只收文档目标，过滤任务、聊天或可执行程序目标；无本机文档目标时可以为空 |
| Clipchamp | 项目类型及应用 Jump List 中的媒体目标 | 不把所有视频文件错误标为 Clipchamp 历史；不解析私有 IndexedDB |
| Edge PDF | Default 与全部 Profile N 下的 History | 只读 SQL，禁用连接池，包含 WAL 最新提交；库被独占时保留缓存并在后台退避重试，不弹警告、不绕过锁 |

应用 ID 来自当前用户可读的开始菜单快捷方式 `System.AppUserModel.ID` 和已安装包的 `AppUserModelId`。读取 Jump List 使用公开 `IApplicationDocumentLists` 接口，包含 Recent 类别中返回的固定项；不解析私有 `automaticDestinations` 文件，也不保证读取应用自行维护的自定义类别。

应用顺序为 Notepad、Paint、WordPad，再接原有 Office 应用。传统应用路径逐个检查文件存在，已缓存的可执行文件失效时不再视为已安装；商店版仅接受 `Package.Status.VerifyIsOK()` 通过且有非空启动项的当前用户应用包，并在打开前重新检查包与启动项。传统应用仍在打开前检查 EXE。Notepad 与 Paint 同时兼容系统 EXE 和商店包；WordPad 路径缺失时隐藏。[微软移除说明](https://learn.microsoft.com/en-us/windows/whats-new/deprecated-features-resources)指出 WordPad 自 Windows 11 24H2 起移除；本工具不安装或恢复该组件。

Office 注册表布局不是跨版本的完整公开 API：上述兼容项只读、逐项验证，缺失时跳过，以 Windows Shell 来源补足。Windows 隐私策略禁用最近记录，或应用从未发布本机记录时，结果可以为空；本程序不会修改系统策略。

时间语义分为访问、记录更新、文件修改和未知。合并优先保留已知的访问／记录时间，随后比较最新时间；合并来源和应用集合，避免先到的旧记录遮住后到的新记录。

Edge 没有 History 文件、尚无 `urls` 表，或者没有任何 PDF 访问行，都是正常空结果。SQLite BUSY/LOCKED 是暂时占用：只写 Info 级别的重试记录，保留上次成功读取的数据，按 15 秒起、最多 5 分钟的间隔退避。损坏的数据库、权限或不兼容结构仍记录并显示真实异常，不把读取失败伪装成空历史。

应用和文档两个列表统一按稳定键增量更新：保留 ViewModel 和图标；无变化时零集合事件，属性变化仅发对应通知，真实排序变化使用 Move，新增／删除只改对应行。显式关闭容器过渡动画，`ItemsStackPanel.ItemsUpdatingScrollMode=KeepItemsInView` 保持首个可见项。界面不显示文档计数和刷新时间。

没有文档时保留空白区域，不显示提示文本。内容区顶部 Padding 为 0，收紧应用／最近文档标题行上方留白，不改变自定义标题栏高度。两栏仅使用两个布局列，无独立拖动柄或分隔空白列。文档卡片最左侧 5 DIP 为边框拖动命中区，悬停显示系统水平调整光标；按下后捕获指针，以窗口内容坐标计算增量，避免边框移动导致坐标反馈。释放、取消、捕获丢失或卸载时终止拖动，栏宽继续按既有边界约束保存。

本地文件在后台采集时记录修改时间与长度作为预览修订标记，与记录时间一起构成图标缓存键。仅为可见容器请求替换缩略图，等待期间不清空旧图；Core 请求代次使旧异步完成无法覆盖新修订。文件内容变化不依赖最近访问时间是否同步变化。网络与云端目标不为缩略图进行本地文件探测。

文档右键菜单由 Core 解析复制与定位目标。普通路径和 UNC 用资源管理器的文件选择入口；网页打开去除文档参数的父目录；Office 与 OneNote 协议定位时解析底层资源，复制时保留完整链接。复制由 UI 写入并持久化系统剪贴板，不将路径或令牌写入日志。

前台计时器间隔为 7.5 秒，每次重新检测应用并采集文档；不命中应用检测的两分钟缓存，也不触发每轮应用图标强制重载。失焦或最小化时真正停止计时器，重新聚焦立即刷新，关闭后不再安排任务。Core 调度策略合并扫描期间的重复 Tick，重新聚焦发生在慢扫描期间时只补一次刷新。界面没有手动刷新入口或 F5。

单实例参照 EGL 的命名互斥体与命名管道，在 WinUI 初始化前决策。作用域按当前用户及会话隔离，管道仅允许当前用户；后续进程授权主进程请求前台后发送定长唤醒消息，并退出而不扫描应用。主实例在 UI 线程恢复最小化窗口并激活，正常退出关闭管道并释放互斥体。隔离回归实际启动测试子进程验证通知，不启动产品界面。

窗口状态在 Core 保存最近普通边界、最大化标记、栏宽和 DPI。UI 只报告原生窗口模式和边界，最大化或最小化时不把其尺寸覆盖到普通边界；最小化保留最后的非最小化模式。重新打开先恢复普通窗口位置，再按目标显示器 DPI 调整宽高并约束到工作区，最后恢复最大化；不对多屏绝对坐标整体缩放。

## 微软与上游依据

- [Office MRU 与 FILETIME 格式示例（Microsoft DevBlogs）](https://devblogs.microsoft.com/scripting/weekend-scripter-using-powershell-to-aid-in-security-forensics/)
- [Office 最近文件行为（Microsoft Support）](https://support.microsoft.com/en-us/office/collab-files/customize-the-list-of-recently-used-files-in-office-apps)
- [IApplicationDocumentLists.GetList](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationdocumentlists-getlist)、[SetAppID](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationdocumentlists-setappid)
- [System.AppUserModel.ID](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)、[GetAppListEntriesAsync](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.getapplistentriesasync)
- [Office URI Schemes](https://learn.microsoft.com/en-us/office/client-developer/office-uri-schemes)、[OneNote 笔记链接](https://support.microsoft.com/en-us/onenote/create-links-to-notebooks-sections-pages-and-paragraphs)
- [Microsoft.Data.Sqlite 只读模式、超时与池化](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)、[SQLite WAL](https://www.sqlite.org/wal.html)
- [SQLite 锁冲突处理](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)、[列表更新时保持视口](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemsstackpanel.itemsupdatingscrollmode)
- [Windows 版本与最低平台版本](https://learn.microsoft.com/en-us/windows/apps/get-started/versioning-overview)
- [WinUI 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

## 日志对齐契约

`Core/LogService.cs` 与 `Core/SensitiveText.cs` 移植自本机 EricGameLauncher 的 `Core`，参照提交为 `a271085906ae47727f862df52fbffda71106248b`。仅调整命名空间和日志故障兜底文件的应用名称，保持模块算法和输出格式一致。

- 四级：Debug、Info、Warn、Error；UTC 时间、源文件／行／成员、操作 ID、异常类型／消息／堆栈。
- 4096 条有界队列，单写入者，每批最多 256 条；溢出时低级别计数，Warn/Error 同步写 critical 文件。
- 每会话独立文件，UTF-8 无 BOM，5 MB 分卷；启动时按 30 天、100 MB 规则清理。
- 操作开始／结束及耗时；进程退出或窗口关闭时完成队列并最多等待 5 秒。
- 相同的 GitHub 令牌脱敏；应用层避免主动记录完整文档地址和命令行。

测试既校验归一化源码 SHA-256，又在 EGL 仓库存在时直接比较两个源文件。后续同步 EGL 时应一并更新此契约与测试基准，不另写一个“相似”的日志器。

## 标题栏对齐

对照同一 EGL 提交的 `UI/MainView.xaml`、`UI/MainView.xaml.cs` 和 `AppIconService`：48 高标题栏，32 大小的内嵌图标，标题与中间区域负责拖拽，右侧独立操作组；44×32 按钮、8 间距与相同的“更多”图标及菜单对齐方式。窗口原生图标与标题栏图标均从内嵌资源在内存中取得，HICON 在关闭时释放。

侧栏应用图标来自本机系统：商店应用从 `shell:AppsFolder` 注册项获取，传统应用从检测到的 EXE 获取，优先使用 `IShellItemImageFactory` 的仅图标模式；失败时尝试 Shell 文件图标并最终显示通用占位。后台线程显式初始化 COM，HBITMAP、HICON 和 COM 接口均在同一读取操作中释放；图片只保存在有界内存缓存中。应用入口或图标来源改变时刷新图标，等待期间保留现有图片；文档优先使用系统缩略图，缺失时使用系统扩展名图标。删除 `Assets/Office` 与对应嵌入声明，仅保留 EricWorkHub 自身图标。依据：[IShellItemImageFactory.GetImage](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage)、[SHGetFileInfo](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfow)。

Outlook 同时存在传统版与商店版时，使用实际 `OUTLOOK.EXE`；仅有商店版时使用已验证的 AUMID。零字节启动别名不作为真实 EXE 或图标来源，避免 Shell 返回有效的通用图标后阻断后备读取。图标日志记录应用键、来源和字节数，Shell 失败记录 HRESULT。可用 `dotnet run --project Test/Regression.csproj -- --diagnose-icons <隔离输出目录>` 做只读图标诊断，生成各入口的 PNG 与像素／哈希摘要，不启动 Outlook 或读取邮箱与文档。

右侧留白按 `TitleBar.RightInset / XamlRoot.RasterizationScale + 12` 动态计算，加载、尺寸变化与 XamlRoot 变化时更新，并在关闭时解除事件订阅。版本号采用 EGL 的 `Ver.{Version}` 格式，放置在右下角；更多菜单底部包含同样布局的应用图标、名称、版本和开发者卡片。本工具的日志及项目／发布链接使用该标题栏，不恢复无意义的设置页或游戏专用菜单。

## 应用 Tab 与管理入口

原“应用”标题替换为文字内置的分段 switch。MsOffice／ArtCraft 使用同一圆角底框和一个选中底块，文字位于各自分段内；内部通过同组 RadioButton 提供键盘与辅助功能语义，自定义模板隐藏单选圆点并保持统一外观，主题资源兼容明暗模式。默认选中 MsOffice，选择只存在于当前窗口 UI 中，不写入注册表或缓存。未选中分段单击只切页，已选中分段再次单击显示对应管理菜单，右键直接显示对应菜单而不切页。方向键及 Home／End 可切换 Tab 并聚焦选中分段。现有应用列表保留在 MsOffice；ArtCraft 暂留空白，未配置管理动作时不创建空菜单。切页不清空已检测应用、图标或文档。

Core 快照只包含实际已安装应用，不再插入管理器行。MsOffice Tab 菜单提供安装与修复操作；执行操作时，在后台查找当前会话内可信的 Office 安装或维护进程，存在时由 UI 层恢复窗口并请求前台激活；Windows 拒绝前台切换时闪烁任务栏并提示，不再次下载。会话 0 常驻服务与无界面的后台更新客户端不会阻断管理入口，同名的其它软件安装器须通过微软签名及 Office 产品身份筛选后才可能匹配。

菜单提供两个操作：

- 修改／修复：读取 Office 卸载注册项的 `ModifyPath`，只接受微软签名的 Office 维护程序和交互参数，通过 Shell 的 `runas` 请求管理员权限并以程序目录为工作目录；不附加 `RepairType`、强制关闭应用或静默修复选项。旧版 MSI 仅接受交互式 `/I` 修改命令。无法安全解析时打开 Windows 已安装应用页面，由用户选择 Office。
- 最新安装器：每次请求微软 `https://c2rsetup.officeapps.live.com/c2r/download.aspx`，固定 `productReleaseID=O365ProPlusRetail&platform=x64`，语言取当前 Windows 界面语言，不读取既有产品来选择安装版本。该入口返回带产品信息的图形安装包；禁用缓存复用，限制 HTTPS 微软下载域的重定向与下载大小，验证 Windows Authenticode 信任、微软发布者和 Office 产品身份后，再以空参数交由系统启动，行为等同双击。产品与许可证处理交给微软安装器，不使用裸部署引导器，不生成部署 XML 或附带 CLI 参数。

安装器固定缓存在 `%TEMP%\eric\ericworkhub\office-setup\OfficeSetup.exe`，每次安装操作重新下载、校验后覆盖该文件，不生成编号文件名或版本目录。下载中的 `.part` 文件在失败、取消或完成后清理；校验失败不覆盖已有文件，也不会自动运行旧缓存。进程存在检查在下载前、覆盖前与启动前执行，进程内互斥与共享文件锁防止多次点击、多个 Hub 进程并行下载。关闭 Hub 会取消未完成的下载，不终止已经启动的微软程序；用户取消系统提权提示属于正常取消。

签名校验使用系统 WinVerifyTrust 与已验证的签名证书，不引入额外安装管理组件。日志记录下载、校验、进程复用与维护请求。根目录 `dotnet test` 使用模拟网络、进程和启动器覆盖各分支，不执行真实安装或修复。内部只读联网探测可运行 `dotnet run --project Test/Regression.csproj -- --probe-office-manager <隔离目录>`，真实下载并验证文件、读取维护注册项，但启动器被替换为空操作。

依据：[微软 Office 安装说明](https://learn.microsoft.com/en-us/microsoft-365/admin/setup/install-applications)、[微软 Office 修复说明](https://support.microsoft.com/en-us/office/repair-an-office-application-7821d4b6-7c1d-4205-aa0e-a6b40c5bb88b)、[WinVerifyTrust 数据与缓存策略](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data)。

## 本地验证与发布

使用 .NET 10 SDK 和 Windows SDK 26100：

```powershell
dotnet build EricWorkHub.csproj -c Debug
dotnet test
```

回归仅使用自己的临时样本，覆盖 MRU、协议与身份、跨源跨应用合并、各应用扩展名、Windows Recent、只读 SQLite/WAL、缓存失效、刷新互斥／取消、多屏边界及日志压力。不会打开真实 Office 示例文档或改写真实浏览器历史。

普通编译不自动运行测试、打包或删除 bin/obj。本地只编译 Debug，正式包只由 GitHub 构建；本地测试不打开 Office 文档、不恢复实机自动化。测试工程通过 `dotnet` 宿主直接运行测试程序集，根目录 `dotnet test` 仍为统一入口。终端脚本使用 `pwsh.exe`。无法访问 NuGet 审计源时，可在本地明确使用 `dotnet test -p:NuGetAudit=false` 验证缓存依赖；这不代表完成了依赖漏洞审计。

发行布局与 EGL 一致：共享 .NET 10 与 Windows App SDK 1.8 运行库；仅依赖 WinUI 与 Runtime 组件包，不引用拉入 AI、ML、Widgets、DWrite 的聚合包。未使用的投影组件在依赖图生成前排除；Release 禁用 PDB。XBF 直接内嵌到 UI 程序集，通过 `Application.ResourceManagerRequested` 与 `ResourceNotFound` 从字节资源解析，既不外置 PRI，也不运行时释放 XBF。启动配置通过 SDK 的 `GenerateBundle` 内嵌到 apphost，仅内嵌两份配置，程序集仍为外部 DLL；不启用需要额外兼容约束的 WinUI 单文件发布。

`Package.ps1` 在最终 ZIP 生成后调用同一测试工程的静态包验收器：直接读取 ZIP、PE 元数据和 .NET bundle，不加载产品程序集或运行 EXE。检查 Release XBF 与构建输出一致、文案与源资源一致、apphost 内嵌 deps/runtimeconfig 的范围和格式、共享 .NET 10 与 x64 目标、EXE/DLL 版本，以及依赖图中每个托管／原生资产的存在、版本或位数。验收失败中止发布。测试样本、打包暂存和原生文件版本读取均使用 `%TEMP%\eric\ericworkhub` 下的唯一隔离目录，完成后清理，不在仓库中创建临时目录；指定的发行输出目录只保存最终 ZIP。普通根目录回归覆盖损坏的 bundle 头与越界条目。

参照依据：[微软 WinUI 自定义资源管理设计](https://github.com/microsoft/microsoft-ui-xaml/blob/main/docs/design-notes/custom-mrt-resourcemanager.md)、[Windows App SDK 部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview)。缺少共享运行库的机器需要先安装运行库，Office 企业版本身不能替代这两项依赖。

所有者明确要求查看本机 Release 时，允许使用预览入口，输出目录必须为空：

```powershell
pwsh.exe -NoProfile -File .github/scripts/Package.ps1 -Tag v1.0.0 -OutputDirectory artifacts/preview -Preview
```

版本参数须与根目录 `Version.cs` 一致。脚本在独立输出目录编译，校验目录与 ZIP 契约后只清理本次暂存目录。默认仍只允许 GitHub 构建正式包，预览不会写 GitHub 输出或触发发布。

根目录 `Version.cs` 是产品版本唯一来源；程序集、原生应用清单、界面和 ZIP 包名从该值取得。`Core/` 源码目录提供唯一的业务实现，包括来源读取、合并、检测、刷新协调、启动防抖与窗口布局持久化，直接编入主程序集，不生成独立 Core DLL。UI 保留渲染、图标、交互和窗口消息；用户文案经 `Core/text.cs` 读取主程序集内嵌的根目录 `text.yaml`。测试直接复用同一组 Core 源文件。本项目由所有者明确豁免 CLI，保留独立图形入口。

GitHub 自动流程参照 EGL：

- `build_release.yml` 按指定的既有 `v` 标签检出，先运行根目录 `dotnet test`，再调用 `.github/scripts/Package.ps1` 构建并校验使用共享运行库的精简 ZIP，标签必须精确匹配代码版本。
- `post_release.yml` 在发布后显示构建提示，调用构建流，仅上传本次运行的产物，再从同一标签读取 `.releasenote.md`。发布身份与发布时间变化时停止旧运行；任一步失败会把对应发布退回草稿，保留说明、附件与诊断记录。
- `promote_prerelease.yml` 每日及手动检查最新预发布，满 48 小时且无 `[no-release]`、存在有效 ZIP、同提交最新发布流成功、无 `block-release` 开放 issue 时转正式。

脚本只清理自身新建的独立暂存目录。工作流不把压缩包提交进 Git，也不在发布后改写主分支。自动流程仅提交到本机代码中，实际触发与云端发包须在推送及发布后发生。
