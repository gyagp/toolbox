# 工具箱：技术决策

## 产品约束

- 仅 Windows，软件名为「工具箱」，英文标识为 Toolbox。
- 原生 WinUI 3 界面，不使用浏览器或 WebView 实现界面。
- Toolbox 是可扩展的小工具集合；捕获与内容库来自 Moment（时刻 / Shike）。不沿用原项目的发布地址。
- 首个迭代以可验证的截图、实验性长截图、显示器录屏为范围。

## 分层

`Tools/ToolCatalog.cs` 定义工具标识、分组、名称、图标和简介。侧栏和捕获页面的顶部切换使用同一组工具标识，`MainWindow.NavigateAsync` 负责选择对应页面。启动直接进入截图，不再保留工具总览。截图、长截图和录屏显示各自的控制区；捕获状态机、快捷键和原生捕获实现继续共用。

捕获相关条目以 `Parent = CaptureMenu` 归组；侧栏只展示 `TopLevel`，点击“屏幕捕获”直接打开截图。四个功能通过常驻顶部切换按钮进入，没有附加下拉。快捷键和捕获完成后的内容库跳转通过 `NavigationId` 高亮侧栏入口及当前功能。

## PDF 工具

`PdfWorkspace` 保留输入文件的只读字节快照，页面计划保存来源、原页码、附加旋转和新增编辑；撤销/重做只改变计划。PDFsharp **6.2.4 Core（MIT）** 负责导入页面、组合和矢量文字/标记；不依赖 WinForms，也不升级到许可证不同的版本。`System.Security.Cryptography.Pkcs` 显式使用已修补的 10.0.14。`PdfFontResolver` 使用随程序集嵌入的 OFL 中文字体，避免依赖用户的系统字体、联网下载或字体集合格式。字体生成脚本校验输入 SHA-256 并将派生字体重命名。

预览通过 Windows `Windows.Data.Pdf` 渲染单页工作副本。编辑坐标以用户当前所见页面为比例基准，并保存创建时旋转；导出时按原 CropBox、原旋转和创建时旋转转换回 PDF 坐标，后续旋转会连同编辑一起旋转。文字和批注文本框会裁切在框选区域内，原有正文不被改写。

遮盖页面不能调用矢量导出。工作区含遮盖时，所有导出页（包括拆分组）都先渲染为 PNG，再新建只包含图像的 PDF。遮盖还会在最终像素上再次涂黑，以覆盖绘制在页面内容之上的原注释。新文件不携带原文字流、字体、注释、附件或原文档元数据；其余可见内容保留为图像。此模式没有文字层，约 200 dpi，最长边最多 4096 像素。

输出只允许新路径：先写同目录的唯一临时文件，刷新并重新打开校验后，以不覆盖的移动提交；失败/取消清理自己创建的临时文件。拆分先写入独立临时目录，全部成功后再命名为新的结果目录。原件与同名输出均不覆盖。密码/编辑权限受限、交互表单和签名文件不进入编辑流程；正常页面副本不承诺保留文档级书签或附件。

`PdfToolsView` 提供页码分组、顺序管理、框选编辑与撤销，切换工具保留工作区。处理期间支持取消；关闭窗口先等待活动操作结束，再询问是否放弃未导出的修改。取消关闭不会清空工作区。`--pdf-test` 用隔离 PDF 验证中文、拆分、旋转/裁切坐标、图像化结构、原件保留和关闭保护。

`src/Toolbox.Platform` 是不依赖 WinUI 的 Windows 适配层：`LockInspector` 使用 Restart Manager 分批注册文件，有关联进程时逐文件确定对应关系。`DirectoryLockQuery` 使用 `NtQueryInformationFile(FileProcessIdsUsingFileInformation)` 查询目录本身，包括空目录和进程工作目录，不把自己的临时查询句柄列入结果；不支持的文件系统会报告查询警告。遍历不跟随链接，查询可取消。进程身份由 PID 与创建时间组成；结束前在同一个内核句柄上核对创建时间、关键进程标记并调用 `TerminateProcess`，避免分别打开 PID 时的竞态。拒绝 Toolbox 自身和 Windows 关键进程，不调用 `RmShutdown` 或按进程树批量结束。

