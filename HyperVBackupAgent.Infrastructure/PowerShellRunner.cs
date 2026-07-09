using System.Diagnostics;
using System.Text;

namespace HyperVBackupAgent.Infrastructure;

public sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError);

public interface IPowerShellRunner
{
    Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken = default);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    public async Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new PowerShellResult(process.ExitCode, await outputTask, await errorTask);
    }
}
