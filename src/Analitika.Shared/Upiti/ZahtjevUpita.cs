namespace Analitika.Shared.Upiti;

/// <summary>
/// Zahtjev koji server šalje mostu. Ne sadrži SQL — samo ime upita s bijele liste
/// i parametre koje most sam veže na parametrizovanu naredbu.
/// </summary>
public sealed record ZahtjevUpita
{
    public required ImeUpita Upit { get; init; }

    /// <summary>Početak perioda, uključivo.</summary>
    public DateOnly? OdDatuma { get; init; }

    /// <summary>Kraj perioda, uključivo.</summary>
    public DateOnly? DoDatuma { get; init; }

    /// <summary>
    /// Godišnje baze nad kojima se upit izvršava (npr. EtisRpos_2026).
    /// Prazno = koristi baze izabrane u postavkama klijenta.
    /// Most provjerava svaki naziv protiv stvarnog popisa baza prije upotrebe.
    /// </summary>
    public IReadOnlyList<string> Baze { get; init; } = [];

    /// <summary>Ograničenje broja redova; most ionako nameće vlastiti maksimum.</summary>
    public int? Vrh { get; init; }

    /// <summary>Filter po grupi artikala (Piće / Hrana). Null = sve.</summary>
    public string? Grupa { get; init; }

    /// <summary>
    /// Tijelo za naredbe koje pišu (npr. normativ koji se snima). Ostaje null kod čitanja.
    /// Ovo je podatak, ne SQL — most ga veže kao parametre.
    /// </summary>
    public string? TeretJson { get; init; }

    /// <summary>Identifikator zapisa kod naredbi nad jednim redom.</summary>
    public int? Id { get; init; }

    /// <summary>Barkod repromaterijala kod razlaganja utroška.</summary>
    public double? Barkod { get; init; }

    /// <summary>Godina kod uvoza i brisanja arhive.</summary>
    public int? Godina { get; init; }

    /// <summary>Oznaka uvoza koji je u toku — veže pakete stavki za isti fajl.</summary>
    public Guid? UvozId { get; init; }
}

/// <summary>Odgovor mosta: već agregirani rezultat, nikad sirovi redovi prometa.</summary>
public sealed record OdgovorUpita
{
    public required bool Uspjeh { get; init; }
    public string? Greska { get; init; }

    /// <summary>Rezultat serijalizovan kao JSON; tip zavisi od <see cref="ImeUpita"/>.</summary>
    public string? PodaciJson { get; init; }

    /// <summary>Koliko je upit trajao kod klijenta — za praćenje opterećenja kase.</summary>
    public int TrajanjeMs { get; init; }

    public static OdgovorUpita Greskom(string poruka) => new() { Uspjeh = false, Greska = poruka };
}
