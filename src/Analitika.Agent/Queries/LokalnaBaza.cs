using Analitika.Shared.Upiti;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Analitika.Agent.Queries;

/// <summary>
/// Baza <c>Analitika</c> na serveru klijenta — jedino mjesto gdje most piše.
/// POS baze se ne diraju; <c>dbo.Normativi</c> koju koristi kasa ostaje netaknuta.
/// </summary>
public sealed class LokalnaBaza(ILogger<LokalnaBaza> log)
{
    public const string Naziv = "Analitika";

    /// <summary>
    /// Datum "od početka" za prvi recept nekog artikla — dovoljno rano da obuhvati
    /// sve godišnje POS baze koje klijent može imati.
    /// </summary>
    private static readonly DateTime OdPocetka = new(2000, 1, 1);

    // Priprema se izvršava jednom po procesu. Ranije je išla pri svakom zahtjevu i,
    // iako su sve naredbe idempotentne, CREATE DATABASE i CREATE INDEX uzimaju
    // zaključavanja koja blokiraju čitanje sys.databases u usporednim upitima —
    // ti su onda čekali do isteka roka i javljali "Execution Timeout Expired".
    private readonly SemaphoreSlim _kapija = new(1, 1);
    private bool _spremna;

    /// <summary>
    /// Kreira bazu i tabele ako ih nema. Naredbe su idempotentne, ali se izvršavaju
    /// samo pri prvom pozivu u životu procesa — dalji pozivi su besplatni.
    /// </summary>
    public async Task PripremiAsync(SqlConnection veza, CancellationToken ct)
    {
        if (_spremna) return;

        await _kapija.WaitAsync(ct);
        try
        {
            if (_spremna) return;
            await IzgradiAsync(veza, ct);
            _spremna = true;
        }
        finally
        {
            _kapija.Release();
        }
    }

