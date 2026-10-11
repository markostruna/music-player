using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>An album identification provider and whether its credentials are configured.</summary>
public sealed record IdentifyProviderResponse(string Id, string Name, bool IsConfigured);

/// <summary>A candidate album returned by an identification provider.</summary>
public sealed record AlbumMatchResponse(
    string Provider,
    string Id,
    string Artist,
    string Title,
    int? Year,
    int? TrackCount,
    string? CoverUrl,
    string? Detail);

public sealed record AlbumMatchTrackResponse(
    int DiscNumber,
    int TrackNumber,
    string Title,
    string Artist,
    double? DurationSeconds);

/// <summary>Full album information, including the track list, for one provider match.</summary>
public sealed record AlbumMatchDetailResponse(
    AlbumMatchResponse Match,
    string Genre,
    string? MusicBrainzReleaseGroupId,
    IReadOnlyList<AlbumMatchTrackResponse> Tracks);

public sealed record AlbumSuggestionsRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Required, MaxLength(40)]
    public required string Provider { get; init; }
}

public sealed record AlbumSuggestionsResponse(
    string? TagArtist,
    string? TagAlbum,
    int? TagYear,
    IReadOnlyList<AlbumMatchResponse> Matches);

public sealed record AlbumSearchRequest
{
    [Required, MaxLength(40)]
    public required string Provider { get; init; }

    [MaxLength(512)]
    public string? Artist { get; init; }

    [MaxLength(512)]
    public string? Album { get; init; }

    [Range(1000, 3000)]
    public int? Year { get; init; }
}

public sealed record AlbumCoversRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Required, MaxLength(40)]
    public required string Provider { get; init; }

    [MaxLength(256)]
    public string? MatchId { get; init; }
}

/// <summary>A selectable album cover; PreviewUrl is an application API path.</summary>
public sealed record CoverCandidateResponse(string Id, string Source, string Label, string PreviewUrl);

public sealed record AlbumCoversResponse(
    IReadOnlyList<CoverCandidateResponse> Candidates,
    IReadOnlyList<string> Warnings);

public sealed record AlbumMoveRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Range(1, int.MaxValue)]
    public required int DestinationRootId { get; init; }

    [Required, MaxLength(512)]
    public required string Artist { get; init; }

    [Required, MaxLength(512)]
    public required string Album { get; init; }

    [Range(0, 3000)]
    public int? Year { get; init; }
}

public sealed record AlbumMovePlanResponse(
    bool CanMove,
    string? Reason,
    string SourcePath,
    string DestinationPath);

public sealed record AlbumMoveResponse(string NewPath, int MovedTracks);
