using System.Globalization;
using System.Text;

namespace Analitika.Shared.Upiti;

/// <summary>Kartica konobara kako je zapisana u kasi.</summary>
public sealed record KonobarKase(string IdKartice, string ImeUKasi);

/// <summary>Odakle dolaze podaci za jednu godinu.</summary>
public enum IzvorGodine
{
    /// <summary>Tekuća godina — uvijek iz žive baze kase, nikad iz fajla.</summary>
    Uzivo,

    /// <summary>Prošla godina čija baza i dalje stoji na SQL Serveru kase.</summary>
    BazaUKasi,

    /// <summary>Prošla godina uvezena iz fajla u lokalnu bazu Analitika.</summary>
    Uvezeno,

    /// <summary>Nema podataka — može se uvesti fajl.</summary>
    Nema
}

public sealed record GodinaPodataka
{
    public int Godina { get; init; }
    public IzvorGodine Izvor { get; init; }
    public DateOnly? PrviDatum { get; init; }
    public DateOnly? ZadnjiDatum { get; init; }
    public long BrojStavki { get; init; }

    /// <summary>Naziv baze u kasi (EtisRpos_2025), kad je izvor baza.</summary>
    public string? Baza { get; init; }

    /// <summary>Ime uvezenog fajla i kada je uvezen, kad je izvor fajl.</summary>
    public string? NazivFajla { get; init; }
    public DateTime? Uvezeno { get; init; }

    /// <summary>
    /// Uvezena godina za koju se kasnije pojavila i baza u kasi. Baza ima prednost,
    /// a uvoz ostaje zapisan dok ga korisnik ne obriše.
    /// </summary>
    public bool ImaIUvoz { get; init; }
}

/// <summary>Jedna prodajna stavka iz fajla — isti stupci kao POS_Stavke u kasi.</summary>
public sealed record StavkaArhive(
    DateOnly Datum,
    string? BrojFr,
    double Barkod,
    string Naziv,
    string? Grupa,
    string? IdKartice,
    string? Konobar,
    decimal Izlaz,
    decimal Mpc,
    int? Vrijeme);

/// <summary>Šta je pročitano iz fajla — prikazuje se korisniku prije potvrde uvoza.</summary>
public sealed class IzvjestajCitanja
{
    public const int NajviseGresaka = 12;

    public int Ispravnih { get; set; }
    public int Neispravnih { get; set; }
    public List<string> Greske { get; } = [];
    public DateOnly? PrviDatum { get; set; }
    public DateOnly? ZadnjiDatum { get; set; }
    public decimal Promet { get; set; }
    public HashSet<string> Racuni { get; } = [];
    public HashSet<string> Konobari { get; } = [];
    public HashSet<double> Artikli { get; } = [];
    public char Razdjelnik { get; set; }

    /// <summary>Greška u zaglavlju — fajl se uopšte ne može čitati.</summary>
    public string? Kobna { get; set; }

    internal void Greska(int red, string poruka)
    {
        Neispravnih++;
        if (Greske.Count < NajviseGresaka) Greske.Add($"Red {red}: {poruka}");
    }
}

