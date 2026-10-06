using System.Diagnostics;
using System.Text.Json;
using Analitika.Shared.Upiti;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Analitika.Agent.Queries;

/// <summary>
/// Izvršava upite s bijele liste nad lokalnim SQL Serverom i vraća samo agregirani rezultat.
/// Nikad ne prima ni ne izvršava SQL primljen sa servera.
/// </summary>
public sealed class Izvrsilac(IConfiguration konfig, LokalnaBaza lokalna, ILogger<Izvrsilac> log)
{
    /// <summary>Gornja granica za rang liste — štiti kasu i od greške na serveru.</summary>
    private const int MaxRedova = 500;

    /// <summary>
    /// Upit koji traje duže od ovoga se prekida da ne opterećuje kasu u špici.
    ///
    /// Šezdeset, a ne trideset: prvi upit poslije paljenja računara gradi plan
    /// izvršenja na hladnom kešu i zna potrajati desetak sekundi duže nego inače.
    /// Sa trideset je prvo otvaranje aplikacije nakon restarta znalo puknuti.
    /// </summary>
    private const int IstekSekundi = 60;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    private string VeznaNiska =>
        konfig.GetConnectionString("PosBaza")
        ?? throw new InvalidOperationException("Nedostaje ConnectionStrings:PosBaza u konfiguraciji.");

    private SqlConnection Veza() => new(VeznaNiska);

