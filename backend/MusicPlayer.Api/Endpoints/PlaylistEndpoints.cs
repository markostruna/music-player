using System.Security.Claims;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class PlaylistEndpoints
{
    public static void MapPlaylistEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/playlists").RequireAuthorization().WithTags("Playlists");
        group.MapGet("/random/{kind}", async (string kind, MusicLibraryBrowseService library, CancellationToken ct) =>
        {
            if (kind is not ("random-albums" or "album-radio" or "random-tracks" or "track-radio"))
                return Results.NotFound();
            var albums = kind is "random-albums" or "album-radio";
            var count = kind switch { "random-albums" => 10, "random-tracks" => 100, _ => 1 };
            return Results.Ok(await library.GetRandomTracksAsync(albums, count, ct));
        }).WithName("GetRandomPlaylist").WithSummary("Selects random albums or tracks.");
        group.MapGet("/", async (ClaimsPrincipal principal, IPlaylistService playlists, CancellationToken ct) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            return Results.Ok(await playlists.ListAsync(userId, ct));
        }).WithName("ListPlaylists").WithSummary("Lists the current user's playlists.");
        group.MapGet("/{id:int}", async (int id, ClaimsPrincipal principal, IPlaylistService playlists, CancellationToken ct) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            var playlist = await playlists.GetAsync(userId, id, ct);
            return playlist is null ? Results.NotFound() : Results.Ok(playlist);
        }).WithName("GetPlaylist").WithSummary("Gets a personal playlist.");
        group.MapPost("/", async (UpdatePlaylistRequest request, ClaimsPrincipal principal, IPlaylistService playlists, CancellationToken ct) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            try
            {
                var playlist = await playlists.CreateAsync(userId, request, ct);
                return Results.Created($"/api/playlists/{playlist.Id}", playlist);
            }
            catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: 400); }
        }).AddEndpointFilter<CsrfEndpointFilter>().WithName("CreatePlaylist").WithSummary("Creates a personal playlist.");
        group.MapPut("/{id:int}", async (int id, UpdatePlaylistRequest request, ClaimsPrincipal principal, IPlaylistService playlists, CancellationToken ct) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            try
            {
                var playlist = await playlists.UpdateAsync(userId, id, request, ct);
                return playlist is null ? Results.NotFound() : Results.Ok(playlist);
            }
            catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: 400); }
        }).AddEndpointFilter<CsrfEndpointFilter>().WithName("UpdatePlaylist").WithSummary("Renames or replaces the ordered tracks of a playlist.");
        group.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, IPlaylistService playlists, CancellationToken ct) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            return await playlists.DeleteAsync(userId, id, ct) ? Results.NoContent() : Results.NotFound();
        }).AddEndpointFilter<CsrfEndpointFilter>().WithName("DeletePlaylist").WithSummary("Deletes a personal playlist.");
    }
}
