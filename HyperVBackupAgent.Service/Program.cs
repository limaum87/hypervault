using HyperVBackupAgent.Infrastructure;
using HyperVBackupAgent.Service;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HyperVBackupAgent.Scheduler");
builder.Services.AddSerilog();
builder.Services.AddHyperVBackupAgent(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
