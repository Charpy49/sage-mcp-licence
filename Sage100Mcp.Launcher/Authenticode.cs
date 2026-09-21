using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sage100Mcp.Launcher;

/// <summary>
/// Vérification de la signature Authenticode d'un binaire téléchargé.
/// <para>
/// C'est le seul contrôle qui protège d'une compromission du point de téléchargement : le SHA-256
/// du manifeste ne sert à rien face à un attaquant qui contrôle à la fois le paquet et le manifeste.
/// </para>
/// <para>
/// La vérification passe par <c>WinVerifyTrust</c> et non par la seule lecture du certificat
/// embarqué : lire le certificat prouverait uniquement qu'un certificat est présent, pas qu'il
/// couvre réellement le contenu du fichier (un binaire modifié auquel on recolle un certificat
/// valide passerait le test).
/// </para>
/// </summary>
internal static class Authenticode
{
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    /// <summary>
    /// Vérifie que <paramref name="filePath"/> est signé, que la signature couvre son contenu, que
    /// la chaîne remonte à une autorité de confiance de la machine, et que le sujet du certificat
    /// contient <paramref name="expectedSubject"/>.
    /// <para>
    /// La révocation n'est <b>pas</b> contrôlée : sur un poste sans accès Internet, la récupération
    /// de la liste de révocation échouerait et bloquerait toute mise à jour. Un certificat volé puis
    /// révoqué passerait donc ce contrôle — la parade est de retirer la version côté serveur (yank).
    /// </para>
    /// </summary>
    public static bool TryVerify(string filePath, string expectedSubject, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "la vérification de signature n'est disponible que sous Windows";
            return false;
        }

        if (!TryVerifyTrust(filePath, out error)) return false;

        try
        {
            // SYSLIB0057 recommande X509CertificateLoader, mais celui-ci charge un *fichier de
            // certificat* (DER/PEM) : appliqué à un binaire PE il échoue avec « objet introuvable ».
            // Extraire le certificat de signature d'un exécutable n'a pas d'équivalent non obsolète
            // en code managé — l'alternative serait un P/Invoke de CryptQueryObject.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            if (!certificate.Subject.Contains(expectedSubject, StringComparison.OrdinalIgnoreCase))
            {
                error = $"signé par un autre publicateur (attendu « {expectedSubject} », trouvé « {certificate.Subject} »)";
                return false;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            error = $"certificat illisible ({ex.Message})";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryVerifyTrust(string filePath, out string error)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

        var trustData = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = fileInfoPtr,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_SAFER_FLAG,
        };

        var trustDataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
        Marshal.StructureToPtr(trustData, trustDataPtr, fDeleteOld: false);

        try
        {
            var result = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, trustDataPtr);

            // Libère l'état conservé par le fournisseur de confiance (obligatoire après VERIFY).
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            trustData.hWVTStateData = Marshal.PtrToStructure<WinTrustData>(trustDataPtr).hWVTStateData;
            Marshal.StructureToPtr(trustData, trustDataPtr, fDeleteOld: true);
            WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, trustDataPtr);

            if (result == 0)
            {
                error = "";
                return true;
            }

            error = Describe(result);
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(trustDataPtr);
        }
    }

    private static string Describe(int hresult) => (uint)hresult switch
    {
        0x800B0100 => "binaire non signé",
        0x800B0101 => "certificat de signature expiré",
        0x800B0109 => "signé par une autorité qui n'est pas de confiance sur ce poste",
        0x80096010 => "contenu du binaire altéré depuis sa signature",
        0x800B010C => "certificat de signature révoqué",
        _ => $"signature refusée (HRESULT 0x{hresult:X8})",
    };

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
