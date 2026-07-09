using System.Runtime.InteropServices;

namespace HyperVBackupAgent.Api;

public sealed class ApiPathValidator
{
    private readonly string[] _allowedRoots;

    public ApiPathValidator(IConfiguration configuration)
    {
        _allowedRoots = configuration
            .GetSection("HyperVBackupAgent:AllowedPathRoots")
            .Get<string[]>()?
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeRoot)
            .ToArray() ?? [];
    }

    public string ValidateAbsolutePath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.");
        }

        if (ContainsParentTraversal(value))
        {
            throw new ArgumentException($"{fieldName} cannot contain parent directory traversal.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{fieldName} is not a valid path.", ex);
        }

        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException($"{fieldName} must be an absolute path.");
        }

        if (_allowedRoots.Length > 0 && !_allowedRoots.Any(root => IsUnderRoot(fullPath, root)))
        {
            throw new ArgumentException($"{fieldName} is outside the configured allowed path roots.");
        }

        return fullPath;
    }

    private static bool ContainsParentTraversal(string value)
    {
        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
    }

    private static string NormalizeRoot(string value)
    {
        if (ContainsParentTraversal(value))
        {
            throw new InvalidOperationException("AllowedPathRoots cannot contain parent directory traversal.");
        }

        var fullPath = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new InvalidOperationException("AllowedPathRoots entries must be absolute paths.");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(root, comparison);
    }
}
