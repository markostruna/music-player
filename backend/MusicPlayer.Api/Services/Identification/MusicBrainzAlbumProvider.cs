using System.Text;
using System.Text.Json;
using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services.Identification;

public sealed class MusicBrainzAlbumProvider(HttpClient http) : IAlbumProvider
{
    public string Id => "musicbrainz";
    public string Name => "MusicBrainz";
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(
        string? artist,
        string? album,
        int? year,
        CancellationToken cancellationToken)
    {
        var terms = new List<string>();
        if (ProviderText.Nullable(album) is { } albumTerm) terms.Add($"release:\"{Escape(albumTerm)}\"");
        if (ProviderText.Nullable(artist) is { } artistTerm) terms.Add($"artist:\"{Escape(artistTerm)}\"");
        if (terms.Count == 0) return [];

        using var document = await GetJsonAsync(
            $"release/?query={Uri.EscapeDataString(string.Join(" AND ", terms))}&fmt=json&limit=12",
            cancellationToken);
        var releases = document?.RootElement.GetPropertyOrNull("releases");
        if (releases is not { ValueKind: JsonValueKind.Array }) return [];

        var matches = new List<AlbumMatchResponse>();
        foreach (var release in releases.Value.EnumerateArray())
        {
            var id = release.GetStringOrNull("id");
            if (id is null) continue;
            var media = release.GetPropertyOrNull("media") is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().ToArray()
                : [];
            var format = media.Select(item => item.GetStringOrNull("format")).FirstOrDefault(item => item is not null);
            var details = new[] { release.GetStringOrNull("country"), format }.Where(item => !string.IsNullOrWhiteSpace(item));
            matches.Add(new AlbumMatchResponse(
                Provider: Id,
                Id: id,
                Artist: ReadArtistCredit(release),
                Title: release.GetStringOrNull("title") ?? "",
                Year: ProviderText.ParseYear(release.GetStringOrNull("date")),
                TrackCount: release.GetPropertyOrNull("track-count") is { ValueKind: JsonValueKind.Number } count ? count.GetInt32() : null,
                CoverUrl: $"https://coverartarchive.org/release/{id}/front-250",
                Detail: string.Join(" · ", details)));
        }

        return matches;
    }

    public async Task<AlbumMatchDetailResponse?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var releaseId)) return null;
        using var document = await GetJsonAsync(
            $"release/{releaseId}?inc=recordings+artist-credits+release-groups+genres&fmt=json",
            cancellationToken);
        if (document is null) return null;
        var release = document.RootElement;
        var tracks = new List<AlbumMatchTrackResponse>();
        var discIndex = 0;
        if (release.GetPropertyOrNull("media") is { ValueKind: JsonValueKind.Array } media)
        {
            foreach (var disc in media.EnumerateArray())
            {
                discIndex++;
                var discNumber = disc.GetPropertyOrNull("position") is { ValueKind: JsonValueKind.Number } position
                    ? position.GetInt32()
                    : discIndex;
                if (disc.GetPropertyOrNull("tracks") is not { ValueKind: JsonValueKind.Array } items) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var length = item.GetPropertyOrNull("length") is { ValueKind: JsonValueKind.Number } milliseconds
                        ? milliseconds.GetDouble() / 1000
                        : (double?)null;
                    tracks.Add(new AlbumMatchTrackResponse(
                        discNumber,
                        item.GetPropertyOrNull("position") is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : tracks.Count + 1,
                        item.GetStringOrNull("title") ?? "",
                        ReadArtistCredit(item),
                        length));
                }
            }
        }

        var releaseGroupId = release.GetPropertyOrNull("release-group")?.GetStringOrNull("id");
        var genre = release.GetPropertyOrNull("genres") is { ValueKind: JsonValueKind.Array } genres
            ? genres.EnumerateArray()
                .OrderByDescending(item => item.GetPropertyOrNull("count") is { ValueKind: JsonValueKind.Number } count ? count.GetInt32() : 0)
                .Select(item => item.GetStringOrNull("name"))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? ""
            : "";
        var match = new AlbumMatchResponse(
            Provider: Id,
            Id: id,
            Artist: ReadArtistCredit(release),
            Title: release.GetStringOrNull("title") ?? "",
            Year: ProviderText.ParseYear(release.GetStringOrNull("date")),
            TrackCount: tracks.Count,
            CoverUrl: $"https://coverartarchive.org/release/{releaseId}/front-500",
            Detail: release.GetStringOrNull("country"));
        return new AlbumMatchDetailResponse(match, genre, releaseGroupId, tracks);
    }

    public async Task<string?> FindReleaseGroupIdAsync(string artist, string album, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"release-group/?query={Uri.EscapeDataString($"releasegroup:\"{Escape(album)}\" AND artist:\"{Escape(artist)}\"")}&fmt=json&limit=1",
            cancellationToken);
        var groups = document?.RootElement.GetPropertyOrNull("release-groups");
        return groups is { ValueKind: JsonValueKind.Array }
            ? groups.Value.EnumerateArray().FirstOrDefault().GetStringOrNull("id")
            : null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await MusicBrainzRateLimiter.WaitAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://musicbrainz.org/ws/2/{path}");
            request.Headers.UserAgent.ParseAdd(ProviderText.UserAgent);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AlbumProviderException("MusicBrainz could not be reached. Please try again later.", exception);
        }
    }

    private static string ReadArtistCredit(JsonElement element)
    {
        if (element.GetPropertyOrNull("artist-credit") is not { ValueKind: JsonValueKind.Array } credits) return "";
        var builder = new StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            builder.Append(credit.GetStringOrNull("name") ?? credit.GetPropertyOrNull("artist")?.GetStringOrNull("name"));
            builder.Append(credit.GetStringOrNull("joinphrase"));
        }

        return builder.ToString().Trim();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
