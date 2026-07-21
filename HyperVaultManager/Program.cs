using System.Security.Claims;
using System.Text.Json;
using HyperVaultManager.Data;
using HyperVaultManager.Dtos;
using HyperVaultManager.Models;
using HyperVaultManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, _, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

var dataPath = builder.Configuration["Manager:DataPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataPath);
var dbPath = Path.Combine(dataPath, "hypervault.db");
var connStr = $"Data Source={dbPath}";

builder.Services.AddDbContext<ManagerDbContext>(opt => opt.UseSqlite(connStr));
builder.Services.AddSingleton<IJobQueue, JobQueue>();
builder.Services.AddSingleton<AgentClient>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddHostedService<JobRunnerWorker>();
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHostedService<HostHealthWorker>();
builder.Services.AddHttpContextAccessor();

// Persist data-protection keys (cookie auth) to the mounted volume so sessions
// survive container redeploys instead of being invalidated.
var keyPath = Path.Combine(dataPath, "keys");
Directory.CreateDirectory(keyPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));

const string AuthScheme = "cookie";
builder.Services
    .AddAuthentication(AuthScheme)
    .AddCookie(AuthScheme, options =>
    {
        options.Cookie.Name = "hypervault_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // For API calls, respond with 401 JSON instead of redirecting.
        options.Events.OnRedirectToLogin = ctx => WriteAuthStatusAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "unauthorized", "Authentication required.");
        options.Events.OnRedirectToAccessDenied = ctx => WriteAuthStatusAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to do that.");
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", p => p.RequireAuthenticatedUser().RequireClaim("role", Roles.Admin));
});

builder.Services.AddHttpClient("agent")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));

// Long-running HTTP client for File-Level Restore: materializing a chain +
// read-only VHDX mount and streaming downloads can take minutes, so it must
// not be bound by the default 20s timeout. Streaming downloads also need
// HttpCompletionOption.ResponseHeadersRead (see AgentClient.GetFlrFileAsync).
builder.Services.AddHttpClient("agent-long")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// ---- DB init + seed admin ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
    db.Database.EnsureCreated();
    // Ensure the users table exists even if an older DB was created before auth.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AppUsers" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Username" TEXT NOT NULL,
            "PasswordHash" TEXT NOT NULL,
            "Role" TEXT NOT NULL DEFAULT 'user',
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "CreatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00',
            "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00',
            "LastLoginAt" TEXT NULL
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUsers_Username" ON "AppUsers" ("Username");
        """);

    if (!db.Users.Any())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        db.Users.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = hasher.Hash("admin"),
            Role = Roles.Admin,
            Enabled = true
        });
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Seeded default admin user 'admin' with password 'admin'. CHANGE IT IMMEDIATELY from Settings.");
    }

    // Add new scheduling columns to the Jobs table if missing (existing DBs).
    EnsureJobsScheduleColumns(db, app.Logger as Microsoft.Extensions.Logging.ILogger);
    // Add Mode + source VM columns to RestoreRuns if missing (existing DBs).
    EnsureRestoreColumns(db, app.Logger as Microsoft.Extensions.Logging.ILogger);
    // Add Tags catalog + VmTags join (existing DBs created before tags existed).
    EnsureTagsSchema(db, app.Logger as Microsoft.Extensions.Logging.ILogger);
}

static async Task WriteAuthStatusAsync(HttpContext ctx, int code, string errorCode, string message)
{
    if (ctx.Response.HasStarted) return;
    ctx.Response.StatusCode = code;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new ApiError(errorCode, message, ctx.TraceIdentifier));
}

// SQLite doesn't support ADD COLUMN IF NOT EXISTS, so check PRAGMA table_info first.
static void EnsureJobsScheduleColumns(ManagerDbContext db, Microsoft.Extensions.Logging.ILogger logger)
{
    var existing = db.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('Jobs')")
        .ToHashSet();

    var add = new (string Col, string Ddl)[]
    {
        ("ScheduleType", "TEXT NOT NULL DEFAULT 'manual'"),
        ("ScheduleTime", "TEXT NOT NULL DEFAULT '00:00'"),
        ("ScheduleWeekdays", "TEXT NOT NULL DEFAULT ''"),
        ("ScheduleDayOfMonth", "INTEGER NULL"),
        ("TimeZone", "TEXT NOT NULL DEFAULT 'UTC'")
    };

    foreach (var (col, ddl) in add)
    {
        if (!existing.Contains(col))
        {
            db.Database.ExecuteSqlRaw($"ALTER TABLE Jobs ADD COLUMN \"{col}\" {ddl};");
            logger.LogInformation("Added column Jobs.{Col}", col);
        }
    }
}

// Adds the restore-mode columns introduced for the redesigned restore screen.
static void EnsureRestoreColumns(ManagerDbContext db, Microsoft.Extensions.Logging.ILogger logger)
{
    var existing = db.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('RestoreRuns')")
        .ToHashSet();

    var add = new (string Col, string Ddl)[]
    {
        ("Mode", "TEXT NOT NULL DEFAULT 'new_vm'"),
        ("SourceVmId", "INTEGER NULL"),
        ("SourceVmName", "TEXT NULL")
    };

    foreach (var (col, ddl) in add)
    {
        if (!existing.Contains(col))
        {
            db.Database.ExecuteSqlRaw($"ALTER TABLE RestoreRuns ADD COLUMN \"{col}\" {ddl};");
            logger.LogInformation("Added column RestoreRuns.{Col}", col);
        }
    }
}

// Adds the Tags catalog + VmTags join introduced for real VM tagging.
// Works on DBs created by EnsureCreated before tags existed (no EF migrations).
static void EnsureTagsSchema(ManagerDbContext db, Microsoft.Extensions.Logging.ILogger logger)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Tags" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Key" TEXT NOT NULL,
            "Label" TEXT NOT NULL,
            "Color" TEXT NOT NULL DEFAULT ''
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tags_Key" ON "Tags" ("Key");
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "VmTags" (
            "VmId" INTEGER NOT NULL,
            "TagId" INTEGER NOT NULL,
            PRIMARY KEY ("VmId", "TagId"),
            FOREIGN KEY ("VmId") REFERENCES "VirtualMachines" ("Id") ON DELETE CASCADE,
            FOREIGN KEY ("TagId") REFERENCES "Tags" ("Id") ON DELETE CASCADE
        );
        """);

    // Seed the default catalog once (idempotent).
    if (!db.Tags.Any())
    {
        var defaults = new (string Key, string Label, string Color)[]
        {
            ("prod", "Prod", ""),
            ("projects", "Projects", ""),
            ("essential", "Essential", ""),
            ("work", "Work", ""),
            ("archive", "Archive", ""),
            ("personal", "Personal", ""),
        };
        foreach (var (key, label, color) in defaults)
            db.Tags.Add(new Tag { Key = key, Label = label, Color = color });
        db.SaveChanges();
        logger.LogInformation("Seeded default tag catalog ({Count} tags).", defaults.Length);
    }
}

