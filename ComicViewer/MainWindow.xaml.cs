using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ComicViewer;

public partial class MainWindow : Window
{
    private const double MouseWheelScrollMultiplier = 5d;
    private const double MouseWheelPixelsPerLine = 16d;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PasswordStore _passwords = new();
    private readonly VideoThumbnailService _thumbnails = new();
    private readonly PlaybackController _playback;
    private ReaderSession? _reader;
    private PageImageLoader? _images;
    private Task? _openTask;
    private TaskCompletionSource<string?>? _passwordPrompt;
    private IInputElement? _pageNumberPreviousFocus;
    private ScrollViewer? _pagesScrollViewer;
    private string? _archivePath;
    private int _currentPageIndex;
    private bool _isLoadingArchive;
    private bool _isCancelingPageNumberInput;
    private bool _closing;
    private bool _allowClose;

    public static readonly DependencyProperty PageWidthProperty = DependencyProperty.Register(
        nameof(PageWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(800d));
    public double PageWidth { get => (double)GetValue(PageWidthProperty); private set => SetValue(PageWidthProperty, value); }
    public IReadOnlyList<ComicPage> Pages { get; private set; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _playback = new PlaybackController(PlayerParkingHost, () => PagesListBox.UpdateLayout());
        _playback.Changed += RefreshReaderViewport;
        _playback.Error += ex => ShowError(ex, "视频播放失败");
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePageWidth();
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            await (_openTask = LoadArchiveWithPasswordRetryAsync(path));
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingArchive || _closing) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择漫画压缩包",
            Filter = "漫画压缩包 (*.zip;*.rar;*.cbz;*.cbr)|*.zip;*.rar;*.cbz;*.cbr|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            await (_openTask = LoadArchiveWithPasswordRetryAsync(dialog.FileName));
    }

    private async Task LoadArchiveWithPasswordRetryAsync(string path)
    {
        _isLoadingArchive = true;
        OpenArchiveButton.IsEnabled = false;
        string? password = null;
        var knownTried = false;
        try
        {
            await ResetReaderAsync();
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                StatusTextBlock.Text = $"正在读取目录 {Path.GetFileName(path)} ...";
                ArchiveSession archive;
                try { archive = await ArchiveSession.OpenAsync(path, password, _lifetime.Token); }
                catch (Exception ex) when (ArchiveSession.IsPasswordError(ex))
                {
                    if (!knownTried)
                    {
                        knownTried = true;
                        var known = _passwords.Except(password);
                        StatusTextBlock.Text = $"正在尝试 {known.Count} 个已知密码 ...";
                        var result = await ArchiveSession.TryKnownAsync(path, known, _lifetime.Token);
                        if (result is not null)
                        {
                            ApplyArchiveSession(result.Session, path, result.Password);
                            return;
                        }
                    }
                    password = await ShowPasswordOverlayAsync(path, knownTried);
                    if (password is null) { StatusTextBlock.Text = "已取消打开压缩包"; return; }
                    continue;
                }
                ApplyArchiveSession(archive, path, password);
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex, "无法打开压缩包"); }
        finally
        {
            _isLoadingArchive = false;
            OpenArchiveButton.IsEnabled = !_closing;
            if (!_closing && _reader is not null) RefreshReaderViewport();
        }
    }

    private void ApplyArchiveSession(ArchiveSession archive, string path, string? password)
    {
        _reader = new ReaderSession(archive);
        _archivePath = path;
        Pages = archive.MediaEntries.Select((entry, index) => new ComicPage(index, entry)).ToArray();
        PagesListBox.ItemsSource = Pages;
        _currentPageIndex = 0;
        var reader = _reader;
        _images = new PageImageLoader(reader, Pages, _thumbnails);
        _images.Updated += (index, error) =>
        {
            if (!ReferenceEquals(_reader, reader) || _closing) return;
            if (error is not null) StatusTextBlock.Text = $"加载失败: {Pages[index].EntryKey} - {error.Message}";
            else UpdateReadingStatus(_currentPageIndex);
        };
        GetPagesScrollViewer()?.ScrollToTop();
        _passwords.Remember(password);
        UpdatePageWidth();
        if (Pages.Count == 0) StatusTextBlock.Text = "压缩包中没有找到支持的图片或视频文件";
        else UpdateReadingStatus(0);
    }

    private async Task ResetReaderAsync()
    {
        await _playback.StopAsync();
        var reader = _reader;
        var images = _images;
        _reader = null;
        _images = null;
        _archivePath = null;
        Pages = [];
        PagesListBox.ItemsSource = Pages;
        ClearReadingProgress();
        // Start cancellation together; disposal waits for all readers before closing the archive.
        var imageStop = images?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var readerStop = reader?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(imageStop, readerStop);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        OpenArchiveButton.IsEnabled = false;
        _lifetime.Cancel();
        CompletePasswordPrompt(null);
        try
        {
            if (_openTask is not null) await _openTask;
            await ResetReaderAsync();
            await _playback.DisposeAsync();
            _thumbnails.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            _lifetime.Dispose();
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void ShowError(Exception ex, string title)
    {
        if (_closing) return;
        StatusTextBlock.Text = title;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();
    private void MaximizeRestoreWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeRestoreWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Dispatcher.BeginInvoke((Action)(() => UpdatePageWidth()));
    }
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PasswordOverlay.Visibility == Visibility.Visible || _isLoadingArchive || Pages.Count == 0) return;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        switch (key)
        {
            case Key.Home: CancelPageNumberInput(); ScrollPageToTop(0); break;
            case Key.End: CancelPageNumberInput(); ScrollPageToTop(Pages.Count - 1); break;
            case Key.PageUp: NavigateByPageOffset(-1); break;
            case Key.PageDown: NavigateByPageOffset(1); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void PlayVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingArchive || _closing || _reader is null
            || sender is not Button { CommandParameter: ComicPage page } || !page.IsVideo) return;
        e.Handled = true;
        await _playback.PlayAsync(_reader, page);
        Mouse.Capture(null);
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComicPage page } && page.IsVideoPlaying)
        {
            _playback.Pause();
            Mouse.Capture(null);
            e.Handled = true;
        }
    }
}