    public async Task<OdgovorUpita> IzvrsiAsync(ZahtjevUpita zahtjev, CancellationToken ct)
    {
        var sat = Stopwatch.StartNew();
        try
        {
            var podaci = await PokreniAsync(zahtjev, ct);
            return new OdgovorUpita
            {
                Uspjeh = true,
                PodaciJson = JsonSerializer.Serialize(podaci, Json),
                TrajanjeMs = (int)sat.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Upit {Upit} nije uspio", zahtjev.Upit);
            return OdgovorUpita.Greskom(ex.Message) with { TrajanjeMs = (int)sat.ElapsedMilliseconds };
        }
    }

    private async Task<object> PokreniAsync(ZahtjevUpita z, CancellationToken ct)
    {
        await using var veza = Veza();
        await veza.OpenAsync(ct);

        if (z.Upit == ImeUpita.PopisBaza)
            return await PopisBazaAsync(veza, ct);

        var izvori = await IzvoriAsync(veza, z, ct);
        var najnovija = izvori.Sifarnik;

        // Naredbe nad lokalnom bazom Analitika — jedino gdje most piše.
        switch (z.Upit)
        {
            case ImeUpita.NormativiSvi:
                await lokalna.PripremiAsync(veza, ct);
                return await lokalna.SviAsync(veza, samoAktivni: true, ct);

            case ImeUpita.NormativSnimi:
            {
                await lokalna.PripremiAsync(veza, ct);
                var normativ = JsonSerializer.Deserialize<Normativ>(z.TeretJson ?? "")
                    ?? throw new InvalidOperationException("Nedostaje sadržaj normativa.");
                return await lokalna.SnimiAsync(veza, normativ, ct);
            }

            case ImeUpita.NormativZatvori:
                await lokalna.PripremiAsync(veza, ct);
                await lokalna.ZatvoriAsync(veza, z.Id ?? throw new InvalidOperationException("Nedostaje Id."), ct);
                return true;

            case ImeUpita.UvoziPostojeceNormative:
                await lokalna.PripremiAsync(veza, ct);
                return await lokalna.UveziAsync(veza, najnovija, ct);

            case ImeUpita.PostavkeUcitaj:
            {
                await lokalna.PripremiAsync(veza, ct);
                var json = await lokalna.PostavkaAsync(veza, "Postavke", ct);
                return json is null ? new Postavke() : JsonSerializer.Deserialize<Postavke>(json) ?? new Postavke();
            }

            case ImeUpita.PostavkeSnimi:
                await lokalna.PripremiAsync(veza, ct);
                await lokalna.SnimiPostavkuAsync(veza, "Postavke", z.TeretJson, ct);
                return true;

            case ImeUpita.KonobariKase:
            {
                var imaArhivu = (await LokalnaBaza.UvezeneGodineAsync(veza, ct)).Count > 0;
                var kolacija = imaArhivu ? await KolacijaAsync(veza, najnovija, ct) : null;
                return await UpitAsync<KonobarKase>(veza, SqlKatalog.KonobariKase(najnovija, imaArhivu, kolacija), null, ct);
            }

            case ImeUpita.GodinePodataka:
                return await GodineAsync(veza, ct);

            case ImeUpita.ArhivaUvozPocni:
            {
                var godina = z.Godina ?? throw new InvalidOperationException("Nedostaje godina.");
                await ProvjeriDaGodinaNijeUKasiAsync(veza, godina, ct);
                await lokalna.PripremiAsync(veza, ct);
                return await lokalna.UvozPocniAsync(veza, godina, z.TeretJson, ct);
            }

            case ImeUpita.ArhivaUvozDio:
            {
                await lokalna.PripremiAsync(veza, ct);
                var stavke = JsonSerializer.Deserialize<List<StavkaArhive>>(z.TeretJson ?? "[]") ?? [];
                return await lokalna.UvozDioAsync(veza, UvozId(z), stavke, ct);
            }

            case ImeUpita.ArhivaUvozZavrsi:
            {
                await lokalna.PripremiAsync(veza, ct);
                var u = await lokalna.UvozZavrsiAsync(veza, UvozId(z), ct);
                return Uvezena(u);
            }

            case ImeUpita.ArhivaUvozOdustani:
                await lokalna.PripremiAsync(veza, ct);
                await lokalna.UvozOdustaniAsync(veza, UvozId(z), ct);
                return true;

            case ImeUpita.ArhivaObrisi:
                await lokalna.PripremiAsync(veza, ct);
                await lokalna.ObrisiGodinuAsync(veza,
                    z.Godina ?? throw new InvalidOperationException("Nedostaje godina."), ct);
                return true;
        }

        // Šifarnici i zalihe ne zavise od perioda — čitaju se iz najnovije baze.
        switch (z.Upit)
        {
            case ImeUpita.Artikli:
                return await UpitAsync<Artikal>(veza, SqlKatalog.Artikli(najnovija), null, ct);
            case ImeUpita.Repromaterijali:
                return await UpitAsync<Repromaterijal>(veza, SqlKatalog.Repromaterijali(najnovija), null, ct);
            case ImeUpita.StanjeRepromaterijala:
                return await UpitAsync<StanjeRepro>(veza, SqlKatalog.StanjeRepromaterijala(najnovija), null, ct);
        }

        var (od, doDat) = Period(z);
        var par = new DynamicParameters();
        par.Add("od", od, System.Data.DbType.Date);
        par.Add("do", doDat, System.Data.DbType.Date);

        switch (z.Upit)
        {
            case ImeUpita.Sazetak:
                var redovi = await UpitAsync<SazetakRed>(veza, SqlKatalog.Sazetak(izvori), par, ct);
                var r = redovi.FirstOrDefault() ?? new SazetakRed();
                return new Sazetak
                {
                    Promet = r.Promet,
                    BrojRacuna = r.BrojRacuna,
                    BrojArtikala = r.BrojArtikala,
                    RadnihDana = r.RadnihDana
                };

            case ImeUpita.PrometPoDanima:
                return (await UpitAsync<DanRed>(veza, SqlKatalog.PrometPoDanima(izvori), par, ct))
                    .Select(x => new DanPrometa(DateOnly.FromDateTime(x.Datum), x.Promet, x.BrojRacuna))
                    .ToList();

            case ImeUpita.PrometPoSatima:
                return await UpitAsync<SatPrometa>(veza, SqlKatalog.PrometPoSatima(izvori), par, ct);

            case ImeUpita.PrometPoDanuISatu:
                return await UpitAsync<PoljeGuzve>(veza, SqlKatalog.PrometPoDanuISatu(izvori), par, ct);

            case ImeUpita.RangArtikala:
                var imaGrupu = !string.IsNullOrWhiteSpace(z.Grupa);
                par.Add("vrh", Math.Clamp(z.Vrh ?? 50, 1, MaxRedova));
                if (imaGrupu) par.Add("grupa", z.Grupa!.Trim());
                return await UpitAsync<RedArtikla>(veza, SqlKatalog.RangArtikala(izvori, imaGrupu), par, ct);

            case ImeUpita.RangKonobara:
                return await UpitAsync<RedKonobara>(veza, SqlKatalog.RangKonobara(izvori), par, ct);

            case ImeUpita.PrometPoGrupama:
                return await UpitAsync<RedGrupe>(veza, SqlKatalog.PrometPoGrupama(izvori), par, ct);

            case ImeUpita.ProdajaPoArtiklu:
                return await UpitAsync<ProdajaArtikla>(veza, SqlKatalog.ProdajaPoArtiklu(izvori), par, ct);

            case ImeUpita.UtrosakRepromaterijala:
                await lokalna.PripremiAsync(veza, ct);
                return await UpitAsync<UtrosakRedak>(veza, SqlKatalog.UtrosakRepromaterijala(izvori), par, ct);

            case ImeUpita.UtrosakPoArtiklu:
                await lokalna.PripremiAsync(veza, ct);
                par.Add("barkod", z.Barkod ?? throw new InvalidOperationException("Nedostaje barkod."));
                return await UpitAsync<UtrosakArtikla>(veza, SqlKatalog.UtrosakPoArtiklu(izvori), par, ct);

            case ImeUpita.DnevnaProdajaSaNormativom:
                await lokalna.PripremiAsync(veza, ct);
                return (await UpitAsync<DnevnaProdajaRed>(veza, SqlKatalog.DnevnaProdajaSaNormativom(izvori), par, ct))
                    .Select(x => new DnevnaProdaja(DateOnly.FromDateTime(x.Datum), x.Sifra, x.Kolicina))
                    .ToList();

            case ImeUpita.ArtikliBezNormativa:
                await lokalna.PripremiAsync(veza, ct);
                par.Add("vrh", Math.Clamp(z.Vrh ?? 50, 1, MaxRedova));
                return await UpitAsync<ArtikalBezNormativa>(
                    veza, SqlKatalog.ArtikliBezNormativa(izvori, najnovija), par, ct);

            case ImeUpita.DnevnaProdajaPoArtiklu:
                return (await UpitAsync<DnevnaProdajaRed>(veza, SqlKatalog.DnevnaProdajaPoArtiklu(izvori), par, ct))
                    .Select(x => new DnevnaProdaja(DateOnly.FromDateTime(x.Datum), x.Sifra, x.Kolicina))
                    .ToList();

            default:
                throw new InvalidOperationException($"Upit {z.Upit} nije podržan.");
        }
    }

    private static (DateTime Od, DateTime Do) Period(ZahtjevUpita z)
    {
        var danas = DateTime.Today;
        var od = z.OdDatuma?.ToDateTime(TimeOnly.MinValue) ?? danas.AddDays(-30);
        var doDat = z.DoDatuma?.ToDateTime(TimeOnly.MinValue) ?? danas;
        if (od > doDat) (od, doDat) = (doDat, od);
        return (od, doDat);
    }

    private static async Task<List<T>> UpitAsync<T>(SqlConnection veza, string sql, DynamicParameters? par, CancellationToken ct)
    {
        var naredba = new CommandDefinition(sql, par, commandTimeout: IstekSekundi, cancellationToken: ct);
        return (await veza.QueryAsync<T>(naredba)).ToList();
    }

    private async Task<List<BazaInfo>> PopisBazaAsync(SqlConnection veza, CancellationToken ct)
    {
        var nazivi = (await UpitAsync<string>(veza, SqlKatalog.PopisBaza, null, ct))
            .Where(SqlKatalog.NazivBazeJeIspravan)
            .ToList();

        var rezultat = new List<BazaInfo>();
        foreach (var naziv in nazivi)
        {
            try
            {
                var o = (await UpitAsync<OpsegRed>(veza, SqlKatalog.OpsegBaze(naziv), null, ct)).FirstOrDefault();
                rezultat.Add(new BazaInfo(
                    naziv,
                    int.TryParse(naziv[^4..], out var g) ? g : null,
                    o?.PrviDatum is { } p ? DateOnly.FromDateTime(p) : null,
                    o?.ZadnjiDatum is { } z ? DateOnly.FromDateTime(z) : null,
                    (int)Math.Min(o?.BrojStavki ?? 0, int.MaxValue)));
            }
            catch (SqlException ex)
            {
                // Stara baza može biti bez POS_Stavke ili nedostupna — preskačemo je,
                // ali je i dalje prijavljujemo da korisnik vidi da postoji.
                log.LogWarning(ex, "Baza {Baza} se ne može očitati", naziv);
                rezultat.Add(new BazaInfo(naziv, null, null, null, 0));
            }
        }
        return rezultat;
    }

    private static Guid UvozId(ZahtjevUpita z) =>
        z.UvozId ?? throw new InvalidOperationException("Nedostaje oznaka uvoza.");

    private static GodinaPodataka Uvezena(LokalnaBaza.UvezenaGodina u) => new()
    {
        Godina = u.Godina,
        Izvor = IzvorGodine.Uvezeno,
        PrviDatum = u.PrviDatum is { } p ? DateOnly.FromDateTime(p) : null,
        ZadnjiDatum = u.ZadnjiDatum is { } d ? DateOnly.FromDateTime(d) : null,
        BrojStavki = u.BrojStavki,
        NazivFajla = u.NazivFajla,
        Uvezeno = u.Uvezeno
    };

    /// <summary>
    /// Bira izvor za svaku godinu u periodu. Kad zahtjev ne navodi baze:
    /// godina čija baza postoji u kasi čita se iz nje (živa baza uvijek ima prednost),
    /// a prošla godina bez baze čita se iz uvoza. Tekuća godina se nikad ne čita iz uvoza.
    /// Šifarnici (Glopos, Robe) se uvijek čitaju iz najnovije baze u kasi.
    /// </summary>
    private async Task<Izvori> IzvoriAsync(SqlConnection veza, ZahtjevUpita z, CancellationToken ct)
    {
        var stvarne = await StvarneBazeAsync(veza, ct);
        var sifarnik = SqlKatalog.NajnovijaBaza([.. stvarne]);

        if (z.Baze.Count > 0)
            return new Izvori(await PotvrdiBazeAsync(veza, z.Baze, ct), [], sifarnik);

        var (od, doDat) = Period(z);
        var uvezene = (await LokalnaBaza.UvezeneGodineAsync(veza, ct)).Select(g => g.Godina).ToHashSet();
        var pokrivenost = await PokrivenostAsync(veza, stvarne, ct);
        var tekuca = DateTime.Today.Year;

        var baze = new List<string>();
        var arhiva = new List<int>();
        for (var godina = od.Year; godina <= doDat.Year; godina++)
        {
            var izKase = BazeZaGodinu(pokrivenost, stvarne, godina);
            if (izKase.Count > 0) baze.AddRange(izKase.Where(b => !baze.Contains(b)));
            else if (godina < tekuca && uvezene.Contains(godina)) arhiva.Add(godina);
        }

        // Period bez ijednog izvora (npr. prije svih podataka) daje prazan rezultat.
        var kolacija = arhiva.Count > 0 ? await KolacijaAsync(veza, sifarnik, ct) : null;
        return new Izvori(baze, arhiva, sifarnik, kolacija);
    }

    /// <summary>
    /// Toliko stavki mora baza imati u nekoj godini da bi se ta godina smatrala „u kasi".
    /// Par zalutalih stavki sa pogrešnim datumom ne smije zaključati cijelu godinu.
    /// </summary>
    private const long NajmanjeStavkiZaGodinu = 50;

    /// <summary>Koliko dugo vrijedi popis godina po bazama — skeniranje datuma nije besplatno.</summary>
    private static readonly TimeSpan TrajanjePokrivenosti = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _kapijaPokrivenosti = new(1, 1);
    private (DateTime Kada, string Kljuc, List<GodinaBaze> Podaci)? _pokrivenost;

    public sealed record GodinaBaze(string Baza, int Godina, long BrojStavki, DateOnly PrviDatum, DateOnly ZadnjiDatum);

    /// <summary>
    /// Koje godine sadrži svaka baza kase. Pamti se deset minuta: nova godina se u bazi
    /// pojavi jednom godišnje, a upit bi inače skenirao datume pri svakom izvještaju.
    /// </summary>
    private async Task<List<GodinaBaze>> PokrivenostAsync(SqlConnection veza, IReadOnlyList<string> stvarne, CancellationToken ct)
    {
        var kljuc = string.Join("|", stvarne.OrderBy(b => b, StringComparer.OrdinalIgnoreCase));
        if (_pokrivenost is { } p && p.Kljuc == kljuc && DateTime.UtcNow - p.Kada < TrajanjePokrivenosti)
            return p.Podaci;

        await _kapijaPokrivenosti.WaitAsync(ct);
        try
        {
            if (_pokrivenost is { } q && q.Kljuc == kljuc && DateTime.UtcNow - q.Kada < TrajanjePokrivenosti)
                return q.Podaci;

            var podaci = new List<GodinaBaze>();
            foreach (var baza in stvarne)
            {
                try
                {
                    var redovi = await UpitAsync<GodinaBazeRed>(veza, SqlKatalog.GodineUBazi(baza), null, ct);
                    podaci.AddRange(redovi.Select(r => new GodinaBaze(baza, r.Godina, r.BrojStavki,
                        DateOnly.FromDateTime(r.PrviDatum), DateOnly.FromDateTime(r.ZadnjiDatum))));
                }
                catch (SqlException ex)
                {
                    log.LogWarning(ex, "Godine u bazi {Baza} se ne mogu očitati", baza);
                }
            }

            _pokrivenost = (DateTime.UtcNow, kljuc, podaci);
            return podaci;
        }
        finally
        {
            _kapijaPokrivenosti.Release();
        }
    }

    /// <summary>
    /// Baze kase koje nose neku godinu: ona koja se tako zove i svaka druga koja ima
    /// bar <see cref="NajmanjeStavkiZaGodinu"/> stavki iz te godine.
    /// </summary>
    public static List<string> BazeZaGodinu(List<GodinaBaze> pokrivenost, IReadOnlyList<string> stvarne, int godina)
    {
        var baze = pokrivenost
            .Where(p => p.Godina == godina && p.BrojStavki >= NajmanjeStavkiZaGodinu)
            .Select(p => p.Baza)
            .ToList();
        var poImenu = stvarne.FirstOrDefault(b => b.Equals($"EtisRpos_{godina}", StringComparison.OrdinalIgnoreCase));
        if (poImenu is not null && !baze.Contains(poImenu, StringComparer.OrdinalIgnoreCase)) baze.Add(poImenu);
        return baze;
    }

    private static async Task<string?> KolacijaAsync(SqlConnection veza, string baza, CancellationToken ct)
    {
        return await veza.ExecuteScalarAsync<string?>(new CommandDefinition(
            SqlKatalog.KolacijaBaze, new { baza }, cancellationToken: ct));
    }

    private static async Task<List<string>> StvarneBazeAsync(SqlConnection veza, CancellationToken ct)
    {
        var stvarne = (await UpitAsync<string>(veza, SqlKatalog.PopisBaza, null, ct))
            .Where(SqlKatalog.NazivBazeJeIspravan)
            .ToList();
        if (stvarne.Count == 0)
            throw new InvalidOperationException("Na ovom serveru nije pronađena nijedna EtisRpos baza.");
        return stvarne;
    }

    /// <summary>
    /// Pregled godina za postavke: tekuća godina, svaka godina sa bazom u kasi,
    /// svaka uvezena godina i bar tri prošle godine, bez rupa između njih.
    /// </summary>
    private async Task<List<GodinaPodataka>> GodineAsync(SqlConnection veza, CancellationToken ct)
    {
        var stvarne = await StvarneBazeAsync(veza, ct);
        var pokrivenost = await PokrivenostAsync(veza, stvarne, ct);
        var uvezene = await LokalnaBaza.UvezeneGodineAsync(veza, ct);
        var tekuca = DateTime.Today.Year;

        var poznate = pokrivenost.Where(p => p.BrojStavki >= NajmanjeStavkiZaGodinu).Select(p => p.Godina)
            .Concat(stvarne.Select(b => int.Parse(b[^4..])))
            .Concat(uvezene.Select(u => u.Godina))
            .Append(tekuca - 3)
            .Where(g => g <= tekuca);
        var najranija = poznate.Min();

        var rezultat = new List<GodinaPodataka>();
        for (var godina = tekuca; godina >= najranija; godina--)
        {
            // Godina koju kasa ima — po nazivu baze ili po stvarnim datumima u bilo kojoj
            // bazi — čita se iz kase i zaključana je za uvoz fajla.
            var izKase = BazeZaGodinu(pokrivenost, stvarne, godina);
            var dijelovi = pokrivenost.Where(p => p.Godina == godina && izKase.Contains(p.Baza)).ToList();
            var uvoz = uvezene.FirstOrDefault(u => u.Godina == godina);

            if (godina == tekuca || izKase.Count > 0)
            {
                rezultat.Add(new GodinaPodataka
                {
                    Godina = godina,
                    Izvor = godina == tekuca ? IzvorGodine.Uzivo : IzvorGodine.BazaUKasi,
                    Baza = izKase.Count > 0 ? string.Join(", ", izKase) : null,
                    PrviDatum = dijelovi.Count > 0 ? dijelovi.Min(d => d.PrviDatum) : null,
                    ZadnjiDatum = dijelovi.Count > 0 ? dijelovi.Max(d => d.ZadnjiDatum) : null,
                    BrojStavki = dijelovi.Sum(d => d.BrojStavki),
                    ImaIUvoz = uvoz is not null
                });
            }
            else if (uvoz is not null)
            {
                rezultat.Add(Uvezena(uvoz));
            }
            else
            {
                rezultat.Add(new GodinaPodataka { Godina = godina, Izvor = IzvorGodine.Nema });
            }
        }
        return rezultat;
    }

    /// <summary>Uvoz se odbija za svaku godinu koju kasa već ima — ona se čita uživo.</summary>
    private async Task ProvjeriDaGodinaNijeUKasiAsync(SqlConnection veza, int godina, CancellationToken ct)
    {
        var stvarne = await StvarneBazeAsync(veza, ct);
        var izKase = BazeZaGodinu(await PokrivenostAsync(veza, stvarne, ct), stvarne, godina);
        if (izKase.Count > 0)
            throw new InvalidOperationException(
                $"{godina}. se već čita iz kase ({string.Join(", ", izKase)}) — uvoz fajla za nju je zaključan.");
    }

    /// <summary>
    /// Naziv baze ulazi u tekst upita, pa mora proći dvije provjere: oblik naziva
    /// i postojanje na ovom serveru. Bez toga bi naziv iz zahtjeva bio put ka injekciji.
    /// </summary>
    private async Task<List<string>> PotvrdiBazeAsync(SqlConnection veza, IReadOnlyList<string> trazene, CancellationToken ct)
    {
        var stvarne = (await UpitAsync<string>(veza, SqlKatalog.PopisBaza, null, ct))
            .Where(SqlKatalog.NazivBazeJeIspravan)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (stvarne.Count == 0)
            throw new InvalidOperationException("Na ovom serveru nije pronađena nijedna EtisRpos baza.");

        if (trazene.Count == 0)
            return [stvarne.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).First()];

        var potvrdjene = trazene.Where(stvarne.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (potvrdjene.Count == 0)
            throw new InvalidOperationException("Nijedna od traženih baza ne postoji na ovom serveru.");

        return potvrdjene;
    }

    /// <summary>
    /// Zagrijava bazu čim se most spoji: pušta jedan lagan agregat nad zadnjih
    /// sedam dana da SQL Server izgradi plan izvršenja i podigne stranice u keš.
    ///
    /// Bez toga prvi upit poslije paljenja računara zna trajati desetak sekundi,
    /// pa je korisnik dočekivan porukom o isteku umjesto podacima.
    /// Greška se namjerno guta — ako zagrijavanje ne uspije, aplikacija i dalje radi.
    /// </summary>
    public async Task ZagrijAsync(CancellationToken ct)
    {
        try
        {
            await using var veza = Veza();
            await veza.OpenAsync(ct);

            var baze = await PotvrdiBazeAsync(veza, [], ct);
            var izvori = new Izvori(baze, [], baze[0]);
            var par = new DynamicParameters();
            par.Add("od", DateTime.Today.AddDays(-7), System.Data.DbType.Date);
            par.Add("do", DateTime.Today, System.Data.DbType.Date);

            var sat = Stopwatch.StartNew();
            await UpitAsync<SazetakRed>(veza, SqlKatalog.Sazetak(izvori), par, ct);
            log.LogInformation("Baza zagrijana za {Ms} ms", sat.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Zagrijavanje baze nije uspjelo; nastavljam");
        }
    }

    // Pomoćni oblici za Dapper — DateOnly i decimal se čitaju kao DateTime pa mapiraju.
    private sealed class SazetakRed
    {
        public decimal Promet { get; set; }
        public int BrojRacuna { get; set; }
        public decimal BrojArtikala { get; set; }
        public int RadnihDana { get; set; }
    }

    private sealed class DanRed
    {
        public DateTime Datum { get; set; }
        public decimal Promet { get; set; }
        public int BrojRacuna { get; set; }
    }

    private sealed class DnevnaProdajaRed
    {
        public DateTime Datum { get; set; }
        public double Sifra { get; set; }
        public decimal Kolicina { get; set; }
    }

    private sealed class GodinaBazeRed
    {
        public int Godina { get; set; }
        public long BrojStavki { get; set; }
        public DateTime PrviDatum { get; set; }
        public DateTime ZadnjiDatum { get; set; }
    }

    private sealed class OpsegRed
    {
        public DateTime? PrviDatum { get; set; }
        public DateTime? ZadnjiDatum { get; set; }
        public long BrojStavki { get; set; }
    }
}
