using System.Text;
using System.Text.RegularExpressions;

namespace Analitika.Agent.Queries;

/// <summary>
/// Odakle se čitaju prodajne stavke za jedan upit: godišnje baze kase, godine uvezene
/// iz fajla i baza iz koje se čitaju šifarnici (uvijek najnovija u kasi).
///
/// <paramref name="Kolacija"/> je kolacija POS baze: lokalna baza Analitika ima serversku,
/// pa se tekst uvezenih stavki pri čitanju prevodi u kolaciju kase. Bez toga UNION i spoj
/// sa Glopos puknu („Cannot resolve the collation conflict"), a upiti nad samom kasom
/// ostaju potpuno isti kao prije.
/// </summary>
public sealed record Izvori(IReadOnlyList<string> Baze, IReadOnlyList<int> Arhiva, string Sifarnik, string? Kolacija = null);

/// <summary>
/// Sav SQL nad POS bazom stoji ovdje. Most ne izvršava ništa što nije odavde —
/// server šalje samo ime upita, nikad SQL.
/// </summary>
public static partial class SqlKatalog
{
    /// <summary>
    /// Naziv baze se ubacuje u tekst upita (ime baze ne može biti parametar),
    /// pa se prvo mora proći kroz ovu provjeru i tek onda kroz QUOTENAME.
    /// </summary>
    [GeneratedRegex(@"^EtisRpos_\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex DozvoljenNazivBaze();

    public static bool NazivBazeJeIspravan(string naziv) => DozvoljenNazivBaze().IsMatch(naziv);

    /// <summary>Najnovija baza — iz nje se čitaju šifarnici (Robe, Glopos, Repromaterijali).</summary>
    public static string NajnovijaBaza(IReadOnlyList<string> baze) =>
        baze.OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase).First();

    /// <summary>
    /// Gradi UNION ALL preko izvora: godišnjih baza kase i godina uvezenih iz fajla.
    /// Sve grane imaju iste stupce. KONOBAR postoji samo u uvezenim godinama — tamo
    /// nema Glopos tabele iz koje bi se ime pročitalo.
    /// Filter <c>DATUM IS NOT NULL</c> je namjeran — u bazi postoje stavke bez datuma.
    /// </summary>
    private static string IzvorStavki(Izvori izvori)
    {
        var grane = new List<string>();
        foreach (var baza in izvori.Baze)
        {
            grane.Add($"""
                        SELECT DATUM, BROJ_FR, BARKOD, NAZIV, GRUPA, ID_CARD, CAST(NULL AS nvarchar(60)) AS KONOBAR, IZLAZ, MPC, VRIJEME
                        FROM {Kv(baza)}.dbo.POS_Stavke
                        WHERE DATUM IS NOT NULL AND DATUM BETWEEN @od AND @do
                """);
        }

        if (izvori.Arhiva.Count > 0)
        {
            // Godine su cijeli brojevi iz tabele ArhivaGodina, ne tekst iz zahtjeva.
            var godine = string.Join(", ", izvori.Arhiva.Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            grane.Add($"""
                        SELECT DATUM, BROJ_FR{K(izvori.Kolacija)} AS BROJ_FR, BARKOD, NAZIV{K(izvori.Kolacija)} AS NAZIV, GRUPA{K(izvori.Kolacija)} AS GRUPA,
                               ID_CARD{K(izvori.Kolacija)} AS ID_CARD, KONOBAR{K(izvori.Kolacija)} AS KONOBAR, IZLAZ, MPC, VRIJEME
                        FROM [{LokalnaBaza.Naziv}].dbo.ArhivaStavki
                        WHERE DATUM BETWEEN @od AND @do AND YEAR(DATUM) IN ({godine})
                """);
        }

        // Period bez ijednog izvora daje prazan skup istog oblika, a ne neispravan SQL.
        if (grane.Count == 0)
        {
            grane.Add("""
                        SELECT CAST(NULL AS date) AS DATUM, CAST(NULL AS varchar(20)) AS BROJ_FR, CAST(NULL AS float) AS BARKOD,
                               CAST(NULL AS nvarchar(100)) AS NAZIV, CAST(NULL AS nvarchar(50)) AS GRUPA, CAST(NULL AS varchar(20)) AS ID_CARD,
                               CAST(NULL AS nvarchar(60)) AS KONOBAR, CAST(NULL AS decimal(12,3)) AS IZLAZ, CAST(NULL AS decimal(12,2)) AS MPC,
                               CAST(NULL AS int) AS VRIJEME
                        WHERE 1 = 0
                """);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < grane.Count; i++)
        {
            if (i > 0) sb.AppendLine("        UNION ALL");
            sb.AppendLine(grane[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex IspravnaKolacija();

    /// <summary>
    /// COLLATE klauzula za uvezene stavke. Naziv kolacije dolazi iz sys.databases, ali
    /// ulazi u tekst upita, pa i on prolazi provjeru oblika.
    /// </summary>
    private static string K(string? kolacija) =>
        kolacija is not null && IspravnaKolacija().IsMatch(kolacija) ? $" COLLATE {kolacija}" : "";

    /// <summary>Kolacija baze kase — uvezene stavke se pri čitanju prevode u nju.</summary>
    public const string KolacijaBaze = "SELECT CAST(DATABASEPROPERTYEX(@baza, 'Collation') AS nvarchar(128));";

    /// <summary>QUOTENAME nad već provjerenim nazivom baze.</summary>
    private static string Kv(string naziv) => "[" + naziv.Replace("]", "]]") + "]";

    // ---------------------------------------------------------------------
    // Otkrivanje baza
    // ---------------------------------------------------------------------

    /// <summary>
    /// Godišnje baze zatečene na serveru klijenta. <c>HAS_DBACCESS</c> odbacuje one
    /// na koje read-only nalog nema pravo, umjesto da upit kasnije pukne.
    /// </summary>
    public const string PopisBaza = """
        SELECT name
        FROM sys.databases
        WHERE name LIKE 'EtisRpos[_]%' AND state = 0 AND HAS_DBACCESS(name) = 1
        ORDER BY name DESC
        """;

    /// <summary>
    /// Koje godine baza kase stvarno sadrži — naziv baze ne govori sve: EtisRpos_2026
    /// zna nositi i decembar prethodne godine, ili cijelu godinu ako kasa nije otvarala novu.
    /// </summary>
    public static string GodineUBazi(string baza) => $"""
        SELECT YEAR(DATUM) AS Godina, COUNT_BIG(*) AS BrojStavki, MIN(DATUM) AS PrviDatum, MAX(DATUM) AS ZadnjiDatum
        FROM {Kv(baza)}.dbo.POS_Stavke
        WHERE DATUM IS NOT NULL
        GROUP BY YEAR(DATUM)
        """;

    /// <summary>Raspon podataka u jednoj bazi — prikazuje se pri izboru godina u postavkama.</summary>
    public static string OpsegBaze(string baza) => $"""
        SELECT MIN(DATUM) AS PrviDatum, MAX(DATUM) AS ZadnjiDatum, COUNT_BIG(*) AS BrojStavki
        FROM {Kv(baza)}.dbo.POS_Stavke
        WHERE DATUM IS NOT NULL
        """;

    // ---------------------------------------------------------------------
    // Analitika prodaje
    // ---------------------------------------------------------------------

    /// <summary>
    /// Promet se računa iz stavki (<c>IZLAZ*MPC</c>), a ne iz <c>POS_Zaglavlja.IZNOS</c> —
    /// zaglavlja imaju 0.00 na dijelu računa pa bi promet bio prepolovljen.
    /// Račun se identifikuje kao (DATUM, BROJ_FR); prazan BROJ_FR = još otvoren sto.
    /// </summary>
    public static string Sazetak(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT
            CAST(ISNULL(SUM(IZLAZ * MPC), 0) AS DECIMAL(18,2)) AS Promet,
            COUNT(DISTINCT CASE WHEN LTRIM(RTRIM(BROJ_FR)) <> ''
                                THEN CONVERT(char(8), DATUM, 112) + BROJ_FR END) AS BrojRacuna,
            CAST(ISNULL(SUM(IZLAZ), 0) AS DECIMAL(18,3)) AS BrojArtikala,
            COUNT(DISTINCT DATUM) AS RadnihDana
        FROM s
        """;

    public static string PrometPoDanima(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT DATUM AS Datum,
               CAST(SUM(IZLAZ * MPC) AS DECIMAL(18,2)) AS Promet,
               COUNT(DISTINCT CASE WHEN LTRIM(RTRIM(BROJ_FR)) <> ''
                                   THEN CONVERT(char(8), DATUM, 112) + BROJ_FR END) AS BrojRacuna
        FROM s
        GROUP BY DATUM
        ORDER BY DATUM
        """;

    /// <summary>
    /// VRIJEME je int u stotinkama sekunde od ponoći (max 8.640.000), nije HHMMSS.
    /// Vrijednosti izvan opsega se odbacuju umjesto da naprave 24+ sat.
    /// </summary>
    public static string PrometPoSatima(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT VRIJEME / 360000 AS Sat,
               CAST(SUM(IZLAZ * MPC) AS DECIMAL(18,2)) AS Promet,
               CAST(SUM(IZLAZ) AS DECIMAL(18,3)) AS BrojArtikala
        FROM s
        WHERE VRIJEME BETWEEN 0 AND 8639999
        GROUP BY VRIJEME / 360000
        ORDER BY Sat
        """;

    /// <summary>
    /// Promet po danu u sedmici i satu — mreža za toplotnu mapu gužve.
    ///
    /// Dan se računa preko <c>DATEDIFF</c> od poznatog ponedjeljka, a ne preko
    /// <c>DATEPART(dw)</c>, jer taj zavisi od postavke DATEFIRST na serveru klijenta
    /// i isti upit bi na dva servera davao pomjerene dane.
    /// </summary>
    public static string PrometPoDanuISatu(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        ),
        polja AS (
            SELECT DATEDIFF(day, '1900-01-01', DATUM) % 7 AS Dan,
                   VRIJEME / 360000 AS Sat,
                   IZLAZ * MPC AS Iznos,
                   DATUM
            FROM s
            WHERE VRIJEME BETWEEN 0 AND 8639999
        )
        SELECT Dan, Sat,
               CAST(SUM(Iznos) AS DECIMAL(18,2)) AS Promet,
               COUNT(DISTINCT DATUM) AS BrojDana
        FROM polja
        GROUP BY Dan, Sat
        ORDER BY Dan, Sat
        """;

    /// <summary>Artikal je BARKOD (-> Robe.SIFRA). ROB_CODE se ne koristi — u njemu je ime konobara.</summary>
    public static string RangArtikala(Izvori izvori, bool filterGrupe) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT TOP (@vrh)
               BARKOD AS Sifra,
               MAX(RTRIM(NAZIV)) AS Naziv,
               MAX(NULLIF(RTRIM(GRUPA), '')) AS Grupa,
               CAST(SUM(IZLAZ) AS DECIMAL(18,3)) AS Kolicina,
               CAST(SUM(IZLAZ * MPC) AS DECIMAL(18,2)) AS Promet
        FROM s
        {(filterGrupe ? "WHERE RTRIM(GRUPA) = @grupa" : "")}
        GROUP BY BARKOD
        ORDER BY Kolicina DESC
        """;

    /// <summary>
    /// Konobar se vodi po ID_CARD (popunjen na svakoj stavci), a ime se dohvata iz Glopos.
    /// POS_Zaglavlja.NAPLATIO se namjerno ne koristi — miješa ime i broj kartice za istu osobu.
    ///
    /// Glopos se prvo sažme na jedan red po kartici: dupla kartica u šifarniku bi inače
    /// udvostručila promet tog konobara. Za uvezene godine ime dolazi iz stupca KONOBAR.
    /// </summary>
    public static string RangKonobara(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        ),
        g AS (
            SELECT RTRIM(ID_CARD) AS Kartica, MAX(NULLIF(RTRIM(IME_PREZIME), '')) AS Ime
            FROM {Kv(izvori.Sifarnik)}.dbo.Glopos
            GROUP BY RTRIM(ID_CARD)
        )
        SELECT RTRIM(s.ID_CARD) AS IdKartice,
               COALESCE(MAX(g.Ime), MAX(NULLIF(RTRIM(s.KONOBAR), '')), MAX(NULLIF(RTRIM(s.ID_CARD), '')), 'Nepoznato') AS Ime,
               CAST(SUM(s.IZLAZ * s.MPC) AS DECIMAL(18,2)) AS Promet,
               CAST(SUM(s.IZLAZ) AS DECIMAL(18,3)) AS BrojArtikala,
               COUNT(DISTINCT CASE WHEN LTRIM(RTRIM(s.BROJ_FR)) <> ''
                                   THEN CONVERT(char(8), s.DATUM, 112) + s.BROJ_FR END) AS BrojRacuna
        FROM s
        LEFT JOIN g ON g.Kartica = RTRIM(s.ID_CARD)
        GROUP BY RTRIM(s.ID_CARD)
        ORDER BY Promet DESC
        """;

    /// <summary>
    /// Sve kartice konobara: iz šifarnika kase, iz prodaje tekuće godine (kartica
    /// koja je obrisana iz šifarnika i dalje ima promet) i iz uvezenih godina.
    /// </summary>
    public static string KonobariKase(string sifarnik, bool imaArhivu, string? kolacija) => $"""
        WITH k AS (
            SELECT RTRIM(ID_CARD) AS IdKartice, NULLIF(RTRIM(IME_PREZIME), '') AS Ime
            FROM {Kv(sifarnik)}.dbo.Glopos
            UNION ALL
            SELECT DISTINCT RTRIM(ID_CARD), CAST(NULL AS nvarchar(60))
            FROM {Kv(sifarnik)}.dbo.POS_Stavke
            {(imaArhivu ? $"""
            UNION ALL
            SELECT RTRIM(ID_CARD){K(kolacija)}, MAX(NULLIF(RTRIM(KONOBAR), '')){K(kolacija)}
            FROM [{LokalnaBaza.Naziv}].dbo.ArhivaStavki
            GROUP BY RTRIM(ID_CARD)
            """ : "")}
        )
        SELECT IdKartice, COALESCE(MAX(Ime), IdKartice) AS ImeUKasi
        FROM k
        WHERE IdKartice IS NOT NULL AND IdKartice <> ''
        GROUP BY IdKartice
        ORDER BY COALESCE(MAX(Ime), IdKartice)
        """;

    public static string PrometPoGrupama(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT ISNULL(NULLIF(RTRIM(GRUPA), ''), 'Bez grupe') AS Grupa,
               CAST(SUM(IZLAZ * MPC) AS DECIMAL(18,2)) AS Promet,
               CAST(SUM(IZLAZ) AS DECIMAL(18,3)) AS Kolicina
        FROM s
        GROUP BY ISNULL(NULLIF(RTRIM(GRUPA), ''), 'Bez grupe')
        ORDER BY Promet DESC
        """;

    public static string ProdajaPoArtiklu(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT BARKOD AS Sifra,
               MAX(RTRIM(NAZIV)) AS Naziv,
               CAST(SUM(IZLAZ) AS DECIMAL(18,3)) AS Kolicina,
               CAST(SUM(IZLAZ * MPC) AS DECIMAL(18,2)) AS Promet
        FROM s
        GROUP BY BARKOD
        """;

    /// <summary>Dnevna prodaja po artiklu — ulaz za sezonske indekse i projekcije.</summary>
    public static string DnevnaProdajaPoArtiklu(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT DATUM AS Datum, BARKOD AS Sifra, CAST(SUM(IZLAZ) AS DECIMAL(18,3)) AS Kolicina
        FROM s
        GROUP BY DATUM, BARKOD
        ORDER BY DATUM
        """;

    /// <summary>
    /// Artikli koji se prodaju a nemaju otvoren recept, poredani po prodaji —
    /// da se prvo pokriju oni koji stvarno troše zalihe.
    /// </summary>
    public static string ArtikliBezNormativa(Izvori izvori, string sifarnik) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        ),
        prodaja AS (
            SELECT BARKOD, MAX(RTRIM(NAZIV)) AS Naziv, MAX(NULLIF(RTRIM(GRUPA), '')) AS Grupa,
                   SUM(IZLAZ) AS Kolicina, SUM(IZLAZ * MPC) AS Promet
            FROM s GROUP BY BARKOD
        )
        SELECT TOP (@vrh)
               p.BARKOD AS Sifra, p.Naziv, p.Grupa,
               CAST(p.Kolicina AS DECIMAL(18,3)) AS Kolicina,
               CAST(p.Promet AS DECIMAL(18,2)) AS Promet
        FROM prodaja p
        WHERE NOT EXISTS (
            SELECT 1 FROM [Analitika].dbo.Normativ n
            WHERE n.ArtikalSifra = p.BARKOD AND n.VaziDo IS NULL
        )
        ORDER BY p.Kolicina DESC
        """;

    /// <summary>
    /// Očekivani utrošak repromaterijala: prodana količina artikla × norma iz recepta.
    /// Recept se bira po datumu prodaje, ne po današnjem — zato normativi imaju VaziOd/VaziDo.
    /// </summary>
    public static string UtrosakRepromaterijala(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT ns.RepromaterijalBarkod AS Barkod,
               MAX(ns.Naziv) AS Naziv,
               MAX(ns.Jm) AS Jm,
               CAST(SUM(s.IZLAZ * ns.Kolicina) AS DECIMAL(18,3)) AS Kolicina,
               CAST(SUM(s.IZLAZ * ns.Kolicina * ns.NabavnaCijena) AS DECIMAL(18,2)) AS Vrijednost,
               COUNT(DISTINCT s.BARKOD) AS BrojArtikala
        FROM s
        JOIN [Analitika].dbo.Normativ n
             ON n.ArtikalSifra = s.BARKOD
            AND s.DATUM >= n.VaziOd
            AND (n.VaziDo IS NULL OR s.DATUM <= n.VaziDo)
        JOIN [Analitika].dbo.NormativStavka ns ON ns.NormativId = n.Id
        GROUP BY ns.RepromaterijalBarkod
        ORDER BY Kolicina DESC
        """;

    /// <summary>
    /// Koliko je koji artikal doprinio utrošku jednog repromaterijala — za razlaganje reda.
    /// </summary>
    public static string UtrosakPoArtiklu(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT s.BARKOD AS Sifra,
               MAX(RTRIM(s.NAZIV)) AS Naziv,
               CAST(SUM(s.IZLAZ) AS DECIMAL(18,3)) AS Kolicina,
               CAST(SUM(s.IZLAZ * ns.Kolicina) AS DECIMAL(18,3)) AS Utroseno
        FROM s
        JOIN [Analitika].dbo.Normativ n
             ON n.ArtikalSifra = s.BARKOD
            AND s.DATUM >= n.VaziOd
            AND (n.VaziDo IS NULL OR s.DATUM <= n.VaziDo)
        JOIN [Analitika].dbo.NormativStavka ns ON ns.NormativId = n.Id
        WHERE ns.RepromaterijalBarkod = @barkod
        GROUP BY s.BARKOD
        ORDER BY Utroseno DESC
        """;

    /// <summary>
    /// Dnevna prodana količina po artiklu, ograničena na artikle koji imaju recept —
    /// ulaz za projekciju potrošnje zaliha.
    /// </summary>
    public static string DnevnaProdajaSaNormativom(Izvori izvori) => $"""
        WITH s AS (
        {IzvorStavki(izvori)}
        )
        SELECT s.DATUM AS Datum, s.BARKOD AS Sifra, CAST(SUM(s.IZLAZ) AS DECIMAL(18,3)) AS Kolicina
        FROM s
        WHERE EXISTS (
            SELECT 1 FROM [Analitika].dbo.Normativ n
            WHERE n.ArtikalSifra = s.BARKOD AND n.VaziDo IS NULL
        )
        GROUP BY s.DATUM, s.BARKOD
        ORDER BY s.DATUM
        """;

    // ---------------------------------------------------------------------
    // Šifarnici i zalihe (uvijek iz najnovije baze)
    // ---------------------------------------------------------------------

    public static string Artikli(string baza) => $"""
        SELECT r.SIFRA AS Sifra, RTRIM(r.NAZIV) AS Naziv,
               NULLIF(RTRIM(g.NAZIV), '') AS Grupa,
               NULLIF(RTRIM(r.JM), '') AS Jm,
               CAST(ISNULL(r.PCIJENA, 0) AS DECIMAL(18,2)) AS Cijena
        FROM {Kv(baza)}.dbo.Robe r
        LEFT JOIN {Kv(baza)}.dbo.Grupe_art g ON g.SIF = r.GRUPA
        ORDER BY RTRIM(r.NAZIV)
        """;

    public static string Repromaterijali(string baza) => $"""
        SELECT BARKOD, RTRIM(NAZIV) AS Naziv, NULLIF(RTRIM(JM), '') AS Jm,
               CAST(ISNULL(NAB_CIJENA, 0) AS DECIMAL(18,4)) AS NabavnaCijena
        FROM {Kv(baza)}.dbo.Repromaterijali
        ORDER BY RTRIM(NAZIV)
        """;

    /// <summary>
    /// Stanje zaliha — ista logika kao postojeći view <c>Lager_repromaterijala</c>
    /// (ULAZ − IZLAZ iz Robno_stavke, bez vrste 'S'), ali bez oslanjanja na taj view
    /// da ostane isto ponašanje i u starijim bazama gdje ga možda nema.
    /// </summary>
    public static string StanjeRepromaterijala(string baza) => $"""
        SELECT r.BARKOD, RTRIM(r.NAZIV) AS Naziv, NULLIF(RTRIM(r.JM), '') AS Jm,
               CAST(ISNULL(SUM(rs.ULAZ - rs.IZLAZ), 0) AS DECIMAL(18,3)) AS Stanje,
               CAST(CASE WHEN ISNULL(SUM(rs.NAB_IZNOS), 0) < 0 THEN 0
                         ELSE ISNULL(SUM(rs.NAB_IZNOS), 0) END AS DECIMAL(18,2)) AS Saldo
        FROM {Kv(baza)}.dbo.Repromaterijali r
        LEFT JOIN {Kv(baza)}.dbo.Robno_stavke rs ON rs.BARKOD = r.BARKOD AND rs.VRS <> 'S'
        GROUP BY r.BARKOD, RTRIM(r.NAZIV), NULLIF(RTRIM(r.JM), '')
        ORDER BY RTRIM(r.NAZIV)
        """;
}
