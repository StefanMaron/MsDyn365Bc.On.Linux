using System;
using System.Security.Cryptography;
using System.Text;

namespace BcLinux.Authentication;

public static class V3Password
{
    // Decompiled from Microsoft.Dynamics.Nav.Core.CryptographyHelper:
    // GeneratePasswordHash uses Guid.Empty; GenerateSaltedPasswordHash then
    // applies the user's security ID and the production iteration count.
    public static string GenerateInner(string password)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Guid.Empty.ToByteArray(),
            iterations: 1,
            HashAlgorithmName.SHA256,
            outputLength: 16);
        return Convert.ToBase64String(bytes) + "-V3";
    }

    public static string GenerateStored(string password, Guid userSecurityId)
    {
        var inner = GenerateInner(password);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(inner),
            userSecurityId.ToByteArray(),
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 16);
        return Convert.ToBase64String(bytes) + "-V3";
    }
}
