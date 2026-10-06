using System.Text.Json;
using Analitika.Agent.Queries;
using Analitika.Shared.Upiti;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Analitika.Tests;

/// <summary>
/// Uvoz prošle godine kroz cijeli tok mosta, protiv lokalnog SQL Servera.
/// Koristi 2019. — godinu za koju u kasi nema baze — i na kraju je briše.
/// </summary>
public class ArhivaUvozTestovi
{
    private const string Veza =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    private const int Godina = 2019;

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

    private static async Task<T> IzvrsiAsync<T>(Izvrsilac i, ZahtjevUpita z)
    {
        var odgovor = await i.IzvrsiAsync(z, CancellationToken.None);
        Assert.True(odgovor.Uspjeh, odgovor.Greska);
        return JsonSerializer.Deserialize<T>(odgovor.PodaciJson!)!;
    }

    [SkippableFact]
    public async Task Uvezena_godina_ulazi_u_izvjestaje_i_moze_se_obrisati()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");
        var i = Napravi();

        var stavke = new List<StavkaArhive>
        {
            new(new DateOnly(Godina, 3, 1), "000001", 1001, "Espresso", "Pice", "T15", "Test Amar", 2, 2.5m, 5_220_000),
            new(new DateOnly(Godina, 3, 1), "000001", 2040, "Pizza", "Hrana", "T15", "Test Amar", 1, 9m, 5_220_000),
            new(new DateOnly(Godina, 3, 2), "000002", 1001, "Espresso", "Pice", "T16", null, 1, 2.5m, null)
        };

        var id = await IzvrsiAsync<Guid>(i, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozPocni, Godina = Godina, TeretJson = "test.csv" });
        try
        {
            Assert.Equal(3, await IzvrsiAsync<int>(i, new ZahtjevUpita
            {
                Upit = ImeUpita.ArhivaUvozDio, UvozId = id, TeretJson = JsonSerializer.Serialize(stavke)
            }));
            var gotovo = await IzvrsiAsync<GodinaPodataka>(i, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozZavrsi, UvozId = id });
            Assert.Equal(3, gotovo.BrojStavki);

            // Bez navedenih baza most sam bira izvor po godini — za 2019. to je uvoz.
            var sazetak = await IzvrsiAsync<Sazetak>(i, new ZahtjevUpita
            {
                Upit = ImeUpita.Sazetak, OdDatuma = new DateOnly(Godina, 1, 1), DoDatuma = new DateOnly(Godina, 12, 31)
            });
            Assert.Equal(16.5m, sazetak.Promet);
            Assert.Equal(2, sazetak.BrojRacuna);

            // Ime konobara dolazi iz stupca KONOBAR jer Glopos za 2019. ne postoji.
            var konobari = await IzvrsiAsync<List<RedKonobara>>(i, new ZahtjevUpita
            {
                Upit = ImeUpita.RangKonobara, OdDatuma = new DateOnly(Godina, 1, 1), DoDatuma = new DateOnly(Godina, 12, 31)
            });
            Assert.Equal("Test Amar", konobari.Single(k => k.IdKartice == "T15").Ime);
            Assert.Equal("T16", konobari.Single(k => k.IdKartice == "T16").Ime);

            var godine = await IzvrsiAsync<List<GodinaPodataka>>(i, new ZahtjevUpita { Upit = ImeUpita.GodinePodataka });
            Assert.Equal(IzvorGodine.Uvezeno, godine.Single(g => g.Godina == Godina).Izvor);
            Assert.Equal(IzvorGodine.Uzivo, godine.Single(g => g.Godina == DateTime.Today.Year).Izvor);
        }
        finally
        {
            await IzvrsiAsync<bool>(i, new ZahtjevUpita { Upit = ImeUpita.ArhivaObrisi, Godina = Godina });
        }

        var poslije = await IzvrsiAsync<List<GodinaPodataka>>(i, new ZahtjevUpita { Upit = ImeUpita.GodinePodataka });
        Assert.DoesNotContain(poslije, g => g.Godina == Godina && g.Izvor == IzvorGodine.Uvezeno);
    }

    /// <summary>Svaka godina koju kasa ima — ovdje 2026. — zaključana je za uvoz.</summary>
    [SkippableFact]
    public async Task Godina_koju_kasa_ima_se_ne_moze_uvesti()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");

        var odgovor = await Napravi().IzvrsiAsync(new ZahtjevUpita
        {
            Upit = ImeUpita.ArhivaUvozPocni, Godina = DateTime.Today.Year, TeretJson = "x.csv"
        }, CancellationToken.None);

        Assert.False(odgovor.Uspjeh);
        Assert.Contains("iz kase", odgovor.Greska);
    }

    [SkippableFact]
    public async Task Paket_iz_druge_godine_se_odbija()
    {
        Skip.IfNot(BazaDostupna(), "Lokalna POS baza nije dostupna.");
        var i = Napravi();

        var id = await IzvrsiAsync<Guid>(i, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozPocni, Godina = Godina, TeretJson = "x.csv" });
        try
        {
            var odgovor = await i.IzvrsiAsync(new ZahtjevUpita
            {
                Upit = ImeUpita.ArhivaUvozDio, UvozId = id,
                TeretJson = JsonSerializer.Serialize(new[] { new StavkaArhive(new DateOnly(2026, 1, 5), null, 1, "X", null, null, null, 1, 1, null) })
            }, CancellationToken.None);
            Assert.False(odgovor.Uspjeh);
        }
        finally
        {
            await IzvrsiAsync<bool>(i, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozOdustani, UvozId = id });
        }
    }
}

/// <summary>Pravilo „godina je u kasi" — bez baze, nad zadanim popisom godina po bazama.</summary>
public class PokrivenostTestovi
{
    private static readonly string[] Baze = ["EtisRpos_2026"];

    private static Izvrsilac.GodinaBaze Red(int godina, long broj) =>
        new("EtisRpos_2026", godina, broj, new DateOnly(godina, 12, 1), new DateOnly(godina, 12, 31));

    [Fact]
    public void Godina_iz_tudje_baze_se_zakljucava_kad_ima_dovoljno_stavki()
    {
        var pokrivenost = new List<Izvrsilac.GodinaBaze> { Red(2026, 270_000), Red(2025, 300) };
        Assert.Equal(["EtisRpos_2026"], Izvrsilac.BazeZaGodinu(pokrivenost, Baze, 2025));
    }

    [Fact]
    public void Zalutale_stavke_ne_zakljucavaju_godinu()
    {
        var pokrivenost = new List<Izvrsilac.GodinaBaze> { Red(2026, 270_000), Red(2019, 10) };
        Assert.Empty(Izvrsilac.BazeZaGodinu(pokrivenost, Baze, 2019));
    }

    [Fact]
    public void Baza_sa_imenom_godine_je_uvijek_izvor_te_godine()
    {
        Assert.Equal(["EtisRpos_2026"], Izvrsilac.BazeZaGodinu([], Baze, 2026));
    }
}
