using HyperVBackupAgent.Core;
using HyperVBackupAgent.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddHyperVBackupAgent(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var configuredToken = app.Configuration["HyperVBackupAgent:ApiToken"];
    if (string.IsNullOrWhiteSpace(configuredToken))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("API token is not configured.");
        return;
    }

    var suppliedToken = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    if (!string.Equals(configuredToken, suppliedToken, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/vms", async (IHyperVService hyperV, CancellationToken ct) => Results.Ok(await hyperV.ListVmsAsync(ct)));
app.MapGet("/vms/{id}", async (string id, IHyperVService hyperV, CancellationToken ct) =>
{
    var vm = await hyperV.GetVmAsync(id, ct);
    return vm is null ? Results.NotFound() : Results.Ok(vm);
});
app.MapGet("/vms/{id}/restore-points", async (string id, IRestorePointCatalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.ListRestorePointsAsync(id, ct)));
app.MapPost("/backups/full", async (BackupRequest request, IBackupEngine engine, CancellationToken ct) => Results.Ok(await engine.RunFullBackupAsync(request, ct)));
app.MapPost("/backups/incremental", async (BackupRequest request, IBackupEngine engine, CancellationToken ct) => Results.Ok(await engine.RunIncrementalBackupAsync(request, ct)));
app.MapPost("/backups/verify-chain", async (VerifyChainRequest request, IVerifyEngine engine, CancellationToken ct) => Results.Ok(await engine.VerifyChainAsync(request.ChainPath, ct)));
app.MapPost("/backups/verify-restore", async (VerifyRestoreRequest request, IVerifyEngine engine, CancellationToken ct) => Results.Ok(await engine.VerifyRestoreAsync(request.RestorePointPath, request.KeepTemporaryFiles, ct)));
app.MapPost("/restore", async (RestoreRequest request, IRestoreEngine engine, CancellationToken ct) =>
{
    await engine.RestoreAsync(request, ct);
    return Results.Accepted();
});
app.MapPost("/maintenance/cleanup-temp-checkpoints", async (CleanupCheckpointsRequest request, IHyperVService hyperV, CancellationToken ct) =>
    Results.Ok(await hyperV.CleanupTemporaryCheckpointsAsync(request.NamePrefix, ct)));

app.Run();

public sealed record VerifyChainRequest(string ChainPath);
public sealed record VerifyRestoreRequest(string RestorePointPath, bool KeepTemporaryFiles = false);
public sealed record CleanupCheckpointsRequest(string NamePrefix = "HyperVBackupAgent-");
