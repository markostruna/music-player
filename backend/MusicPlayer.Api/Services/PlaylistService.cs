using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public interface IPlaylistService
{
    Task<IReadOnlyList<PlaylistResponse>> ListAsync(int userId, CancellationToken cancellationToken);
    Task<PlaylistResponse?> GetAsync(int userId, int id, CancellationToken cancellationToken);
    Task<PlaylistResponse> CreateAsync(int userId, UpdatePlaylistRequest request, CancellationToken cancellationToken);
    Task<PlaylistResponse?> UpdateAsync(int userId, int id, UpdatePlaylistRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int userId, int id, CancellationToken cancellationToken);
}

public sealed class PlaylistService(MusicDbContext database, MusicLibraryBrowseService library) : IPlaylistService
{
    public async Task<IReadOnlyList<PlaylistResponse>> ListAsync(int userId, CancellationToken cancellationToken)
    {
        var playlists = await database.Playlists.AsNoTracking().Include(p => p.Tracks)
            .Where(p => p.UserId == userId).OrderBy(p => p.Id).ToListAsync(cancellationToken);
        var result = new List<PlaylistResponse>();
        foreach (var playlist in playlists)
            result.Add(await ToResponseAsync(playlist, cancellationToken));
        return result;
    }

    public async Task<PlaylistResponse?> GetAsync(int userId, int id, CancellationToken cancellationToken)
    {
        var playlist = await database.Playlists.AsNoTracking().Include(p => p.Tracks)
            .SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id, cancellationToken);
        return playlist is null ? null : await ToResponseAsync(playlist, cancellationToken);
    }

    public async Task<PlaylistResponse> CreateAsync(int userId, UpdatePlaylistRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);
        var playlist = new MusicPlaylist { UserId = userId, Name = request.Name.Trim(), BackgroundId = request.BackgroundId ?? 1 };
        playlist.Tracks = request.TrackIds.Select((id, index) => new MusicPlaylistTrack { TrackId = id, Position = index }).ToList();
        database.Playlists.Add(playlist);
        await database.SaveChangesAsync(cancellationToken);
        return await ToResponseAsync(playlist, cancellationToken);
    }

    public async Task<PlaylistResponse?> UpdateAsync(int userId, int id, UpdatePlaylistRequest request, CancellationToken cancellationToken)
    {
        var playlist = await database.Playlists.Include(p => p.Tracks)
            .SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id, cancellationToken);
        if (playlist is null) return null;
        await ValidateAsync(request, cancellationToken);
        var existing = playlist.Tracks.OrderBy(item => item.Position).ToArray();
        var available = await library.GetAvailableTrackIdsAsync(existing.Select(item => item.TrackId).ToArray(), cancellationToken);
        var ids = request.TrackIds.ToList();
        foreach (var item in existing.Where(item => !available.Contains(item.TrackId)))
            ids.Insert(Math.Min(item.Position, ids.Count), item.TrackId);
        playlist.Name = request.Name.Trim();
        if (request.BackgroundId is int backgroundId) playlist.BackgroundId = backgroundId;
        database.PlaylistTracks.RemoveRange(playlist.Tracks);
        playlist.Tracks = ids.Select((trackId, index) => new MusicPlaylistTrack { TrackId = trackId, Position = index }).ToList();
        await database.SaveChangesAsync(cancellationToken);
        return await ToResponseAsync(playlist, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int userId, int id, CancellationToken cancellationToken)
    {
        var playlist = await database.Playlists.SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id, cancellationToken);
        if (playlist is null) return false;
        database.Playlists.Remove(playlist);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ValidateAsync(UpdatePlaylistRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.TrackIds is null
            || request.TrackIds.Any(id => id <= 0) || request.BackgroundId is < 1 or > 20)
            throw new ArgumentException("Invalid playlist name, tracks, or background.");
        var ids = await library.GetAvailableTrackIdsAsync(request.TrackIds, cancellationToken);
        if (request.TrackIds.Any(id => !ids.Contains(id)))
            throw new ArgumentException("The playlist contains unavailable tracks.");
    }

    private async Task<PlaylistResponse> ToResponseAsync(MusicPlaylist playlist, CancellationToken cancellationToken)
    {
        var tracks = await library.GetTracksByIdsAsync(
            playlist.Tracks.OrderBy(item => item.Position).Select(item => item.TrackId).ToArray(), cancellationToken);
        return new(playlist.Id, playlist.Name, tracks, playlist.Tracks.Count - tracks.Count, playlist.BackgroundId);
    }
}