`FileOperationRecovery` 执行已经确认的原操作，失败后查询占用。默认自动处理仅对来源共享冲突 HRESULT（`0x80070020`、`0x80070021`、`0x80270027`）生效；其他错误只显示诊断。结束前再次检查路径范围及其祖先的重解析点，并重新查询对应文件/目录的当前占用身份。自动流程另跳过服务、桌面基础进程、其他会话及身份不明者。每轮结束前重新验证操作计划，部分移动产生目标冲突时不继续结束进程；最多两轮重试，同一 PID/创建时间最多尝试一次。自动重试沿用首次确认的模式和路径；用户发起的新一轮手动彻底删除重试仍要求输入名称。取消贯穿扫描、结束前和重试等待阶段；结束已发出的进程需等待其退出，最长 10 秒，不自动恢复进程。页面保留处理记录和自动处理开关。

`ShellFileOperation` 在专用 STA 线程调用 `IFileOperation`，支持文件/文件夹移动与回收站删除。Core 中的 `FileActionPlan` 保存用户确认的意图，执行和重试前检查源、目标、目录自包含与冲突。Shell 错误回到页面进行占用诊断；无法回收的内容保留 Windows 的额外警告。文件操作期间暂停工具切换、捕获快捷键与窗口关闭，避免操作交叉或中断。

`FileAction.PermanentDelete` 是独立意图，不复用回收标记。`ShellFileOperation.FlagsFor` 明确分离移动、回收和彻底删除；彻底删除不传 `FOF_ALLOWUNDO` 或 `FOFX_RECYCLEONDELETE`。继续使用 Windows Shell 处理目录及联接，不添加手写递归删除或失败后自动升级删除模式。`FileToolsView` 以完整路径和名称输入确认首次彻底删除及手动重试，默认取消；开启自动处理时，原确认涵盖本轮操作的两次自动重试。手动结束进程重试路径先确认删除，之后才结束用户选择的进程。批量目录删除可能部分完成，界面明确提示，不能回滚已删除内容。未知动作在规划和 Shell 参数层均拒绝。

文件工具每次交互创建一个取消令牌，传入 `IFileOperationProgressSink` 的操作前、操作后和进度回调，取消时返回失败 HRESULT 停止后续处理。取消不是线程强杀，也不尝试回滚已完成文件。Shell 原生取消 HRESULT 转为取消任务，界面不会继续查锁或自动重试。占用扫描共享取消信号；确认对话框会关闭。主窗口在收到关闭请求后发出取消，等 `BusyChanged(false)` 才关闭。底部固定取消条不随内容滚动；Windows 的单步调用可能有取消延迟。

`FileOperationProgress` 把 Shell 的 `UpdateProgress` 工作量估算转换为百分比；工作量不是字节或文件数，未知/无效总量保持不确定状态，完成前最多显示 99%。`IShellItem.GetDisplayName(SIGDN_FILESYSPATH)` 提供正在处理的项目路径，无法取得时保留源路径。`FinishOperations` 回调和 `Post*` 成功/调度通知都不能证明完成，只有 `PerformOperations` 返回、HRESULT/取消标志及源路径检查通过后才发布 Completed/100%。恢复流程对查询占用、结束进程和重试发布独立阶段并清空比例。`LatestUiProgress` 合并密集回调，保留最新快照，避免大量小文件导致 UI 队列堆积；取消和旧操作的延迟更新仍受令牌/操作身份检查保护。计时从文件操作确认后开始，包含自动恢复时间，不伪造剩余时间。

`tests/Toolbox.Platform.Tests` 只启动自建的文件占用辅助进程，验证实际占用定位、选择结束、PID 创建时间校验、移动重试和回收站操作；清理仅限自己的进程与临时目录。

