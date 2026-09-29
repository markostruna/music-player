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
var databasePath = Environment.GetEnvironmentVariable("MUSICPLAYER_DATABASE_PATH") ?? Path.Combine(dataDirectory, "music-library.db");

builder.WebHost.UseUrls(builder.Configuration["Api:Url"] ?? "http://127.0.0.1:5080");
builder.Services.AddDbContext<MusicDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IMusicLibraryService, MusicLibraryService>();
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

public partial class Program;
