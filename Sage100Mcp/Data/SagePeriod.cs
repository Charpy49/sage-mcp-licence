using System.Globalization;

namespace Sage100Mcp.Data;

/// <summary>Résolution des bornes de période à partir de chaînes optionnelles "yyyy-MM-dd".</summary>
public static class SagePeriod
{
    public static DateTime ParseDate(string? value, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso.Date;
        if (DateTime.TryParse(value, SageFormat.Fr, DateTimeStyles.None, out var fr))
            return fr.Date;
        throw new ArgumentException($"Date invalide : '{value}'. Format attendu : AAAA-MM-JJ.");
    }

    /// <summary>Période par défaut = année civile en cours (1er janv. -> 31 déc.).</summary>
    public static (DateTime From, DateTime To) Resolve(string? from, string? to)
    {
        var today = DateTime.Today;
        var resolvedTo = ParseDate(to, new DateTime(today.Year, 12, 31));
        var resolvedFrom = ParseDate(from, new DateTime(resolvedTo.Year, 1, 1));
        return (resolvedFrom, resolvedTo.Date.AddDays(1).AddTicks(-1)); // inclut toute la journée de fin
    }

    public static string Describe(DateTime from, DateTime to)
        => $"du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
}
