using System.Collections.Concurrent;
using System.Text.Json;
using Analitika.Server.Mostovi;
using Analitika.Shared.Upiti;

namespace Analitika.Server.Servisi;

/// <summary>
/// Postavke restorana (ime, logo, imena konobara) žive u lokalnoj bazi kod klijenta,
/// kao i normativi. Server ih drži samo u memoriji, da zaglavlje i rang lista ne pitaju
/// most pri svakom iscrtavanju. Kad most nije na vezi, koristi se zadnje poznato stanje.
/// </summary>
public sealed class PostavkeRestorana(MostKlijent most, ILogger<PostavkeRestorana> log)
{
    private readonly ConcurrentDictionary<Guid, Postavke> _kes = new();

    /// <summary>Javlja se kad se postavke nekog restorana promijene — zaglavlje se osvježi.</summary>
    public event Action<Guid>? Promijenjene;

    /// <summary>Zadnje poznate postavke, bez odlaska do mosta.</summary>
    public Postavke? Poznate(Guid restoran) => _kes.GetValueOrDefault(restoran);

    /// <summary>Učitava sa mosta ako još nisu učitane; greška nije kobna — vraća zadnje poznato.</summary>
    public async Task<Postavke> UcitajAsync(Guid restoran, bool iznova = false, CancellationToken ct = default)
    {
        if (!iznova && _kes.TryGetValue(restoran, out var poznate)) return poznate;
        if (!most.JeNaVezi(restoran)) return _kes.GetValueOrDefault(restoran) ?? new Postavke();

        try
        {
            var postavke = await most.PitajAsync<Postavke>(restoran,
                new ZahtjevUpita { Upit = ImeUpita.PostavkeUcitaj }, ct);
            _kes[restoran] = postavke;
            Promijenjene?.Invoke(restoran);
            return postavke;
        }
        catch (Exception ex) when (ex is UpitException or MostNijeNaVeziException)
        {
            log.LogWarning(ex, "Postavke za {Restoran} se ne mogu učitati", restoran);
            return _kes.GetValueOrDefault(restoran) ?? new Postavke();
        }
    }

    public async Task SnimiAsync(Guid restoran, Postavke postavke, CancellationToken ct = default)
    {
        await most.PitajAsync<bool>(restoran, new ZahtjevUpita
        {
            Upit = ImeUpita.PostavkeSnimi,
            TeretJson = JsonSerializer.Serialize(postavke)
        }, ct);
        _kes[restoran] = postavke;
        Promijenjene?.Invoke(restoran);
    }

    /// <summary>Ime za prikaz: upisano u postavkama, inače naziv iz uparivanja mosta.</summary>
    public string Naziv(Restoran restoran) =>
        Poznate(restoran.Id)?.NazivRestorana is { Length: > 0 } n ? n : restoran.Naziv;

    /// <summary>Inicijali za krug kad logo nije postavljen: „Gradska kafana" → „GK".</summary>
    public static string Inicijali(string naziv)
    {
        var rijeci = naziv.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => char.IsLetterOrDigit(r[0]))
            .ToList();
        return rijeci.Count switch
        {
            0 => "?",
            1 => rijeci[0][..Math.Min(2, rijeci[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(rijeci[0][0]), char.ToUpperInvariant(rijeci[1][0]))
        };
    }

    /// <summary>Primjenjuje prikazna imena na rang listu konobara; kasa ostaje netaknuta.</summary>
    public static List<RedKonobara> Preimenuj(List<RedKonobara> konobari, Postavke postavke) =>
        postavke.ImenaKonobara.Count == 0
            ? konobari
            : [.. konobari.Select(k => postavke.ImenaKonobara.TryGetValue(k.IdKartice, out var ime) && !string.IsNullOrWhiteSpace(ime)
                ? k with { Ime = ime.Trim() }
                : k)];
}
