namespace Analitika.Shared.Upiti;

/// <summary>Jedna godišnja POS baza zatečena kod klijenta.</summary>
public sealed record BazaInfo(string Naziv, int? Godina, DateOnly? PrviDatum, DateOnly? ZadnjiDatum, int BrojStavki);

/// <summary>Zbirni pokazatelji za period.</summary>
public sealed record Sazetak
{
    public decimal Promet { get; init; }
    public int BrojRacuna { get; init; }
    public decimal BrojArtikala { get; init; }
    /// <summary>Dani na kojima je bilo prometa — restoran ne radi svaki dan.</summary>
    public int RadnihDana { get; init; }
    public decimal ProsjecnaKorpa => BrojRacuna == 0 ? 0 : Promet / BrojRacuna;
    public decimal PrometPoDanu => RadnihDana == 0 ? 0 : Promet / RadnihDana;
}

public sealed record DanPrometa(DateOnly Datum, decimal Promet, int BrojRacuna);

public sealed record SatPrometa(int Sat, decimal Promet, decimal BrojArtikala);

/// <summary>
/// Jedno polje toplotne mape: promet za određeni dan u sedmici i sat.
/// <paramref name="Dan"/> je 0 = ponedjeljak … 6 = nedjelja.
/// </summary>
public sealed record PoljeGuzve(int Dan, int Sat, decimal Promet, int BrojDana)
{
    /// <summary>Prosjek po pojavljivanju tog dana — inače duži period lažno izgleda jači.</summary>
    public decimal PoDanu => BrojDana == 0 ? 0 : Promet / BrojDana;
}

public sealed record RedArtikla
{
    public double Sifra { get; init; }
    public required string Naziv { get; init; }
    public string? Grupa { get; init; }
    public decimal Kolicina { get; init; }
    public decimal Promet { get; init; }
    /// <summary>Prosječna postignuta cijena — otkriva popuste i različite veličine porcija.</summary>
    public decimal ProsjecnaCijena => Kolicina == 0 ? 0 : Promet / Kolicina;
}

public sealed record RedKonobara
{
    public required string IdKartice { get; init; }
    /// <summary>Ime iz Glopos; kad mapiranja nema, ostaje broj kartice.</summary>
    public required string Ime { get; init; }
    public decimal Promet { get; init; }
    public decimal BrojArtikala { get; init; }
    public int BrojRacuna { get; init; }
    public decimal ProsjecnaKorpa => BrojRacuna == 0 ? 0 : Promet / BrojRacuna;
}

public sealed record RedGrupe(string Grupa, decimal Promet, decimal Kolicina);

public sealed record Artikal(double Sifra, string Naziv, string? Grupa, string? Jm, decimal Cijena);

public sealed record Repromaterijal(double Barkod, string Naziv, string? Jm, decimal NabavnaCijena);

public sealed record StanjeRepro(double Barkod, string Naziv, string? Jm, decimal Stanje, decimal Saldo);

/// <summary>Prodana količina artikla u periodu — osnova za utrošak.</summary>
public sealed record ProdajaArtikla(double Sifra, string Naziv, decimal Kolicina, decimal Promet);

/// <summary>Prodana količina artikla po danu — ulaz za projekcije.</summary>
public sealed record DnevnaProdaja(DateOnly Datum, double Sifra, decimal Kolicina);