// Applies the friendly scheduling fields to a job, derives the cron and the
// next-run time (in the job's own timezone).
static void ApplySchedule(BackupJob job, JobCreateDto dto)
{
    job.ScheduleType = string.IsNullOrWhiteSpace(dto.ScheduleType) ? ScheduleTypes.Manual : dto.ScheduleType;
    job.ScheduleTime = string.IsNullOrWhiteSpace(dto.ScheduleTime) ? "00:00" : dto.ScheduleTime;
    job.ScheduleWeekdays = dto.ScheduleWeekdays ?? "";
    job.ScheduleDayOfMonth = dto.ScheduleDayOfMonth;
    var tzId = ScheduleBuilder.IsValidTimeZone(dto.TimeZone) ? dto.TimeZone.Trim() : "UTC";
    job.TimeZone = tzId;
    job.CronSchedule = ScheduleBuilder.BuildCron(job.ScheduleType, job.ScheduleTime, job.ScheduleWeekdays, job.ScheduleDayOfMonth);
    var tz = ScheduleBuilder.ResolveTimeZone(tzId);
    job.NextRunAt = job.Enabled
        ? CronNextRun.Next(job.CronSchedule, DateTimeOffset.UtcNow, tz)
        : null;
}

// ---- error handling ----
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (AgentCallException ex)
    {
        await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "agent_error", ex.Message);
    }
    catch (ArgumentException ex)
    {
        await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_request", ex.Message);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
    {
        await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
    }
    catch (InvalidOperationException ex) when (
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("overwrite", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("cannot ", StringComparison.OrdinalIgnoreCase))
    {
        await WriteErrorAsync(ctx, StatusCodes.Status409Conflict, "conflict", ex.Message);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled error {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        await WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Force browsers to always revalidate static assets (app.js, index.html, ...)
    // via ETag/Last-Modified instead of silently serving stale cached copies
    // after a redeploy. 304 responses are cheap, correctness matters more here.
    OnPrepareResponse = ctx =>
    {
        var h = ctx.Context.Response.Headers;
        h.CacheControl = "no-cache";
        h["Pragma"] = "no-cache";
        h.Expires = "-1";
    }
});

// All API endpoints require authentication unless explicitly marked AllowAnonymous.
var api = app.MapGroup("/api").RequireAuthorization();

// ---------- HEALTH (public) ----------
api.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// ---------- AUTH ----------
api.MapPost("/auth/login", async (LoginDto dto, ManagerDbContext db, PasswordHasher hasher, HttpContext ctx) =>
{
    dto = dto with { Username = (dto.Username ?? "").Trim().ToLowerInvariant() };
    if (string.IsNullOrEmpty(dto.Username) || string.IsNullOrEmpty(dto.Password))
        throw new ArgumentException("Username and password are required.");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == dto.Username);
    if (user is null || !user.Enabled || !hasher.Verify(dto.Password, user.PasswordHash))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Results.Json(new ApiError("invalid_credentials", "Invalid username or password."), statusCode: StatusCodes.Status401Unauthorized);
    }
    user.LastLoginAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await SignInAsync(ctx, user);
    return Results.Ok(Map.Me(user));
}).AllowAnonymous();

api.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("cookie");
    return Results.Ok(new { ok = true });
});

api.MapGet("/auth/me", (HttpContext ctx) =>
{
    var id = CurrentUserId(ctx);
    return Results.Ok(new { id, username = CurrentUsername(ctx), role = CurrentRole(ctx) });
});

api.MapPost("/auth/change-password", async (ChangePasswordDto dto, ManagerDbContext db, PasswordHasher hasher, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(dto.NewPassword) || dto.NewPassword.Length < 4)
        throw new ArgumentException("New password must be at least 4 characters.");
    var id = CurrentUserId(ctx);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id)
        ?? throw new InvalidOperationException($"User {id} not found");
    if (!hasher.Verify(dto.CurrentPassword, user.PasswordHash))
        throw new ArgumentException("Current password is incorrect.");
    user.PasswordHash = hasher.Hash(dto.NewPassword);
    user.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---------- USERS (admin only) ----------
api.MapGet("/users", async (ManagerDbContext db) =>
{
    var list = await db.Users.OrderBy(u => u.Username).ToListAsync();
    return Results.Ok(list.Select(Map.Me));
}).RequireAuthorization("admin");

api.MapPost("/users", async (UserCreateDto dto, ManagerDbContext db, PasswordHasher hasher) =>
{
    var username = (dto.Username ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(username)) throw new ArgumentException("Username is required.");
    if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 4)
        throw new ArgumentException("Password must be at least 4 characters.");
    if (await db.Users.AnyAsync(u => u.Username == username))
        throw new InvalidOperationException($"User '{username}' already exists");
    var user = new AppUser
    {
        Username = username,
        PasswordHash = hasher.Hash(dto.Password),
        Role = dto.Role == Roles.Admin ? Roles.Admin : Roles.User,
        Enabled = dto.Enabled
    };
    db.Users.Add(user); await db.SaveChangesAsync();
    return Results.Created($"/api/users/{user.Id}", Map.Me(user));
}).RequireAuthorization("admin");

