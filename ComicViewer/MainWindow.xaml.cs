using LibVLCSharp.Shared;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Readers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ComicViewer;

public partial class MainWindow : Window
{
    private const int BackwardPreloadCount = 5;
    private const int ForwardPreloadCount = 10;
    private const double MouseWheelScrollMultiplier = 5d;
    private const double MouseWheelPixelsPerLine = 16d;
    private const int MinimumDecodePixelWidth = 480;
    private const long MaxCachedVideoBytes = 128L * 1024 * 1024;
    private const long CoverProbeStepBytes = 16L * 1024 * 1024;
    private const long MaxCoverProbeBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan CoverProbeFrameTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PageWidthUpdateDelay = TimeSpan.FromMilliseconds(120);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".avi",
        ".wmv",
        ".mkv",
        ".webm"
    };

    private string? _archivePath;
    private string? _password;
    private int _cacheWindowStart = -1;
    private int _cacheWindowEnd = -1;
    private int? _loadingWindowStart;
    private int? _loadingWindowEnd;
    private double _pageWidth = 800;
    private bool _isArchiveLoading;
    private bool _isCacheLoadWorkerRunning;
    private int _pendingCachePageIndex = -1;
    private TaskCompletionSource<string?>? _passwordPromptCompletion;
    private ScrollViewer? _pagesScrollViewer;
    private CancellationTokenSource? _cacheLoadCts;
    private CancellationTokenSource? _coverLoadCts;
    private CancellationTokenSource? _videoPlayCts;
    private double _pendingPageWidth;
    private readonly LibVLC _libVlc;
    private readonly SemaphoreSlim _coverLoadSemaphore = new(1, 1);
    private readonly DispatcherTimer _pageWidthUpdateTimer = new()
    {
        Interval = PageWidthUpdateDelay
    };

    public ObservableCollection<ComicPage> Pages { get; } = [];

    public ObservableCollection<string> PasswordHistory { get; } = [];

    public MainWindow()
    {
        Core.Initialize();
        _libVlc = new LibVLC();
        InitializeComponent();
        DataContext = this;
        _pageWidthUpdateTimer.Tick += PageWidthUpdateTimer_Tick;
        LoadPasswordHistory();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _archivePath = null;
            _loadingWindowStart = null;
            _loadingWindowEnd = null;
            CancelCacheLoads();
            CancelVideoPlayLoad();
            _cacheLoadCts?.Dispose();
            _coverLoadCts?.Dispose();
            _videoPlayCts?.Dispose();
            StopAllVideos();
            _libVlc.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializePagesScrollViewer();

        var archivePath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
        {
            await LoadArchiveWithPasswordRetryAsync(archivePath);
        }
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择漫画压缩包",
            Filter = "漫画压缩包 (*.zip;*.rar)|*.zip;*.rar|ZIP 文件 (*.zip)|*.zip|RAR 文件 (*.rar)|*.rar|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await LoadArchiveWithPasswordRetryAsync(dialog.FileName);
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeRestoreWindowButton.Content = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
        Dispatcher.BeginInvoke((Action)(() => UpdatePageWidth()), DispatcherPriority.Loaded);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PasswordOverlay.Visibility == Visibility.Visible
            || _isArchiveLoading
            || Pages.Count == 0
            || _archivePath is null)
        {
            return;
        }

        var pageOffset = e.Key switch
        {
            Key.PageUp => -1,
            Key.PageDown => 1,
            _ => 0
        };

        if (pageOffset == 0)
        {
            return;
        }

        var targetPageIndex = Math.Clamp(GetCurrentPageIndex() + pageOffset, 0, Pages.Count - 1);
        ScrollPageToTop(targetPageIndex);
        e.Handled = true;
    }

    private async Task LoadArchiveWithPasswordRetryAsync(string archivePath, string? initialPassword = null)
    {
        string? password = initialPassword;

        while (true)
        {
            try
            {
                _isArchiveLoading = true;
                SetLoadingState(true, $"正在读取目录 {Path.GetFileName(archivePath)} ...");
                UpdatePageWidth();
                CancelCacheLoads();
                CancelVideoPlayLoad();
                _pendingCachePageIndex = -1;
                _loadingWindowStart = null;
                _loadingWindowEnd = null;
                StopAllVideos();
                var entries = await Task.Run(() => LoadMediaEntries(archivePath, password));
                var pages = entries
                    .Select((entry, index) => new ComicPage(index, entry.Key, entry.Type, _pageWidth))
                    .ToList();

                _archivePath = archivePath;
                _password = password;
                _cacheWindowStart = -1;
                _cacheWindowEnd = -1;
                _loadingWindowStart = null;
                _loadingWindowEnd = null;
                CancelCacheLoads();
                _pendingCachePageIndex = -1;

                Pages.Clear();
                foreach (var page in pages)
                {
                    Pages.Add(page);
                }

                GetPagesScrollViewer()?.ScrollToTop();
                UpdatePageWidth();
                if (Pages.Count == 0)
                {
                    StatusTextBlock.Text = "压缩包中没有找到支持的图片或视频文件";
                    return;
                }

                UpdateReadingStatus(0);
                var initialWindow = GetPreloadWindow(0);
                await LoadCacheWindowAsync(initialWindow.Start, initialWindow.End, 0, showErrors: false);
                UpdateReadingStatus(0);
                RememberPassword(password);

                return;
            }
            catch (Exception ex) when (IsLikelyPasswordProblem(ex))
            {
                ResetArchiveState();
                _isArchiveLoading = false;
                SetLoadingState(false);

                var requestedPassword = await ShowPasswordOverlayAsync(archivePath);

                if (requestedPassword is null)
                {
                    StatusTextBlock.Text = "已取消打开压缩包";
                    return;
                }

                password = requestedPassword;
            }
            catch (Exception ex)
            {
                ResetArchiveState();

                StatusTextBlock.Text = "打开失败";
                MessageBox.Show(this, ex.Message, "无法打开压缩包", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                _isArchiveLoading = false;
                SetLoadingState(false);
            }
        }
    }

    private void ResetArchiveState()
    {
        _archivePath = null;
        _loadingWindowStart = null;
        _loadingWindowEnd = null;
        _cacheWindowStart = -1;
        _cacheWindowEnd = -1;
        CancelCacheLoads();
        CancelVideoPlayLoad();
        _pendingCachePageIndex = -1;
        StopAllVideos();
        Pages.Clear();
    }

    private Task<string?> ShowPasswordOverlayAsync(string archivePath)
    {
        _passwordPromptCompletion?.TrySetResult(null);
        _passwordPromptCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PasswordArchiveNameTextBlock.Text = Path.GetFileName(archivePath);
        ArchivePasswordBox.Clear();
        PasswordOverlay.Visibility = Visibility.Visible;
        OpenArchiveButton.IsEnabled = false;
        Dispatcher.BeginInvoke(() => ArchivePasswordBox.Focus());
        return _passwordPromptCompletion.Task;
    }

    private void CompletePasswordPrompt(string? password)
    {
        var completion = _passwordPromptCompletion;
        if (completion is null)
        {
            return;
        }

        _passwordPromptCompletion = null;
        PasswordOverlay.Visibility = Visibility.Collapsed;
        ArchivePasswordBox.Clear();
        OpenArchiveButton.IsEnabled = true;
        completion.TrySetResult(password);
    }

    private static string PasswordHistoryFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ComicViewer",
            "password-history.json");

    private void LoadPasswordHistory()
    {
        try
        {
            if (!File.Exists(PasswordHistoryFilePath))
            {
                return;
            }

            var passwords = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PasswordHistoryFilePath));
            if (passwords is null)
            {
                return;
            }

            foreach (var password in passwords.Where(password => !string.IsNullOrWhiteSpace(password)).Distinct())
            {
                PasswordHistory.Add(password);
            }
        }
        catch
        {
            // Password history is a convenience cache; ignore unreadable files.
        }
    }

    private void RememberPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var existing = PasswordHistory.FirstOrDefault(item => string.Equals(item, password, StringComparison.Ordinal));
        if (existing is not null)
        {
            PasswordHistory.Remove(existing);
        }

        PasswordHistory.Insert(0, password);
        while (PasswordHistory.Count > 50)
        {
            PasswordHistory.RemoveAt(PasswordHistory.Count - 1);
        }

        SavePasswordHistory();
    }

    private void SavePasswordHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(PasswordHistory.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            Directory.CreateDirectory(Path.GetDirectoryName(PasswordHistoryFilePath)!);
            File.WriteAllText(PasswordHistoryFilePath, json);
        }
        catch
        {
            // Keep the viewer usable even if the install directory is read-only.
        }
    }

    private void PasswordOpenButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(ArchivePasswordBox.Password);
    }

    private void PasswordCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(null);
    }

    private void ArchivePasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CompletePasswordPrompt(ArchivePasswordBox.Password);
        }
        else if (e.Key == Key.Escape)
        {
            CompletePasswordPrompt(null);
        }
    }

    private void PasswordHistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PasswordHistoryListBox.SelectedItem is string password)
        {
            ArchivePasswordBox.Password = password;
            CompletePasswordPrompt(password);
        }
    }

    private static List<ComicArchiveEntry> LoadMediaEntries(string archivePath, string? password)
    {
        var options = new ReaderOptions
        {
            Password = password
        };

        using var archive = ArchiveFactory.OpenArchive(archivePath, options);
        var mediaEntries = archive.Entries
            .Where(entry => !entry.IsDirectory && IsSupportedMediaFile(entry.Key))
            .OrderBy(entry => entry.Key, NaturalFileNameComparer.Instance)
            .ToList();

        var firstMediaEntry = mediaEntries.FirstOrDefault();
        if (firstMediaEntry is not null)
        {
            using var entryStream = firstMediaEntry.OpenEntryStream();
            _ = entryStream.ReadByte();
        }

        return mediaEntries
            .Select(entry => new ComicArchiveEntry(entry.Key!, GetMediaType(entry.Key!)))
            .ToList();
    }

    private static List<ImageLoadRequest> CreateImageLoadRequests(
        IReadOnlyList<ComicPage> pages,
        int startIndex,
        int count,
        bool onlyMissing)
    {
        if (pages.Count == 0 || count <= 0)
        {
            return [];
        }

        var endIndex = Math.Min(pages.Count - 1, startIndex + count - 1);
        return pages
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .Where(page => page.IsImage && (!onlyMissing || !page.IsImageLoaded))
            .Select(page => new ImageLoadRequest(page.Index, page.EntryKey))
            .ToList();
    }

    private static Dictionary<int, BitmapImage> LoadImagesFromArchive(
        string archivePath,
        string? password,
        IReadOnlyList<ImageLoadRequest> requests,
        int decodePixelWidth,
        Action<int, string, BitmapImage>? imageLoaded = null,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var requestedPagesByKey = requests.ToDictionary(request => request.EntryKey, request => request.Index, StringComparer.Ordinal);

        if (requestedPagesByKey.Count == 0)
        {
            return [];
        }

        var options = new ReaderOptions
        {
            Password = password
        };

        var images = new Dictionary<int, BitmapImage>();
        using var archive = ArchiveFactory.OpenArchive(archivePath, options);
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory && entry.Key is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!requestedPagesByKey.TryGetValue(entry.Key!, out var pageIndex))
            {
                continue;
            }

            using var entryStream = entry.OpenEntryStream();
            using var memoryStream = new MemoryStream();
            TryCopyToMemoryStream(entryStream, memoryStream, cancellationToken);

            memoryStream.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = memoryStream;
            image.EndInit();
            image.Freeze();
            images[pageIndex] = image;
            imageLoaded?.Invoke(pageIndex, entry.Key!, image);

            if (images.Count == requestedPagesByKey.Count)
            {
                break;
            }
        }

        return images;
    }

    private static void TryCopyToMemoryStream(Stream source, MemoryStream destination, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                return;
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }

    private static bool CopyAtMostToMemoryStream(
        Stream source,
        MemoryStream destination,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[128 * 1024];
        var remainingBytes = maxBytes;
        while (remainingBytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min(buffer.Length, remainingBytes);
            var bytesRead = source.Read(buffer, 0, bytesToRead);
            if (bytesRead == 0)
            {
                return true;
            }

            destination.Write(buffer, 0, bytesRead);
            remainingBytes -= bytesRead;
        }

        return false;
    }

    private static MemoryStream CloneMemoryStream(MemoryStream source)
    {
        var copy = new MemoryStream(checked((int)source.Length));
        if (source.TryGetBuffer(out var buffer))
        {
            copy.Write(buffer.Array!, buffer.Offset, checked((int)source.Length));
        }
        else
        {
            var previousPosition = source.Position;
            source.Position = 0;
            source.CopyTo(copy);
            source.Position = previousPosition;
        }

        copy.Position = 0;
        return copy;
    }

    private static MemoryStream CreateReadOnlyMemoryStreamView(MemoryStream source)
    {
        if (source.TryGetBuffer(out var buffer))
        {
            return new MemoryStream(buffer.Array!, buffer.Offset, checked((int)source.Length), writable: false);
        }

        return CloneMemoryStream(source);
    }

    private MemoryStream LoadVideoToMemory(ComicPage page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archivePath = _archivePath;
        var password = _password;
        if (archivePath is null)
        {
            throw new InvalidOperationException("还没有打开压缩包。");
        }

        var options = new ReaderOptions
        {
            Password = password
        };

        using var archive = ArchiveFactory.OpenArchive(archivePath, options);
        var entry = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory && string.Equals(entry.Key, page.EntryKey, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new FileNotFoundException("在压缩包中找不到这个视频。", page.EntryKey);
        }

        using var entryStream = entry.OpenEntryStream();
        var memoryStream = new MemoryStream();
        try
        {
            TryCopyToMemoryStream(entryStream, memoryStream, cancellationToken);
            memoryStream.Position = 0;
            return memoryStream;
        }
        catch
        {
            memoryStream.Dispose();
            throw;
        }
    }

    private VideoPlaybackSession CreateMemoryVideoPlaybackSession(ComicPage page, MemoryStream videoStream, bool disableAudio)
    {
        var input = new StreamMediaInput(videoStream);
        var media = new VlcMedia(_libVlc, input);
        if (disableAudio)
        {
            media.AddOption(":no-audio");
        }

        var mediaPlayer = new VlcMediaPlayer(_libVlc);
        var renderer = new VideoFrameRenderer(
            frame => page.SetVideoFrame(frame),
            (width, height) => page.SetAspectRatio((double)height / width, _pageWidth));

        renderer.AttachTo(mediaPlayer);
        mediaPlayer.Media = media;

        return new VideoPlaybackSession(videoStream, input, media, mediaPlayer, renderer);
    }

    private async Task<VideoCoverLoadResult?> TryLoadVideoCoverAsync(ComicPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archivePath = _archivePath;
        var password = _password;
        if (archivePath is null)
        {
            return null;
        }

        VideoCoverProbeReader? probeReader = null;
        try
        {
            probeReader = await Task.Run(
                () => VideoCoverProbeReader.Open(archivePath, password, page.EntryKey),
                cancellationToken);
            if (probeReader is null)
            {
                return new VideoCoverLoadResult(null, VideoCoverLoadStatus.Failed);
            }

            using var probeStream = new MemoryStream();
            var reachedEnd = false;
            while (probeStream.Length < MaxCoverProbeBytes && !reachedEnd)
            {
                var bytesToRead = Math.Min(CoverProbeStepBytes, MaxCoverProbeBytes - probeStream.Length);
                reachedEnd = await Task.Run(
                    () => probeReader.CopyAtMostToMemoryStream(probeStream, bytesToRead, cancellationToken),
                    cancellationToken);

                if (probeStream.Length == 0 || cancellationToken.IsCancellationRequested || page.IsVideoPlaying)
                {
                    return null;
                }

                var frameFound = await TryRenderVideoCoverFrameAsync(page, CreateReadOnlyMemoryStreamView(probeStream), cancellationToken);
                if (frameFound)
                {
                    return new VideoCoverLoadResult(
                        reachedEnd ? CloneMemoryStream(probeStream) : null,
                        VideoCoverLoadStatus.Success);
                }
            }

            return new VideoCoverLoadResult(
                null,
                reachedEnd ? VideoCoverLoadStatus.Failed : VideoCoverLoadStatus.Oversized);
        }
        finally
        {
            if (probeReader is not null)
            {
                await Task.Run(probeReader.Dispose, CancellationToken.None);
            }
        }
    }

    private async Task<bool> TryRenderVideoCoverFrameAsync(ComicPage page, MemoryStream videoStream, CancellationToken cancellationToken)
    {
        VideoPlaybackSession? session = null;
        try
        {
            session = CreateMemoryVideoPlaybackSession(page, videoStream, disableAudio: true);
            page.SetCoverSession(session);

            if (!session.MediaPlayer.Play())
            {
                return false;
            }

            await session.Renderer.FirstFrameDisplayed.WaitAsync(CoverProbeFrameTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            if (session is not null)
            {
                page.DetachCoverSession(session);
                await Task.Run(() =>
                {
                    try
                    {
                        session.Stop();
                        session.Dispose();
                    }
                    catch
                    {
                        // Cover extraction is best-effort; native cleanup should not break loading.
                    }
                }, CancellationToken.None);
            }
            else
            {
                videoStream.Dispose();
            }
        }
    }

    private sealed class VideoCoverProbeReader : IDisposable
    {
        private readonly IArchive _archive;
        private readonly Stream _entryStream;
        private bool _isDisposed;

        private VideoCoverProbeReader(IArchive archive, Stream entryStream)
        {
            _archive = archive;
            _entryStream = entryStream;
        }

        public static VideoCoverProbeReader? Open(string archivePath, string? password, string entryKey)
        {
            var options = new ReaderOptions
            {
                Password = password
            };

            var archive = ArchiveFactory.OpenArchive(archivePath, options);
            try
            {
                var entry = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory && string.Equals(entry.Key, entryKey, StringComparison.Ordinal));
                if (entry is null)
                {
                    archive.Dispose();
                    return null;
                }

                return new VideoCoverProbeReader(archive, entry.OpenEntryStream());
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        public bool CopyAtMostToMemoryStream(MemoryStream destination, long maxBytes, CancellationToken cancellationToken)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(VideoCoverProbeReader));
            }

            return MainWindow.CopyAtMostToMemoryStream(_entryStream, destination, maxBytes, cancellationToken);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _entryStream.Dispose();
            _archive.Dispose();
        }
    }

    private async void PlayVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isArchiveLoading)
        {
            return;
        }

        if (sender is not Button button || button.CommandParameter is not ComicPage page)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            if (page.PlaybackSession is not null && page.IsVideoPaused)
            {
                page.ResumeVideo();
            }
            else
            {
                await PlayVideoFromMemoryAsync(page);
            }
        }
        catch (Exception ex)
        {
            page.IsVideoPlaying = false;
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, ex.Message, "无法播放视频", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
            e.Handled = true;
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ComicPage page } || !page.IsVideoPlaying)
        {
            return;
        }

        page.PauseVideo();
        e.Handled = true;
    }

    private async Task PlayVideoFromMemoryAsync(ComicPage page)
    {
        var archivePath = _archivePath;
        if (archivePath is null)
        {
            return;
        }

        var videoPlayCts = BeginVideoPlayLoad();
        var cancellationToken = videoPlayCts.Token;
        MemoryStream? videoStream = null;
        VideoPlaybackSession? session = null;
        var sessionAssignedToPage = false;

        try
        {
            CancelCoverLoads();
            var cachedVideoStream = page.TryTakeCachedVideoStream();
            page.StopVideo();
            StopOtherVideos(page);
            page.StopCoverSession();
            StatusTextBlock.Text = cachedVideoStream is null
                ? $"正在载入视频到内存 {Path.GetFileName(page.EntryKey)} ..."
                : $"正在准备播放 {Path.GetFileName(page.EntryKey)} ...";
            videoStream = cachedVideoStream ?? await Task.Run(() => LoadVideoToMemory(page, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVideoPlayRequestCurrent(videoPlayCts, archivePath, page))
            {
                return;
            }

            session = CreateMemoryVideoPlaybackSession(page, videoStream, disableAudio: false);
            videoStream = null;
            page.SetVideoPlaybackSession(session);
            sessionAssignedToPage = true;
            AttachPlaybackEvents(page, session);
            page.IsVideoPlaying = true;
            page.IsVideoPaused = false;
            session.MediaPlayer.Mute = false;
            session.MediaPlayer.Volume = 100;

            if (!session.MediaPlayer.Play())
            {
                throw new InvalidOperationException("播放器没有成功启动视频。");
            }

            StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 使用内存播放";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsVideoPlayRequestCurrent(videoPlayCts, archivePath, page))
            {
                return;
            }

            if (IsLikelyPasswordProblem(ex))
            {
                page.StopVideo();
                ResetArchiveState();
                _isArchiveLoading = false;
                SetLoadingState(false);

                var requestedPassword = await ShowPasswordOverlayAsync(archivePath);
                if (requestedPassword is null)
                {
                    StatusTextBlock.Text = "已取消打开压缩包";
                    return;
                }

                await LoadArchiveWithPasswordRetryAsync(archivePath, requestedPassword);
                return;
            }

            page.StopVideo();
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, ex.Message, "无法播放视频", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!sessionAssignedToPage)
            {
                session?.Dispose();
            }

            videoStream?.Dispose();
            FinishVideoPlayLoad(videoPlayCts);
        }
    }

    private void AttachPlaybackEvents(ComicPage page, VideoPlaybackSession session)
    {
        session.MediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.IsVideoPaused = false;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 正在播放";
            }
        });

        session.MediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.IsVideoPaused = true;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 已暂停";
            }
        });

        session.MediaPlayer.LengthChanged += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.SetVideoDuration(args.Length);
            }
        });

        session.MediaPlayer.TimeChanged += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.SetVideoPosition(args.Time);
            }
        });

        session.MediaPlayer.Vout += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session && args.Count > 0)
            {
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 视频输出已就绪";
            }
        });

        session.MediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.StopVideo();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);

        session.MediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (page.PlaybackSession != session)
            {
                return;
            }

            page.StopVideo();
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, "播放器无法播放这个视频。", "视频播放失败", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    private static bool IsImageFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsVideoFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && VideoExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsSupportedMediaFile(string? fileName)
    {
        return IsImageFile(fileName) || IsVideoFile(fileName);
    }

    private static ComicMediaType GetMediaType(string fileName)
    {
        return IsVideoFile(fileName) ? ComicMediaType.Video : ComicMediaType.Image;
    }

    private static bool IsLikelyPasswordProblem(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crypt", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crc", StringComparison.OrdinalIgnoreCase)
            || message.Contains("data error", StringComparison.OrdinalIgnoreCase);
    }

    private void SetLoadingState(bool isLoading, string? status = null)
    {
        OpenArchiveButton.IsEnabled = !isLoading;
        if (status is not null)
        {
            StatusTextBlock.Text = status;
        }
    }

    private CancellationTokenSource BeginCacheLoad()
    {
        CancelCacheLoads();
        _cacheLoadCts = new CancellationTokenSource();
        return _cacheLoadCts;
    }

    private void CancelCacheLoads()
    {
        var cts = _cacheLoadCts;
        _cacheLoadCts = null;
        cts?.Cancel();
        CancelCoverLoads();
    }

    private void FinishCacheLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cacheLoadCts, cts))
        {
            _cacheLoadCts = null;
        }

        cts.Dispose();
    }

    private CancellationTokenSource BeginCoverLoad()
    {
        CancelCoverLoads();
        _coverLoadCts = new CancellationTokenSource();
        return _coverLoadCts;
    }

    private void CancelCoverLoads()
    {
        var cts = _coverLoadCts;
        _coverLoadCts = null;
        cts?.Cancel();
    }

    private void FinishCoverLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_coverLoadCts, cts))
        {
            _coverLoadCts = null;
        }

        cts.Dispose();
    }

    private CancellationTokenSource BeginVideoPlayLoad()
    {
        CancelVideoPlayLoad();
        _videoPlayCts = new CancellationTokenSource();
        return _videoPlayCts;
    }

    private void CancelVideoPlayLoad()
    {
        var cts = _videoPlayCts;
        _videoPlayCts = null;
        cts?.Cancel();
    }

    private void FinishVideoPlayLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_videoPlayCts, cts))
        {
            _videoPlayCts = null;
        }

        cts.Dispose();
    }

    private bool IsVideoPlayRequestCurrent(CancellationTokenSource cts, string archivePath, ComicPage page)
    {
        return ReferenceEquals(_videoPlayCts, cts)
            && !cts.IsCancellationRequested
            && string.Equals(_archivePath, archivePath, StringComparison.Ordinal)
            && page.Index >= 0
            && page.Index < Pages.Count
            && ReferenceEquals(Pages[page.Index], page);
    }

    private void InitializePagesScrollViewer()
    {
        _pagesScrollViewer = GetPagesScrollViewer();
    }

    private ScrollViewer? GetPagesScrollViewer()
    {
        if (_pagesScrollViewer is not null)
        {
            return _pagesScrollViewer;
        }

        PagesListBox.ApplyTemplate();
        _pagesScrollViewer = FindVisualChild<ScrollViewer>(PagesListBox);
        return _pagesScrollViewer;
    }

    private double GetVerticalOffset()
    {
        return GetPagesScrollViewer()?.VerticalOffset ?? 0d;
    }

    private int GetCurrentPageIndex()
    {
        return TryGetFirstVisiblePageIndex() ?? GetPageIndexAtOffset(GetVerticalOffset());
    }

    private void ScrollPageToTop(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        PagesListBox.ScrollIntoView(Pages[pageIndex]);
        Dispatcher.BeginInvoke((Action)(() =>
        {
            AlignRealizedPageToTop(pageIndex);
            UpdateReadingStatus(pageIndex);
            QueueCacheWindowLoad(pageIndex);
        }), DispatcherPriority.Loaded);
    }

    private void AlignRealizedPageToTop(int pageIndex)
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        PagesListBox.UpdateLayout();
        if (PagesListBox.ItemContainerGenerator.ContainerFromIndex(pageIndex) is not FrameworkElement container)
        {
            return;
        }

        Rect bounds;
        try
        {
            bounds = container.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + bounds.Top);
    }

    private int? TryGetFirstVisiblePageIndex()
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null || Pages.Count == 0)
        {
            return null;
        }

        var bestIndex = -1;
        var bestTop = double.PositiveInfinity;
        foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
        {
            if (container.ActualHeight <= 0)
            {
                continue;
            }

            var index = PagesListBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0 || index >= Pages.Count)
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = container.TransformToAncestor(scrollViewer)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (bounds.Bottom <= 0 || bounds.Top >= scrollViewer.ViewportHeight)
            {
                continue;
            }

            if (bounds.Top < bestTop)
            {
                bestTop = bounds.Top;
                bestIndex = index;
            }
        }

        return bestIndex >= 0 ? bestIndex : null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nestedChild in FindVisualChildren<T>(child))
            {
                yield return nestedChild;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null)
            {
                return nestedChild;
            }
        }

        return null;
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SchedulePageWidthUpdate(e.NewSize.Width);
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var wheelLines = SystemParameters.WheelScrollLines > 0
            ? SystemParameters.WheelScrollLines
            : 3;
        var deltaSteps = e.Delta / (double)System.Windows.Input.Mouse.MouseWheelDeltaForOneLine;
        var scrollPixels = deltaSteps * wheelLines * MouseWheelPixelsPerLine * MouseWheelScrollMultiplier;

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - scrollPixels);
        e.Handled = true;
    }

    private void UpdatePageWidth(double fallbackWidth = 0)
    {
        _pageWidthUpdateTimer.Stop();
        var width = ResolvePageWidth(fallbackWidth);
        _pageWidth = width;
        ResizeAllPages(width);
    }

    private void SchedulePageWidthUpdate(double fallbackWidth)
    {
        var width = ResolvePageWidth(fallbackWidth);
        if (Math.Abs(_pageWidth - width) <= 0.1)
        {
            return;
        }

        _pageWidth = width;
        _pendingPageWidth = width;
        ResizeRealizedPages(width);
        _pageWidthUpdateTimer.Stop();
        _pageWidthUpdateTimer.Start();
    }

    private void PageWidthUpdateTimer_Tick(object? sender, EventArgs e)
    {
        _pageWidthUpdateTimer.Stop();
        ResizeAllPages(_pendingPageWidth > 0 ? _pendingPageWidth : _pageWidth);
    }

    private double ResolvePageWidth(double fallbackWidth = 0)
    {
        // Prefer the inner ScrollViewer viewport; during template/layout transitions it can
        // briefly be unavailable, so fall back through the size-change payload, the ListBox
        // width, and finally the window width to keep page sizing usable during startup/state changes.
        // The custom scrollbar overlays the content, so it does not reserve layout width here.
        var scrollViewer = GetPagesScrollViewer();
        var width = scrollViewer?.ViewportWidth ?? 0;
        if (double.IsNaN(width) || width <= 1)
        {
            width = fallbackWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = PagesListBox.ActualWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = ActualWidth;
        }

        return Math.Max(1, width);
    }

    private void ResizeAllPages(double pageWidth)
    {
        foreach (var page in Pages)
        {
            page.Resize(pageWidth);
        }
    }

    private void ResizeRealizedPages(double pageWidth)
    {
        foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
        {
            if (container.DataContext is ComicPage page)
            {
                page.Resize(pageWidth);
            }
        }
    }

    private void ImageScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_isArchiveLoading || Pages.Count == 0 || _archivePath is null)
        {
            return;
        }

        var currentPageIndex = GetCurrentPageIndex();
        UpdateReadingStatus(currentPageIndex);
        QueueCacheWindowLoad(currentPageIndex);
    }

    private void QueueCacheWindowLoad(int currentPageIndex)
    {
        var window = GetPreloadWindow(currentPageIndex);
        if (_loadingWindowStart == window.Start
            && _loadingWindowEnd == window.End
            && _cacheLoadCts?.IsCancellationRequested == false)
        {
            return;
        }

        if (window.Start == _cacheWindowStart && window.End == _cacheWindowEnd && !WindowHasMissingImages(window.Start, window.End))
        {
            return;
        }

        _pendingCachePageIndex = currentPageIndex;
        CancelCacheLoads();
        if (_isCacheLoadWorkerRunning)
        {
            return;
        }

        _isCacheLoadWorkerRunning = true;
        _ = ProcessPendingCacheLoadsAsync();
    }

    private async Task ProcessPendingCacheLoadsAsync()
    {
        try
        {
            while (_pendingCachePageIndex >= 0)
            {
                await Task.Delay(90);
                var currentPageIndex = _pendingCachePageIndex;
                _pendingCachePageIndex = -1;
                if (_isArchiveLoading || Pages.Count == 0 || _archivePath is null)
                {
                    continue;
                }

                currentPageIndex = Math.Clamp(currentPageIndex, 0, Pages.Count - 1);
                var window = GetPreloadWindow(currentPageIndex);
                if (_loadingWindowStart == window.Start
                    && _loadingWindowEnd == window.End
                    && _cacheLoadCts?.IsCancellationRequested == false)
                {
                    continue;
                }

                if (window.Start == _cacheWindowStart && window.End == _cacheWindowEnd && !WindowHasMissingImages(window.Start, window.End))
                {
                    continue;
                }

                await LoadCacheWindowAsync(window.Start, window.End, currentPageIndex);
                if (!_isArchiveLoading && Pages.Count > 0 && _archivePath is not null)
                {
                    UpdateReadingStatus(GetCurrentPageIndex());
                }
            }
        }
        finally
        {
            _isCacheLoadWorkerRunning = false;
            if (_pendingCachePageIndex >= 0 && !_isArchiveLoading && Pages.Count > 0 && _archivePath is not null)
            {
                _isCacheLoadWorkerRunning = true;
                _ = ProcessPendingCacheLoadsAsync();
            }
        }
    }

    private async Task LoadCacheWindowAsync(int windowStart, int windowEnd, int currentPageIndex, bool showErrors = true)
    {
        var archivePath = _archivePath;
        if (archivePath is null || Pages.Count == 0)
        {
            return;
        }

        windowStart = Math.Clamp(windowStart, 0, Pages.Count - 1);
        windowEnd = Math.Clamp(windowEnd, windowStart, Pages.Count - 1);
        if (_loadingWindowStart == windowStart
            && _loadingWindowEnd == windowEnd
            && _cacheLoadCts?.IsCancellationRequested == false)
        {
            return;
        }

        var cacheLoadCts = BeginCacheLoad();
        var cancellationToken = cacheLoadCts.Token;
        try
        {
            var password = _password;
            _loadingWindowStart = windowStart;
            _loadingWindowEnd = windowEnd;
            var pageSnapshot = Pages.ToList();
            PruneMediaOutsideWindow(windowStart, windowEnd);
            var requests = CreateImageLoadRequests(pageSnapshot, windowStart, windowEnd - windowStart + 1, onlyMissing: true);
            var decodePixelWidth = GetDecodePixelWidth();

            var loadedImages = await Task.Run(() => LoadImagesFromArchive(
                archivePath,
                password,
                requests,
                decodePixelWidth,
                (pageIndex, entryKey, image) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !string.Equals(_archivePath, archivePath, StringComparison.Ordinal)
                        || pageIndex < windowStart
                        || pageIndex > windowEnd
                        || pageIndex >= Pages.Count
                        || !Pages[pageIndex].IsImage
                        || !string.Equals(Pages[pageIndex].EntryKey, entryKey, StringComparison.Ordinal))
                    {
                        return;
                    }

                    Pages[pageIndex].SetImage(image, _pageWidth);
                    UpdateReadingStatus(GetCurrentPageIndex());
                })),
                cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ApplyLoadedImages(loadedImages, windowStart, windowEnd);
            _cacheWindowStart = windowStart;
            _cacheWindowEnd = windowEnd;
            var coverLoadCts = BeginCoverLoad();
            _ = ScheduleVideoCoversAsync(windowStart, windowEnd, coverLoadCts, currentPageIndex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!showErrors)
            {
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            StatusTextBlock.Text = "读取图片失败";
            MessageBox.Show(this, ex.Message, "无法读取图片", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_cacheLoadCts, cacheLoadCts)
                && _loadingWindowStart == windowStart
                && _loadingWindowEnd == windowEnd)
            {
                _loadingWindowStart = null;
                _loadingWindowEnd = null;
            }

            FinishCacheLoad(cacheLoadCts);
        }
    }

    private void ApplyLoadedImages(IReadOnlyDictionary<int, BitmapImage> loadedImages, int windowStart, int windowEnd)
    {
        for (var i = windowStart; i <= windowEnd && i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (page.IsImage && loadedImages.TryGetValue(i, out var image))
            {
                page.SetImage(image, _pageWidth);
            }
            else if (page.IsVideo)
            {
                page.StopCoverSession();
            }
        }
    }

    private void PruneMediaOutsideWindow(int windowStart, int windowEnd)
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            if (i >= windowStart && i <= windowEnd)
            {
                continue;
            }

            var page = Pages[i];
            if (page.IsImage)
            {
                page.SetImage(null, _pageWidth);
            }
            else if (page.IsVideo)
            {
                page.StopCoverSession();
                page.ClearVideoFrame();
                page.ClearCachedVideoData();
            }
        }
    }

    private int GetDecodePixelWidth()
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Max(MinimumDecodePixelWidth, (int)Math.Ceiling(_pageWidth * dpiScale));
    }

    private async Task ScheduleVideoCoversAsync(int windowStart, int windowEnd, CancellationTokenSource coverLoadCts, int currentPageIndex)
    {
        var cancellationToken = coverLoadCts.Token;
        try
        {
            await Task.Delay(1200, cancellationToken);
            if (!IsCoverWindowCurrent(windowStart, windowEnd, cancellationToken))
            {
                return;
            }

            var videoPages = Pages
                .Skip(windowStart)
                .Take(windowEnd - windowStart + 1)
                .Where(page => page.IsVideo && page.VideoFrame is null && !page.IsVideoPlaying)
                .OrderBy(page => Math.Abs(page.Index - currentPageIndex))
                .ToList();

            foreach (var page in videoPages)
            {
                if (!IsCoverWindowCurrent(windowStart, windowEnd, cancellationToken))
                {
                    return;
                }

                await EnsureVideoCoverAsync(page, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Video covers are best-effort and should never slow or break image reading.
        }
        finally
        {
            FinishCoverLoad(coverLoadCts);
        }
    }

    private bool IsCoverWindowCurrent(int windowStart, int windowEnd, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && _loadingWindowStart is null
            && _cacheWindowStart == windowStart
            && _cacheWindowEnd == windowEnd
            && _archivePath is not null;
    }

    private bool WindowHasMissingImages(int windowStart, int windowEnd)
    {
        if (Pages.Count == 0)
        {
            return false;
        }

        for (var i = windowStart; i <= windowEnd; i++)
        {
            var page = Pages[i];
            if (page.IsImage && !page.IsImageLoaded)
            {
                return true;
            }

        }

        return false;
    }

    private (int Start, int End) GetPreloadWindow(int currentPageIndex)
    {
        if (Pages.Count == 0)
        {
            return (0, -1);
        }

        var start = Math.Max(0, currentPageIndex - BackwardPreloadCount);
        var end = Math.Min(Pages.Count - 1, currentPageIndex + ForwardPreloadCount);
        return (start, end);
    }

    private int GetPageIndexAtOffset(double offset)
    {
        var accumulatedHeight = 0d;
        for (var i = 0; i < Pages.Count; i++)
        {
            accumulatedHeight += Pages[i].DisplayHeight;
            if (accumulatedHeight > offset)
            {
                return i;
            }
        }

        return Math.Max(0, Pages.Count - 1);
    }

    private void UpdateReadingStatus(int currentPageIndex)
    {
        if (_archivePath is null || Pages.Count == 0)
        {
            return;
        }

        var loadedRangeText = GetLoadedMediaRangeText();
        var loadingText = _loadingWindowStart is null ? "" : "，正在加载";
        StatusTextBlock.Text = $"{Path.GetFileName(_archivePath)} - 第 {currentPageIndex + 1}/{Pages.Count} 项，已加载 {loadedRangeText}{loadingText}";
    }

    private string GetLoadedMediaRangeText()
    {
        var ranges = new List<(int Start, int End)>();
        int? rangeStart = null;
        var rangeEnd = -1;

        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].IsLoaded)
            {
                rangeStart ??= i + 1;
                rangeEnd = i + 1;
                continue;
            }

            if (rangeStart is not null)
            {
                ranges.Add((rangeStart.Value, rangeEnd));
                rangeStart = null;
            }
        }

        if (rangeStart is not null)
        {
            ranges.Add((rangeStart.Value, rangeEnd));
        }

        if (ranges.Count == 0)
        {
            return "--";
        }

        return string.Join("，", ranges.Select(range =>
            range.Start == range.End
                ? range.Start.ToString(CultureInfo.InvariantCulture)
                : $"{range.Start}-{range.End}"));
    }

    private void StopAllVideos()
    {
        foreach (var page in Pages)
        {
            page.StopVideo();
        }
    }

    private void StopOtherVideos(ComicPage currentPage)
    {
        foreach (var page in Pages)
        {
            if (!ReferenceEquals(page, currentPage))
            {
                page.StopVideo();
            }
        }
    }

    private async Task EnsureVideoCoverAsync(ComicPage page, CancellationToken cancellationToken)
    {
        if (!page.TryBeginCoverLoad())
        {
            return;
        }

        var semaphoreAcquired = false;
        VideoCoverLoadResult? coverLoadResult = null;
        try
        {
            await _coverLoadSemaphore.WaitAsync(cancellationToken);
            semaphoreAcquired = true;
            if (cancellationToken.IsCancellationRequested || page.IsVideoPlaying)
            {
                return;
            }

            coverLoadResult = await TryLoadVideoCoverAsync(page, cancellationToken);
            if (cancellationToken.IsCancellationRequested || page.IsVideoPlaying)
            {
                return;
            }

            page.SetCoverLoadStatus(coverLoadResult?.Status ?? VideoCoverLoadStatus.Failed);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    UpdateReadingStatus(GetCurrentPageIndex());
                }
            });
            if (coverLoadResult?.VideoStream is not null)
            {
                page.TryCacheVideoData(coverLoadResult.VideoStream, MaxCachedVideoBytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            page.SetCoverLoadStatus(VideoCoverLoadStatus.Failed);
            // Cover extraction is best-effort; the play button remains available.
        }
        finally
        {
            coverLoadResult?.VideoStream?.Dispose();
            page.EndCoverLoad();
            if (semaphoreAcquired)
            {
                _coverLoadSemaphore.Release();
            }
        }
    }

    private void ArchivePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordOpenButton.IsEnabled = !string.IsNullOrWhiteSpace(ArchivePasswordBox.Password);
    }
}

