using System.Windows.Media.Imaging;

namespace ComicViewer;

/// <summary>UI-thread coordinator. Keeps decoded images only around the viewport.</summary>
internal sealed class PageImageLoader(ReaderSession session, IReadOnlyList<ComicPage> pages, VideoThumbnailService thumbnails) : IAsyncDisposable
{
    private readonly Dictionary<int, Job> _jobs = [];
    private readonly Dictionary<int, int> _decodedWidths = [];
    private readonly HashSet<int> _failed = [];
    private readonly List<Task> _tasks = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _prefetch = Task.CompletedTask;
    private int[] _prefetchTargets = [];
    private bool _disposed;
    public event Action<int, Exception?>? Updated;

    public void Refresh(IEnumerable<int> priorities, int decodeWidth)
    {
        if (_disposed) return;
        var indexes = priorities.Distinct().ToArray();
        var wanted = indexes.ToHashSet();
        session.Protect(indexes);
        foreach (var index in _decodedWidths.Keys.Where(index => !wanted.Contains(index)).ToArray())
        {
            pages[index].SetDisplayImage(null);
            _decodedWidths.Remove(index);
        }
        foreach (var pair in _jobs.ToArray())
        {
            if (!wanted.Contains(pair.Key) || (!pages[pair.Key].IsVideo && pair.Value.Width != decodeWidth))
            {
                pair.Value.Cancellation.Cancel();
                _jobs.Remove(pair.Key);
            }
        }
        _tasks.RemoveAll(task => task.IsCompleted);
        foreach (var index in indexes)
        {
            if (_failed.Contains(index) || _jobs.ContainsKey(index)) continue;
            if (_decodedWidths.TryGetValue(index, out var existing) && (pages[index].IsVideo || existing >= decodeWidth)) continue;
            var job = new Job(decodeWidth);
            _jobs.Add(index, job);
            _tasks.Add(LoadAsync(index, job, ImageDecoder.MaxDecodedImageBytes / Math.Max(1, indexes.Length)));
        }
        var targets = indexes.Length == 0 ? [] : indexes.Concat(ReadingOrder(indexes[0], pages.Count)).Distinct().ToArray();
        if (!_prefetchTargets.SequenceEqual(targets))
        {
            _prefetchTargets = targets;
            if (_prefetch.IsCompleted) _prefetch = PrefetchAsync();
        }
    }

    private async Task PrefetchAsync()
    {
        // Defer until Refresh has installed the task, including for cache hits.
        await Task.Yield();
        int[] targets;
        do
        {
            targets = _prefetchTargets;
            foreach (var index in targets)
            {
                if (_disposed || !ReferenceEquals(targets, _prefetchTargets)) break;
                if (session.IsCached(index) || session.GetFailure(index) is not null) continue;
                try
                {
                    using var lease = await session.AcquireAsync(index, _lifetime.Token, foreground: false);
                    if (lease.Content.AspectRatio is { } ratio) pages[index].SetAspectRatio(ratio);
                    Updated?.Invoke(index, null);
                }
                catch (CacheBusyException) { break; }
                catch (OperationCanceledException) when (_disposed) { return; }
                catch (Exception ex) { Updated?.Invoke(index, ex); }
            }
        } while (!_disposed && !ReferenceEquals(targets, _prefetchTargets));
    }

    internal static IEnumerable<int> ReadingOrder(int current, int count)
    {
        if (count == 0) yield break;
        current = Math.Clamp(current, 0, count - 1);
        yield return current;
        for (var distance = 1; distance <= 12; distance++)
        {
            if (distance % 2 == 0 && distance <= 8 && current - distance / 2 >= 0) yield return current - distance / 2;
            if (current + distance < count) yield return current + distance;
        }
    }

    private async Task LoadAsync(int index, Job job, long pixelBudget)
    {
        var token = job.Cancellation.Token;
        var page = pages[index];
        try
        {
            if (page.IsVideo) page.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
            using var lease = await session.AcquireAsync(index, token);
            BitmapSource image;
            if (page.IsVideo)
                image = await thumbnails.GenerateAsync(lease.Content.Data, token)
                    ?? throw new InvalidOperationException("无法生成视频封面。");
            else
                image = await ImageDecoder.DecodeAsync(lease.Content, job.Width, token, pixelBudget);
            token.ThrowIfCancellationRequested();
            if (_disposed || !_jobs.TryGetValue(index, out var current) || !ReferenceEquals(current, job)) return;
            page.SetDisplayImage(image);
            page.SetAspectRatio(lease.Content.AspectRatio ?? (double)image.PixelHeight / image.PixelWidth);
            if (page.IsVideo) page.SetCoverLoadStatus(VideoCoverLoadStatus.Success);
            _decodedWidths[index] = job.Width;
            Updated?.Invoke(index, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (CacheBusyException) { } // A later viewport or playback update can retry.
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested)
            {
                _failed.Add(index);
                if (page.IsVideo) page.SetCoverLoadStatus(ex is MediaTooLargeException ? VideoCoverLoadStatus.Oversized : VideoCoverLoadStatus.Failed);
                Updated?.Invoke(index, ex);
            }
        }
        finally
        {
            if (_jobs.TryGetValue(index, out var current) && ReferenceEquals(current, job)) _jobs.Remove(index);
            job.Cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        await Task.WhenAll(_tasks.Append(_prefetch));
        foreach (var index in _decodedWidths.Keys) pages[index].SetDisplayImage(null);
        _decodedWidths.Clear();
        _jobs.Clear();
        _lifetime.Dispose();
    }

    private sealed class Job(int width)
    {
        public int Width { get; } = width;
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
