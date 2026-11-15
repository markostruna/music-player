using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed partial class UnscannedFolderService(
    MusicDbContext database,
    IWebHostEnvironment environment,
    ILogger<UnscannedFolderService> logger) : IUnscannedFolderService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav",
    };

    public async Task<IReadOnlyList<UnscannedFolderResponse>> ListAsync(CancellationToken cancellationToken) =>
        await database.SourceRoots.AsNoTracking()
            .Where(root => !root.IsEnabled)
            .OrderBy(root => root.Name)
            .Select(root => new UnscannedFolderResponse(root.Id, root.Name, root.Path))
            .ToListAsync(cancellationToken);

    public async Task<UnscannedFolderResponse> AddAsync(
        CreateUnscannedFolderRequest request,
        CancellationToken cancellationToken)
    {
        var path = NormalizeRootPath(request.Path);
        if (!Directory.Exists(path) || HasReparsePoint(path))
        {
            throw new ArgumentException("The selected folder does not exist or is a symbolic link.");
        }

        EnsureOutsideWebRoot(path);
        var configuredPaths = await database.SourceRoots
            .Select(root => root.Path)
            .ToListAsync(cancellationToken);
        if (configuredPaths.Any(configured => PathsOverlap(path, configured)))
        {
            throw new InvalidOperationException(
                "Unscanned folders cannot overlap another configured music folder.");
        }

        var root = new MusicSourceRoot
        {
            Name = request.Name.Trim(),
            Path = path,
            IsEnabled = false,
        };
        database.SourceRoots.Add(root);
        await database.SaveChangesAsync(cancellationToken);
        return new UnscannedFolderResponse(root.Id, root.Name, root.Path);
    }

    public async Task<bool> RemoveAsync(int rootId, CancellationToken cancellationToken)
    {
        var root = await database.SourceRoots.SingleOrDefaultAsync(
            item => item.Id == rootId && !item.IsEnabled,
            cancellationToken);
        if (root is null)
        {
            return false;
        }

        database.SourceRoots.Remove(root);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MusicFolderContentsResponse?> BrowseAsync(
        int rootId,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        var root = await GetRootAsync(rootId, cancellationToken);
        var normalized = NormalizeRelativePath(relativePath, allowEmpty: true);
        var rootPath = Path.GetFullPath(root.Path);
        var folderPath = ResolvePath(rootPath, normalized);
        if (!Directory.Exists(folderPath) || !IsSafePath(rootPath, folderPath))
        {
            return null;
        }

        try
        {
            var folders = Directory.EnumerateDirectories(folderPath)
                .Where(path => !HasReparsePoint(path))
                .Select(path => new MusicFolderBrowseEntryResponse(
                    root.Id,
                    Path.GetFileName(path),
                    ToRelativePath(rootPath, path)))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var files = Directory.EnumerateFiles(folderPath)
                .Where(path => !HasReparsePoint(path))
                .Select(path => new MusicFolderFileResponse(
                    Path.GetFileName(path),
                    ToRelativePath(rootPath, path)))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MusicFolderContentsResponse(root.Id, root.Name, normalized, folders, files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The selected folder could not be read.", exception);
        }
    }

    public async Task RenameFolderAsync(
        int rootId,
        string relativePath,
        RenameUnscannedFolderRequest request,
        CancellationToken cancellationToken)
    {
        var root = await GetRootAsync(rootId, cancellationToken);
        var relative = NormalizeRelativePath(relativePath, allowEmpty: false);
        var source = ResolvePath(root.Path, relative);
        EnsureSafeDescendant(root.Path, source);
        var target = Path.Combine(Path.GetDirectoryName(source)!, ValidateSegment(request.Name));
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException("A file or folder with that name already exists.");
        }

        Directory.Move(source, target);
    }

    public async Task RenameTrackAsync(
        int rootId,
        string relativePath,
        RenameUnscannedTrackRequest request,
        CancellationToken cancellationToken)
    {
        var root = await GetRootAsync(rootId, cancellationToken);
        var relative = NormalizeRelativePath(relativePath, allowEmpty: false);
        var source = ResolvePath(root.Path, relative);
        EnsureSafeDescendant(root.Path, source);
        var extension = Path.GetExtension(source);
        if (!AudioExtensions.Contains(extension))
        {
            throw new ArgumentException("Only MP3, FLAC, and WAV files can be renamed as tracks.");
        }

        var name = ValidateSegment(request.Name);
        if (Path.HasExtension(name))
        {
            if (!string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The track file extension cannot be changed.");
            }

            name = Path.GetFileNameWithoutExtension(name);
        }

        var target = Path.Combine(Path.GetDirectoryName(source)!, name + extension);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException("A file or folder with that name already exists.");
        }

        System.IO.File.Move(source, target);
        await UpdateCueReferencesAsync(Path.GetDirectoryName(source)!, source, target, cancellationToken);
    }

    public async Task<CheckUnscannedAlbumResponse> CheckAlbumAsync(
        int rootId,
        CheckUnscannedAlbumRequest request,
        CancellationToken cancellationToken)
    {
        var root = await GetRootAsync(rootId, cancellationToken);
        var folder = ResolvePath(root.Path, NormalizeRelativePath(request.RelativePath, allowEmpty: false));
        EnsureSafeDescendant(root.Path, folder);
        if (!Directory.Exists(folder))
        {
            throw new ArgumentException("The selected album folder does not exist.");
        }

        var destination = await database.SourceRoots.SingleOrDefaultAsync(
            item => item.Id == request.DestinationRootId && item.IsEnabled,
            cancellationToken)
            ?? throw new ArgumentException("The destination must be a scanned music folder.");
        if (!Directory.Exists(destination.Path) || HasReparsePoint(destination.Path))
        {
            throw new InvalidOperationException("The destination music folder is unavailable or unsafe.");
        }
        if (PathsOverlap(root.Path, destination.Path))
        {
            throw new InvalidOperationException("The unscanned folder and destination cannot overlap.");
        }

        var artistPath = Directory.EnumerateDirectories(destination.Path)
            .Where(path => !HasReparsePoint(path))
            .FirstOrDefault(path => Path.GetFileName(path).Equals(
                ValidateSegment(request.Artist),
                StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(destination.Path, ValidateSegment(request.Artist));
        var targetPath = Path.Combine(artistPath, ValidateSegment(request.Album));
        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException("The destination album path is occupied by a file.");
        }
        if (Directory.Exists(targetPath) && HasReparsePoint(targetPath))
        {
            throw new InvalidOperationException("The destination album folder is unsafe.");
        }
        var matches = await FindAlbumsAsync(request.Artist, request.Album, cancellationToken);
        var hasCueFile = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase));
        return new CheckUnscannedAlbumResponse(hasCueFile, Directory.Exists(targetPath), matches);
    }

    public async Task<ImportedAlbum> ImportAlbumAsync(
        int sourceRootId,
        ImportUnscannedAlbumRequest request,
        CancellationToken cancellationToken)
    {
        var sourceRoot = await GetRootAsync(sourceRootId, cancellationToken);
        var sourceRelative = NormalizeRelativePath(request.RelativePath, allowEmpty: false);
        var sourcePath = ResolvePath(sourceRoot.Path, sourceRelative);
        EnsureSafeDescendant(sourceRoot.Path, sourcePath);
        if (!Directory.Exists(sourcePath))
        {
            throw new ArgumentException("The selected album folder does not exist.");
        }

        EnsureTreeHasNoLinks(sourcePath);
        var destinationRoot = await database.SourceRoots.SingleOrDefaultAsync(
            root => root.Id == request.DestinationRootId && root.IsEnabled,
            cancellationToken)
            ?? throw new ArgumentException("The destination must be a scanned music folder.");
        var artistName = ValidateSegment(request.Artist);
        var albumName = ValidateSegment(request.Album);
        var destinationRootPath = Path.GetFullPath(destinationRoot.Path);
        if (!Directory.Exists(destinationRootPath) || HasReparsePoint(destinationRootPath))
        {
            throw new InvalidOperationException("The destination music folder is unavailable or unsafe.");
        }
        if (PathsOverlap(sourceRoot.Path, destinationRootPath))
        {
            throw new InvalidOperationException("The unscanned folder and destination cannot overlap.");
        }

        var artistPath = Directory.EnumerateDirectories(destinationRootPath)
            .Where(path => !HasReparsePoint(path))
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                artistName,
                StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(destinationRootPath, artistName);
        var targetPath = Path.Combine(artistPath, albumName);
        if (Directory.Exists(artistPath) && HasReparsePoint(artistPath))
        {
            throw new InvalidOperationException("The destination artist folder is unsafe.");
        }
        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException("The destination album path is occupied by a file.");
        }
        if (Directory.Exists(targetPath) && HasReparsePoint(targetPath))
        {
            throw new InvalidOperationException("The destination album folder is unsafe.");
        }

        var existingAlbums = await FindAlbumsAsync(artistName, albumName, cancellationToken);
        var targetExists = Directory.Exists(targetPath);
        var hasConflict = existingAlbums.Count > 0 || targetExists;
        if (hasConflict && request.ConflictAction is not ("rename" or "delete"))
        {
            throw new InvalidOperationException("Choose whether to rename or delete the existing album before importing.");
        }

        if (existingAlbums.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one matching album is in the library. Resolve those copies individually before importing.");
        }

        if (existingAlbums.Count == 1
            && targetExists
            && !Path.GetFullPath(targetPath).Equals(
                ResolvePath(
                    (await database.SourceRoots.SingleAsync(
                        item => item.Id == existingAlbums[0].SourceRootId,
                        cancellationToken)).Path,
                    existingAlbums[0].RelativePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The destination already contains a different folder as well as a matching catalog album. Resolve the destination folder first.");
        }

        Directory.CreateDirectory(artistPath);
        var stagingPath = targetPath + ".importing-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(sourcePath, stagingPath);
            if (request.SplitCue)
            {
                await SplitCueAsync(stagingPath, artistName, albumName, cancellationToken);
            }
            else
            {
                CopyTagsInDirectory(stagingPath, artistName, albumName);
            }

            if (hasConflict)
            {
                await ResolveExistingAlbumAsync(
                    existingAlbums.SingleOrDefault(),
                    targetPath,
                    request,
                    cancellationToken);
            }

            Directory.Move(stagingPath, targetPath);
            var importedPath = ToRelativePath(destinationRootPath, targetPath);
            if (!request.KeepCueSources)
            {
                try
                {
                    Directory.Delete(sourcePath, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(exception, "Imported album but could not remove source folder {SourcePath}.", sourcePath);
                    return new ImportedAlbum(importedPath, true);
                }
            }

            return new ImportedAlbum(importedPath, request.KeepCueSources);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            throw;
        }
    }

    private async Task<MusicSourceRoot> GetRootAsync(int rootId, CancellationToken cancellationToken) =>
        await database.SourceRoots.SingleOrDefaultAsync(
            root => root.Id == rootId && !root.IsEnabled,
            cancellationToken)
        ?? throw new ArgumentException("The unscanned folder was not found.");

    private async Task<IReadOnlyList<ExistingAlbumResponse>> FindAlbumsAsync(
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artist.Trim();
        var normalizedAlbum = album.Trim();
        var tracks = await database.Tracks.AsNoTracking()
            .Where(track => track.IsAvailable)
            .Select(track => new
            {
                track.SourceRootId,
                track.Artist,
                track.AlbumArtist,
                track.Album,
                RelativePath = track.Folder!.RelativePath,
            })
            .ToListAsync(cancellationToken);
        return tracks
            .Where(track => track.Album.Trim().Equals(normalizedAlbum, StringComparison.OrdinalIgnoreCase)
                && (track.AlbumArtist.Trim().Length == 0 ? track.Artist.Trim() : track.AlbumArtist.Trim())
                    .Equals(normalizedArtist, StringComparison.OrdinalIgnoreCase))
            .Select(track => new ExistingAlbumResponse(
                track.SourceRootId,
                track.AlbumArtist.Trim() == "" ? track.Artist : track.AlbumArtist,
                track.Album,
                track.RelativePath))
            .Distinct()
            .ToArray();
    }

    private async Task ResolveExistingAlbumAsync(
        ExistingAlbumResponse? existing,
        string targetPath,
        ImportUnscannedAlbumRequest request,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            if (request.ConflictAction == "rename")
            {
                var renamedPath = Path.Combine(
                    Path.GetDirectoryName(targetPath)!,
                    ValidateSegment(request.ExistingAlbumNewName));
                if (Directory.Exists(renamedPath) || File.Exists(renamedPath))
                {
                    throw new InvalidOperationException("The replacement album name is already in use.");
                }

                Directory.Move(targetPath, renamedPath);
            }
            else
            {
                Directory.Delete(targetPath, recursive: true);
            }

            return;
        }

        var root = await database.SourceRoots.SingleAsync(
            item => item.Id == existing.SourceRootId,
            cancellationToken);
        if (string.IsNullOrEmpty(existing.RelativePath))
        {
            throw new InvalidOperationException("The matching album is stored at the root of its music source and cannot be moved safely.");
        }

        var oldPath = ResolvePath(root.Path, existing.RelativePath);
        EnsureSafeDescendant(root.Path, oldPath);
        if (!Directory.Exists(oldPath))
        {
            throw new InvalidOperationException("The existing album folder is no longer available on disk.");
        }

        var tracks = await database.Tracks
            .Where(track => track.SourceRootId == root.Id
                && (track.Folder!.RelativePath == existing.RelativePath
                    || track.Folder.RelativePath.StartsWith(existing.RelativePath + "/")))
            .ToListAsync(cancellationToken);
        var folders = await database.Folders
            .Where(folder => folder.SourceRootId == root.Id
                && (folder.RelativePath == existing.RelativePath
                    || folder.RelativePath.StartsWith(existing.RelativePath + "/")))
            .ToListAsync(cancellationToken);
        if (tracks.Any(track =>
                !track.Album.Equals(existing.Album, StringComparison.OrdinalIgnoreCase)
                || !(track.AlbumArtist.Trim().Length == 0 ? track.Artist : track.AlbumArtist)
                    .Equals(existing.Artist, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The matching folder contains tracks from another album or artist and cannot be changed as a unit.");
        }

        if (request.ConflictAction == "delete")
        {
            var backupPath = oldPath + ".deleting-" + Guid.NewGuid().ToString("N");
            Directory.Move(oldPath, backupPath);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                database.Tracks.RemoveRange(tracks);
                database.Folders.RemoveRange(folders);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                Directory.Move(backupPath, oldPath);
                throw;
            }

            Directory.Delete(backupPath, recursive: true);
            return;
        }

        var newName = ValidateSegment(request.ExistingAlbumNewName);
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            throw new InvalidOperationException("The replacement album name is already in use.");
        }

        var proposedRelative = ToRelativePath(root.Path, newPath);
        var conflictingFolders = await database.Folders.AnyAsync(
            folder => folder.SourceRootId == root.Id
                && (folder.RelativePath == proposedRelative
                    || folder.RelativePath.StartsWith(proposedRelative + "/")),
            cancellationToken);
        if (conflictingFolders)
        {
            throw new InvalidOperationException("The replacement album name is already present in the library database.");
        }

        Directory.Move(oldPath, newPath);
        await using var renameTransaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var oldRelative = existing.RelativePath;
            var newRelative = proposedRelative;
            foreach (var folder in folders)
            {
                var suffix = folder.RelativePath.Length == oldRelative.Length
                    ? ""
                    : folder.RelativePath[(oldRelative.Length + 1)..];
                folder.RelativePath = suffix.Length == 0 ? newRelative : $"{newRelative}/{suffix}";
                if (folder.RelativePath == newRelative)
                {
                    folder.Name = newName;
                }
            }

            foreach (var track in tracks)
            {
                var folderPath = folders.Single(folder => folder.Id == track.FolderId).RelativePath;
                var fileName = Path.GetFileName(track.RelativePath);
                track.RelativePath = $"{folderPath}/{fileName}";
                track.Album = newName;
                UpdateEmbeddedAlbumTag(ResolvePath(root.Path, track.RelativePath), newName);
            }

            await database.SaveChangesAsync(cancellationToken);
            await renameTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            Directory.Move(newPath, oldPath);
            throw;
        }
    }

    private async Task SplitCueAsync(
        string albumDirectory,
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        var cueFiles = Directory.EnumerateFiles(albumDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (cueFiles.Length != 1)
        {
            throw new ArgumentException("CUE splitting requires exactly one CUE file in the album folder.");
        }

        var cueFile = cueFiles[0];
        var cueLines = await System.IO.File.ReadAllLinesAsync(cueFile, cancellationToken);
        var cueAudio = ReadCueFileName(cueFile);
        var audioPath = ResolvePath(albumDirectory, NormalizeRelativePath(cueAudio, allowEmpty: false));
        if (!Path.GetExtension(audioPath).Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(audioPath))
        {
            throw new ArgumentException("The CUE file must reference a FLAC file in the selected album folder.");
        }

        var tracks = ParseCueTracks(cueLines);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("The CUE file does not contain any indexed tracks.");
        }

        var outputDirectory = Path.Combine(albumDirectory, ".split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        for (var index = 0; index < tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = tracks[index];
            var end = index + 1 < tracks.Count ? tracks[index + 1].Start : (TimeSpan?)null;
            if (end is not null && end <= track.Start)
            {
                throw new ArgumentException("The CUE track indexes are not in ascending order.");
            }

            var trackName = $"{track.Number:D2} - {CleanFileName(track.Title)}.flac";
            var outputPath = Path.Combine(outputDirectory, trackName);
            var startArgument = track.Start.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            var durationArgument = end is null
                ? null
                : (end.Value - track.Start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startArgument);
            if (durationArgument is not null)
            {
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(durationArgument);
            }

            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("flac");
            startInfo.ArgumentList.Add(outputPath);
            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("FFmpeg could not be started.");
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "FFmpeg must be installed and available on the API server's PATH to split CUE albums.",
                    exception);
            }

            using (process)
            {
                var error = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw;
                }

                var errorText = await error;
                if (process.ExitCode != 0 || !System.IO.File.Exists(outputPath))
                {
                    throw new InvalidOperationException(
                        $"FFmpeg could not split track {track.Number}. Check the CUE file and FFmpeg logs.");
                }

                using var audio = TagLib.File.Create(outputPath);
                audio.Tag.Title = track.Title;
                audio.Tag.Performers = [track.Performer ?? artist];
                audio.Tag.Album = album;
                audio.Tag.AlbumArtists = [artist];
                audio.Tag.Track = track.Number;
                audio.Save();

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    logger.LogDebug("FFmpeg output while splitting CUE track {TrackNumber}: {FfmpegOutput}",
                        track.Number, errorText);
                }
            }
        }

        foreach (var generatedTrack in Directory.EnumerateFiles(outputDirectory))
        {
            System.IO.File.Move(generatedTrack, Path.Combine(albumDirectory, Path.GetFileName(generatedTrack)));
        }

        Directory.Delete(outputDirectory, recursive: true);
        var cueFileInAlbum = Directory.EnumerateFiles(albumDirectory, "*", SearchOption.TopDirectoryOnly)
            .Single(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase));
        var audioPathInAlbum = Path.Combine(albumDirectory, ReadCueFileName(cueFileInAlbum));
        if (!System.IO.File.Exists(audioPathInAlbum))
        {
            throw new ArgumentException("The CUE file's FLAC source file could not be found.");
        }

        System.IO.File.Delete(cueFileInAlbum);
        System.IO.File.Delete(audioPathInAlbum);
    }

    private static List<CueTrack> ParseCueTracks(IReadOnlyList<string> lines)
    {
        var tracks = new List<CueTrack>();
        string? title = null;
        string? performer = null;
        uint number = 0;
        TimeSpan? start = null;
        foreach (var line in lines)
        {
            var trackMatch = CueTrackRegex().Match(line);
            if (trackMatch.Success)
            {
                if (number > 0 && start is not null)
                {
                    tracks.Add(new CueTrack(number, title ?? $"Track {number}", performer, start.Value));
                }

                number = uint.Parse(trackMatch.Groups["number"].Value, CultureInfo.InvariantCulture);
                title = null;
                performer = null;
                start = null;
                continue;
            }

            var indexMatch = CueIndexRegex().Match(line);
            if (indexMatch.Success && indexMatch.Groups["index"].Value == "01" && number > 0)
            {
                var minutes = int.Parse(indexMatch.Groups["minutes"].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(indexMatch.Groups["seconds"].Value, CultureInfo.InvariantCulture);
                var frames = int.Parse(indexMatch.Groups["frames"].Value, CultureInfo.InvariantCulture);
                if (seconds > 59 || frames > 74)
                {
                    throw new ArgumentException("The CUE file contains an invalid track index.");
                }

                start = TimeSpan.FromSeconds(minutes * 60 + seconds + frames / 75d);
                continue;
            }

            var fieldMatch = CueFieldRegex().Match(line);
            if (fieldMatch.Success)
            {
                var value = fieldMatch.Groups["value"].Value.Trim().Trim('"');
                if (fieldMatch.Groups["field"].Value.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    title = value;
                }
                else if (fieldMatch.Groups["field"].Value.Equals("PERFORMER", StringComparison.OrdinalIgnoreCase))
                {
                    performer = value;
                }
            }
        }

        if (number > 0 && start is not null)
        {
            tracks.Add(new CueTrack(number, title ?? $"Track {number}", performer, start.Value));
        }

        return tracks;
    }

    private static string ReadCueFileName(string cuePath)
    {
        var line = System.IO.File.ReadLines(cuePath).FirstOrDefault(line => CueFileRegex().IsMatch(line))
            ?? throw new ArgumentException("The CUE file does not reference an audio file.");
        var match = CueFileRegex().Match(line);
        var fileName = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["unquoted"].Value;
        if (Path.IsPathRooted(fileName) || fileName.Split(['/', '\\']).Any(part => part is "." or ".."))
        {
            throw new ArgumentException("The CUE file references a path outside its album folder.");
        }

        return fileName;
    }

    private static async Task UpdateCueReferencesAsync(
        string directory,
        string oldAudioPath,
        string newAudioPath,
        CancellationToken cancellationToken)
    {
        var cueFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            .Where(path => !HasReparsePoint(path));
        foreach (var cueFile in cueFiles)
        {
            var lines = await System.IO.File.ReadAllLinesAsync(cueFile, cancellationToken);
            var changed = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var match = CueFileRegex().Match(lines[index]);
                if (!match.Success) continue;

                var isQuoted = match.Groups["quoted"].Success;
                var fileGroup = isQuoted ? match.Groups["quoted"] : match.Groups["unquoted"];
                string referencedPath;
                try
                {
                    referencedPath = ResolvePath(
                        Path.GetDirectoryName(cueFile)!,
                        NormalizeRelativePath(fileGroup.Value.Replace('\\', '/'), allowEmpty: false));
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (!Path.GetFullPath(referencedPath).Equals(
                    Path.GetFullPath(oldAudioPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    continue;
                }

                var newReference = ToRelativePath(Path.GetDirectoryName(cueFile)!, newAudioPath);
                var replacement = isQuoted
                    ? newReference
                    : newReference.Contains(' ') ? $"\"{newReference}\"" : newReference;
                lines[index] = lines[index][..fileGroup.Index]
                    + replacement
                    + lines[index][(fileGroup.Index + fileGroup.Length)..];
                changed = true;
            }

            if (changed)
            {
                await System.IO.File.WriteAllLinesAsync(cueFile, lines, cancellationToken);
            }
        }
    }

    private static void CopyTagsInDirectory(string directory, string artist, string album)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => AudioExtensions.Contains(Path.GetExtension(path))))
        {
            CopyTagsToAlbum(file, artist, album);
        }
    }

    private static void CopyTagsToAlbum(string path, string artist, string album)
    {
        using var audio = TagLib.File.Create(path);
        audio.Tag.Album = album;
        audio.Tag.AlbumArtists = [artist];
        audio.Save();
    }

    private static void UpdateEmbeddedAlbumTag(string path, string album)
    {
        using var audio = TagLib.File.Create(path);
        audio.Tag.Album = album;
        audio.Save();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static string NormalizeRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private void EnsureOutsideWebRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(environment.WebRootPath)
            && PathsOverlap(path, environment.WebRootPath))
        {
            throw new ArgumentException("The selected folder cannot be inside the web root.");
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        var firstPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var secondPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return IsSameOrChild(firstPath, secondPath) || IsSameOrChild(secondPath, firstPath);
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var parentPrefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.Equals(parent, comparison)
            || path.StartsWith(parentPrefix, comparison);
    }

    private static string NormalizeRelativePath(string? path, bool allowEmpty)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            if (allowEmpty) return "";
            throw new ArgumentException("A folder path is required.");
        }

        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The folder path contains an invalid segment.");
        }

        return normalized;
    }

    private static string ResolvePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrChild(fullPath, fullRoot))
        {
            throw new ArgumentException("The folder path is outside the configured source.");
        }

        return fullPath;
    }

    private static string ToRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsSafePath(string root, string path)
    {
        if (!IsSameOrChild(path, root))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, path);
        var current = Path.GetFullPath(root);
        if (HasReparsePoint(current)) return false;
        if (relative == ".") return true;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (HasReparsePoint(current)) return false;
        }

        return true;
    }

    private static bool HasReparsePoint(string path) =>
        (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void EnsureSafeDescendant(string root, string path)
    {
        if (!IsSafePath(root, path))
        {
            throw new ArgumentException("The selected path is outside the unscanned folder or contains a symbolic link.");
        }
    }

    private static void EnsureTreeHasNoLinks(string path)
    {
        if (HasReparsePoint(path))
        {
            throw new ArgumentException("Albums containing symbolic links cannot be imported.");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            if (HasReparsePoint(entry))
            {
                throw new ArgumentException("Albums containing symbolic links cannot be imported.");
            }
        }
    }

    private static string ValidateSegment(string value)
    {
        var segment = value.Trim();
        if (segment.Length == 0
            || segment is "." or ".."
            || segment.Length > 255
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || segment.Contains('/') || segment.Contains('\\'))
        {
            throw new ArgumentException("Names must be a single valid folder or file name.");
        }

        return segment;
    }

    private static string CleanFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    [GeneratedRegex(@"^\s*TRACK\s+(?<number>\d+)\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueTrackRegex();

    [GeneratedRegex(@"^\s*INDEX\s+(?<index>\d+)\s+(?<minutes>\d{2}):(?<seconds>\d{2}):(?<frames>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueIndexRegex();

    [GeneratedRegex(@"^\s*(?<field>TITLE|PERFORMER)\s+(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueFieldRegex();

    [GeneratedRegex(@"^\s*FILE\s+(?:""(?<quoted>[^""]+)""|(?<unquoted>\S+))\s+\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueFileRegex();

    private sealed record CueTrack(uint Number, string Title, string? Performer, TimeSpan Start);
}
