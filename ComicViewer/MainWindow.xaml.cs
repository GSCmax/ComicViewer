using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ComicViewer;

public partial class MainWindow : Window
{
    private const int MinimumDecodePixelWidth = 480;
    private const int MediaLoadParallelism = 2;
    private const long MaxMediaCacheBytes = 512L * 1024 * 1024;
    private const long MaxInMemoryVideoPlaybackBytes = MaxMediaCacheBytes;
    private const int ImageDecodeDebounceMilliseconds = 50;
    private const int VideoCoverIdleDelayMilliseconds = 350;
    private const double MouseWheelScrollMultiplier = 5d;
    private const double MouseWheelPixelsPerLine = 16d;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedMedia> _mediaCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadsInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodesInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedMedia = new(StringComparer.Ordinal);

    private ArchiveSession? _archiveSession;
    private CancellationTokenSource? _mediaLoadCts;
    private CancellationTokenSource? _videoPlayCts;
    private CancellationTokenSource? _videoCoverCts;
    private CancellationTokenSource? _imageDecodeDebounceCts;
    private Task? _mediaLoadTask;
    private TaskCompletionSource<string?>? _passwordPrompt;
    private ScrollViewer? _pagesScrollViewer;
    private MpvVideoPlayerControl? _sharedVideoPlayer;
    private ComicPage? _sharedVideoPage;
    private VideoThumbnailService? _videoThumbnailService;
    private HashSet<int> _realizedPageIndexesSnapshot = [];
    private HashSet<int> _visiblePageIndexesSnapshot = [];
    private string? _archivePath;
    private double _pageWidth = 800;
    private int? _firstVisiblePageIndexSnapshot;
    private int _cacheGeneration;
    private int _currentPageIndex;
    private long _cacheBytes;
    private long _reservedCacheBytes;
    private DateTime _lastCacheTrimUtc = DateTime.MinValue;
    private DateTime _lastScrollUtc = DateTime.MinValue;
    private bool _isLoadingArchive;
    private bool _isCancelingPageNumberInput;
    private bool _viewportSnapshotDirty = true;

    public ObservableCollection<ComicPage> Pages { get; } = [];

    public ObservableCollection<string> PasswordHistory { get; } = [];

    public MainWindow()
    {
        InitializeComponent();

        WindowBackdropHelper.Apply(this);

        DataContext = this;
        _sharedVideoPlayer = CreateVideoPlayer();
        VideoCoverGeneratorHost.Content = _sharedVideoPlayer;
        _videoThumbnailService = new VideoThumbnailService(new HeadlessMpvVideoThumbnailProvider());
        AttachPlaybackEvents(_sharedVideoPlayer);
        LoadPasswordHistory();

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            StopMediaLoader();
            CancelVideoPlayLoad();
            CancelVideoCoverGeneration();
            StopAllVideos();
            DisposeSharedVideoPlayer();
            DisposeArchiveSession();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _pagesScrollViewer = FindVisualChild<ScrollViewer>(PagesListBox);
        UpdatePageWidth();

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

        if (dialog.ShowDialog(this) == true)
        {
            await LoadArchiveWithPasswordRetryAsync(dialog.FileName);
        }
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
        MaximizeRestoreWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Dispatcher.BeginInvoke((Action)(() => UpdatePageWidth()));
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PasswordOverlay.Visibility == Visibility.Visible || _isLoadingArchive || Pages.Count == 0)
        {
            return;
        }

        var offset = GetNavigationKey(e) switch
        {
            Key.PageUp => -1,
            Key.PageDown => 1,
            _ => 0
        };

        if (offset == 0)
        {
            return;
        }

