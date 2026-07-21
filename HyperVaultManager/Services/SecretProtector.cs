using Microsoft.AspNetCore.DataProtection;

namespace HyperVaultManager.Services;

/// <summary>Reversibly encrypts secrets (e.g. SMB passwords) at rest using
/// ASP.NET Core Data Protection. The plaintext is needed back to authenticate
/// against the SMB share, so one-way hashing (as used for AppUser passwords)
/// is not an option here. Keys are persisted to the data volume, so secrets
/// remain decryptable across restarts.</summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider dp)
        => _protector = dp.CreateProtector("HyperVaultManager:SmbPassword:v1");

    /// <summary>Encrypts a plaintext secret. Empty input -> empty string (no cipher stored).</summary>
    public string Protect(string? plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>Decrypts a cipher. Null/empty/blank -> empty string.</summary>
    public string Unprotect(string? cipher) => string.IsNullOrWhiteSpace(cipher) ? "" : _protector.Unprotect(cipher);
}

/// <summary>Optional SMB credentials to forward to an agent when accessing a UNC share.
/// Null/empty fields mean "no credential" (the agent falls back to the host's own access).</summary>
public sealed record SmbCredentials(string? Username, string? Password, string? Domain)
{
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
    public static SmbCredentials? From(string? username, string? password, string? domain)
        => string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password) ? null
           : new SmbCredentials(username, password, domain);
}
