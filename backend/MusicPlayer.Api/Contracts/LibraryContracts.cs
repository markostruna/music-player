using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>A configured folder that the server is allowed to scan.</summary>
public sealed record CreateSourceRootRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Required, MaxLength(2048)]
    public required string Path { get; init; }
}

/// <summary>A configured music source visible to an administrator.</summary>
public sealed record SourceRootResponse(int Id, string Name, string Path, int TrackCount, bool IsEnabled);

/// <summary>A browsable directory inside a configured source.</summary>
public sealed record MusicFolderResponse(int Id, int SourceRootId, string Name, string RelativePath);

/// <summary>A catalog entry available for browsing and playback.</summary>
public sealed record TrackResponse(
    int Id,
    int SourceRootId,
    int FolderId,
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    string Genre,
    uint TrackNumber,
    uint DiscNumber,
    uint Year,
    double DurationSeconds,
    string StreamUrl,
    string CoverUrl,
    string FileExtension);

/// <summary>An album grouped from embedded track metadata.</summary>
public sealed record AlbumResponse(
    string Album,
    string Artist,
    int TrackCount,
    double DurationSeconds,
    int CoverTrackId,
    IReadOnlyList<TrackResponse> Tracks);

/// <summary>Summary of a completed source scan.</summary>
public sealed record ScanResponse(int SourceRootId, int DiscoveredTracks, int UnreadableTracks);

/// <summary>A media file resolved from a catalog record.</summary>
public sealed record ResolvedMedia(string FullPath, string ContentType);

/// <summary>Embedded cover art and its validated media type.</summary>
public sealed record CoverImage(byte[] Data, string ContentType);
