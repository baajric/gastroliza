using System.Collections.Concurrent;

namespace Analitika.Server.Mostovi;

/// <summary>
/// Ko je trenutno na vezi. Jedan restoran = jedan most; nova veza istog restorana
/// zamjenjuje staru (npr. poslije restarta servisa kod klijenta).
/// </summary>
public sealed class MostRegistar(ILogger<MostRegistar> log)
{
    private readonly ConcurrentDictionary<Guid, Veza> _po_restoranu = new();
    private readonly ConcurrentDictionary<string, Guid> _po_vezi = new();

    private sealed record Veza(string IdVeze, DateTimeOffset Od);

    public void Prijavi(Guid restoran, string idVeze)
    {
        _po_restoranu[restoran] = new Veza(idVeze, DateTimeOffset.UtcNow);
        _po_vezi[idVeze] = restoran;
        log.LogInformation("Most restorana {Restoran} je na vezi ({Veza})", restoran, idVeze);
        Promjena?.Invoke(restoran, true);
    }

    public void Odjavi(string idVeze)
    {
        if (!_po_vezi.TryRemove(idVeze, out var restoran)) return;

        // Uklanjamo samo ako je to i dalje ta veza — inače bismo obrisali novu
        // vezu koja je u međuvremenu zamijenila staru.
        if (_po_restoranu.TryGetValue(restoran, out var v) && v.IdVeze == idVeze)
            _po_restoranu.TryRemove(restoran, out _);

        log.LogInformation("Most restorana {Restoran} više nije na vezi", restoran);
        Promjena?.Invoke(restoran, false);
    }

    public string? IdVeze(Guid restoran) =>
        _po_restoranu.TryGetValue(restoran, out var v) ? v.IdVeze : null;

    public bool JeNaVezi(Guid restoran) => _po_restoranu.ContainsKey(restoran);

    public DateTimeOffset? NaVeziOd(Guid restoran) =>
        _po_restoranu.TryGetValue(restoran, out var v) ? v.Od : null;

    /// <summary>Javlja UI-u da se stanje veze promijenilo, da korisnik ne mora osvježavati stranicu.</summary>
    public event Action<Guid, bool>? Promjena;
}
