using HyperVBackupAgent.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;

namespace HyperVBackupAgent.Tests;

public sealed class ApiJobServiceTests
{
    [Fact]
    public async Task EnqueueRunsJobAndPersistsHistory()
    {
        var root = CreateTempDirectory();
        var storePath = Path.Combine(root, "api-jobs.json");
        var service = CreateService(root, storePath);

        var job = service.Enqueue("test", "ERP01", root, _ => Task.FromResult(new ApiJobOutcome(root, "done")), "corr-123");

        var completed = await WaitForJobAsync(service, job.JobId);
        var reloaded = CreateService(root, storePath).GetJob(job.JobId);

        Assert.Equal(ApiJobStatus.Completed, completed.Status);
        Assert.Equal(root, completed.ResultPath);
        Assert.Equal("corr-123", completed.CorrelationId);
        Assert.NotNull(reloaded);
        Assert.Equal(ApiJobStatus.Completed, reloaded.Status);
        Assert.Equal("corr-123", reloaded.CorrelationId);
    }

    private static async Task<ApiJobRecord> WaitForJobAsync(ApiJobService service, string jobId)
    {
        for (var i = 0; i < 50; i++)
        {
            var job = service.GetJob(jobId)!;
            if (job.Status is ApiJobStatus.Completed or ApiJobStatus.Failed or ApiJobStatus.Canceled)
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Job did not complete in time.");
    }

    private static ApiJobService CreateService(string contentRoot, string storePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:Api:Jobs:StorePath"] = storePath
            })
            .Build();

        return new ApiJobService(
            configuration,
            new TestEnvironment(contentRoot),
            NullLogger<ApiJobService>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-job-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public TestEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "HyperVBackupAgent.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
