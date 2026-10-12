# 工具箱：技术决策

## 产品约束

- 仅 Windows，软件名为「工具箱」，英文标识为 Toolbox。
- 原生 WinUI 3 界面，不使用浏览器或 WebView 实现界面。
- Toolbox 是可扩展的小工具集合；捕获与内容库来自 Moment（时刻 / Shike）。不沿用原项目的发布地址。
- 首个迭代以可验证的截图、实验性长截图、显示器录屏为范围。

## 分层

`Tools/ToolCatalog.cs` 定义工具标识、分组、名称、图标和简介。`ToolHomeView` 和侧栏从同一目录生成，`MainWindow.NavigateAsync` 负责选择对应页面。截图、长截图和录屏显示各自的控制区；捕获状态机、快捷键和原生捕获实现继续共用。新增工具时在目录添加一项，并接入其独立视图即可。

`src/Toolbox.Platform` 是不依赖 WinUI 的 Windows 适配层：`LockInspector` 使用 Restart Manager 分批注册文件，有关联进程时逐文件确定对应关系。`DirectoryLockQuery` 使用 `NtQueryInformationFile(FileProcessIdsUsingFileInformation)` 查询目录本身，包括空目录和进程工作目录，不把自己的临时查询句柄列入结果；不支持的文件系统会报告查询警告。遍历不跟随链接，查询可取消。进程身份由 PID 与创建时间组成，结束前再次核对，并拒绝 Toolbox 自身和 Windows 关键进程。每次只结束用户选择的单个进程，不调用会批量关闭应用的 `RmShutdown`。

`ShellFileOperation` 在专用 STA 线程调用 `IFileOperation`，支持文件/文件夹移动与回收站删除。Core 中的 `FileActionPlan` 保存用户确认的意图，执行和重试前检查源、目标、目录自包含与冲突。Shell 错误回到页面进行占用诊断；无法回收的内容保留 Windows 的额外警告。文件操作期间暂停工具切换、捕获快捷键与窗口关闭，避免操作交叉或中断。

`tests/Toolbox.Platform.Tests` 只启动自建的文件占用辅助进程，验证实际占用定位、选择结束、PID 创建时间校验、移动重试和回收站操作；清理仅限自己的进程与临时目录。

`ShellFileOperation` 的进度回调保留每个项目的原始 HRESULT，向用户区分共享冲突、源/目标权限和取消，而不只显示笼统的失败。

`ImageViewerView` 使用 WinUI BitmapImage 和 Windows 图像解码器，不引入额外图像运行库。GIF 使用原生动画播放。每次加载附带版本标记，避免旧任务覆盖新选择；离开页面停止动画、清空图片并释放输入流，避免妨碍文件工具。原始大小按屏幕缩放换算到物理像素，超长图片的适应显示不受 ScrollViewer 最小缩放倍率限制。

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

## 发布流程

以 x64、自包含、多文件 ZIP 发布。多文件形式适配 WinUI PRI 资源和混合模式原生录屏依赖，避免单文件解包与加载路径问题。`scripts/package.ps1` 捆绑 .NET、Windows App SDK、Visual C++ CRT 和许可证，生成 ZIP 与 SHA-256。CI 对普通提交做构建与核心测试，标签触发 Release。

ARM64、安装程序、代码签名和自动更新留待后续版本。不要在未验证架构依赖、捕获和编码的情况下仅修改 RID 来宣称支持。
