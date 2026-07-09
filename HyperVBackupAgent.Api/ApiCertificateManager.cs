using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyperVBackupAgent.Api;

public static class ApiCertificateManager
{
    private const string DefaultSubject = "CN=HyperVBackupAgent API";

    public static X509Certificate2 LoadConfiguredCertificate(IConfiguration configuration, string contentRootPath)
    {
        var section = configuration.GetSection("HyperVBackupAgent:Api:Certificate");
        var certificatePath = section["Path"];
        var certificatePassword = section["Password"];

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            return LoadCertificate(certificatePath, certificatePassword);
        }

        if (!section.GetValue("AutoGenerate", true))
        {
            throw new InvalidOperationException("API certificate path is empty and auto generation is disabled.");
        }

        var storePath = ResolveStorePath(section["StorePath"], contentRootPath);
        if (File.Exists(storePath))
        {
            return LoadCertificate(storePath, certificatePassword);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        using var generated = CreateSelfSignedCertificate(section);
        var bytes = generated.Export(X509ContentType.Pkcs12, certificatePassword);
        File.WriteAllBytes(storePath, bytes);
        return LoadCertificate(storePath, certificatePassword);
    }

    public static ApiCertificateInfo GetCertificateInfo(IConfiguration configuration, string contentRootPath)
    {
        var section = configuration.GetSection("HyperVBackupAgent:Api:Certificate");
        var path = !string.IsNullOrWhiteSpace(section["Path"])
            ? section["Path"]!
            : ResolveStorePath(section["StorePath"], contentRootPath);

        using var certificate = LoadCertificate(path, section["Password"]);
        return new ApiCertificateInfo(
            certificate.Subject,
            ComputeSha256Fingerprint(certificate),
            certificate.NotBefore,
            certificate.NotAfter,
            Path.GetFullPath(path));
    }

    public static ApiCertificateInfo? TryGetCertificateInfo(IConfiguration configuration, string contentRootPath)
    {
        var section = configuration.GetSection("HyperVBackupAgent:Api:Certificate");
        var path = !string.IsNullOrWhiteSpace(section["Path"])
            ? section["Path"]!
            : ResolveStorePath(section["StorePath"], contentRootPath);

        if (!File.Exists(path))
        {
            return null;
        }

        return GetCertificateInfo(configuration, contentRootPath);
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
        => new(
            Path.GetFullPath(path),
            password,
            X509KeyStorageFlags.MachineKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);

    private static X509Certificate2 CreateSelfSignedCertificate(IConfiguration section)
    {
        using var rsa = RSA.Create(3072);
        var subject = section["Subject"];
        var request = new CertificateRequest(
            string.IsNullOrWhiteSpace(subject) ? DefaultSubject : subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        var usages = new OidCollection();
        usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var validDays = Math.Max(30, section.GetValue("ValidDays", 825));
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(validDays);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static string ResolveStorePath(string? configuredPath, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var baseDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HyperVBackupAgent",
                "certs")
            : Path.Combine(contentRootPath, "certs");

        return Path.Combine(baseDirectory, "agent-api.pfx");
    }

    private static string ComputeSha256Fingerprint(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hash);
    }
}

public sealed record ApiCertificateInfo(
    string Subject,
    string Sha256Fingerprint,
    DateTime NotBefore,
    DateTime NotAfter,
    string Path);
