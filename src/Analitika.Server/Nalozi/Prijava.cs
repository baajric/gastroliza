using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Analitika.Server.Mostovi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace Analitika.Server.Nalozi;

/// <summary>Šta most šalje pri uparivanju, i šta dobija nazad.</summary>
public sealed record ZahtjevUparivanja(string Kod, string? Racunar);
public sealed record OdgovorUparivanja(Guid Restoran, string Naziv, string Kljuc);

/// <summary>Jednokratno preuzimanje paketa: veže token iz linka za kod i restoran.</summary>
public sealed record PaketZaPreuzimanje(Guid Restoran, long Korisnik, string Kod);

public static class Prijava
{
    public const string TvrdnjaRestorana = "restoran";

    public static ClaimsPrincipal Napravi(Korisnik k, IEnumerable<Restoran> restorani)
    {
        var tvrdnje = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, k.Id.ToString()),
            new(ClaimTypes.Name, k.Email),
            new(ClaimTypes.GivenName, k.Ime)
        };
        tvrdnje.AddRange(restorani.Select(r => new Claim(TvrdnjaRestorana, r.Id.ToString())));
        return new ClaimsPrincipal(new ClaimsIdentity(tvrdnje, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static long? IdKorisnika(ClaimsPrincipal korisnik) =>
        long.TryParse(korisnik.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? PrviRestoran(ClaimsPrincipal korisnik) =>
        Guid.TryParse(korisnik.FindFirstValue(TvrdnjaRestorana), out var id) ? id : null;

    /// <summary>Povratak poslije prijave samo na vlastite stranice — nikad na tuđu adresu iz linka.</summary>
    public static string SiguranPovratak(string? adresa) =>
        !string.IsNullOrEmpty(adresa) && adresa.StartsWith('/') && !adresa.StartsWith("//") && !adresa.StartsWith("/\\")
            ? adresa
            : "/";

    public static void MapNaloge(this WebApplication app)
    {
        // Odjava je POST sa antiforgery tokenom — link koji neko podmetne ne može nikog odjaviti.
        app.MapPost("/nalog/odjava", async (HttpContext http, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/nalog/prijava");
        }).DisableAntiforgery();

        // Most mijenja jednokratni kod za trajni ključ. Bez prijave — most nema korisnika,
        // ima samo kod koji je vlasnik dobio na webu. Kočnica sprečava pogađanje kodova.
        app.MapPost("/api/most/upari", (ZahtjevUparivanja zahtjev, ServerBaza baza, ILoggerFactory logovi) =>
        {
            var log = logovi.CreateLogger("Analitika.Uparivanje");
            if (string.IsNullOrWhiteSpace(zahtjev.Kod) || zahtjev.Kod.Length > 20)
                return Results.BadRequest(new { greska = "Kod nije ispravan." });

            var ishod = baza.Upari(zahtjev.Kod, zahtjev.Racunar);
            if (ishod is not { } u)
            {
                log.LogWarning("Neuspješno uparivanje sa računara {Racunar}", zahtjev.Racunar);
                return Results.BadRequest(new { greska = "Kod nije važeći ili je istekao. Napravi novi na stranici „Poveži kasu\"." });
            }

            log.LogInformation("Restoran {Restoran} je uparen sa računarom {Racunar}", u.Restoran.Id, zahtjev.Racunar);
            return Results.Ok(new OdgovorUparivanja(u.Restoran.Id, u.Restoran.Naziv, u.Kljuc));
        }).RequireRateLimiting("uparivanje").DisableAntiforgery();

        // Paket mosta sa upisanim kodom i adresom servera — instalacija ne traži nikakvo kucanje.
        app.MapGet("/most/preuzmi/{token:guid}", (Guid token, HttpContext http, IMemoryCache kes,
            IConfiguration konfig, IWebHostEnvironment okruzenje) =>
        {
            if (!kes.TryGetValue<PaketZaPreuzimanje>(token, out var paket) || paket is null
                || IdKorisnika(http.User) != paket.Korisnik)
                return Results.NotFound("Link za preuzimanje je istekao. Vrati se na „Poveži kasu\" i preuzmi ponovo.");

            var folder = Path.GetFullPath(Path.Combine(okruzenje.ContentRootPath,
                konfig["Most:Paket"] ?? Path.Combine("..", "..", "dist", "most")));
            if (!File.Exists(Path.Combine(folder, "Analitika.Agent.exe")))
                return Results.Problem("Paket mosta nije objavljen na serveru (nedostaje Analitika.Agent.exe).");

            var adresa = konfig["Most:JavnaAdresa"]?.TrimEnd('/') is { Length: > 0 } a
                ? a
                : $"{http.Request.Scheme}://{http.Request.Host}";

            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var fajl in Directory.EnumerateFiles(folder))
                {
                    var ime = Path.GetFileName(fajl);
                    // Simboli za otklanjanje grešaka i stari ručni fajlovi ne idu klijentu.
                    if (ime.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                        || ime.Equals("uparivanje.json", StringComparison.OrdinalIgnoreCase)) continue;
                    zip.CreateEntryFromFile(fajl, ime, CompressionLevel.Optimal);
                }

                var unos = zip.CreateEntry("uparivanje.json");
                using var pisac = new StreamWriter(unos.Open());
                pisac.Write(JsonSerializer.Serialize(new { AdresaServera = adresa, Kod = paket.Kod },
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            ms.Position = 0;
            return Results.File(ms, "application/zip", "Gastroliza-Most.zip");
        }).RequireAuthorization();
    }
}

/// <summary>
/// Kočnica za prijavu: poslije pet pogrešnih lozinki za isti email (ili sa iste adrese)
/// sljedeći pokušaj čeka 15 minuta. Pamti se u memoriji — restart je briše, što je
/// prihvatljivo, a ne treba posebna tabela.
/// </summary>
public sealed class KocnicaPrijave
{
    private const int NajviseGresaka = 5;
    private static readonly TimeSpan Pauza = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (int Broj, DateTime Do)> _greske = new();

    public bool JeZakocen(string email, string? adresa) =>
        Zakocen("e:" + email.ToLowerInvariant()) || (adresa is not null && Zakocen("a:" + adresa));

    public void Greska(string email, string? adresa)
    {
        Dodaj("e:" + email.ToLowerInvariant());
        if (adresa is not null) Dodaj("a:" + adresa);
    }

    public void Uspjeh(string email) => _greske.TryRemove("e:" + email.ToLowerInvariant(), out _);

    private bool Zakocen(string kljuc) =>
        _greske.TryGetValue(kljuc, out var g) && g.Broj >= NajviseGresaka && g.Do > DateTime.UtcNow;

    private void Dodaj(string kljuc) => _greske.AddOrUpdate(kljuc,
        _ => (1, DateTime.UtcNow.Add(Pauza)),
        (_, g) => g.Do < DateTime.UtcNow ? (1, DateTime.UtcNow.Add(Pauza)) : (g.Broj + 1, DateTime.UtcNow.Add(Pauza)));
}
