using System.Security.Cryptography;
using System.Text;

namespace Sage100Mcp.LicenseServer.Licensing;

public static class LicenseKeyGenerator
{
    /// <summary>Génère une clé de licence lisible, ex. SAGE-A1B2-C3D4-E5F6-G7H8-I9J0-K1L2-M3N4-O5P6.</summary>
    public static string Generate()
    {
        var hex = RandomNumberGenerator.GetHexString(32, lowercase: false);
        var sb = new StringBuilder("SAGE");
        for (var i = 0; i < hex.Length; i += 4)
        {
            sb.Append('-').Append(hex, i, 4);
        }
        return sb.ToString();
    }

    public static string Hash(string licenseKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(licenseKey.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }

    public static string Prefix(string licenseKey) =>
        licenseKey.Length <= 14 ? licenseKey : licenseKey[..14];
}
