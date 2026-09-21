namespace Sage100Mcp.Data;

/// <summary>Un exercice comptable Sage tel que défini dans P_Dossier (D_DebutExoNN / D_FinExoNN).</summary>
public sealed record SageExerciceInfo(int Numero, DateTime Debut, DateTime Fin);

/// <summary>Résolution des exercices comptables déclarés dans P_Dossier (jusqu'à 10 exercices, numérotés 01 à 10).</summary>
public static class SageExercice
{
    private const int MaxExercices = 10;

    /// <summary>Liste les exercices définis dans P_Dossier (ceux dont les bornes sont renseignées).</summary>
    public static async Task<IReadOnlyList<SageExerciceInfo>> ListAsync(
        SageDatabaseRegistry registry, string? baseSage, CancellationToken ct)
    {
        var colonnes = string.Join(", ", Enumerable.Range(1, MaxExercices)
            .Select(n => $"D_DebutExo{n:00}, D_FinExo{n:00}"));
        var rows = await registry.QueryAsync(baseSage, $"SELECT TOP (1) {colonnes} FROM P_Dossier", null, ct);
        if (rows.Count == 0) return Array.Empty<SageExerciceInfo>();

        var row = rows[0];
        var exercices = new List<SageExerciceInfo>();
        for (var n = 1; n <= MaxExercices; n++)
        {
            var debut = row[$"D_DebutExo{n:00}"] as DateTime?;
            var fin = row[$"D_FinExo{n:00}"] as DateTime?;
            // Sage laisse les bornes non utilisées à la date minimale SQL (01/01/1753) : on les ignore.
            if (debut is null || fin is null || debut.Value.Year < 1900) continue;
            exercices.Add(new SageExerciceInfo(n, debut.Value, fin.Value));
        }
        return exercices;
    }

    /// <summary>
    /// Résout un exercice par numéro (tel que défini dans P_Dossier), ou à défaut l'exercice couvrant
    /// <paramref name="dateReference"/> (ou le dernier exercice clos avant cette date).
    /// Renvoie null si aucun exercice n'est configuré dans P_Dossier.
    /// </summary>
    public static async Task<SageExerciceInfo?> ResolveAsync(
        SageDatabaseRegistry registry, string? baseSage, int? numero, DateTime dateReference, CancellationToken ct)
    {
        var exercices = await ListAsync(registry, baseSage, ct);
        if (exercices.Count == 0) return null;

        if (numero is not null)
        {
            return exercices.FirstOrDefault(e => e.Numero == numero.Value)
                ?? throw new ArgumentException(
                    $"Exercice {numero} introuvable dans P_Dossier. Exercices disponibles : " +
                    string.Join(", ", exercices.Select(e => e.Numero)) + ".");
        }

        return exercices.FirstOrDefault(e => dateReference >= e.Debut && dateReference <= e.Fin)
            ?? exercices.Where(e => e.Fin <= dateReference).OrderByDescending(e => e.Fin).FirstOrDefault()
            ?? exercices.OrderBy(e => e.Debut).First();
    }
}
