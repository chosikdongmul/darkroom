using System.Security.Cryptography;
using System.Text;

namespace Darkroom.App;

/// <summary>비밀번호 저장용. Windows 사용자 계정(DPAPI)으로 암호화해서 같은 사용자만 풀 수 있다.</summary>
internal static class Secrets
{
    static readonly byte[] Entropy = "Darkroom.Calendar.v1"u8.ToArray();

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
