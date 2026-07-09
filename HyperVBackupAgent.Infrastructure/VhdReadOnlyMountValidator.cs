namespace HyperVBackupAgent.Infrastructure;

public sealed class VhdReadOnlyMountValidator
{
    private readonly IPowerShellRunner _powerShell;

    public VhdReadOnlyMountValidator(IPowerShellRunner powerShell)
    {
        _powerShell = powerShell;
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(IReadOnlyList<string> diskPaths, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var vhdPaths = diskPaths
            .Where(path => path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (vhdPaths.Length == 0)
        {
            warnings.Add("No VHDX/AVHDX files were produced; read-only mount validation was skipped.");
            return warnings;
        }

        foreach (var path in vhdPaths)
        {
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $path = '{{Escape(path)}}'
                Mount-VHD -Path $path -ReadOnly -Passthru | Out-Null
                Dismount-VHD -Path $path
                [pscustomobject]@{ Mounted = $true; Path = $path } | ConvertTo-Json
                """;
            var result = await _powerShell.RunAsync(script, cancellationToken);
            if (result.ExitCode != 0)
            {
                warnings.Add($"Read-only mount validation failed for {path}: {result.StandardError.Trim()}");
            }
        }

        return warnings;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
