using System.Security.Cryptography.X509Certificates;

namespace HyperVBackupAgent.Api;

public static class ApiEndpointConfiguration
{
    public static void ConfigureApiEndpoints(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("HyperVBackupAgent:Api");
        if (!section.GetValue("ConfigureKestrel", false))
        {
            return;
        }

        var httpPort = section.GetValue<int?>("HttpPort");
        var httpsPort = section.GetValue<int?>("HttpsPort") ?? 5443;
        var certificatePath = section["Certificate:Path"];
        var certificatePassword = section["Certificate:Password"];

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (httpPort is not null)
            {
                options.ListenAnyIP(httpPort.Value);
            }

            options.ListenAnyIP(httpsPort, listenOptions =>
            {
                if (string.IsNullOrWhiteSpace(certificatePath))
                {
                    listenOptions.UseHttps();
                    return;
                }

                var certificate = new X509Certificate2(
                    certificatePath,
                    certificatePassword,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
                listenOptions.UseHttps(certificate);
            });
        });
    }
}
