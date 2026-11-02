using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class ProviderCredential
{
    [MaxLength(128)]
    public required string Key { get; set; }

    [MaxLength(8192)]
    public required string ProtectedValue { get; set; }
}
