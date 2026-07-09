using HyperVBackupAgent.Core;
using HyperVBackupAgent.Api;
using HyperVBackupAgent.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;
using Serilog.Events;

const string CorrelationIdHeader = "X-Correlation-Id";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
ApiEndpointConfiguration.ConfigureApiEndpoints(builder);
builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    ConfigureSerilog(loggerConfiguration, context.Configuration, context.HostingEnvironment.ContentRootPath);
});
builder.Services.AddHyperVBackupAgent(builder.Configuration);
builder.Services.AddSingleton<ApiPathValidator>();
builder.Services.AddSingleton<ApiJobService>();
builder.Services.AddSingleton<ApiAgentInfoService>();
builder.Services.AddSingleton<ApiPreflightService>();
builder.Services.AddSingleton<ApiHealthService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var correlationId = GetOrCreateCorrelationId(context);
    context.TraceIdentifier = correlationId;
    context.Response.Headers[CorrelationIdHeader] = correlationId;

    using (LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
    };
});
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
    if (context.Request.Path.StartsWithSegments("/health"))
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
app.MapGet("/health/live", (ApiHealthService health) => Results.Ok(health.GetLive()));
app.MapGet("/health/ready", async (ApiHealthService health, CancellationToken ct) =>
{
    var result = await health.GetReadyAsync(ct);
    return result.Status == "ready"
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/agent", (ApiAgentInfoService agent) => Results.Ok(agent.GetAgentInfo()));
app.MapGet("/configuration/effective", (ApiAgentInfoService agent) => Results.Ok(agent.GetEffectiveConfiguration()));
app.MapGet("/agent/certificate", (IConfiguration configuration, IWebHostEnvironment environment) =>
{
    var certificate = ApiCertificateManager.TryGetCertificateInfo(configuration, environment.ContentRootPath);
    return certificate is null
        ? Results.NotFound(new ApiError("certificate_not_found", "API certificate has not been generated or configured yet.", string.Empty))
        : Results.Ok(certificate);
});
app.MapGet("/jobs", (ApiJobService jobs) => Results.Ok(jobs.ListJobs()));
app.MapGet("/jobs/{id}", (string id, ApiJobService jobs) =>
{
    var job = jobs.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
app.MapPost("/jobs/{id}/cancel", (string id, ApiJobService jobs) =>
    jobs.Cancel(id) ? Results.Accepted($"/jobs/{id}") : Results.NotFound());
app.MapPost("/jobs/backup-full", ([FromBody] BackupRequest request, IBackupEngine engine, ApiPathValidator paths, ApiJobService jobs, HttpContext context) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    var job = jobs.Enqueue("backup-full", validated.VmNameOrId, validated.Destination, async ct =>
    {
        var result = await engine.RunFullBackupAsync(validated, ct);
        if (result.Status != BackupStatus.Completed)
        {
            throw new InvalidOperationException(result.Error ?? "Full backup failed.");
        }

        return new ApiJobOutcome(result.Path, $"{result.Type} backup completed: {result.BackupId}");
    }, context.TraceIdentifier);
    return Results.Accepted($"/jobs/{job.JobId}", job);
});
app.MapPost("/jobs/backup-incremental", ([FromBody] BackupRequest request, IBackupEngine engine, ApiPathValidator paths, ApiJobService jobs, HttpContext context) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    var job = jobs.Enqueue("backup-incremental", validated.VmNameOrId, validated.Destination, async ct =>
    {
        var result = await engine.RunIncrementalBackupAsync(validated, ct);
        if (result.Status != BackupStatus.Completed)
        {
            throw new InvalidOperationException(result.Error ?? "Incremental backup failed.");
        }

        return new ApiJobOutcome(result.Path, $"{result.Type} backup completed: {result.BackupId}");
    }, context.TraceIdentifier);
    return Results.Accepted($"/jobs/{job.JobId}", job);
});
app.MapPost("/jobs/verify-chain", ([FromBody] VerifyChainRequest request, IVerifyEngine engine, ApiPathValidator paths, ApiJobService jobs, HttpContext context) =>
{
    var chainPath = paths.ValidateAbsolutePath(request.ChainPath, nameof(request.ChainPath));
    var job = jobs.Enqueue("verify-chain", null, chainPath, async ct =>
    {
        var result = await engine.VerifyChainAsync(chainPath, ct);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors));
        }

        return new ApiJobOutcome(chainPath, "Chain verification completed.");
    }, context.TraceIdentifier);
    return Results.Accepted($"/jobs/{job.JobId}", job);
});
app.MapPost("/jobs/verify-restore", ([FromBody] VerifyRestoreRequest request, IVerifyEngine engine, ApiPathValidator paths, ApiJobService jobs, HttpContext context) =>
{
    var restorePointPath = paths.ValidateAbsolutePath(request.RestorePointPath, nameof(request.RestorePointPath));
    var job = jobs.Enqueue("verify-restore", null, restorePointPath, async ct =>
    {
        var result = await engine.VerifyRestoreAsync(restorePointPath, request.KeepTemporaryFiles, ct);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors));
        }

        return new ApiJobOutcome(restorePointPath, "Restore verification completed.");
    }, context.TraceIdentifier);
    return Results.Accepted($"/jobs/{job.JobId}", job);
});
app.MapPost("/jobs/restore", ([FromBody] RestoreRequest request, IRestoreEngine engine, ApiPathValidator paths, ApiJobService jobs, HttpContext context) =>
{
    var validated = request with
    {
        RestorePoint = paths.ValidateAbsolutePath(request.RestorePoint, nameof(request.RestorePoint)),
        Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination))
    };
    var job = jobs.Enqueue("restore", validated.NewName, validated.Destination, async ct =>
    {
        await engine.RestoreAsync(validated, ct);
        return new ApiJobOutcome(validated.Destination, $"Restore completed: {validated.NewName}");
    }, context.TraceIdentifier);
    return Results.Accepted($"/jobs/{job.JobId}", job);
});
app.MapPost("/backups/preflight", async ([FromBody] BackupPreflightRequest request, ApiPreflightService preflight, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    return Results.Ok(await preflight.CheckBackupAsync(validated, ct));
});
app.MapPost("/restore/preflight", async ([FromBody] RestorePreflightRequest request, ApiPreflightService preflight, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with
    {
        RestorePoint = paths.ValidateAbsolutePath(request.RestorePoint, nameof(request.RestorePoint)),
        Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination))
    };
    return Results.Ok(await preflight.CheckRestoreAsync(validated, ct));
});
app.MapGet("/vms", async (IHyperVService hyperV, CancellationToken ct) => Results.Ok(await hyperV.ListVmsAsync(ct)));
app.MapGet("/vms/{id}", async (string id, IHyperVService hyperV, CancellationToken ct) =>
{
    var vm = await hyperV.GetVmAsync(id, ct);
    return vm is null ? Results.NotFound() : Results.Ok(vm);
});
app.MapGet("/vms/{id}/restore-points", async (
    string id,
    string? status,
    DateTimeOffset? from,
    DateTimeOffset? to,
    IRestorePointCatalog catalog,
    CancellationToken ct) =>
{
    BackupStatus? parsedStatus = string.IsNullOrWhiteSpace(status)
        ? null
        : Enum.Parse<BackupStatus>(status, ignoreCase: true);
    return Results.Ok(await catalog.ListRestorePointsAsync(id, parsedStatus, from, to, ct));
});
app.MapPost("/backups/full", async ([FromBody] BackupRequest request, IBackupEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    return Results.Ok(await engine.RunFullBackupAsync(validated, ct));
});
app.MapPost("/backups/incremental", async ([FromBody] BackupRequest request, IBackupEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with { Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination)) };
    return Results.Ok(await engine.RunIncrementalBackupAsync(validated, ct));
});
app.MapPost("/backups/verify-chain", async ([FromBody] VerifyChainRequest request, IVerifyEngine engine, ApiPathValidator paths, CancellationToken ct) =>
    Results.Ok(await engine.VerifyChainAsync(paths.ValidateAbsolutePath(request.ChainPath, nameof(request.ChainPath)), ct)));
