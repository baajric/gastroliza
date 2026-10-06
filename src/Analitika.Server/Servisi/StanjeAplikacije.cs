using Analitika.Server.Mostovi;

namespace Analitika.Server.Servisi;

public enum Preset { Danas, SedamDana, TridesetDana, OvajMjesec, ProsliMjesec, GodinaUnazad, Prilagodjeno }

/// <summary>
/// Period je zajednički svim stranicama — jedan izbornik iznad svega,
/// a ne po jedan filter u svakoj kartici.
/// </summary>
public sealed class StanjeAplikacije
{
    private readonly IKljucStore _restorani;

    /// <summary>
    /// Restoran dolazi iz prijave: kolačić nosi ID restorana koji pripada nalogu,
    /// pa korisnik vidi samo svoj restoran. Bez prijave restorana nema.
    /// </summary>
    public StanjeAplikacije(IKljucStore restorani, Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider prijava)
    {
        _restorani = restorani;

        // Blazor postavi stanje prijave čim se krug otvori, pa je zadatak već završen;
        // ako nije, restoran se postavi kad stigne.
        var zadatak = prijava.GetAuthenticationStateAsync();
        if (zadatak.IsCompletedSuccessfully) PostaviKorisnika(zadatak.Result.User);
        prijava.AuthenticationStateChanged += async z => PostaviKorisnika((await z).User);

        PostaviPreset(Preset.TridesetDana);
    }

    private void PostaviKorisnika(System.Security.Claims.ClaimsPrincipal korisnik)
    {
        Restoran = Analitika.Server.Nalozi.Prijava.PrviRestoran(korisnik) is { } id ? _restorani.PronadjiPoId(id) : null;
        Promjena?.Invoke();
    }

    public Restoran? Restoran { get; private set; }

    /// <summary>Sve se računa u odnosu na današnji datum, kako je i traženo.</summary>
    public static DateOnly Danas => DateOnly.FromDateTime(DateTime.Today);

    public DateOnly Od { get; private set; }
    public DateOnly Do { get; private set; }
    public Preset Izabrani { get; private set; }

    /// <summary>Godišnje baze koje ulaze u analizu; bira ih korisnik u Postavkama.</summary>
    public List<string> Baze { get; private set; } = [];

    public int BrojDana => Do.DayNumber - Od.DayNumber + 1;

    /// <summary>Prethodni period jednake dužine — osnova za poređenje "u odnosu na prošli".</summary>
    public (DateOnly Od, DateOnly Do) PrethodniPeriod()
    {
        var duzina = BrojDana;
        var doDat = Od.AddDays(-1);
        return (doDat.AddDays(-(duzina - 1)), doDat);
    }

    /// <summary>
    /// Ispisani prethodni period, npr. „18.06. – 17.07.2026.".
    /// Stoji uz svaki postotak da korisnik ne mora pogađati s čim se poredi.
    /// </summary>
    public string PrethodniOpis
    {
        get
        {
            var (od, doDat) = PrethodniPeriod();
            return Opis(od, doDat);
        }
    }

    /// <summary>Ispisani izabrani period.</summary>
    public string TekuciOpis => Opis(Od, Do);

    private static string Opis(DateOnly od, DateOnly doDatuma) =>
        od.Year == doDatuma.Year
            ? $"{od:dd.MM.} – {doDatuma:dd.MM.yyyy.}"     // ista godina — piše se jednom
            : $"{od:dd.MM.yyyy.} – {doDatuma:dd.MM.yyyy.}";

    public void PostaviPreset(Preset preset)
    {
        Izabrani = preset;
        var d = Danas;

        (Od, Do) = preset switch
        {
            Preset.Danas         => (d, d),
            Preset.SedamDana     => (d.AddDays(-6), d),
            Preset.TridesetDana  => (d.AddDays(-29), d),
            Preset.OvajMjesec    => (new DateOnly(d.Year, d.Month, 1), d),
            Preset.ProsliMjesec  => PrviIZadnjiProslogMjeseca(d),
            Preset.GodinaUnazad  => (d.AddYears(-1).AddDays(1), d),
            _                    => (Od, Do)
        };

        Promjena?.Invoke();
    }

    private static (DateOnly, DateOnly) PrviIZadnjiProslogMjeseca(DateOnly d)
    {
        var prvi = new DateOnly(d.Year, d.Month, 1).AddMonths(-1);
        return (prvi, prvi.AddMonths(1).AddDays(-1));
    }

    public void PostaviRaspon(DateOnly od, DateOnly doDatuma)
    {
        if (od > doDatuma) (od, doDatuma) = (doDatuma, od);
        Od = od;
        Do = doDatuma;
        Izabrani = Preset.Prilagodjeno;
        Promjena?.Invoke();
    }

    public void PostaviBaze(IEnumerable<string> baze)
    {
        Baze = [.. baze];
        Promjena?.Invoke();
    }

    /// <summary>Stranice se pretplaćuju da se same osvježe kad se period promijeni.</summary>
    public event Action? Promjena;

    public static string Naziv(Preset p) => p switch
    {
        Preset.Danas => "Danas",
        Preset.SedamDana => "7 dana",
        Preset.TridesetDana => "30 dana",
        Preset.OvajMjesec => "Ovaj mjesec",
        Preset.ProsliMjesec => "Prošli mjesec",
        Preset.GodinaUnazad => "Godina unazad",
        _ => "Prilagođeno"
    };
}
