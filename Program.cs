using System.Net;
using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchboard.Api.Data;
using Watchboard.Api.Endpoints;
using Watchboard.Api.Models;
using Watchboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddOptions<WatchboardOptions>().Bind(builder.Configuration.GetSection("Watchboard"))
    .Validate(options => options.PriceIntervalMinutes is >= 15 and <= 10080, "PriceIntervalMinutes must be between 15 and 10080.")
    .Validate(options => options.VersionIntervalMinutes is >= 5 and <= 10080, "VersionIntervalMinutes must be between 5 and 10080.")
    .Validate(options => options.AllowedOrigins.All(RequestAccess.IsValidOrigin), "AllowedOrigins must contain exact HTTP(S) origins without paths, wildcards or credentials.")
    .Validate(options => !options.AllowRemoteAccess || options.AllowedOrigins.Length > 0, "AllowRemoteAccess requires explicit AllowedOrigins.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OperationGate>();
builder.Services.AddSingleton<SourceHttp>();
builder.Services.AddSingleton<RefreshCoordinator>();
builder.Services.AddScoped<DashboardReader>();
builder.Services.AddScoped<ReleaseSources>();
builder.Services.AddScoped<PriceSources>();
builder.Services.AddScoped<GameWatchService>();
builder.Services.AddScoped<LocalVersionDetector>();
builder.Services.AddScoped<ILocalVersionDetector>(services => services.GetRequiredService<LocalVersionDetector>());
builder.Services.AddScoped<ConfigurationService>();
builder.Services.AddSingleton<SteamSaleEnrichment>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = UserAccounts.PasswordIterations);
builder.Services.AddSingleton<LoginPasswordVerifier>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Watchboard.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // HTTPS requests get Secure cookies; TLS-terminating proxies opt into SecureCookies.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = UserAccounts.ValidateCookieAsync;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<TimeProvider, IOptions<WatchboardOptions>>((options, clock, config) =>
    {
        options.TimeProvider = clock;
        if (config.Value.SecureCookies) options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization(options => options.AddPolicy(UserAccounts.AdminPolicy, policy => policy.RequireRole("admin")));
var protection = builder.Services.AddDataProtection().SetApplicationName("Watchboard");
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddOptions<KeyManagementOptions>().Configure<IOptions<WatchboardOptions>, IWebHostEnvironment, ILoggerFactory>(
    (keys, config, environment, logging) =>
    {
        // Alongside the ignored database, not wwwroot or the source tree. Tests override DatabasePath.
        var data = Path.GetDirectoryName(Path.GetFullPath(config.Value.DatabasePath, environment.ContentRootPath))!;
        var directory = new DirectoryInfo(Path.Combine(data, "keys"));
        directory.Create();
        keys.XmlRepository = new FileSystemXmlRepository(directory, logging);
    });
builder.Services.AddHostedService<PollingWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("sources", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    var version = typeof(Program).Assembly.GetName().Version!.ToString(3);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Watchboard/{version} (personal local price and release monitor)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("writes", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "local", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
    options.AddPolicy("auth", _ => RateLimitPartition.GetFixedWindowLimiter("local-auth", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
});
builder.Services.AddDbContext<WatchboardDbContext>((services, options) =>
{
    var config = services.GetRequiredService<IOptions<WatchboardOptions>>().Value;
    var path = Path.GetFullPath(config.DatabasePath, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
});

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; connect-src 'self' https://api.github.com https://registry.npmjs.org https://nodejs.org https://dotnetcli.blob.core.windows.net; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    var config = context.RequestServices.GetRequiredService<IOptions<WatchboardOptions>>().Value;
    // Container networking is explicit opt-in; forwarded headers never bypass this guard.
    if (!config.AllowRemoteAccess && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { detail = "Watchboard solo admite acceso local. No lo publiques mediante proxies o túneles." });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        var path = context.Request.Path;
        var configBody = path == "/api/configuration/import" || path == "/api/configuration/preview";
        var bodyLimit = configBody ? 256 * 1024 : 16 * 1024;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = bodyLimit;
        if (context.Request.ContentLength > bodyLimit)
        {
            context.Response.StatusCode = 413;
            await context.Response.WriteAsJsonAsync(new { detail = "El cuerpo de la solicitud supera el límite permitido." });
            return;
        }
        var origin = context.Request.Headers.Origin.ToString();
        var trustedOrigin = RequestAccess.IsAllowedOrigin(origin, config);
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!trustedOrigin || fetchSite is not ("" or "same-origin" or "same-site" or "none") ||
            context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-Watchboard-Client"] != "web")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { detail = "Origen o cliente no permitido." });
            return;
        }
    }
    await next(context);
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    // This is an assertion, not an authentication/ownership credential. The signed cookie remains authoritative.
    // It rejects stale multi-step requests from a tab whose cookie was switched to another user elsewhere.
    var expected = context.Request.Headers["X-Watchboard-User"].ToString();
    if (context.Request.Path.StartsWithSegments("/api") && expected.Length != 0 && context.User.Identity?.IsAuthenticated == true &&
        (!Guid.TryParse(expected, out var requestedUser) || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actualUser) || requestedUser != actualUser))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { detail = "La sesión ha cambiado. Vuelve a entrar." });
        return;
    }
    await next(context);
});
app.UseRateLimiter();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAccounts();
app.MapWatchboard();
app.MapConfiguration();
app.Map("/api/{**path}", () => Results.Problem(statusCode: 404, title: "Ruta de API no encontrada"));
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    var config = scope.ServiceProvider.GetRequiredService<IOptions<WatchboardOptions>>().Value;
    var db = scope.ServiceProvider.GetRequiredService<WatchboardDbContext>();
    var newDatabase = !File.Exists(Path.GetFullPath(config.DatabasePath, app.Environment.ContentRootPath));
    await db.Database.MigrateAsync();
    if (config.SeedDefaults && newDatabase) await DefaultWatches.SeedAsync(db, CancellationToken.None);
    if (config.SeedDefaults) await DefaultWatches.EnsureV2Async(db, CancellationToken.None);
}
await app.RunAsync();

public partial class Program;
