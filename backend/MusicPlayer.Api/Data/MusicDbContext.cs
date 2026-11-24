using Microsoft.EntityFrameworkCore;

namespace MusicPlayer.Api.Data;

public sealed class MusicDbContext(DbContextOptions<MusicDbContext> options) : DbContext(options)
{
    public DbSet<MusicUser> Users => Set<MusicUser>();
    public DbSet<MusicSourceRoot> SourceRoots => Set<MusicSourceRoot>();
    public DbSet<MusicFolder> Folders => Set<MusicFolder>();
    public DbSet<MusicTrack> Tracks => Set<MusicTrack>();
    public DbSet<TrackAudioAnalysis> TrackAudioAnalyses => Set<TrackAudioAnalysis>();
    public DbSet<MusicArtistMetadata> ArtistMetadata => Set<MusicArtistMetadata>();
    public DbSet<MusicAlbumMetadata> AlbumMetadata => Set<MusicAlbumMetadata>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<PlaybackState> PlaybackStates => Set<PlaybackState>();
    public DbSet<PlaybackHistory> PlaybackHistory => Set<PlaybackHistory>();
    public DbSet<MusicPlaylist> Playlists => Set<MusicPlaylist>();
    public DbSet<MusicPlaylistTrack> PlaylistTracks => Set<MusicPlaylistTrack>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MusicUser>().HasIndex(user => user.NormalizedEmail).IsUnique();
        modelBuilder.Entity<PlaybackState>().HasKey(state => state.UserId);
        modelBuilder.Entity<MusicPlaylist>().HasOne(playlist => playlist.User).WithMany()
            .HasForeignKey(playlist => playlist.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicPlaylist>().Property(playlist => playlist.BackgroundId).HasDefaultValue(1);
        modelBuilder.Entity<MusicPlaylistTrack>().HasOne(item => item.Playlist).WithMany(playlist => playlist.Tracks)
            .HasForeignKey(item => item.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicPlaylistTrack>().HasOne(item => item.Track).WithMany()
            .HasForeignKey(item => item.TrackId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicPlaylistTrack>().HasIndex(item => new { item.PlaylistId, item.Position });
        modelBuilder.Entity<PlaybackState>().HasOne(state => state.User).WithOne()
            .HasForeignKey<PlaybackState>(state => state.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlaybackHistory>().HasIndex(history => new { history.UserId, history.PlayId }).IsUnique();
        modelBuilder.Entity<PlaybackHistory>().HasIndex(history =>
            new { history.UserId, history.TrackId, history.StartedAtUnixMilliseconds, history.Id });
        modelBuilder.Entity<PlaybackHistory>().HasOne(history => history.User).WithMany()
            .HasForeignKey(history => history.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProviderCredential>().HasKey(credential => credential.Key);
        modelBuilder.Entity<MusicUser>().Property(user => user.Role).HasConversion<string>();
        modelBuilder.Entity<MusicUser>().Property(user => user.Theme).HasConversion<string>();
        modelBuilder.Entity<MusicSourceRoot>().HasIndex(root => root.Path).IsUnique();
        modelBuilder.Entity<MusicFolder>().HasIndex(folder => new { folder.SourceRootId, folder.RelativePath }).IsUnique();
        modelBuilder.Entity<MusicTrack>().HasIndex(track => new { track.SourceRootId, track.RelativePath }).IsUnique();
        modelBuilder.Entity<TrackAudioAnalysis>().HasKey(analysis => analysis.TrackId);
        modelBuilder.Entity<TrackAudioAnalysis>().HasOne(analysis => analysis.Track).WithOne(track => track.AudioAnalysis)
            .HasForeignKey<TrackAudioAnalysis>(analysis => analysis.TrackId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicArtistMetadata>().HasKey(metadata => metadata.Key);
        modelBuilder.Entity<MusicAlbumMetadata>().HasKey(metadata => metadata.Key);
        modelBuilder.Entity<MusicSourceRoot>()
            .HasMany(root => root.Folders)
            .WithOne(folder => folder.SourceRoot)
            .HasForeignKey(folder => folder.SourceRootId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicSourceRoot>()
            .HasMany(root => root.Tracks)
            .WithOne(track => track.SourceRoot)
            .HasForeignKey(track => track.SourceRootId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MusicFolder>()
            .HasMany<MusicTrack>()
            .WithOne(track => track.Folder)
            .HasForeignKey(track => track.FolderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
