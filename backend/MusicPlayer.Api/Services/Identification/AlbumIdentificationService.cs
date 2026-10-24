using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services.Identification;

public interface IAlbumIdentificationService
{
    IReadOnlyList<IdentifyProviderResponse> GetProviders();
    Task<AlbumSuggestionsResponse> SuggestAsync(AlbumSuggestionsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(AlbumSearchRequest request, CancellationToken cancellationToken);
    Task<AlbumMatchDetailResponse?> GetMatchAsync(string provider, string id, CancellationToken cancellationToken);
    Task<AlbumCoversResponse> GetCoverCandidatesAsync(AlbumCoversRequest request, CancellationToken cancellationToken);
    Task<(byte[] Data, string ContentType)?> ReadLocalImageAsync(int rootId, string relativePath, CancellationToken cancellationToken);
    Task<AlbumMovePlanResponse> PlanMoveAsync(AlbumMoveRequest request, CancellationToken cancellationToken);
    Task<AlbumMoveResponse> MoveAsync(AlbumMoveRequest request, CancellationToken cancellationToken);
}

public sealed partial class AlbumIdentificationService(
    MusicDbContext database,
    IEnumerable<IAlbumProvider> providers,
    MusicBrainzAlbumProvider musicBrainz,
    HttpClient http,
    IConfiguration configuration) : IAlbumIdentificationService
{
    private const long MaximumLocalImageSize = SafeImageFetcher.MaximumImageSize;
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public IReadOnlyList<IdentifyProviderResponse> GetProviders() =>
        providers.Select(provider => new IdentifyProviderResponse(provider.Id, provider.Name, provider.IsConfigured)).ToArray();

    public async Task<AlbumSuggestionsResponse> SuggestAsync(AlbumSuggestionsRequest request, CancellationToken cancellationToken)
    {
        var provider = GetProvider(request.Provider);
        var tracks = await LoadTracksAsync(request.TrackIds, cancellationToken);
        var tagArtist = MostCommon(tracks.Select(track => ProviderText.Nullable(track.AlbumArtist) ?? ProviderText.Nullable(track.Artist)))
            is { } artist && !artist.StartsWith("Unknown artist", StringComparison.OrdinalIgnoreCase) ? artist : null;
        var tagAlbum = MostCommon(tracks.Select(track => ProviderText.Nullable(track.Album)));
        if (tagAlbum is not null && tagAlbum.StartsWith("Unknown album", StringComparison.OrdinalIgnoreCase)) tagAlbum = null;
        var yearValue = MostCommon(tracks.Where(track => track.Year > 0).Select(track => track.Year.ToString()));
        int? tagYear = int.TryParse(yearValue, out var parsedYear) ? parsedYear : null;

        var queries = new List<(string? Artist, string? Album, int? Year)>();
        if (tagAlbum is not null) queries.Add((tagArtist, tagAlbum, tagYear));

        var directory = tracks[0].Folder?.RelativePath ?? string.Empty;
        var segments = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
        {
            var (folderYear, folderTitle) = ParseFolderName(segments[^1]);
            var folderArtist = segments.Length > 1 ? segments[^2] : tagArtist;
            if (!string.IsNullOrWhiteSpace(folderTitle)
                && !queries.Any(query => string.Equals(query.Album, folderTitle, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(query.Artist, folderArtist, StringComparison.OrdinalIgnoreCase)))
            {
                queries.Add((folderArtist, folderTitle, folderYear ?? tagYear));
            }
        }

        var matches = new List<AlbumMatchResponse>();
        foreach (var query in queries)
        {
            matches.AddRange(await provider.SearchAsync(query.Artist, query.Album, query.Year, cancellationToken));
        }

        var ranked = matches
            .DistinctBy(match => $"{match.Provider}:{match.Id}")
            .OrderByDescending(match => tagYear.HasValue && match.Year == tagYear ? 1 : 0)
            .ThenByDescending(match => tagAlbum is not null && string.Equals(match.Title, tagAlbum, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Take(15)
            .ToArray();
        return new AlbumSuggestionsResponse(tagArtist, tagAlbum, tagYear, ranked);
    }

    public async Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(AlbumSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Artist) && string.IsNullOrWhiteSpace(request.Album) && request.Year is null)
        {
            throw new ArgumentException("Enter an artist, album title or release year to search.");
        }

        return await GetProvider(request.Provider).SearchAsync(
            ProviderText.Nullable(request.Artist), ProviderText.Nullable(request.Album), request.Year, cancellationToken);
    }

    public Task<AlbumMatchDetailResponse?> GetMatchAsync(string provider, string id, CancellationToken cancellationToken) =>
        GetProvider(provider).GetAsync(id, cancellationToken);

    public async Task<AlbumCoversResponse> GetCoverCandidatesAsync(AlbumCoversRequest request, CancellationToken cancellationToken)
    {
        var tracks = await LoadTracksAsync(request.TrackIds, cancellationToken);
        var candidates = new List<CoverCandidateResponse>();
        var warnings = new List<string>();

        foreach (var image in FindLocalImages(tracks))
        {
            candidates.Add(new CoverCandidateResponse(
                $"local:{image.RootId}:{image.RelativePath}",
                "Album folder",
                Path.GetFileName(image.RelativePath),
                $"/api/admin/identify/local-image?rootId={image.RootId}&path={Uri.EscapeDataString(image.RelativePath)}"));
        }

        candidates.Add(new CoverCandidateResponse(
            "embedded", "Current cover", "Current album cover", $"/api/library/tracks/{tracks[0].Id}/cover"));

        string? releaseGroupId = null;
        if (!string.IsNullOrWhiteSpace(request.MatchId))
        {
            var provider = GetProvider(request.Provider);
            AlbumMatchDetailResponse? detail = null;
            try
            {
                detail = await provider.GetAsync(request.MatchId, cancellationToken);
            }
            catch (AlbumProviderException exception)
            {
                warnings.Add(exception.Message);
            }

            if (detail is not null)
            {
                if (!string.IsNullOrWhiteSpace(detail.Match.CoverUrl))
                {
                    candidates.Add(RemoteCandidate("provider", provider.Name, $"{provider.Name} cover", detail.Match.CoverUrl));
                }

                releaseGroupId = detail.MusicBrainzReleaseGroupId;
                if (releaseGroupId is null && provider.Id != musicBrainz.Id)
                {
                    try
                    {
                        releaseGroupId = await musicBrainz.FindReleaseGroupIdAsync(
                            detail.Match.Artist, detail.Match.Title, cancellationToken);
                    }
                    catch (AlbumProviderException exception)
                    {
                        warnings.Add(exception.Message);
                    }
                }
            }
        }

        if (releaseGroupId is not null)
        {
            candidates.Add(RemoteCandidate(
                "caa", "Cover Art Archive", "Cover Art Archive front cover",
                $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500"));
            var apiKey = configuration["FanartTv:ApiKey"] ?? configuration["FanartTv:ClientKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                warnings.Add("fanart.tv covers are unavailable because FanartTv:ApiKey is not configured.");
            }
            else
            {
                try
                {
                    foreach (var url in await GetFanartCoversAsync(releaseGroupId, apiKey, cancellationToken))
                    {
                        candidates.Add(RemoteCandidate("fanart", "fanart.tv", "fanart.tv album cover", url));
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    warnings.Add("fanart.tv could not be reached.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.MatchId))
        {
            warnings.Add("No MusicBrainz release group was found, so fanart.tv covers are unavailable.");
        }

        return new AlbumCoversResponse(candidates, warnings);
    }

    public async Task<(byte[] Data, string ContentType)?> ReadLocalImageAsync(
        int rootId, string relativePath, CancellationToken cancellationToken)
    {
        var root = await database.SourceRoots.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == rootId && item.IsEnabled, cancellationToken);
        if (root is null || !ImageExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string path;
        try
        {
            path = ResolvePath(root.Path, relativePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaximumLocalImageSize || HasReparsePoint(file, root.Path))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        var contentType = ImageSniffer.DetectContentType(data);
        return contentType is null ? null : (data, contentType);
    }

    public async Task<AlbumMovePlanResponse> PlanMoveAsync(AlbumMoveRequest request, CancellationToken cancellationToken) =>
        (await BuildMovePlanAsync(request, cancellationToken)).Response;

    public async Task<AlbumMoveResponse> MoveAsync(AlbumMoveRequest request, CancellationToken cancellationToken)
    {
        var plan = await BuildMovePlanAsync(request, cancellationToken);
        if (!plan.Response.CanMove || plan.Context is not { } context)
        {
            throw new InvalidOperationException(plan.Response.Reason ?? "The album cannot be moved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(context.DestinationPath)!);
        MoveDirectory(context.SourcePath, context.DestinationPath);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var folders = await database.Folders
                .Where(folder => folder.SourceRootId == context.DestinationRoot.Id)
                .ToDictionaryAsync(folder => folder.RelativePath, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var oldFolderIds = context.Tracks.Select(track => track.FolderId).Distinct().ToArray();
            foreach (var track in context.Tracks)
            {
                var remainder = track.RelativePath[(context.SourceRelative.Length + 1)..];
                track.SourceRootId = context.DestinationRoot.Id;
                track.RelativePath = $"{context.DestinationRelative}/{remainder}";
                var folderPath = track.RelativePath.Contains('/') ? track.RelativePath[..track.RelativePath.LastIndexOf('/')] : string.Empty;
                if (!folders.TryGetValue(folderPath, out var folder))
                {
                    folder = new MusicFolder
                    {
                        SourceRootId = context.DestinationRoot.Id,
                        RelativePath = folderPath,
                        Name = Path.GetFileName(folderPath),
                    };
                    database.Folders.Add(folder);
                    await database.SaveChangesAsync(cancellationToken);
                    folders.Add(folderPath, folder);
                }

                track.FolderId = folder.Id;
            }

            await database.SaveChangesAsync(cancellationToken);
            var stale = await database.Folders
                .Where(folder => oldFolderIds.Contains(folder.Id) && !database.Tracks.Any(track => track.FolderId == folder.Id))
                .ToListAsync(cancellationToken);
            database.Folders.RemoveRange(stale);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            database.ChangeTracker.Clear();
            try
            {
                MoveDirectory(context.DestinationPath, context.SourcePath);
            }
            catch (Exception)
            {
                // The original failure is more useful than a failed rollback.
            }

            throw;
        }

        return new AlbumMoveResponse(context.DestinationPath, context.Tracks.Count);
    }

    private async Task<(AlbumMovePlanResponse Response, MoveContext? Context)> BuildMovePlanAsync(
        AlbumMoveRequest request, CancellationToken cancellationToken)
    {
        var tracks = await LoadTracksAsync(request.TrackIds, cancellationToken);
        var destinationRoot = await database.SourceRoots
            .FirstOrDefaultAsync(root => root.Id == request.DestinationRootId && root.IsEnabled, cancellationToken)
            ?? throw new ArgumentException("The destination source root was not found.");

        var artistName = SanitizeSegment(request.Artist);
        var albumName = SanitizeSegment(request.Album);
        var folderName = request.Year is > 0 ? $"{request.Year} - {albumName}" : albumName;
        var destinationRelative = $"{artistName}/{folderName}";
        var destinationPath = ResolvePath(destinationRoot.Path, destinationRelative);

        AlbumMovePlanResponse Refuse(string reason, string source = "") => new(false, reason, source, destinationPath);

        var firstRoot = tracks[0].SourceRoot!;
        if (tracks.Any(track => track.SourceRootId != firstRoot.Id))
        {
            return (Refuse("The tracks are stored in different source roots."), null);
        }

        var directories = tracks.Select(track => track.Folder!.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceRelative = CommonDirectory(directories);
        if (sourceRelative.Length == 0)
        {
            return (Refuse("The album is stored directly in the root of the library, so its folder cannot be moved."), null);
        }

        var sourcePath = ResolvePath(firstRoot.Path, sourceRelative);
        if (!Directory.Exists(sourcePath))
        {
            return (Refuse("The album folder no longer exists on disk.", sourcePath), null);
        }

        var prefix = sourceRelative + "/";
        var catalogCount = await database.Tracks.CountAsync(
            track => track.SourceRootId == firstRoot.Id && track.RelativePath.StartsWith(prefix), cancellationToken);
        if (catalogCount != tracks.Count)
        {
            return (Refuse("The album folder contains other tracks that are not part of this album.", sourcePath), null);
        }

        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            return (Refuse("The album is already stored at this location.", sourcePath), null);
        }

        if (IsWithin(destinationPath, sourcePath) || IsWithin(sourcePath, destinationPath))
        {
            return (Refuse("The destination overlaps the current album folder.", sourcePath), null);
        }

        if (ContainsReparsePoint(sourcePath))
        {
            return (Refuse("The album folder contains links and cannot be moved safely.", sourcePath), null);
        }

        var suffix = 2;
        var finalRelative = destinationRelative;
        while (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            finalRelative = $"{destinationRelative} ({suffix++})";
            destinationPath = ResolvePath(destinationRoot.Path, finalRelative);
        }

        var response = new AlbumMovePlanResponse(true, null, sourcePath, destinationPath);
        return (response, new MoveContext(tracks, destinationRoot, sourcePath, destinationPath, sourceRelative, finalRelative));
    }

    private static bool MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return false;
        }
        catch (IOException) when (!Path.GetPathRoot(Path.GetFullPath(source))!
            .Equals(Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(source, destination);
            Directory.Delete(source, recursive: true);
            return true;
        }
    }

    internal static void CopyDirectory(string source, string destination)
    {
        var stagingDirectory = $"{destination}.copying-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(stagingDirectory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                if (new FileInfo(file).Length != new FileInfo(target).Length)
                {
                    throw new IOException("A copied file does not match its source.");
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(stagingDirectory, Path.GetRelativePath(source, directory)));
            }

            Directory.Move(stagingDirectory, destination);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private static string CommonDirectory(IReadOnlyList<string> directories)
    {
        var common = directories[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in directories.Skip(1))
        {
            var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var length = 0;
            while (length < common.Length && length < parts.Length
                && string.Equals(common[length], parts[length], StringComparison.OrdinalIgnoreCase))
            {
                length++;
            }

            common = common[..length];
        }

        return string.Join('/', common);
    }

    private IEnumerable<(int RootId, string RelativePath)> FindLocalImages(IReadOnlyList<MusicTrack> tracks)
    {
        var results = new List<(int, string)>();
        foreach (var group in tracks.GroupBy(track => track.SourceRootId))
        {
            var root = group.First().SourceRoot!;
            var directories = group.Select(track => track.Folder!.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var common = CommonDirectory(directories);
            var searchDirectories = common.Length == 0 ? directories : [common];
            var depth = common.Length == 0 ? 0 : 2;
            foreach (var relative in searchDirectories)
            {
                string directory;
                try
                {
                    directory = ResolvePath(root.Path, relative);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (!Directory.Exists(directory)) continue;
                foreach (var file in EnumerateImages(directory, depth))
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaximumLocalImageSize || HasReparsePoint(info, root.Path)) continue;
                    results.Add((root.Id, NormalizeRelative(Path.GetRelativePath(root.Path, file))));
                    if (results.Count >= 40) return results;
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateImages(string directory, int depth)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files) yield return file;
        if (depth <= 0) yield break;
        string[] children;
        try
        {
            children = Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var file in EnumerateImages(child, depth - 1)) yield return file;
        }
    }

    private async Task<IReadOnlyList<string>> GetFanartCoversAsync(string releaseGroupId, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://webservice.fanart.tv/v3/music/albums/{Uri.EscapeDataString(releaseGroupId)}?api_key={Uri.EscapeDataString(apiKey)}");
        request.Headers.UserAgent.ParseAdd(ProviderText.UserAgent);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var urls = new List<string>();
        if (document.RootElement.GetPropertyOrNull("albums")?.GetPropertyOrNull(releaseGroupId)?.GetPropertyOrNull("albumcover") is
            { ValueKind: System.Text.Json.JsonValueKind.Array } covers)
        {
            foreach (var cover in covers.EnumerateArray())
            {
                if (cover.GetStringOrNull("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                {
                    urls.Add(url);
                }
            }
        }

        return urls.Take(12).ToArray();
    }

    private static CoverCandidateResponse RemoteCandidate(string id, string source, string label, string url) =>
        new($"{id}:{url}", source, label, $"/api/admin/identify/image?url={Uri.EscapeDataString(url)}");

    private async Task<List<MusicTrack>> LoadTracksAsync(int[] ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        var tracks = await database.Tracks
            .Include(item => item.SourceRoot)
            .Include(item => item.Folder)
            .Where(item => distinct.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (tracks.Count == 0 || tracks.Count != distinct.Length)
        {
            throw new KeyNotFoundException("One or more tracks were not found.");
        }

        return tracks;
    }

    private IAlbumProvider GetProvider(string id) =>
        providers.FirstOrDefault(provider => provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            is { IsConfigured: true } provider
            ? provider
            : throw new ArgumentException("The selected identification provider is unknown or not configured.");

    private static string? MostCommon(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

    internal static (int? Year, string Title) ParseFolderName(string name)
    {
        var match = FolderYearPattern().Match(name.Trim());
        return match.Success
            ? (int.Parse(match.Groups["year"].Value), match.Groups["title"].Value.Trim())
            : (null, name.Trim());
    }

    [GeneratedRegex(@"^[\(\[]?(?<year>(19|20)\d{2})[\)\]]?\s*[-â€“._]\s*(?<title>.+)$")]
    private static partial Regex FolderYearPattern();

    internal static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        var safe = new string(value.Trim().Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd('.', ' ');
        if (safe.Length > 120) safe = safe[..120].TrimEnd('.', ' ');
        var stem = safe.Split('.')[0].Trim();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return string.IsNullOrWhiteSpace(safe) ? "Unknown" : safe;
    }

    private static bool ContainsReparsePoint(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Any(entry => File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint));

    private static bool HasReparsePoint(FileInfo file, string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        for (var directory = file.Directory; directory is not null && IsWithin(directory.FullName, root)
            && !directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }

        return file.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsWithin(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePath(string rootPath, string relativePath)
    {
        var root = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(fullPath, root))
        {
            throw new InvalidOperationException("The path escapes its configured source root.");
        }

        return fullPath;
    }

    private static string NormalizeRelative(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record MoveContext(
        List<MusicTrack> Tracks,
        MusicSourceRoot DestinationRoot,
        string SourcePath,
        string DestinationPath,
        string SourceRelative,
        string DestinationRelative);
}
