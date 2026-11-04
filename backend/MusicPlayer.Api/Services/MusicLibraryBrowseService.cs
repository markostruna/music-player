using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed class MusicLibraryBrowseService(MusicDbContext database)
{
    private static readonly Regex ArtistReferencePattern = new(
        @"\[a=(?<artist>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static int SelectPage(int requestedPage, int pageCount, bool random) =>
        random ? Random.Shared.Next(pageCount) + 1 : Math.Min(requestedPage, pageCount);

    public async Task<IReadOnlyList<TrackResponse>> GetTracksAsync(CancellationToken cancellationToken)
        => await GetTracksAsync(null, cancellationToken);

    public async Task<IReadOnlyList<TrackResponse>> GetTracksAsync(
        string? query,
        CancellationToken cancellationToken,
        int? folderId = null,
        int? sourceRootId = null,
        string? folderPath = null)
    {
        var tracksQuery = ApplyFolderFilter(AvailableTracks(), folderId, sourceRootId, folderPath);
        tracksQuery = ApplySearch(tracksQuery, query);
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
        CancellationToken cancellationToken,
        bool random = false,
        int? folderId = null,
        int? sourceRootId = null,
        string? folderPath = null)
    {
        var tracksQuery = ApplyFolderFilter(AvailableTracks(), folderId, sourceRootId, folderPath);
        tracksQuery = ApplySearch(tracksQuery, query);

        var totalCount = await tracksQuery.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var actualPage = SelectPage(page, pageCount, random);
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

    public async Task<PagedResponse<AlbumSummaryResponse>> GetAlbumsPageAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool random = false)
    {
        var albums = AvailableTracks().Select(track => new
        {
            Track = track,
            Album = EF.Functions.Collate(track.Album.Trim(), "NOCASE"),
            Artist = EF.Functions.Collate(
                (track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist).Trim(),
                "NOCASE"),
        });
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = CreateSearchPattern(query);
            albums = albums.Where(item =>
                EF.Functions.Like(item.Album, pattern, "\\")
                || EF.Functions.Like(item.Artist, pattern, "\\"));
        }

        var totalCount = await albums
            .Select(item => new { item.Album, item.Artist })
            .Distinct()
            .CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var actualPage = SelectPage(page, pageCount, random);
        var albumGroups = albums
            .GroupBy(item => new { item.Album, item.Artist })
            .Select(group => new
            {
                group.Key.Album,
                group.Key.Artist,
                ReleaseYear = group.Max(item => item.Track.Year),
                TrackCount = group.Count(),
                DurationSeconds = group.Sum(item => item.Track.DurationSeconds),
                CoverTrackId = group.Min(item => item.Track.Id),
            });
        var albumPage = await albumGroups
            .OrderBy(item => item.Artist)
            .ThenBy(item => item.Album)
            .ThenBy(item => item.CoverTrackId)
            .Skip((actualPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var coverTrackIds = albumPage.Select(item => item.CoverTrackId).ToArray();
        var coverTracks = coverTrackIds.Length == 0
            ? Array.Empty<TrackResponse>()
            : await ToTrackResponsesAsync(
                await AvailableTracks().Where(track => coverTrackIds.Contains(track.Id)).ToListAsync(cancellationToken),
                cancellationToken);
        var coversByTrackId = coverTracks.ToDictionary(track => track.Id);
        var items = albumPage.Select(item => new AlbumSummaryResponse(
            item.Album,
            item.Artist,
            item.ReleaseYear == 0 ? null : item.ReleaseYear,
            item.TrackCount,
            item.DurationSeconds,
            item.CoverTrackId,
            $"{coversByTrackId[item.CoverTrackId].CoverUrl}&variant=tile")).ToArray();
        return new PagedResponse<AlbumSummaryResponse>(items, totalCount, actualPage, pageSize);
    }

    public async Task<IReadOnlyList<TrackResponse>> GetAlbumTracksAsync(
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        var requestedArtist = artist.Trim();
        var requestedAlbum = album.Trim();
        var normalizedArtist = requestedArtist.ToUpperInvariant();
        var normalizedAlbum = requestedAlbum.ToUpperInvariant();
        var tracks = await AvailableTracks()
            .Where(track =>
                ((track.AlbumArtist.Trim() == "" ? track.Artist.Trim() : track.AlbumArtist.Trim()) == requestedArtist
                    || (track.AlbumArtist.Trim() == "" ? track.Artist.Trim() : track.AlbumArtist.Trim()).ToUpper() == normalizedArtist)
                && (track.Album.Trim() == requestedAlbum || track.Album.Trim().ToUpper() == normalizedAlbum))
            .OrderBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title)
            .ThenBy(track => track.Id)
            .ToListAsync(cancellationToken);
        return await ToTrackResponsesAsync(tracks, cancellationToken);
    }

    public async Task<IReadOnlyList<TrackResponse>> GetArtistTracksAsync(
        string artist,
        CancellationToken cancellationToken)
    {
        var requestedArtist = artist.Trim();
        var normalizedArtist = requestedArtist.ToUpperInvariant();
        var tracks = await AvailableTracks()
            .Where(track =>
                (track.AlbumArtist.Trim() == ""
                    ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                    : track.AlbumArtist.Trim()) == requestedArtist
                || (track.AlbumArtist.Trim() == ""
                    ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                    : track.AlbumArtist.Trim()).ToUpper() == normalizedArtist)
            .OrderBy(track => track.AlbumArtist)
            .ThenBy(track => track.Album)
            .ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title)
            .ThenBy(track => track.Id)
            .ToListAsync(cancellationToken);
        return await ToTrackResponsesAsync(tracks, cancellationToken);
    }

    public async Task<IReadOnlyList<TrackResponse>> GetAlbumTracksByTrackIdAsync(
        int trackId,
        CancellationToken cancellationToken)
    {
        var album = await AvailableTracks()
            .Where(track => track.Id == trackId)
            .Select(track => new
            {
                Artist = (track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist).Trim(),
                track.Album,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return album is null
            ? Array.Empty<TrackResponse>()
            : await GetAlbumTracksAsync(album.Artist, album.Album, cancellationToken);
    }

    public async Task<PagedResponse<ArtistSummaryResponse>> GetArtistsPageAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool random = false)
    {
        var artists = AvailableTracks().Select(track => new
        {
            Track = track,
            Artist = EF.Functions.Collate(
                track.AlbumArtist.Trim() == ""
                    ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                    : track.AlbumArtist.Trim(),
                "NOCASE"),
        });
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = CreateSearchPattern(query);
            artists = artists.Where(item => EF.Functions.Like(item.Artist, pattern, "\\"));
        }

        var totalCount = await artists.Select(item => item.Artist).Distinct().CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var actualPage = SelectPage(page, pageCount, random);
        var artistGroups = artists
            .GroupBy(item => item.Artist)
            .Select(group => new
            {
                Artist = group.Key,
                TrackCount = group.Count(),
                RepresentativeTrackId = group.Min(item => item.Track.Id),
            });
        var artistPage = await artistGroups
            .OrderBy(item => item.Artist)
            .ThenBy(item => item.RepresentativeTrackId)
            .Skip((actualPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var artistNames = artistPage.Select(item => item.Artist).ToArray();
        var distinctAlbums = AvailableTracks()
            .Select(track => new
            {
                Artist = EF.Functions.Collate(
                    track.AlbumArtist.Trim() == ""
                        ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                        : track.AlbumArtist.Trim(),
                    "NOCASE"),
                Album = EF.Functions.Collate(track.Album.Trim(), "NOCASE"),
            })
            .Where(item => artistNames.Contains(item.Artist))
            .Distinct();
        var albumCounts = await distinctAlbums
            .GroupBy(item => item.Artist)
            .Select(group => new { Artist = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Artist, item => item.Count, cancellationToken);
        var representativeIds = artistPage.Select(item => item.RepresentativeTrackId).ToArray();
        var representativeTracks = representativeIds.Length == 0
            ? new List<MusicTrack>()
            : await AvailableTracks()
                .Where(track => representativeIds.Contains(track.Id))
                .ToListAsync(cancellationToken);
        var representatives = await ToTrackResponsesAsync(representativeTracks, cancellationToken);
        var representativesById = representatives.ToDictionary(track => track.Id);
        var artistKeys = artistPage.Select(item => GetArtistMetadataKey(item.Artist)).Distinct().ToArray();
        var artistMetadata = artistKeys.Length == 0
            ? new Dictionary<string, MusicArtistMetadata>()
            : await database.ArtistMetadata.AsNoTracking()
                .Where(item => artistKeys.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, cancellationToken);
        var items = artistPage.Select(item =>
        {
            var representative = representativesById[item.RepresentativeTrackId];
            artistMetadata.TryGetValue(GetArtistMetadataKey(item.Artist), out var metadata);
            return new ArtistSummaryResponse(
                item.Artist,
                item.TrackCount,
                albumCounts.GetValueOrDefault(item.Artist),
                $"{representative.CoverUrl}&variant=tile",
                GetArtistImageUrl(metadata) ?? "",
                IsArtistImageMissing(metadata));
        }).ToArray();
        return new PagedResponse<ArtistSummaryResponse>(items, totalCount, actualPage, pageSize);
    }

    public async Task<ArtistMetadataResponse> GetArtistMetadataAsync(
        string artist,
        CancellationToken cancellationToken)
    {
        var key = GetArtistMetadataKey(artist);
        var metadata = await database.ArtistMetadata.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        var description = metadata?.Description ?? "";
        var references = ArtistReferencePattern.Matches(description)
            .Select(match => match.Groups["artist"].Value.Trim())
            .Where(name => name.Length is > 0 and <= 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referencedArtists = Array.Empty<string>();
        if (references.Length > 0)
        {
            var normalizedReferences = references.Select(name => name.ToUpperInvariant()).ToArray();
            var trackArtists = await AvailableTracks()
                .Where(track => normalizedReferences.Contains(
                    (track.AlbumArtist.Trim() == ""
                        ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                        : track.AlbumArtist.Trim()).ToUpper()))
                .Select(track => track.AlbumArtist.Trim() == ""
                    ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                    : track.AlbumArtist.Trim())
                .Distinct()
                .ToListAsync(cancellationToken);
            var knownArtists = trackArtists
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
            referencedArtists = references
                .Where(knownArtists.ContainsKey)
                .Select(name => knownArtists[name])
                .ToArray();
        }

        return new ArtistMetadataResponse(
            description,
            GetArtistImageUrl(metadata),
            IsArtistImageMissing(metadata),
            referencedArtists);
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

    private static IQueryable<MusicTrack> ApplyFolderFilter(
        IQueryable<MusicTrack> tracks,
        int? folderId,
        int? sourceRootId,
        string? folderPath)
    {
        if (folderId.HasValue) tracks = tracks.Where(track => track.FolderId == folderId.Value);
        if (sourceRootId.HasValue && folderPath is not null)
        {
            tracks = tracks.Where(track =>
                track.SourceRootId == sourceRootId.Value &&
                track.Folder != null &&
                track.Folder.RelativePath == folderPath);
        }

        return tracks;
    }

    private static IQueryable<MusicTrack> ApplySearch(IQueryable<MusicTrack> tracks, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return tracks;
        }

        var pattern = CreateSearchPattern(query);
        return tracks.Where(track =>
            EF.Functions.Like(track.Title, pattern, "\\")
            || EF.Functions.Like(track.Artist, pattern, "\\")
            || EF.Functions.Like(track.Album, pattern, "\\")
            || EF.Functions.Like(track.AlbumArtist, pattern, "\\"));
    }

    private static string CreateSearchPattern(string query) =>
        $"%{query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

    private async Task<IReadOnlyList<TrackResponse>> ToTrackResponsesAsync(
        IReadOnlyList<MusicTrack> tracks,
        CancellationToken cancellationToken)
    {
        var albumKeys = tracks
            .Select(track => GetAlbumMetadataKey(
                string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist,
                track.Album))
            .Distinct()
            .ToArray();
        var albumMetadata = albumKeys.Length == 0
            ? new Dictionary<string, MusicAlbumMetadata>()
            : await database.AlbumMetadata.AsNoTracking()
                .Where(item => albumKeys.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, cancellationToken);

        return tracks.Select(track =>
        {
            var albumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
            albumMetadata.TryGetValue(GetAlbumMetadataKey(albumArtist, track.Album), out var album);
            return ToResponse(track, album);
        }).ToArray();
    }

    private static TrackResponse ToResponse(
        MusicTrack track,
        MusicAlbumMetadata? album) => new(
        track.Id,
        track.SourceRootId,
        track.FolderId,
        track.RelativePath,
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
            ? $"/api/library/tracks/{track.Id}/cover?format=webp&variant=track&v={album.UpdatedAt.ToUnixTimeMilliseconds()}"
            : $"/api/library/tracks/{track.Id}/cover?format=webp&variant=track",
            Path.GetExtension(track.RelativePath).TrimStart('.').ToLowerInvariant());

    private static string? GetArtistImageUrl(MusicArtistMetadata? metadata) =>
            metadata?.ImageMissing == false && !string.IsNullOrWhiteSpace(metadata.ImageFileName)
                ? $"/api/library/artists/{metadata.Key}/image?format=webp&variant=tile&v={metadata.UpdatedAt.ToUnixTimeMilliseconds()}"
                : null;

    private static bool IsArtistImageMissing(MusicArtistMetadata? metadata) =>
            metadata is null || metadata.ImageMissing || string.IsNullOrWhiteSpace(metadata.ImageFileName);

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
