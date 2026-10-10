using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services;

public sealed class LibraryOperationManager(
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryOperationManager> logger) : BackgroundService
{
    public const string ScanOperation = "scan";
    public const string MetadataRefreshOperation = "metadata-refresh";

    private readonly Channel<LibraryOperationJob> queue = Channel.CreateUnbounded<LibraryOperationJob>();
    private readonly object sync = new();
    private readonly Dictionary<Guid, LibraryOperationStatus> history = [];
    private readonly Queue<Guid> historyOrder = new();
    private LibraryOperationStatus? current;

    public bool TryStart(
        int rootId,
        string rootName,
        string operationType,
        out LibraryOperationStatus status)
    {
        lock (sync)
        {
            if (current is { State: "queued" or "running" })
            {
                status = current;
                return false;
            }

            status = new LibraryOperationStatus(
                Guid.NewGuid(),
                rootId,
                rootName,
                operationType,
                "queued",
                operationType == ScanOperation ? "Scan queued." : "Metadata refresh queued.",
                0,
                0,
                DateTimeOffset.UtcNow,
                null);
            current = status;
            history[status.Id] = status;
            historyOrder.Enqueue(status.Id);
            if (!queue.Writer.TryWrite(new LibraryOperationJob(status.Id, rootId, operationType)))
            {
                current = null;
                history.Remove(status.Id);
                historyOrder.Dequeue();
                throw new InvalidOperationException("The library operation queue is unavailable.");
            }

            while (historyOrder.Count > 50)
            {
                var oldestId = historyOrder.Dequeue();
                if (oldestId != current.Id)
                {
                    history.Remove(oldestId);
                }
            }

            return true;
        }
    }

    public LibraryOperationStatus? Get(Guid operationId)
    {
        lock (sync)
        {
            return history.GetValueOrDefault(operationId);
        }
    }

    public LibraryOperationStatus? GetActive()
    {
        lock (sync)
        {
            return current is { State: "queued" or "running" } ? current : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ExecuteOperationAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteOperationAsync(LibraryOperationJob job, CancellationToken cancellationToken)
    {
        Update(job.Id, status => status with
        {
            State = "running",
            Message = job.OperationType == ScanOperation ? "Scanning folder…" : "Preparing metadata refresh…",
        });

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var library = scope.ServiceProvider.GetRequiredService<IMusicLibraryService>();
            if (job.OperationType == ScanOperation)
            {
                var result = await library.ScanAsync(job.RootId, cancellationToken);
                Complete(
                    job.Id,
                    "completed",
                    $"Scanned {result.DiscoveredTracks} tracks; {result.UnreadableTracks} files could not be read.");
                return;
            }

            var metadataResult = await library.RefreshRootMetadataAsync(
                job.RootId,
                (completed, total, message) => Update(job.Id, status => status with
                {
                    CompletedUnits = completed,
                    TotalUnits = total,
                    Message = $"{message} ({completed}/{total})",
                }),
                cancellationToken);
            Complete(
                job.Id,
                "completed",
                $"Refreshed {metadataResult.ArtistsProcessed} artists and {metadataResult.AlbumsProcessed} albums. "
                    + $"Updated {metadataResult.ArtistImagesUpdated} artist images and "
                    + $"{metadataResult.AlbumImagesUpdated} album images.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(job.Id, "failed", "The operation was stopped because the API is shutting down.");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Library operation {OperationId} ({OperationType}) failed for source root {SourceRootId}.",
                job.Id,
                job.OperationType,
                job.RootId);
            Complete(job.Id, "failed", "The operation failed. Check that the folder is accessible and review the API logs.");
        }
    }

    private void Complete(Guid operationId, string state, string message) =>
        Update(operationId, status => status with
        {
            State = state,
            Message = message,
            FinishedAt = DateTimeOffset.UtcNow,
        });

    private void Update(Guid operationId, Func<LibraryOperationStatus, LibraryOperationStatus> update)
    {
        lock (sync)
        {
            if (current?.Id == operationId)
            {
                current = update(current);
                history[operationId] = current;
            }
        }
    }

    private sealed record LibraryOperationJob(Guid Id, int RootId, string OperationType);
}
