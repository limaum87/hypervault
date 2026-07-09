using HyperVBackupAgent.Api;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Tests;

public sealed class ApiPathValidatorTests
{
    [Fact]
    public void ValidateAbsolutePathReturnsNormalizedPathInsideAllowedRoot()
    {
        var root = CreateTempDirectory();
        var validator = CreateValidator(root);
        var path = Path.Combine(root, "vm-backups");

        var validated = validator.ValidateAbsolutePath(path, "Destination");

        Assert.Equal(Path.GetFullPath(path), validated);
    }

    [Fact]
    public void ValidateAbsolutePathRejectsParentTraversal()
    {
        var root = CreateTempDirectory();
        var validator = CreateValidator(root);
        var path = Path.Combine(root, "..", "outside");

        var error = Assert.Throws<ArgumentException>(() =>
            validator.ValidateAbsolutePath(path, "Destination"));

        Assert.Contains("parent directory traversal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAbsolutePathRejectsPathsOutsideAllowedRoots()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var validator = CreateValidator(root);

        var error = Assert.Throws<ArgumentException>(() =>
            validator.ValidateAbsolutePath(outside, "Destination"));

        Assert.Contains("outside the configured allowed path roots", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ApiPathValidator CreateValidator(string allowedRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:AllowedPathRoots:0"] = allowedRoot
            })
            .Build();

        return new ApiPathValidator(configuration);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
