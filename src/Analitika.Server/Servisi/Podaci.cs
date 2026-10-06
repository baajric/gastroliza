using System.Text.Json;
using Analitika.Server.Mostovi;
using Analitika.Shared.Upiti;

namespace Analitika.Server.Servisi;

/// <summary>
/// Sve stranice traže podatke odavde. Nigdje se ne zapisuju — samo prolaze
/// kroz memoriju servera do browsera.
/// </summary>
public sealed class Podaci(MostKlijent most, StanjeAplikacije stanje, PostavkeRestorana postavke)
{
    private Guid Restoran => stanje.Restoran?.Id
        ?? throw new InvalidOperationException("Restoran nije izabran.");

    /// <summary>
    /// Baze izabrane u postavkama; ako korisnik još nije birao, most sam uzima najnoviju.
    /// </summary>
    private List<string> Baze => stanje.Baze;

    public bool NaVezi => stanje.Restoran is not null && most.JeNaVezi(stanje.Restoran.Id);

    public Task<List<BazaInfo>> BazeAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<BazaInfo>>(Restoran, new ZahtjevUpita { Upit = ImeUpita.PopisBaza }, ct);

    public Task<Sazetak> SazetakAsync(CancellationToken ct = default) =>
        Pitaj<Sazetak>(ImeUpita.Sazetak, stanje.Od, stanje.Do, ct);

    /// <summary>Isti upit nad prethodnim periodom jednake dužine — za prikaz rasta/pada.</summary>
    public Task<Sazetak> SazetakPrethodniAsync(CancellationToken ct = default)
    {
        var (od, doDat) = stanje.PrethodniPeriod();
        return Pitaj<Sazetak>(ImeUpita.Sazetak, od, doDat, ct);
    }

    public Task<List<DanPrometa>> PoDanimaAsync(CancellationToken ct = default) =>
        Pitaj<List<DanPrometa>>(ImeUpita.PrometPoDanima, stanje.Od, stanje.Do, ct);

    public Task<List<SatPrometa>> PoSatimaAsync(CancellationToken ct = default) =>
        Pitaj<List<SatPrometa>>(ImeUpita.PrometPoSatima, stanje.Od, stanje.Do, ct);

    public Task<List<PoljeGuzve>> GuzvaAsync(CancellationToken ct = default) =>
        Pitaj<List<PoljeGuzve>>(ImeUpita.PrometPoDanuISatu, stanje.Od, stanje.Do, ct);

    /// <summary>Isti dnevni presjek za prethodni period — sloj poređenja na grafu.</summary>
    public Task<List<DanPrometa>> PoDanimaPrethodniAsync(CancellationToken ct = default)
    {
        var (od, doDat) = stanje.PrethodniPeriod();
        return Pitaj<List<DanPrometa>>(ImeUpita.PrometPoDanima, od, doDat, ct);
    }

    public Task<List<RedGrupe>> PoGrupamaAsync(CancellationToken ct = default) =>
        Pitaj<List<RedGrupe>>(ImeUpita.PrometPoGrupama, stanje.Od, stanje.Do, ct);

    /// <summary>Rang lista konobara, sa prikaznim imenima iz postavki umjesto imena iz kase.</summary>
    public async Task<List<RedKonobara>> KonobariAsync(CancellationToken ct = default)
    {
        var konobari = await Pitaj<List<RedKonobara>>(ImeUpita.RangKonobara, stanje.Od, stanje.Do, ct);
        var p = await postavke.UcitajAsync(Restoran, ct: ct);
        return PostavkeRestorana.Preimenuj(konobari, p);
    }

    /// <summary>Sve kartice konobara iz kase i iz uvezenih godina.</summary>
    public Task<List<KonobarKase>> KonobariKaseAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<KonobarKase>>(Restoran, new ZahtjevUpita { Upit = ImeUpita.KonobariKase }, ct);

    // ------------------------------------------------------------------
    // Godine podataka i uvoz prošlih godina
    // ------------------------------------------------------------------

    public Task<List<GodinaPodataka>> GodineAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<GodinaPodataka>>(Restoran, new ZahtjevUpita { Upit = ImeUpita.GodinePodataka }, ct);

