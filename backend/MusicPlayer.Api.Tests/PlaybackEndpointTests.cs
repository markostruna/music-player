using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Endpoints;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class PlaybackEndpointTests
{
    [Fact]
    public async Task PlaybackEndpointsRequireAuthenticationCsrfAndIsolateAccounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<MusicDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddScoped<CsrfEndpointFilter>();
        builder.Services.AddScoped<MusicLibraryBrowseService>();
        builder.Services.AddScoped<IPlaybackStatisticsService, PlaybackStatisticsService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PlaybackCoordinator>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapPlaybackEndpoints();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
            await database.Database.EnsureCreatedAsync();
            database.Users.AddRange(
                new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "test" },
                new MusicUser { Id = 2, Email = "two", NormalizedEmail = "TWO", PasswordHash = "test" });
            database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
            database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
            database.Tracks.Add(new MusicTrack
            {
                Id = 1, SourceRootId = 1, FolderId = 1, RelativePath = "1.mp3", Title = "One",
            });
            await database.SaveChangesAsync();
        }
        await app.StartAsync();
        var address = new Uri(app.Urls.Single());
        using var anonymous = CreateClient(address);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/playback/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/playback/statistics")).StatusCode);

        using var first = CreateClient(address, "1");
        var session = Guid.NewGuid().ToString();
        var poll = new PlaybackPollRequest(session, "Browser", -1, 0, false, false);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync("/api/playback/poll", poll)).StatusCode);
        await AddCsrfAsync(first);
        var initial = await ReadAsync(await first.PostAsJsonAsync("/api/playback/poll", poll));
        var command = new PlaybackCommandRequest(session, initial.Revision, "play", [1], 0, null, null);
        var playing = await ReadAsync(await first.PostAsJsonAsync("/api/playback/command", command));
        Assert.Single(playing.Queue!);
        var progress = poll with { Revision = playing.Revision, PositionSeconds = 23, IsPlaying = true };
        await ReadAsync(await first.PostAsJsonAsync("/api/playback/poll", progress));

        using var second = CreateClient(address, "2");
        await AddCsrfAsync(second);
        var other = await ReadAsync(await second.PostAsJsonAsync("/api/playback/poll",
            new PlaybackPollRequest(Guid.NewGuid().ToString(), "Other", -1, 0, false, false)));
        Assert.Empty(other.Queue!);
        var firstHistory = await first.GetFromJsonAsync<PlaybackHistoryResponse[]>("/api/playback/history");
        var secondHistory = await second.GetFromJsonAsync<PlaybackHistoryResponse[]>("/api/playback/history");
        Assert.Equal(23, Assert.Single(firstHistory!).PositionSeconds);
        Assert.Empty(secondHistory!);
        var statistics = await first.GetFromJsonAsync<PlaybackStatisticsResponse>("/api/playback/statistics");
        Assert.Empty(statistics!.RecentlyPlayed);
        Assert.Empty(statistics.ContinueListening);
        var otherStatistics = await second.GetFromJsonAsync<PlaybackStatisticsResponse>("/api/playback/statistics");
        Assert.Empty(otherStatistics!.RecentlyPlayed);
        await app.StopAsync();
    }

    private static HttpClient CreateClient(Uri address, string? user = null)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = address,
        };
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<CsrfToken>("/test/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.Token);
    }

    private static async Task<PlaybackResponse> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaybackResponse>())!;
    }

    private sealed record CsrfToken(string Token);

    private sealed class HeaderAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["X-Test-User"].ToString();
            if (id is not ("1" or "2")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
