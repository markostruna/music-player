using System.Text.Json;

namespace MusicPlayer.Api.Services;

public sealed class MusicBrainzClient(
    HttpClient http,
    ILogger<MusicBrainzClient> logger,
    IConfiguration configuration) : IMusicBrainzClient
{
    private static readonly SemaphoreSlim MusicBrainzThrottle = new(1, 1);
    private static DateTimeOffset nextMusicBrainzRequest;
    private const int MaximumImageSize = 10 * 1024 * 1024;
    private readonly string? fanartApiKey = configuration["FanartTv:ApiKey"];
    private readonly string? fanartClientKey = configuration["FanartTv:ClientKey"];

    public async Task<MusicBrainzResult?> FindArtistAsync(string artist, CancellationToken cancellationToken)
    {
        using var search = await GetMusicBrainzJsonAsync(
            $"artist/?query={Uri.EscapeDataString($"artist:\"{EscapeTerm(artist)}\"")}&fmt=json&limit=1",
            cancellationToken);
        var id = FirstEntityId(search, "artists");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var result = await ResolveEntityAsync("artist", id, cancellationToken);
        var fanartImage = await GetFanartArtistImageAsync(id, cancellationToken);
        if (result is null)
        {
            return fanartImage is null
                ? null
                : new MusicBrainzResult(id, "", fanartImage.Value.Data, fanartImage.Value.ContentType, "Fanart.tv");
        }

        return fanartImage is null
            ? result
            : result with
            {
                Image = fanartImage.Value.Data,
                ImageContentType = fanartImage.Value.ContentType,
                ImageProvider = "Fanart.tv",
            };
    }

    private async Task<(byte[] Data, string ContentType)?> GetFanartArtistImageAsync(
        string musicBrainzId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fanartApiKey))
        {
            logger.LogWarning("Fanart.tv developer API key is not configured; using the linked artist image fallback.");
            return null;
        }

        try
        {
            var uri = $"https://webservice.fanart.tv/v3/music/{Uri.EscapeDataString(musicBrainzId)}?api_key={Uri.EscapeDataString(fanartApiKey)}";
            if (!string.IsNullOrWhiteSpace(fanartClientKey))
            {
                uri += $"&client_key={Uri.EscapeDataString(fanartClientKey)}";
            }

            using var response = await http.GetAsync(
                uri,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Fanart.tv lookup returned HTTP {StatusCode} for MusicBrainz artist {MusicBrainzId}.",
                    response.StatusCode,
                    musicBrainzId);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var imageUrl = ReadFirstImageUrl(document.RootElement, "artistthumb")
                ?? ReadFirstImageUrl(document.RootElement, "artistbackground");
            return string.IsNullOrWhiteSpace(imageUrl)
                ? null
                : await GetImageAsync(imageUrl, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Fanart.tv artist image lookup failed for MusicBrainz artist {MusicBrainzId}.", musicBrainzId);
            return null;
        }
    }

    private static string? ReadFirstImageUrl(JsonElement artist, string imageType)
    {
        var images = artist.GetPropertyOrNull(imageType);
        if (images is null || images.Value.ValueKind != JsonValueKind.Array) return null;
        return images.Value.EnumerateArray()
            .Select(image => image.GetStringOrNull("url"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    public async Task<MusicBrainzResult?> FindAlbumAsync(string artist, string album, CancellationToken cancellationToken)
    {
        using var search = await GetMusicBrainzJsonAsync(
            $"release-group/?query={Uri.EscapeDataString($"releasegroup:\"{EscapeTerm(album)}\" AND artist:\"{EscapeTerm(artist)}\"")}&fmt=json&limit=1",
            cancellationToken);
        var id = FirstEntityId(search, "release-groups");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var result = await ResolveEntityAsync("release-group", id, cancellationToken);
        var image = await GetImageAsync($"https://coverartarchive.org/release-group/{Uri.EscapeDataString(id)}/front-500", cancellationToken);
        return result is null
            ? new MusicBrainzResult(id, "", image?.Data, image?.ContentType, image is null ? null : "Cover Art Archive")
            : result with
            {
                Image = image?.Data ?? result.Image,
                ImageContentType = image?.ContentType ?? result.ImageContentType,
                ImageProvider = image is null ? result.ImageProvider : "Cover Art Archive",
            };
    }

    private async Task<MusicBrainzResult?> ResolveEntityAsync(string entityType, string id, CancellationToken cancellationToken)
    {
        using var entity = await GetMusicBrainzJsonAsync(
            $"{entityType}/{Uri.EscapeDataString(id)}?inc=url-rels&fmt=json",
            cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var relations = entity.RootElement.GetPropertyOrNull("relations")?.EnumerateArray().ToArray() ?? [];
        var wikipediaUrl = relations
            .Where(relation => string.Equals(relation.GetStringOrNull("type"), "wikipedia", StringComparison.OrdinalIgnoreCase))
            .Select(relation => relation.GetPropertyOrNull("url")?.GetStringOrNull("resource"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var wikidataUrl = relations
            .Where(relation => string.Equals(relation.GetStringOrNull("type"), "wikidata", StringComparison.OrdinalIgnoreCase))
            .Select(relation => relation.GetPropertyOrNull("url")?.GetStringOrNull("resource"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var summary = string.IsNullOrWhiteSpace(wikipediaUrl)
            ? null
            : await GetWikipediaSummaryAsync(wikipediaUrl, cancellationToken);
        var wikidataId = GetWikidataId(wikidataUrl);
        var wikidata = wikidataId is null || summary is { Description.Length: > 0, Image: not null }
            ? null
            : await GetWikidataDataAsync(wikidataId, cancellationToken);

        var image = summary?.Image is null ? wikidata?.Image : summary?.Image;
        var imageProvider = summary?.Image is not null
            ? "Wikipedia"
            : wikidata?.Image is not null ? "Wikidata" : null;
        return new MusicBrainzResult(
            id,
            string.IsNullOrWhiteSpace(summary?.Description) ? wikidata?.Description ?? "" : summary?.Description ?? "",
            image,
            summary?.Image is null ? wikidata?.ImageContentType : summary?.ImageContentType,
            imageProvider);
    }

    private async Task<(string Description, byte[]? Image, string? ImageContentType)?> GetWikidataDataAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.wikidata.org/wiki/Special:EntityData/{id}.json");
            request.Headers.UserAgent.ParseAdd("AfterhoursMusicPlayer/1.0 (https://github.com/markostruna/music-player)");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var entity = document.RootElement.GetPropertyOrNull("entities")?.GetPropertyOrNull(id);
            var description = entity?.GetPropertyOrNull("descriptions")?.GetPropertyOrNull("en")?.GetStringOrNull("value") ?? "";
            var imageName = entity?.GetPropertyOrNull("claims")?.GetPropertyOrNull("P18")?
                .EnumerateArray().FirstOrDefault()
                .GetPropertyOrNull("mainsnak")?.GetPropertyOrNull("datavalue")?.GetStringOrNull("value");
            var image = string.IsNullOrWhiteSpace(imageName)
                ? null
                : await GetImageAsync($"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(imageName)}?width=600", cancellationToken);
            return (description, image?.Data, image?.ContentType);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Wikidata metadata lookup failed for {EntityId}.", id);
            return null;
        }
    }

    private static string? GetWikidataId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Host.Equals("wikidata.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.wikidata.org", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return id is { Length: > 1 } && id[0] == 'Q' && id[1..].All(char.IsAsciiDigit) ? id : null;
    }

    private async Task<JsonDocument?> GetMusicBrainzJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await MusicBrainzThrottle.WaitAsync(cancellationToken);
            try
            {
                var delay = nextMusicBrainzRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                nextMusicBrainzRequest = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            finally
            {
                MusicBrainzThrottle.Release();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://musicbrainz.org/ws/2/{path}");
            request.Headers.UserAgent.ParseAdd("AfterhoursMusicPlayer/1.0 (https://github.com/markostruna/music-player)");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MusicBrainz lookup returned HTTP {StatusCode} for {Path}.", response.StatusCode, path);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "MusicBrainz metadata lookup failed for {Path}.", path);
            return null;
        }
    }

    private async Task<(string Description, byte[]? Image, string? ImageContentType)?> GetWikipediaSummaryAsync(
        string pageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
                || !(pageUri.Host.Equals("wikipedia.org", StringComparison.OrdinalIgnoreCase)
                    || pageUri.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var title = Uri.UnescapeDataString(pageUri.AbsolutePath[(pageUri.AbsolutePath.LastIndexOf('/') + 1)..]);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://{pageUri.Host}/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}");
            request.Headers.UserAgent.ParseAdd("AfterhoursMusicPlayer/1.0 (https://github.com/markostruna/music-player)");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var description = document.RootElement.GetStringOrNull("extract") ?? "";
            var imageUrl = document.RootElement.GetPropertyOrNull("thumbnail")?.GetStringOrNull("source");
            var image = string.IsNullOrWhiteSpace(imageUrl) ? null : await GetImageAsync(imageUrl, cancellationToken);
            return (description, image?.Data, image?.ContentType);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or UriFormatException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Wikipedia summary lookup failed for {PageUrl}.", pageUrl);
            return null;
        }
    }

    private async Task<(byte[] Data, string ContentType)?> GetImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUri)
                || imageUri.Scheme != Uri.UriSchemeHttps
                || !(imageUri.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                    || imageUri.Host.EndsWith(".coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                    || imageUri.Host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase)
                    || imageUri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase)
                    || imageUri.Host.Equals("assets.fanart.tv", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            using var response = await http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumImageSize)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return data.Length is > 0 and <= MaximumImageSize
                && contentType is "image/jpeg" or "image/png" or "image/gif" or "image/webp"
                ? (data, contentType)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Metadata image download failed for {ImageUrl}.", url);
            return null;
        }
    }

    private static string? FirstEntityId(JsonDocument? document, string propertyName)
    {
        var elements = document?.RootElement.GetPropertyOrNull(propertyName);
        if (elements is null || elements.Value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = elements.Value.EnumerateArray().FirstOrDefault();
        return first.GetStringOrNull("id");
    }

    private static string EscapeTerm(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : null;

    public static string? GetStringOrNull(this JsonElement element, string name) =>
        element.GetPropertyOrNull(name)?.GetString();

    public static string? GetStringOrNull(this JsonElement? element, string name) =>
        element?.GetPropertyOrNull(name)?.GetString();
}
