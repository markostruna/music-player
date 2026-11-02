using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using MusicPlayer.Api.Services.Identification;

namespace MusicPlayer.Api.Tests;

public sealed class SpotifyAlbumProviderTests
{
    [Fact]
    public async Task TokenAuthorizationFailureExplainsThatCredentialsWereRejected()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spotify:ClientId"] = "test-client-id",
                ["Spotify:ClientSecret"] = "test-client-secret",
            })
            .Build();
        var provider = new SpotifyAlbumProvider(http, configuration);

        var exception = await Assert.ThrowsAsync<AlbumProviderException>(() =>
            provider.SearchAsync("Test Artist", "Test Album", null, CancellationToken.None));

        Assert.Contains("rejected the Client ID or Client secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlbumSearchRequestsAtMostTenResults()
    {
        var searchQuery = "";
        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "accounts.spotify.com")
            {
                return JsonResponse("""{"access_token":"test-access-token","expires_in":3600}""");
            }

            searchQuery = request.RequestUri.Query;
            return JsonResponse("""{"albums":{"items":[]}}""");
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spotify:ClientId"] = "limit-test-client",
                ["Spotify:ClientSecret"] = "limit-test-secret",
            })
            .Build();
        var provider = new SpotifyAlbumProvider(http, configuration);

        await provider.SearchAsync("Test Artist", "Test Album", null, CancellationToken.None);

        Assert.Contains("limit=10", searchQuery, StringComparison.Ordinal);
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
