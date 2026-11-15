using Microsoft.AspNetCore.Http.HttpResults;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class UnscannedFolderEndpoints
{
    public static void MapUnscannedFolderEndpoints(this WebApplication app)
    {
        var folders = app.MapGroup("/api/admin/unscanned-folders")
            .WithTags("Unscanned folders")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));

        folders.MapGet("/", async (
            IUnscannedFolderService service,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListUnscannedFolders")
            .WithSummary("Lists administrator-managed folders that are not scanned into the library.");

        folders.MapPost("/", async Task<Results<
            Created<UnscannedFolderResponse>,
            BadRequest<string>,
            Conflict<string>>> (
            CreateUnscannedFolderRequest request,
            IUnscannedFolderService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var folder = await service.AddAsync(request, cancellationToken);
                return TypedResults.Created($"/api/admin/unscanned-folders/{folder.Id}", folder);
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("AddUnscannedFolder")
            .WithSummary("Registers an existing server folder without scanning it.");

        folders.MapDelete("/{rootId:int}", async Task<Results<NoContent, NotFound, Conflict<string>>> (
            int rootId,
            IUnscannedFolderService service,
            LibraryOperationManager operations,
            CancellationToken cancellationToken) =>
        {
            if (!operations.TryBeginRootRemoval(rootId))
            {
                return TypedResults.Conflict("A library operation is running, so the folder cannot be removed.");
            }

            try
            {
                return await service.RemoveAsync(rootId, cancellationToken)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound();
            }
            finally
            {
                operations.EndRootRemoval(rootId);
            }
        })
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("RemoveUnscannedFolder")
            .WithSummary("Removes an unscanned-folder registration without deleting its files.");

        folders.MapGet("/{rootId:int}", async Task<Results<
            Ok<MusicFolderContentsResponse>,
            NotFound,
            BadRequest<string>,
            Conflict<string>>> (
            int rootId,
            string? path,
            IUnscannedFolderService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var contents = await service.BrowseAsync(rootId, path, cancellationToken);
                return contents is null ? TypedResults.NotFound() : TypedResults.Ok(contents);
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
            .WithName("BrowseUnscannedFolder")
            .WithSummary("Lists folders and files in an administrator-managed unscanned source.");

        folders.MapPut("/{rootId:int}/folders", async Task<Results<NoContent, BadRequest<string>, Conflict<string>>> (
            int rootId,
            string path,
            RenameUnscannedFolderRequest request,
            IUnscannedFolderService service,
            LibraryOperationManager operations,
            CancellationToken cancellationToken) =>
        {
            if (!operations.TryBeginLibraryMutation())
            {
                return TypedResults.Conflict("A library operation is running, so the folder cannot be renamed.");
            }

            try
            {
                await service.RenameFolderAsync(rootId, path, request, cancellationToken);
                return TypedResults.NoContent();
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
            finally
            {
                operations.EndLibraryMutationReservation();
            }
        })
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("RenameUnscannedFolder")
            .WithSummary("Renames a directory in an unscanned source.");

        folders.MapPut("/{rootId:int}/tracks", async Task<Results<NoContent, BadRequest<string>, Conflict<string>>> (
            int rootId,
            string path,
            RenameUnscannedTrackRequest request,
            IUnscannedFolderService service,
            LibraryOperationManager operations,
            CancellationToken cancellationToken) =>
        {
            if (!operations.TryBeginLibraryMutation())
            {
                return TypedResults.Conflict("A library operation is running, so the track cannot be renamed.");
            }

            try
            {
                await service.RenameTrackAsync(rootId, path, request, cancellationToken);
                return TypedResults.NoContent();
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
            finally
            {
                operations.EndLibraryMutationReservation();
            }
        })
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("RenameUnscannedTrack")
            .WithSummary("Renames an audio filename without changing its embedded tags.");

        folders.MapPost("/{rootId:int}/check-album", async Task<Results<
            Ok<CheckUnscannedAlbumResponse>,
            BadRequest<string>,
            Conflict<string>>> (
            int rootId,
            CheckUnscannedAlbumRequest request,
            IUnscannedFolderService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await service.CheckAlbumAsync(rootId, request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
            .WithName("CheckUnscannedAlbum")
            .WithSummary("Checks an unscanned album against the library database.");

        folders.MapPost("/{rootId:int}/import", async Task<Results<
            Accepted<LibraryOperationStatus>,
            BadRequest<string>,
            Conflict<string>>> (
            int rootId,
            ImportUnscannedAlbumRequest request,
            IUnscannedFolderService service,
            IMusicLibraryService music,
            LibraryOperationManager operations,
            CancellationToken cancellationToken) =>
        {
            if (!operations.TryBeginLibraryMutation())
            {
                return TypedResults.Conflict("Another library operation is already running.");
            }

            try
            {
                var importedAlbum = await service.ImportAlbumAsync(rootId, request, cancellationToken);
                var destination = (await music.GetRootsAsync(cancellationToken))
                    .SingleOrDefault(item => item.Id == request.DestinationRootId && item.IsEnabled);
                if (destination is null)
                {
                    return TypedResults.BadRequest("The destination music folder is not available.");
                }

                var operation = operations.StartReservedImportOperation(
                    destination.Id,
                    destination.Name,
                    importedAlbum.RelativePath,
                    importedAlbum.SourceRetained);
                return operation is null
                    ? TypedResults.Conflict("The library operation could not be queued after import.")
                    : TypedResults.Accepted($"/api/admin/operations/{operation.Id}", operation);
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
            finally
            {
                operations.EndLibraryMutationReservation();
            }
        })
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("ImportUnscannedAlbum")
            .WithSummary("Moves an album into a scanned source and queues a scan and metadata refresh.");
    }
}
