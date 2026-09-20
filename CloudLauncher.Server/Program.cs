using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using CloudLauncher.Server;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Net;
using CloudLauncher.Server.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Prevent ASP.NET from remapping JWT claim names (sub → nameidentifier, etc.)
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
// Short response cache + per-platform pacing for the mod-platform proxy, so every launcher's update
// check adds up to a queue at the stores' rate limits instead of a burst that gets the whole server
// address throttled (see Net/UpstreamGuard.cs).
// Its own size-limited cache rather than the shared IMemoryCache: a SizeLimit on the shared one would make
// every future cache.Set elsewhere in the server throw unless it also sets a Size.
builder.Services.AddSingleton(_ => new UpstreamGuard(
    new Microsoft.Extensions.Caching.Memory.MemoryCache(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 96L * 1024 * 1024 })));

// Retry transient database failures instead of turning them into 500s. On 2026-09-19 the box's
// disk filled, Postgres PANICked and spent minutes in crash recovery answering every connection with
// "57P03: the database system is not yet accepting connections"; the API stayed up and answered every
// request with an unhandled exception, so launchers showed a 500 and an empty instance list. A handful
// of spaced retries rides out a recovery like that, and DbUnavailableMiddleware turns what is left
// into an honest 503 the client can wait on.
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connectionString, npgsql =>
    npgsql.EnableRetryOnFailure(maxRetryCount: 4, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));

builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequireDigit = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequiredLength = 8;
        o.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IAccountEmailSender, AccountEmailSender>();
// Scoped (not singleton): this service depends on the scoped UserManager/JwtTokenService
// (and through them the scoped, non-thread-safe AppDbContext). A singleton would capture a
// single DbContext for the app lifetime, corrupting state across concurrent sign-ins. The
// only cross-request state (the pending-flow dictionary) is static, so it survives regardless.
builder.Services.AddScoped<GoogleAuthService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<PackPermissionResolver>();
builder.Services.AddScoped<ModPermissionResolver>();
builder.Services.AddScoped<SharedWorldPermissionResolver>();
builder.Services.AddScoped<ResourcePackPermissionResolver>();

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

// Rate limiting for the password/email auth endpoints. There is no account lockout, so this is
// the primary defense against online password brute-force, credential stuffing and mail-bombing.
// Keyed by client IP; refresh/token endpoints are intentionally NOT limited (they burst legitimately
// at client startup and aren't password-guessable).
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
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(5000);
    o.Limits.MaxRequestBodySize = 200L * 1024 * 1024; // 200 MB per blob upload
});

var app = builder.Build();

// The database may still be coming up (a container start, or crash recovery after the disk filled).
// Wait for it rather than dying on the first connection: systemd would restart us straight into the
// same wall, and after a few attempts give up entirely.
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
