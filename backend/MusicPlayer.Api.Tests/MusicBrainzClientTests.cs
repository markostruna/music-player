using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class MusicBrainzClientTests
{
    [Fact]
    public async Task ArtistLookupUsesFanartTvImageInPreferenceToLinkedFallbacks()
    {
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+2QAAAAASUVORK5CYII=");
        var fanartRequest = "";
        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "musicbrainz.org" && uri.AbsolutePath.EndsWith("/artist/", StringComparison.Ordinal))
            {
                return JsonResponse("""{"artists":[{"id":"test-artist-mbid"}]}""");
            }

            if (uri.Host == "musicbrainz.org" && uri.AbsolutePath.EndsWith("/artist/test-artist-mbid", StringComparison.Ordinal))
            {
                return JsonResponse("""{"relations":[]}""");
            }

            if (uri.Host == "webservice.fanart.tv")
            {
                fanartRequest = uri.ToString();
                return JsonResponse("""{"artistthumb":[{"url":"https://assets.fanart.tv/fanart/music/test/artistthumb/test.png"}]}""");
            }

            if (uri.Host == "assets.fanart.tv")
            {
                var content = new ByteArrayContent(imageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FanartTv:ApiKey"] = "test-api-key",
                ["FanartTv:ClientKey"] = "test-client-key",
            })
            .Build();
        var client = new MusicBrainzClient(http, NullLogger<MusicBrainzClient>.Instance, configuration);

        var result = await client.FindArtistAsync("Test Artist", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("test-artist-mbid", result.MusicBrainzId);
        Assert.Equal(imageBytes, result.Image);
        Assert.Equal("image/png", result.ImageContentType);
        Assert.Equal("Fanart.tv", result.ImageProvider);
        Assert.Contains("api_key=test-api-key", fanartRequest, StringComparison.Ordinal);
        Assert.Contains("client_key=test-client-key", fanartRequest, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
