using Microsoft.EntityFrameworkCore;

namespace MusicPlayer.Api.Data;

public sealed class MusicDbContext(DbContextOptions<MusicDbContext> options) : DbContext(options)
{
    public DbSet<MusicUser> Users => Set<MusicUser>();
    public DbSet<MusicSourceRoot> SourceRoots => Set<MusicSourceRoot>();
    public DbSet<MusicFolder> Folders => Set<MusicFolder>();
    public DbSet<MusicTrack> Tracks => Set<MusicTrack>();
    public DbSet<MusicArtistMetadata> ArtistMetadata => Set<MusicArtistMetadata>();
    public DbSet<MusicAlbumMetadata> AlbumMetadata => Set<MusicAlbumMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MusicUser>().HasIndex(user => user.NormalizedEmail).IsUnique();
        modelBuilder.Entity<MusicUser>().Property(user => user.Role).HasConversion<string>();
        modelBuilder.Entity<MusicUser>().Property(user => user.Theme).HasConversion<string>();
        modelBuilder.Entity<MusicSourceRoot>().HasIndex(root => root.Path).IsUnique();
        modelBuilder.Entity<MusicFolder>().HasIndex(folder => new { folder.SourceRootId, folder.RelativePath }).IsUnique();
        modelBuilder.Entity<MusicTrack>().HasIndex(track => new { track.SourceRootId, track.RelativePath }).IsUnique();
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
