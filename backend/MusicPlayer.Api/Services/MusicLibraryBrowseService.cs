using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public async Task<IReadOnlyList<TrackResponse>> GetTracksByIdsAsync(
        IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var tracks = await AvailableTracks().Where(track => ids.Contains(track.Id)).ToListAsync(cancellationToken);
        var responses = await ToTrackResponsesAsync(tracks, cancellationToken);
        var byId = responses.ToDictionary(track => track.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    public async Task<HashSet<int>> GetAvailableTrackIdsAsync(
        IReadOnlyList<int> ids, CancellationToken cancellationToken) =>
        (await AvailableTracks().Where(track => ids.Contains(track.Id))
            .Select(track => track.Id).ToArrayAsync(cancellationToken)).ToHashSet();

    public Task<TrackResponse?> FindSimilarTrackAsync(
        int trackId, IReadOnlyList<int> excludedIds, CancellationToken cancellationToken) =>
        FindSimilarTrackAsync(trackId, excludedIds, Random.Shared, cancellationToken);

    internal async Task<TrackResponse?> FindSimilarTrackAsync(
        int trackId, IReadOnlyList<int> excludedIds, Random random, CancellationToken cancellationToken)
    {
        var source = await AvailableTracks().Include(track => track.AudioAnalysis)
            .SingleOrDefaultAsync(track => track.Id == trackId, cancellationToken);
        if (source is null) return null;
        var sourceVector = GetCurrentEmbedding(source);
        var artist = source.Artist.Trim();
        var knownArtist = artist.Length > 0 && !artist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase);
        var sameArtist = new List<(int Id, double Score)>();
        var otherArtists = new List<(int Id, double Score)>();
        var candidates = AvailableTracks().Include(track => track.AudioAnalysis)
            .Where(track => track.Id != trackId && !excludedIds.Contains(track.Id));
        await foreach (var candidate in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var artistMatch = knownArtist && artist.Equals(candidate.Artist.Trim(), StringComparison.OrdinalIgnoreCase);
            var genreMatch = GenresMatch(source.Genre, candidate.Genre)
                || GenresMatch(source.Genre, candidate.PredictedGenre)
                || GenresMatch(source.PredictedGenre, candidate.Genre)
                || GenresMatch(source.PredictedGenre, candidate.PredictedGenre);
            var similarity = sourceVector is null ? -1 : CosineSimilarity(sourceVector, GetCurrentEmbedding(candidate));
            if (!artistMatch && !genreMatch && similarity < 0.6) continue;
            var scored = artistMatch ? sameArtist : otherArtists;
            scored.Add((candidate.Id, (genreMatch ? 1 : 0) + Math.Max(0, similarity)));
            if (scored.Count > 5)
                scored.Remove(scored.MinBy(item => item.Score));
        }
        if (sameArtist.Count == 0 && otherArtists.Count == 0) return null;
        var best = sameArtist.Count == 0 ? otherArtists
            : otherArtists.Count == 0 ? sameArtist
            : random.Next(2) == 0 ? sameArtist : otherArtists;
        var id = best[random.Next(best.Count)].Id;
        return (await GetTracksByIdsAsync([id], cancellationToken)).SingleOrDefault();
    }

    private static bool GenresMatch(string left, string right) =>
        !string.IsNullOrWhiteSpace(left)
        && left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static float[]? GetCurrentEmbedding(MusicTrack track)
    {
        var analysis = track.AudioAnalysis;
        if (analysis is null || analysis.Version != AudioAnalysisWorker.AnalysisVersion
            || analysis.FileSize != track.FileSize || analysis.LastWriteTime != track.LastWriteTime)
            return null;
        var vector = JsonSerializer.Deserialize<float[]>(analysis.EmbeddingJson)
            ?? throw new InvalidDataException("A saved audio embedding is invalid.");
        if (vector.Length != 512 || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("A saved audio embedding is invalid.");
        return vector;
    }

    private static double CosineSimilarity(float[]? left, float[]? right)
    {
        if (left is null || right is null) return -1;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += (double)left[index] * right[index];
            leftNorm += (double)left[index] * left[index];
            rightNorm += (double)right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    public async Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken)
    {
        var tagged = AvailableTracks()
            .Select(track => EF.Functions.Collate(track.Genre.Trim(), "NOCASE"))
            .Where(genre => genre != "");
        var predicted = AvailableTracks()
            .Select(track => EF.Functions.Collate(track.PredictedGenre.Trim(), "NOCASE"))
            .Where(genre => genre != "");
        return await tagged.Union(predicted).OrderBy(genre => genre).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GenreSourceResponse>> GetGenreSourcesAsync(CancellationToken cancellationToken)
    {
        var tagged = AvailableTracks()
            .Select(track => new { Name = EF.Functions.Collate(track.Genre.Trim(), "NOCASE"), Tagged = 1, Analyzed = 0 })
            .Where(item => item.Name != "");
        var predicted = AvailableTracks()
            .Select(track => new { Name = EF.Functions.Collate(track.PredictedGenre.Trim(), "NOCASE"), Tagged = 0, Analyzed = 1 })
            .Where(item => item.Name != "");
        var genres = await tagged.Concat(predicted)
            .GroupBy(item => item.Name)
            .Select(group => new { Name = group.Key, Tagged = group.Max(item => item.Tagged), Analyzed = group.Max(item => item.Analyzed) })
            .OrderBy(item => item.Name)
            .ToArrayAsync(cancellationToken);
        return genres.Select(item => new GenreSourceResponse(item.Name, item.Tagged == 1, item.Analyzed == 1)).ToArray();
    }

    public async Task<PagedResponse<TrackResponse>> SearchListeningTracksAsync(
        int userId, string? query, string? genre, string status, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var tracks = ApplySearch(AvailableTracks(), query);
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var value = genre.Trim();
            tracks = tracks.Where(track =>
                EF.Functions.Collate(track.Genre.Trim(), "NOCASE") == value
                || EF.Functions.Collate(track.PredictedGenre.Trim(), "NOCASE") == value);
        }

        var history = database.PlaybackHistory.AsNoTracking().Where(item => item.UserId == userId);
        var latest = history.Where(item => !history.Any(other => other.TrackId == item.TrackId
            && (other.StartedAtUnixMilliseconds > item.StartedAtUnixMilliseconds
                || (other.StartedAtUnixMilliseconds == item.StartedAtUnixMilliseconds && other.Id > item.Id))));
        tracks = status switch
        {
            "recent" => tracks.Where(track => latest.Any(item =>
                item.TrackId == track.Id && item.CompletedAtUnixMilliseconds != null)),
            "progress" => tracks.Where(track => latest.Any(item =>
                item.TrackId == track.Id && item.CompletedAtUnixMilliseconds == null
                && item.PositionSeconds > 0 && track.DurationSeconds > 0
                && item.PositionSeconds < track.DurationSeconds)),
            "unplayed" => tracks.Where(track => !history.Any(item => item.TrackId == track.Id)),
            "all" => tracks,
            _ => throw new ArgumentException("Invalid listening status.", nameof(status)),
        };

        var total = await tracks.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var actualPage = Math.Min(page, pageCount);
        var ordered = status == "recent"
            ? tracks.OrderByDescending(track => latest.Where(item => item.TrackId == track.Id)
                .Select(item => item.CompletedAtUnixMilliseconds).FirstOrDefault()).ThenBy(track => track.Id)
            : status == "progress"
                ? tracks.OrderByDescending(track => latest.Where(item => item.TrackId == track.Id)
                    .Select(item => (long?)item.StartedAtUnixMilliseconds).FirstOrDefault()).ThenBy(track => track.Id)
                : tracks.OrderBy(track => track.Artist).ThenBy(track => track.Album)
                    .ThenBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber)
                    .ThenBy(track => track.Id);
        var items = await ordered.Skip((actualPage - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResponse<TrackResponse>(await ToTrackResponsesAsync(items, cancellationToken), total, actualPage, pageSize);
    }

    public async Task<IReadOnlyList<TrackResponse>> GetRandomTracksAsync(
        bool albums, int count, CancellationToken cancellationToken)
    {
        if (!albums)
        {
            var tracks = await AvailableTracks().OrderBy(_ => EF.Functions.Random())
                .Take(count).ToListAsync(cancellationToken);
            return await ToTrackResponsesAsync(tracks, cancellationToken);
        }

        var albumsToPlay = await AvailableTracks()
            .Select(track => new
            {
                Artist = EF.Functions.Collate((track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist).Trim(), "NOCASE"),
                Album = EF.Functions.Collate(track.Album.Trim(), "NOCASE"),
            })
            .Distinct().OrderBy(_ => EF.Functions.Random()).Take(count).ToListAsync(cancellationToken);
        var result = new List<TrackResponse>();
        foreach (var album in albumsToPlay)
            result.AddRange(await GetAlbumTracksAsync(album.Artist, album.Album, cancellationToken));
        return result;
    }

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
        bool random = false,
        bool recent = false)
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
        var albumImportTimes = database.Tracks
            .Where(track => track.SourceRoot != null && track.SourceRoot.IsEnabled)
            .GroupBy(track => new
            {
                Album = EF.Functions.Collate(track.Album.Trim(), "NOCASE"),
                Artist = EF.Functions.Collate(
                    (track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist).Trim(),
                    "NOCASE"),
            })
            .Select(group => new
            {
                group.Key.Album,
                group.Key.Artist,
                ImportedAt = group.Min(track => track.ImportedAtUnixMilliseconds),
            });
        var albumsWithImportTimes =
            from album in albumGroups
            join time in albumImportTimes
                on new { album.Album, album.Artist } equals new { time.Album, time.Artist }
            select new
            {
                album.Album,
                album.Artist,
                album.ReleaseYear,
                album.TrackCount,
                album.DurationSeconds,
                album.CoverTrackId,
                time.ImportedAt,
            };
        var orderedAlbums = recent
            ? albumsWithImportTimes.OrderByDescending(item => item.ImportedAt)
                .ThenByDescending(item => item.CoverTrackId)
            : albumsWithImportTimes.OrderBy(item => item.Artist)
                .ThenBy(item => item.Album)
                .ThenBy(item => item.CoverTrackId);
        var albumPage = await orderedAlbums
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
        var albumNames = albumPage.Select(item => item.Album).Distinct().ToArray();
        var albumTrackFiles = await AvailableTracks()
            .Where(track => albumNames.Contains(EF.Functions.Collate(track.Album.Trim(), "NOCASE")))
            .Select(track => new
            {
                Album = EF.Functions.Collate(track.Album.Trim(), "NOCASE"),
                Artist = EF.Functions.Collate(
                    (track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist).Trim(),
                    "NOCASE"),
                track.RelativePath,
            })
            .ToListAsync(cancellationToken);
        var formatsByAlbum = albumTrackFiles
            .GroupBy(item => $"{item.Artist.ToUpperInvariant()}\0{item.Album.ToUpperInvariant()}")
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(item => Path.GetExtension(item.RelativePath).TrimStart('.').ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        var items = albumPage.Select(item => new AlbumSummaryResponse(
            item.Album,
            item.Artist,
            item.ReleaseYear == 0 ? null : item.ReleaseYear,
            item.TrackCount,
            item.DurationSeconds,
            item.CoverTrackId,
            $"{coversByTrackId[item.CoverTrackId].CoverUrl}&variant=tile",
            formatsByAlbum.GetValueOrDefault(
                $"{item.Artist.ToUpperInvariant()}\0{item.Album.ToUpperInvariant()}",
                Array.Empty<string>()),
            item.ImportedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(item.ImportedAt.Value) : null)).ToArray();
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
        var artistImportTimes = database.Tracks
            .Where(track => track.SourceRoot != null && track.SourceRoot.IsEnabled)
            .GroupBy(track => EF.Functions.Collate(
                track.AlbumArtist.Trim() == ""
                    ? (track.Artist.Trim() == "" ? "Unknown artist" : track.Artist.Trim())
                    : track.AlbumArtist.Trim(), "NOCASE"))
            .Select(group => new
            {
                Artist = group.Key,
                ImportedAt = group.Min(track => track.ImportedAtUnixMilliseconds),
            });
        var artistsWithImportTimes =
            from artist in artistGroups
            join time in artistImportTimes on artist.Artist equals time.Artist
            select new { artist.Artist, artist.TrackCount, artist.RepresentativeTrackId, time.ImportedAt };
        var artistPage = await artistsWithImportTimes
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
                IsArtistImageMissing(metadata),
                item.ImportedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(item.ImportedAt.Value) : null);
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
        string.IsNullOrWhiteSpace(track.Genre) ? track.PredictedGenre : track.Genre,
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
