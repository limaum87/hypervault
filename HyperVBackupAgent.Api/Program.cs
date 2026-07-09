using HyperVBackupAgent.Core;
using HyperVBackupAgent.Api;
using HyperVBackupAgent.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
ApiEndpointConfiguration.ConfigureApiEndpoints(builder);
builder.Host.UseSerilog();
builder.Services.AddHyperVBackupAgent(builder.Configuration);
builder.Services.AddSingleton<ApiPathValidator>();

var app = builder.Build();

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ArgumentException ex)
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request", ex.Message);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
    {
        await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", ex.Message);
    }
    catch (InvalidOperationException ex) when (
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("overwrite", StringComparison.OrdinalIgnoreCase))
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "conflict", ex.Message);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled API error while processing {Method} {Path}", context.Request.Method, context.Request.Path);
        await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
    }
});
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
        await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "token_not_configured", "API token is not configured.");
        return;
    }

    var suppliedToken = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    if (!string.Equals(configuredToken, suppliedToken, StringComparison.Ordinal))
    {
        await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Bearer token is missing or invalid.");
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
app.MapPost("/backups/full", async (BackupRequest request, IBackupEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    return Results.Ok(await engine.RunFullBackupAsync(validated, ct));
});
app.MapPost("/backups/incremental", async (BackupRequest request, IBackupEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    return Results.Ok(await engine.RunIncrementalBackupAsync(validated, ct));
});
app.MapPost("/backups/verify-chain", async (VerifyChainRequest request, IVerifyEngine engine, ApiPathValidator paths, CancellationToken ct) =>
    Results.Ok(await engine.VerifyChainAsync(paths.ValidateAbsolutePath(request.ChainPath, nameof(request.ChainPath)), ct)));
app.MapPost("/backups/verify-restore", async (VerifyRestoreRequest request, IVerifyEngine engine, ApiPathValidator paths, CancellationToken ct) =>
    Results.Ok(await engine.VerifyRestoreAsync(paths.ValidateAbsolutePath(request.RestorePointPath, nameof(request.RestorePointPath)), request.KeepTemporaryFiles, ct)));
app.MapPost("/restore", async (RestoreRequest request, IRestoreEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with
    {
        RestorePoint = paths.ValidateAbsolutePath(request.RestorePoint, nameof(request.RestorePoint)),
        Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination))
    };
    await engine.RestoreAsync(validated, ct);
    return Results.Accepted();
});
app.MapPost("/maintenance/cleanup-temp-checkpoints", async (CleanupCheckpointsRequest request, IHyperVService hyperV, CancellationToken ct) =>
    Results.Ok(await hyperV.CleanupTemporaryCheckpointsAsync(request.NamePrefix, ct)));
app.MapPost("/maintenance/apply-retention", async (RetentionRequest request, IRetentionService retention, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { BackupRoot = paths.ValidateAbsolutePath(request.BackupRoot, nameof(request.BackupRoot)) };
    return Results.Ok(await retention.ApplyRetentionAsync(validated, ct));
});

app.Run();

static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
{
    if (context.Response.HasStarted)
    {
        return;
    }

    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new ApiError(code, message, context.TraceIdentifier));
}

public sealed record ApiError(string Code, string Message, string TraceId);
public sealed record VerifyChainRequest(string ChainPath);
public sealed record VerifyRestoreRequest(string RestorePointPath, bool KeepTemporaryFiles = false);
public sealed record CleanupCheckpointsRequest(string NamePrefix = "HyperVBackupAgent-");
