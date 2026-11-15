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
    public const string ImportScanMetadataOperation = "import-scan-metadata";
    public const string DatabaseCleanupOperation = "database-cleanup";

    private readonly Channel<LibraryOperationJob> queue = Channel.CreateUnbounded<LibraryOperationJob>();
    private readonly object sync = new();
    private readonly Dictionary<Guid, LibraryOperationStatus> history = [];
    private readonly Queue<Guid> historyOrder = new();
    private readonly HashSet<int> rootsBeingRemoved = [];
    private LibraryOperationStatus? current;
    private bool importInProgress;

    public LibraryOperationStatus? TryStart(
        int rootId,
        string rootName,
        string operationType)
    {
        lock (sync)
        {
            if (current is { State: "queued" or "running" } || rootsBeingRemoved.Count > 0 || importInProgress)
            {
                return null;
            }

            return StartOperation(rootId, rootName, operationType, sourceRetained: false, relativePath: null);
        }
    }

    public bool TryBeginLibraryMutation()
    {
        lock (sync)
        {
            if (current is { State: "queued" or "running" }
                || rootsBeingRemoved.Count > 0
                || importInProgress)
            {
                return false;
            }

            importInProgress = true;
            return true;
        }
    }

    public LibraryOperationStatus? StartReservedImportOperation(
        int rootId,
        string rootName,
        string relativePath,
        bool sourceRetained)
    {
        lock (sync)
        {
            if (!importInProgress || current is { State: "queued" or "running" })
            {
                return null;
            }

            importInProgress = false;
            return StartOperation(rootId, rootName, ImportScanMetadataOperation, sourceRetained, relativePath);
        }
    }

    public void EndLibraryMutationReservation()
    {
        lock (sync)
        {
            importInProgress = false;
        }
    }

    public bool TryBeginRootRemoval(int rootId)
    {
        lock (sync)
        {
            if (current is { State: "queued" or "running" }
                || importInProgress
                || !rootsBeingRemoved.Add(rootId))
            {
                return false;
            }

            return true;
        }
    }

    public void EndRootRemoval(int rootId)
    {
        lock (sync)
        {
            rootsBeingRemoved.Remove(rootId);
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
            Message = job.OperationType == DatabaseCleanupOperation
                ? "Checking library entries…"
                : job.OperationType == ScanOperation
                ? "Scanning folder…"
                : job.OperationType == ImportScanMetadataOperation
                    ? "Scanning imported album…"
                    : "Preparing metadata refresh…",
        });

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var library = scope.ServiceProvider.GetRequiredService<IMusicLibraryService>();
            if (job.OperationType == DatabaseCleanupOperation)
            {
                var result = await library.CleanDatabaseAsync(
                    (completed, total, message) => Update(job.Id, status => status with
                    {
                        CompletedUnits = completed,
                        TotalUnits = total,
                        Message = message,
                    }),
                    cancellationToken);
                var skipped = result.SkippedRoots.Count == 0
                    ? " No sources skipped."
                    : $" Skipped {result.SkippedRoots.Count} unavailable or unsafe sources: {string.Join(", ", result.SkippedRoots)}.";
                Complete(job.Id, "completed",
                    $"Removed {result.RemovedTracks} tracks and {result.RemovedFolders} folders from the database.{skipped}");
                return;
            }

            if (job.OperationType == ScanOperation)
            {
                var result = await library.ScanAsync(job.RootId, cancellationToken);
                Complete(
                    job.Id,
                    "completed",
                    $"Scanned {result.DiscoveredTracks} tracks; {result.UnreadableTracks} files could not be read.");
                return;
            }

            if (job.OperationType == ImportScanMetadataOperation)
            {
                var albumPath = job.RelativePath
                    ?? throw new InvalidOperationException("The imported album folder is missing from the operation.");
                var scanResult = await library.ScanFolderAsync(job.RootId, albumPath, cancellationToken);
                Update(job.Id, status => status with
                {
                    Message = $"Scanned {scanResult.DiscoveredTracks} tracks. Refreshing metadata…",
                    CompletedUnits = 1,
                    TotalUnits = 2,
                });
                var refreshResult = await library.RefreshFolderMetadataAsync(
                    job.RootId,
                    albumPath,
                    (completed, total, message) => Update(job.Id, status => status with
                    {
                        CompletedUnits = completed + 1,
                        TotalUnits = total + 1,
                        Message = message,
                    }),
                    cancellationToken);
                Complete(
                    job.Id,
                    "completed",
                    $"Imported album scan completed with {scanResult.DiscoveredTracks} tracks; "
                        + $"{scanResult.UnreadableTracks} files could not be read; refreshed "
                        + $"{refreshResult.ArtistsProcessed} artists and {refreshResult.AlbumsProcessed} albums."
                        + (job.SourceRetained ? " Original files were retained in the unscanned folder." : ""));
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
            Complete(job.Id, "failed", job.OperationType == DatabaseCleanupOperation
                ? "Database cleanup failed; some sources may already have been cleaned. Review the API logs."
                : "The operation failed. Check that the folder is accessible and review the API logs.");
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

    private LibraryOperationStatus StartOperation(
        int rootId,
        string rootName,
        string operationType,
        bool sourceRetained,
        string? relativePath)
    {
        var status = new LibraryOperationStatus(
            Guid.NewGuid(),
            rootId,
            rootName,
            operationType,
            "queued",
            operationType switch
            {
                ScanOperation => "Scan queued.",
                DatabaseCleanupOperation => "Database cleanup queued.",
                ImportScanMetadataOperation => sourceRetained
                    ? "Album imported; original files remain in the unscanned folder. Scan and metadata refresh queued."
                    : "Album import queued for scanning and metadata refresh.",
                _ => "Metadata refresh queued.",
            },
            0,
            0,
            DateTimeOffset.UtcNow,
            null);
        current = status;
        history[status.Id] = status;
        historyOrder.Enqueue(status.Id);
        if (!queue.Writer.TryWrite(new LibraryOperationJob(status.Id, rootId, operationType, sourceRetained, relativePath)))
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

        return status;
    }

    private sealed record LibraryOperationJob(
        Guid Id, int RootId, string OperationType, bool SourceRetained, string? RelativePath);
}
