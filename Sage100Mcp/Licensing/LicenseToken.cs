using System.Security.Cryptography;
using System.Text.Json;

namespace Sage100Mcp.Licensing;

/// <summary>
/// Jeton de licence signé par le serveur de licences (ECDSA P-256). C'est la seule source de vérité
/// sur la licence : le nom du client, l'échéance et les outils autorisés sont lus ici, jamais dans les
/// champs en clair de la réponse. Sans cela, il suffirait de pointer <c>License:ServerUrl</c> vers un
/// faux serveur qui répond « valide », ou de retoucher le cache local, pour lever toute restriction.
/// </summary>
public sealed class LicenseToken
{
    /// <summary>
    /// Clé publique du serveur de licences. La clé privée correspondante est dans <c>Licensing__SigningKey</c>
    /// du serveur de licences ; en changer impose de publier une version du serveur MCP portant la nouvelle clé.
    /// </summary>
    private const string PublicKeyPem =
        """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEeZP0xtBwsZ6bzQJOIE1SEsMVsi/i
        /j71+j+JrxMEFchPV9BRjlen2/ijixrC/9NUj2YY06J170KJdsRrdvHkBg==
        -----END PUBLIC KEY-----
        """;

    public int V { get; init; }
    public string LicenseKeyHash { get; init; } = "";
    public string MachineId { get; init; } = "";
    public string ClientName { get; init; } = "";
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset OfflineUntilUtc { get; init; }
    public string? Nonce { get; init; }

    /// <summary>
    /// Vérifie la signature et désérialise le jeton. Renvoie null si le jeton est illisible ou si la
    /// signature ne correspond pas (jeton fabriqué ou modifié).
    /// </summary>
    public static LicenseToken? Verify(string? token, string? signature)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(signature)) return null;
        try
        {
            var payload = Convert.FromBase64String(token);
            using var key = ECDsa.Create();
            key.ImportFromPem(PublicKeyPem);
            if (!key.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256)) return null;

            var parsed = JsonSerializer.Deserialize<LicenseToken>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed is { V: 1 } ? parsed : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }
}