        NavigateByPageOffset(offset);
        e.Handled = true;
    }

    private static Key GetNavigationKey(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
    }

    private async Task LoadArchiveWithPasswordRetryAsync(string archivePath, string? initialPassword = null)
    {
        var password = initialPassword;
        var hasTriedKnownPasswords = false;
        var hasTriedAnyKnownPassword = false;

        while (true)
        {
            try
            {
                await LoadArchiveAsync(archivePath, password, $"正在读取目录 {Path.GetFileName(archivePath)} ...");
                return;
            }
            catch (Exception ex) when (IsLikelyPasswordProblem(ex))
            {
                ResetReader();
                _isLoadingArchive = false;
                SetLoadingState(false);

                if (!hasTriedKnownPasswords)
                {
                    hasTriedKnownPasswords = true;
                    var knownPasswords = GetKnownPasswordsToTry(password);
                    if (knownPasswords.Count > 0)
                    {
                        hasTriedAnyKnownPassword = true;
                        try
                        {
                            var knownPasswordResult = await TryKnownPasswordsAsync(archivePath, knownPasswords);
                            if (knownPasswordResult is not null)
                            {
                                ApplyArchiveSession(knownPasswordResult.Session, archivePath, knownPasswordResult.Password);
                                return;
                            }
                        }
                        catch (Exception knownPasswordException)
                        {
                            ResetReader();
                            ShowOpenArchiveError(knownPasswordException);
                            return;
                        }
                    }
                }

                password = await ShowPasswordOverlayAsync(archivePath, hasTriedAnyKnownPassword);
                if (password is null)
                {
                    StatusTextBlock.Text = "已取消打开压缩包";
                    return;
                }
            }
            catch (Exception ex)
            {
                ResetReader();
                ShowOpenArchiveError(ex);
                return;
            }
            finally
            {
                _isLoadingArchive = false;
                SetLoadingState(false);
            }
        }
    }

    private List<string> GetKnownPasswordsToTry(string? skippedPassword)
    {
        return PasswordHistory
            .Where(password => !string.IsNullOrWhiteSpace(password))
            .Distinct(StringComparer.Ordinal)
            .Where(password => !string.Equals(password, skippedPassword, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<KnownPasswordResult?> TryKnownPasswordsAsync(string archivePath, IReadOnlyList<string> knownPasswords)
    {
        _isLoadingArchive = true;
        SetLoadingState(true, $"正在尝试 {knownPasswords.Count} 个已知密码 ...");

        var nextPasswordIndex = -1;
        var hasResult = 0;
        var maxConcurrency = Math.Min(knownPasswords.Count, Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        var resultLock = new object();
        using var cancellation = new CancellationTokenSource();
        KnownPasswordResult? result = null;
        Exception? unexpectedException = null;

        async Task TryPasswordWorkerAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                var passwordIndex = Interlocked.Increment(ref nextPasswordIndex);
                if (passwordIndex >= knownPasswords.Count)
                {
                    return;
                }

                var password = knownPasswords[passwordIndex];
                try
                {
                    var session = await Task.Run(() => ArchiveSession.Open(archivePath, password), cancellation.Token);
                    if (Interlocked.CompareExchange(ref hasResult, 1, 0) == 0)
                    {
                        lock (resultLock)
                        {
                            result = new KnownPasswordResult(password, session);
                        }

                        await cancellation.CancelAsync();
                    }
                    else
                    {
                        DisposeArchiveSessionInBackground(session);
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsLikelyPasswordProblem(ex))
                {
                }
                catch (Exception ex)
                {
                    lock (resultLock)
                    {
                        unexpectedException ??= ex;
                    }

                    await cancellation.CancelAsync();
                    return;
                }
            }
        }

        var workers = Enumerable.Range(0, maxConcurrency)
            .Select(_ => TryPasswordWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers);

        if (result is not null)
        {
            return result;
        }

        if (unexpectedException is not null)
        {
            throw unexpectedException;
        }

        return null;
    }

    private async Task LoadArchiveAsync(string archivePath, string? password, string loadingStatus)
    {
        _isLoadingArchive = true;
        SetLoadingState(true, loadingStatus);
        ResetReader();
        UpdatePageWidth();

        var session = await Task.Run(() => ArchiveSession.Open(archivePath, password));
        ApplyArchiveSession(session, archivePath, password);
    }

    private void ApplyArchiveSession(ArchiveSession session, string archivePath, string? password)
    {
        var pages = session.MediaEntries
            .Select((entry, index) => new ComicPage(index, entry.Key, entry.Type, entry.Size, _pageWidth))
            .ToList();

        _archiveSession = session;
        _archivePath = archivePath;

        Pages.Clear();
        foreach (var page in pages)
        {
            Pages.Add(page);
        }
        InvalidateViewportSnapshot();

        GetPagesScrollViewer()?.ScrollToTop();
        if (Pages.Count == 0)
        {
            StatusTextBlock.Text = "压缩包中没有找到支持的图片或视频文件";
            return;
        }

        RememberPassword(password);
        UpdatePageWidth();
        UpdateReadingStatus(0);
        StartMediaLoading(0);
        RefreshDecodedImages();
    }

    private void ShowOpenArchiveError(Exception exception)
    {
        StatusTextBlock.Text = "打开失败";
        MessageBox.Show(this, exception.Message, "无法打开压缩包", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ResetReader()
    {
        _archivePath = null;
        StopMediaLoader();
        CancelVideoPlayLoad();
        CancelVideoCoverGeneration();
        StopAllVideos();
        DisposeArchiveSession();
        Pages.Clear();
        InvalidateViewportSnapshot();
        ClearReadingProgress();
    }

    private void DisposeArchiveSession()
    {
        ClearMediaCache(clearPages: false);
        var session = _archiveSession;
        _archiveSession = null;
        DisposeArchiveSessionInBackground(session);
    }

    private static void DisposeArchiveSessionInBackground(IDisposable? session)
    {
        if (session is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                session.Dispose();
            }
            catch
            {
            }
        });
    }

    private void SetLoadingState(bool isLoading, string? status = null)
    {
        OpenArchiveButton.IsEnabled = !isLoading;
        if (status is not null)
        {
            StatusTextBlock.Text = status;
        }
    }
}
