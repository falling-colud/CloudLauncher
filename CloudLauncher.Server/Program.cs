using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using CloudLauncher.Server;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Net;
using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Prevent ASP.NET from remapping JWT claim names (sub -> nameidentifier, etc.)
// so our controllers can read claims by their original JWT names.
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required. Supply it via environment variables or user-secrets (the committed appsettings.json ships empty on purpose).");
if (connectionString.Contains("CHANGE_ME", StringComparison.Ordinal))
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres still contains the placeholder password. Supply real credentials via environment variables or user-secrets.");

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || jwtOptions.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters");
if (jwtOptions.SigningKey.Contains("CHANGE_ME", StringComparison.Ordinal))
    throw new InvalidOperationException(
        "Jwt:SigningKey is still the committed placeholder. Supply a real secret via environment variables or user-secrets so JWTs cannot be forged.");

var appOptions = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();
var googleOptions = builder.Configuration.GetSection("Google").Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();
var emailOptions = builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();

var blobOptions = builder.Configuration.GetSection("Blobs").Get<BlobStoreOptions>() ?? new BlobStoreOptions();
if (string.IsNullOrWhiteSpace(blobOptions.RootPath))
    throw new InvalidOperationException("Blobs:RootPath is required");
Directory.CreateDirectory(blobOptions.RootPath);

var launcherOptions = builder.Configuration.GetSection("Launcher").Get<LauncherStoreOptions>() ?? new LauncherStoreOptions();
if (string.IsNullOrWhiteSpace(launcherOptions.RootPath))
    launcherOptions.RootPath = Path.Combine(blobOptions.RootPath, "_launcher");
Directory.CreateDirectory(launcherOptions.RootPath);

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(googleOptions);
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton(blobOptions);
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton(launcherOptions);
builder.Services.AddSingleton<LauncherStore>();
builder.Services.AddHttpClient();
// Outbound routing for the mod-platform proxy. Registers one HttpClient per configured route so a
// CDN block on this server address can be retried through a relay or outbound proxy. Throws on
// malformed "Upstream" config, so a typo surfaces at startup rather than when someone browses mods.
builder.Services.AddUpstreamRouting(builder.Configuration);
// Short response cache plus per-platform pacing for the mod-platform proxy, so launchers' update
// checks queue under the stores' rate limits instead of bursting and getting this server's address
// throttled (see Net/UpstreamGuard.cs). It has its own size-limited cache: a SizeLimit on the
// shared IMemoryCache would make every cache.Set elsewhere throw unless it also set a Size.
builder.Services.AddSingleton(_ => new UpstreamGuard(
    new Microsoft.Extensions.Caching.Memory.MemoryCache(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 96L * 1024 * 1024 })));

// Retry transient database failures (Postgres still recovering, for example) instead of returning
// 500s. What still fails becomes a 503 in DbUnavailableMiddleware.
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connectionString, npgsql =>
    npgsql.EnableRetryOnFailure(maxRetryCount: 4, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));

builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequireDigit = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        // Applies when a password is set or changed. Sign-in never re-checks a stored password
        // against the policy, so accounts made under the old minimum keep working.
        o.Password.RequiredLength = 10;
        o.User.RequireUniqueEmail = true;
        o.User.AllowedUserNameCharacters = UsernameRules.AllowedCharacters;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 8;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddPasswordValidator<CommonPasswordValidator>();

builder.Services.AddSingleton<IAccountEmailSender, AccountEmailSender>();
// Scoped because it depends on the scoped UserManager/JwtTokenService and, through them, the
// non-thread-safe AppDbContext. Its only cross-request state (pending flows) is static.
builder.Services.AddScoped<GoogleAuthService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<PackPermissionResolver>();
builder.Services.AddScoped<ModPermissionResolver>();
builder.Services.AddScoped<SharedWorldPermissionResolver>();
builder.Services.AddScoped<ResourcePackPermissionResolver>();
builder.Services.AddScoped<ContentBundlePermissionResolver>();
// Writes the activity feed: an injectable wrapper around ActivityLog's static methods.
builder.Services.AddScoped<ActivityWriter>();
// Deletes old refresh tokens and abandoned Google sign-ins.
builder.Services.AddHostedService<AuthMaintenance>();
// Upload quota checks, the pending-upload ledger and the unused-file cleanup job.
builder.Services.AddStorageMaintenance();

// nginx on this machine is the only proxy allowed to say who the client is and whether it came in
// over https. The defaults already trust loopback alone and one hop, so only the headers are set.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep JWT claim names as-is (sub, unique_name, etc.)
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// Rate limiting for the password/email auth endpoints, on top of the per-account lockout: it
// limits guessing across many accounts, credential stuffing and mail-bombing. Keyed by client IP,
// which is the real client's once UseForwardedHeaders has run. Refresh/token endpoints are not
// limited: they burst at client startup and don't take a password.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // User-search autocomplete: a much higher limit than the auth endpoints, since every keystroke in
    // the invite box is a request, but still bounded because it answers questions about other
    // people's accounts. Keyed by IP because this runs before UseAuthentication, so there is no user
    // id yet.
    options.AddPolicy(RateLimitPolicies.UserSearch, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(5000);
    o.Limits.MaxRequestBodySize = 200L * 1024 * 1024; // 200 MB per blob upload
    o.AddServerHeader = false;
});

