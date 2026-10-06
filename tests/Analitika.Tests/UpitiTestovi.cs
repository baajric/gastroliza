using Analitika.Agent.Queries;
using Analitika.Shared.Upiti;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Analitika.Tests;

/// <summary>
/// Testovi rade protiv stvarne lokalne POS baze. Očekivane vrijednosti su izmjerene
/// nezavisno preko sqlcmd-a, pa hvataju svaku promjenu logike upita.
/// Ako baza nije dostupna, testovi se preskaču umjesto da padnu.
/// </summary>

public class UpitiTestovi
{
    private const string Veza =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    private static readonly DateOnly JuliOd = new(2026, 7, 1);
    private static readonly DateOnly JuliDo = new(2026, 7, 31);
    private static readonly string[] Baza2026 = ["EtisRpos_2026"];

    private static Izvrsilac Napravi()
    {
        var konfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PosBaza"] = Veza })
            .Build();
        return new Izvrsilac(konfig, new LokalnaBaza(NullLogger<LokalnaBaza>.Instance), NullLogger<Izvrsilac>.Instance);
    }

    private static bool BazaDostupna()
    {
        try
        {
            using var v = new SqlConnection(Veza);
            v.Open();
            using var k = v.CreateCommand();
            k.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = 'EtisRpos_2026' AND state = 0";
            return (int)k.ExecuteScalar()! == 1;
        }
        catch { return false; }
    }

    private static async Task<T> IzvrsiAsync<T>(ZahtjevUpita z)
    {
        var odgovor = await Napravi().IzvrsiAsync(z, CancellationToken.None);
        Assert.True(odgovor.Uspjeh, odgovor.Greska);
        return System.Text.Json.JsonSerializer.Deserialize<T>(odgovor.PodaciJson!)!;
    }

    [SkippableFact]
    public async Task Popis_baza_nalazi_EtisRpos_2026()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var baze = await IzvrsiAsync<List<BazaInfo>>(new ZahtjevUpita { Upit = ImeUpita.PopisBaza });

        var baza = Assert.Single(baze, b => b.Naziv == "EtisRpos_2026");
        Assert.Equal(2026, baza.Godina);
        Assert.Equal(new DateOnly(2026, 1, 2), baza.PrviDatum);
    }

    [SkippableFact]
    public async Task Sazetak_za_juli_daje_izmjerene_vrijednosti()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var s = await IzvrsiAsync<Sazetak>(new ZahtjevUpita
        {
            Upit = ImeUpita.Sazetak, OdDatuma = JuliOd, DoDatuma = JuliDo, Baze = Baza2026
        });

        // Provjereno u SSMS-u: SUM(IZLAZ*MPC) za juli 2026 = 177.143,00
        Assert.Equal(177143.00m, s.Promet);
        Assert.Equal(3607, s.BrojRacuna);
        Assert.Equal(27, s.RadnihDana);
        Assert.Equal(48746.000m, s.BrojArtikala);
    }

    [SkippableFact]
    public async Task Promet_se_ne_uzima_iz_zaglavlja()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var s = await IzvrsiAsync<Sazetak>(new ZahtjevUpita
        {
            Upit = ImeUpita.Sazetak, OdDatuma = new DateOnly(2026, 1, 1), DoDatuma = new DateOnly(2026, 12, 31), Baze = Baza2026
        });

        // POS_Zaglavlja.IZNOS zbraja 506.534,50 jer je 0.00 na 4.403 računa.
        // Ako upit ikad počne čitati zaglavlja, ovaj test pada.
        Assert.True(s.Promet > 900_000m, $"Promet {s.Promet} izgleda kao da dolazi iz POS_Zaglavlja.");
    }

    [SkippableFact]
    public async Task Rang_konobara_razrjesava_imena_iz_Glopos()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var rang = await IzvrsiAsync<List<RedKonobara>>(new ZahtjevUpita
        {
            Upit = ImeUpita.RangKonobara, OdDatuma = JuliOd, DoDatuma = JuliDo, Baze = Baza2026
        });

        var prvi = rang[0];
        Assert.Equal("0001234944", prvi.IdKartice);
        Assert.Equal("Konobar4", prvi.Ime);
        Assert.Equal(24394.00m, prvi.Promet);
        Assert.Equal(6597.000m, prvi.BrojArtikala);

        // Nijedan red ne smije ostati bez imena.
        Assert.All(rang, k => Assert.False(string.IsNullOrWhiteSpace(k.Ime)));
    }

    [SkippableFact]
    public async Task Promet_po_satima_tumaci_VRIJEME_kao_stotinke()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var sati = await IzvrsiAsync<List<SatPrometa>>(new ZahtjevUpita
        {
            Upit = ImeUpita.PrometPoSatima, OdDatuma = JuliOd, DoDatuma = JuliDo, Baze = Baza2026
        });

        Assert.All(sati, s => Assert.InRange(s.Sat, 0, 23));
        // Dva vrha: ručak oko 13h i večer oko 21h.
        Assert.Equal(21, sati.OrderByDescending(s => s.Promet).First().Sat);
    }

    [SkippableFact]
    public async Task Rang_artikala_postuje_ogranicenje_i_filter_grupe()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var hrana = await IzvrsiAsync<List<RedArtikla>>(new ZahtjevUpita
        {
            Upit = ImeUpita.RangArtikala, OdDatuma = JuliOd, DoDatuma = JuliDo,
            Baze = Baza2026, Vrh = 10, Grupa = "Hrana"
        });

        Assert.Equal(10, hrana.Count);
        Assert.All(hrana, a => Assert.Equal("Hrana", a.Grupa));
        // Sortirano opadajuće po količini.
        Assert.Equal(hrana.OrderByDescending(a => a.Kolicina).Select(a => a.Sifra), hrana.Select(a => a.Sifra));
    }

    [Theory]
    [InlineData("EtisRpos_2026", true)]
    [InlineData("EtisRpos_1999", true)]
    [InlineData("master", false)]
    [InlineData("EtisRpos_2026; DROP DATABASE master --", false)]
    [InlineData("EtisRpos_20261", false)]
    [InlineData("[EtisRpos_2026]", false)]
    public void Naziv_baze_prolazi_samo_ocekivani_oblik(string naziv, bool ocekivano)
        => Assert.Equal(ocekivano, SqlKatalog.NazivBazeJeIspravan(naziv));

    [SkippableFact]
    public async Task Nepostojeca_baza_se_odbija()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var odgovor = await Napravi().IzvrsiAsync(new ZahtjevUpita
        {
            Upit = ImeUpita.Sazetak, OdDatuma = JuliOd, DoDatuma = JuliDo, Baze = ["EtisRpos_1999"]
        }, CancellationToken.None);

        Assert.False(odgovor.Uspjeh);
        Assert.Contains("ne postoji", odgovor.Greska!, StringComparison.OrdinalIgnoreCase);
    }
}
