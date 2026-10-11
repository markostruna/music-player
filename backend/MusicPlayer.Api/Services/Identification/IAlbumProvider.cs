using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services.Identification;

public interface IAlbumProvider
{
    string Id { get; }
    string Name { get; }
    bool IsConfigured { get; }

    Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(
        string? artist,
        string? album,
        int? year,
        CancellationToken cancellationToken);

    Task<AlbumMatchDetailResponse?> GetAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Raised when an identification provider cannot be reached or rejects a request.</summary>
public sealed class AlbumProviderException(string message, Exception? inner = null) : Exception(message, inner);

internal static class ProviderText
{
    public const string UserAgent = "AfterhoursMusicPlayer/1.0 (https://github.com/markostruna/music-player)";

    public static int? ParseYear(string? value) =>
        value is { Length: >= 4 } && int.TryParse(value.AsSpan(0, 4), out var year) && year > 0 ? year : null;

    public static string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