var app = builder.Build();

// Email links and the Google redirect are built from PublicBaseUrl. A localhost value in
// production breaks them without any other obvious error.
if (app.Environment.IsProduction()
    && (!Uri.TryCreate(appOptions.PublicBaseUrl, UriKind.Absolute, out var publicBase) || publicBase.IsLoopback))
    app.Logger.LogWarning(
        "App:PublicBaseUrl is '{PublicBaseUrl}', which is not a public address. Email links and Google sign-in " +
        "will not work until it is set to the server's public https URL.", appOptions.PublicBaseUrl);

if (!string.Equals(appOptions.DirectHttp, AppOptions.DirectHttpUpdatesOnly, StringComparison.OrdinalIgnoreCase)
    && !string.Equals(appOptions.DirectHttp, AppOptions.DirectHttpFull, StringComparison.OrdinalIgnoreCase))
    app.Logger.LogWarning("App:DirectHttp is '{DirectHttp}'; expected UpdatesOnly or Full. Treating it as UpdatesOnly.",
        appOptions.DirectHttp);

// The database may still be starting or recovering. Wait for it instead of failing on the first
// connection, which would just make systemd restart us until it gives up.
await WaitForDatabaseAsync(app.Services, app.Logger);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

static async Task WaitForDatabaseAsync(IServiceProvider services, ILogger logger)
{
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Database.CanConnectAsync()) return;
        }
        catch (Exception ex) when (DbUnavailableMiddleware.IsUnavailable(ex))
        {
            if (DateTimeOffset.UtcNow > deadline) throw;
            logger.LogWarning("Database not ready yet (attempt {Attempt}): {Message}", attempt, ex.Message);
        }
        if (DateTimeOffset.UtcNow > deadline)
            throw new TimeoutException("Database did not become available within five minutes.");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)));
    }
}

// First, so every response carries the security headers, the gate's refusals included. They are
// written as the response starts, by which time the forwarded proto below has been applied.
app.UseMiddleware<SecurityHeadersMiddleware>();
// Before UseForwardedHeaders: the gate needs the real peer address and nginx's own X-Forwarded-Proto.
app.UseMiddleware<DirectHttpGate>();
// Early, so the rate limiter partitions and every log line see the real client address.
app.UseForwardedHeaders();

// Share links (https://<this host>/invitations/<token>) get opened in browsers, where this
// [Authorize] API route would return a blank 401. A browser sends no Authorization header and
// asks for HTML, so only that case gets wwwroot/invite.html, which explains how to redeem the
// link in the launcher. Everything else, including the launcher's own calls, passes through.
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (HttpMethods.IsGet(request.Method)
        && request.Path.StartsWithSegments("/invitations", out var tail)
        && tail.Value is { Length: > 1 } rest && rest.IndexOf('/', 1) < 0
        && !request.Headers.ContainsKey("Authorization")
        && PrefersHtml(request))
    {
        var page = app.Environment.WebRootFileProvider.GetFileInfo("invite.html");
        if (page.Exists)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(page);
            return;
        }
    }
    await next();
});

// text/html listed explicitly and not ranked below JSON: browsers send that, API clients don't.
static bool PrefersHtml(HttpRequest request)
{
    double html = 0, json = 0;
    foreach (var type in request.GetTypedHeaders().Accept)
    {
        var q = type.Quality ?? 1;
        if (type.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) html = Math.Max(html, q);
        else if (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) json = Math.Max(json, q);
    }
    return html > 0 && html >= json;
}

// The legal pages live at /privacy and /terms, and security.txt at its well-known address. The
// static file provider will not serve anything under a dot-folder, so that one is rewritten too.
app.Use((context, next) =>
{
    var request = context.Request;
    if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && WebRootAlias(request.Path) is { } file
        && app.Environment.WebRootFileProvider.GetFileInfo(file).Exists)
        request.Path = "/" + file;
    return next(context);
});

static string? WebRootAlias(PathString path)
{
    var p = path.Value?.TrimEnd('/');
    if (string.Equals(p, Legal.PrivacyPath, StringComparison.OrdinalIgnoreCase)) return "privacy.html";
    if (string.Equals(p, Legal.TermsPath, StringComparison.OrdinalIgnoreCase)) return "terms.html";
    if (string.Equals(p, "/.well-known/security.txt", StringComparison.OrdinalIgnoreCase)) return "security.txt";
    return null;
}

// Serve the marketing / download site from wwwroot ("/" -> index.html). Placed before the
// rate limiter and auth so the public landing page and installer download aren't gated.
app.UseDefaultFiles();
app.UseStaticFiles();

// Before anything that touches the database, so a database that is down reaches the client as a
// 503 with a Retry-After rather than an unhandled 500.
app.UseMiddleware<DbUnavailableMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.Run();