`ShellFileOperation` 的进度回调保留每个项目的原始 HRESULT，向用户区分共享冲突、源/目标权限和取消，而不只显示笼统的失败。

`ImageViewerView` 使用 WinUI BitmapImage 和 Windows 图像解码器，不引入额外图像运行库。GIF 使用原生动画播放。每次加载附带版本标记，避免旧任务覆盖新选择；离开页面停止动画、清空图片并释放输入流，避免妨碍文件工具。原始大小按屏幕缩放换算到物理像素，超长图片的适应显示不受 ScrollViewer 最小缩放倍率限制。

全屏由主窗口的 `AppWindowPresenterKind.FullScreen` 实现；保留原 presenter、位置、大小和最大化状态，退出后恢复。看图控件隐藏普通界面并显示可自动隐藏的浮动控件，预览键盘事件负责翻图和 Esc/F11。打开损坏图片也会保留其目录位置，避免下一张操作卡在同一文件。

`ImageLaunchRequest` 解析图片路径和可选的 `--fullscreen`，普通无参数启动仍进入工具总览；路径启动立即进入图片查看器。`ImageFileTypes` 统一关联、选择器和目录过滤的扩展名。`ExplorerImageComparer` 使用 Windows 自然数字排序。

`ImageFileAssociations` 仅在用户点击关联按钮后注册 HKCU 下的 ProgID、Applications/SupportedTypes、OpenWithProgids、Capabilities 与 RegisteredApplications。打开命令同时引用可执行文件和 `%1` 参数，支持含空格的路径；不写扩展名默认值或 UserChoice。注册测试只操作随机命名的隔离注册表分支。

`src/Toolbox.App` 是 .NET 10、WinUI 3 的 unpackaged 桌面应用。主窗口负责捕获状态、错误提示、预览、全局快捷键和生命周期。单次只允许一个捕获会话，录屏结束后等待编码完成再恢复空闲状态；关闭时也会等待保存。

`Services/NativeMethods.cs` 集中封装 Win32：枚举显示器和顶层窗口，通过 GDI BitBlt / DIB 读取 BGRA 像素，通过 RegisterHotKey 注册快捷键。`Views/SelectionWindow.cs` 使用整个虚拟桌面的静态画面，工具栏提供矩形、窗口、全屏和任意形状模式，将逻辑坐标映射回物理像素，保留负坐标显示器的偏移。窗口按原生 Z 顺序命中测试，过滤隐藏、最小化、cloaked、桌面和工具窗口；轮廓使用 DWM 可见边界。`WindowCapture` 通过 ScreenRecorderLib 的 Windows Graphics Capture 窗口源捕获实际 HWND，不退化为包含其他应用的桌面裁剪。任意形状使用偶奇规则填充、透明背景及 PNG alpha。

`AutoScrollDriver` 将鼠标放在所选内容区域，聚焦目标，发送真实滚轮输入；每次等待稳定画面后交给拼接器。每次输入前都验证前台窗口、窗口位置和鼠标所在区域；焦点变化、目标移动或鼠标离开会停止。停止操作取消同一会话的令牌，后续不再发送输入；已接受的条带仍会保存。连续三次内容不变视为到底或无法滚动。长截图仍限定在所选显示器内的单个窗口区域。

`ImageStore` 通过 Windows BitmapEncoder 编码 PNG，通过 Windows 剪贴板复制图片。`RecordingSession` 封装 ScreenRecorderLib，以原生图形捕获 / Media Foundation 编码 H.264 MP4。是否启用系统回放音频与麦克风由界面明确选择。完成和失败事件映射为 Task，UI 通过 DispatcherQueue 接收状态。

`src/Toolbox.Core` 不依赖 Windows，包含像素帧和滚动匹配算法。算法比较相邻视口中不同向下偏移的重叠区域，接受误差小且不歧义的匹配，将新增底部条带追加到结果中。失败时保留最后一次成功帧，供用户回退重试。无可靠特征、固定标题、动画或跳页不在首版保证范围内。

