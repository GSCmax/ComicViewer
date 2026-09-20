namespace ComicViewer;

internal interface IMediaArchive : IDisposable
{
    IReadOnlyList<ComicArchiveEntry> MediaEntries { get; }
    ArraySegment<byte> ReadEntry(int index, CancellationToken cancellationToken, long maxBytes);
}

internal sealed record MediaContent(ArraySegment<byte> Data, double? AspectRatio);

internal sealed class MediaLease(MediaContent content, Action release) : IDisposable
{
    private Action? _release = release;
    public MediaContent Content { get; } = content;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed class CacheBusyException() : Exception("内存缓存正在被使用，请稍后重试。");
internal sealed class MediaTooLargeException() : Exception("文件超过 1 GiB 内存缓存上限。");

/// <summary>Owns one archive, shared reads and encoded buffers. Leases pin active media.</summary>
internal sealed class ReaderSession : IAsyncDisposable
{
    public const long DefaultCacheLimit = 1024L * 1024 * 1024;
    private readonly IMediaArchive _archive;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, CacheEntry> _cache = [];
    private readonly Dictionary<int, PendingRead> _reads = [];
    private readonly Dictionary<int, Exception> _failures = [];
    private readonly LinkedList<int> _lru = [];
    private readonly SortedSet<int> _cachedIndexes = [];
    private HashSet<int> _protectedIndexes = [];
    private long _cacheBytes;
    private long _reservedBytes;
    private bool _disposed;
    private Task? _disposeTask;

    public ReaderSession(IMediaArchive archive, long cacheLimit = DefaultCacheLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheLimit);
        _archive = archive;
        CacheLimit = cacheLimit;
    }

    public IReadOnlyList<ComicArchiveEntry> Entries => _archive.MediaEntries;
    public long CacheLimit { get; }
    public (long Cached, long Reserved) MemoryUsage { get { lock (_sync) return (_cacheBytes, _reservedBytes); } }
    public string LoadedRange { get { lock (_sync) return _cachedIndexes.Count switch
    {
        0 => "0",
        1 => (_cachedIndexes.Min + 1).ToString(),
        _ => $"{_cachedIndexes.Min + 1}-{_cachedIndexes.Max + 1}"
    }; } }
    public bool IsCached(int index) { lock (_sync) return _cache.ContainsKey(index); }
    public Exception? GetFailure(int index) { lock (_sync) return _failures.GetValueOrDefault(index); }
    public void Protect(IEnumerable<int> indexes) { lock (_sync) _protectedIndexes = indexes.ToHashSet(); }

    public async Task<MediaLease> AcquireAsync(int index, CancellationToken cancellationToken, bool foreground = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingRead pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(index, out var cached)) return Pin(cached);
            if (_failures.TryGetValue(index, out var failure)) throw failure;
            if (!_reads.TryGetValue(index, out pending!))
            {
                pending = new PendingRead();
                _reads.Add(index, pending);
                pending.Task = Task.Run(() => ReadAsync(index, pending));
                _ = pending.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            pending.Users++;
            pending.Foreground |= foreground;
        }
        try
        {
            var entry = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return Pin(entry);
            }
        }
        finally
        {
            lock (_sync)
            {
                pending.Users--;
                FinishRead(index, pending);
            }
        }
    }

    private async Task<CacheEntry> ReadAsync(int index, PendingRead pending)
    {
        var token = _lifetime.Token;
        var entered = false;
        try
        {
            await _readGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var metadata = Entries[index];
            var required = metadata.Size > 0 ? metadata.Size : CacheLimit;
            lock (_sync)
            {
                if (required > CacheLimit) throw new MediaTooLargeException();
                MakeRoom(required, pending.Foreground);
                _reservedBytes = required;
            }
            var data = _archive.ReadEntry(index, token, required);
            var content = new MediaContent(data, metadata.Type == ComicMediaType.Image ? ImageDecoder.ReadAspectRatio(data) : null);
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                // Count allocated capacity, not just the populated segment.
                var bytes = data.Array?.LongLength ?? 0;
                if (bytes > required) throw new InvalidOperationException("归档读取超出预留内存。");
                var entry = new CacheEntry(index, content, bytes, _lru.AddLast(index));
                _cache.Add(index, entry);
                _cachedIndexes.Add(index);
                _reservedBytes = 0;
                _cacheBytes += bytes;
                pending.Entry = entry; // A pin protects the callers of this shared read.
                return entry;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CacheBusyException)
        {
            lock (_sync) _failures[index] = ex;
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (entered) _reservedBytes = 0;
                if (entered) _readGate.Release();
                pending.Completed = true;
                FinishRead(index, pending);
            }
        }
    }

    private void MakeRoom(long bytes, bool foreground)
    {
        bool CanEvict(CacheEntry entry) => entry.Pins == 0 && (foreground || !_protectedIndexes.Contains(entry.Index));
        if (_cacheBytes + bytes > CacheLimit && _cache.Values.Where(CanEvict).Sum(entry => entry.Bytes) < _cacheBytes + bytes - CacheLimit)
            throw new CacheBusyException();
        for (var node = _lru.First; _cacheBytes + bytes > CacheLimit && node is not null;)
        {
            var next = node.Next;
            var entry = _cache[node.Value];
            if (CanEvict(entry)) Remove(entry);
            node = next;
        }
        if (_cacheBytes + bytes > CacheLimit) throw new CacheBusyException();
    }

    private MediaLease Pin(CacheEntry entry)
    {
        entry.Pins++;
        _lru.Remove(entry.Node);
        _lru.AddLast(entry.Node);
        return new MediaLease(entry.Content, () => { lock (_sync) Unpin(entry); });
    }

    private void Unpin(CacheEntry entry)
    {
        entry.Pins--;
        if (_disposed && entry.Pins == 0) Remove(entry);
    }

    private void FinishRead(int index, PendingRead pending)
    {
        if (!pending.Completed || pending.Users != 0) return;
        _reads.Remove(index);
        if (pending.Entry is { } entry) Unpin(entry);
    }

    private void Remove(CacheEntry entry)
    {
        _cache.Remove(entry.Index);
        _cachedIndexes.Remove(entry.Index);
        _lru.Remove(entry.Node);
        _cacheBytes -= entry.Bytes;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            _disposeTask = DisposeCoreAsync(_reads.Values.Select(read => read.Task).ToArray());
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] reads)
    {
        try { await Task.WhenAll(reads).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        lock (_sync)
        {
            foreach (var entry in _cache.Values.Where(entry => entry.Pins == 0).ToArray()) Remove(entry);
            _failures.Clear();
        }
        _archive.Dispose();
        _readGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed class PendingRead
    {
        public Task<CacheEntry> Task = null!;
        public int Users;
        public bool Completed;
        public bool Foreground;
        public CacheEntry? Entry;
    }

    private sealed class CacheEntry(int index, MediaContent content, long bytes, LinkedListNode<int> node)
    {
        public int Index { get; } = index;
        public MediaContent Content { get; } = content;
        public long Bytes { get; } = bytes;
        public LinkedListNode<int> Node { get; } = node;
        public int Pins = 1;
    }
}
