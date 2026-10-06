namespace Analitika.Server.Servisi;

/// <summary>
/// Boje grafova. Uloge su stroge:
///   • Plava       — tekući period, glavna serija. Jedina „boja podatka".
///   • Siva        — prethodni period, uvijek isprekidana (Stripe „previous period").
///   • Jantar, tirkiz — samo kad graf ima više kategorija (piće / hrana / ostalo).
///   • Zelena/crvena — isključivo rast i pad u značkama, nikad serija na grafu.
///
/// Parovi provjereni na plohi #0a0a0a: plava ↔ jantar i plava ↔ tirkiz razlikuju se
/// i pri deuteranopiji, jer se razlikuju i po svjetlini, ne samo po tonu.
/// </summary>
public static class Paleta
{
    /// <summary>Tekući period, istaknuti stupac, glavna serija.</summary>
    public static string Serija1() => "#3b8bff";

    /// <summary>Poređenje: prethodni period. Neutralna, da ne glumi drugu mjeru.</summary>
    public static string Serija2() => "#6b6b6b";

    /// <summary>Druga i treća kategorija.</summary>
    public static string Kategorija2() => "#f2a93b";
    public static string Kategorija3() => "#2fc6a4";

    public static string Mreza() => "#1c1c1c";
    public static string Osa() => "#242424";
    public static string PrigusenoMastilo() => "#6b6b6b";
    public static string SekundarnoMastilo() => "#a1a1a1";

    /// <summary>Stupac koji nije istaknut — ploha, da vrhunac sam iskoči.</summary>
    public static string Neistaknuto() => "#262626";

    /// <summary>Ploha na kojoj graf stoji.</summary>
    public static string Povrsina() => "#0a0a0a";

    /// <summary>Porast naspram prethodnog perioda. Nikad kao serija na grafu.</summary>
    public static string Rast() => "#4ccf7d";

    /// <summary>Pad naspram prethodnog perioda. Nikad kao serija na grafu.</summary>
    public static string Pad() => "#ff6166";

    public static string[] Kategorijski() => [Serija1(), Kategorija2(), Kategorija3()];

    /// <summary>
    /// Skala toplotne mape: od plohe do pune plave. Jedan ton, svjetlina nosi
    /// vrijednost — višebojna skala bi izmislila kategorije kojih u podatku nema.
    /// </summary>
    public static string Guzva(decimal udio)
    {
        var t = (double)Math.Clamp(udio, 0m, 1m);

        // Ispod praga polje ostaje gotovo prazno, da se mreža ne pretvori u puni blok.
        if (t < 0.02) return "#141414";

        var (r, g, b) = (59, 139, 255);
        var (pr, pg, pb) = (24, 24, 24);

        // Eksponent iznad jedinice drži srednje vrijednosti tamnijim, pa se vrhunac
        // stvarno istakne.
        var k = Math.Pow(t, 1.35);

        return $"#{(int)Math.Round(pr + (r - pr) * k):x2}{(int)Math.Round(pg + (g - pg) * k):x2}{(int)Math.Round(pb + (b - pb) * k):x2}";
    }
}
