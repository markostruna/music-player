using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services;

public interface IUnscannedFolderService
{
    Task<IReadOnlyList<UnscannedFolderResponse>> ListAsync(CancellationToken cancellationToken);
    Task<UnscannedFolderResponse> AddAsync(
        CreateUnscannedFolderRequest request,
        CancellationToken cancellationToken);
    Task<bool> RemoveAsync(int rootId, CancellationToken cancellationToken);
    Task<MusicFolderContentsResponse?> BrowseAsync(
        int rootId,
        string? relativePath,
        CancellationToken cancellationToken);
    Task RenameFolderAsync(
        int rootId,
        string relativePath,
        RenameUnscannedFolderRequest request,
        CancellationToken cancellationToken);
    Task RenameTrackAsync(
        int rootId,
        string relativePath,
        RenameUnscannedTrackRequest request,
        CancellationToken cancellationToken);
    Task<CheckUnscannedAlbumResponse> CheckAlbumAsync(
        int rootId,
        CheckUnscannedAlbumRequest request,
        CancellationToken cancellationToken);
    Task<ImportedAlbum> ImportAlbumAsync(
        int sourceRootId,
        ImportUnscannedAlbumRequest request,
        CancellationToken cancellationToken);
}

public sealed record ImportedAlbum(string RelativePath, bool SourceRetained);
