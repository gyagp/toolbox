# 验证与已知限制

## 本次 Toolbox 验证（2026-10-12）

- Release 构建：0 警告、0 错误；NuGet 锁文件恢复通过。
- 63 项 Core 断言通过：52 项原有捕获几何、拼接、内容库断言，以及 11 项文件操作路径、根目录、自包含、冲突和重试校验。
- 22 项 Windows 集成断言通过：真实文件占用的进程/PID/路径定位；多进程独立识别；FILETIME 结构对齐；占用时移动失败且源保留；PID 复用与自身进程保护；只结束选中的测试进程；结束后移动成功且字节不变；文件与文件夹回收；扫描取消；空目录/子目录/进程工作目录占用；原生错误码；退出占用进程后空目录移动成功。
- 图片查看器 18 项集成断言通过：PNG、JPEG、BMP、TIFF、WebP 解码；GIF 动画标记和真实渲染帧变化；暂停后像素稳定、继续播放；同目录前后浏览；异步加载竞争；损坏文件提示；离开页面停止动画并释放源文件；返回后重新打开。
- 界面与内容库集成通过：工具总览、四个独立工具页、占用结果渲染；历史内容发现、图片预览、视频筛选/播放/默认不自动播放、释放句柄后重命名、最近删除/恢复、外部变化刷新和空搜索。
- 视觉检查使用应用自身的控件树渲染，覆盖默认紧凑尺寸的首页和文件处理页。
- 完整捕获自检在桌面滚动拼接环节报 `Rendered scroll did not match / NoMatch`，后续滚轮/录屏段未执行。不能把内容库或文件工具测试通过等同于所有截图录屏交互均已复测。

运行可重复检查：

```powershell
dotnet run --project tests/Toolbox.Core.Tests -c Release
dotnet run --project tests/Toolbox.Platform.Tests -c Release
./artifacts/publish/Toolbox.exe --startup-test ./artifacts/startup
./artifacts/publish/Toolbox.exe --library-test ./artifacts/library-test
./artifacts/publish/Toolbox.exe --image-test ./artifacts/image-test ./tests/fixtures/images
```

Windows 文件测试只创建并结束自己的辅助进程，移动和回收自己的临时文件。内容库诊断也使用输出目录中的隔离文件，不改动现有捕获内容。检查退出码和 `result.txt`；界面输出包括 `ui.png`、`screenshot-tool.png`、`scroll-tool.png`、`recording-tool.png`、`file-tool.png`、`file-locks.png` 和 `library.png`。

完整屏幕自检需要保持测试窗口可见、前台桌面不受其他窗口干扰，会短暂显示窗口、操作测试页面的滚轮并录制无声测试画面：

```powershell
./artifacts/publish/Toolbox.exe --smoke-test ./artifacts/smoke
```

Moment 原版本曾完成截图模式、透明 PNG、实际 HWND 遮挡隔离、自动滚动和录屏暂停/继续的集成验证；这是来源项目的历史证据，不代表本次 Toolbox 全部重测通过。原算法和捕获服务随迁移保留，详见 migration.md。

## 尚需人工验证

- 原生文件/文件夹选择器、确认对话框、取消和键盘操作的完整鼠标交互。
- 跨磁盘/网络共享移动、回收站不可用、只读与权限受限文件、海量目录。
- 系统服务和受保护进程的权限失败；不自动请求管理员权限。
- 截图拖动、模式快捷键、Esc、快捷键冲突、复制到其他程序。
- 混合 DPI、多显示器、负坐标、显示器热插拔。
- 浏览器/PDF/编辑器中的真实滚动、固定标题、动画与滚轮惯性。
- 系统声音、麦克风权限、设备切换、长时间录制、磁盘不足、睡眠恢复。
- 无预装运行时的干净 Windows 10/11。

## 功能边界

- 文件与目录句柄分别查询，但不保证覆盖所有远程文件系统、驱动或受保护进程；无结果不等于无占用。目录查询不报告其自身的临时句柄；无法核对身份的进程可显示但不能被结束。扫描跳过链接/重解析点，报告不可读的路径。
- 图片格式依赖 Windows 解码器，WebP 等扩展格式在缺少解码器时显示错误；TIFF 显示第一页。当前未提供 ICO、SVG、PSD/RAW 编辑或多帧 TIFF 翻页。
- 只结束明确勾选并确认的进程，不结束其子进程；查询与操作之间仍可能出现新占用，届时再次查询。
- 跨磁盘移动可能部分完成。发生失败后需核对源、目标；已有同名目标会拒绝自动重试覆盖或合并。
- 文件工具默认使用 Windows 回收站，无法回收时由 Windows 额外确认。内容库自己的“最近删除”仍位于 `.shike-trash`。
- 录屏为整个显示器；自动长截图为实验功能，不处理固定页头或不稳定内容。
- 最小化、受保护窗口、DRM 和 UAC 安全桌面遵循 Windows 捕获限制。
- 尚无标注、托盘、自定义快捷键、设置持久化、安装程序、代码签名或 ARM64 包。
