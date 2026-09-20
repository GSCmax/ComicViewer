# ComicViewer

Windows WPF 漫画查看器，读取 ZIP/RAR（含 CBZ/CBR）中的图片和视频，支持自然排序、密码历史、连续滚动、页码跳转和内存视频播放。

## 构建

需要 Windows x64、能够构建 .NET 8 项目的 SDK，以及 .NET 8 Desktop Runtime。

原生依赖放在 `ComicViewer/Native/win-x64/libmpv-2.dll`。该二进制不纳入 Git，也不会在构建时自动下载；新环境需要先提供兼容现有 mpv client/render API 的 x64 原生库。NuGet 依赖由项目固定版本引用。当前验证过的原生库为 117,140,992 字节，SHA-256：

```text
44AF8B7F5FF45CF50DB49B9866FE9238E84C4D935122576C4C6F7CE859226E8A
```

```powershell
Get-FileHash ComicViewer/Native/win-x64/libmpv-2.dll -Algorithm SHA256
dotnet build ComicViewer.sln -c Release
dotnet run --project ComicViewer -c Release
```

可以在程序命令行中传入漫画压缩包路径。发布应用时只发布应用项目：

```powershell
dotnet publish ComicViewer/ComicViewer.csproj -c Release
```

## 缓存和加载

- 编码媒体缓存上限是 **1 GiB（1,073,741,824 字节）**，由 `ReaderSession.DefaultCacheLimit` 定义。
- 申请内存之前检查本次文件大小，按 LRU 淘汰空闲条目；预取不会淘汰当前视口受保护的条目。
- 播放、封面和图片解码通过 lease 使用同一份缓存数据。使用中的数据仍计入缓存，不能被淘汰；容量暂时不足时不突破预算。
- 每个归档只有一个读取通道。同一个条目的并发请求合并，图片解码并发全局最多 2 个。
- 根据 ZIP/RAR 索引中的有效长度直接读取到最终缓存数组，使用 `Stream.ReadExactly` 检测截断；不把加密填充当成图片数据，也不再经过中间缓冲和扩容。
- 预取范围为当前页、前方最多 12 页和后方最多 4 页，并优先处理可见页。调度不随整本漫画的页数重新排序。
- 解码位图仅保留可见页与邻页。根据约 128 MiB 的解码像素预算分配每页分辨率；窗口扩大时先保留旧图，异步换成新图。
- 视频封面直接返回冻结位图，不再先编码 PNG 再解码。播放与封面仍使用内存源，不写媒体临时文件。

1 GiB 是编码缓存预算，不是整个进程的内存上限。解码中的临时图像、换图时的新旧位图、原生播放器和 GPU 资源还会占用内存。

## 代码边界

| 模块 | 职责 |
| --- | --- |
| `ArchiveSession` | 条目索引、直接读取、打开及密码重试；归档自身由阅读会话持有 |
| `ReaderSession` | 每本漫画的缓存、预留、请求去重、lease 和取消/释放 |
| `PageImageLoader` / `ImageDecoder` | 统一视口加载与有限预取、解码并发、错误状态和宽度档位 |
| `PlaybackController` | 单播放器宿主切换、播放请求、活动视频 lease |
| `MpvVideoPlaybackEngine` | 单独线程处理原生命令和事件，按媒体版本过滤旧通知 |
| `MpvOpenGlVideoView` / `Interop` | OpenGL 呈现和原生 ABI |
| `VideoThumbnailService` | 单一对象串行管理 mpv 封面，直接执行命令并从原生像素创建位图 |
| `MainWindow` / `ComicPage` | 用户输入、导航和显示状态；可见页直接取自 WPF 已实现的容器 |
| `Themes/ReaderStyles.xaml` | 原有窗口控件样式与模板 |

关停顺序为取消工作、等待读取/解码结束、关闭归档；播放先停止原生媒体，再释放对应 lease。事件线程退出后才释放 mpv 句柄。原生事件和唤醒的处理遵循 [mpv client API 的生命周期约束](https://github.com/mpv-player/mpv/blob/master/include/mpv/client.h)。

## 回归检查

`ComicViewer.Tests` 是无需额外测试框架包的可执行回归工程，失败时返回非零退出码。生成的素材放在独立临时目录中，运行结束后清理。窗口检查在屏幕外运行，不操作用户已打开的应用。

```powershell
dotnet run --project ComicViewer.Tests -c Release
```

默认覆盖缓存边界、去重、取消、会话隔离、淘汰、加密 ZIP、重复文件名、加密填充与截断边界、坏图、解码，以及 PNG/JPEG 的 WPF 实际渲染、跳页和切包。完整视频检查需要一个可播放的小 MP4。可使用已安装的 FFmpeg 生成合成素材：

```powershell
$videoFixture = Join-Path $env:TEMP 'ComicViewer-refactor-fixture.mp4'
ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=160x90:rate=10' -t 3 -c:v libx264 -pix_fmt yuv420p -an $videoFixture
dotnet run --project ComicViewer.Tests -c Release -- --video $videoFixture
```

默认检查共 16 项，加上视频检查共 19 项。可额外传入 `--archive "压缩包路径"`，逐张读取、解码并检查首张、第二张、中间页和末页的实际渲染；加密包会使用应用已经保存的密码历史，不输出密码。已用一个含 40 张图片的加密 RAR 验证读取，归档流末尾的加密填充不再误触发缓存超限。solid RAR 与更多显卡、编码器组合仍需对应真实素材做兼容性回归。
