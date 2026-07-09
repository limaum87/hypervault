using HyperVBackupAgent.Api;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Tests;

public sealed class ApiCertificateManagerTests
{
    [Fact]
    public void LoadConfiguredCertificateAutoGeneratesReusableCertificate()
    {
        var root = CreateTempDirectory();
        var certificatePath = Path.Combine(root, "agent-api.pfx");
        var configuration = CreateConfiguration(certificatePath);

        using var first = ApiCertificateManager.LoadConfiguredCertificate(configuration, root);
        using var second = ApiCertificateManager.LoadConfiguredCertificate(configuration, root);
        var info = ApiCertificateManager.GetCertificateInfo(configuration, root);

        Assert.True(File.Exists(certificatePath));
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.Equal("CN=HyperVBackupAgent API", info.Subject);
        Assert.Equal(64, info.Sha256Fingerprint.Length);
    }

    [Fact]
    public void TryGetCertificateInfoReturnsNullWhenCertificateDoesNotExist()
    {
        var root = CreateTempDirectory();
        var certificatePath = Path.Combine(root, "missing.pfx");
        var configuration = CreateConfiguration(certificatePath);

        var info = ApiCertificateManager.TryGetCertificateInfo(configuration, root);

        Assert.Null(info);
    }

    private static IConfiguration CreateConfiguration(string certificatePath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:Api:Certificate:AutoGenerate"] = "true",
                ["HyperVBackupAgent:Api:Certificate:StorePath"] = certificatePath,
                ["HyperVBackupAgent:Api:Certificate:Subject"] = "CN=HyperVBackupAgent API",
                ["HyperVBackupAgent:Api:Certificate:ValidDays"] = "30"
            })
            .Build();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-cert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
