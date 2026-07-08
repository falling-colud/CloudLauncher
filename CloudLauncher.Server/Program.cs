using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using CloudLauncher.Server;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
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

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connectionString));

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.Run();
