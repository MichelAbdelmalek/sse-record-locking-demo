using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LockDemo.Api.Data;
using LockDemo.Api.Events;
using LockDemo.Api.Locking;
using LockDemo.Api.Records;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Default (see README).");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<LockOptions>(config.GetSection("Locks"));

// Kerberos/GSS encryption is not used by local Postgres or Neon; skip the probe (and its missing-library error).
var csb = new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(csb.ConnectionString));
builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<LockService>();
builder.Services.AddScoped<TabSessionService>();
builder.Services.AddSingleton<TabRegistry>();
builder.Services.AddHostedService<LockMaintenanceService>();

// One instance: deliver events in memory. Several instances: fan out through Postgres LISTEN/NOTIFY.
if (string.Equals(config["Events:Mode"], "Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ILockEventBus, PostgresLockEventBus>();
    builder.Services.AddHostedService<PostgresLockEventListener>();
}
else
{
    builder.Services.AddSingleton<ILockEventBus, InMemoryLockEventBus>();
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .WithMethods("GET", "POST", "PUT", "DELETE")
    .WithHeaders("Content-Type", RecordsEndpoints.TabHeader)));

// Behind Render's proxy the client IP arrives in X-Forwarded-For; trust only the nearest hop.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// A public demo: cap writes and open streams per client IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("writes", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("streams", ctx => RateLimitPartition.GetConcurrencyLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 20, QueueLimit = 0 }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.XFrameOptions = "DENY";
    h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    await next();
});
app.UseCors();
app.UseRateLimiter();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(db, TimeProvider.System);
}

app.MapHealthChecks("/api/health");
app.MapRecords();
app.MapEvents();

app.Run();

public partial class Program;
