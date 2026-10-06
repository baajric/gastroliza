using System.Security.Cryptography;
using Analitika.Server.Mostovi;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Analitika.Server.Nalozi;

public sealed record Korisnik(long Id, string Email, string Ime);

/// <summary>Stanje uparenosti mosta jednog restorana — za stranicu „Poveži kasu".</summary>
public sealed record UparenostMosta(DateTime? Upareno, string? Racunar);

/// <summary>
/// Vlastita baza servera: nalozi, restorani i hash ključeva mostova.
/// Prodajni podaci ovdje nikad ne ulaze — oni ostaju u kasi klijenta.
///
/// SQLite je dovoljan: piše se samo pri registraciji i uparivanju, a čita pri prijavi
/// i spajanju mosta. Fajl se pravi sam pri prvom pokretanju.
/// </summary>
public sealed class ServerBaza
{
    private readonly string _veznaNiska;
    private readonly PasswordHasher<Korisnik> _hasher = new();

    /// <summary>Koliko dugo vrijedi kod za uparivanje — dovoljno za preuzimanje i instalaciju.</summary>
    public static readonly TimeSpan TrajanjeKoda = TimeSpan.FromMinutes(30);

    /// <summary>Slova bez onih koja se brkaju (0/O, 1/I/L) — kod se zna i prepisivati.</summary>
    private const string Abeceda = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public ServerBaza(IConfiguration konfig, IWebHostEnvironment okruzenje)
    {
        var putanja = konfig.GetConnectionString("Server") is { Length: > 0 } n
            ? n
            : $"Data Source={Path.Combine(okruzenje.ContentRootPath, "App_Data", "gastroliza.db")}";

        var graditelj = new SqliteConnectionStringBuilder(putanja);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(graditelj.DataSource))!);
        _veznaNiska = graditelj.ToString();
    }

    private SqliteConnection Veza()
    {
        var veza = new SqliteConnection(_veznaNiska);
        veza.Open();
        veza.Execute("PRAGMA foreign_keys = ON;");
        return veza;
    }

    public void Pripremi()
    {
        using var veza = Veza();
        veza.Execute("""
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Korisnik (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Email       TEXT NOT NULL UNIQUE COLLATE NOCASE,
                Ime         TEXT NOT NULL,
                LozinkaHash TEXT NOT NULL,
                Kreiran     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Restoran (
                Id        TEXT PRIMARY KEY,
                Naziv     TEXT NOT NULL,
                VlasnikId INTEGER NOT NULL REFERENCES Korisnik(Id),
                Kreiran   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Restoran_Vlasnik ON Restoran (VlasnikId);

            -- Jedan važeći ključ po restoranu; novo uparivanje zamjenjuje stari.
            -- Čuva se samo SHA-256 ključa — sam ključ zna samo most.
            CREATE TABLE IF NOT EXISTS KljucMosta (
                RestoranId TEXT PRIMARY KEY REFERENCES Restoran(Id),
                Hash       TEXT NOT NULL UNIQUE,
                Racunar    TEXT NULL,
                Upareno    TEXT NOT NULL
            );

            -- Jednokratni kodovi za uparivanje, isto samo kao hash.
            CREATE TABLE IF NOT EXISTS KodUparivanja (
                Hash       TEXT PRIMARY KEY,
                RestoranId TEXT NOT NULL REFERENCES Restoran(Id),
                Istice     TEXT NOT NULL
            );
            """);
    }

    // ------------------------------------------------------------------
    // Nalozi
    // ------------------------------------------------------------------

    /// <summary>Pravi nalog i njegov prvi restoran. Vraća null ako email već postoji.</summary>
    public (Korisnik Korisnik, Restoran Restoran)? Registruj(string email, string ime, string lozinka, string nazivRestorana)
    {
        using var veza = Veza();
        using var tx = veza.BeginTransaction();

        if (veza.ExecuteScalar<long>("SELECT COUNT(*) FROM Korisnik WHERE Email = @email;", new { email }, tx) > 0)
            return null;

        var privremeni = new Korisnik(0, email, ime);
        var id = veza.ExecuteScalar<long>("""
            INSERT INTO Korisnik (Email, Ime, LozinkaHash, Kreiran) VALUES (@email, @ime, @hash, @sad);
            SELECT last_insert_rowid();
            """, new { email, ime, hash = _hasher.HashPassword(privremeni, lozinka), sad = Sad() }, tx);

        var restoran = new Restoran(Guid.NewGuid(), nazivRestorana);
        veza.Execute("INSERT INTO Restoran (Id, Naziv, VlasnikId, Kreiran) VALUES (@id, @naziv, @vlasnik, @sad);",
            new { id = restoran.Id.ToString(), naziv = restoran.Naziv, vlasnik = id, sad = Sad() }, tx);

        tx.Commit();
        return (privremeni with { Id = id }, restoran);
    }

    /// <summary>Provjerava lozinku. Stari hash se usput prepiše jačim kad ga biblioteka pojača.</summary>
    public Korisnik? Prijavi(string email, string lozinka)
    {
        using var veza = Veza();
        var red = veza.QuerySingleOrDefault<KorisnikRed>(
            "SELECT Id, Email, Ime, LozinkaHash FROM Korisnik WHERE Email = @email;", new { email });

        // Bez korisnika se ipak računa jedan hash, da trajanje odgovora ne oda postoji li email.
        if (red is null)
        {
            _hasher.VerifyHashedPassword(new Korisnik(0, email, ""), LazniHash, lozinka);
            return null;
        }

        var korisnik = new Korisnik(red.Id, red.Email, red.Ime);
        var ishod = _hasher.VerifyHashedPassword(korisnik, red.LozinkaHash, lozinka);
        if (ishod == PasswordVerificationResult.Failed) return null;

        if (ishod == PasswordVerificationResult.SuccessRehashNeeded)
            veza.Execute("UPDATE Korisnik SET LozinkaHash = @h WHERE Id = @id;",
                new { h = _hasher.HashPassword(korisnik, lozinka), id = red.Id });

        return korisnik;
    }

    private static readonly string LazniHash = new PasswordHasher<Korisnik>().HashPassword(new Korisnik(0, "", ""), "lazna");

    public List<Restoran> RestoraniKorisnika(long korisnik)
    {
        using var veza = Veza();
        return [.. veza.Query<RestoranRed>(
                "SELECT Id, Naziv FROM Restoran WHERE VlasnikId = @korisnik ORDER BY Kreiran;", new { korisnik })
            .Select(r => new Restoran(Guid.Parse(r.Id), r.Naziv))];
    }

    public Restoran? Restoran(Guid id)
    {
        using var veza = Veza();
        var r = veza.QuerySingleOrDefault<RestoranRed>("SELECT Id, Naziv FROM Restoran WHERE Id = @id;", new { id = id.ToString() });
        return r is null ? null : new Restoran(Guid.Parse(r.Id), r.Naziv);
    }

    public bool JeVlasnik(long korisnik, Guid restoran)
    {
        using var veza = Veza();
        return veza.ExecuteScalar<long>("SELECT COUNT(*) FROM Restoran WHERE Id = @r AND VlasnikId = @k;",
            new { r = restoran.ToString(), k = korisnik }) > 0;
    }

    /// <summary>Razvojni nalog vezan za restoran iz konfiguracije — da lokalni most radi i poslije uvođenja prijave.</summary>
    public void OsigurajRazvojniNalog(string email, string lozinka, Restoran restoran)
    {
        using var veza = Veza();
        using var tx = veza.BeginTransaction();
        var id = veza.ExecuteScalar<long?>("SELECT Id FROM Korisnik WHERE Email = @email;", new { email }, tx);
        if (id is null)
        {
            id = veza.ExecuteScalar<long>("""
                INSERT INTO Korisnik (Email, Ime, LozinkaHash, Kreiran) VALUES (@email, 'Razvoj', @hash, @sad);
                SELECT last_insert_rowid();
                """, new { email, hash = _hasher.HashPassword(new Korisnik(0, email, ""), lozinka), sad = Sad() }, tx);
        }
        veza.Execute("""
            INSERT INTO Restoran (Id, Naziv, VlasnikId, Kreiran) VALUES (@rid, @naziv, @vlasnik, @sad)
            ON CONFLICT(Id) DO NOTHING;
            """, new { rid = restoran.Id.ToString(), naziv = restoran.Naziv, vlasnik = id, sad = Sad() }, tx);
        tx.Commit();
    }

    // ------------------------------------------------------------------
    // Uparivanje mostova
    // ------------------------------------------------------------------

    /// <summary>
    /// Novi jednokratni kod za restoran, oblika ABCD-EF23. Stari neiskorišteni kodovi
    /// tog restorana se poništavaju — važi samo zadnji.
    /// </summary>
    public (string Kod, DateTime Istice) NoviKod(Guid restoran)
    {
        var znakovi = new char[8];
        for (var i = 0; i < znakovi.Length; i++) znakovi[i] = Abeceda[RandomNumberGenerator.GetInt32(Abeceda.Length)];
        var kod = new string(znakovi, 0, 4) + "-" + new string(znakovi, 4, 4);
        var istice = DateTime.UtcNow.Add(TrajanjeKoda);

        using var veza = Veza();
        veza.Execute("""
            DELETE FROM KodUparivanja WHERE RestoranId = @r OR Istice < @sad;
            INSERT INTO KodUparivanja (Hash, RestoranId, Istice) VALUES (@hash, @r, @istice);
            """, new { r = restoran.ToString(), sad = Sad(), hash = HashKoda(kod), istice = istice.ToString("O") });
        return (kod, istice);
    }

    /// <summary>
    /// Mijenja kod za trajni ključ. Kod se troši odmah, i kad uparivanje uspije i kad
    /// ne — isti kod se ne može iskoristiti dvaput. Vraća ključ samo jednom, mostu.
    /// </summary>
    public (Restoran Restoran, string Kljuc)? Upari(string kod, string? racunar)
    {
        var hash = HashKoda(kod);
        using var veza = Veza();
        using var tx = veza.BeginTransaction();

        var red = veza.QuerySingleOrDefault<KodRed>(
            "SELECT RestoranId, Istice FROM KodUparivanja WHERE Hash = @hash;", new { hash }, tx);
        veza.Execute("DELETE FROM KodUparivanja WHERE Hash = @hash;", new { hash }, tx);

        if (red is null || DateTime.Parse(red.Istice, null, System.Globalization.DateTimeStyles.RoundtripKind) < DateTime.UtcNow)
        {
            tx.Commit();
            return null;
        }

        var kljuc = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        veza.Execute("""
            INSERT INTO KljucMosta (RestoranId, Hash, Racunar, Upareno) VALUES (@r, @hash, @racunar, @sad)
            ON CONFLICT(RestoranId) DO UPDATE SET Hash = excluded.Hash, Racunar = excluded.Racunar, Upareno = excluded.Upareno;
            """, new { r = red.RestoranId, hash = KonfigKljucStore.Hash(kljuc), racunar = racunar?[..Math.Min(100, racunar.Length)], sad = Sad() }, tx);

        var restoran = veza.QuerySingle<RestoranRed>("SELECT Id, Naziv FROM Restoran WHERE Id = @r;", new { r = red.RestoranId }, tx);
        tx.Commit();
        return (new Restoran(Guid.Parse(restoran.Id), restoran.Naziv), kljuc);
    }

    public Restoran? RestoranPoHashuKljuca(string hash)
    {
        using var veza = Veza();
        var r = veza.QuerySingleOrDefault<RestoranRed>("""
            SELECT r.Id, r.Naziv FROM KljucMosta k JOIN Restoran r ON r.Id = k.RestoranId WHERE k.Hash = @hash;
            """, new { hash });
        return r is null ? null : new Restoran(Guid.Parse(r.Id), r.Naziv);
    }

    public UparenostMosta Uparenost(Guid restoran)
    {
        using var veza = Veza();
        var r = veza.QuerySingleOrDefault<UparenostRed>(
            "SELECT Upareno, Racunar FROM KljucMosta WHERE RestoranId = @r;", new { r = restoran.ToString() });
        return new UparenostMosta(
            r?.Upareno is { } u ? DateTime.Parse(u, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
            r?.Racunar);
    }

    /// <summary>Kod se upoređuje bez crtice i razmaka, velikim slovima — prepisan rukom i dalje važi.</summary>
    private static string HashKoda(string kod)
    {
        var cist = new string(kod.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return KonfigKljucStore.Hash("kod:" + cist);
    }

    private static string Sad() => DateTime.UtcNow.ToString("O");

    private sealed class KorisnikRed
    {
        public long Id { get; set; }
        public string Email { get; set; } = "";
        public string Ime { get; set; } = "";
        public string LozinkaHash { get; set; } = "";
    }

    private sealed class RestoranRed
    {
        public string Id { get; set; } = "";
        public string Naziv { get; set; } = "";
    }

    private sealed class KodRed
    {
        public string RestoranId { get; set; } = "";
        public string Istice { get; set; } = "";
    }

    private sealed class UparenostRed
    {
        public string? Upareno { get; set; }
        public string? Racunar { get; set; }
    }
}
