using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Endpoints;
using MusicPlayer.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterhours");
Directory.CreateDirectory(dataDirectory);
builder.Configuration
    .AddJsonFile(Path.Combine(dataDirectory, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();
var databasePath = Environment.GetEnvironmentVariable("MUSICPLAYER_DATABASE_PATH") ?? Path.Combine(dataDirectory, "music-library.db");

builder.WebHost.UseUrls(builder.Configuration["Api:Url"] ?? "http://127.0.0.1:5080");
builder.Services.AddDbContext<MusicDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IMusicLibraryService, MusicLibraryService>();
builder.Services.AddHttpClient<IMusicBrainzClient, MusicBrainzClient>("MetadataProviders", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Logging.AddFilter("System.Net.Http.HttpClient.MetadataProviders", LogLevel.Warning);
builder.Services.AddScoped<CsrfEndpointFilter>();
builder.Services.AddScoped<Microsoft.AspNetCore.Identity.IPasswordHasher<MusicUser>, Microsoft.AspNetCore.Identity.PasswordHasher<MusicUser>>();
builder.Services.AddValidation();
var cookieSecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "afterhours.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "afterhours.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = cookieSecurePolicy;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var securityStamp = context.Principal?.FindFirstValue("security_stamp");
            if (!int.TryParse(idValue, out var id) || string.IsNullOrEmpty(securityStamp))
            {
                context.RejectPrincipal();
                return;
            }

            var database = context.HttpContext.RequestServices.GetRequiredService<MusicDbContext>();
            var account = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id);
            if (account is null || !account.IsEnabled || account.SecurityStamp != securityStamp)
            {
                context.RejectPrincipal();
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownProxies.Add(IPAddress.Loopback);
forwardedHeaders.KnownProxies.Add(IPAddress.IPv6Loopback);

var app = builder.Build();
app.UseForwardedHeaders(forwardedHeaders);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ArtistMetadata" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_ArtistMetadata" PRIMARY KEY,
            "Artist" TEXT NOT NULL,
            "Description" TEXT NOT NULL,
            "DescriptionEdited" INTEGER NOT NULL,
            "MusicBrainzId" TEXT NOT NULL,
            "ImageFileName" TEXT NULL,
            "ImageSourceRootId" INTEGER NULL,
            "ImageRelativePath" TEXT NULL,
            "ImageMissing" INTEGER NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "AlbumMetadata" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_AlbumMetadata" PRIMARY KEY,
            "Artist" TEXT NOT NULL,
            "Album" TEXT NOT NULL,
            "Description" TEXT NOT NULL,
            "DescriptionEdited" INTEGER NOT NULL,
            "MusicBrainzId" TEXT NOT NULL,
            "ImageFileName" TEXT NULL,
            "ImageSourceRootId" INTEGER NULL,
            "ImageRelativePath" TEXT NULL,
            "ImageMissing" INTEGER NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        """);
    await EnsureMetadataImageColumnsAsync(database);
    var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
    await accounts.BootstrapAdminAsync(
        Environment.GetEnvironmentVariable("MUSICPLAYER_BOOTSTRAP_EMAIL"),
        Environment.GetEnvironmentVariable("MUSICPLAYER_BOOTSTRAP_PASSWORD"),
        app.Lifetime.ApplicationStopping);
}

app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithSummary("Checks whether the music API is available.");
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapLibraryEndpoints();

app.Run();

static async Task EnsureMetadataImageColumnsAsync(MusicDbContext database)
{
    var connection = database.Database.GetDbConnection();
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) await connection.OpenAsync();

    try
    {
        foreach (var (table, column, definition) in new[]
        {
            ("ArtistMetadata", "ImageSourceRootId", "INTEGER NULL"),
            ("ArtistMetadata", "ImageRelativePath", "TEXT NULL"),
            ("AlbumMetadata", "ImageSourceRootId", "INTEGER NULL"),
            ("AlbumMetadata", "ImageRelativePath", "TEXT NULL"),
        })
        {
            await using var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await inspect.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            await reader.DisposeAsync();
            if (!found)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                await alter.ExecuteNonQueryAsync();
            }
        }
    }
    finally
    {
        if (openedHere) await connection.CloseAsync();
    }
}

public partial class Program;
