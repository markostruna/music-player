using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services;

public interface IMusicLibraryService
{
    Task<IReadOnlyList<SourceRootResponse>> GetRootsAsync(CancellationToken cancellationToken);
    Task<SourceRootResponse> AddRootAsync(CreateSourceRootRequest request, CancellationToken cancellationToken);
    Task<bool> RemoveRootAsync(int rootId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicFolderResponse>> GetFoldersAsync(CancellationToken cancellationToken);
    Task<ScanResponse> ScanAsync(int rootId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrackResponse>> GetTracksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AlbumResponse>> GetAlbumsAsync(CancellationToken cancellationToken);
    Task<ResolvedMedia?> ResolveStreamAsync(int trackId, CancellationToken cancellationToken);
    Task<CoverImage?> ResolveCoverAsync(int trackId, CancellationToken cancellationToken);
    Task<CoverImage?> ResolveArtistImageAsync(string key, CancellationToken cancellationToken);
    Task<MetadataRefreshResponse> RefreshArtistMetadataAsync(string artist, CancellationToken cancellationToken);
    Task<MetadataRefreshResponse> RefreshAlbumMetadataAsync(string artist, string album, CancellationToken cancellationToken);
    Task UpdateMetadataDescriptionAsync(MetadataDescriptionRequest request, bool album, CancellationToken cancellationToken);
    Task UpdateMetadataImageAsync(MetadataImageRequest request, bool album, CancellationToken cancellationToken);
    Task<BatchMutationResponse> UpdateMetadataAsync(int[] trackIds, MusicMetadataPatch changes, CancellationToken cancellationToken);
    Task<BatchMutationResponse> UpdateCoverAsync(int[] trackIds, byte[] image, CancellationToken cancellationToken);
    Task<BatchMutationResponse> MoveTracksAsync(int[] trackIds, int destinationFolderId, CancellationToken cancellationToken);
}
