using Analitika.Server.Servisi;
using Analitika.Shared.Upiti;

namespace Analitika.Tests;

/// <summary>
/// Testovi modela projekcije. Ne diraju bazu — rade nad izmišljenom historijom
/// sa poznatim odgovorom, pa hvataju grešku u samoj računici.
/// </summary>
public class ProjekcijaTestovi
{
    private const double Sifra = 1;
    private static readonly Dictionary<double, string> Nazivi = new() { [Sifra] = "Test artikal" };

    private static Normativ Recept(decimal poKomadu) => new()
    {
        ArtikalSifra = Sifra,
        ArtikalNaziv = "Test artikal",
        VaziOd = new DateOnly(2000, 1, 1),
        Stavke = [new NormativStavka { RepromaterijalBarkod = 1, Naziv = "Brašno", Kolicina = poKomadu, Jm = "KG" }]
    };

    /// <summary>Ravna historija: isto svaki dan, pa je i procjena jednostavna za provjeriti.</summary>
    private static List<DnevnaProdaja> Ravna(DateOnly doDatuma, int dana, decimal dnevno) =>
        [.. Enumerable.Range(0, dana)
            .Select(i => new DnevnaProdaja(doDatuma.AddDays(-i), Sifra, dnevno))];

    [Fact]
    public void Ravna_prodaja_daje_procjenu_jednaku_dnevnom_prosjeku()
    {
        var danas = new DateOnly(2026, 8, 15);
        var historija = Ravna(danas, 60, 10m);

        var r = Projekcija.Izracunaj(historija, [Recept(0.2m)], Nazivi,
            danas, danas.AddDays(1), danas.AddDays(7));

        var a = Assert.Single(r.Artikli);
        Assert.Equal(70m, a.Ocekivano);                 // 7 dana × 10 komada
        Assert.Equal(14m, Assert.Single(r.Sirovine).Ocekivano); // 70 × 0,2 kg
    }

    [Fact]
    public void Tekuci_mjesec_ima_prednost_nad_starijim_sedmicama()
    {
        // Juli mirniji (10/dan), august dvostruko jači (20/dan) — kao u stvarnim podacima.
        var danas = new DateOnly(2026, 8, 15);
        var historija = new List<DnevnaProdaja>();
        historija.AddRange(Enumerable.Range(0, 31)
            .Select(i => new DnevnaProdaja(new DateOnly(2026, 7, 1).AddDays(i), Sifra, 10m)));
        historija.AddRange(Enumerable.Range(0, 15)
            .Select(i => new DnevnaProdaja(new DateOnly(2026, 8, 1).AddDays(i), Sifra, 20m)));

        var r = Projekcija.Izracunaj(historija, [Recept(1m)], Nazivi,
            danas, new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 22));

