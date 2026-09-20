using ComicViewer;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _root = "";
    private static byte[] _png = [];

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "ComicViewer.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Console.WriteLine(e.Exception); e.Handled = true; _failed++; app.Shutdown(1); };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                _png = MakePng();
                await Check("default encoded cache is 1 GiB", DefaultBudget);
                await Check("read deduplication and independent waiter cancellation", SharedRead);
                await Check("pinned cache, strict reservation and retry after release", Budget);
                await Check("eviction follows recent access", Lru);
                await Check("prefetch preserves viewport buffers under pressure", ProtectedCache);
                await Check("old session cancellation cannot touch a new session", SessionIsolation);
                await Check("session shutdown waits for readers; leases remain counted", Shutdown);
                await Check("failed entries are not read repeatedly", Failure);
                await Check("shutdown drains queued and running reads", QueuedShutdown);
                await Check("duplicate ZIP names remain separate indexed entries", ZipRead);
                await Check("archive payload excludes encryption padding and detects truncation", EntryLength);
                await Check("encrypted ZIP and concurrent known-password retry", Passwords);
                await Check("image decode is frozen and obeys pixel budget", Decode);
                await Check("viewport release and corrupt-image retry suppression", Images);
                await Check("bounded reading order and natural filenames", Ordering);
                await Check("WPF resources, layout, archive load, jump and switch", WindowSmoke);
                var archiveIndex = Array.IndexOf(args, "--archive");
                if (archiveIndex >= 0 && archiveIndex + 1 < args.Length)
                    await Check("external archive image read and decode", () => ExternalArchive(args[archiveIndex + 1]), 180);
                var videoIndex = Array.IndexOf(args, "--video");
                if (videoIndex >= 0 && videoIndex + 1 < args.Length)
                {
                    var video = File.ReadAllBytes(args[videoIndex + 1]);
                    await Check("native thumbnail creation and source release", () => Thumbnail(video));
                    await Check("native playback, pause, replacement and disposal", () => Playback(video));
                    await Check("mixed archive playback host and archive switch", () => MixedArchive(video));
                }
                Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            }
            finally
            {
                // Only remove the unique fixture directory owned by this run.
                Directory.Delete(_root, recursive: true);
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        });
        return app.Run();
    }

    private static async Task Check(string name, Func<Task> test, int timeoutSeconds = 25)
    {
        var watch = Stopwatch.StartNew();
        try { await test().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); _passed++; Console.WriteLine($"PASS {name} ({watch.ElapsedMilliseconds} ms)"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static async Task DefaultBudget()
    {
        await using var session = new ReaderSession(new FakeArchive(1));
        Require(session.CacheLimit == 1_073_741_824, "Wrong default limit");
    }

    private static async Task SharedRead()
    {
        using var release = new ManualResetEventSlim();
        var archive = new FakeArchive(64) { BeforeRead = ct => release.Wait(ct) };
        await using var session = new ReaderSession(archive, 100);
        using var cancellation = new CancellationTokenSource();
        var canceled = session.AcquireAsync(0, cancellation.Token);
        await archive.Entered.Task;
        var others = Enumerable.Range(0, 12).Select(_ => session.AcquireAsync(0, CancellationToken.None)).ToArray();
        cancellation.Cancel();
        await Throws<OperationCanceledException>(async () => await canceled);
        release.Set();
        var leases = await Task.WhenAll(others);
        Require(archive.Reads == 1, "Read was duplicated");
        Require(leases.All(lease => ReferenceEquals(leases[0].Content.Data.Array, lease.Content.Data.Array)), "Buffers were copied");
        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        Require(session.MemoryUsage == (64, 0), "Reservation was not finalized exactly once");
    }

    private static async Task Budget()
    {
        var archive = new FakeArchive(64, 64, 101);
        await using var session = new ReaderSession(archive, 100);
        var first = await session.AcquireAsync(0, default);
        await Throws<CacheBusyException>(async () => (await session.AcquireAsync(1, default)).Dispose());
        Require(archive.Reads == 1 && session.MemoryUsage == (64, 0), "Over-budget read reached the archive");
        Require(session.GetFailure(1) is null, "Budget pressure must be retryable");
        first.Dispose();
        using var second = await session.AcquireAsync(1, default);
        Require(!session.IsCached(0) && session.MemoryUsage == (64, 0), "Unpinned entry was not evicted");
        await Throws<MediaTooLargeException>(async () => (await session.AcquireAsync(2, default)).Dispose());
    }

    private static async Task Lru()
    {
        await using var session = new ReaderSession(new FakeArchive(40, 40, 40), 100);
        (await session.AcquireAsync(0, default)).Dispose();
        (await session.AcquireAsync(1, default)).Dispose();
        (await session.AcquireAsync(0, default)).Dispose();
        (await session.AcquireAsync(2, default)).Dispose();
        Require(session.IsCached(0) && !session.IsCached(1) && session.IsCached(2), "Incorrect LRU eviction");
    }

    private static async Task SessionIsolation()
    {
        using var never = new ManualResetEventSlim();
        var oldArchive = new FakeArchive(60) { BeforeRead = ct => never.Wait(ct) };
        var old = new ReaderSession(oldArchive, 100);
        var oldRead = old.AcquireAsync(0, default);
        await oldArchive.Entered.Task;
        await using var current = new ReaderSession(new FakeArchive(60), 100);
        using var currentLease = await current.AcquireAsync(0, default);
        await old.DisposeAsync();
        await Throws<OperationCanceledException>(async () => await oldRead);
        Require(current.MemoryUsage == (60, 0) && current.IsCached(0), "New session was changed by old completion");
        Require(oldArchive.Disposed && !oldArchive.DisposedDuringRead, "Archive closed during active read");
    }

    private static async Task ProtectedCache()
    {
        await using var session = new ReaderSession(new FakeArchive(60, 60), 100);
        (await session.AcquireAsync(0, default)).Dispose();
        session.Protect([0]);
        await Throws<CacheBusyException>(async () => (await session.AcquireAsync(1, default, foreground: false)).Dispose());
        Require(session.IsCached(0), "Prefetch evicted the viewport");
        (await session.AcquireAsync(1, default)).Dispose();
        Require(session.IsCached(1) && !session.IsCached(0), "Foreground request could not reclaim unpinned media");
    }

    private static async Task QueuedShutdown()
    {
        using var never = new ManualResetEventSlim();
        var archive = new FakeArchive(Enumerable.Repeat(10, 20).ToArray()) { BeforeRead = ct => never.Wait(ct) };
        var session = new ReaderSession(archive, 100);
        var tasks = Enumerable.Range(0, 20).Select(i => session.AcquireAsync(i, default)).ToArray();
        await archive.Entered.Task;
        await session.DisposeAsync();
        foreach (var task in tasks) await Throws<OperationCanceledException>(async () => await task);
        Require(archive.Disposed && !archive.DisposedDuringRead && session.MemoryUsage == (0, 0), "Queued work survived shutdown");
    }

    private static async Task Passwords()
    {
        var path = Path.Combine(_root, "encrypted.zip");
        EncryptedZipFixture.Write(path, "correct", _png);
        try
        {
            using var wrong = await ArchiveSession.OpenAsync(path, "wrong", default);
            throw new InvalidOperationException("Wrong password was accepted");
        }
        catch (Exception ex) when (ArchiveSession.IsPasswordError(ex)) { }
        var result = await ArchiveSession.TryKnownAsync(path, ["wrong", "correct", "another"], default);
        Require(result?.Password == "correct", "Known-password retry failed");
        await using var session = new ReaderSession(result!.Session);
        using var lease = await session.AcquireAsync(0, default);
        Require(lease.Content.Data.AsSpan().SequenceEqual(_png), "Decrypted data changed");
        Require(await ArchiveSession.TryKnownAsync(path, ["wrong", "another"], default) is null, "All-wrong passwords should fall back to prompt");
    }

    private static async Task Shutdown()
    {
        var archive = new FakeArchive(60);
        var session = new ReaderSession(archive, 100);
        var lease = await session.AcquireAsync(0, default);
        await session.DisposeAsync();
        Require(session.MemoryUsage.Cached == 60, "Active lease stopped being accounted");
        lease.Dispose();
        Require(session.MemoryUsage == (0, 0), "Released lease was retained after shutdown");
        await session.DisposeAsync();
        await Throws<ObjectDisposedException>(async () => await session.AcquireAsync(0, default));
    }

    private static async Task Failure()
    {
        var archive = new FakeArchive(1) { BeforeRead = _ => throw new InvalidDataException("fixture") };
        await using var session = new ReaderSession(archive, 100);
        for (var i = 0; i < 3; i++) await Throws<InvalidDataException>(async () => await session.AcquireAsync(0, default));
        Require(archive.Reads == 1 && session.MemoryUsage == (0, 0), "Failure was retried or leaked a reservation");
    }

    private static async Task ZipRead()
    {
        var path = Path.Combine(_root, "duplicates.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[] { ("2.png", _png), ("1.png", new byte[] { 1, 2, 3 }), ("1.png", new byte[] { 4, 5 }) })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }
        await using var session = new ReaderSession(ArchiveSession.Open(path, null));
        Require(session.Entries.Count == 3 && session.Entries[2].Key == "2.png", "Directory ordering failed");
        using var a = await session.AcquireAsync(0, default);
        using var b = await session.AcquireAsync(1, default);
        Require(a.Content.Data.Count == 3 && b.Content.Data.Count == 2, "Duplicate entry names collided");
    }

    private static async Task Decode()
    {
        var content = new MediaContent(new(_png), ImageDecoder.ReadAspectRatio(new(_png)));
        var image = await ImageDecoder.DecodeAsync(content, 1000, default, 16 * 1024);
        Require(image.IsFrozen, "Cross-thread bitmap was not frozen");
        Require(image.PixelWidth * (long)image.PixelHeight * 4 <= 16 * 1024 + 1024, "Pixel budget exceeded");
        Require(Math.Abs(content.AspectRatio!.Value - 2) < 0.001, "Wrong metadata ratio");
    }

    private static async Task Images()
    {
        var archive = new FakeArchive(_png.Length, 3) { Type = ComicMediaType.Image, Data = index => index == 0 ? _png : [1, 2, 3] };
        await using var session = new ReaderSession(archive);
        using var thumbnails = new VideoThumbnailService();
        var pages = session.Entries.Select((entry, i) => new ComicPage(i, entry)).ToArray();
        await using var images = new PageImageLoader(session, pages, thumbnails);
        var errors = 0;
        images.Updated += (_, error) => { if (error is not null) errors++; };
        images.Refresh([0, 1], 512);
        await Until(() => pages[0].DisplayImage is not null && errors == 1);
        var oldImage = pages[0].DisplayImage;
        images.Refresh([0, 1], 640);
        Require(ReferenceEquals(oldImage, pages[0].DisplayImage), "Resize blanked the old image");
        await Until(() => !ReferenceEquals(oldImage, pages[0].DisplayImage));
        images.Refresh([], 640);
        Require(pages.All(page => page.DisplayImage is null), "Off-screen image was retained");
        images.Refresh([1], 640);
        await Task.Delay(100);
        Require(errors == 1, "Corrupt image was retried");
    }

    private static Task Ordering()
    {
        var small = PageImageLoader.ReadingOrder(200, 500).ToArray();
        var huge = PageImageLoader.ReadingOrder(200, 5_000_000).ToArray();
        Require(small.SequenceEqual(huge) && huge.Length == 17 && huge.Distinct().Count() == 17, "Prefetch grows with archive size");
        var names = new[] { "10.jpg", "02.jpg", "1.jpg", "2.jpg" };
        Array.Sort(names, NaturalFileNameComparer.Instance);
        Require(names.SequenceEqual(new[] { "1.jpg", "2.jpg", "02.jpg", "10.jpg" }), "Natural ordering changed");
        return Task.CompletedTask;
    }

    private static async Task WindowSmoke()
    {
        var path = Path.Combine(_root, "pages.zip");
        var jpeg = MakeImage(new JpegBitmapEncoder(), 1600, 2400);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (var i = 0; i < 30; i++)
            {
                var isJpeg = i % 2 == 1;
                using var stream = zip.CreateEntry($"{i}.{(isJpeg ? "jpg" : "png")}").Open();
                stream.Write(isJpeg ? jpeg : _png);
            }
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 700, Height = 800 };
        window.Show();
        try
        {
            await InvokeTask(window, "LoadArchiveWithPasswordRetryAsync", path);
            await Until(() => window.Pages.Count == 30 && window.Pages[0].DisplayImage is not null);
            Require(window.PageWidth > 100, "Viewport width not resolved");
            await AssertPageRendered(window, 0);
            Invoke(window, "ScrollPageToTop", 1);
            await Until(() => window.Pages[1].DisplayImage is not null);
            await AssertPageRendered(window, 1);
            Invoke(window, "ScrollPageToTop", 20);
            await Until(() => window.Pages[20].DisplayImage is not null);
            await AssertPageRendered(window, 20);
            foreach (var index in new[] { 5, 18, 4, 27 }) Invoke(window, "ScrollPageToTop", index);
            await Until(() => window.Pages[27].DisplayImage is not null);
            await AssertPageRendered(window, 27);
            window.Width = 1000;
            await Task.Delay(100);
            await InvokeTask(window, "LoadArchiveWithPasswordRetryAsync", path);
            await Until(() => window.Pages[0].DisplayImage is not null);
            await AssertPageRendered(window, 0);
            await InvokeTask(window, "ResetReaderAsync");
            Require(window.Pages.Count == 0, "Reader reset failed");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task;
        }
    }

    private static async Task Thumbnail(byte[] video)
    {
        using var service = new VideoThumbnailService();
        var bitmap = await service.GenerateAsync(new(video), default);
        Require(bitmap is { IsFrozen: true, PixelWidth: > 0 }, "No native thumbnail");
        var repeated = await Task.WhenAll(service.GenerateAsync(new(video), default), service.GenerateAsync(new(video), default));
        Require(repeated.All(image => image is { IsFrozen: true, PixelWidth: > 0 }), "Queued thumbnail requests failed");
        var source = (ArraySegment<byte>)typeof(VideoThumbnailService).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Require(source.Array is null, "Thumbnail session retained source bytes");
    }

    private static async Task EntryLength()
    {
        // A RAR decryptor can return the entire last AES block, including padding.
        for (var size = 1; size <= 16; size++)
        {
            var bytes = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            using var source = new MemoryStream(bytes);
            var result = ArchiveSession.ReadEntryData(source, size, default, size);
            Require(result.Count == size && result.AsSpan().SequenceEqual(bytes.AsSpan(0, size)), "Padding entered the cached payload");
            Require(result.Array!.Length == size, "Padding exceeded the reserved capacity");
        }
        using var truncated = new MemoryStream(new byte[15]);
        await Throws<EndOfStreamException>(() => Task.FromResult(ArchiveSession.ReadEntryData(truncated, 16, default, 16)));
        using var oversized = new MemoryStream(new byte[17]);
        await Throws<MediaTooLargeException>(() => Task.FromResult(ArchiveSession.ReadEntryData(oversized, 17, default, 16)));
    }

    private static async Task ExternalArchive(string path)
    {
        ArchiveSession archive;
        try { archive = ArchiveSession.Open(path, null); }
        catch (Exception ex) when (ArchiveSession.IsPasswordError(ex))
        {
            var result = await ArchiveSession.TryKnownAsync(path, new PasswordStore().Except(null), default);
            archive = result?.Session ?? throw new InvalidOperationException("Archive password is not in the application's saved history.");
        }
        await using var session = new ReaderSession(archive);
        Console.WriteLine($"Archive entries: {session.Entries.Count}");
        foreach (var index in Enumerable.Range(0, session.Entries.Count))
        {
            Console.WriteLine($"Reading entry {index}: size={session.Entries[index].Size}, type={session.Entries[index].Type}");
            using var lease = await session.AcquireAsync(index, default);
            Require(lease.Content.Data.Count == session.Entries[index].Size, "Payload length differs from archive entry");
            if (session.Entries[index].Type != ComicMediaType.Image) continue;
            var image = await ImageDecoder.DecodeAsync(lease.Content, 768, default);
            Require(image.PixelWidth > 0 && image.PixelHeight > 0, "Empty decoded image");
            Console.WriteLine($"Decoded entry {index}: {image.PixelWidth} x {image.PixelHeight}");
        }
        await session.DisposeAsync();
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 700, Height = 800 };
        window.Show();
        try
        {
            await InvokeTask(window, "LoadArchiveWithPasswordRetryAsync", path);
            foreach (var index in new[] { 0, 1, window.Pages.Count / 2, window.Pages.Count - 1 }.Distinct())
            {
                Invoke(window, "ScrollPageToTop", index);
                await Until(() => window.Pages[index].DisplayImage is not null);
                await AssertPageRendered(window, index, expectRed: false);
            }
            Console.WriteLine("External archive first, second, middle and last pages rendered successfully");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task;
        }
    }

    private static async Task AssertPageRendered(MainWindow window, int index, bool expectRed = true)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var list = (ListBox)window.FindName("PagesListBox");
        var container = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        Require(container is not null, $"Page {index} has no realized container");
        var image = VisualChildren<Image>(container!).Single();
        Require(ReferenceEquals(image.Source, window.Pages[index].DisplayImage), $"Page {index} image binding did not update");
        Require(image.ActualWidth > 100 && image.ActualHeight > 100,
            $"Page {index} image has invalid bounds: {image.ActualWidth} x {image.ActualHeight}");
        var bitmap = new RenderTargetBitmap((int)list.ActualWidth, (int)list.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(list);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Require(pixel[3] > 200 && (!expectRed || pixel[2] > 200 && pixel[1] < 50 && pixel[0] < 50),
            $"Page {index} is blank or obscured: BGRA={string.Join(',', pixel)}");
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private static async Task Playback(byte[] video)
    {
        var player = new MpvVideoPlayerControl();
        var window = new Window { Content = player, Width = 320, Height = 240, ShowActivated = false, ShowInTaskbar = false,
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        var errors = 0;
        player.PlaybackError += () => errors++;
        try
        {
            await player.PlayAsync(new(video), default);
            await Until(() => player.IsPlaying || errors > 0);
            Require(errors == 0 && player.IsPlaying, "Native playback did not start");
            player.Pause();
            Require(player.IsPaused, "Pause state failed");
            await Task.Delay(80);
            player.Resume();
            await Until(() => player.IsPlaying);
            await player.StopAsync();
            Require(!player.IsPlaying && !player.IsPreparing, "Stop state failed");
            await player.PlayAsync(new(video), default);
            await Until(() => player.IsPlaying || errors > 0);
            Require(errors == 0 && player.IsPlaying, "Replacement failed");
            await player.StopAsync();
        }
        finally { await player.DisposeAsync(); window.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Condition not reached");
            await Task.Delay(15);
        }
    }

    private static async Task MixedArchive(byte[] video)
    {
        var path = Path.Combine(_root, "mixed.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[] { ("0.png", _png), ("1.mp4", video), ("2.png", _png) })
            { using var stream = zip.CreateEntry(name).Open(); stream.Write(data); }
        }
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 700, Height = 800 };
        window.Show();
        try
        {
            await InvokeTask(window, "LoadArchiveWithPasswordRetryAsync", path);
            Invoke(window, "ScrollPageToTop", 1);
            var page = window.Pages[1];
            await Until(() => page.DisplayImage is not null);
            var controller = (PlaybackController)typeof(MainWindow).GetField("_playback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var reader = (ReaderSession)typeof(MainWindow).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var errors = 0;
            controller.Error += _ => errors++;
            await controller.PlayAsync(reader, page);
            await Until(() => page.IsVideoPlaying || errors > 0);
            Require(errors == 0 && page.IsVideoPlaying, "Shared player did not attach to page");
            controller.Pause();
            Require(page.IsVideoPaused, "Page did not reflect pause state");
            await controller.PlayAsync(reader, page);
            await InvokeTask(window, "LoadArchiveWithPasswordRetryAsync", path);
            Require(page.PlaybackElement is null && reader.MemoryUsage == (0, 0), "Switch retained the old player or source lease");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task;
        }
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static Task InvokeTask(object instance, string method, params object[] args) => (Task)Invoke(instance, method, args)!;

    private static byte[] MakePng() => MakeImage(new PngBitmapEncoder(), 64, 128);

    private static byte[] MakeImage(BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class FakeArchive(params int[] sizes) : IMediaArchive
    {
        public Action<CancellationToken>? BeforeRead { get; init; }
        public Func<int, byte[]>? Data { get; init; }
        public ComicMediaType Type { get; init; } = ComicMediaType.Video;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
        private int _active;
        public bool Disposed;
        public bool DisposedDuringRead;
        public IReadOnlyList<ComicArchiveEntry> MediaEntries => sizes.Select((size, i) => new ComicArchiveEntry($"{i}.media", Type, size)).ToArray();
        public ArraySegment<byte> ReadEntry(int index, CancellationToken token, long maxBytes)
        {
            Interlocked.Increment(ref Reads);
            Interlocked.Increment(ref _active);
            Entered.TrySetResult();
            try { BeforeRead?.Invoke(token); token.ThrowIfCancellationRequested(); return new(Data?.Invoke(index) ?? new byte[sizes[index]]); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { DisposedDuringRead = _active != 0; Disposed = true; }
    }
}
