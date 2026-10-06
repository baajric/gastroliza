using Microsoft.AspNetCore.SignalR;

namespace Analitika.Server.Mostovi;

/// <summary>
/// Tačka na koju se mostovi spajaju. Veza ide iznutra prema van — most zove server,
/// pa kod klijenta nema otvorenih portova.
/// </summary>
public sealed class MostHub(IKljucStore kljucevi, MostRegistar registar, ILogger<MostHub> log) : Hub
{
    public const string Putanja = "/hub/most";
    public const string ZaglavljeKljuca = "X-Analitika-Kljuc";

    public override async Task OnConnectedAsync()
    {
        var kljuc = Context.GetHttpContext()?.Request.Headers[ZaglavljeKljuca].ToString();
        var restoran = kljuc is null ? null : kljucevi.PronadjiPoKljucu(kljuc);

        if (restoran is null)
        {
            log.LogWarning("Odbijena veza mosta bez ispravnog ključa ({Veza})", Context.ConnectionId);
            Context.Abort();
            return;
        }

        registar.Prijavi(restoran.Id, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? greska)
    {
        registar.Odjavi(Context.ConnectionId);
        return base.OnDisconnectedAsync(greska);
    }
}
