using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

public sealed record ProviderCredentialStatus(string Key, string Label, bool Required, bool IsConfigured);

public sealed record ProviderSettingsItem(
    string Id,
    string Name,
    string Description,
    bool IsConfigured,
    IReadOnlyList<ProviderCredentialStatus> Credentials);

public sealed record UpdateProviderSettingsRequest
{
    [Required]
    public required Dictionary<string, string?> Credentials { get; init; }
}

public sealed record DefaultAlbumProviderResponse(string ProviderId);

public sealed record UpdateDefaultAlbumProviderRequest
{
    [Required, MaxLength(40)]
    public required string ProviderId { get; init; }
}
