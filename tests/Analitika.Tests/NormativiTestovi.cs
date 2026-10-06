using Analitika.Agent.Queries;
using Analitika.Shared.Upiti;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Analitika.Tests;

/// <summary>
/// Testovi lokalne baze Analitika. Rade nad stvarnim SQL Serverom, ali koriste
/// izmišljenu šifru artikla i sve za sobom počiste, pa ne diraju stvarne recepte.
/// </summary>

public class NormativiTestovi : IAsyncLifetime
{
    private const string Veza =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    /// <summary>Šifra koja ne postoji u šifarniku — da test ne pregazi pravi recept.</summary>
    private const double TestSifra = 999901;

    private readonly LokalnaBaza _baza = new(NullLogger<LokalnaBaza>.Instance);

    private static bool Dostupno()
    {
        try
        {
            using var v = new SqlConnection(Veza);
            v.Open();
            return true;
        }
        catch { return false; }
    }

    public async Task InitializeAsync() => await OcistiAsync();

    public async Task DisposeAsync() => await OcistiAsync();

    private async Task OcistiAsync()
    {
        if (!Dostupno()) return;
        await using var veza = new SqlConnection(Veza);
        await veza.OpenAsync();
        await _baza.PripremiAsync(veza, CancellationToken.None);
        await veza.ChangeDatabaseAsync(LokalnaBaza.Naziv);
        await veza.ExecuteAsync("DELETE FROM dbo.Normativ WHERE ArtikalSifra = @s;", new { s = TestSifra });
    }

    private static Normativ Uzorak(decimal kolicina = 0.25m) => new()
    {
        ArtikalSifra = TestSifra,
        ArtikalNaziv = "TEST artikal",
        Stavke =
        [
            new NormativStavka { RepromaterijalBarkod = 7001, Naziv = "BRASNO TIP 500", Kolicina = kolicina, Jm = "KG", NabavnaCijena = 1.28m }
        ]
    };

    private async Task<SqlConnection> VezaAsync()
    {
        var veza = new SqlConnection(Veza);
        await veza.OpenAsync();
        await _baza.PripremiAsync(veza, CancellationToken.None);
        return veza;
    }

    [SkippableFact]
    public async Task Snimljeni_normativ_se_procita_nazad()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");
        await using var veza = await VezaAsync();

        await _baza.SnimiAsync(veza, Uzorak(), CancellationToken.None);

        var svi = await _baza.SviAsync(veza, samoAktivni: true, CancellationToken.None);
        var n = Assert.Single(svi, x => x.ArtikalSifra == TestSifra);

        var s = Assert.Single(n.Stavke);
        Assert.Equal(0.25m, s.Kolicina);
        Assert.Equal("KG", s.Jm);
        // Cijena porcije = količina × nabavna cijena sastojka.
        Assert.Equal(0.32m, n.NabavnaCijena);
    }

    [SkippableFact]
    public async Task Prvi_recept_vazi_i_za_raniju_prodaju()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");
        await using var veza = await VezaAsync();

        await _baza.SnimiAsync(veza, Uzorak(), CancellationToken.None);

        var n = Assert.Single(
            await _baza.SviAsync(veza, samoAktivni: true, CancellationToken.None),
            x => x.ArtikalSifra == TestSifra);

        // Ako bi prvi recept važio tek od danas, utrošak za prošli mjesec bio bi nula
        // iako se artikal cijelo vrijeme prodavao.
        Assert.True(n.VaziOd.Year <= 2000,
            $"Prvi recept mora obuhvatiti raniju prodaju, a važi tek od {n.VaziOd}.");
    }

    [SkippableFact]
    public async Task Ponovni_unos_istog_dana_ne_pravi_novu_verziju()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");
        await using var veza = await VezaAsync();

        var prvi = await _baza.SnimiAsync(veza, Uzorak(0.25m), CancellationToken.None);
        var drugi = await _baza.SnimiAsync(veza, Uzorak(0.40m), CancellationToken.None);

        // Isti zapis se dopunjuje — inače bi zatvorena i nova verzija imale isti VaziOd.
        Assert.Equal(prvi, drugi);

        var svi = await _baza.SviAsync(veza, samoAktivni: false, CancellationToken.None);
        var zaTest = svi.Where(x => x.ArtikalSifra == TestSifra).ToList();
        Assert.Single(zaTest);
        Assert.Equal(0.40m, Assert.Single(zaTest[0].Stavke).Kolicina);
    }

    [SkippableFact]
    public async Task Arhiviranje_zatvara_recept_ali_ga_ne_brise()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");
        await using var veza = await VezaAsync();

        var id = await _baza.SnimiAsync(veza, Uzorak(), CancellationToken.None);
        await _baza.ZatvoriAsync(veza, id, CancellationToken.None);

        var aktivni = await _baza.SviAsync(veza, samoAktivni: true, CancellationToken.None);
        Assert.DoesNotContain(aktivni, x => x.ArtikalSifra == TestSifra);

        // Historija mora ostati — stari obračuni utroška se na nju oslanjaju.
        var svi = await _baza.SviAsync(veza, samoAktivni: false, CancellationToken.None);
        var arhivirani = Assert.Single(svi, x => x.ArtikalSifra == TestSifra);
        Assert.NotNull(arhivirani.VaziDo);
    }

    [SkippableFact]
    public async Task Normativ_bez_stavki_se_odbija()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");
        await using var veza = await VezaAsync();

        var prazan = Uzorak() with { Stavke = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _baza.SnimiAsync(veza, prazan, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Priprema_baze_se_smije_pokrenuti_vise_puta()
    {
        Skip.IfNot(Dostupno(), "SQL Server nije dostupan.");

        // Most je zove pri svakom pokretanju; ponovno izvršavanje ne smije pući.
        await using var veza = new SqlConnection(Veza);
        await veza.OpenAsync();
        await _baza.PripremiAsync(veza, CancellationToken.None);
        await _baza.PripremiAsync(veza, CancellationToken.None);
    }
}