    /// <summary>Otvara uvoz; ime fajla putuje kao obični tekst, samo radi prikaza.</summary>
    public Task<Guid> UvozPocniAsync(int godina, string nazivFajla, CancellationToken ct = default) =>
        most.PitajAsync<Guid>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.ArhivaUvozPocni, Godina = godina, TeretJson = nazivFajla
        }, ct);

    public Task<int> UvozDioAsync(Guid uvozId, IReadOnlyList<StavkaArhive> stavke, CancellationToken ct = default) =>
        most.PitajAsync<int>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.ArhivaUvozDio, UvozId = uvozId, TeretJson = JsonSerializer.Serialize(stavke)
        }, ct);

    public Task<GodinaPodataka> UvozZavrsiAsync(Guid uvozId, CancellationToken ct = default) =>
        most.PitajAsync<GodinaPodataka>(Restoran, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozZavrsi, UvozId = uvozId }, ct);

    public Task<bool> UvozOdustaniAsync(Guid uvozId, CancellationToken ct = default) =>
        most.PitajAsync<bool>(Restoran, new ZahtjevUpita { Upit = ImeUpita.ArhivaUvozOdustani, UvozId = uvozId }, ct);

    public Task<bool> ObrisiGodinuAsync(int godina, CancellationToken ct = default) =>
        most.PitajAsync<bool>(Restoran, new ZahtjevUpita { Upit = ImeUpita.ArhivaObrisi, Godina = godina }, ct);

    public Task<List<RedArtikla>> ArtikliAsync(int vrh, string? grupa, CancellationToken ct = default) =>
        most.PitajAsync<List<RedArtikla>>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.RangArtikala,
            OdDatuma = stanje.Od,
            DoDatuma = stanje.Do,
            Baze = Baze,
            Vrh = vrh,
            Grupa = grupa
        }, ct);

    /// <summary>Rang artikala za prethodni period — da se vidi ko je skočio, a ko pao.</summary>
    public Task<List<RedArtikla>> ArtikliPrethodniAsync(int vrh, string? grupa, CancellationToken ct = default)
    {
        var (od, doDat) = stanje.PrethodniPeriod();
        return most.PitajAsync<List<RedArtikla>>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.RangArtikala,
            OdDatuma = od,
            DoDatuma = doDat,
            Baze = Baze,
            Vrh = vrh,
            Grupa = grupa
        }, ct);
    }

    // ------------------------------------------------------------------
    // Normativi — jedino mjesto gdje aplikacija piše, i to u lokalnu bazu klijenta.
    // ------------------------------------------------------------------

    public Task<List<Normativ>> NormativiAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<Normativ>>(Restoran,
            new ZahtjevUpita { Upit = ImeUpita.NormativiSvi, Baze = Baze }, ct);

    public Task<int> SnimiNormativAsync(Normativ normativ, CancellationToken ct = default) =>
        most.PitajAsync<int>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.NormativSnimi,
            Baze = Baze,
            TeretJson = JsonSerializer.Serialize(normativ)
        }, ct);

    public Task<bool> ZatvoriNormativAsync(int id, CancellationToken ct = default) =>
        most.PitajAsync<bool>(Restoran,
            new ZahtjevUpita { Upit = ImeUpita.NormativZatvori, Baze = Baze, Id = id }, ct);

    public Task<int> UveziNormativeAsync(CancellationToken ct = default) =>
        most.PitajAsync<int>(Restoran,
            new ZahtjevUpita { Upit = ImeUpita.UvoziPostojeceNormative, Baze = Baze }, ct);

    public Task<List<ArtikalBezNormativa>> BezNormativaAsync(int vrh, CancellationToken ct = default) =>
        most.PitajAsync<List<ArtikalBezNormativa>>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.ArtikliBezNormativa,
            OdDatuma = stanje.Od,
            DoDatuma = stanje.Do,
            Baze = Baze,
            Vrh = vrh
        }, ct);

    // ------------------------------------------------------------------
    // Utrošak i projekcije
    // ------------------------------------------------------------------

    public Task<List<UtrosakRedak>> UtrosakAsync(CancellationToken ct = default) =>
        Pitaj<List<UtrosakRedak>>(ImeUpita.UtrosakRepromaterijala, stanje.Od, stanje.Do, ct);

    public Task<List<UtrosakArtikla>> UtrosakPoArtikluAsync(double barkod, CancellationToken ct = default) =>
        most.PitajAsync<List<UtrosakArtikla>>(Restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.UtrosakPoArtiklu,
            OdDatuma = stanje.Od,
            DoDatuma = stanje.Do,
            Baze = Baze,
            Barkod = barkod
        }, ct);

    /// <summary>Dnevna prodaja za zadani raspon — osnovica projekcije, ne ovisi o izabranom periodu.</summary>
    public Task<List<DnevnaProdaja>> DnevnaProdajaAsync(DateOnly od, DateOnly doDatuma, CancellationToken ct = default) =>
        Pitaj<List<DnevnaProdaja>>(ImeUpita.DnevnaProdajaSaNormativom, od, doDatuma, ct);

    public Task<List<Artikal>> SifarnikArtikalaAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<Artikal>>(Restoran,
            new ZahtjevUpita { Upit = ImeUpita.Artikli, Baze = Baze }, ct);

    public Task<List<Repromaterijal>> SifarnikRepromaterijalaAsync(CancellationToken ct = default) =>
        most.PitajAsync<List<Repromaterijal>>(Restoran,
            new ZahtjevUpita { Upit = ImeUpita.Repromaterijali, Baze = Baze }, ct);

    private Task<T> Pitaj<T>(ImeUpita upit, DateOnly od, DateOnly doDat, CancellationToken ct) =>
        most.PitajAsync<T>(Restoran, new ZahtjevUpita
        {
            Upit = upit,
            OdDatuma = od,
            DoDatuma = doDat,
            Baze = Baze
        }, ct);
}