    private async Task IzgradiAsync(SqlConnection veza, CancellationToken ct)
    {
        await Izvrsi(veza, $"IF DB_ID('{Naziv}') IS NULL CREATE DATABASE [{Naziv}];", ct);

        await Izvrsi(veza, $"""
            USE [{Naziv}];

            IF OBJECT_ID('dbo.Normativ') IS NULL
            CREATE TABLE dbo.Normativ
            (
                Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Normativ PRIMARY KEY,
                ArtikalSifra float             NOT NULL,
                ArtikalNaziv nvarchar(100)     NOT NULL,
                VaziOd       date              NOT NULL CONSTRAINT DF_Normativ_VaziOd DEFAULT (CAST(GETDATE() AS date)),
                VaziDo       date              NULL,
                Izmijenjeno  datetime2(0)      NOT NULL CONSTRAINT DF_Normativ_Izmijenjeno DEFAULT (SYSDATETIME())
            );

            IF OBJECT_ID('dbo.NormativStavka') IS NULL
            CREATE TABLE dbo.NormativStavka
            (
                Id                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_NormativStavka PRIMARY KEY,
                NormativId           int               NOT NULL
                    CONSTRAINT FK_NormativStavka_Normativ REFERENCES dbo.Normativ(Id) ON DELETE CASCADE,
                RepromaterijalBarkod float             NOT NULL,
                Naziv                nvarchar(100)     NOT NULL,
                Kolicina             decimal(18,5)     NOT NULL,
                Jm                   nvarchar(10)      NULL,
                NabavnaCijena        decimal(18,5)     NOT NULL CONSTRAINT DF_NormativStavka_Cijena DEFAULT (0)
            );

            IF OBJECT_ID('dbo.Postavke') IS NULL
            CREATE TABLE dbo.Postavke
            (
                Kljuc      nvarchar(50)  NOT NULL CONSTRAINT PK_Postavke PRIMARY KEY,
                Vrijednost nvarchar(max) NULL
            );
            """, ct);

        // Jedan artikal smije imati samo jedan otvoren recept.
        await Izvrsi(veza, $"""
            USE [{Naziv}];
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Normativ_Aktivan')
                CREATE UNIQUE INDEX UQ_Normativ_Aktivan ON dbo.Normativ (ArtikalSifra) WHERE VaziDo IS NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NormativStavka_Normativ')
                CREATE INDEX IX_NormativStavka_Normativ ON dbo.NormativStavka (NormativId);
            """, ct);

        // Prodaja prošlih godina uvezena iz fajla — za godine čije baze više nema u kasi.
        // Isti stupci kao POS_Stavke, plus ime konobara jer Glopos te godine ne postoji.
        // ArhivaUvoz je privremena: paketi se skupljaju tu, a u ArhivaStavki prelaze tek
        // kad korisnik potvrdi — prekinut uvoz nikad ne ostavi pola godine.
        await Izvrsi(veza, $"""
            USE [{Naziv}];

            IF OBJECT_ID('dbo.ArhivaStavki') IS NULL
            BEGIN
                CREATE TABLE dbo.ArhivaStavki
                (
                    DATUM   date           NOT NULL,
                    VRIJEME int            NULL,
                    BROJ_FR varchar(20)    NULL,
                    BARKOD  float          NOT NULL,
                    NAZIV   nvarchar(100)  NOT NULL,
                    GRUPA   nvarchar(50)   NULL,
                    ID_CARD varchar(20)    NULL,
                    KONOBAR nvarchar(60)   NULL,
                    IZLAZ   decimal(12,3)  NOT NULL,
                    MPC     decimal(12,2)  NOT NULL
                );
                CREATE CLUSTERED INDEX CX_ArhivaStavki_Datum ON dbo.ArhivaStavki (DATUM);
            END

            IF OBJECT_ID('dbo.ArhivaUvoz') IS NULL
            BEGIN
                CREATE TABLE dbo.ArhivaUvoz
                (
                    UvozId  uniqueidentifier NOT NULL,
                    DATUM   date           NOT NULL,
                    VRIJEME int            NULL,
                    BROJ_FR varchar(20)    NULL,
                    BARKOD  float          NOT NULL,
                    NAZIV   nvarchar(100)  NOT NULL,
                    GRUPA   nvarchar(50)   NULL,
                    ID_CARD varchar(20)    NULL,
                    KONOBAR nvarchar(60)   NULL,
                    IZLAZ   decimal(12,3)  NOT NULL,
                    MPC     decimal(12,2)  NOT NULL
                );
                CREATE CLUSTERED INDEX CX_ArhivaUvoz ON dbo.ArhivaUvoz (UvozId);
            END

            IF OBJECT_ID('dbo.ArhivaUvozZaglavlje') IS NULL
            CREATE TABLE dbo.ArhivaUvozZaglavlje
            (
                UvozId     uniqueidentifier NOT NULL CONSTRAINT PK_ArhivaUvozZaglavlje PRIMARY KEY,
                Godina     int              NOT NULL,
                NazivFajla nvarchar(260)    NULL,
                Pocetak    datetime2(0)     NOT NULL CONSTRAINT DF_ArhivaUvozZaglavlje_Pocetak DEFAULT (SYSDATETIME())
            );

            IF OBJECT_ID('dbo.ArhivaGodina') IS NULL
            CREATE TABLE dbo.ArhivaGodina
            (
                Godina      int           NOT NULL CONSTRAINT PK_ArhivaGodina PRIMARY KEY,
                NazivFajla  nvarchar(260) NULL,
                Uvezeno     datetime2(0)  NOT NULL,
                BrojStavki  bigint        NOT NULL,
                PrviDatum   date          NULL,
                ZadnjiDatum date          NULL
            );
            """, ct);

        log.LogInformation("Lokalna baza {Baza} je spremna", Naziv);
    }

