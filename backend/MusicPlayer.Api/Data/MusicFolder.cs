using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class MusicFolder
{
    public int Id { get; set; }
    public int SourceRootId { get; set; }
    public MusicSourceRoot? SourceRoot { get; set; }

    [MaxLength(2048)]
    public required string RelativePath { get; set; }

    [MaxLength(255)]
    public required string Name { get; set; }
}
