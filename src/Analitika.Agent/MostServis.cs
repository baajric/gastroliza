using Analitika.Agent.Queries;
using Analitika.Shared.Upiti;
using Microsoft.AspNetCore.SignalR.Client;

namespace Analitika.Agent;

/// <summary>
/// Drži otvorenu vezu prema serveru i na zahtjev izvršava upite s bijele liste
/// nad lokalnom POS bazom. Veza je izlazna, pa na ruteru klijenta ne treba ništa otvarati.
/// </summary>
public sealed class MostServis(
    IConfiguration konfig,
    Izvrsilac izvrsilac,
    IKljucCuvar kljucCuvar,
    ILogger<MostServis> log) : BackgroundService
{
    private HubConnection? _veza;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kljuc = kljucCuvar.Ucitaj();
        if (string.IsNullOrWhiteSpace(kljuc))
        {
            log.LogError("Most nije uparen. Pokreni 'Analitika.Agent.exe upari <kod>' sa kodom iz web aplikacije.");
            return;
        }

        var adresa = konfig["Most:AdresaServera"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Nedostaje Most:AdresaServera.");

        // Samo za razvoj protiv localhosta sa self-signed certifikatom.
        var dozvoliNepouzdanCert = konfig.GetValue("Most:DozvoliNepouzdanCert", false);
        if (dozvoliNepouzdanCert)
            log.LogWarning("Provjera certifikata je isključena — ovo smije samo u razvoju.");

        _veza = new HubConnectionBuilder()
            .WithUrl($"{adresa}/hub/most", opcije =>
            {
                opcije.Headers["X-Analitika-Kljuc"] = kljuc;

                if (!dozvoliNepouzdanCert) return;

                opcije.HttpMessageHandlerFactory = h =>
                    h is HttpClientHandler hch
                        ? new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }
                        : h;
                opcije.WebSocketConfiguration = ws =>
                    ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            })
            .WithAutomaticReconnect(new StalnoPonovo(
                TimeSpan.FromSeconds(konfig.GetValue("Most:IntervalPonovnogSpajanjaSek", 10))))
            .Build();

        // Ovo je jedini ulaz sa servera: ime upita + parametri. SQL se ne prima.
        _veza.On<ZahtjevUpita, OdgovorUpita>("IzvrsiUpit", async zahtjev =>
        {
            log.LogInformation("Zahtjev: {Upit} ({Od} – {Do})", zahtjev.Upit, zahtjev.OdDatuma, zahtjev.DoDatuma);
            return await izvrsilac.IzvrsiAsync(zahtjev, stoppingToken);
        });

        _veza.Reconnected += id => { log.LogInformation("Veza sa serverom obnovljena"); return Task.CompletedTask; };
        _veza.Closed += greska =>
        {
            log.LogWarning(greska, "Veza sa serverom je prekinuta");
            return Task.CompletedTask;
        };

        // Baza se zagrijava usporedo sa spajanjem — dok se veza uspostavlja,
        // SQL Server već gradi planove, pa prvi korisnikov upit ne čeka.
        var zagrijavanje = izvrsilac.ZagrijAsync(stoppingToken);

        await SpajajDokNeUspijeAsync(adresa, stoppingToken);
        await zagrijavanje;
    }

    private async Task SpajajDokNeUspijeAsync(string adresa, CancellationToken ct)
    {
        var pauza = TimeSpan.FromSeconds(konfig.GetValue("Most:IntervalPonovnogSpajanjaSek", 10));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _veza!.StartAsync(ct);
                log.LogInformation("Most je na vezi sa {Adresa}", adresa);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Server možda još nije podignut ili nema interneta — nema panike, pokušavamo ponovo.
                log.LogWarning("Spajanje nije uspjelo ({Razlog}); ponovo za {Sek}s", ex.Message, pauza.TotalSeconds);
                await Task.Delay(pauza, ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_veza is not null) await _veza.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Ponovno spajanje bez odustajanja — restoran može biti danima offline.</summary>
    private sealed class StalnoPonovo(TimeSpan razmak) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => razmak;
    }
}