        // Mora biti na augustovskom nivou (140 za 7 dana), ne na sredini
        // između jula i augusta (105) niti podignuto iznad njega.
        Assert.Equal(140m, Assert.Single(r.Artikli).Ocekivano);
    }

    [Fact]
    public void Na_pocetku_mjeseca_se_koriste_prethodne_sedmice()
    {
        // 2. august — tekući mjesec ima samo dva dana, pa osnovica mora posegnuti unazad.
        var danas = new DateOnly(2026, 8, 2);
        var historija = Enumerable.Range(0, 40)
            .Select(i => new DnevnaProdaja(danas.AddDays(-i), Sifra, 10m))
            .ToList();

        var r = Projekcija.Izracunaj(historija, [Recept(1m)], Nazivi,
            danas, danas.AddDays(1), danas.AddDays(7));

        Assert.Equal(70m, Assert.Single(r.Artikli).Ocekivano);
    }

    [Fact]
    public void Dan_u_sedmici_se_postuje()
    {
        // Vikendom dvostruko više nego radnim danom.
        var danas = new DateOnly(2026, 8, 15);
        var historija = Enumerable.Range(0, 56)
            .Select(i => danas.AddDays(-i))
            .Select(d => new DnevnaProdaja(d, Sifra,
                d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 20m : 10m))
            .ToList();

        // Ciljamo samo jednu subotu.
        var subota = new DateOnly(2026, 8, 22);
        Assert.Equal(DayOfWeek.Saturday, subota.DayOfWeek);

        var r = Projekcija.Izracunaj(historija, [Recept(1m)], Nazivi, danas, subota, subota);

        Assert.Equal(20m, Assert.Single(r.Artikli).Ocekivano);
    }

    [Fact]
    public void Jedan_neradni_dan_ne_obara_procjenu_bitno()
    {
        var danas = new DateOnly(2026, 8, 15);
        var historija = Ravna(danas, 56, 10m);
        var praznik = historija.First(h => h.Datum.DayOfWeek == DayOfWeek.Monday);
        historija[historija.IndexOf(praznik)] = praznik with { Kolicina = 0m };

        var r = Projekcija.Izracunaj(historija, [Recept(1m)], Nazivi,
            danas, danas.AddDays(1), danas.AddDays(7));

        // Jedan zatvoren dan u osam sedmica smije spustiti očekivanje za koji postotak —
        // i budući period će sadržati poneki takav dan. Ne smije ga srušiti.
        Assert.InRange(Assert.Single(r.Artikli).Ocekivano, 64m, 70m);
    }

    [Fact]
    public void Dani_bez_prodaje_artikla_ulaze_u_racun()
    {
        // Artikal se prodaje samo ponedjeljkom, po 10 komada. Ostalih dana nema reda
        // u podacima — ne smije se pretpostaviti da se prodaje svaki dan.
        var danas = new DateOnly(2026, 8, 15);
        var drugiArtikal = 2d;

        var historija = new List<DnevnaProdaja>();
        for (var i = 0; i < 40; i++)
        {
            var d = danas.AddDays(-i);

            // Drugi artikal se prodaje svaki dan — on definiše koji su dani radni.
            historija.Add(new DnevnaProdaja(d, drugiArtikal, 100m));

            if (d.DayOfWeek == DayOfWeek.Monday)
                historija.Add(new DnevnaProdaja(d, Sifra, 10m));
        }

        var r = Projekcija.Izracunaj(historija, [Recept(1m)],
            new Dictionary<double, string> { [Sifra] = "Test artikal", [drugiArtikal] = "Drugi" },
            danas, danas.AddDays(1), danas.AddDays(7));

        var a = Assert.Single(r.Artikli, x => x.Sifra == Sifra);

        // Sedmica sadrži jedan ponedjeljak, pa se očekuje oko 10 komada — nikako 70.
        Assert.InRange(a.Ocekivano, 0m, 25m);
    }

    [Fact]
    public void Projekcija_reprodukuje_skorasnju_prodaju_kad_je_period_isti()
    {
        // Najvažniji test: ako se projektuje period jednake dužine i sastava kao onaj
        // koji je upravo prošao, rezultat mora biti blizu stvarno prodanog. Model je
        // kroz razvoj redom davao 42, 71, 76, 81, 92 i 95 komada tamo gdje je stvarnost
        // bila 55 — svaki put zbog druge greške u računu.
        var danas = new DateOnly(2026, 8, 15);
        var neujednacenaProdaja = new decimal[] { 4, 11, 5, 4, 2, 1, 5, 5, 8, 4, 1, 5 };

        var historija = new List<DnevnaProdaja>();
        var i = 0;
        for (var d = new DateOnly(2026, 8, 1); d <= danas; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Sunday) continue;   // nedjeljom zatvoreno
            if (i >= neujednacenaProdaja.Length) break;
            historija.Add(new DnevnaProdaja(d, Sifra, neujednacenaProdaja[i++]));
        }

        var stvarno = neujednacenaProdaja.Take(i).Sum();

        var r = Projekcija.Izracunaj(historija, [Recept(1m)], Nazivi,
            danas, new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 29));

        var procjena = Assert.Single(r.Artikli).Ocekivano;

        Assert.InRange(procjena, stvarno * 0.85m, stvarno * 1.15m);
    }

    [Fact]
    public void Kratka_historija_nosi_upozorenje()
    {
        var danas = new DateOnly(2026, 8, 15);
        var r = Projekcija.Izracunaj(Ravna(danas, 10, 5m), [Recept(1m)], Nazivi,
            danas, danas.AddDays(1), danas.AddDays(7));

        Assert.NotNull(r.Upozorenje);
        Assert.False(r.SezonaIzmjerena);
    }

    [Fact]
    public void Bez_historije_vraca_prazan_rezultat_a_ne_pada()
    {
        var danas = new DateOnly(2026, 8, 15);
        var r = Projekcija.Izracunaj([], [Recept(1m)], Nazivi, danas, danas.AddDays(1), danas.AddDays(7));

        Assert.Empty(r.Artikli);
        Assert.Empty(r.Sirovine);
        Assert.NotNull(r.Upozorenje);
    }

    [Fact]
    public void Artikal_bez_recepta_ne_ulazi_u_sirovine()
    {
        var danas = new DateOnly(2026, 8, 15);
        var historija = Ravna(danas, 30, 10m);

        var r = Projekcija.Izracunaj(historija, [], Nazivi, danas, danas.AddDays(1), danas.AddDays(7));

        Assert.Single(r.Artikli);
        Assert.Empty(r.Sirovine);
    }
}
