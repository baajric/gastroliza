using System.Text.Json;
using Analitika.Shared.Upiti;
using Microsoft.AspNetCore.SignalR;

namespace Analitika.Server.Mostovi;

/// <summary>Most restorana nije na vezi — računar je ugašen ili nema interneta.</summary>
public sealed class MostNijeNaVeziException(string naziv)
    : Exception($"Restoran „{naziv}\" trenutno nije na vezi.");

/// <summary>Upit je stigao do klijenta, ali tamo nije uspio.</summary>
public sealed class UpitException(string poruka) : Exception(poruka);

/// <summary>
/// Odavde stranice postavljaju pitanja mostu. Rezultat prolazi kroz server samo
/// u memoriji — nigdje se ne zapisuje.
/// </summary>
public sealed class MostKlijent(
    IHubContext<MostHub> hub,
    MostRegistar registar,
    IKljucStore kljucevi,
    ILogger<MostKlijent> log)
{
    /// <summary>
    /// Koliko čekamo klijenta prije nego odustanemo — njegov računar može biti spor.
    /// Mora biti duže od roka koji most postavlja na sam SQL upit, inače server
    /// odustane prije nego klijent stigne javiti pravu grešku.
    /// </summary>
    private static readonly TimeSpan Strpljenje = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public bool JeNaVezi(Guid restoran) => registar.JeNaVezi(restoran);

    public async Task<T> PitajAsync<T>(Guid restoran, ZahtjevUpita zahtjev, CancellationToken ct = default)
    {
        var idVeze = registar.IdVeze(restoran)
            ?? throw new MostNijeNaVeziException(kljucevi.PronadjiPoId(restoran)?.Naziv ?? restoran.ToString());

        using var istek = CancellationTokenSource.CreateLinkedTokenSource(ct);
        istek.CancelAfter(Strpljenje);

        OdgovorUpita odgovor;
        try
        {
            odgovor = await hub.Clients.Client(idVeze)
                .InvokeAsync<OdgovorUpita>("IzvrsiUpit", zahtjev, istek.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpitException("Restoran nije odgovorio na vrijeme.");
        }
        catch (IOException)
        {
            // Veza je pukla usred upita.
            throw new MostNijeNaVeziException(kljucevi.PronadjiPoId(restoran)?.Naziv ?? restoran.ToString());
        }

        if (!odgovor.Uspjeh)
            throw new UpitException(odgovor.Greska ?? "Nepoznata greška kod klijenta.");

        log.LogInformation("Upit {Upit} za {Restoran} trajao {Ms} ms kod klijenta",
            zahtjev.Upit, restoran, odgovor.TrajanjeMs);

        return JsonSerializer.Deserialize<T>(odgovor.PodaciJson ?? "null", Json)
            ?? throw new UpitException("Prazan odgovor od klijenta.");
    }
}
