using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Sage100Mcp.Licensing;

/// <summary>
/// Identité du poste envoyée au serveur de licences pour l'activation par poste.
/// <see cref="Id"/> est une empreinte SHA-256 du MachineGuid de Windows : l'identifiant brut ne quitte
/// jamais le poste. Il est propre à une installation de Windows — une réinstallation, ou un clone de VM
/// non généralisé (sysprep), change ou duplique l'empreinte ; le poste doit alors être libéré côté
/// administration (<c>DELETE /api/admin/licenses/{id}/machines/{machineId}</c>).
/// </summary>
public sealed record MachineIdentity(string Id, string Name)
{
    public static MachineIdentity Current()
    {
        var raw = ReadMachineGuid();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Sage100Mcp:" + raw.Trim().ToLowerInvariant())));
        return new MachineIdentity(id, Environment.MachineName);
    }

    private static string ReadMachineGuid()
    {
        if (OperatingSystem.IsWindows())
        {
            // Vue 64 bits explicite : un process 32 bits lirait sinon la branche WOW6432Node, sans MachineGuid.
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
            throw new InvalidOperationException(@"MachineGuid introuvable dans HKLM\SOFTWARE\Microsoft\Cryptography.");
        }

        // Hors Windows (développement) : identifiant systemd.
        const string machineIdPath = "/etc/machine-id";
        if (File.Exists(machineIdPath) && File.ReadAllText(machineIdPath).Trim() is { Length: > 0 } id) return id;
        throw new PlatformNotSupportedException("Impossible d'identifier ce poste : ni MachineGuid ni /etc/machine-id.");
    }
}
