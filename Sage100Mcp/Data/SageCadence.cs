namespace Sage100Mcp.Data;

/// <summary>Périodicité constatée d'une suite de dates (échéances d'emprunt, prélèvements récurrents…).</summary>
/// <param name="JoursMedian">Écart médian entre deux occurrences, en jours.</param>
/// <param name="Libelle">Libellé français de la cadence (« Mensuelle », « Trimestrielle », « Irrégulière »…).</param>
/// <param name="MoisPeriode">Périodicité exprimée en mois (1, 2, 3, 6, 12) quand elle en est un multiple, sinon null.</param>
/// <param name="JourDuMois">Jour du mois le plus fréquent, uniquement pour une cadence mensuelle ou plus longue.</param>
/// <param name="Reguliere">Faux si les écarts s'écartent trop de la médiane pour qu'une projection ait du sens.</param>
public sealed record SageCadenceInfo(
    int JoursMedian, string Libelle, int? MoisPeriode, int? JourDuMois, bool Reguliere);

/// <summary>
/// Détection de périodicité à partir des dates réellement constatées en comptabilité.
/// Sage ne stocke aucun échéancier pour les emprunts ni pour les charges fixes : la cadence
/// (le 5 de chaque mois, tous les trimestres…) ne peut être que déduite de l'historique.
/// </summary>
public static class SageCadence
{
    /// <summary>Déduit la périodicité d'une suite de dates. Renvoie null en dessous de deux occurrences.</summary>
    public static SageCadenceInfo? Detecter(IReadOnlyList<DateTime> dates)
    {
        if (dates.Count < 2) return null;

        var triees = dates.OrderBy(d => d).ToList();
        var ecarts = new List<int>();
        for (var i = 1; i < triees.Count; i++)
            ecarts.Add((int)(triees[i] - triees[i - 1]).TotalDays);
        ecarts.Sort();
        var median = ecarts[ecarts.Count / 2];
        if (median <= 0) return null;

        (string libelle, int? mois) = median switch
        {
            <= 2 => ("Quotidienne", (int?)null),
            <= 9 => ("Hebdomadaire", null),
            <= 20 => ("Quinzaine", null),
            <= 45 => ("Mensuelle", 1),
            <= 75 => ("Bimestrielle", 2),
            <= 135 => ("Trimestrielle", 3),
            <= 225 => ("Semestrielle", 6),
            <= 420 => ("Annuelle", 12),
            _ => ("Ponctuelle", null)
        };

        // Régulière si au moins trois quarts des écarts restent à moins de 25 % de la médiane
        // (tolérance plancher de 3 jours : un prélèvement « le 5 » glisse avec les week-ends).
        var tolerance = Math.Max(3, median / 4);
        var conformes = ecarts.Count(e => Math.Abs(e - median) <= tolerance);
        var reguliere = conformes * 4 >= ecarts.Count * 3;
        if (!reguliere) libelle = "Irrégulière";

        int? jourDuMois = mois is null
            ? null
            : triees.GroupBy(d => d.Day).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

        return new SageCadenceInfo(median, libelle, mois, jourDuMois, reguliere);
    }

    /// <summary>
    /// Date de la <paramref name="rang"/>-ième occurrence après <paramref name="derniere"/> :
    /// décalage en mois (en recalant sur le jour habituel) si la cadence est mensuelle, sinon en jours.
    /// </summary>
    public static DateTime Suivante(DateTime derniere, SageCadenceInfo cadence, int rang = 1)
    {
        if (cadence.MoisPeriode is int mois and > 0)
        {
            var cible = derniere.AddMonths(mois * rang);
            if (cadence.JourDuMois is int jour)
                cible = new DateTime(cible.Year, cible.Month, Math.Min(jour, DateTime.DaysInMonth(cible.Year, cible.Month)));
            return cible;
        }
        return derniere.AddDays((double)cadence.JoursMedian * rang);
    }
}
