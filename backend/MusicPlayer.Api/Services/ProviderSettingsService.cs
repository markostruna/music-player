using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public interface IProviderSettingsService
{
    Task<IReadOnlyList<ProviderSettingsItem>> GetAsync(CancellationToken cancellationToken);
    Task<string> GetDefaultAlbumProviderAsync(CancellationToken cancellationToken);
    Task<string> UpdateDefaultAlbumProviderAsync(string providerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderSettingsItem>> UpdateAsync(
        string providerId,
        IReadOnlyDictionary<string, string?> credentials,
        CancellationToken cancellationToken);
    Task LoadOverridesAsync(CancellationToken cancellationToken);
}

public sealed class ProviderSettingsService(
    MusicDbContext database,
    IDataProtectionProvider protectionProvider,
    IConfiguration configuration) : IProviderSettingsService
{
    private const int MaximumCredentialLength = 4096;
    private const string DefaultAlbumProviderKey = "settings:default-album-provider";
    private const string FallbackAlbumProviderId = "musicbrainz";
    private readonly IDataProtector protector = protectionProvider.CreateProtector("Afterhours.ProviderCredentials.v1");
    private readonly IConfigurationRoot configurationRoot = configuration as IConfigurationRoot
        ?? throw new InvalidOperationException("The provider settings service requires a mutable configuration root.");

    private static readonly ProviderDefinition[] Providers =
    [
        new("musicbrainz", "MusicBrainz", "Public metadata search; no API key is required.", []),
        new("cover-art-archive", "Cover Art Archive", "Public album artwork linked to MusicBrainz; no API key is required.", []),
        new("wikipedia", "Wikipedia", "Public article summaries linked from MusicBrainz; no API key is required.", []),
        new("wikidata", "Wikidata", "Public entity descriptions and metadata; no API key is required.", []),
        new("wikimedia-commons", "Wikimedia Commons", "Public artist images; no API key is required.", []),
        new("discogs", "Discogs", "Album search and release details.", [new("token", "Personal access token", "Discogs:Token", true)]),
        new("spotify", "Spotify", "Album search and track details.", [
            new("clientId", "Client ID", "Spotify:ClientId", true),
            new("clientSecret", "Client secret", "Spotify:ClientSecret", true),
        ]),
        new("fanartTv", "Fanart.tv", "Artist images. Provide a developer API key or a personal key; a client key is optional.", [
            new("apiKey", "API key or personal key", "FanartTv:ApiKey", false),
            new("clientKey", "Client key", "FanartTv:ClientKey", false),
        ]),
    ];

    public async Task<IReadOnlyList<ProviderSettingsItem>> GetAsync(CancellationToken cancellationToken)
    {
        var storedCredentials = await database.ProviderCredentials.AsNoTracking()
            .ToDictionaryAsync(credential => credential.Key, credential => credential.ProtectedValue, cancellationToken);
        return Providers.Select(provider => ToResponse(provider, storedCredentials)).ToArray();
    }

    public async Task<string> GetDefaultAlbumProviderAsync(CancellationToken cancellationToken)
    {
        var storedValue = await database.ProviderCredentials.AsNoTracking()
            .Where(item => item.Key == DefaultAlbumProviderKey)
            .Select(item => item.ProtectedValue)
            .SingleOrDefaultAsync(cancellationToken);
        if (storedValue is null)
        {
            return FallbackAlbumProviderId;
        }

        var providerId = protector.Unprotect(storedValue);
        var provider = (await GetAsync(cancellationToken))
            .SingleOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        return IsAlbumIdentificationProvider(providerId) && provider?.IsConfigured == true
            ? providerId
            : FallbackAlbumProviderId;
    }

    public async Task<string> UpdateDefaultAlbumProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (!IsAlbumIdentificationProvider(providerId))
        {
            throw new ArgumentException("Choose a supported album identification provider.");
        }

        var provider = (await GetAsync(cancellationToken))
            .Single(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (!provider.IsConfigured)
        {
            throw new ArgumentException($"{provider.Name} is not configured.");
        }

        var setting = await database.ProviderCredentials.FindAsync([DefaultAlbumProviderKey], cancellationToken);
        var protectedValue = protector.Protect(provider.Id);
        if (setting is null)
        {
            database.ProviderCredentials.Add(new ProviderCredential
            {
                Key = DefaultAlbumProviderKey,
                ProtectedValue = protectedValue,
            });
        }
        else
        {
            setting.ProtectedValue = protectedValue;
        }

        await database.SaveChangesAsync(cancellationToken);
        return provider.Id;
    }

    public async Task<IReadOnlyList<ProviderSettingsItem>> UpdateAsync(
        string providerId,
        IReadOnlyDictionary<string, string?> credentials,
        CancellationToken cancellationToken)
    {
        var provider = Providers.SingleOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The requested provider does not exist.");

        var updates = credentials.Select(pair =>
        {
            var definition = provider.Credentials.SingleOrDefault(item => item.Key.Equals(pair.Key, StringComparison.Ordinal))
                ?? throw new ArgumentException($"The credential '{pair.Key}' is not supported for {provider.Name}.");
            if (pair.Value is { Length: > MaximumCredentialLength })
            {
                throw new ArgumentException($"{definition.Label} must be no longer than {MaximumCredentialLength} characters.");
            }

            return (Definition: definition, Value: pair.Value?.Trim() ?? "");
        }).ToArray();

        foreach (var (definition, value) in updates)
        {
            var storedKey = GetStoredKey(provider, definition);
            var credential = await database.ProviderCredentials.FindAsync([storedKey], cancellationToken);
            var protectedValue = protector.Protect(value);
            if (credential is null)
            {
                database.ProviderCredentials.Add(new ProviderCredential { Key = storedKey, ProtectedValue = protectedValue });
            }
            else
            {
                credential.ProtectedValue = protectedValue;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        foreach (var (definition, value) in updates)
        {
            configurationRoot[definition.ConfigurationPath] = value;
        }

        await ResetDefaultProviderIfUnavailableAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task LoadOverridesAsync(CancellationToken cancellationToken)
    {
        var storedCredentials = await database.ProviderCredentials.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var credential in storedCredentials)
        {
            if (credential.Key == DefaultAlbumProviderKey)
            {
                continue;
            }

            var (_, definition) = FindCredential(credential.Key);
            configurationRoot[definition.ConfigurationPath] = protector.Unprotect(credential.ProtectedValue);
        }
    }

    private async Task ResetDefaultProviderIfUnavailableAsync(CancellationToken cancellationToken)
    {
        var setting = await database.ProviderCredentials.FindAsync([DefaultAlbumProviderKey], cancellationToken);
        if (setting is null)
        {
            return;
        }

        var selectedProviderId = protector.Unprotect(setting.ProtectedValue);
        var selectedProvider = (await GetAsync(cancellationToken))
            .SingleOrDefault(item => item.Id.Equals(selectedProviderId, StringComparison.OrdinalIgnoreCase));
        if (!IsAlbumIdentificationProvider(selectedProviderId) || selectedProvider?.IsConfigured != true)
        {
            setting.ProtectedValue = protector.Protect(FallbackAlbumProviderId);
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool IsAlbumIdentificationProvider(string providerId) =>
        providerId.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("discogs", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("spotify", StringComparison.OrdinalIgnoreCase);

    private ProviderSettingsItem ToResponse(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string> storedCredentials)
    {
        var statuses = provider.Credentials.Select(definition =>
        {
            var value = storedCredentials.TryGetValue(GetStoredKey(provider, definition), out var protectedValue)
                ? protector.Unprotect(protectedValue)
                : configuration[definition.ConfigurationPath];
            return new ProviderCredentialStatus(
                definition.Key,
                definition.Label,
                definition.Required,
                !string.IsNullOrWhiteSpace(value));
        }).ToArray();
        var isConfigured = provider.Id == "fanartTv"
            ? statuses.Any(status => (status.Key is "apiKey" or "clientKey") && status.IsConfigured)
            : statuses.Where(status => status.Required).All(status => status.IsConfigured);
        return new ProviderSettingsItem(
            provider.Id,
            provider.Name,
            provider.Description,
            isConfigured,
            statuses);
    }

    private static string GetStoredKey(ProviderDefinition provider, CredentialDefinition credential) =>
        $"{provider.Id}:{credential.Key}";

    private static (ProviderDefinition Provider, CredentialDefinition Credential) FindCredential(string storedKey)
    {
        foreach (var provider in Providers)
        {
            var credential = provider.Credentials.FirstOrDefault(item =>
                GetStoredKey(provider, item).Equals(storedKey, StringComparison.Ordinal));
            if (credential is not null) return (provider, credential);
        }

        throw new InvalidOperationException($"Stored provider credential '{storedKey}' is not supported.");
    }

    private sealed record ProviderDefinition(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<CredentialDefinition> Credentials);

    private sealed record CredentialDefinition(string Key, string Label, string ConfigurationPath, bool Required);
}
