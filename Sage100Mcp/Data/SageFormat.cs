using System.Globalization;
using System.Text;

namespace Sage100Mcp.Data;

/// <summary>Helpers de formatage (montants en euros, dates, tableaux markdown) en français.</summary>
public static class SageFormat
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        double db => (decimal)db,
        float f => (decimal)f,
        int i => i,
        long l => l,
        _ => decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0m
    };

    public static long ToLong(object? value) => value switch
    {
        null => 0,
        long l => l,
        int i => i,
        _ => long.TryParse(value.ToString(), out var r) ? r : 0
    };

    public static string Euro(object? value) => $"{ToDecimal(value).ToString("N2", Fr)} €";

    public static string Date(object? value)
        => value is DateTime dt ? dt.ToString("dd/MM/yyyy", Fr) : value?.ToString() ?? "";

    public static string Text(object? value) => value?.ToString()?.Trim() ?? "";

    public static readonly string[] MonthsFr =
    {
        "", "janv.", "févr.", "mars", "avr.", "mai", "juin",
        "juil.", "août", "sept.", "oct.", "nov.", "déc."
    };

    /// <summary>Construit un tableau markdown à partir des lignes et d'un jeu de colonnes (titre, sélecteur).</summary>
    public static string Table(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        params (string Header, Func<IReadOnlyDictionary<string, object?>, string> Cell)[] columns)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", columns.Select(c => c.Header))).AppendLine(" |");
        sb.Append("|").Append(string.Join("|", columns.Select(_ => "---"))).AppendLine("|");
        foreach (var row in rows)
            sb.Append("| ").Append(string.Join(" | ", columns.Select(c => c.Cell(row)))).AppendLine(" |");
        return sb.ToString();
    }
}