api.MapPut("/users/{id:int}", async (int id, UserUpdateDto dto, ManagerDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id)
        ?? throw new InvalidOperationException($"User {id} not found");
    var username = (dto.Username ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(username)) throw new ArgumentException("Username is required.");
    if (await db.Users.AnyAsync(u => u.Username == username && u.Id != id))
        throw new InvalidOperationException($"User '{username}' already exists");
    // Prevent removing the last admin role.
    if (user.Role == Roles.Admin && dto.Role != Roles.Admin)
    {
        var adminCount = await db.Users.CountAsync(u => u.Role == Roles.Admin && u.Enabled);
        if (adminCount <= 1) throw new InvalidOperationException("Cannot demote the last enabled admin.");
    }
    user.Username = username;
    user.Role = dto.Role == Roles.Admin ? Roles.Admin : Roles.User;
    user.Enabled = dto.Enabled;
    user.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(Map.Me(user));
}).RequireAuthorization("admin");

api.MapPost("/users/{id:int}/reset-password", async (int id, ResetPasswordDto dto, ManagerDbContext db, PasswordHasher hasher) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id)
        ?? throw new InvalidOperationException($"User {id} not found");
    if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 4)
        throw new ArgumentException("Password must be at least 4 characters.");
    user.PasswordHash = hasher.Hash(dto.Password);
    user.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
}).RequireAuthorization("admin");

