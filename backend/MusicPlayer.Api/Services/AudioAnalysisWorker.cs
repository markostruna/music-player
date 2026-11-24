using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed class AudioAnalysisWorker(
    IServiceScopeFactory scopes,
    IAudioAnalyzer analyzer,
    ILogger<AudioAnalysisWorker> logger) : BackgroundService
{
    internal const int AnalysisVersion = 1;
    private readonly Dictionary<int, DateTimeOffset> failed = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? idleSince = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await AnalyzeNextAsync(stoppingToken))
                    {
                        idleSince = null;
                    }
                    else
                    {
                        idleSince ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - idleSince >= TimeSpan.FromMinutes(1))
                            analyzer.Reset();
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException
                    or UnauthorizedAccessException or TimeoutException or Win32Exception or JsonException
                    or DbUpdateException or DbException or ArgumentException)
                {
                    logger.LogError(exception, "Audio analysis failed; retrying in one minute.");
                    analyzer.Reset();
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            analyzer.Reset();
        }
    }

    internal async Task<bool> AnalyzeNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in failed.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            failed.Remove(id);
        var excluded = failed.Keys.ToArray();
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        var track = await database.Tracks
            .Include(item => item.SourceRoot)
            .Include(item => item.AudioAnalysis)
            .Where(item => item.IsAvailable && item.SourceRoot != null && item.SourceRoot.IsEnabled
                && !excluded.Contains(item.Id)
                && (item.AudioAnalysis == null || item.AudioAnalysis.Version != AnalysisVersion
                    || item.AudioAnalysis.FileSize != item.FileSize
                    || item.AudioAnalysis.LastWriteTime != item.LastWriteTime))
            .OrderByDescending(item => item.ImportedAtUnixMilliseconds)
            .ThenBy(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (track?.SourceRoot is null) return false;

        var library = scope.ServiceProvider.GetRequiredService<IMusicLibraryService>();
        var media = await library.ResolveStreamAsync(track.Id, cancellationToken);
        if (media is null || !MusicLibraryService.IsSafePathWithoutLinks(track.SourceRoot.Path, media.FullPath))
        {
            logger.LogWarning("Cannot analyze unavailable or unsafe track {TrackId}.", track.Id);
            failed[track.Id] = now.AddMinutes(5);
            return true;
        }

        var snapshotSize = track.FileSize;
        var snapshotTime = track.LastWriteTime;
        var info = new FileInfo(media.FullPath);
        if (info.Length != snapshotSize || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != snapshotTime)
        {
            logger.LogWarning("Track {TrackId} changed since its library scan; rescan it before analysis.", track.Id);
            failed[track.Id] = now.AddMinutes(5);
            return true;
        }

        var result = await analyzer.AnalyzeAsync(media.FullPath, track.DurationSeconds, cancellationToken);
        if (result.Error is not null)
        {
            logger.LogWarning("Could not analyze track {TrackId}: {Error}", track.Id, result.Error);
            failed[track.Id] = now.AddMinutes(5);
            return true;
        }
        if (result.Embedding is null || result.Embedding.Length != 512
            || result.Embedding.Any(value => !float.IsFinite(value))
            || !result.Embedding.Any(value => value != 0)
            || string.IsNullOrWhiteSpace(result.Genre) || result.Genre.Length > 256)
            throw new JsonException("The audio analysis response was invalid.");

        info.Refresh();
        if (!info.Exists || info.Length != snapshotSize
            || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != snapshotTime)
        {
            logger.LogWarning("Track {TrackId} changed during audio analysis.", track.Id);
            failed[track.Id] = now.AddMinutes(5);
            return true;
        }
        await database.Entry(track).ReloadAsync(cancellationToken);
        if (database.Entry(track).State == EntityState.Detached || !track.IsAvailable
            || track.FileSize != snapshotSize || track.LastWriteTime != snapshotTime)
            return true;

        var analysis = track.AudioAnalysis;
        if (analysis is null)
        {
            analysis = new TrackAudioAnalysis { TrackId = track.Id, Genre = "", EmbeddingJson = "" };
            database.TrackAudioAnalyses.Add(analysis);
        }
        analysis.FileSize = snapshotSize;
        analysis.LastWriteTime = snapshotTime;
        analysis.Version = AnalysisVersion;
        analysis.Genre = result.Genre;
        analysis.EmbeddingJson = JsonSerializer.Serialize(result.Embedding, JsonOptions);
        track.PredictedGenre = result.Genre;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

}
