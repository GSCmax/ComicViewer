using SharpCompress.Archives;
using SharpCompress.Readers;
using System.IO;

namespace ComicViewer;

internal sealed class ArchiveSession : IDisposable
{
    private readonly IArchive _archive;
    private readonly List<IArchiveEntry> _entries;
    private readonly SemaphoreSlim _archiveLock = new(1, 1);
    private bool _isDisposed;

    private ArchiveSession(IArchive archive, List<IArchiveEntry> entries, IReadOnlyList<ComicArchiveEntry> mediaEntries)
    {
        _archive = archive;
        _entries = entries;
        MediaEntries = mediaEntries;
    }

    public IReadOnlyList<ComicArchiveEntry> MediaEntries { get; }

    public static ArchiveSession Open(string archivePath, string? password)
    {
        var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions
        {
            Password = password
        });

        try
        {
            var entries = archive.Entries
                .Where(entry => !entry.IsDirectory && entry.Key is not null)
                .ToList();
            var mediaEntries = entries
                .Where(entry => MediaFileClassifier.IsSupported(entry.Key))
                .OrderBy(entry => entry.Key, NaturalFileNameComparer.Instance)
                .Select(entry => new ComicArchiveEntry(entry.Key!, MediaFileClassifier.GetMediaType(entry.Key!), entry.Size))
                .ToList();

            var firstEntry = mediaEntries.FirstOrDefault();
            if (firstEntry is not null)
            {
                using var stream = entries.First(entry => string.Equals(entry.Key, firstEntry.Key, StringComparison.Ordinal)).OpenEntryStream();
                _ = stream.ReadByte();
            }

            return new ArchiveSession(archive, entries, mediaEntries);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public MemoryStream CopyEntryToMemory(string entryKey, CancellationToken cancellationToken)
    {
        _archiveLock.Wait(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var entry = _entries.FirstOrDefault(entry => string.Equals(entry.Key, entryKey, StringComparison.Ordinal))
                ?? throw new FileNotFoundException("Entry not found in archive.", entryKey);

            using var source = entry.OpenEntryStream();
            var destination = entry.Size is > 0 and <= int.MaxValue
                ? new MemoryStream(checked((int)entry.Size))
                : new MemoryStream();
            CopyToMemory(source, destination, cancellationToken);
            destination.Position = 0;
            return destination;
        }
        finally
        {
            _archiveLock.Release();
        }
    }

    private static void CopyToMemory(Stream source, MemoryStream destination, CancellationToken cancellationToken)
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

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ArchiveSession));
        }
    }

    public void Dispose()
    {
        _archiveLock.Wait();
        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _archive.Dispose();
        }
        finally
        {
            _archiveLock.Release();
            _archiveLock.Dispose();
        }
    }
}
