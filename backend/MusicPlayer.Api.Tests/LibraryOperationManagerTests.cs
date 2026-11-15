using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class LibraryOperationManagerTests
{
    [Fact]
    public void RootRemovalAndLibraryOperationCannotOverlap()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = new LibraryOperationManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LibraryOperationManager>.Instance);

        Assert.True(manager.TryBeginRootRemoval(1));
        Assert.Null(manager.TryStart(1, "Music", LibraryOperationManager.ScanOperation));
        manager.EndRootRemoval(1);

        Assert.NotNull(manager.TryStart(1, "Music", LibraryOperationManager.ScanOperation));
        Assert.False(manager.TryBeginRootRemoval(1));
    }

    [Fact]
    public void AlbumImportReservationPreventsOverlappingOperationsAndQueuesCompositeScan()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var manager = new LibraryOperationManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LibraryOperationManager>.Instance);

        Assert.True(manager.TryBeginLibraryMutation());
        Assert.Null(manager.TryStart(1, "Music", LibraryOperationManager.ScanOperation));
        Assert.False(manager.TryBeginRootRemoval(1));

        var operation = manager.StartReservedImportOperation(2, "Library", "Artist/Album", sourceRetained: true);

        Assert.NotNull(operation);
        Assert.Equal(LibraryOperationManager.ImportScanMetadataOperation, operation.OperationType);
        Assert.Contains("original files remain", operation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.TryBeginLibraryMutation());
    }
}
