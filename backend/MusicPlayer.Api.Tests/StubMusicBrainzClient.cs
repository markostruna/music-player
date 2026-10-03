using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

internal sealed class StubMusicBrainzClient : IMusicBrainzClient
{
    public int ArtistLookups { get; private set; }
    public int AlbumLookups { get; private set; }
    public MusicBrainzResult? ArtistResult { get; set; }
    public MusicBrainzResult? AlbumResult { get; set; }

    public Task<MusicBrainzResult?> FindArtistAsync(string artist, CancellationToken cancellationToken)
    {
        ArtistLookups++;
        return Task.FromResult(ArtistResult);
    }

    public Task<MusicBrainzResult?> FindAlbumAsync(string artist, string album, CancellationToken cancellationToken)
    {
        AlbumLookups++;
        return Task.FromResult(AlbumResult);
    }
}