/// <summary>
/// Čita prodajne stavke jedne godine iz CSV fajla.
///
/// Prihvata ono što ljudi stvarno imaju: Excel na bosanskim postavkama snima
/// sa <c>;</c> i decimalnim zarezom, izvoz iz SSMS-a sa <c>,</c> i tačkom.
/// Razdjelnik se prepoznaje iz zaglavlja, stupci se traže po imenu (redoslijed
/// nije bitan), a prepoznaju se i imena stupaca iz kase (DATUM, IZLAZ, MPC…).
/// </summary>
public static class ArhivaCsv
{
    private static readonly Dictionary<string, string> Nazivi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DATUM"] = "DATUM", ["DATE"] = "DATUM",
        ["BROJ_FR"] = "BROJ_FR", ["RACUN"] = "BROJ_FR", ["RAČUN"] = "BROJ_FR", ["BROJ_RACUNA"] = "BROJ_FR",
        ["BARKOD"] = "BARKOD", ["SIFRA"] = "BARKOD", ["ŠIFRA"] = "BARKOD",
        ["NAZIV"] = "NAZIV", ["ARTIKAL"] = "NAZIV",
        ["GRUPA"] = "GRUPA",
        ["ID_CARD"] = "ID_CARD", ["KARTICA"] = "ID_CARD",
        ["KONOBAR"] = "KONOBAR", ["IME_PREZIME"] = "KONOBAR",
        ["IZLAZ"] = "IZLAZ", ["KOLICINA"] = "IZLAZ", ["KOLIČINA"] = "IZLAZ",
        ["MPC"] = "MPC", ["CIJENA"] = "MPC",
        ["VRIJEME"] = "VRIJEME", ["SAT"] = "VRIJEME"
    };

    public static readonly string[] Obavezni = ["DATUM", "BARKOD", "NAZIV", "IZLAZ", "MPC"];

    /// <summary>Zaglavlje predloška koji se nudi za preuzimanje.</summary>
    public const string Zaglavlje = "DATUM;VRIJEME;BROJ_FR;BARKOD;NAZIV;GRUPA;ID_CARD;KONOBAR;IZLAZ;MPC";

    private static readonly string[] FormatiDatuma =
    [
        "dd.MM.yyyy", "dd.MM.yyyy.", "d.M.yyyy", "d.M.yyyy.", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss",
        "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy. HH:mm", "d.M.yyyy HH:mm", "d.M.yyyy. HH:mm"
    ];

    /// <summary>
    /// Čita stavke jednu po jednu, da veliki fajl ne mora cijeli stati u memoriju.
    /// Neispravni redovi se preskaču i bilježe u izvještaj; ispravni se vraćaju.
    /// </summary>
    /// <param name="godina">Ako je zadana, stavke iz drugih godina se odbacuju kao greška.</param>
    public static IEnumerable<StavkaArhive> Citaj(TextReader ulaz, IzvjestajCitanja izvjestaj, int? godina = null)
    {
        var prvi = ulaz.ReadLine();
        if (prvi is null)
        {
            izvjestaj.Kobna = "Fajl je prazan.";
            yield break;
        }

        prvi = prvi.TrimStart('﻿');
        var razdjelnik = PrepoznajRazdjelnik(prvi);
        izvjestaj.Razdjelnik = razdjelnik;

        var zaglavlje = Polja(prvi, razdjelnik).Select(p => Nazivi.GetValueOrDefault(p.Trim().Trim('"'), "")).ToList();
        var nedostaju = Obavezni.Where(o => !zaglavlje.Contains(o)).ToList();
        if (nedostaju.Count > 0)
        {
            izvjestaj.Kobna = $"U zaglavlju nedostaju stupci: {string.Join(", ", nedostaju)}. " +
                              $"Očekivano zaglavlje: {Zaglavlje}";
            yield break;
        }

        int Stupac(string ime) => zaglavlje.IndexOf(ime);
        var (iDatum, iBroj, iBarkod, iNaziv, iGrupa, iKartica, iKonobar, iIzlaz, iMpc, iVrijeme) = (
            Stupac("DATUM"), Stupac("BROJ_FR"), Stupac("BARKOD"), Stupac("NAZIV"), Stupac("GRUPA"),
            Stupac("ID_CARD"), Stupac("KONOBAR"), Stupac("IZLAZ"), Stupac("MPC"), Stupac("VRIJEME"));

        var red = 1;
        string? linija;
        while ((linija = ulaz.ReadLine()) is not null)
        {
            red++;
            if (string.IsNullOrWhiteSpace(linija)) continue;

            var p = Polja(linija, razdjelnik);
            string? Uzmi(int i) => i >= 0 && i < p.Count && !string.IsNullOrWhiteSpace(p[i]) ? p[i].Trim() : null;

            if (!ProcitajDatum(Uzmi(iDatum), out var datum, out var vrijemeIzDatuma))
            {
                izvjestaj.Greska(red, $"datum „{Uzmi(iDatum)}\" nije prepoznat.");
                continue;
            }
            if (godina is { } g && datum.Year != g)
            {
                izvjestaj.Greska(red, $"datum {datum:dd.MM.yyyy.} nije iz {g}. godine.");
                continue;
            }
            if (!double.TryParse(Uzmi(iBarkod)?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var barkod))
            {
                izvjestaj.Greska(red, $"šifra artikla „{Uzmi(iBarkod)}\" nije broj.");
                continue;
            }
            var naziv = Uzmi(iNaziv);
            if (naziv is null)
            {
                izvjestaj.Greska(red, "nedostaje naziv artikla.");
                continue;
            }
            if (!ProcitajBroj(Uzmi(iIzlaz), out var izlaz))
            {
                izvjestaj.Greska(red, $"količina „{Uzmi(iIzlaz)}\" nije broj.");
                continue;
            }
            if (!ProcitajBroj(Uzmi(iMpc), out var mpc))
            {
                izvjestaj.Greska(red, $"cijena „{Uzmi(iMpc)}\" nije broj.");
                continue;
            }

            int? vrijeme = vrijemeIzDatuma;
            var tekstVremena = Uzmi(iVrijeme);
            if (tekstVremena is not null)
            {
                if (!ProcitajVrijeme(tekstVremena, out var v))
                {
                    izvjestaj.Greska(red, $"vrijeme „{tekstVremena}\" nije prepoznato.");
                    continue;
                }
                vrijeme = v;
            }

            var stavka = new StavkaArhive(
                datum, Skrati(Uzmi(iBroj), 20), barkod, Skrati(naziv, 100)!, Skrati(Uzmi(iGrupa), 50),
                Skrati(Uzmi(iKartica), 20), Skrati(Uzmi(iKonobar), 60), izlaz, mpc, vrijeme);

            izvjestaj.Ispravnih++;
            izvjestaj.Promet += izlaz * mpc;
            if (izvjestaj.PrviDatum is null || datum < izvjestaj.PrviDatum) izvjestaj.PrviDatum = datum;
            if (izvjestaj.ZadnjiDatum is null || datum > izvjestaj.ZadnjiDatum) izvjestaj.ZadnjiDatum = datum;
            if (stavka.BrojFr is { } br) izvjestaj.Racuni.Add($"{datum:yyyyMMdd}{br}");
            if (stavka.IdKartice is { } k) izvjestaj.Konobari.Add(k);
            izvjestaj.Artikli.Add(barkod);

            yield return stavka;
        }
    }

    /// <summary>Predložak za preuzimanje: zaglavlje i dva primjera u Excel obliku.</summary>
    public static string Predlozak() =>
        Zaglavlje + "\r\n" +
        "15.08.2025.;14:35;000123;1001;Espresso;Pice;0000000015;Amar;2;2,50\r\n" +
        "15.08.2025.;14:35;000123;2040;Pizza Capricciosa;Hrana;0000000015;Amar;1;9,00\r\n";

    private static char PrepoznajRazdjelnik(string zaglavlje)
    {
        var kandidati = new[] { ';', '\t', ',' };
        return kandidati.OrderByDescending(k => zaglavlje.Count(z => z == k)).First();
    }

    /// <summary>Dijeli red na polja; poštuje navodnike i udvojene navodnike unutar njih.</summary>
    public static List<string> Polja(string linija, char razdjelnik)
    {
        var polja = new List<string>();
        var sb = new StringBuilder();
        var uNavodnicima = false;
        for (var i = 0; i < linija.Length; i++)
        {
            var c = linija[i];
            if (uNavodnicima)
            {
                if (c == '"' && i + 1 < linija.Length && linija[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') uNavodnicima = false;
                else sb.Append(c);
            }
            else if (c == '"') uNavodnicima = true;
            else if (c == razdjelnik) { polja.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        polja.Add(sb.ToString());
        return polja;
    }

    /// <summary>
    /// Broj sa decimalnim zarezom ili tačkom. Kad su prisutni oba znaka, decimalni je
    /// onaj koji je zadnji — „1.234,50" i „1,234.50" su oba 1234,5.
    /// </summary>
    public static bool ProcitajBroj(string? tekst, out decimal broj)
    {
        broj = 0;
        if (string.IsNullOrWhiteSpace(tekst)) return false;
        var t = tekst.Replace(" ", "").Replace(" ", "");
        var zarez = t.LastIndexOf(',');
        var tacka = t.LastIndexOf('.');
        if (zarez >= 0 && tacka >= 0)
            t = zarez > tacka ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (zarez >= 0)
            t = t.Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out broj);
    }

    private static bool ProcitajDatum(string? tekst, out DateOnly datum, out int? vrijeme)
    {
        datum = default;
        vrijeme = null;
        if (tekst is null) return false;
        if (!DateTime.TryParseExact(tekst, FormatiDatuma, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        datum = DateOnly.FromDateTime(dt);
        if (dt.TimeOfDay != TimeSpan.Zero) vrijeme = (int)(dt.TimeOfDay.TotalSeconds * 100);
        return true;
    }

    /// <summary>
    /// Vrijeme kao „14:35" / „14:35:20" ili kao cijeli broj stotinki sekunde od ponoći,
    /// kako ga kasa čuva (najviše 8.639.999).
    /// </summary>
    public static bool ProcitajVrijeme(string tekst, out int stotinke)
    {
        stotinke = 0;
        if (tekst.Contains(':'))
        {
            if (!TimeSpan.TryParseExact(tekst, [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var ts))
                return false;
            stotinke = (int)(ts.TotalSeconds * 100);
            return true;
        }
        return int.TryParse(tekst, NumberStyles.Integer, CultureInfo.InvariantCulture, out stotinke)
               && stotinke is >= 0 and <= 8_639_999;
    }

    private static string? Skrati(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
