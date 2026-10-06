using Analitika.Shared.Upiti;

namespace Analitika.Server.Mostovi;

public static class Dijagnostika
{
    /// <summary>
    /// Provjera da put server → most → SQL radi. Vraća poslovne podatke, pa je
    /// namjerno ograničena na razvojno okruženje.
    /// </summary>
    public static void MapDijagnostiku(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapGet("/dev/most", (MostRegistar registar, IKljucStore kljucevi) =>
            Results.Ok(kljucevi.Svi().Select(r => new
            {
                r.Id,
                r.Naziv,
                NaVezi = registar.JeNaVezi(r.Id),
                Od = registar.NaVeziOd(r.Id)
            })));

        app.MapGet("/dev/sazetak", async (
            Guid restoran, DateOnly od, DateOnly doDatuma,
            MostKlijent most, CancellationToken ct) =>
        {
            var baze = await most.PitajAsync<List<BazaInfo>>(restoran,
                new ZahtjevUpita { Upit = ImeUpita.PopisBaza }, ct);

            var sazetak = await most.PitajAsync<Sazetak>(restoran, new ZahtjevUpita
            {
                Upit = ImeUpita.Sazetak,
                OdDatuma = od,
                DoDatuma = doDatuma,
                Baze = baze.Select(b => b.Naziv).ToList()
            }, ct);

            return Results.Ok(new { baze, sazetak });
        });
    }
}
