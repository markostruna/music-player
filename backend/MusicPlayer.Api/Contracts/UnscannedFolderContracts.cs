using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>Registers a server-side folder that must remain outside scanned music sources.</summary>
public sealed record CreateUnscannedFolderRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Required, MaxLength(2048)]
    public required string Path { get; init; }
}

/// <summary>Describes a folder registered for manual album curation.</summary>
public sealed record UnscannedFolderResponse(int Id, string Name, string Path);

/// <summary>Renames a directory within an unscanned source.</summary>
public sealed record RenameUnscannedFolderRequest
{
    [Required, MaxLength(255)]
    public required string Name { get; init; }
}

/// <summary>Renames an audio file without changing its embedded tags.</summary>
public sealed record RenameUnscannedTrackRequest
{
    [Required, MaxLength(255)]
    public required string Name { get; init; }
}

/// <summary>Checks an unscanned album against catalogued artist and album names.</summary>
public sealed record CheckUnscannedAlbumRequest
{
    [Required, MaxLength(2048)]
    public required string RelativePath { get; init; }

    [Required, MaxLength(1024)]
    public required string Artist { get; init; }

    [Required, MaxLength(1024)]
    public required string Album { get; init; }

    [Range(1, int.MaxValue)]
    public required int DestinationRootId { get; init; }
}

/// <summary>A catalogued album matching an unscanned album.</summary>
public sealed record ExistingAlbumResponse(int SourceRootId, string Artist, string Album, string RelativePath);

/// <summary>Duplicate-check result for an unscanned album.</summary>
public sealed record CheckUnscannedAlbumResponse(
    bool HasCueFile,
    bool DestinationFolderExists,
    IReadOnlyList<ExistingAlbumResponse> ExistingAlbums);

/// <summary>Resolves an album conflict before moving an unscanned album into a scanned source.</summary>
public sealed record ImportUnscannedAlbumRequest
{
    [Required, MaxLength(2048)]
    public required string RelativePath { get; init; }

    [Range(1, int.MaxValue)]
    public required int DestinationRootId { get; init; }

    [Required, MaxLength(1024)]
    public required string Artist { get; init; }

    [Required, MaxLength(1024)]
    public required string Album { get; init; }

    [MaxLength(20)]
    public string ConflictAction { get; init; } = "";

    [MaxLength(255)]
    public string ExistingAlbumNewName { get; init; } = "";

    public bool SplitCue { get; init; }
    public bool KeepCueSources { get; init; }
}
