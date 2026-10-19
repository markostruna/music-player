using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services.Identification;

public sealed partial class DiscogsAlbumProvider(HttpClient http, IConfiguration configuration) : IAlbumProvider
{
    private string? Token => configuration["Discogs:Token"];

    public string Id => "discogs";
    public string Name => "Discogs";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);

    public async Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(
        string? artist,
        string? album,
        int? year,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { "type=release", "per_page=12" };
        if (ProviderText.Nullable(artist) is { } artistTerm) query.Add($"artist={Uri.EscapeDataString(artistTerm)}");
        if (ProviderText.Nullable(album) is { } albumTerm) query.Add($"release_title={Uri.EscapeDataString(albumTerm)}");
        if (year is > 0) query.Add($"year={year}");
        if (query.Count == 2) return [];

        using var document = await GetJsonAsync($"database/search?{string.Join('&', query)}", cancellationToken);
        var results = document?.RootElement.GetPropertyOrNull("results");
        if (results is not { ValueKind: JsonValueKind.Array }) return [];

        var matches = new List<AlbumMatchResponse>();
        foreach (var result in results.Value.EnumerateArray())
        {
            if (result.GetPropertyOrNull("id") is not { ValueKind: JsonValueKind.Number } id) continue;
            var title = result.GetStringOrNull("title") ?? "";
            var separator = title.IndexOf(" - ", StringComparison.Ordinal);
            var formats = result.GetPropertyOrNull("format") is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))
                : [];
            var details = new[] { result.GetStringOrNull("country"), string.Join(", ", formats) }
                .Where(item => !string.IsNullOrWhiteSpace(item));
            matches.Add(new AlbumMatchResponse(
                Provider: Id,
                Id: id.GetInt64().ToString(CultureInfo.InvariantCulture),
                Artist: separator > 0 ? CleanName(title[..separator]) : "",
                Title: separator > 0 ? title[(separator + 3)..].Trim() : title,
                Year: ProviderText.ParseYear(result.GetStringOrNull("year")),
                TrackCount: null,
                CoverUrl: ProviderText.Nullable(result.GetStringOrNull("cover_image")) ?? ProviderText.Nullable(result.GetStringOrNull("thumb")),
                Detail: string.Join(" · ", details)));
        }

        return matches;
    }

    public async Task<AlbumMatchDetailResponse?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var releaseId)) return null;
        using var document = await GetJsonAsync($"releases/{releaseId}", cancellationToken);
        if (document is null) return null;
        var release = document.RootElement;
        var releaseArtist = JoinArtists(release.GetPropertyOrNull("artists"));
        var tracks = new List<AlbumMatchTrackResponse>();
        if (release.GetPropertyOrNull("tracklist") is { ValueKind: JsonValueKind.Array } tracklist)
        {
            foreach (var item in tracklist.EnumerateArray())
            {
                if (item.GetStringOrNull("type_") is { } type && type != "track") continue;
                var (disc, number) = ParsePosition(item.GetStringOrNull("position"), tracks.Count + 1);
                var itemArtist = JoinArtists(item.GetPropertyOrNull("artists"));
                tracks.Add(new AlbumMatchTrackResponse(
                    disc,
                    number,
                    item.GetStringOrNull("title") ?? "",
                    itemArtist.Length > 0 ? itemArtist : releaseArtist,
                    ParseDuration(item.GetStringOrNull("duration"))));
            }
        }

        var genres = release.GetPropertyOrNull("genres") is { ValueKind: JsonValueKind.Array } genreArray
            ? genreArray.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            : null;
        var cover = release.GetPropertyOrNull("images") is { ValueKind: JsonValueKind.Array } images
            ? images.EnumerateArray()
                .OrderBy(item => item.GetStringOrNull("type") == "primary" ? 0 : 1)
                .Select(item => item.GetStringOrNull("uri"))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            : null;
        var match = new AlbumMatchResponse(
            Provider: Id,
            Id: id,
            Artist: releaseArtist,
            Title: release.GetStringOrNull("title") ?? "",
            Year: release.GetPropertyOrNull("year") is { ValueKind: JsonValueKind.Number } year ? year.GetInt32() : null,
            TrackCount: tracks.Count,
            CoverUrl: cover,
            Detail: release.GetStringOrNull("country"));
        return new AlbumMatchDetailResponse(match, genres ?? "", null, tracks);
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new AlbumProviderException("Discogs is not configured. Set Discogs:Token on the server.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.discogs.com/{path}");
            request.Headers.UserAgent.ParseAdd(ProviderText.UserAgent);
            request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={Token}");
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AlbumProviderException("Discogs could not be reached or rejected the request.", exception);
        }
    }

    private static string JoinArtists(JsonElement? artists) =>
        artists is { ValueKind: JsonValueKind.Array } array
            ? string.Join(", ", array.EnumerateArray()
                .Select(item => CleanName(item.GetStringOrNull("name") ?? ""))
                .Where(item => item.Length > 0))
            : "";

    // Discogs disambiguates identical artist names with a numeric suffix such as "Artist (2)".
    private static string CleanName(string value) => DisambiguationSuffix().Replace(value.Trim(), "");

    private static (int Disc, int Number) ParsePosition(string? position, int fallbackNumber)
    {
        var match = position is null ? null : DiscTrackPosition().Match(position);
        return match is { Success: true }
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : (1, fallbackNumber);
    }

    private static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
            total = total * 60 + number;
        }

        return total > 0 ? total : null;
    }

    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex DisambiguationSuffix();

    [GeneratedRegex(@"^(\d+)[-.](\d+)$")]
    private static partial Regex DiscTrackPosition();
}