public sealed class ComicPage : INotifyPropertyChanged
{
    private const double DefaultAspectRatio = 1.45;
    private const double DefaultVideoAspectRatio = 9d / 16d;

    private BitmapImage? _image;
    private VideoPlaybackSession? _videoPlaybackSession;
    private VideoPlaybackSession? _coverSession;
    private VlcMediaPlayer? _mediaPlayer;
    private ImageSource? _videoFrame;
    private ArraySegment<byte>? _cachedVideoData;
    private VideoCoverLoadStatus _coverLoadStatus;
    private bool _isCoverLoading;
    private bool _isVideoPaused;
    private bool _isVideoPlaying;
    private long _videoPositionMs;
    private long _videoDurationMs;
    private double _aspectRatio;
    private double _displayWidth;
    private double _displayHeight;

    public ComicPage(int index, string entryKey, ComicMediaType mediaType, double pageWidth)
    {
        Index = index;
        EntryKey = entryKey;
        MediaType = mediaType;
        _aspectRatio = IsVideo ? DefaultVideoAspectRatio : DefaultAspectRatio;
        Resize(pageWidth);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public string EntryKey { get; }

    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public ComicMediaType MediaType { get; }

    public bool IsImage => MediaType == ComicMediaType.Image;

    public bool IsVideo => MediaType == ComicMediaType.Video;

    public bool IsImageLoaded => Image is not null;

    public bool IsLoaded => IsImage
        ? Image is not null
        : VideoFrame is not null;

    public VideoPlaybackSession? PlaybackSession => _videoPlaybackSession;

    public BitmapImage? Image
    {
        get => _image;
        private set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                OnPropertyChanged();
            }
        }
    }

    public VlcMediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set
        {
            if (!ReferenceEquals(_mediaPlayer, value))
            {
                _mediaPlayer = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource? VideoFrame
    {
        get => _videoFrame;
        private set
        {
            if (!ReferenceEquals(_videoFrame, value))
            {
                _videoFrame = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsVideoPlaying
    {
        get => _isVideoPlaying;
        set
        {
            if (_isVideoPlaying != value)
            {
                _isVideoPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoOverlayText));
            }
        }
    }

    public bool IsVideoPaused
    {
        get => _isVideoPaused;
        set
        {
            if (_isVideoPaused != value)
            {
                _isVideoPaused = value;
                OnPropertyChanged();
            }
        }
    }

    public string VideoTimeText => IsVideo
        ? $"{FormatVideoTime(_videoPositionMs)}/{FormatVideoTime(_videoDurationMs)}"
        : "";

    public string VideoOverlayText => !IsVideo
        ? ""
        : !IsVideoPlaying && VideoFrame is null
            ? _coverLoadStatus switch
            {
                VideoCoverLoadStatus.Oversized => "视频过大，取消封面加载",
                VideoCoverLoadStatus.Failed => "未能成功加载封面",
                _ => VideoTimeText
            }
            : VideoTimeText;

    public double DisplayHeight
    {
        get => _displayHeight;
        private set
        {
            if (Math.Abs(_displayHeight - value) > 0.1)
            {
                _displayHeight = value;
                OnPropertyChanged();
            }
        }
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        private set
        {
            if (Math.Abs(_displayWidth - value) > 0.1)
            {
                _displayWidth = value;
                OnPropertyChanged();
            }
        }
    }

    public void SetImage(BitmapImage? image, double pageWidth)
    {
        var wasLoaded = IsLoaded;
        Image = image;

        if (image?.PixelWidth > 0)
        {
            _aspectRatio = (double)image.PixelHeight / image.PixelWidth;
        }

        Resize(pageWidth);
        if (wasLoaded != IsLoaded)
        {
            OnPropertyChanged(nameof(IsLoaded));
        }
    }

    public void SetVideoPlaybackSession(VideoPlaybackSession session)
    {
        _videoPlaybackSession = session;
        MediaPlayer = session.MediaPlayer;
    }

    public void PauseVideo()
    {
        if (!IsVideoPlaying || _videoPlaybackSession is null)
        {
            return;
        }

        _videoPlaybackSession.MediaPlayer.SetPause(true);
        IsVideoPaused = true;
    }

    public void ResumeVideo()
    {
        if (_videoPlaybackSession is null)
        {
            return;
        }

        _videoPlaybackSession.MediaPlayer.SetPause(false);
        IsVideoPlaying = true;
        IsVideoPaused = false;
    }

    public void SetVideoPosition(long positionMs)
    {
        if (positionMs < 0)
        {
            positionMs = 0;
        }

        if (_videoPositionMs != positionMs)
        {
            _videoPositionMs = positionMs;
            OnPropertyChanged(nameof(VideoTimeText));
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetVideoDuration(long durationMs)
    {
        if (durationMs < 0)
        {
            durationMs = 0;
        }

        if (_videoDurationMs != durationMs)
        {
            _videoDurationMs = durationMs;
            OnPropertyChanged(nameof(VideoTimeText));
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetVideoFrame(ImageSource frame)
    {
        var wasLoaded = IsLoaded;
        SetCoverLoadStatus(VideoCoverLoadStatus.None);
        VideoFrame = frame;
        if (wasLoaded != IsLoaded)
        {
            OnPropertyChanged(nameof(IsLoaded));
        }

        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void ClearVideoFrame()
    {
        var wasLoaded = IsLoaded;
        VideoFrame = null;
        if (wasLoaded != IsLoaded)
        {
            OnPropertyChanged(nameof(IsLoaded));
        }

        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void SetCoverLoadStatus(VideoCoverLoadStatus status)
    {
        if (_coverLoadStatus != status)
        {
            _coverLoadStatus = status;
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public bool TryBeginCoverLoad()
    {
        if (!IsVideo || IsVideoPlaying || VideoFrame is not null || _isCoverLoading)
        {
            return false;
        }

        _isCoverLoading = true;
        SetCoverLoadStatus(VideoCoverLoadStatus.None);
        return true;
    }

    public void EndCoverLoad()
    {
        _isCoverLoading = false;
    }

    public bool TryCacheVideoData(MemoryStream videoStream, long maxCachedBytes)
    {
        if (!IsVideo || videoStream.Length <= 0 || videoStream.Length > maxCachedBytes)
        {
            return false;
        }

        if (!videoStream.TryGetBuffer(out var buffer))
        {
            return false;
        }

        _cachedVideoData = new ArraySegment<byte>(buffer.Array!, buffer.Offset, (int)videoStream.Length);
        return true;
    }

    public MemoryStream? TryTakeCachedVideoStream()
    {
        var cachedVideoData = _cachedVideoData;
        _cachedVideoData = null;
        if (cachedVideoData is not { Array: { } buffer })
        {
            return null;
        }

        return new MemoryStream(buffer, cachedVideoData.Value.Offset, cachedVideoData.Value.Count, writable: false);
    }

    public void ClearCachedVideoData()
    {
        _cachedVideoData = null;
    }

    public void SetCoverSession(VideoPlaybackSession session)
    {
        DisposeSessionInBackground(_coverSession, stopFirst: true);
        _coverSession = session;
    }

    public void ClearCoverSession(VideoPlaybackSession? session)
    {
        if (session is not null && !ReferenceEquals(_coverSession, session))
        {
            DisposeSessionInBackground(session, stopFirst: false);
            return;
        }

        DisposeSessionInBackground(_coverSession, stopFirst: false);
        _coverSession = null;
    }

    public void DetachCoverSession(VideoPlaybackSession session)
    {
        if (ReferenceEquals(_coverSession, session))
        {
            _coverSession = null;
        }
    }

    public void StopCoverSession()
    {
        DisposeSessionInBackground(_coverSession, stopFirst: true);
        _coverSession = null;
        _isCoverLoading = false;
    }

    public void SetAspectRatio(double aspectRatio, double pageWidth)
    {
        if (aspectRatio > 0)
        {
            _aspectRatio = aspectRatio;
            Resize(pageWidth);
        }
    }

    public void StopVideo()
    {
        IsVideoPlaying = false;
        IsVideoPaused = false;
        MediaPlayer = null;
        SetVideoPosition(0);
        DisposeSessionInBackground(_videoPlaybackSession, stopFirst: true);
        _videoPlaybackSession = null;
        StopCoverSession();
        ClearCachedVideoData();
    }

    public void Resize(double pageWidth)
    {
        DisplayWidth = Math.Max(1, pageWidth);
        DisplayHeight = DisplayWidth * _aspectRatio;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatVideoTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void DisposeSessionInBackground(VideoPlaybackSession? session, bool stopFirst)
    {
        if (session is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (stopFirst)
                {
                    session.Stop();
                }

                session.Dispose();
            }
            catch
            {
                // LibVLC cleanup is best-effort; the UI should never freeze on native disposal.
            }
        });
    }
}

public sealed record ComicArchiveEntry(string Key, ComicMediaType Type);

public sealed record ImageLoadRequest(int Index, string EntryKey);

public sealed record VideoCoverLoadResult(MemoryStream? VideoStream, VideoCoverLoadStatus Status);

public sealed class VideoPlaybackSession : IDisposable
{
    private readonly MemoryStream _videoStream;
    private readonly StreamMediaInput _input;
    private readonly VlcMedia _media;
    private readonly VideoFrameRenderer _renderer;
    private bool _isStopped;
    private bool _isDisposed;

    public VideoPlaybackSession(
        MemoryStream videoStream,
        StreamMediaInput input,
        VlcMedia media,
        VlcMediaPlayer mediaPlayer,
        VideoFrameRenderer renderer)
    {
        _videoStream = videoStream;
        _input = input;
        _media = media;
        _renderer = renderer;
        MediaPlayer = mediaPlayer;
    }

    public VlcMediaPlayer MediaPlayer { get; }

    public VideoFrameRenderer Renderer => _renderer;

    public void Stop()
    {
        if (_isStopped || _isDisposed)
        {
            return;
        }

        _isStopped = true;
        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Stop();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        MediaPlayer.Dispose();
        _renderer.Dispose();
        _media.Dispose();
        _input.Dispose();
        _videoStream.Dispose();
    }
}

public sealed class VideoFrameRenderer : IDisposable
{
    private readonly Action<ImageSource> _setFrame;
    private readonly Action<uint, uint> _setVideoSize;
    private readonly TaskCompletionSource _firstFrameDisplayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VlcMediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private readonly VlcMediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly VlcMediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly object _frameSync = new();

    private byte[]? _frameBuffer;
    private byte[]? _pendingFrame;
    private GCHandle _frameBufferHandle;
    private WriteableBitmap? _bitmap;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private bool _frameUpdateQueued;
    private bool _isDisposed;

    public VideoFrameRenderer(Action<ImageSource> setFrame, Action<uint, uint> setVideoSize)
    {
        _setFrame = setFrame;
        _setVideoSize = setVideoSize;
        _lockCallback = LockVideo;
        _unlockCallback = UnlockVideo;
        _displayCallback = DisplayVideo;
        _formatCallback = FormatVideo;
        _cleanupCallback = CleanupVideo;
    }

    public void AttachTo(VlcMediaPlayer mediaPlayer)
    {
        mediaPlayer.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        mediaPlayer.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
    }

    public Task FirstFrameDisplayed => _firstFrameDisplayed.Task;

    private uint FormatVideo(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _width = width;
        _height = height;
        _pitch = width * 4;
        pitches = _pitch;
        lines = height;

        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        AllocateFrameBuffer((int)(_pitch * _height));

        Application.Current.Dispatcher.Invoke(() =>
        {
            var bitmap = new WriteableBitmap((int)_width, (int)_height, 96, 96, PixelFormats.Bgr32, null);
            _bitmap = bitmap;
            _setVideoSize(_width, _height);
        });

        return 1;
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        if (_frameBufferHandle.IsAllocated)
        {
            Marshal.WriteIntPtr(planes, _frameBufferHandle.AddrOfPinnedObject());
        }

        return IntPtr.Zero;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (_isDisposed || _frameBuffer is null || _bitmap is null || _width == 0 || _height == 0)
        {
            return;
        }

        var frameSize = (int)(_pitch * _height);
        var shouldQueueRender = false;
        lock (_frameSync)
        {
            if (_isDisposed || _frameBuffer is null)
            {
                return;
            }

            if (_pendingFrame is null || _pendingFrame.Length != frameSize)
            {
                _pendingFrame = new byte[frameSize];
            }

            Buffer.BlockCopy(_frameBuffer, 0, _pendingFrame, 0, frameSize);
            if (!_frameUpdateQueued)
            {
                _frameUpdateQueued = true;
                shouldQueueRender = true;
            }
        }

        if (shouldQueueRender)
        {
            Application.Current.Dispatcher.BeginInvoke((Action)RenderPendingFrame, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void RenderPendingFrame()
    {
        lock (_frameSync)
        {
            if (_isDisposed || _bitmap is null || _pendingFrame is null || _width == 0 || _height == 0)
            {
                _frameUpdateQueued = false;
                return;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, (int)_width, (int)_height), _pendingFrame, (int)_pitch, 0);
            _setFrame(_bitmap);
            _frameUpdateQueued = false;
            _firstFrameDisplayed.TrySetResult();
        }
    }

    private void CleanupVideo(ref IntPtr opaque)
    {
        ReleaseFrameBuffer();
    }

    private void AllocateFrameBuffer(int size)
    {
        ReleaseFrameBuffer();
        _frameBuffer = new byte[size];
        _frameBufferHandle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
    }

    private void ReleaseFrameBuffer()
    {
        if (_frameBufferHandle.IsAllocated)
        {
            _frameBufferHandle.Free();
        }

        _frameBuffer = null;
        lock (_frameSync)
        {
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }
    }

    public void Dispose()
    {
        lock (_frameSync)
        {
            _isDisposed = true;
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }

        _firstFrameDisplayed.TrySetCanceled();
        ReleaseFrameBuffer();
    }
}

public enum ComicMediaType
{
    Image,
    Video
}

public enum VideoCoverLoadStatus
{
    None,
    Success,
    Failed,
    Oversized
}

public sealed class MinimumThumbTrack : Track
{
    public static readonly DependencyProperty MinimumThumbLengthProperty =
        DependencyProperty.Register(
            nameof(MinimumThumbLength),
            typeof(double),
            typeof(MinimumThumbTrack),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinimumThumbLength
    {
        get => (double)GetValue(MinimumThumbLengthProperty);
        set => SetValue(MinimumThumbLengthProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Thumb is null || MinimumThumbLength <= 0)
        {
            return size;
        }

        if (Orientation == Orientation.Vertical)
        {
            ArrangeVerticalThumb(arrangeSize);
        }
        else
        {
            ArrangeHorizontalThumb(arrangeSize);
        }

        return size;
    }

    private void ArrangeVerticalThumb(Size arrangeSize)
    {
        var currentHeight = Thumb.RenderSize.Height;
        var targetHeight = Math.Min(MinimumThumbLength, arrangeSize.Height);
        if (currentHeight >= targetHeight || targetHeight <= 0)
        {
            return;
        }

        var currentTop = VisualTreeHelper.GetOffset(Thumb).Y;
        var top = Math.Clamp(currentTop - (targetHeight - currentHeight) / 2, 0, Math.Max(0, arrangeSize.Height - targetHeight));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, arrangeSize.Width, top));
        Thumb.Arrange(new Rect(0, top, arrangeSize.Width, targetHeight));
        IncreaseRepeatButton?.Arrange(new Rect(0, top + targetHeight, arrangeSize.Width, Math.Max(0, arrangeSize.Height - top - targetHeight)));
    }

    private void ArrangeHorizontalThumb(Size arrangeSize)
    {
        var currentWidth = Thumb.RenderSize.Width;
        var targetWidth = Math.Min(MinimumThumbLength, arrangeSize.Width);
        if (currentWidth >= targetWidth || targetWidth <= 0)
        {
            return;
        }

        var currentLeft = VisualTreeHelper.GetOffset(Thumb).X;
        var left = Math.Clamp(currentLeft - (targetWidth - currentWidth) / 2, 0, Math.Max(0, arrangeSize.Width - targetWidth));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, left, arrangeSize.Height));
        Thumb.Arrange(new Rect(left, 0, targetWidth, arrangeSize.Height));
        IncreaseRepeatButton?.Arrange(new Rect(left + targetWidth, 0, Math.Max(0, arrangeSize.Width - left - targetWidth), arrangeSize.Height));
    }
}

internal sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var ix = 0;
        var iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var numberComparison = CompareNumberChunks(x, ref ix, y, ref iy);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }
            }
            else
            {
                var charComparison = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (charComparison != 0)
                {
                    return charComparison;
                }

                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int CompareNumberChunks(string x, ref int ix, string y, ref int iy)
    {
        var startX = ix;
        var startY = iy;

        while (ix < x.Length && char.IsDigit(x[ix]))
        {
            ix++;
        }

        while (iy < y.Length && char.IsDigit(y[iy]))
        {
            iy++;
        }

        var chunkX = x[startX..ix].TrimStart('0');
        var chunkY = y[startY..iy].TrimStart('0');

        if (chunkX.Length == 0)
        {
            chunkX = "0";
        }

        if (chunkY.Length == 0)
        {
            chunkY = "0";
        }

        var lengthComparison = chunkX.Length.CompareTo(chunkY.Length);
        if (lengthComparison != 0)
        {
            return lengthComparison;
        }

        var valueComparison = string.Compare(chunkX, chunkY, CultureInfo.InvariantCulture, CompareOptions.Ordinal);
        if (valueComparison != 0)
        {
            return valueComparison;
        }

        return (ix - startX).CompareTo(iy - startY);
    }
}
