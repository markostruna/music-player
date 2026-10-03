namespace MusicPlayer.Api.Services;

public interface IMusicBrainzClient
{
    Task<MusicBrainzResult?> FindArtistAsync(string artist, CancellationToken cancellationToken);
    Task<MusicBrainzResult?> FindAlbumAsync(string artist, string album, CancellationToken cancellationToken);
}

public sealed record MusicBrainzResult(
    string MusicBrainzId,
    string Description,
    byte[]? Image,
    string? ImageContentType,
    string? ImageProvider = null);
