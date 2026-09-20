using SharpCompress.Archives;
using SharpCompress.Readers;
using System.IO;

namespace ComicViewer;

internal sealed class ArchiveSession : IMediaArchive
{
    private readonly IArchive _archive;
    private readonly IArchiveEntry[] _entries;

    private ArchiveSession(IArchive archive, IArchiveEntry[] entries)
    {
        _archive = archive;
        _entries = entries;
        MediaEntries = entries.Select(entry => new ComicArchiveEntry(entry.Key!, MediaFileClassifier.GetMediaType(entry.Key!), entry.Size)).ToArray();
    }

    public IReadOnlyList<ComicArchiveEntry> MediaEntries { get; }

    public static ArchiveSession Open(string archivePath, string? password)
    {
        var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = password });
        try
        {
            // Indexes also distinguish entries with duplicate names.
            var entries = archive.Entries.Where(entry => !entry.IsDirectory && MediaFileClassifier.IsSupported(entry.Key))
                .OrderBy(entry => entry.Key, NaturalFileNameComparer.Instance).ToArray();
            if (entries.Length > 0)
            {
                using var stream = entries[0].OpenEntryStream();
                _ = stream.ReadByte();
            }
            return new ArchiveSession(archive, entries);
        }
        catch { archive.Dispose(); throw; }
    }

    // ReaderSession serializes reads and waits for them before disposal.
    public ArraySegment<byte> ReadEntry(int index, CancellationToken cancellationToken, long maxBytes)
    {
        var entry = _entries[index];
        if (entry.Size > maxBytes) throw new MediaTooLargeException();
        using var source = entry.OpenEntryStream();
        return ReadEntryData(source, entry.Size, cancellationToken, maxBytes);
    }

    internal static ArraySegment<byte> ReadEntryData(Stream source, long size, CancellationToken token, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size > maxBytes) throw new MediaTooLargeException();
        var data = new byte[checked((int)size)];
        // ZIP/RAR indexes provide the payload length. Read directly into its final
        // buffer, excluding encrypted RAR padding and detecting truncated entries.
        for (var offset = 0; offset < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(128 * 1024, data.Length - offset);
            source.ReadExactly(data, offset, count);
            offset += count;
        }
        return new(data);
    }

    public void Dispose() => _archive.Dispose();

    public static async Task<ArchiveSession> OpenAsync(string path, string? password, CancellationToken token)
    {
        var archive = await Task.Run(() => Open(path, password), token).ConfigureAwait(false);
        if (!token.IsCancellationRequested) return archive;
        archive.Dispose();
        token.ThrowIfCancellationRequested();
        return archive;
    }

    public static bool IsPasswordError(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crypt", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<KnownPasswordResult?> TryKnownAsync(string path, IReadOnlyList<string> passwords, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        KnownPasswordResult? result = null;
        Exception? failure = null;
        try
        {
            await Parallel.ForEachAsync(passwords, new ParallelOptions
            {
                CancellationToken = stop.Token,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
            }, async (password, ct) =>
            {
                try
                {
                    var session = await OpenAsync(path, password, ct);
                    var candidate = new KnownPasswordResult(password, session);
                    if (Interlocked.CompareExchange(ref result, candidate, null) is null) stop.Cancel();
                    else session.Dispose();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) when (IsPasswordError(ex)) { }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                }
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (token.IsCancellationRequested)
        {
            result?.Session.Dispose();
            token.ThrowIfCancellationRequested();
        }
        if (result is null && failure is not null) throw failure;
        return result;
    }

    internal sealed record KnownPasswordResult(string Password, ArchiveSession Session);
}
