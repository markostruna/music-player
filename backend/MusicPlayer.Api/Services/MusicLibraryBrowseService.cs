using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed class MusicLibraryBrowseService(MusicDbContext database)
{
    public async Task<IReadOnlyList<TrackResponse>> GetTracksAsync(CancellationToken cancellationToken)
        => await GetTracksAsync(null, cancellationToken);

    public async Task<IReadOnlyList<TrackResponse>> GetTracksAsync(string? query, CancellationToken cancellationToken)
    {
        var tracksQuery = ApplySearch(AvailableTracks(), query);
        var tracks = await tracksQuery
            .OrderBy(track => track.AlbumArtist)
            .ThenBy(track => track.Album)
            .ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title)
            .ToListAsync(cancellationToken);
        return await ToTrackResponsesAsync(tracks, cancellationToken);
    }

    public async Task<PagedResponse<TrackResponse>> GetTracksPageAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var tracksQuery = ApplySearch(AvailableTracks(), query);

        var totalCount = await tracksQuery.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var actualPage = Math.Min(page, pageCount);
        var tracks = await tracksQuery
            .OrderBy(track => track.AlbumArtist)
            .ThenBy(track => track.Album)
            .ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title)
            .ThenBy(track => track.Id)
            .Skip((actualPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<TrackResponse>(
            await ToTrackResponsesAsync(tracks, cancellationToken),
            totalCount,
            actualPage,
            pageSize);
    }

    public async Task<IReadOnlyList<AlbumResponse>> GetAlbumsAsync(CancellationToken cancellationToken)
    {
        var tracks = await GetTracksAsync(cancellationToken);
        return tracks.GroupBy(track => new AlbumKey(
                track.Album.Trim(),
                string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist),
            AlbumKeyComparer.Instance)
            .Select(group => new AlbumResponse(
                group.Key.Album,
                group.Key.Artist,
                group.Count(),
                group.Sum(track => track.DurationSeconds),
                group.First().Id,
                group.ToArray()))
            .OrderBy(album => album.Artist)
            .ThenBy(album => album.Album)
            .ToList();
    }

    private IQueryable<MusicTrack> AvailableTracks() =>
        database.Tracks.AsNoTracking()
            .Where(track => track.IsAvailable && track.SourceRoot != null && track.SourceRoot.IsEnabled);

    private static IQueryable<MusicTrack> ApplySearch(IQueryable<MusicTrack> tracks, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return tracks;
        }

        var pattern = $"%{query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        return tracks.Where(track =>
            EF.Functions.Like(track.Title, pattern, "\\")
            || EF.Functions.Like(track.Artist, pattern, "\\")
            || EF.Functions.Like(track.Album, pattern, "\\")
            || EF.Functions.Like(track.AlbumArtist, pattern, "\\"));
    }

    private async Task<IReadOnlyList<TrackResponse>> ToTrackResponsesAsync(
        IReadOnlyList<MusicTrack> tracks,
        CancellationToken cancellationToken)
    {
        var artistKeys = tracks
            .SelectMany(track => new[]
            {
                GetArtistMetadataKey(track.Artist),
                GetArtistMetadataKey(string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist),
            })
            .Distinct()
            .ToArray();
        var albumKeys = tracks
            .Select(track => GetAlbumMetadataKey(
                string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist,
                track.Album))
            .Distinct()
            .ToArray();
        var artistMetadata = artistKeys.Length == 0
            ? new Dictionary<string, MusicArtistMetadata>()
            : await database.ArtistMetadata.AsNoTracking()
                .Where(item => artistKeys.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, cancellationToken);
        var albumMetadata = albumKeys.Length == 0
            ? new Dictionary<string, MusicAlbumMetadata>()
            : await database.AlbumMetadata.AsNoTracking()
                .Where(item => albumKeys.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, cancellationToken);

        return tracks.Select(track =>
        {
            var albumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
            artistMetadata.TryGetValue(GetArtistMetadataKey(track.Artist), out var artist);
            artistMetadata.TryGetValue(GetArtistMetadataKey(albumArtist), out var albumArtistInfo);
            albumMetadata.TryGetValue(GetAlbumMetadataKey(albumArtist, track.Album), out var album);
            return ToResponse(track, artist, albumArtistInfo, album);
        }).ToArray();
    }

    private static TrackResponse ToResponse(
        MusicTrack track,
        MusicArtistMetadata? artist,
        MusicArtistMetadata? albumArtist,
        MusicAlbumMetadata? album) => new(
        track.Id,
        track.SourceRootId,
        track.FolderId,
        track.Title,
        track.Artist,
        track.Album,
        track.AlbumArtist,
        track.Genre,
        track.TrackNumber,
        track.DiscNumber,
        track.Year,
        track.DurationSeconds,
        $"/api/library/tracks/{track.Id}/stream",
        album?.ImageMissing == false && !string.IsNullOrWhiteSpace(album.ImageFileName)
            ? $"/api/library/tracks/{track.Id}/cover?v={album.UpdatedAt.ToUnixTimeMilliseconds()}"
            : $"/api/library/tracks/{track.Id}/cover",
        Path.GetExtension(track.RelativePath).TrimStart('.').ToLowerInvariant(),
        album?.Description ?? "",
        artist?.Description ?? "",
        artist?.ImageMissing == false && !string.IsNullOrWhiteSpace(artist.ImageFileName)
            ? $"/api/library/artists/{artist.Key}/image?v={artist.UpdatedAt.ToUnixTimeMilliseconds()}"
            : "",
        artist is null || artist.ImageMissing || string.IsNullOrWhiteSpace(artist.ImageFileName),
        albumArtist?.Description ?? "",
        albumArtist?.ImageMissing == false && !string.IsNullOrWhiteSpace(albumArtist.ImageFileName)
            ? $"/api/library/artists/{albumArtist.Key}/image?v={albumArtist.UpdatedAt.ToUnixTimeMilliseconds()}"
            : "",
        albumArtist is null || albumArtist.ImageMissing || string.IsNullOrWhiteSpace(albumArtist.ImageFileName));

    private static string GetArtistMetadataKey(string artist) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"artist\0{artist.Trim().ToUpperInvariant()}")));

    private static string GetAlbumMetadataKey(string artist, string album) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"album\0{artist.Trim().ToUpperInvariant()}\0{album.Trim().ToUpperInvariant()}")));

    private readonly record struct AlbumKey(string Album, string Artist);

    private sealed class AlbumKeyComparer : IEqualityComparer<AlbumKey>
    {
        public static AlbumKeyComparer Instance { get; } = new();

        public bool Equals(AlbumKey x, AlbumKey y) =>
            string.Equals(x.Album, y.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(AlbumKey value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Album),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Artist));
    }
}