api.MapDelete("/users/{id:int}", async (int id, ManagerDbContext db, HttpContext ctx) =>
{
    if (id == CurrentUserId(ctx))
        throw new InvalidOperationException("You cannot delete your own account.");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id)
        ?? throw new InvalidOperationException($"User {id} not found");
    if (user.Role == Roles.Admin)
    {
        var adminCount = await db.Users.CountAsync(u => u.Role == Roles.Admin && u.Enabled);
        if (adminCount <= 1) throw new InvalidOperationException("Cannot delete the last admin.");
    }
    db.Users.Remove(user); await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("admin");

// ---------- HOSTS ----------
api.MapGet("/hosts", async (ManagerDbContext db) =>
{
    var hosts = await db.Hosts.OrderBy(h => h.Name).ToListAsync();
    var counts = await db.VirtualMachines
        .GroupBy(v => v.HostId)
        .Select(g => new { g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.Key, x => x.Count);
    return Results.Ok(hosts.Select(h => Map.Host(h, counts.GetValueOrDefault(h.Id))));
});

api.MapGet("/hosts/{id:int}", async (int id, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    var vmCount = await db.VirtualMachines.CountAsync(v => v.HostId == id);
    return Results.Ok(Map.Host(host, vmCount));
});

api.MapPost("/hosts", async (HostCreateDto dto, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name)) throw new ArgumentException("Name is required");
    if (string.IsNullOrWhiteSpace(dto.IpOrFqdn)) throw new ArgumentException("IpOrFqdn is required");
    if (await db.Hosts.AnyAsync(h => h.Name == dto.Name, ct))
        throw new InvalidOperationException($"Host '{dto.Name}' already exists");

    // Build a transient (untracked) host to probe the agent BEFORE persisting,
    // so we never save a host that can't actually be reached.
    var probe = new HyperVHost
    {
        Name = dto.Name.Trim(),
        IpOrFqdn = dto.IpOrFqdn.Trim(),
        Port = dto.Port <= 0 ? 5443 : dto.Port,
        UseHttps = dto.UseHttps,
        ApiToken = dto.ApiToken ?? string.Empty,
        CertificateFingerprint = dto.CertificateFingerprint,
    };

    AgentHealthResult health;
    try
    {
        health = await agent.GetHealthAsync(probe, ct);
    }
    catch (Exception ex)
    {
        return Results.Json(new ApiError("host_unreachable", $"Connection test failed: {ex.Message}"),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    if (!health.IsReady)
    {
        return Results.Json(new ApiError("host_not_ready",
            $"Agent is reachable but not ready (live={health.LiveStatus}, ready={health.ReadyStatus})."),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // Connection is good -> persist as Online and sync its VMs in the same request.
    probe.Notes = dto.Notes;
    probe.Status = HostStatuses.Online;
    probe.LastSeenAt = DateTimeOffset.UtcNow;
    db.Hosts.Add(probe);
    await db.SaveChangesAsync(ct);
    var host = probe;

    try
    {
        var vms = await agent.GetVmsAsync(host, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var snap in vms)
        {
            db.VirtualMachines.Add(new VirtualMachine
            {
                HostId = host.Id, ExternalId = snap.Id, Name = snap.Name, State = snap.State,
                Generation = snap.Generation, MemoryBytes = snap.MemoryBytes, DiskSizeBytes = snap.DiskSizeBytes,
                LastSyncedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
    }
    catch (Exception ex)
    {
        // Host is online & saved; initial sync is best-effort and must not fail the create.
        app.Logger.LogWarning(ex, "Host {HostId} created & online but initial VM sync failed", host.Id);
    }

    var vmCount = await db.VirtualMachines.CountAsync(v => v.HostId == host.Id, ct);
    return Results.Created($"/api/hosts/{host.Id}", Map.Host(host, vmCount));
});

api.MapPut("/hosts/{id:int}", async (int id, HostUpdateDto dto, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    host.Name = dto.Name.Trim();
    host.IpOrFqdn = dto.IpOrFqdn.Trim();
    host.Port = dto.Port <= 0 ? host.Port : dto.Port;
    host.UseHttps = dto.UseHttps;
    if (!string.IsNullOrWhiteSpace(dto.ApiToken)) host.ApiToken = dto.ApiToken;
    host.CertificateFingerprint = dto.CertificateFingerprint;
    host.Notes = dto.Notes;
    host.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(Map.Host(host, await db.VirtualMachines.CountAsync(v => v.HostId == id)));
});

api.MapDelete("/hosts/{id:int}", async (int id, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    db.Hosts.Remove(host);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

api.MapPost("/hosts/{id:int}/test", async (int id, AgentClient agent, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    try
    {
        var health = await agent.GetHealthAsync(host);
        host.Status = health.IsReady ? HostStatuses.Online : HostStatuses.Offline;
        host.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { status = host.Status, live = health.LiveStatus, ready = health.ReadyStatus });
    }
    catch (Exception ex)
    {
        host.Status = HostStatuses.Offline;
        await db.SaveChangesAsync();
        return Results.Ok(new { status = HostStatuses.Offline, error = ex.Message });
    }
});

api.MapPost("/hosts/{id:int}/sync-vms", async (int id, AgentClient agent, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    var vms = await agent.GetVmsAsync(host);
    var now = DateTimeOffset.UtcNow;
    var existing = await db.VirtualMachines.Where(v => v.HostId == id).ToListAsync();
    foreach (var snap in vms)
    {
        var ent = existing.FirstOrDefault(e => e.ExternalId == snap.Id);
        if (ent is null)
        {
            db.VirtualMachines.Add(new VirtualMachine
            {
                HostId = id, ExternalId = snap.Id, Name = snap.Name, State = snap.State,
                Generation = snap.Generation, MemoryBytes = snap.MemoryBytes, DiskSizeBytes = snap.DiskSizeBytes,
                LastSyncedAt = now
            });
        }
        else
        {
            ent.Name = snap.Name; ent.State = snap.State; ent.Generation = snap.Generation;
            ent.MemoryBytes = snap.MemoryBytes; ent.DiskSizeBytes = snap.DiskSizeBytes; ent.LastSyncedAt = now;
        }
    }
    await db.SaveChangesAsync();
    var result = await db.VirtualMachines.Where(v => v.HostId == id).ToListAsync();
    return Results.Ok(result.Select(v => Map.Vm(v, host.Name)));
});

api.MapGet("/hosts/{id:int}/vms/{vmId:int}/restore-points", async (int id, int vmId, AgentClient agent, ManagerDbContext db) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    var vm = await db.VirtualMachines.FirstOrDefaultAsync(v => v.Id == vmId && v.HostId == id)
        ?? throw new InvalidOperationException($"VM {vmId} not found");
    return Results.Ok(await agent.GetRestorePointsAsync(host, vm.ExternalId));
});

// ---------- STORAGES ----------
api.MapGet("/storages", async (ManagerDbContext db) =>
{
    var list = await db.Storages.OrderBy(s => s.Name).ToListAsync();
    return Results.Ok(list.Select(Map.Storage));
});

api.MapPost("/storages", async (StorageCreateDto dto, ManagerDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name)) throw new ArgumentException("Name is required");
    if (string.IsNullOrWhiteSpace(dto.Path)) throw new ArgumentException("Path is required");
    var s = new StorageTarget { Name = dto.Name.Trim(), Type = dto.Type, Path = dto.Path, Notes = dto.Notes };
    db.Storages.Add(s); await db.SaveChangesAsync();
    return Results.Created($"/api/storages/{s.Id}", Map.Storage(s));
});

api.MapPut("/storages/{id:int}", async (int id, StorageCreateDto dto, ManagerDbContext db) =>
{
    var s = await db.Storages.FirstOrDefaultAsync(x => x.Id == id)
        ?? throw new InvalidOperationException($"Storage {id} not found");
    s.Name = dto.Name.Trim(); s.Type = dto.Type; s.Path = dto.Path; s.Notes = dto.Notes;
    await db.SaveChangesAsync();
    return Results.Ok(Map.Storage(s));
});

api.MapDelete("/storages/{id:int}", async (int id, ManagerDbContext db) =>
{
    var s = await db.Storages.FirstOrDefaultAsync(x => x.Id == id)
        ?? throw new InvalidOperationException($"Storage {id} not found");
    db.Storages.Remove(s); await db.SaveChangesAsync();
    return Results.NoContent();
});

// Aggregate disk capacity for every storage/vault. For each storage we look up a
// host that backs up to it (via jobs) and ask its agent for the volume stats of the
// storage path. Returns a map { storageId: StorageStatsDto | null }.
api.MapGet("/storages/stats", async (AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var storages = await db.Storages.AsNoTracking().ToListAsync(ct);
    var jobs = await db.Jobs.Include(j => j.Host).AsNoTracking().ToListAsync(ct);
    var hostsByStorage = jobs
        .Where(j => j.Host is not null)
        .GroupBy(j => j.StorageId)
        .ToDictionary(g => g.Key, g => g.Select(j => j.Host!).DistinctBy(h => h.Id).ToList());

    async Task<KeyValuePair<int, StorageStatsDto?>> ResolveAsync(StorageTarget s)
    {
        // Cap each storage's resolution so one slow/offline host can't stall the panel.
        using var storageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        storageCts.CancelAfter(TimeSpan.FromSeconds(10));

        if (!hostsByStorage.TryGetValue(s.Id, out var hosts) || hosts.Count == 0)
        {
            return new(s.Id, null);
        }

        foreach (var host in hosts)
        {
            try
            {
                var stats = await agent.GetStorageStatsAsync(host, s.Path, storageCts.Token).ConfigureAwait(false);
                if (stats is null) continue;
                var total = stats["totalBytes"]?.GetValue<long>() ?? 0;
                if (total <= 0) continue;
                var free = stats["freeBytes"]?.GetValue<long>() ?? 0;
                return new(s.Id, new StorageStatsDto(total, free, Math.Max(0, total - free), host.Name));
            }
            catch (OperationCanceledException) when (storageCts.IsCancellationRequested) { break; }
            catch { /* host unreachable / rejected path — try the next one */ }
        }
        return new(s.Id, null);
    }

    var resolved = await Task.WhenAll(storages.Select(ResolveAsync));
    return Results.Ok(resolved.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => (object?)kv.Value));
});

// ---------- VMS ----------
api.MapGet("/vms", async (int? hostId, ManagerDbContext db) =>
{
    var q = db.VirtualMachines.Include(v => v.Host).Include(v => v.VmTags).ThenInclude(vt => vt.Tag).AsQueryable();
    if (hostId.HasValue) q = q.Where(v => v.HostId == hostId);
    var list = await q.OrderByDescending(v => v.Name).ToListAsync();
    var result = new List<VmViewDto>();
    foreach (var v in list)
    {
        // last 10 backup runs for this VM (newest first), reversed so the history
        // reads left-to-right as time progresses; rightmost bar == most recent run.
        var recent = await db.BackupRuns.Where(r => r.VmId == v.Id)
            .OrderByDescending(r => r.QueuedAt).Take(10).ToListAsync();
        var last = recent.FirstOrDefault();
        var history = recent.AsEnumerable().Reverse().ToList();
        result.Add(Map.Vm(v, v.Host?.Name ?? "", last, history));
    }
    return Results.Ok(result);
});

// ---------- TAGS ----------
// Catalog: list all tags (used by the VM tag picker).
api.MapGet("/tags", async (ManagerDbContext db) =>
{
    var tags = await db.Tags.OrderBy(t => t.Label).ToListAsync();
    return Results.Ok(tags.Select(Map.Tag));
});

// Create a new tag in the catalog.
api.MapPost("/tags", async (TagCreateDto dto, ManagerDbContext db) =>
{
    var key = (dto.Key ?? "").Trim().ToLowerInvariant();
    var label = string.IsNullOrWhiteSpace(dto.Label) ? key : dto.Label.Trim();
    if (string.IsNullOrWhiteSpace(key))
        throw new ArgumentException("Tag key is required.");
    if (await db.Tags.AnyAsync(t => t.Key == key))
        throw new InvalidOperationException($"A tag with key '{key}' already exists");
    var tag = new Tag { Key = key, Label = label, Color = (dto.Color ?? "").Trim() };
    db.Tags.Add(tag); await db.SaveChangesAsync();
    return Results.Created($"/api/tags/{tag.Id}", Map.Tag(tag));
});

// Delete a tag from the catalog (cascade-removes all VM assignments).
api.MapDelete("/tags/{id:int}", async (int id, ManagerDbContext db) =>
{
    var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id)
        ?? throw new InvalidOperationException($"Tag {id} not found");
    db.Tags.Remove(tag); await db.SaveChangesAsync();
    return Results.NoContent();
});

// Replace a VM's tags with the given set (full replace semantics).
api.MapPut("/vms/{vmId:int}/tags", async (int vmId, VmTagsAssignDto dto, ManagerDbContext db) =>
{
    var vm = await db.VirtualMachines.Include(v => v.VmTags).FirstOrDefaultAsync(v => v.Id == vmId)
        ?? throw new InvalidOperationException($"VM {vmId} not found");
    var desired = (dto?.TagIds ?? Array.Empty<int>()).Distinct().ToHashSet();
    // drop assignments no longer desired
    foreach (var existing in vm.VmTags.Where(vt => !desired.Contains(vt.TagId)).ToList())
        db.VmTags.Remove(existing);
    // add new ones (only if the tag exists)
    var validIds = (await db.Tags.Where(t => desired.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();
    foreach (var tid in desired.Where(id => validIds.Contains(id) && !vm.VmTags.Any(vt => vt.TagId == id)))
        vm.VmTags.Add(new VmTag { TagId = tid });
    await db.SaveChangesAsync();
    // return refreshed tag list
    var fresh = await db.VirtualMachines.Include(v => v.VmTags).ThenInclude(vt => vt.Tag)
        .FirstOrDefaultAsync(v => v.Id == vmId);
    return Results.Ok((fresh?.VmTags ?? new List<VmTag>())
        .Where(vt => vt.Tag != null)
        .Select(vt => Map.Tag(vt.Tag)).ToList());
});

// ---------- JOBS ----------
api.MapGet("/jobs", async (ManagerDbContext db) =>
{
    var jobs = await db.Jobs.Include(j => j.Host).Include(j => j.Vm).Include(j => j.Storage)
        .OrderByDescending(j => j.CreatedAt).ToListAsync();
    return Results.Ok(jobs.Select(Map.Job));
});

api.MapPost("/jobs", async (JobCreateDto dto, ManagerDbContext db) =>
{
    if (await db.Jobs.AnyAsync(j => j.HostId == dto.HostId && j.VmId == dto.VmId && j.Name == dto.Name))
        throw new InvalidOperationException("A job with that name already exists for the VM");
    var vm = await db.VirtualMachines.FirstOrDefaultAsync(v => v.Id == dto.VmId)
        ?? throw new InvalidOperationException($"VM {dto.VmId} not found");
    var job = new BackupJob
    {
        Name = dto.Name.Trim(),
        HostId = dto.HostId, VmId = dto.VmId, StorageId = dto.StorageId,
        Type = dto.Type == JobTypes.Incremental ? JobTypes.Incremental : JobTypes.Full,
        RetentionDays = dto.RetentionDays, Enabled = dto.Enabled
    };
    ApplySchedule(job, dto);
    db.Jobs.Add(job); await db.SaveChangesAsync();
    await db.Entry(job).Reference(j => j.Host).LoadAsync();
    await db.Entry(job).Reference(j => j.Vm).LoadAsync();
    await db.Entry(job).Reference(j => j.Storage).LoadAsync();
    return Results.Created($"/api/jobs/{job.Id}", Map.Job(job));
});

api.MapPut("/jobs/{id:int}", async (int id, JobCreateDto dto, ManagerDbContext db) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id)
        ?? throw new InvalidOperationException($"Job {id} not found");
    job.Name = dto.Name.Trim(); job.HostId = dto.HostId; job.VmId = dto.VmId;
    job.StorageId = dto.StorageId; job.Type = dto.Type;
    job.RetentionDays = dto.RetentionDays; job.Enabled = dto.Enabled;
    ApplySchedule(job, dto);
    await db.SaveChangesAsync();
    return Results.Ok();
});

api.MapPost("/jobs/{id:int}/run-now", async (int id, ManagerDbContext db, IJobQueue queue) =>
{
    var job = await db.Jobs.Include(j => j.Host).Include(j => j.Vm).Include(j => j.Storage)
        .FirstOrDefaultAsync(j => j.Id == id)
        ?? throw new InvalidOperationException($"Job {id} not found");
    var run = await SchedulerFire.FireJobAsync(db, queue, job, default);
    return Results.Accepted($"/api/backups/{run.Id}", new { run.Id });
});

api.MapDelete("/jobs/{id:int}", async (int id, ManagerDbContext db) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id)
        ?? throw new InvalidOperationException($"Job {id} not found");
    db.Jobs.Remove(job); await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---------- BACKUP RUNS / HISTORY ----------
api.MapGet("/backups", async (int? hostId, int? vmId, int? jobId, string? status, int? limit, ManagerDbContext db) =>
{
    var q = db.BackupRuns.Include(r => r.Host).Include(r => r.Vm).Include(r => r.Storage).Include(r => r.Job).AsQueryable();
    if (hostId.HasValue) q = q.Where(r => r.HostId == hostId);
    if (vmId.HasValue) q = q.Where(r => r.VmId == vmId);
    if (jobId.HasValue) q = q.Where(r => r.JobId == jobId);
    if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
    q = q.OrderByDescending(r => r.QueuedAt).Take(limit ?? 200);
    var list = await q.ToListAsync();
    return Results.Ok(list.Select(Map.BackupRun));
});

api.MapGet("/backups/{id:int}", async (int id, ManagerDbContext db) =>
{
    var run = await db.BackupRuns.Include(r => r.Host).Include(r => r.Vm).Include(r => r.Storage).Include(r => r.Job)
        .FirstOrDefaultAsync(r => r.Id == id)
        ?? throw new InvalidOperationException($"Backup {id} not found");
    return Results.Ok(Map.BackupRun(run));
});

// Manual backup from a VM
api.MapPost("/hosts/{hostId:int}/vms/{vmId:int}/backup", async (int hostId, int vmId, ManualBackupDto dto, ManagerDbContext db, IJobQueue queue) =>
{
    var vm = await db.VirtualMachines.FirstOrDefaultAsync(v => v.Id == vmId && v.HostId == hostId)
        ?? throw new InvalidOperationException($"VM {vmId} not found");
    var storage = await db.Storages.FirstOrDefaultAsync(s => s.Id == dto.StorageId)
        ?? throw new InvalidOperationException($"Storage {dto.StorageId} not found");
    var run = new BackupRun
    {
        JobId = dto.JobId, HostId = hostId, VmId = vmId, StorageId = dto.StorageId,
        Type = dto.Type == JobTypes.Incremental ? JobTypes.Incremental : JobTypes.Full,
        Status = RunStatuses.Queued, QueuedAt = DateTimeOffset.UtcNow
    };
    db.BackupRuns.Add(run); await db.SaveChangesAsync();
    queue.Enqueue(new BackupJobRequest(run.Id));
    return Results.Accepted($"/api/backups/{run.Id}", new { run.Id });
});

// ---------- VERIFY ----------
api.MapGet("/verifications", async (ManagerDbContext db) =>
{
    var list = await db.VerificationRuns.Include(v => v.Host).OrderByDescending(v => v.QueuedAt).Take(200).ToListAsync();
    return Results.Ok(list.Select(Map.Verify));
});

api.MapPost("/backups/{id:int}/verify", async (int id, ManagerDbContext db, IJobQueue queue) =>
{
    var run = await db.BackupRuns.Include(r => r.Host).FirstOrDefaultAsync(r => r.Id == id)
        ?? throw new InvalidOperationException($"Backup {id} not found");
    if (run.Host is null) throw new InvalidOperationException("Backup has no host");
    var target = run.ResultPath;
    if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("Backup has no result path to verify");
    var v = new VerificationRun
    {
        BackupRunId = id, HostId = run.HostId, Kind = VerifyKinds.Chain,
        TargetPath = target, Status = RunStatuses.Queued, QueuedAt = DateTimeOffset.UtcNow
    };
    db.VerificationRuns.Add(v); await db.SaveChangesAsync();
    queue.Enqueue(new VerifyJobRequest(v.Id));
    return Results.Accepted($"/api/verifications/{v.Id}", new { v.Id });
});

api.MapPost("/verify", async (VerifyDto dto, ManagerDbContext db, IJobQueue queue) =>
{
    if (await db.Hosts.AllAsync(h => h.Id != dto.HostId)) throw new InvalidOperationException($"Host {dto.HostId} not found");
    var v = new VerificationRun
    {
        BackupRunId = dto.BackupRunId, HostId = dto.HostId,
        Kind = dto.Kind == VerifyKinds.Restore ? VerifyKinds.Restore : VerifyKinds.Chain,
        TargetPath = dto.TargetPath, Status = RunStatuses.Queued, QueuedAt = DateTimeOffset.UtcNow
    };
    db.VerificationRuns.Add(v); await db.SaveChangesAsync();
    queue.Enqueue(new VerifyJobRequest(v.Id));
    return Results.Accepted($"/api/verifications/{v.Id}", new { v.Id });
});

// Verify the latest completed backup of a job (job-centric entry point for the UI).
api.MapPost("/jobs/{id:int}/verify", async (int id, ManagerDbContext db, IJobQueue queue) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id)
        ?? throw new InvalidOperationException($"Job {id} not found");
    var run = await db.BackupRuns
        .Where(r => r.JobId == id && r.Status == RunStatuses.Succeeded && r.ResultPath != null && r.ResultPath != "")
        .OrderByDescending(r => r.CompletedAt ?? r.QueuedAt)
        .FirstOrDefaultAsync()
        ?? throw new InvalidOperationException($"No completed backup found for job '{job.Name}' to verify");
    var v = new VerificationRun
    {
        BackupRunId = run.Id, HostId = run.HostId, Kind = VerifyKinds.Chain,
        TargetPath = run.ResultPath!, Status = RunStatuses.Queued, QueuedAt = DateTimeOffset.UtcNow
    };
    db.VerificationRuns.Add(v); await db.SaveChangesAsync();
    queue.Enqueue(new VerifyJobRequest(v.Id));
    return Results.Accepted($"/api/verifications/{v.Id}", new { v.Id, BackupRunId = run.Id });
});

// ---------- RESTORE ----------
api.MapGet("/restores", async (ManagerDbContext db) =>
{
    var list = await db.RestoreRuns.Include(r => r.TargetHost).OrderByDescending(r => r.QueuedAt).Take(200).ToListAsync();
    return Results.Ok(list.Select(Map.Restore));
});

api.MapPost("/restore", async (RestoreDto dto, ManagerDbContext db, IJobQueue queue) =>
{
    if (await db.Hosts.AllAsync(h => h.Id != dto.TargetHostId)) throw new InvalidOperationException($"Target host {dto.TargetHostId} not found");

    // Resolve source host from the chosen VM when the caller omits it.
    var sourceHostId = dto.SourceHostId;
    var sourceVmName = dto.SourceVmName;
    if (dto.SourceVmId is int vmId)
    {
        var vm = await db.VirtualMachines.FirstOrDefaultAsync(v => v.Id == vmId);
        if (vm is null) throw new InvalidOperationException($"VM {vmId} not found");
        if (sourceHostId <= 0) sourceHostId = vm.HostId;
        if (string.IsNullOrWhiteSpace(sourceVmName)) sourceVmName = vm.Name;
    }
    if (sourceHostId <= 0) sourceHostId = dto.TargetHostId; // best-effort fallback

    var mode = string.IsNullOrWhiteSpace(dto.Mode) ? RestoreModes.NewVm : dto.Mode.Trim().ToLowerInvariant();
    if (mode != RestoreModes.NewVm && mode != RestoreModes.DiskOnly)
        throw new ArgumentException($"Unknown restore mode '{dto.Mode}'. Expected 'new_vm' or 'disk_only'.");

    // NewName identifies the restored VM (new_vm) or prefixes the materialized disk files (disk_only).
    if (!dto.OverwriteExisting && string.IsNullOrWhiteSpace(dto.NewName))
        throw new ArgumentException("NewName is required when not overwriting (safety check)");

    var r = new RestoreRun
    {
        BackupRunId = dto.BackupRunId, SourceHostId = sourceHostId, TargetHostId = dto.TargetHostId,
        SourceVmId = dto.SourceVmId, SourceVmName = sourceVmName, Mode = mode,
        RestorePointPath = dto.RestorePointPath, Destination = dto.Destination, NewName = dto.NewName,
        TargetBackupId = dto.TargetBackupId, OverwriteExisting = dto.OverwriteExisting,
        Status = RunStatuses.Queued, QueuedAt = DateTimeOffset.UtcNow
    };
    db.RestoreRuns.Add(r); await db.SaveChangesAsync();
    queue.Enqueue(new RestoreJobRequest(r.Id));
    return Results.Accepted($"/api/restores/{r.Id}", new { r.Id });
});

// ---------- FILE-LEVEL RESTORE (FLR) proxy ----------
// The browser only ever talks to the manager. The manager adds the bearer token
// and forwards to the agent that owns the chain. Only volumeId + relative path
// are accepted for browse/download; the agent enforces confinement.
api.MapPost("/hosts/{id:int}/flr/sessions", async (int id, FlrSessionCreateDto dto, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    if (string.IsNullOrWhiteSpace(dto.RestorePointPath))
        throw new ArgumentException("RestorePointPath is required.");
    var session = await agent.CreateFlrSessionAsync(host, dto.RestorePointPath, dto.TargetBackupId, dto.TtlMinutes, ct)
        ?? throw new InvalidOperationException("Agent did not return a file-level restore session.");
    return Results.Ok(session);
});

api.MapGet("/hosts/{id:int}/flr/sessions/{sessionId}", async (int id, string sessionId, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    try { return Results.Ok(await agent.GetFlrSessionAsync(host, sessionId, ct)); }
    catch (AgentCallException ex) when (ex.StatusCode == 404) { return Results.NotFound(new { code = "session_expired" }); }
});

api.MapGet("/hosts/{id:int}/flr/sessions/{sessionId}/ls", async (int id, string sessionId, HttpContext ctx, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    var volumeId = ctx.Request.Query["volumeId"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(volumeId))
        throw new ArgumentException("volumeId is required.");
    try
    {
        var entries = await agent.ListFlrEntriesAsync(host, sessionId, volumeId, string.IsNullOrEmpty(path) ? null : path, ct);
        return Results.Ok(entries);
    }
    catch (AgentCallException ex) when (ex.StatusCode == 404)
    {
        return Results.NotFound(new { code = "session_expired", message = "File-level restore session expired." });
    }
});

// Streams a file from the agent to the browser without buffering it in the
// manager. Propagates Range / Content-Range / Content-Length / Content-Type /
// Content-Disposition so the browser can do resumable downloads with the right
// filename. A 404 upstream (expired session) is forwarded as 404.
api.MapGet("/hosts/{id:int}/flr/sessions/{sessionId}/get", async (int id, string sessionId, HttpContext ctx, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    var volumeId = ctx.Request.Query["volumeId"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(volumeId) || string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("volumeId and path are required.");
    var range = ctx.Request.Headers.Range.ToString();
    var upstream = await agent.GetFlrFileAsync(host, sessionId, volumeId, path, string.IsNullOrWhiteSpace(range) ? null : range, ct);
    try
    {
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        CopyFlrHeader(upstream, ctx.Response.Headers, "Content-Type");
        CopyFlrHeader(upstream, ctx.Response.Headers, "Content-Length");
        CopyFlrHeader(upstream, ctx.Response.Headers, "Content-Range");
        CopyFlrHeader(upstream, ctx.Response.Headers, "Accept-Ranges");
        CopyFlrHeader(upstream, ctx.Response.Headers, "Content-Disposition");
        CopyFlrHeader(upstream, ctx.Response.Headers, "Last-Modified");
        await upstream.Content.CopyToAsync(ctx.Response.Body, ct);
    }
    finally { upstream.Dispose(); }
});

api.MapDelete("/hosts/{id:int}/flr/sessions/{sessionId}", async (int id, string sessionId, AgentClient agent, ManagerDbContext db, CancellationToken ct) =>
{
    var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id)
        ?? throw new InvalidOperationException($"Host {id} not found");
    try { await agent.CloseFlrSessionAsync(host, sessionId, ct); }
    catch (AgentCallException ex) when (ex.StatusCode == 404) { /* already closed/expired */ }
    return Results.NoContent();
});

// Copies a response header from either the content or message header collection.
static void CopyFlrHeader(HttpResponseMessage src, IHeaderDictionary dst, string name)
{
    if ((src.Content?.Headers.TryGetValues(name, out var values) ?? false) || src.Headers.TryGetValues(name, out values))
        dst[name] = values.ToArray();
}

// ---------- DASHBOARD ----------
api.MapGet("/dashboard", async (ManagerDbContext db) =>
{
    var hosts = await db.Hosts.ToListAsync();
    var vms = await db.VirtualMachines.ToListAsync();
    var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
    var backups = await db.BackupRuns.Include(r => r.Host).Include(r => r.Vm).Include(r => r.Storage).Include(r => r.Job)
        .OrderByDescending(r => r.QueuedAt).Take(500).ToListAsync();

    var succeededVmIds = backups.Where(b => b.Status == RunStatuses.Succeeded).Select(b => b.VmId).ToHashSet();
    var vmsWithoutBackup = vms.Count(v => !succeededVmIds.Contains(v.Id));

    var dto = new DashboardDto(
        TotalHosts: hosts.Count,
        OnlineHosts: hosts.Count(h => h.Status == HostStatuses.Online),
        OfflineHosts: hosts.Count(h => h.Status != HostStatuses.Online),
        TotalVms: vms.Count,
        VmsWithoutBackup: vmsWithoutBackup,
        BackupsLast24h: backups.Count(b => b.QueuedAt >= cutoff),
        FailedBackupsLast24h: backups.Count(b => b.QueuedAt >= cutoff && b.Status == RunStatuses.Failed),
        EstimatedStorageBytes: backups.Where(b => b.Status == RunStatuses.Succeeded).Sum(b => b.SizeBytes),
        RecentBackups: backups.Take(10).Select(Map.BackupRun).ToList(),
        RecentFailures: backups.Where(b => b.Status == RunStatuses.Failed).Take(5).Select(Map.BackupRun).ToList());

    return Results.Ok(dto);
});

app.MapFallbackToFile("index.html");
app.Run();
return;

// ---------- helpers ----------
static async Task WriteErrorAsync(HttpContext ctx, int code, string errorCode, string message)
{
    if (ctx.Response.HasStarted) return;
    ctx.Response.StatusCode = code;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new ApiError(errorCode, message, ctx.TraceIdentifier));
}

