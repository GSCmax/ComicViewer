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
    private int _cacheLoadVersion;
    private int _activeCacheLoadVersion;
    private int _coverLoadVersion;
    private double _pageWidth = 800;
    private bool _isArchiveLoading;
    private bool _memoryCleanupScheduled;
    private bool _isCacheLoadWorkerRunning;
    private int _pendingCachePageIndex = -1;
    private TaskCompletionSource<string?>? _passwordPromptCompletion;
    private readonly LibVLC _libVlc;
    private readonly SemaphoreSlim _coverLoadSemaphore = new(1, 1);

    public ObservableCollection<ComicPage> Pages { get; } = [];

    public ObservableCollection<string> PasswordHistory { get; } = [];

    public MainWindow()
    {
        Core.Initialize();
        _libVlc = new LibVLC();
        InitializeComponent();
        DataContext = this;
        LoadPasswordHistory();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _archivePath = null;
            _loadingWindowStart = null;
            _loadingWindowEnd = null;
            _cacheLoadVersion++;
            _coverLoadVersion++;
            StopAllVideos();
            _libVlc.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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
    }

    private async Task LoadArchiveWithPasswordRetryAsync(string archivePath)
    {
        string? password = null;

        while (true)
        {
            try
            {
                _isArchiveLoading = true;
                SetLoadingState(true, $"正在读取目录 {Path.GetFileName(archivePath)} ...");
                UpdatePageWidth();
                _cacheLoadVersion++;
                _activeCacheLoadVersion = 0;
                _coverLoadVersion++;
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
                _cacheLoadVersion++;
                _activeCacheLoadVersion = 0;
                _coverLoadVersion++;
                _pendingCachePageIndex = -1;

                Pages.Clear();
                foreach (var page in pages)
                {
                    Pages.Add(page);
                }

                ImageScrollViewer.ScrollToTop();
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
        _cacheLoadVersion++;
        _activeCacheLoadVersion = 0;
        _coverLoadVersion++;
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
        Path.Combine(AppContext.BaseDirectory, "password-history.json");

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
        return archive.Entries
            .Where(entry => !entry.IsDirectory && IsSupportedMediaFile(entry.Key))
            .OrderBy(entry => entry.Key, NaturalFileNameComparer.Instance)
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
        Action<int, BitmapImage>? imageLoaded = null,
        Func<bool>? shouldContinue = null)
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
            if (shouldContinue?.Invoke() == false)
            {
                break;
            }

            if (!requestedPagesByKey.TryGetValue(entry.Key!, out var pageIndex))
            {
                continue;
            }

            using var entryStream = entry.OpenEntryStream();
            using var memoryStream = new MemoryStream();
            if (!TryCopyToMemoryStream(entryStream, memoryStream, shouldContinue))
            {
                break;
            }

            memoryStream.Position = 0;
            if (shouldContinue?.Invoke() == false)
            {
                break;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = memoryStream;
            image.EndInit();
            image.Freeze();
            images[pageIndex] = image;
            imageLoaded?.Invoke(pageIndex, image);

            if (images.Count == requestedPagesByKey.Count)
            {
                break;
            }
        }

        return images;
    }

    private static bool TryCopyToMemoryStream(Stream source, MemoryStream destination, Func<bool>? shouldContinue)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            if (shouldContinue?.Invoke() == false)
            {
                return false;
            }

            var bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                return true;
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }

    private MemoryStream LoadVideoToMemory(ComicPage page, Func<bool>? shouldContinue = null)
    {
        if (_archivePath is null)
        {
            throw new InvalidOperationException("还没有打开压缩包。");
        }

        var options = new ReaderOptions
        {
            Password = _password
        };

        using var archive = ArchiveFactory.OpenArchive(_archivePath, options);
        var entry = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory && string.Equals(entry.Key, page.EntryKey, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new FileNotFoundException("在压缩包中找不到这个视频。", page.EntryKey);
        }

        using var entryStream = entry.OpenEntryStream();
        var memoryStream = new MemoryStream();
        if (!TryCopyToMemoryStream(entryStream, memoryStream, shouldContinue))
        {
            memoryStream.Dispose();
            throw new OperationCanceledException();
        }

        memoryStream.Position = 0;
        return memoryStream;
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
        try
        {
            page.StopVideo();
            StopOtherVideos(page);
            page.StopCoverSession();
            StatusTextBlock.Text = $"正在载入视频到内存 {Path.GetFileName(page.EntryKey)} ...";
            var videoStream = await Task.Run(() => LoadVideoToMemory(page));
            var session = CreateMemoryVideoPlaybackSession(page, videoStream, disableAudio: false);
            page.SetVideoPlaybackSession(session);
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
        catch (Exception ex)
        {
            page.IsVideoPlaying = false;
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, ex.Message, "无法播放视频", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageWidth(e.NewSize.Width);
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var wheelLines = SystemParameters.WheelScrollLines > 0
            ? SystemParameters.WheelScrollLines
            : 3;
        var deltaSteps = e.Delta / (double)System.Windows.Input.Mouse.MouseWheelDeltaForOneLine;
        var scrollPixels = deltaSteps * wheelLines * MouseWheelPixelsPerLine * MouseWheelScrollMultiplier;

        ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.VerticalOffset - scrollPixels);
        e.Handled = true;
    }

    private void UpdatePageWidth(double fallbackWidth = 0)
    {
        var width = ImageScrollViewer.ViewportWidth;
        if (double.IsNaN(width) || width <= 1)
        {
            width = fallbackWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = ImageScrollViewer.ActualWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = ActualWidth;
        }

        _pageWidth = Math.Max(1, width);
        ImagesItemsControl.Width = _pageWidth;
        foreach (var page in Pages)
        {
            page.Resize(_pageWidth);
        }
    }

    private void ImageScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_isArchiveLoading || Pages.Count == 0 || _archivePath is null)
        {
            return;
        }

        var currentPageIndex = GetPageIndexAtOffset(ImageScrollViewer.VerticalOffset);
        UpdateReadingStatus(currentPageIndex);
        QueueCacheWindowLoad(currentPageIndex);
    }

    private void QueueCacheWindowLoad(int currentPageIndex)
    {
        var window = GetPreloadWindow(currentPageIndex);
        if (_loadingWindowStart == window.Start
            && _loadingWindowEnd == window.End
            && _activeCacheLoadVersion == _cacheLoadVersion)
        {
            return;
        }

        if (window.Start == _cacheWindowStart && window.End == _cacheWindowEnd && !WindowHasMissingImages(window.Start, window.End))
        {
            return;
        }

        _pendingCachePageIndex = currentPageIndex;
        _cacheLoadVersion++;
        _coverLoadVersion++;
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
                    && _activeCacheLoadVersion == _cacheLoadVersion)
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
                    UpdateReadingStatus(GetPageIndexAtOffset(ImageScrollViewer.VerticalOffset));
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
        if (_archivePath is null || Pages.Count == 0)
        {
            return;
        }

        windowStart = Math.Clamp(windowStart, 0, Pages.Count - 1);
        windowEnd = Math.Clamp(windowEnd, windowStart, Pages.Count - 1);
        if (_loadingWindowStart == windowStart && _loadingWindowEnd == windowEnd)
        {
            return;
        }

        _loadingWindowStart = windowStart;
        _loadingWindowEnd = windowEnd;
        var version = ++_cacheLoadVersion;
        _activeCacheLoadVersion = version;
        var pageSnapshot = Pages.ToList();
        PruneMediaOutsideWindow(windowStart, windowEnd);
        var requests = CreateImageLoadRequests(pageSnapshot, windowStart, windowEnd - windowStart + 1, onlyMissing: true);

        try
        {
            var loadedImages = await Task.Run(() => LoadImagesFromArchive(
                _archivePath,
                _password,
                requests,
                GetDecodePixelWidth(),
                (pageIndex, image) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _cacheLoadVersion
                        || pageIndex < windowStart
                        || pageIndex > windowEnd
                        || pageIndex >= Pages.Count
                        || !Pages[pageIndex].IsImage)
                    {
                        return;
                    }

                    Pages[pageIndex].SetImage(image, _pageWidth);
                    UpdateReadingStatus(GetPageIndexAtOffset(ImageScrollViewer.VerticalOffset));
                })),
                () => version == _cacheLoadVersion));
            if (version != _cacheLoadVersion)
            {
                return;
            }

            ApplyLoadedImages(loadedImages, windowStart, windowEnd);
            _cacheWindowStart = windowStart;
            _cacheWindowEnd = windowEnd;
            var coverVersion = ++_coverLoadVersion;
            _ = ScheduleVideoCoversAsync(windowStart, windowEnd, coverVersion, currentPageIndex);
        }
        catch (Exception ex)
        {
            if (!showErrors)
            {
                throw;
            }

            if (version != _cacheLoadVersion)
            {
                return;
            }

            StatusTextBlock.Text = "读取图片失败";
            MessageBox.Show(this, ex.Message, "无法读取图片", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_loadingWindowStart == windowStart && _loadingWindowEnd == windowEnd)
            {
                _loadingWindowStart = null;
                _loadingWindowEnd = null;
            }

            if (_activeCacheLoadVersion == version)
            {
                _activeCacheLoadVersion = 0;
            }
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
        var unloadedAnyMedia = false;
        for (var i = 0; i < Pages.Count; i++)
        {
            if (i >= windowStart && i <= windowEnd)
            {
                continue;
            }

            var page = Pages[i];
            if (page.IsImage)
            {
                unloadedAnyMedia |= page.IsImageLoaded;
                page.SetImage(null, _pageWidth);
            }
            else if (page.IsVideo)
            {
                unloadedAnyMedia |= page.VideoFrame is not null;
                page.StopCoverSession();
                page.ClearVideoFrame();
            }
        }

        if (unloadedAnyMedia)
        {
            ScheduleMemoryCleanup();
        }
    }

    private int GetDecodePixelWidth()
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Max(MinimumDecodePixelWidth, (int)Math.Ceiling(_pageWidth * dpiScale));
    }

    private void ScheduleMemoryCleanup()
    {
        if (_memoryCleanupScheduled)
        {
            return;
        }

        _memoryCleanupScheduled = true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(600);
            _memoryCleanupScheduled = false;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        });
    }

    private async Task ScheduleVideoCoversAsync(int windowStart, int windowEnd, int version, int currentPageIndex)
    {
        try
        {
            await Task.Delay(1200);
            if (!IsCoverWindowCurrent(windowStart, windowEnd, version))
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
                if (!IsCoverWindowCurrent(windowStart, windowEnd, version))
                {
                    return;
                }

                await EnsureVideoCoverAsync(page, version);
            }
        }
        catch
        {
            // Video covers are best-effort and should never slow or break image reading.
        }
    }

    private bool IsCoverWindowCurrent(int windowStart, int windowEnd, int version)
    {
        return version == _coverLoadVersion
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

    private async Task EnsureVideoCoverAsync(ComicPage page, int version)
    {
        if (!page.TryBeginCoverLoad())
        {
            return;
        }

        VideoPlaybackSession? session = null;
        var semaphoreAcquired = false;
        try
        {
            await _coverLoadSemaphore.WaitAsync();
            semaphoreAcquired = true;
            if (version != _coverLoadVersion || page.IsVideoPlaying)
            {
                return;
            }

            var videoStream = await Task.Run(() => LoadVideoToMemory(page, () => version == _coverLoadVersion));
            if (version != _coverLoadVersion || page.IsVideoPlaying)
            {
                videoStream.Dispose();
                return;
            }

            session = CreateMemoryVideoPlaybackSession(page, videoStream, disableAudio: true);
            page.SetCoverSession(session);

            if (!session.MediaPlayer.Play())
            {
                return;
            }

            await session.Renderer.FirstFrameDisplayed.WaitAsync(TimeSpan.FromSeconds(4));
            await Dispatcher.InvokeAsync(() => UpdateReadingStatus(GetPageIndexAtOffset(ImageScrollViewer.VerticalOffset)));
            session.Stop();
        }
        catch
        {
            // Cover extraction is best-effort; the play button remains available.
        }
        finally
        {
            page.ClearCoverSession(session);
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
        }
    }

    public void SetVideoFrame(ImageSource frame)
    {
        var wasLoaded = IsLoaded;
        VideoFrame = frame;
        if (wasLoaded != IsLoaded)
        {
            OnPropertyChanged(nameof(IsLoaded));
        }
    }

    public void ClearVideoFrame()
    {
        var wasLoaded = IsLoaded;
        VideoFrame = null;
        if (wasLoaded != IsLoaded)
        {
            OnPropertyChanged(nameof(IsLoaded));
        }
    }

    public bool TryBeginCoverLoad()
    {
        if (!IsVideo || IsVideoPlaying || VideoFrame is not null || _isCoverLoading)
        {
            return false;
        }

        _isCoverLoading = true;
        return true;
    }

    public void EndCoverLoad()
    {
        _isCoverLoading = false;
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
            _setFrame(bitmap);
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
