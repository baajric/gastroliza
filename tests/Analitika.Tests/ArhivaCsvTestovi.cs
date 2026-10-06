using Analitika.Server.Servisi;
using Analitika.Shared.Upiti;

namespace Analitika.Tests;

/// <summary>Čitanje fajla prodaje prošle godine — oblici koje ljudi stvarno šalju.</summary>
public class ArhivaCsvTestovi
{
    private static (List<StavkaArhive> Stavke, IzvjestajCitanja Izvjestaj) Procitaj(string sadrzaj, int? godina = null)
    {
        var izvjestaj = new IzvjestajCitanja();
        var stavke = ArhivaCsv.Citaj(new StringReader(sadrzaj), izvjestaj, godina).ToList();
        return (stavke, izvjestaj);
    }

    [Fact]
    public void Predlozak_se_cita_bez_greske()
    {
        var (stavke, izvjestaj) = Procitaj(ArhivaCsv.Predlozak(), 2025);

        Assert.Null(izvjestaj.Kobna);
        Assert.Equal(2, stavke.Count);
        Assert.Equal(0, izvjestaj.Neispravnih);
        Assert.Equal(';', izvjestaj.Razdjelnik);

        var espresso = stavke[0];
        Assert.Equal(new DateOnly(2025, 8, 15), espresso.Datum);
        Assert.Equal(1001d, espresso.Barkod);
        Assert.Equal(2m, espresso.Izlaz);
        Assert.Equal(2.50m, espresso.Mpc);
        Assert.Equal((14 * 3600 + 35 * 60) * 100, espresso.Vrijeme);
        Assert.Equal("Amar", espresso.Konobar);
        Assert.Equal(14m, izvjestaj.Promet);
        Assert.Single(izvjestaj.Racuni);
    }

    [Fact]
    public void Izvoz_iz_SSMS_sa_zarezom_i_tackom_se_prepoznaje()
    {
        // Imena stupaca iz kase, drugi redoslijed, decimalna tačka, ISO datum, vrijeme u stotinkama.
        var csv = """
            MPC,IZLAZ,NAZIV,BARKOD,DATUM,VRIJEME,ID_CARD,BROJ_FR,GRUPA
            3.50,1.000,"Coca cola, 0,25",77,2024-03-01,5220000,15,A12,Pice
            """;
        var (stavke, izvjestaj) = Procitaj(csv, 2024);

        Assert.Null(izvjestaj.Kobna);
        var s = Assert.Single(stavke);
        Assert.Equal(',', izvjestaj.Razdjelnik);
        Assert.Equal("Coca cola, 0,25", s.Naziv);
        Assert.Equal(3.5m, s.Mpc);
        Assert.Equal(5_220_000, s.Vrijeme);
        Assert.Null(s.Konobar);
    }

    [Fact]
    public void Nedostaje_obavezan_stupac_je_kobna_greska()
    {
        var (stavke, izvjestaj) = Procitaj("DATUM;NAZIV;IZLAZ\n01.01.2025.;Kafa;1");

        Assert.Empty(stavke);
        Assert.NotNull(izvjestaj.Kobna);
        Assert.Contains("BARKOD", izvjestaj.Kobna);
        Assert.Contains("MPC", izvjestaj.Kobna);
    }

    [Fact]
    public void Stavka_iz_druge_godine_se_odbacuje_i_prijavljuje()
    {
        var csv = """
            DATUM;BARKOD;NAZIV;IZLAZ;MPC
            31.12.2024.;1;Kafa;1;2
            01.01.2025.;1;Kafa;1;2
            """;
        var (stavke, izvjestaj) = Procitaj(csv, 2024);

        Assert.Single(stavke);
        Assert.Equal(1, izvjestaj.Neispravnih);
        Assert.Contains("Red 3", izvjestaj.Greske[0]);
    }

    [Fact]
    public void Neispravni_redovi_se_preskacu_a_ostali_cuvaju()
    {
        var csv = """
            DATUM;BARKOD;NAZIV;IZLAZ;MPC
            15.06.2025.;1;Kafa;1;2
            nije datum;1;Kafa;1;2
            15.06.2025.;abc;Kafa;1;2
            15.06.2025.;1;Kafa;jedan;2

            16.06.2025.;2;Sok;2;3,5
            """;
        var (stavke, izvjestaj) = Procitaj(csv, 2025);

        Assert.Equal(2, stavke.Count);
        Assert.Equal(3, izvjestaj.Neispravnih);
        Assert.Equal(new DateOnly(2025, 6, 15), izvjestaj.PrviDatum);
        Assert.Equal(new DateOnly(2025, 6, 16), izvjestaj.ZadnjiDatum);
        Assert.Equal(9m, izvjestaj.Promet);
    }

    [Theory]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("12,5", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("-2", -2)]
    public void Broj_sa_zarezom_ili_tackom(string tekst, double ocekivano)
    {
        Assert.True(ArhivaCsv.ProcitajBroj(tekst, out var broj));
        Assert.Equal((decimal)ocekivano, broj);
    }

    [Theory]
    [InlineData("14:35", 5_250_000)]
    [InlineData("9:05:30", 3_273_000)]
    [InlineData("8639999", 8_639_999)]
    public void Vrijeme_u_satima_ili_stotinkama(string tekst, int ocekivano)
    {
        Assert.True(ArhivaCsv.ProcitajVrijeme(tekst, out var stotinke));
        Assert.Equal(ocekivano, stotinke);
    }

    [Fact]
    public void Vrijeme_izvan_dana_se_odbija() => Assert.False(ArhivaCsv.ProcitajVrijeme("8640000", out _));

    [Fact]
    public void Prikazna_imena_mijenjaju_samo_upisane_konobare()
    {
        var konobari = new List<RedKonobara>
        {
            new() { IdKartice = "15", Ime = "Konobar15", Promet = 100 },
            new() { IdKartice = "16", Ime = "Konobar16", Promet = 50 }
        };
        var postavke = new Postavke { ImenaKonobara = new() { ["15"] = "Amar" } };

        var rezultat = PostavkeRestorana.Preimenuj(konobari, postavke);

        Assert.Equal("Amar", rezultat[0].Ime);
        Assert.Equal("Konobar16", rezultat[1].Ime);
        Assert.Equal(100, rezultat[0].Promet);
    }

    [Theory]
    [InlineData("Gradska kafana", "GK")]
    [InlineData("City", "CI")]
    [InlineData("  ", "?")]
    public void Inicijali_za_krug(string naziv, string ocekivano) =>
        Assert.Equal(ocekivano, PostavkeRestorana.Inicijali(naziv));
}

/// <summary>Povratak poslije prijave ne smije voditi na tuđi sajt.</summary>
public class PrijavaTestovi
{
    [Theory]
    [InlineData("/konobari", "/konobari")]
    [InlineData("/postavke?x=1", "/postavke?x=1")]
    [InlineData("https://zlo.example/", "/")]
    [InlineData("//zlo.example", "/")]
    [InlineData(@"/\zlo.example", "/")]
    [InlineData(null, "/")]
    public void Siguran_povratak(string? adresa, string ocekivano) =>
        Assert.Equal(ocekivano, Analitika.Server.Nalozi.Prijava.SiguranPovratak(adresa));
}
