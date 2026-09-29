using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using TagLib;

namespace MusicPlayer.Api.Services;

public sealed class MusicLibraryService(MusicDbContext database, IWebHostEnvironment environment) : IMusicLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav",
    };

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
        var root = await database.SourceRoots.Include(source => source.Tracks).SingleOrDefaultAsync(source => source.Id == rootId, cancellationToken);
        if (root is null)
        {
            return false;
        }

        if (root.Tracks.Count != 0)
        {
            throw new InvalidOperationException("Move or rescan the root's tracks before removing it.");
        }

        database.SourceRoots.Remove(root);
        await database.SaveChangesAsync(cancellationToken);
        return true;
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
                track.Artist = FirstTag(tag.Performers, "Unknown artist");
                track.Album = string.IsNullOrWhiteSpace(tag.Album) ? folder.Name : tag.Album.Trim();
                track.AlbumArtist = FirstTag(tag.AlbumArtists, string.Empty);
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
        return tracks.Select(ToResponse).ToList();
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
        var media = await ResolveStreamAsync(trackId, cancellationToken);
        if (media is null)
        {
            return null;
        }

        using var audio = TagLib.File.Create(media.FullPath);
        var picture = audio.Tag.Pictures.FirstOrDefault(item => item.Type == PictureType.FrontCover) ?? audio.Tag.Pictures.FirstOrDefault();
        if (picture is null || picture.Data.Count == 0)
        {
            return null;
        }

        return new CoverImage(picture.Data.Data, picture.MimeType);
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

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private static TrackResponse ToResponse(MusicTrack track) => new(
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
        $"/api/library/tracks/{track.Id}/cover",
        Path.GetExtension(track.RelativePath).TrimStart('.').ToLowerInvariant());

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
