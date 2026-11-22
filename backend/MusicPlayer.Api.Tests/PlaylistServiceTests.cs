using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class PlaylistServiceTests
{
    [Fact]
    public async Task PlaylistsArePrivateAndKeepUnavailableTracksDuringEdits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Users.AddRange(
            new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "x" },
            new MusicUser { Id = 2, Email = "two", NormalizedEmail = "TWO", PasswordHash = "x" });
        database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
        database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
        database.Tracks.AddRange(
            new MusicTrack { Id = 1, SourceRootId = 1, FolderId = 1, RelativePath = "1.mp3", Title = "One" },
            new MusicTrack { Id = 2, SourceRootId = 1, FolderId = 1, RelativePath = "2.mp3", Title = "Two" });
        await database.SaveChangesAsync();
        var service = new PlaylistService(database, new MusicLibraryBrowseService(database));
        var playlist = await service.CreateAsync(1, new UpdatePlaylistRequest { Name = "Original", TrackIds = [1, 2, 1] }, default);
        Assert.Equal(1, playlist.BackgroundId);
        Assert.Null(await service.GetAsync(2, playlist.Id, default));
        Assert.Null(await service.UpdateAsync(2, playlist.Id, new UpdatePlaylistRequest { Name = "Other", TrackIds = [], BackgroundId = 20 }, default));
        Assert.False(await service.DeleteAsync(2, playlist.Id, default));
        Assert.Empty(await service.ListAsync(2, default));
        var artwork = await service.UpdateAsync(1, playlist.Id, new UpdatePlaylistRequest
        {
            Name = "Original", TrackIds = [1, 2, 1], BackgroundId = 20,
        }, default);
        Assert.Equal(20, artwork!.BackgroundId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(1, playlist.Id,
            new UpdatePlaylistRequest { Name = "Original", TrackIds = [1], BackgroundId = 21 }, default));
        (await database.Tracks.SingleAsync(track => track.Id == 2)).IsAvailable = false;
        await database.SaveChangesAsync();
        var updated = await service.UpdateAsync(1, playlist.Id, new UpdatePlaylistRequest { Name = "Renamed", TrackIds = [1] }, default);
        Assert.Equal(1, updated!.UnavailableTrackCount);
        (await database.Tracks.SingleAsync(track => track.Id == 2)).IsAvailable = true;
        await database.SaveChangesAsync();
        var restored = await service.GetAsync(1, playlist.Id, default);
        Assert.Equal("Renamed", restored!.Name);
        Assert.Equal(20, restored.BackgroundId);
        Assert.Equal([1, 2], restored.Tracks.Select(track => track.Id));
        Assert.True(await service.DeleteAsync(1, playlist.Id, default));
    }

    [Fact]
    public async Task RandomSelectionsRespectCountsAndKeepAlbumTracksTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
        database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
        for (var album = 0; album < 12; album++)
            for (var track = 0; track < 10; track++)
                database.Tracks.Add(new MusicTrack
                {
                    SourceRootId = 1, FolderId = 1, RelativePath = $"{album}/{track}.mp3",
                    Album = $"Album {album}", Artist = "Artist", Title = $"Track {track}", TrackNumber = (uint)track,
                });
        await database.SaveChangesAsync();
        var library = new MusicLibraryBrowseService(database);
        var tracks = await library.GetRandomTracksAsync(false, 100, default);
        Assert.Equal(100, tracks.Count);
        Assert.Equal(100, tracks.Select(item => item.Id).Distinct().Count());
        var albums = await library.GetRandomTracksAsync(true, 10, default);
        Assert.Equal(100, albums.Count);
        Assert.Equal(10, albums.Select(item => item.Album).Distinct().Count());
        Assert.All(albums.GroupBy(item => item.Album), group => Assert.Equal(10, group.Count()));
    }
}
