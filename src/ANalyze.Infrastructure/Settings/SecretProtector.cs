using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ANalyze.Infrastructure.Settings;

/// <summary>Encrypts secrets (the AI API key) stored in the settings table with ASP.NET data protection.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _protector = provider.CreateProtector("ANalyze.Secrets.v1");

    public string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain) ? null : Prefix + _protector.Protect(plain);

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            // Keys were lost (e.g. /data/keys not persisted): the key must be entered again.
            return null;
        }
    }
}