app.MapPost("/backups/verify-restore", async ([FromBody] VerifyRestoreRequest request, IVerifyEngine engine, ApiPathValidator paths, CancellationToken ct) =>
    Results.Ok(await engine.VerifyRestoreAsync(paths.ValidateAbsolutePath(request.RestorePointPath, nameof(request.RestorePointPath)), request.KeepTemporaryFiles, ct)));
app.MapPost("/restore", async ([FromBody] RestoreRequest request, IRestoreEngine engine, ApiPathValidator paths, CancellationToken ct) =>
{
    var validated = request with
    {
        RestorePoint = paths.ValidateAbsolutePath(request.RestorePoint, nameof(request.RestorePoint)),
        Destination = paths.ValidateAbsolutePath(request.Destination, nameof(request.Destination))
    };
    await engine.RestoreAsync(validated, ct);
    return Results.Accepted();
});
app.MapPost("/maintenance/cleanup-temp-checkpoints", async ([FromBody] CleanupCheckpointsRequest request, IHyperVService hyperV, CancellationToken ct) =>
    Results.Ok(await hyperV.CleanupTemporaryCheckpointsAsync(request.NamePrefix, ct)));
app.MapPost("/maintenance/apply-retention", async ([FromBody] RetentionRequest request, IRetentionService retention, ApiPathValidator paths, CancellationToken ct) =>
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

static string GetOrCreateCorrelationId(HttpContext context)
{
    var supplied = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
    return string.IsNullOrWhiteSpace(supplied)
        ? context.TraceIdentifier
        : supplied.Trim();
}

static void ConfigureSerilog(LoggerConfiguration loggerConfiguration, IConfiguration configuration, string contentRootPath)
{
    var loggingSection = configuration.GetSection("HyperVBackupAgent:Api:Logging");
    var fileEnabled = loggingSection.GetValue("FileEnabled", true);
    var retainedFileCountLimit = loggingSection.GetValue<int?>("RetainedFileCountLimit") ?? 14;
    var fileSizeLimitBytes = loggingSection.GetValue<long?>("FileSizeLimitBytes") ?? 104_857_600;

    loggerConfiguration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(new RenderedCompactJsonFormatter());

    if (!fileEnabled)
    {
        return;
    }

    var logDirectory = ResolveLogDirectory(loggingSection["Directory"], contentRootPath);
    Directory.CreateDirectory(logDirectory);
    loggerConfiguration.WriteTo.File(
        new RenderedCompactJsonFormatter(),
        Path.Combine(logDirectory, "hypervbackupagent-api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: retainedFileCountLimit,
        fileSizeLimitBytes: fileSizeLimitBytes,
        rollOnFileSizeLimit: true,
        shared: true);
}

static string ResolveLogDirectory(string? configuredDirectory, string contentRootPath)
{
    if (!string.IsNullOrWhiteSpace(configuredDirectory))
    {
        return Path.GetFullPath(configuredDirectory);
    }

    if (OperatingSystem.IsWindows())
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HyperVBackupAgent",
            "logs");
    }

    return Path.Combine(contentRootPath, "logs");
}

public sealed record ApiError(string Code, string Message, string TraceId);
public sealed record VerifyChainRequest(string ChainPath);
public sealed record VerifyRestoreRequest(string RestorePointPath, bool KeepTemporaryFiles = false);
public sealed record CleanupCheckpointsRequest(string NamePrefix = "HyperVBackupAgent-");
