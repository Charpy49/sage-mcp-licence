using System.Security.Cryptography;
using System.Text.Json;

namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>
/// Signe les jetons de licence (ECDSA P-256, SHA-256, signature au format IEEE P1363).
/// La clé privée ne quitte jamais le serveur ; la clé publique correspondante est embarquée dans le
/// serveur MCP (<c>Sage100Mcp/Licensing/LicenseToken.cs</c>). Changer de clé impose donc de publier
/// une nouvelle version du serveur MCP <b>avant</b> de basculer le serveur de licences.
/// </summary>
public sealed class LicenseSigner : IDisposable
{
    public const string ConfigurationKey = "Licensing:SigningKey";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ECDsa _key;

    public LicenseSigner(IConfiguration configuration)
    {
        var value = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} doit contenir la clé privée de signature des licences (variable d'environnement " +
                "Licensing__SigningKey). Générez-la avec : dotnet run -- generate-signing-key");
        }

        _key = ECDsa.Create();
        _key.ImportPkcs8PrivateKey(Convert.FromBase64String(StripPem(value)), out _);
        if (_key.KeySize != 256)
        {
            throw new InvalidOperationException($"{ConfigurationKey} : clé ECDSA P-256 attendue (taille lue : {_key.KeySize}).");
        }
    }

    /// <summary>Sérialise le jeton et le signe. Le client vérifie la signature sur ces octets exacts.</summary>
    public (string Token, string Signature) Sign(LicenseToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions);
        var signature = _key.SignData(payload, HashAlgorithmName.SHA256);
        return (Convert.ToBase64String(payload), Convert.ToBase64String(signature));
    }

    /// <summary>
    /// Génère une paire de clés : la privée (PKCS#8 en base64, sur une ligne pour tenir dans une variable
    /// d'environnement) pour le serveur de licences, la publique (PEM) pour le serveur MCP.
    /// </summary>
    public static (string PrivateKeyBase64, string PublicKeyPem) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Accepte la clé en base64 brut comme en PEM (« -----BEGIN PRIVATE KEY----- »).</summary>
    private static string StripPem(string value) =>
        string.Concat(value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));

    public void Dispose() => _key.Dispose();
}
