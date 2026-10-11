using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services.Identification;

public sealed class SpotifyAlbumProvider(HttpClient http, IConfiguration configuration) : IAlbumProvider
{
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string? accessToken;
    private static DateTimeOffset accessTokenExpiry;

    private string? ClientId => configuration["Spotify:ClientId"];
    private string? ClientSecret => configuration["Spotify:ClientSecret"];

    public string Id => "spotify";
    public string Name => "Spotify";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public async Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(
        string? artist,
        string? album,
        int? year,
        CancellationToken cancellationToken)
    {
        var terms = new List<string>();
        if (ProviderText.Nullable(album) is { } albumTerm) terms.Add($"album:\"{Escape(albumTerm)}\"");
        if (ProviderText.Nullable(artist) is { } artistTerm) terms.Add($"artist:\"{Escape(artistTerm)}\"");
        if (terms.Count == 0) return [];
        if (year is > 0) terms.Add($"year:{year}");

        using var document = await GetJsonAsync(
            $"search?type=album&limit=12&q={Uri.EscapeDataString(string.Join(' ', terms))}",
            cancellationToken);
        var items = document?.RootElement.GetPropertyOrNull("albums")?.GetPropertyOrNull("items");
        if (items is not { ValueKind: JsonValueKind.Array }) return [];

        return items.Value.EnumerateArray()
            .Select(item => ToMatch(item, null))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }

    public async Task<AlbumMatchDetailResponse?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit)) return null;
        using var document = await GetJsonAsync($"albums/{id}", cancellationToken);
        if (document is null) return null;
        var album = document.RootElement;
        var tracks = new List<AlbumMatchTrackResponse>();
        if (album.GetPropertyOrNull("tracks")?.GetPropertyOrNull("items") is { ValueKind: JsonValueKind.Array } items)
        {
            foreach (var item in items.EnumerateArray())
            {
                tracks.Add(new AlbumMatchTrackResponse(
                    item.GetPropertyOrNull("disc_number") is { ValueKind: JsonValueKind.Number } disc ? disc.GetInt32() : 1,
                    item.GetPropertyOrNull("track_number") is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : tracks.Count + 1,
                    item.GetStringOrNull("name") ?? "",
                    JoinArtists(item.GetPropertyOrNull("artists")),
                    item.GetPropertyOrNull("duration_ms") is { ValueKind: JsonValueKind.Number } duration ? duration.GetDouble() / 1000 : null));
            }
        }

        var genre = album.GetPropertyOrNull("genres") is { ValueKind: JsonValueKind.Array } genres
            ? genres.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? ""
            : "";
        var match = ToMatch(album, tracks.Count);
        return match is null ? null : new AlbumMatchDetailResponse(match, genre, null, tracks);
    }

    private AlbumMatchResponse? ToMatch(JsonElement album, int? trackCount)
    {
        var id = album.GetStringOrNull("id");
        if (id is null) return null;
        var cover = album.GetPropertyOrNull("images") is { ValueKind: JsonValueKind.Array } images
            ? images.EnumerateArray().Select(item => item.GetStringOrNull("url")).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            : null;
        return new AlbumMatchResponse(
            Provider: Id,
            Id: id,
            Artist: JoinArtists(album.GetPropertyOrNull("artists")),
            Title: album.GetStringOrNull("name") ?? "",
            Year: ProviderText.ParseYear(album.GetStringOrNull("release_date")),
            TrackCount: trackCount ?? (album.GetPropertyOrNull("total_tracks") is { ValueKind: JsonValueKind.Number } total ? total.GetInt32() : null),
            CoverUrl: cover,
            Detail: album.GetStringOrNull("album_type"));
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new AlbumProviderException("Spotify is not configured. Set Spotify:ClientId and Spotify:ClientSecret on the server.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AlbumProviderException("Spotify could not be reached or rejected the request.", exception);
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await TokenGate.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && accessTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            accessToken = document.RootElement.GetStringOrNull("access_token")
                ?? throw new JsonException("The Spotify token response was invalid.");
            var seconds = document.RootElement.GetPropertyOrNull("expires_in") is { ValueKind: JsonValueKind.Number } expires
                ? expires.GetInt32()
                : 3600;
            accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return accessToken;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    private static string JoinArtists(JsonElement? artists) =>
        artists is { ValueKind: JsonValueKind.Array } array
            ? string.Join(", ", array.EnumerateArray().Select(item => item.GetStringOrNull("name")).Where(item => !string.IsNullOrWhiteSpace(item)))
            : "";

    private static string Escape(string value) => value.Replace("\"", "").Replace("\\", "");
}
