namespace Analitika.Shared.Upiti;

/// <summary>
/// Recept za jedan artikal. Verzionisan po datumu da promjena recepta danas
/// ne pokvari obračun utroška za prošli mjesec.
/// </summary>
public sealed record Normativ
{
    public int Id { get; init; }
    public double ArtikalSifra { get; init; }
    public required string ArtikalNaziv { get; init; }

    /// <summary>Od kada recept vrijedi. Stariji period koristi raniju verziju.</summary>
    public DateOnly VaziOd { get; init; }

    /// <summary>Do kada je vrijedio; null = i dalje važi.</summary>
    public DateOnly? VaziDo { get; init; }

    public List<NormativStavka> Stavke { get; init; } = [];

    /// <summary>Nabavna cijena porcije — zbir cijena sastojaka.</summary>
    public decimal NabavnaCijena => Stavke.Sum(s => s.Kolicina * s.NabavnaCijena);
}

public sealed record NormativStavka
{
    public int Id { get; init; }
    public double RepromaterijalBarkod { get; init; }
    public required string Naziv { get; init; }
    public decimal Kolicina { get; init; }
    public string? Jm { get; init; }

    /// <summary>Nabavna cijena jedinice mjere, prepisana iz šifarnika radi prikaza.</summary>
    public decimal NabavnaCijena { get; init; }
}

/// <summary>Artikal koji se prodaje, a nema recept — kandidat za unos, sortiran po prodaji.</summary>
public sealed record ArtikalBezNormativa(double Sifra, string Naziv, string? Grupa, decimal Kolicina, decimal Promet);

/// <summary>Očekivani utrošak jednog repromaterijala u periodu.</summary>
public sealed record UtrosakRedak
{
    public double Barkod { get; init; }
    public required string Naziv { get; init; }
    public string? Jm { get; init; }

    /// <summary>Koliko je potrošeno prema receptima i stvarnoj prodaji.</summary>
    public decimal Kolicina { get; init; }

    /// <summary>Vrijednost po nabavnim cijenama iz recepta; 0 kad sirovina nema cijenu u šifarniku.</summary>
    public decimal Vrijednost { get; init; }

    /// <summary>Koliko različitih artikala troši ovu sirovinu.</summary>
    public int BrojArtikala { get; init; }
}

/// <summary>Doprinos jednog artikla utrošku sirovine.</summary>
public sealed record UtrosakArtikla(double Sifra, string Naziv, decimal Kolicina, decimal Utroseno);

/// <summary>Postavke koje se čuvaju kod klijenta, ne na serveru.</summary>
public sealed record Postavke
{
    /// <summary>Godišnje baze koje ulaze u analizu; prazno = most sam bira po periodu.</summary>
    public List<string> IzabraneBaze { get; init; } = [];

    /// <summary>Ime restorana u zaglavlju; null = naziv iz uparivanja mosta.</summary>
    public string? NazivRestorana { get; init; }

    /// <summary>
    /// Logo kao data URL (PNG, najviše 256×256 — browser ga smanji prije slanja).
    /// Čuva se uz ostale postavke da ne treba posebno skladište fajlova.
    /// </summary>
    public string? Logo { get; init; }

    /// <summary>
    /// Prikazna imena konobara po ID kartice iz kase. Kasa se ne mijenja —
    /// ime se zamjenjuje tek pri prikazu.
    /// </summary>
    public Dictionary<string, string> ImenaKonobara { get; init; } = [];
}