static async Task SignInAsync(HttpContext ctx, AppUser user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new("role", user.Role),
    };
    var identity = new ClaimsIdentity(claims, "cookie", ClaimTypes.Name, "role");
    await ctx.SignInAsync("cookie", new ClaimsPrincipal(identity));
}

static int CurrentUserId(HttpContext ctx) =>
    int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
static string CurrentUsername(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "";
static string CurrentRole(HttpContext ctx) => ctx.User.FindFirstValue("role") ?? "";

static class Map
{
    public static HostViewDto Host(HyperVHost h, int vmCount) => new(
        h.Id, h.Name, h.IpOrFqdn, h.Port, h.UseHttps, h.Status, h.AgentVersion, h.LastSeenAt,
        h.Notes, !string.IsNullOrWhiteSpace(h.ApiToken), vmCount);

    public static StorageViewDto Storage(StorageTarget s) =>
        new(s.Id, s.Name, s.Type, s.Path, s.Notes, s.CreatedAt);

    public static VmViewDto Vm(VirtualMachine v, string hostName, BackupRun? last = null, IReadOnlyList<BackupRun>? history = null) => new(
        v.Id, v.HostId, hostName, v.ExternalId, v.Name, v.State, v.Generation, v.MemoryBytes,
        v.DiskSizeBytes, v.LastSyncedAt, last?.CompletedAt, last?.Status,
        (history ?? Array.Empty<BackupRun>())
            .Select(r => new BackupHistoryEntryDto(r.Status, r.CompletedAt ?? r.QueuedAt))
            .ToList(),
        (v.VmTags ?? new List<VmTag>())
            .Where(vt => vt.Tag != null)
            .Select(vt => new TagDto(vt.Tag.Id, vt.Tag.Key, vt.Tag.Label, vt.Tag.Color ?? ""))
            .ToList());

    public static TagDto Tag(Tag t) => new(t.Id, t.Key, t.Label, t.Color ?? "");

    public static JobViewDto Job(BackupJob j) => new(
        j.Id, j.Name, j.HostId, j.Host?.Name ?? "", j.VmId, j.Vm?.Name ?? j.Vm?.ExternalId ?? "",
        j.StorageId, j.Storage?.Name ?? "", j.Type,
        j.ScheduleType, j.ScheduleTime, j.ScheduleWeekdays, j.ScheduleDayOfMonth, j.TimeZone, j.CronSchedule,
        ScheduleLabel(j), NextRunLabel(j),
        j.RetentionDays, j.Enabled, j.LastRunAt, j.NextRunAt, j.CreatedAt);

    public static string ScheduleLabel(BackupJob j)
    {
        var tz = string.IsNullOrWhiteSpace(j.TimeZone) ? "UTC" : j.TimeZone;
        var time = string.IsNullOrWhiteSpace(j.ScheduleTime) ? "00:00" : j.ScheduleTime;
        return j.ScheduleType?.ToLowerInvariant() switch
        {
            ScheduleTypes.Daily => $"Daily @ {time} ({tz})",
            ScheduleTypes.Weekly => $"Weekly @ {time} ({tz})",
            ScheduleTypes.Monthly => $"Monthly @ {time} ({tz})",
            _ => "Manual only"
        };
    }

    public static string NextRunLabel(BackupJob j)
    {
        if (!j.Enabled || string.IsNullOrWhiteSpace(j.CronSchedule) || j.NextRunAt is null) return "Manual";
        return j.NextRunAt.Value.ToUniversalTime().ToString("u");
    }

    public static BackupRunViewDto BackupRun(BackupRun r) => new(
        r.Id, r.JobId, r.Job?.Name, r.HostId, r.Host?.Name ?? "", r.VmId, r.Vm?.Name ?? r.Vm?.ExternalId ?? "",
        r.StorageId, r.Storage?.Name ?? "", r.Type, r.Status, r.AgentJobId, r.CorrelationId,
        r.ResultPath, r.ChainId, r.BackupId, r.SizeBytes, r.DurationSeconds, r.Message, r.Error,
        r.QueuedAt, r.StartedAt, r.CompletedAt);

    public static VerificationRunViewDto Verify(VerificationRun v) => new(
        v.Id, v.BackupRunId, v.HostId, v.Host?.Name ?? "", v.Kind, v.TargetPath, v.Status, v.IsValid,
        v.AgentJobId, v.CorrelationId, v.Errors, v.Warnings, v.QueuedAt, v.StartedAt, v.CompletedAt);

    public static RestoreRunViewDto Restore(RestoreRun r) => new(
        r.Id, r.BackupRunId, r.SourceHostId, r.TargetHostId, r.TargetHost?.Name ?? "", r.NewName,
        r.Destination, r.Status, r.AgentJobId, r.CorrelationId, r.Error, r.Message,
        r.QueuedAt, r.StartedAt, r.CompletedAt,
        r.Mode, r.SourceVmId, r.SourceVmName);

    public static MeDto Me(AppUser u) => new(u.Id, u.Username, u.Role, u.Enabled, u.CreatedAt, u.LastLoginAt);
}

// Make Program accessible for integration tests
public partial class Program { }
