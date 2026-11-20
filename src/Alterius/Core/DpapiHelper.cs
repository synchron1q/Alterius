using System.Security.Cryptography;
using System.Text;

namespace Alterius.Core;

/// <summary>
/// Encrypts and decrypts sensitive strings (such as SMB passwords) using Windows DPAPI
/// scoped to the current Windows user (<see cref="DataProtectionScope.CurrentUser"/>).
/// </summary>
public static class DpapiHelper
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Alterius.DPAPI.v1");

    public static string Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Decrypt(string? encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            return string.Empty;
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(encryptedBase64.Trim());
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
