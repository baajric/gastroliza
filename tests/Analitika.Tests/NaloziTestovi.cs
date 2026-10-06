using Analitika.Server.Mostovi;
using Analitika.Server.Nalozi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Analitika.Tests;

/// <summary>Nalozi i uparivanje mostova nad privremenom SQLite bazom.</summary>
public sealed class NaloziTestovi : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gastroliza-test-" + Guid.NewGuid().ToString("N"));
    private readonly ServerBaza _baza;

    public NaloziTestovi()
    {
        Directory.CreateDirectory(_folder);
        var konfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Server"] = $"Data Source={Path.Combine(_folder, "test.db")};Pooling=False"
            })
            .Build();
        _baza = new ServerBaza(konfig, new Okruzenje(_folder));
        _baza.Pripremi();
    }

    [Fact]
    public void Registracija_i_prijava()
    {
        var r = _baza.Registruj("vlasnik@restoran.ba", "Lejla", "dovoljno-duga-lozinka", "Kafana Most");
        Assert.NotNull(r);

        Assert.Null(_baza.Registruj("VLASNIK@restoran.ba", "Neko", "druga-lozinka-123", "Druga"));
        Assert.Null(_baza.Prijavi("vlasnik@restoran.ba", "pogresna-lozinka"));
        Assert.Null(_baza.Prijavi("nepostoji@restoran.ba", "dovoljno-duga-lozinka"));

        var k = _baza.Prijavi("Vlasnik@Restoran.ba", "dovoljno-duga-lozinka");
        Assert.NotNull(k);
        var restoran = Assert.Single(_baza.RestoraniKorisnika(k!.Id));
        Assert.Equal("Kafana Most", restoran.Naziv);
        Assert.True(_baza.JeVlasnik(k.Id, restoran.Id));
    }

    [Fact]
    public void Kod_se_mijenja_za_kljuc_samo_jednom()
    {
        var (_, restoran) = _baza.Registruj("a@b.ba", "A", "lozinka-lozinka", "Restoran A")!.Value;
        var (kod, istice) = _baza.NoviKod(restoran.Id);

        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", kod);
        Assert.True(istice > DateTime.UtcNow.AddMinutes(29));

        // Kod prepisan rukom — mala slova, bez crtice — i dalje važi.
        var upareno = _baza.Upari(kod.Replace("-", " ").ToLowerInvariant(), "KASA-PC");
        Assert.NotNull(upareno);
        Assert.Equal(restoran.Id, upareno!.Value.Restoran.Id);
        Assert.True(upareno.Value.Kljuc.Length >= 40);

        Assert.Null(_baza.Upari(kod, "KASA-PC"));

        // Server prepoznaje most po ključu, a čuva samo njegov hash.
        Assert.Equal(restoran.Id, _baza.RestoranPoHashuKljuca(KonfigKljucStore.Hash(upareno.Value.Kljuc))?.Id);
        Assert.Equal("KASA-PC", _baza.Uparenost(restoran.Id).Racunar);
    }

    [Fact]
    public void Novi_kod_ponistava_stari_i_novo_uparivanje_mijenja_kljuc()
    {
        var (_, restoran) = _baza.Registruj("c@d.ba", "C", "lozinka-lozinka", "Restoran C")!.Value;
        var (stari, _) = _baza.NoviKod(restoran.Id);
        var (novi, _) = _baza.NoviKod(restoran.Id);
        Assert.Null(_baza.Upari(stari, null));

        var prvi = _baza.Upari(novi, null)!.Value.Kljuc;
        var drugi = _baza.Upari(_baza.NoviKod(restoran.Id).Kod, null)!.Value.Kljuc;

        Assert.Null(_baza.RestoranPoHashuKljuca(KonfigKljucStore.Hash(prvi)));
        Assert.NotNull(_baza.RestoranPoHashuKljuca(KonfigKljucStore.Hash(drugi)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { /* privremeni fajl */ }
    }

    private sealed class Okruzenje(string korijen) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = korijen;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = korijen;
        public string EnvironmentName { get; set; } = "Test";
    }
}
