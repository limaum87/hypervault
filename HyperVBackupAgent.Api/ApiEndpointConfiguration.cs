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

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (httpPort is not null)
            {
                options.ListenAnyIP(httpPort.Value);
            }

            options.ListenAnyIP(httpsPort, listenOptions =>
            {
                var certificateSection = section.GetSection("Certificate");
                var certificatePath = certificateSection["Path"];
                if (string.IsNullOrWhiteSpace(certificatePath) &&
                    !certificateSection.GetValue("AutoGenerate", true))
                {
                    listenOptions.UseHttps();
                    return;
                }

                var certificate = ApiCertificateManager.LoadConfiguredCertificate(
                    builder.Configuration,
                    builder.Environment.ContentRootPath);
                listenOptions.UseHttps(certificate);
            });
        });
    }
}