    private static Task Izvrsi(SqlConnection veza, string sql, CancellationToken ct) =>
        veza.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 60, cancellationToken: ct));

    // -----------------------------------------------------------------
    // Normativi
    // -----------------------------------------------------------------

    public async Task<List<Normativ>> SviAsync(SqlConnection veza, bool samoAktivni, CancellationToken ct)
    {
        var sql = $"""
            USE [{Naziv}];
            SELECT Id, ArtikalSifra, ArtikalNaziv, VaziOd, VaziDo FROM dbo.Normativ
            {(samoAktivni ? "WHERE VaziDo IS NULL" : "")}
            ORDER BY ArtikalNaziv;

            SELECT s.Id, s.NormativId, s.RepromaterijalBarkod, s.Naziv, s.Kolicina, s.Jm, s.NabavnaCijena
            FROM dbo.NormativStavka s
            JOIN dbo.Normativ n ON n.Id = s.NormativId
            {(samoAktivni ? "WHERE n.VaziDo IS NULL" : "")}
            ORDER BY s.Naziv;
            """;

        await using var visestruki = await veza.QueryMultipleAsync(
            new CommandDefinition(sql, commandTimeout: 30, cancellationToken: ct));

        var zaglavlja = (await visestruki.ReadAsync<NormativRed>()).ToList();
        var stavke = (await visestruki.ReadAsync<StavkaRed>()).ToList()
            .GroupBy(s => s.NormativId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return [.. zaglavlja.Select(z => new Normativ
        {
            Id = z.Id,
            ArtikalSifra = z.ArtikalSifra,
            ArtikalNaziv = z.ArtikalNaziv,
            VaziOd = DateOnly.FromDateTime(z.VaziOd),
            VaziDo = z.VaziDo is { } d ? DateOnly.FromDateTime(d) : null,
            Stavke = [.. (stavke.GetValueOrDefault(z.Id) ?? []).Select(s => new NormativStavka
            {
                Id = s.Id,
                RepromaterijalBarkod = s.RepromaterijalBarkod,
                Naziv = s.Naziv,
                Kolicina = s.Kolicina,
                Jm = s.Jm,
                NabavnaCijena = s.NabavnaCijena
            })]
        })];
    }

    /// <summary>
    /// Snima recept. Postojeći otvoreni recept za isti artikal se zatvara umjesto
    /// da se prepiše — stari obračuni utroška ostaju tačni.
    /// </summary>
    public async Task<int> SnimiAsync(SqlConnection veza, Normativ normativ, CancellationToken ct)
    {
        if (normativ.Stavke.Count == 0)
            throw new InvalidOperationException("Normativ mora imati bar jednu stavku.");

        await veza.ChangeDatabaseAsync(Naziv, ct);
        await using var tx = (SqlTransaction)await veza.BeginTransactionAsync(ct);

        var danas = DateTime.Today;

        // Recept koji je već danas dirán se dopunjuje umjesto da se pravi nova verzija —
        // dok korisnik podešava sastojke ne treba mu gomila verzija istog dana.
        var danasDiran = await veza.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT TOP 1 Id FROM dbo.Normativ
            WHERE ArtikalSifra = @sifra AND VaziDo IS NULL AND CAST(Izmijenjeno AS date) = @danas;
            """, new { sifra = normativ.ArtikalSifra, danas }, tx, cancellationToken: ct));

        int id;
        if (danasDiran is { } postojeci)
        {
            id = postojeci;
            await veza.ExecuteAsync(new CommandDefinition("""
                DELETE FROM dbo.NormativStavka WHERE NormativId = @id;
                UPDATE dbo.Normativ SET Izmijenjeno = SYSDATETIME() WHERE Id = @id;
                """, new { id }, tx, cancellationToken: ct));
        }
        else
        {
            // Prvi recept za artikal vrijedi od početka — opisuje kako se jelo oduvijek pravi,
            // pa mora obuhvatiti i raniju prodaju. Tek izmjena postojećeg recepta pravi
            // vremensku granicu od danas.
            var ikadaPostojao = await veza.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.Normativ WHERE ArtikalSifra = @sifra;",
                new { sifra = normativ.ArtikalSifra }, tx, cancellationToken: ct)) > 0;

            var vaziOd = ikadaPostojao ? danas : OdPocetka;

            await veza.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.Normativ SET VaziDo = DATEADD(day, -1, @danas)
                WHERE ArtikalSifra = @sifra AND VaziDo IS NULL;
                """, new { sifra = normativ.ArtikalSifra, danas }, tx, cancellationToken: ct));

            id = await veza.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO dbo.Normativ (ArtikalSifra, ArtikalNaziv, VaziOd)
                OUTPUT INSERTED.Id
                VALUES (@sifra, @naziv, @vaziOd);
                """,
                new { sifra = normativ.ArtikalSifra, naziv = normativ.ArtikalNaziv, vaziOd },
                tx, cancellationToken: ct));
        }

        foreach (var s in normativ.Stavke)
        {
            await veza.ExecuteAsync(new CommandDefinition("""
                INSERT INTO dbo.NormativStavka
                    (NormativId, RepromaterijalBarkod, Naziv, Kolicina, Jm, NabavnaCijena)
                VALUES (@id, @barkod, @naziv, @kolicina, @jm, @cijena);
                """,
                new
                {
                    id,
                    barkod = s.RepromaterijalBarkod,
                    naziv = s.Naziv,
                    kolicina = s.Kolicina,
                    jm = s.Jm,
                    cijena = s.NabavnaCijena
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        log.LogInformation("Normativ {Id} za artikal {Artikal} je snimljen", id, normativ.ArtikalNaziv);
        return id;
    }

    /// <summary>Zatvara recept umjesto brisanja — historija utroška ostaje netaknuta.</summary>
    public async Task ZatvoriAsync(SqlConnection veza, int id, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        await veza.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.Normativ SET VaziDo = CAST(GETDATE() AS date)
            WHERE Id = @id AND VaziDo IS NULL;
            """, new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Prepisuje recepte iz POS baze (dbo.Normativi + Repromaterijali) u lokalnu bazu.
    /// Preskače artikle koji već imaju recept, da ne pregazi ručni unos.
    /// </summary>
    public async Task<int> UveziAsync(SqlConnection veza, string posBaza, CancellationToken ct)
    {
        var sql = $"""
            SELECT n.SIF AS ArtikalSifra,
                   RTRIM(ISNULL(r.NAZIV, n.NAZIV)) AS ArtikalNaziv,
                   n.ROB AS RepromaterijalBarkod,
                   RTRIM(ISNULL(rm.NAZIV, n.NAZIV)) AS Naziv,
                   n.KOL AS Kolicina,
                   NULLIF(RTRIM(rm.JM), '') AS Jm,
                   CAST(ISNULL(rm.NAB_CIJENA, 0) AS decimal(18,5)) AS NabavnaCijena
            FROM [{posBaza}].dbo.Normativi n
            LEFT JOIN [{posBaza}].dbo.Robe r ON r.SIFRA = n.SIF
            LEFT JOIN [{posBaza}].dbo.Repromaterijali rm ON rm.BARKOD = n.ROB
            WHERE n.KOL > 0
            ORDER BY n.SIF, n.STAVKA;
            """;

        var redovi = (await veza.QueryAsync<UvozRed>(
            new CommandDefinition(sql, commandTimeout: 60, cancellationToken: ct))).ToList();

        if (redovi.Count == 0) return 0;

        var postojeci = (await SviAsync(veza, samoAktivni: true, ct))
            .Select(n => n.ArtikalSifra)
            .ToHashSet();

        var uvezeno = 0;
        foreach (var grupa in redovi.GroupBy(r => r.ArtikalSifra))
        {
            if (postojeci.Contains(grupa.Key)) continue;

            await SnimiAsync(veza, new Normativ
            {
                ArtikalSifra = grupa.Key,
                ArtikalNaziv = grupa.First().ArtikalNaziv,
                Stavke = [.. grupa.Select(r => new NormativStavka
                {
                    RepromaterijalBarkod = r.RepromaterijalBarkod,
                    Naziv = r.Naziv,
                    Kolicina = r.Kolicina,
                    Jm = r.Jm,
                    NabavnaCijena = r.NabavnaCijena
                })]
            }, ct);
            uvezeno++;
        }

        log.LogInformation("Uvezeno {Broj} normativa iz {Baza}", uvezeno, posBaza);
        return uvezeno;
    }

    // -----------------------------------------------------------------
    // Postavke
    // -----------------------------------------------------------------

    public async Task<string?> PostavkaAsync(SqlConnection veza, string kljuc, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        return await veza.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Vrijednost FROM dbo.Postavke WHERE Kljuc = @kljuc;",
            new { kljuc }, cancellationToken: ct));
    }

    public async Task SnimiPostavkuAsync(SqlConnection veza, string kljuc, string? vrijednost, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        await veza.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.Postavke AS c
            USING (SELECT @kljuc AS Kljuc) AS n ON c.Kljuc = n.Kljuc
            WHEN MATCHED THEN UPDATE SET Vrijednost = @vrijednost
            WHEN NOT MATCHED THEN INSERT (Kljuc, Vrijednost) VALUES (@kljuc, @vrijednost);
            """, new { kljuc, vrijednost }, cancellationToken: ct));
    }

    // -----------------------------------------------------------------
    // Uvezene prošle godine
    // -----------------------------------------------------------------

    /// <summary>
    /// Godine uvezene iz fajla. Čita se bez pripreme baze — ako Analitika još ne postoji,
    /// nema ni uvezenih godina, a upiti prodaje ne smiju zavisiti od prava na CREATE DATABASE.
    /// </summary>
    public static async Task<List<UvezenaGodina>> UvezeneGodineAsync(SqlConnection veza, CancellationToken ct)
    {
        var postoji = await veza.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT CASE WHEN OBJECT_ID('[{Naziv}].dbo.ArhivaGodina') IS NULL THEN 0 ELSE 1 END;",
            cancellationToken: ct));
        if (postoji == 0) return [];

        return (await veza.QueryAsync<UvezenaGodina>(new CommandDefinition($"""
            SELECT Godina, NazivFajla, Uvezeno, BrojStavki, PrviDatum, ZadnjiDatum
            FROM [{Naziv}].dbo.ArhivaGodina ORDER BY Godina DESC;
            """, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Otvara uvoz. Tekuća godina se odbija ovdje, a ne samo u browseru —
    /// ona uvijek dolazi iz žive baze kase.
    /// </summary>
    public async Task<Guid> UvozPocniAsync(SqlConnection veza, int godina, string? nazivFajla, CancellationToken ct)
    {
        if (godina >= DateTime.Today.Year)
            throw new InvalidOperationException($"{godina}. se ne uvozi iz fajla — tekuća godina dolazi iz žive baze kase.");
        if (godina < 1990)
            throw new InvalidOperationException($"Godina {godina} nije ispravna.");

        await veza.ChangeDatabaseAsync(Naziv, ct);

        // Ostaci prekinutih uvoza (zatvoren browser, pukla veza) čiste se pri sljedećem.
        await veza.ExecuteAsync(new CommandDefinition("""
            DELETE u FROM dbo.ArhivaUvoz u
            JOIN dbo.ArhivaUvozZaglavlje z ON z.UvozId = u.UvozId
            WHERE z.Pocetak < DATEADD(hour, -6, SYSDATETIME());
            DELETE FROM dbo.ArhivaUvozZaglavlje WHERE Pocetak < DATEADD(hour, -6, SYSDATETIME());
            """, commandTimeout: 120, cancellationToken: ct));

        var id = Guid.NewGuid();
        var naziv = nazivFajla is null ? null : nazivFajla[..Math.Min(260, nazivFajla.Length)];
        await veza.ExecuteAsync(new CommandDefinition(
            "INSERT INTO dbo.ArhivaUvozZaglavlje (UvozId, Godina, NazivFajla) VALUES (@id, @godina, @naziv);",
            new { id, godina, naziv }, cancellationToken: ct));
        return id;
    }

    /// <summary>Jedan paket stavki ide u privremenu tabelu preko SqlBulkCopy.</summary>
    public async Task<int> UvozDioAsync(SqlConnection veza, Guid uvozId, IReadOnlyList<StavkaArhive> stavke, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        var godina = await veza.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Godina FROM dbo.ArhivaUvozZaglavlje WHERE UvozId = @uvozId;", new { uvozId }, cancellationToken: ct))
            ?? throw new InvalidOperationException("Uvoz nije otvoren ili je istekao — pokreni ga ponovo.");

        // Server je fajl već provjerio, ali most ne vjeruje nikome: stavka iz druge
        // godine bi se kasnije pomiješala sa živom bazom.
        if (stavke.Any(s => s.Datum.Year != godina))
            throw new InvalidOperationException($"Paket sadrži stavke koje nisu iz {godina}. godine.");

        var tabela = new System.Data.DataTable();
        tabela.Columns.Add("UvozId", typeof(Guid));
        tabela.Columns.Add("DATUM", typeof(DateTime));
        tabela.Columns.Add("VRIJEME", typeof(int));
        tabela.Columns.Add("BROJ_FR", typeof(string));
        tabela.Columns.Add("BARKOD", typeof(double));
        tabela.Columns.Add("NAZIV", typeof(string));
        tabela.Columns.Add("GRUPA", typeof(string));
        tabela.Columns.Add("ID_CARD", typeof(string));
        tabela.Columns.Add("KONOBAR", typeof(string));
        tabela.Columns.Add("IZLAZ", typeof(decimal));
        tabela.Columns.Add("MPC", typeof(decimal));

        foreach (var s in stavke)
        {
            tabela.Rows.Add(uvozId, s.Datum.ToDateTime(TimeOnly.MinValue), (object?)s.Vrijeme ?? DBNull.Value,
                (object?)s.BrojFr ?? DBNull.Value, s.Barkod, s.Naziv, (object?)s.Grupa ?? DBNull.Value,
                (object?)s.IdKartice ?? DBNull.Value, (object?)s.Konobar ?? DBNull.Value, s.Izlaz, s.Mpc);
        }

        using var kopija = new SqlBulkCopy(veza) { DestinationTableName = "dbo.ArhivaUvoz", BulkCopyTimeout = 120 };
        foreach (System.Data.DataColumn k in tabela.Columns) kopija.ColumnMappings.Add(k.ColumnName, k.ColumnName);
        await kopija.WriteToServerAsync(tabela, ct);
        return stavke.Count;
    }

    /// <summary>
    /// Potvrđuje uvoz: stara verzija godine (ako je bila) se briše i nova upisuje
    /// u jednoj transakciji. Ponovni uvoz iste godine je zato siguran.
    /// </summary>
    public async Task<UvezenaGodina> UvozZavrsiAsync(SqlConnection veza, Guid uvozId, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        await using var tx = (SqlTransaction)await veza.BeginTransactionAsync(ct);

        var zaglavlje = await veza.QuerySingleOrDefaultAsync<ZaglavljeUvoza>(new CommandDefinition(
            "SELECT Godina, NazivFajla FROM dbo.ArhivaUvozZaglavlje WHERE UvozId = @uvozId;",
            new { uvozId }, tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("Uvoz nije otvoren ili je istekao — pokreni ga ponovo.");

        var od = new DateTime(zaglavlje.Godina, 1, 1);
        var rezultat = await veza.QuerySingleAsync<UvezenaGodina>(new CommandDefinition("""
            DELETE FROM dbo.ArhivaStavki WHERE DATUM >= @od AND DATUM < DATEADD(year, 1, @od);

            INSERT INTO dbo.ArhivaStavki (DATUM, VRIJEME, BROJ_FR, BARKOD, NAZIV, GRUPA, ID_CARD, KONOBAR, IZLAZ, MPC)
            SELECT DATUM, VRIJEME, BROJ_FR, BARKOD, NAZIV, GRUPA, ID_CARD, KONOBAR, IZLAZ, MPC
            FROM dbo.ArhivaUvoz WHERE UvozId = @uvozId;

            DELETE FROM dbo.ArhivaUvoz WHERE UvozId = @uvozId;
            DELETE FROM dbo.ArhivaUvozZaglavlje WHERE UvozId = @uvozId;

            MERGE dbo.ArhivaGodina AS c
            USING (
                SELECT @godina AS Godina, COUNT_BIG(*) AS BrojStavki, MIN(DATUM) AS PrviDatum, MAX(DATUM) AS ZadnjiDatum
                FROM dbo.ArhivaStavki WHERE DATUM >= @od AND DATUM < DATEADD(year, 1, @od)
            ) AS n ON c.Godina = n.Godina
            WHEN MATCHED THEN UPDATE SET NazivFajla = @naziv, Uvezeno = SYSDATETIME(),
                 BrojStavki = n.BrojStavki, PrviDatum = n.PrviDatum, ZadnjiDatum = n.ZadnjiDatum
            WHEN NOT MATCHED THEN INSERT (Godina, NazivFajla, Uvezeno, BrojStavki, PrviDatum, ZadnjiDatum)
                 VALUES (n.Godina, @naziv, SYSDATETIME(), n.BrojStavki, n.PrviDatum, n.ZadnjiDatum);

            SELECT Godina, NazivFajla, Uvezeno, BrojStavki, PrviDatum, ZadnjiDatum
            FROM dbo.ArhivaGodina WHERE Godina = @godina;
            """, new { uvozId, od, godina = zaglavlje.Godina, naziv = zaglavlje.NazivFajla },
            tx, commandTimeout: 300, cancellationToken: ct));

        await tx.CommitAsync(ct);
        log.LogInformation("Uvezena {Godina}. godina: {Broj} stavki iz {Fajl}",
            zaglavlje.Godina, rezultat.BrojStavki, zaglavlje.NazivFajla);
        return rezultat;
    }

    public async Task UvozOdustaniAsync(SqlConnection veza, Guid uvozId, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        await veza.ExecuteAsync(new CommandDefinition("""
            DELETE FROM dbo.ArhivaUvoz WHERE UvozId = @uvozId;
            DELETE FROM dbo.ArhivaUvozZaglavlje WHERE UvozId = @uvozId;
            """, new { uvozId }, commandTimeout: 120, cancellationToken: ct));
    }

    public async Task ObrisiGodinuAsync(SqlConnection veza, int godina, CancellationToken ct)
    {
        await veza.ChangeDatabaseAsync(Naziv, ct);
        var od = new DateTime(godina, 1, 1);
        await veza.ExecuteAsync(new CommandDefinition("""
            DELETE FROM dbo.ArhivaStavki WHERE DATUM >= @od AND DATUM < DATEADD(year, 1, @od);
            DELETE FROM dbo.ArhivaGodina WHERE Godina = @godina;
            """, new { od, godina }, commandTimeout: 300, cancellationToken: ct));
        log.LogInformation("Obrisana uvezena {Godina}. godina", godina);
    }

    public sealed class UvezenaGodina
    {
        public int Godina { get; set; }
        public string? NazivFajla { get; set; }
        public DateTime Uvezeno { get; set; }
        public long BrojStavki { get; set; }
        public DateTime? PrviDatum { get; set; }
        public DateTime? ZadnjiDatum { get; set; }
    }

    private sealed class ZaglavljeUvoza
    {
        public int Godina { get; set; }
        public string? NazivFajla { get; set; }
    }

    // Pomoćni oblici za Dapper.
    private sealed class NormativRed
    {
        public int Id { get; set; }
        public double ArtikalSifra { get; set; }
        public string ArtikalNaziv { get; set; } = "";
        public DateTime VaziOd { get; set; }
        public DateTime? VaziDo { get; set; }
    }

    private sealed class StavkaRed
    {
        public int Id { get; set; }
        public int NormativId { get; set; }
        public double RepromaterijalBarkod { get; set; }
        public string Naziv { get; set; } = "";
        public decimal Kolicina { get; set; }
        public string? Jm { get; set; }
        public decimal NabavnaCijena { get; set; }
    }

    private sealed class UvozRed
    {
        public double ArtikalSifra { get; set; }
        public string ArtikalNaziv { get; set; } = "";
        public double RepromaterijalBarkod { get; set; }
        public string Naziv { get; set; } = "";
        public decimal Kolicina { get; set; }
        public string? Jm { get; set; }
        public decimal NabavnaCijena { get; set; }
    }
}
