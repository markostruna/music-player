using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Cryptography;
using System.Text;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using TagLib;

namespace MusicPlayer.Api.Services;

public sealed class MusicLibraryService(
    MusicDbContext database,
    IWebHostEnvironment environment,
    IMusicBrainzClient musicBrainz,
    ILogger<MusicLibraryService> logger) : IMusicLibraryService
{
    private const int MaximumCoverFileSize = 10 * 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav",
    };

    private static readonly string[] CoverFileNames =
    [
        "Folder.jpg", "folder.jpg", "Folder.jpeg", "folder.jpeg", "Folder.png", "folder.png", "Folder.webp", "folder.webp", "Folder.gif", "folder.gif",
        "cover.jpg", "Cover.jpg", "cover.jpeg", "Cover.jpeg", "cover.png", "Cover.png", "cover.webp", "Cover.webp", "cover.gif", "Cover.gif",
        "album.jpg", "Album.jpg", "album.jpeg", "Album.jpeg", "album.png", "Album.png", "album.webp", "Album.webp", "album.gif", "Album.gif",
        "front.jpg", "Front.jpg", "front.jpeg", "Front.jpeg", "front.png", "Front.png", "front.webp", "Front.webp", "front.gif", "Front.gif",
        "AlbumArt.jpg", "AlbumArtSmall.jpg",
    ];

    public async Task<IReadOnlyList<SourceRootResponse>> GetRootsAsync(CancellationToken cancellationToken) =>
        await database.SourceRoots.AsNoTracking()
            .OrderBy(root => root.Name)
            .Select(root => new SourceRootResponse(
                root.Id,
                root.Name,
                root.Path,
                root.Tracks.Count(track => track.IsAvailable),
                root.IsEnabled))
            .ToListAsync(cancellationToken);

    public async Task<SourceRootResponse> AddRootAsync(CreateSourceRootRequest request, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(request.Path.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException("The source folder does not exist.");
        }

        var attributes = System.IO.File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("A source folder cannot be a symbolic link or junction.");
        }

        EnsureOutsideWebRoot(fullPath);
        if (await database.SourceRoots.AnyAsync(root => root.Path.ToUpper() == fullPath.ToUpper(), cancellationToken))
        {
            throw new InvalidOperationException("This source folder is already configured.");
        }

        var root = new MusicSourceRoot { Name = request.Name.Trim(), Path = Path.TrimEndingDirectorySeparator(fullPath) };
        database.SourceRoots.Add(root);
        await database.SaveChangesAsync(cancellationToken);
        return new SourceRootResponse(root.Id, root.Name, root.Path, 0, root.IsEnabled);
    }

    public async Task<bool> RemoveRootAsync(int rootId, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Tracks.Where(track => track.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken);
        await database.Folders.Where(folder => folder.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken);
        var removedRoots = await database.SourceRoots.Where(source => source.Id == rootId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removedRoots > 0;
    }

    public async Task<IReadOnlyList<MusicFolderResponse>> GetFoldersAsync(CancellationToken cancellationToken) =>
        await database.Folders.AsNoTracking()
            .OrderBy(folder => folder.SourceRootId)
            .ThenBy(folder => folder.RelativePath)
            .Select(folder => new MusicFolderResponse(folder.Id, folder.SourceRootId, folder.Name, folder.RelativePath))
            .ToListAsync(cancellationToken);

    public async Task<ScanResponse> ScanAsync(int rootId, CancellationToken cancellationToken)
    {
        var root = await database.SourceRoots.SingleOrDefaultAsync(item => item.Id == rootId && item.IsEnabled, cancellationToken)
            ?? throw new KeyNotFoundException("The source root was not found.");
        var rootPath = Path.GetFullPath(root.Path);
        if (!Directory.Exists(rootPath) || (System.IO.File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured source folder is unavailable or unsafe.");
        }

        var folders = await database.Folders.Where(folder => folder.SourceRootId == rootId).ToListAsync(cancellationToken);
        var foldersByPath = folders.ToDictionary(folder => folder.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (!foldersByPath.ContainsKey(string.Empty))
        {
            var rootFolder = new MusicFolder { SourceRootId = rootId, RelativePath = string.Empty, Name = root.Name };
            database.Folders.Add(rootFolder);
            await database.SaveChangesAsync(cancellationToken);
            foldersByPath.Add(string.Empty, rootFolder);
        }

        var tracks = await database.Tracks.Where(track => track.SourceRootId == rootId).ToListAsync(cancellationToken);
        var tracksByPath = tracks.ToDictionary(track => track.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            track.IsAvailable = false;
        }

        var discovered = 0;
        var unreadable = 0;
        foreach (var filePath in EnumerateFiles(rootPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
            var relativeFolder = NormalizeRelativePath(Path.GetDirectoryName(Path.GetRelativePath(rootPath, filePath)) ?? string.Empty);
            if (!foldersByPath.TryGetValue(relativeFolder, out var folder))
            {
                folder = new MusicFolder
                {
                    SourceRootId = rootId,
                    RelativePath = relativeFolder,
                    Name = Path.GetFileName(relativeFolder),
                };
                database.Folders.Add(folder);
                await database.SaveChangesAsync(cancellationToken);
                foldersByPath.Add(relativeFolder, folder);
            }

            var fileInfo = new FileInfo(filePath);
            MusicTrack track;
            try
            {
                using var audio = TagLib.File.Create(filePath);
                var tag = audio.Tag;
                track = tracksByPath.TryGetValue(relativePath, out var existing)
                    ? existing
                    : new MusicTrack { SourceRootId = rootId, RelativePath = relativePath, Title = string.Empty };
                track.FolderId = folder.Id;
                track.Title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(filePath) : tag.Title.Trim();
                track.Artist = JoinArtistTags(tag.Performers, "Unknown artist");
                track.Album = string.IsNullOrWhiteSpace(tag.Album) ? folder.Name : tag.Album.Trim();
                track.AlbumArtist = JoinArtistTags(tag.AlbumArtists, string.Empty);
                track.Genre = FirstTag(tag.Genres, string.Empty);
                track.TrackNumber = tag.Track;
                track.DiscNumber = tag.Disc;
                track.Year = tag.Year;
                track.DurationSeconds = Math.Max(0, audio.Properties.Duration.TotalSeconds);
                track.FileSize = fileInfo.Length;
                track.LastWriteTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
                track.ScannedAt = DateTimeOffset.UtcNow;
                track.IsAvailable = true;
                if (!tracksByPath.ContainsKey(relativePath))
                {
                    database.Tracks.Add(track);
                    tracksByPath.Add(relativePath, track);
                }

                discovered++;
            }
            catch (Exception exception) when (exception is TagLib.CorruptFileException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                unreadable++;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return new ScanResponse(rootId, discovered, unreadable);
    }

    public async Task<RootMetadataRefreshResponse> RefreshRootMetadataAsync(
        int rootId,
        Action<int, int, string> reportProgress,
        CancellationToken cancellationToken)
    {
        var rootExists = await database.SourceRoots.AnyAsync(
            root => root.Id == rootId && root.IsEnabled,
            cancellationToken);
        if (!rootExists)
        {
            throw new KeyNotFoundException("The source root was not found.");
        }

        var tracks = await database.Tracks.AsNoTracking()
            .Where(track => track.SourceRootId == rootId && track.IsAvailable)
            .Select(track => new { track.Artist, track.AlbumArtist, track.Album })
            .ToListAsync(cancellationToken);

        var artists = tracks
            .SelectMany(track => new[] { track.Artist.Trim(), track.AlbumArtist.Trim() })
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(artist => artist, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var albums = new List<(string Artist, string Album)>();
        var albumKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var album = track.Album.Trim();
            var artist = string.IsNullOrWhiteSpace(track.AlbumArtist)
                ? track.Artist.Trim()
                : track.AlbumArtist.Trim();
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)
                || !albumKeys.Add($"{artist}\0{album}"))
            {
                continue;
            }

            albums.Add((artist, album));
        }

        var total = artists.Length + albums.Count;
        var completed = 0;
        var artistImagesUpdated = 0;
        var albumImagesUpdated = 0;
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RefreshArtistMetadataAsync(artist, cancellationToken);
            if (result.ImageUpdated)
            {
                artistImagesUpdated++;
            }

            completed++;
            reportProgress(completed, total, $"Processed artist {artist}");
        }

        foreach (var album in albums)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RefreshAlbumMetadataAsync(album.Artist, album.Album, cancellationToken);
            if (result.ImageUpdated)
            {
                albumImagesUpdated++;
            }

            completed++;
            reportProgress(completed, total, $"Processed album {album.Album}");
        }

        return new RootMetadataRefreshResponse(
            rootId,
            artists.Length,
            albums.Count,
            artistImagesUpdated,
            albumImagesUpdated);
    }

    public async Task<IReadOnlyList<TrackResponse>> GetTracksAsync(CancellationToken cancellationToken)
    {
        var tracks = await database.Tracks.AsNoTracking()
            .Where(track => track.IsAvailable && track.SourceRoot != null && track.SourceRoot.IsEnabled)
            .OrderBy(track => track.AlbumArtist)
            .ThenBy(track => track.Album)
            .ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title)
            .ToListAsync(cancellationToken);
        var artistMetadata = await database.ArtistMetadata.AsNoTracking().ToDictionaryAsync(item => item.Key, cancellationToken);
        var albumMetadata = await database.AlbumMetadata.AsNoTracking().ToDictionaryAsync(item => item.Key, cancellationToken);
        return tracks.Select(track =>
        {
            var artistKey = GetArtistMetadataKey(track.Artist);
            var albumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
            var albumKey = GetAlbumMetadataKey(albumArtist, track.Album);
            artistMetadata.TryGetValue(artistKey, out var artist);
            var albumArtistKey = GetArtistMetadataKey(albumArtist);
            artistMetadata.TryGetValue(albumArtistKey, out var albumArtistInfo);
            albumMetadata.TryGetValue(albumKey, out var album);
            return ToResponse(track, artist, albumArtistInfo, album);
        }).ToList();
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

    public async Task<ResolvedMedia?> ResolveStreamAsync(int trackId, CancellationToken cancellationToken)
    {
        var track = await database.Tracks.AsNoTracking()
            .Include(item => item.SourceRoot)
            .SingleOrDefaultAsync(item => item.Id == trackId && item.IsAvailable && item.SourceRoot != null && item.SourceRoot.IsEnabled, cancellationToken);
        if (track?.SourceRoot is null)
        {
            return null;
        }

        var path = ResolvePath(track.SourceRoot.Path, track.RelativePath);
        if (!System.IO.File.Exists(path) || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        return new ResolvedMedia(path, GetContentType(path));
    }

    public async Task<CoverImage?> ResolveCoverAsync(int trackId, CancellationToken cancellationToken)
    {
        var track = await database.Tracks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is not null)
        {
            var albumKey = GetAlbumMetadataKey(
                string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist,
                track.Album);
            var metadata = await database.AlbumMetadata.AsNoTracking().SingleOrDefaultAsync(item => item.Key == albumKey, cancellationToken);
            var customImage = metadata is null
                ? null
                : metadata.ImageSourceRootId.HasValue
                    ? await ReadLibraryImageAsync(metadata.ImageSourceRootId, metadata.ImageRelativePath, cancellationToken)
                    : await ReadMetadataImageAsync(metadata.ImageFileName, "albums", cancellationToken);
            if (customImage is not null)
            {
                return customImage;
            }
        }

        var media = await ResolveStreamAsync(trackId, cancellationToken);
        if (media is null)
        {
            return null;
        }

        var folderCover = await ResolveFolderCoverAsync(Path.GetDirectoryName(media.FullPath)!, cancellationToken);
        if (folderCover is not null)
        {
            return folderCover;
        }

        using var audio = TagLib.File.Create(media.FullPath);
        var picture = audio.Tag.Pictures.FirstOrDefault(item => item.Type == PictureType.FrontCover) ?? audio.Tag.Pictures.FirstOrDefault();
        if (picture is null || picture.Data.Count == 0)
        {
            return null;
        }

        return new CoverImage(picture.Data.Data, picture.MimeType);
    }

    public async Task<CoverImage?> ResolveArtistImageAsync(string key, CancellationToken cancellationToken)
    {
        var metadata = await database.ArtistMetadata.AsNoTracking().SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        return metadata is null
            ? null
            : metadata.ImageSourceRootId.HasValue
                ? await ReadLibraryImageAsync(metadata.ImageSourceRootId, metadata.ImageRelativePath, cancellationToken)
                : await ReadMetadataImageAsync(metadata.ImageFileName, "artists", cancellationToken);
    }

    public async Task<MetadataRefreshResponse> RefreshArtistMetadataAsync(string artist, CancellationToken cancellationToken)
    {
        var name = artist.Trim();
        var result = await musicBrainz.FindArtistAsync(name, cancellationToken);
        var key = GetArtistMetadataKey(name);
        var metadata = await database.ArtistMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (result is null)
        {
            return metadata is null
                ? new MetadataRefreshResponse(false, "", "", null, true, DateTimeOffset.UtcNow)
                : new MetadataRefreshResponse(
                    false,
                    metadata.MusicBrainzId,
                    metadata.Description,
                    GetArtistImageUrl(metadata),
                    metadata.ImageMissing || string.IsNullOrWhiteSpace(metadata.ImageFileName),
                    metadata.UpdatedAt);
        }

        if (metadata is null)
        {
            metadata = new MusicArtistMetadata { Key = key, Artist = name };
            database.ArtistMetadata.Add(metadata);
        }
        var oldImageFileName = metadata.ImageFileName;
        var oldImageRootId = metadata.ImageSourceRootId;
        var oldImageRelativePath = metadata.ImageRelativePath;

        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            metadata.Description = result.Description;
            metadata.DescriptionEdited = false;
        }

        metadata.MusicBrainzId = result.MusicBrainzId;
        metadata.UpdatedAt = DateTimeOffset.UtcNow;
        var imageUpdated = false;
        string? imageProvider = null;
        if (result.Image is not null && result.ImageContentType is not null)
        {
            var destination = await FindArtistImageDestinationAsync(name, cancellationToken);
            if (destination is null)
            {
                logger.LogWarning("No music folder could be found for artist image {Artist}.", name);
            }
            else
            {
                var imageFileName = await SaveLookupImageAsync(
                    result.Image,
                    result.ImageContentType,
                    destination,
                    destination.IsArtistDirectory ? "ArtistCover-MusicBrainz" : $"ArtistCover-{SanitizeFileName(name)}-MusicBrainz",
                    cancellationToken);
                if (imageFileName is not null)
                {
                    metadata.ImageFileName = imageFileName;
                    metadata.ImageSourceRootId = destination.SourceRootId;
                    metadata.ImageRelativePath = NormalizeRelativePath(Path.Combine(destination.RelativeDirectory, imageFileName));
                    metadata.ImageMissing = false;
                    imageUpdated = true;
                    imageProvider = result.ImageProvider;
                }
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        DeleteReplacedImage(oldImageFileName, oldImageRootId, oldImageRelativePath, metadata.ImageFileName,
            metadata.ImageSourceRootId, metadata.ImageRelativePath, "artists");
        return new MetadataRefreshResponse(
            true,
            metadata.MusicBrainzId,
            metadata.Description,
            GetArtistImageUrl(metadata),
            metadata.ImageMissing || string.IsNullOrWhiteSpace(metadata.ImageFileName),
            metadata.UpdatedAt,
            imageUpdated,
            imageProvider);
    }

    public async Task<MetadataRefreshResponse> RefreshAlbumMetadataAsync(string artist, string album, CancellationToken cancellationToken)
    {
        var artistName = artist.Trim();
        var albumName = album.Trim();
        var result = await musicBrainz.FindAlbumAsync(artistName, albumName, cancellationToken);
        var key = GetAlbumMetadataKey(artistName, albumName);
        var metadata = await database.AlbumMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (result is null)
        {
            var existingTrackId = await FindAlbumCoverTrackIdAsync(artistName, albumName, cancellationToken);
            return metadata is null
                ? new MetadataRefreshResponse(false, "", "", existingTrackId is null ? null : GetAlbumImageUrl(existingTrackId.Value), true, DateTimeOffset.UtcNow)
                : new MetadataRefreshResponse(
                    false,
                    metadata.MusicBrainzId,
                    metadata.Description,
                    existingTrackId is null ? null : GetAlbumImageUrl(existingTrackId.Value, metadata.UpdatedAt),
                    metadata.ImageMissing,
                    metadata.UpdatedAt);
        }

        if (metadata is null)
        {
            metadata = new MusicAlbumMetadata { Key = key, Artist = artistName, Album = albumName };
            database.AlbumMetadata.Add(metadata);
        }
        var oldImageFileName = metadata.ImageFileName;
        var oldImageRootId = metadata.ImageSourceRootId;
        var oldImageRelativePath = metadata.ImageRelativePath;

        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            metadata.Description = result.Description;
            metadata.DescriptionEdited = false;
        }

        metadata.MusicBrainzId = result.MusicBrainzId;
        metadata.UpdatedAt = DateTimeOffset.UtcNow;
        var imageUpdated = false;
        string? imageProvider = null;
        if (result.Image is not null && result.ImageContentType is not null)
        {
            var destination = await FindAlbumImageDestinationAsync(artistName, albumName, cancellationToken);
            if (destination is null)
            {
                logger.LogWarning("No music folder could be found for album image {Album} by {Artist}.", albumName, artistName);
            }
            else
            {
                var imageFileName = await SaveLookupImageAsync(
                    result.Image, result.ImageContentType, destination, "Cover-MusicBrainz", cancellationToken);
                if (imageFileName is not null)
                {
                    metadata.ImageFileName = imageFileName;
                    metadata.ImageSourceRootId = destination.SourceRootId;
                    metadata.ImageRelativePath = NormalizeRelativePath(Path.Combine(destination.RelativeDirectory, imageFileName));
                    metadata.ImageMissing = false;
                    imageUpdated = true;
                    imageProvider = result.ImageProvider;
                }
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        DeleteReplacedImage(oldImageFileName, oldImageRootId, oldImageRelativePath, metadata.ImageFileName,
            metadata.ImageSourceRootId, metadata.ImageRelativePath, "albums");
        var trackId = await FindAlbumCoverTrackIdAsync(artistName, albumName, cancellationToken);
        return new MetadataRefreshResponse(
            true,
            metadata.MusicBrainzId,
            metadata.Description,
            trackId is null ? null : GetAlbumImageUrl(trackId.Value, metadata.UpdatedAt),
            metadata.ImageMissing,
            metadata.UpdatedAt,
            imageUpdated,
            imageProvider);
    }

    public async Task UpdateMetadataDescriptionAsync(
        MetadataDescriptionRequest request,
        bool album,
        CancellationToken cancellationToken)
    {
        if (album)
        {
            var key = GetAlbumMetadataKey(request.Artist, request.Album);
            var metadata = await database.AlbumMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
            if (metadata is null)
            {
                metadata = new MusicAlbumMetadata { Key = key, Artist = request.Artist.Trim(), Album = request.Album.Trim() };
                database.AlbumMetadata.Add(metadata);
            }

            metadata.Description = request.Description.Trim();
            metadata.DescriptionEdited = true;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            var key = GetArtistMetadataKey(request.Artist);
            var metadata = await database.ArtistMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
            if (metadata is null)
            {
                metadata = new MusicArtistMetadata { Key = key, Artist = request.Artist.Trim() };
                database.ArtistMetadata.Add(metadata);
            }

            metadata.Description = request.Description.Trim();
            metadata.DescriptionEdited = true;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMetadataImageAsync(
        MetadataImageRequest request,
        bool album,
        CancellationToken cancellationToken)
    {
        var contentType = DetectImageContentType(request.Image);
        if (contentType is null || request.Image.Length > MaximumCoverFileSize)
        {
            throw new ArgumentException("The image must be a supported PNG, JPEG, GIF, or WebP file under 10 MB.");
        }

        var key = album ? GetAlbumMetadataKey(request.Artist, request.Album) : GetArtistMetadataKey(request.Artist);
        var extension = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => throw new ArgumentException("The image format is not supported."),
        };
        var newFileName = $"{key}-{Guid.NewGuid():N}{extension}";
        var directory = Path.Combine(GetMetadataDirectory(), album ? "albums" : "artists");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, newFileName);
        await System.IO.File.WriteAllBytesAsync(imagePath, request.Image, cancellationToken);

        string? oldFileName;
        int? oldImageRootId;
        string? oldImageRelativePath;
        try
        {
            if (album)
            {
                var metadata = await database.AlbumMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
                if (metadata is null)
                {
                    metadata = new MusicAlbumMetadata { Key = key, Artist = request.Artist.Trim(), Album = request.Album.Trim() };
                    database.AlbumMetadata.Add(metadata);
                }

                oldFileName = metadata.ImageFileName;
                oldImageRootId = metadata.ImageSourceRootId;
                oldImageRelativePath = metadata.ImageRelativePath;
                metadata.ImageFileName = newFileName;
                metadata.ImageSourceRootId = null;
                metadata.ImageRelativePath = null;
                metadata.ImageMissing = false;
                metadata.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var metadata = await database.ArtistMetadata.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
                if (metadata is null)
                {
                    metadata = new MusicArtistMetadata { Key = key, Artist = request.Artist.Trim() };
                    database.ArtistMetadata.Add(metadata);
                }

                oldFileName = metadata.ImageFileName;
                oldImageRootId = metadata.ImageSourceRootId;
                oldImageRelativePath = metadata.ImageRelativePath;
                metadata.ImageFileName = newFileName;
                metadata.ImageSourceRootId = null;
                metadata.ImageRelativePath = null;
                metadata.ImageMissing = false;
                metadata.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (System.IO.File.Exists(imagePath)) System.IO.File.Delete(imagePath);
            throw;
        }

        DeleteReplacedImage(oldFileName, oldImageRootId, oldImageRelativePath, newFileName, null, null, album ? "albums" : "artists");
    }

    private async Task<string?> SaveLookupImageAsync(
        byte[] image,
        string contentType,
        MetadataImageDestination destination,
        string baseName,
        CancellationToken cancellationToken)
    {
        var validatedType = DetectImageContentType(image);
        if (validatedType is null || validatedType != contentType || image.Length > MaximumCoverFileSize)
        {
            logger.LogWarning("Rejected an invalid image returned by metadata lookup for {FileName}.", baseName);
            return null;
        }
        if (!IsSafePathWithoutLinks(destination.RootPath, destination.DirectoryPath))
        {
            throw new InvalidOperationException("The music folder selected for an image is no longer safe.");
        }

        var extension = validatedType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("Unsupported metadata image type."),
        };
        var fileName = $"{baseName}{extension}";
        var path = Path.Combine(destination.DirectoryPath, fileName);
        var temporaryPath = Path.Combine(destination.DirectoryPath, $".{fileName}.{Guid.NewGuid():N}.tmp");
        await System.IO.File.WriteAllBytesAsync(temporaryPath, image, cancellationToken);
        try
        {
            System.IO.File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
            throw;
        }

        return fileName;
    }

    private async Task<MetadataImageDestination?> FindAlbumImageDestinationAsync(
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artist.Trim().ToUpperInvariant();
        var normalizedAlbum = album.Trim().ToUpperInvariant();
        var tracks = await database.Tracks.AsNoTracking()
            .Include(track => track.SourceRoot)
            .Include(track => track.Folder)
            .Where(track => track.IsAvailable
                && track.SourceRoot != null
                && track.SourceRoot.IsEnabled
                && track.Album.Trim().ToUpper() == normalizedAlbum
                && (string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist).Trim().ToUpper() == normalizedArtist)
            .OrderBy(track => track.Id)
            .ToListAsync(cancellationToken);
        foreach (var track in tracks)
        {
            if (track.SourceRoot is null || track.Folder is null) continue;
            var destination = TryGetImageDestination(track.SourceRoot, track.Folder.RelativePath);
            if (destination is not null) return destination;
        }

        return null;
    }

    private async Task<MetadataImageDestination?> FindArtistImageDestinationAsync(
        string artist,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artist.Trim().ToUpperInvariant();
        var artistFolders = await database.Folders.AsNoTracking()
            .Include(folder => folder.SourceRoot)
            .Where(folder => folder.SourceRoot != null && folder.SourceRoot.IsEnabled
                && folder.Name.Trim().ToUpper() == normalizedArtist)
            .OrderBy(folder => folder.Id)
            .ToListAsync(cancellationToken);
        foreach (var folder in artistFolders)
        {
            if (folder.SourceRoot is null) continue;
            var destination = TryGetImageDestination(folder.SourceRoot, folder.RelativePath, isArtistDirectory: true);
            if (destination is not null) return destination;
        }

        var tracks = await database.Tracks.AsNoTracking()
            .Include(track => track.SourceRoot)
            .Include(track => track.Folder)
            .Where(track => track.IsAvailable
                && track.SourceRoot != null
                && track.SourceRoot.IsEnabled
                && (track.Artist.Trim().ToUpper() == normalizedArtist
                    || track.AlbumArtist.Trim().ToUpper() == normalizedArtist))
            .OrderBy(track => track.Id)
            .ToListAsync(cancellationToken);
        foreach (var track in tracks)
        {
            if (track.SourceRoot is null || track.Folder is null) continue;
            var trackDirectory = ResolvePath(track.SourceRoot.Path, track.Folder.RelativePath);
            for (var directory = new DirectoryInfo(trackDirectory);
                 directory.Exists && IsWithin(directory.FullName, track.SourceRoot.Path);
                 directory = directory.Parent!)
            {
                if (string.Equals(directory.Name, artist.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var artistDirectory = Path.GetRelativePath(track.SourceRoot.Path, directory.FullName);
                    var artistDestination = TryGetImageDestination(track.SourceRoot, artistDirectory, isArtistDirectory: true);
                    if (artistDestination is not null) return artistDestination;
                    break;
                }

                if (directory.Parent is null) break;
            }

            var destination = TryGetImageDestination(track.SourceRoot, track.Folder.RelativePath);
            if (destination is not null) return destination;
        }

        return null;
    }

    private MetadataImageDestination? TryGetImageDestination(
        MusicSourceRoot root,
        string relativeDirectory,
        bool isArtistDirectory = false)
    {
        try
        {
            var directory = ResolvePath(root.Path, relativeDirectory);
            if (!Directory.Exists(directory) || (System.IO.File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            return new MetadataImageDestination(root.Id, root.Path, relativeDirectory, directory, isArtistDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not use music folder {RelativeDirectory} for metadata image storage.", relativeDirectory);
            return null;
        }
    }

    private async Task<CoverImage?> ReadMetadataImageAsync(
        string? fileName,
        string category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
        {
            return null;
        }

        var path = Path.Combine(GetMetadataDirectory(), category, fileName);
        try
        {
            if (!System.IO.File.Exists(path) || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumCoverFileSize) return null;
            var data = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
            var contentType = DetectImageContentType(data);
            return contentType is null ? null : new CoverImage(data, contentType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read metadata image {ImageFileName}.", fileName);
            return null;
        }
    }

    private async Task<CoverImage?> ReadLibraryImageAsync(
        int? sourceRootId,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        if (!sourceRootId.HasValue || string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = await database.SourceRoots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceRootId.Value && item.IsEnabled, cancellationToken);
        if (root is null) return null;

        try
        {
            var path = ResolvePath(root.Path, relativePath);
            if (!IsSafePathWithoutLinks(root.Path, path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumCoverFileSize) return null;
            var data = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
            var contentType = DetectImageContentType(data);
            return contentType is null ? null : new CoverImage(data, contentType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not read stored library image {RelativePath}.", relativePath);
            return null;
        }
    }

    private static bool IsSafePathWithoutLinks(string rootPath, string fullPath)
    {
        if (!Directory.Exists(rootPath)
            || (System.IO.File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var relative = Path.GetRelativePath(rootPath, fullPath);
        var current = Path.GetFullPath(rootPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }

        return true;
    }

    private void DeleteMetadataImage(string? fileName, string category)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return;
        var path = Path.Combine(GetMetadataDirectory(), category, fileName);
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove replaced metadata image {ImageFileName}.", fileName);
        }
    }

    private void DeleteReplacedImage(
        string? oldFileName,
        int? oldSourceRootId,
        string? oldRelativePath,
        string? newFileName,
        int? newSourceRootId,
        string? newRelativePath,
        string category)
    {
        var sameLocation = oldSourceRootId.HasValue
            ? oldSourceRootId == newSourceRootId
                && string.Equals(oldRelativePath, newRelativePath, StringComparison.OrdinalIgnoreCase)
            : !newSourceRootId.HasValue && string.Equals(oldFileName, newFileName, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(oldFileName)
            || sameLocation)
        {
            return;
        }

        if (oldSourceRootId is null)
        {
            DeleteMetadataImage(oldFileName, category);
            return;
        }

        if (string.IsNullOrWhiteSpace(oldRelativePath)) return;
        var rootPath = database.SourceRoots.AsNoTracking()
            .Where(root => root.Id == oldSourceRootId.Value)
            .Select(root => root.Path)
            .FirstOrDefault();
        if (rootPath is null) return;

        try
        {
            var path = ResolvePath(rootPath, oldRelativePath);
            if (IsSafePathWithoutLinks(rootPath, path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not remove replaced library image {RelativePath}.", oldRelativePath);
        }
    }

    private static string? GetArtistImageUrl(MusicArtistMetadata metadata) =>
        !metadata.ImageMissing && !string.IsNullOrWhiteSpace(metadata.ImageFileName)
            ? $"/api/library/artists/{metadata.Key}/image?v={metadata.UpdatedAt.ToUnixTimeMilliseconds()}"
            : null;

    private static string GetAlbumImageUrl(int trackId, DateTimeOffset? updatedAt = null) =>
        $"/api/library/tracks/{trackId}/cover{(updatedAt.HasValue ? $"?v={updatedAt.Value.ToUnixTimeMilliseconds()}" : "")}";

    private Task<int?> FindAlbumCoverTrackIdAsync(
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artist.Trim().ToUpperInvariant();
        var normalizedAlbum = album.Trim().ToUpperInvariant();
        return database.Tracks.AsNoTracking()
            .Where(track => track.IsAvailable
                && track.SourceRoot != null
                && track.SourceRoot.IsEnabled
                && track.Album.Trim().ToUpper() == normalizedAlbum
                && (string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist).Trim().ToUpper() == normalizedArtist)
            .OrderBy(track => track.Id)
            .Select(track => (int?)track.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<CoverImage?> ResolveFolderCoverAsync(string directoryPath, CancellationToken cancellationToken)
    {
        foreach (var fileName in CoverFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directoryPath, fileName);
            try
            {
                if (!System.IO.File.Exists(path)
                    || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var fileInfo = new FileInfo(path);
                if (fileInfo.Length is <= 0 or > MaximumCoverFileSize)
                {
                    continue;
                }

                var data = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
                var contentType = DetectImageContentType(data);
                if (contentType is not null)
                {
                    return new CoverImage(data, contentType);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public Task<BatchMutationResponse> UpdateMetadataAsync(
        int[] trackIds,
        MusicMetadataPatch changes,
        CancellationToken cancellationToken) =>
        ApplyToTracksAsync(trackIds, (track, path) => WriteTagsAsync(track, path, changes, null, cancellationToken), cancellationToken);

    public Task<BatchMutationResponse> UpdateCoverAsync(
        int[] trackIds,
        byte[] image,
        CancellationToken cancellationToken)
    {
        var contentType = DetectImageContentType(image);
        if (contentType is null || image.Length > 10 * 1024 * 1024)
        {
            throw new ArgumentException("Cover art must be a supported image smaller than 10 MB.");
        }

        return ApplyToTracksAsync(trackIds, (track, path) => WriteTagsAsync(track, path, new MusicMetadataPatch(), new CoverImage(image, contentType), cancellationToken), cancellationToken);
    }

    public async Task<BatchMutationResponse> MoveTracksAsync(int[] trackIds, int destinationFolderId, CancellationToken cancellationToken)
    {
        var destinationFolder = await database.Folders.Include(folder => folder.SourceRoot)
            .SingleOrDefaultAsync(folder => folder.Id == destinationFolderId && folder.SourceRoot != null && folder.SourceRoot.IsEnabled, cancellationToken);
        if (destinationFolder?.SourceRoot is null)
        {
            throw new KeyNotFoundException("The destination folder was not found.");
        }

        var results = new List<TrackMutationResult>();
        foreach (var trackId in trackIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = await database.Tracks.Include(item => item.SourceRoot)
                .SingleOrDefaultAsync(item => item.Id == trackId && item.IsAvailable, cancellationToken);
            if (track?.SourceRoot is null)
            {
                results.Add(new TrackMutationResult(trackId, false, "Track was not found."));
                continue;
            }

            var sourcePath = ResolvePath(track.SourceRoot.Path, track.RelativePath);
            var destinationDirectory = ResolvePath(destinationFolder.SourceRoot.Path, destinationFolder.RelativePath);
            if (!System.IO.File.Exists(sourcePath)
                || !Directory.Exists(destinationDirectory)
                || (System.IO.File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            {
                results.Add(new TrackMutationResult(trackId, false, "The source or destination is unavailable."));
                continue;
            }

            var originalRootId = track.SourceRootId;
            var originalFolderId = track.FolderId;
            var originalRelativePath = track.RelativePath;
            var destinationPath = GetAvailableDestination(destinationDirectory, Path.GetFileName(sourcePath));
            var relativeDestination = NormalizeRelativePath(Path.GetRelativePath(destinationFolder.SourceRoot.Path, destinationPath));
            var temporaryPath = destinationPath + $".moving-{Guid.NewGuid():N}";
            var sourceBackupPath = sourcePath + $".moving-{Guid.NewGuid():N}";
            var sourceQuarantined = false;
            var destinationCreated = false;
            var catalogCommitted = false;
            try
            {
                await using (var source = System.IO.File.OpenRead(sourcePath))
                await using (var destination = System.IO.File.Create(temporaryPath))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                }

                if (new FileInfo(sourcePath).Length != new FileInfo(temporaryPath).Length)
                {
                    throw new IOException("The copied file did not pass size verification.");
                }

                System.IO.File.Move(temporaryPath, destinationPath);
                destinationCreated = true;
                System.IO.File.Move(sourcePath, sourceBackupPath);
                sourceQuarantined = true;
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                track.SourceRootId = destinationFolder.SourceRootId;
                track.FolderId = destinationFolder.Id;
                track.RelativePath = relativeDestination;
                track.FileSize = new FileInfo(destinationPath).Length;
                track.LastWriteTime = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(destinationPath), TimeSpan.Zero);
                track.ScannedAt = DateTimeOffset.UtcNow;
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                catalogCommitted = true;

                var message = "Moved successfully.";
                try
                {
                    System.IO.File.Delete(sourceBackupPath);
                    sourceQuarantined = false;
                }
                catch (IOException)
                {
                    message = "Moved successfully; a source recovery copy remains outside the library scan.";
                }
                catch (UnauthorizedAccessException)
                {
                    message = "Moved successfully; a source recovery copy remains outside the library scan.";
                }

                results.Add(new TrackMutationResult(trackId, true, message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbUpdateException or System.Data.Common.DbException or OperationCanceledException)
            {
                var catalogStateKnown = catalogCommitted;
                var catalogPointsToDestination = catalogCommitted;
                if (!catalogStateKnown)
                {
                    try
                    {
                        await database.Entry(track).ReloadAsync(CancellationToken.None);
                        catalogStateKnown = true;
                        catalogPointsToDestination = track.SourceRootId == destinationFolder.SourceRootId
                            && track.RelativePath == relativeDestination;
                    }
                    catch (Exception reloadException) when (reloadException is InvalidOperationException or DbUpdateException or System.Data.Common.DbException)
                    {
                        catalogStateKnown = false;
                    }
                }

                if (catalogPointsToDestination)
                {
                    try
                    {
                        if (System.IO.File.Exists(sourceBackupPath))
                        {
                            System.IO.File.Delete(sourceBackupPath);
                        }

                        sourceQuarantined = false;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }

                    results.Add(new TrackMutationResult(trackId, true, "Moved successfully; a source recovery copy may remain outside the library scan."));
                }
                else if (catalogStateKnown)
                {
                    track.SourceRootId = originalRootId;
                    track.FolderId = originalFolderId;
                    track.RelativePath = originalRelativePath;
                    var sourceRestored = !sourceQuarantined;
                    if (sourceQuarantined && System.IO.File.Exists(sourceBackupPath) && !System.IO.File.Exists(sourcePath))
                    {
                        try
                        {
                            System.IO.File.Move(sourceBackupPath, sourcePath);
                            sourceQuarantined = false;
                            sourceRestored = true;
                        }
                        catch (IOException)
                        {
                            sourceRestored = false;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            sourceRestored = false;
                        }
                    }

                    var destinationRemoved = !destinationCreated || !System.IO.File.Exists(destinationPath);
                    if (sourceRestored && destinationCreated && System.IO.File.Exists(destinationPath))
                    {
                        try
                        {
                            System.IO.File.Delete(destinationPath);
                            destinationRemoved = true;
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }

                    var recovered = sourceRestored && destinationRemoved;
                    results.Add(new TrackMutationResult(trackId, false, recovered
                        ? "Move failed; the original file was restored."
                        : "Move failed; keep the source recovery copy and destination for manual recovery."));
                }
                else
                {
                    results.Add(new TrackMutationResult(trackId, false, "Move outcome is uncertain; the destination and source recovery copy were retained."));
                }

                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporaryPath))
                {
                    try
                    {
                        System.IO.File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        return new BatchMutationResponse(results);
    }

    private async Task<BatchMutationResponse> ApplyToTracksAsync(
        int[] trackIds,
        Func<MusicTrack, string, Task> apply,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackMutationResult>();
        foreach (var trackId in trackIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = await database.Tracks.Include(item => item.SourceRoot)
                .SingleOrDefaultAsync(item => item.Id == trackId && item.IsAvailable, cancellationToken);
            if (track?.SourceRoot is null)
            {
                results.Add(new TrackMutationResult(trackId, false, "Track was not found."));
                continue;
            }

            var path = ResolvePath(track.SourceRoot.Path, track.RelativePath);
            try
            {
                await apply(track, path);
                results.Add(new TrackMutationResult(trackId, true, "Updated successfully."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TagLib.UnsupportedFormatException or TagLib.CorruptFileException or ArgumentException)
            {
                results.Add(new TrackMutationResult(trackId, false, "This file could not be updated; its previous contents were kept."));
            }
        }

        return new BatchMutationResponse(results);
    }

    private async Task WriteTagsAsync(MusicTrack track, string path, MusicMetadataPatch changes, CoverImage? cover, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(path) || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("The catalog file is unavailable.");
        }

        var extension = Path.GetExtension(path);
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("The track folder is unavailable.");
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.tag-{Guid.NewGuid():N}{extension}");
        var backupPath = temporaryPath + ".bak";
        System.IO.File.Copy(path, temporaryPath);
        try
        {
            using (var audio = TagLib.File.Create(temporaryPath))
            {
                var tag = audio.Tag;
                if (changes.Title is not null) tag.Title = changes.Title.Trim();
                if (changes.Artist is not null) tag.Performers = ToTagValues(changes.Artist);
                if (changes.Album is not null) tag.Album = changes.Album.Trim();
                if (changes.AlbumArtist is not null) tag.AlbumArtists = ToTagValues(changes.AlbumArtist);
                if (changes.Genre is not null) tag.Genres = ToTagValues(changes.Genre);
                if (changes.TrackNumber.HasValue) tag.Track = changes.TrackNumber.Value;
                if (changes.DiscNumber.HasValue) tag.Disc = changes.DiscNumber.Value;
                if (changes.Year.HasValue) tag.Year = changes.Year.Value;
                if (cover is not null)
                {
                    tag.Pictures = [new Picture(new ByteVector(cover.Data)) { MimeType = cover.ContentType, Type = PictureType.FrontCover }];
                }

                audio.Save();
            }

            ApplyDatabaseTags(track, changes);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            System.IO.File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            await transaction.CommitAsync(cancellationToken);
            System.IO.File.Delete(backupPath);
        }
        catch
        {
            if (System.IO.File.Exists(backupPath))
            {
                System.IO.File.Copy(backupPath, path, overwrite: true);
            }

            if (System.IO.File.Exists(temporaryPath))
            {
                System.IO.File.Delete(temporaryPath);
            }

            if (System.IO.File.Exists(backupPath))
            {
                System.IO.File.Delete(backupPath);
            }

            throw;
        }
    }

    private static void ApplyDatabaseTags(MusicTrack track, MusicMetadataPatch changes)
    {
        if (changes.Title is not null) track.Title = changes.Title.Trim();
        if (changes.Artist is not null) track.Artist = string.IsNullOrWhiteSpace(changes.Artist) ? "Unknown artist" : changes.Artist.Trim();
        if (changes.Album is not null) track.Album = string.IsNullOrWhiteSpace(changes.Album) ? "Unknown album" : changes.Album.Trim();
        if (changes.AlbumArtist is not null) track.AlbumArtist = changes.AlbumArtist.Trim();
        if (changes.Genre is not null) track.Genre = changes.Genre.Trim();
        if (changes.TrackNumber.HasValue) track.TrackNumber = changes.TrackNumber.Value;
        if (changes.DiscNumber.HasValue) track.DiscNumber = changes.DiscNumber.Value;
        if (changes.Year.HasValue) track.Year = changes.Year.Value;
    }

    private static string[] ToTagValues(string value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];

    private static string? DetectImageContentType(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return "image/jpeg";
        if (data.Length >= 6 && (System.Text.Encoding.ASCII.GetString(data, 0, 6) is "GIF87a" or "GIF89a")) return "image/gif";
        if (data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP") return "image/webp";
        return null;
    }

    private static string GetAvailableDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!System.IO.File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
    }

    private IEnumerable<string> EnumerateFiles(string rootPath, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = System.IO.File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (SupportedExtensions.Contains(Path.GetExtension(entry)))
                {
                    yield return entry;
                }
            }
        }
    }

    private void EnsureOutsideWebRoot(string sourcePath)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return;
        }

        var normalizedWebRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot));
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        if (IsWithin(normalizedSource, normalizedWebRoot) || IsWithin(normalizedWebRoot, normalizedSource))
        {
            throw new ArgumentException("Music roots cannot overlap the web document root.");
        }
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
            throw new InvalidOperationException("The catalog path escapes its configured source root.");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string path) =>
        path == "." ? string.Empty : path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string FirstTag(string[]? tags, string fallback) =>
        tags?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? fallback;

    private static string JoinArtistTags(string[]? tags, string fallback)
    {
        var values = tags?.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return values is { Length: > 0 } ? string.Join("/", values) : fallback;
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

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

    private static string GetMetadataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterhours", "metadata");

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        var safeName = new string(value.Trim().Select(character =>
            char.IsControl(character) || invalidCharacters.Contains(character) ? '_' : character).ToArray()).TrimEnd('.', ' ');
        if (safeName.Length > 80) safeName = safeName[..80];
        return string.IsNullOrWhiteSpace(safeName) ? "Unknown" : safeName;
    }

    private readonly record struct AlbumKey(string Album, string Artist);

    private sealed record MetadataImageDestination(
        int SourceRootId,
        string RootPath,
        string RelativeDirectory,
        string DirectoryPath,
        bool IsArtistDirectory = false);

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