`tests/Toolbox.Core.Tests` 是无需测试框架依赖的断言程序，覆盖像素重建、无重叠/向上移动拒绝、大小限制、负坐标桌面、反向矩形选择及凹多边形透明遮罩。App 的显式 `--smoke-test` 入口验证四种模式的选择逻辑、PNG alpha、实际 HWND 的遮挡隔离、原生滚轮自动滚动和停止保护，以及媒体集成，不在正常启动时运行。

## 内容库与界面

v0.1.2 使用固定侧栏、缩略图网格及独立预览面板。`LibraryView` 是 WinUI 原生控件，不使用 WebView 或位图模拟交互。`CaptureLibrary` 位于 Core 层，直接扫描两处保存目录，不维护易失同步的独立文件数据库；文件的名称、大小和时间是索引来源，未完成的 `.partial.mp4` 不显示。搜索与排序不触碰文件。

重命名和恢复使用不覆盖的文件移动。软删除移入同一保存目录内 `.shike-trash/<GUID>/<原文件名>`，新进程也能恢复；永久删除仅允许针对经过路径校验的归档文件。目录外路径及符号链接不可操作。界面在软删除和永久删除前显示确认，默认选取消。

缩略图按可见容器异步加载，限制并发和缓存数量；预览请求使用版本标记避免快速切换后回写旧图片。图片限制解码尺寸，普通图片初始适应面板，长图适应宽度。MediaPlayer 默认不播放，离开内容库、开始捕获或文件操作前释放播放器。FileSystemWatcher 合并变更事件，刷新时尽量保留当前选择。

## 磁盘清理的安全边界

`DiskCleanupInspector` 是只读适配器：仅枚举文件系统元数据并调用 `SHQueryRecycleBinW`，不打开文件内容，没有写入、删除、移动、回收、修改注册表、提权或结束进程的代码路径。生产扫描范围来自固定的六类已知目录和所选盘符；不使用可任意重定向的 `TEMP` / `TMP`，不遍历整个磁盘或用户目录。检查路径祖先和枚举项的重解析、脱机和云召回标记，跳过不可读项；每个目录类别有时间、条目和深度上限，输出区分缺失、不完整与不可读。大小是逻辑文件大小估算，不是物理磁盘可释放空间，硬链接、压缩和文件系统并发变化可能影响数值。

`WindowsCleanupLauncher` 只启动系统目录下的 `cleanmgr.exe /d <单个盘符>`，或打开 `ms-settings:storagesense` 设置页。不使用 `/sagerun`、`/verylowdisk`、预设注册表或自动确认；Windows 原生对话框保留用户选择和确认流程，现有勾选状态由 Windows 管理。Toolbox 不保证用户在 Windows 中选择的任何内容都可安全删除。之所以不实现按年龄批量删除临时文件，是因为临时目录也可能包含必要的安装、恢复和未保存数据。

`DiskCleanupView` 不调用现有的 `ShellFileOperation` 或 `LockInspector`；安全边界独立于手动文件处理工具。离开页面或关闭窗口会取消只读扫描。测试使用隔离目录验证只读性质、占用、ACL 拒绝、目录联接、过滤范围、扫描上限和启动参数；不对真实缓存执行清理。`--cleanup-test` 只渲染合成结果，不启动 Windows 清理。

## 发布流程

以 x64、自包含、多文件 ZIP 发布。多文件形式适配 WinUI PRI 资源和混合模式原生录屏依赖，避免单文件解包与加载路径问题。`scripts/package.ps1` 捆绑 .NET、Windows App SDK、Visual C++ CRT 和许可证，生成 ZIP 与 SHA-256。CI 对普通提交做构建与核心测试，标签触发 Release。

ARM64、安装程序、代码签名和自动更新留待后续版本。不要在未验证架构依赖、捕获和编码的情况下仅修改 RID 来宣称支持。
