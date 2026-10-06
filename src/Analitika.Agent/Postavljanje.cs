using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace Analitika.Agent;

/// <summary>
/// Postavke ovog računara: adresa servera i veza na SQL Server. Stoje u ProgramData,
/// odvojeno od programa — nova verzija mosta se raspakuje preko stare bez gubitka postavki.
/// </summary>
public static class MasinskePostavke
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Analitika");

    public static string Putanja => Path.Combine(Folder, "most.json");

    /// <summary>Upisuje jednu vrijednost (npr. "Most:AdresaServera"), ostale ostaju.</summary>
    public static void Upisi(string kljuc, string vrijednost)
    {
        Directory.CreateDirectory(Folder);
        var korijen = File.Exists(Putanja)
            ? JsonNode.Parse(File.ReadAllText(Putanja)) as JsonObject ?? []
            : [];

        var dijelovi = kljuc.Split(':');
        var cvor = korijen;
        foreach (var dio in dijelovi[..^1])
        {
            if (cvor[dio] is not JsonObject dijete) cvor[dio] = dijete = [];
            cvor = dijete;
        }
        cvor[dijelovi[^1]] = vrijednost;

        File.WriteAllText(Putanja, korijen.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Uparivanje: jednokratni kod sa web stranice se mijenja za trajni ključ.
/// Ključ ne vidi ni korisnik ni browser — ide direktno sa servera u DPAPI na ovom računaru.
/// </summary>
public static class Uparivanje
{
    private sealed record Odgovor(Guid Restoran, string Naziv, string Kljuc);

    public static async Task<int> PokreniAsync(string kod, string? server, IConfiguration konfig, IKljucCuvar cuvar)
    {
        var adresa = (server ?? konfig["Most:AdresaServera"])?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(adresa))
        {
            Console.Error.WriteLine("Nije poznata adresa servera. Pokreni: Analitika.Agent.exe upari <kod> <adresa-servera>");
            return 2;
        }

        using var handler = new HttpClientHandler();
        if (konfig.GetValue("Most:DozvoliNepouzdanCert", false))
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        HttpResponseMessage odgovor;
        try
        {
            odgovor = await http.PostAsJsonAsync($"{adresa}/api/most/upari",
                new { Kod = kod.Trim(), Racunar = Environment.MachineName });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server {adresa} nije dostupan: {ex.Message}");
            return 3;
        }

        if (!odgovor.IsSuccessStatusCode)
        {
            var tekst = await odgovor.Content.ReadAsStringAsync();
            var poruka = TryGreska(tekst) ?? $"{(int)odgovor.StatusCode} {odgovor.ReasonPhrase}";
            Console.Error.WriteLine($"Uparivanje nije uspjelo: {poruka}");
            return 4;
        }

        var rezultat = await odgovor.Content.ReadFromJsonAsync<Odgovor>()
            ?? throw new InvalidOperationException("Server je vratio prazan odgovor.");

        cuvar.Snimi(rezultat.Kljuc);
        MasinskePostavke.Upisi("Most:AdresaServera", adresa);
        Console.WriteLine($"Most je uparen sa restoranom „{rezultat.Naziv}\".");
        return 0;
    }

    private static string? TryGreska(string json)
    {
        try { return JsonNode.Parse(json)?["greska"]?.GetValue<string>(); }
        catch { return null; }
    }
}

/// <summary>
/// Daje servisu mosta pravo da čita POS baze. Pokreće ga instalacija, pod Windows
/// nalogom korisnika koji je administrator SQL Servera (isti kojim otvara SSMS).
///
/// Servis radi pod vlastitim virtuelnim nalogom („NT SERVICE\Analitika Most"), a ne
/// kao LocalSystem — na SQL Serveru 2012+ LocalSystem nema pristup bazama kase.
/// Nalog dobija samo čitanje svih baza i vlasništvo nad bazom Analitika.
/// </summary>
public static class PristupSql
{
    public static async Task<int> PokreniAsync(string nalog)
    {
        var instance = PronadjiInstance();
        Console.WriteLine($"SQL Server instance na ovom računaru: {string.Join(", ", instance)}");

        foreach (var server in instance)
        {
            var vezna = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 10
            };

            try
            {
                await using var veza = new SqlConnection(vezna.ConnectionString);
                await veza.OpenAsync();

                var baze = await BrojEtisBazaAsync(veza);
                if (baze == 0)
                {
                    Console.WriteLine($"  {server}: nema EtisRpos baza, preskačem.");
                    continue;
                }

                Console.WriteLine($"  {server}: pronađeno {baze} EtisRpos baza.");
                await DodijeliAsync(veza, nalog);

                MasinskePostavke.Upisi("ConnectionStrings:PosBaza", new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    InitialCatalog = "master",
                    IntegratedSecurity = true,
                    TrustServerCertificate = true,
                    ApplicationName = "Analitika Most",
                    ConnectTimeout = 10
                }.ConnectionString);

                Console.WriteLine($"Most će čitati bazu kase sa {server}.");
                return 0;
            }
            catch (SqlException ex) when (ex.Number is 229 or 15247 or 262 or 1031 or 15151)
            {
                Console.Error.WriteLine($"  {server}: tvoj Windows nalog nema prava da dodijeli pristup ({ex.Message}).");
                Console.Error.WriteLine("  Pokreni instalaciju nalogom koji je administrator SQL Servera (onim kojim otvaraš SSMS).");
                return 5;
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"  {server}: nije dostupan ({ex.Message}).");
            }
        }

        Console.Error.WriteLine("Nije pronađen SQL Server sa EtisRpos bazama na ovom računaru.");
        return 6;
    }

    /// <summary>
    /// Instance iz registra: podrazumijevana (MSSQLSERVER) je „localhost",
    /// imenovana je „localhost\IME" (npr. SQLEXPRESS).
    /// </summary>
    private static List<string> PronadjiInstance()
    {
        var rezultat = new List<string>();
        foreach (var pogled in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var korijen = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, pogled);
            using var kljuc = korijen.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            if (kljuc is null) continue;
            foreach (var ime in kljuc.GetValueNames())
            {
                var server = ime.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "localhost" : $@"localhost\{ime}";
                if (!rezultat.Contains(server, StringComparer.OrdinalIgnoreCase)) rezultat.Add(server);
            }
        }
        if (rezultat.Count == 0) rezultat.Add("localhost");
        return rezultat;
    }

    private static async Task<int> BrojEtisBazaAsync(SqlConnection veza)
    {
        await using var k = veza.CreateCommand();
        k.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'EtisRpos[_]%' AND state = 0;";
        return (int)(await k.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Na SQL Serveru 2014+ nalog dobija CONNECT ANY DATABASE i SELECT ALL USER SECURABLES —
    /// čitanje svih baza, i onih koje kasa tek otvori za novu godinu. Na starijim verzijama
    /// dobija db_datareader u svakoj postojećoj EtisRpos bazi. Pisati smije samo u Analitika.
    /// </summary>
    public static async Task DodijeliAsync(SqlConnection veza, string nalog)
    {
        var n = nalog.Replace("]", "]]");
        var l = nalog.Replace("'", "''");

        await IzvrsiAsync(veza, $"""
            IF SUSER_ID(N'{l}') IS NULL CREATE LOGIN [{n}] FROM WINDOWS;
            IF DB_ID('Analitika') IS NULL CREATE DATABASE [Analitika];
            """);

        await IzvrsiAsync(veza, $"""
            USE [Analitika];
            IF USER_ID(N'{l}') IS NULL CREATE USER [{n}] FOR LOGIN [{n}];
            ALTER ROLE db_owner ADD MEMBER [{n}];
            """);

        await using var verzija = veza.CreateCommand();
        verzija.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);";
        var glavna = (int)(await verzija.ExecuteScalarAsync() ?? 0);

        if (glavna >= 12)
        {
            await IzvrsiAsync(veza, $"""
                USE [master];
                GRANT CONNECT ANY DATABASE TO [{n}];
                GRANT SELECT ALL USER SECURABLES TO [{n}];
                """);
            Console.WriteLine($"  {nalog}: čitanje svih baza (i budućih godina), pisanje samo u Analitika.");
            return;
        }

        var baze = new List<string>();
        await using (var k = veza.CreateCommand())
        {
            k.CommandText = "SELECT name FROM sys.databases WHERE name LIKE 'EtisRpos[_]%' AND state = 0;";
            await using var c = await k.ExecuteReaderAsync();
            while (await c.ReadAsync()) baze.Add(c.GetString(0));
        }
        foreach (var baza in baze)
        {
            await IzvrsiAsync(veza, $"""
                USE [{baza.Replace("]", "]]")}];
                IF USER_ID(N'{l}') IS NULL CREATE USER [{n}] FOR LOGIN [{n}];
                ALTER ROLE db_datareader ADD MEMBER [{n}];
                """);
        }
        Console.WriteLine($"  {nalog}: čitanje {baze.Count} EtisRpos baza (stari SQL Server — nova godina traži ponovno pokretanje instalacije).");
    }

    private static async Task IzvrsiAsync(SqlConnection veza, string sql)
    {
        await using var k = veza.CreateCommand();
        k.CommandText = sql;
        k.CommandTimeout = 120;
        await k.ExecuteNonQueryAsync();
    }
}
