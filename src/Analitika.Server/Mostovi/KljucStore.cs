using System.Security.Cryptography;
using System.Text;

namespace Analitika.Server.Mostovi;

public sealed record Restoran(Guid Id, string Naziv);

/// <summary>
/// Provjerava ključeve mostova i mapira ih na restoran. Server nikad ne prima
/// <c>tenant_id</c> od klijenta — izvodi ga isključivo odavde, iz ključa.
/// </summary>
public interface IKljucStore
{
    Restoran? PronadjiPoKljucu(string kljuc);
    Restoran? PronadjiPoId(Guid id);
    IReadOnlyList<Restoran> Svi();
}

/// <summary>
/// Privremena izvedba za razvoj — ključevi se čitaju iz konfiguracije.
/// Zamjenjuje se bazom (korisnici, restorani, hash ključeva) u koraku sa loginom.
/// </summary>
public sealed class KonfigKljucStore : IKljucStore
{
    private readonly Dictionary<string, Restoran> _po_hashu = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Restoran> _po_id = [];

    public KonfigKljucStore(IConfiguration konfig)
    {
        foreach (var stavka in konfig.GetSection("Restorani").GetChildren())
        {
            var id = Guid.Parse(stavka["Id"]!);
            var restoran = new Restoran(id, stavka["Naziv"] ?? "Bez naziva");
            _po_hashu[Hash(stavka["Kljuc"]!)] = restoran;
            _po_id[id] = restoran;
        }
    }

    /// <summary>
    /// Čuva se samo hash ključa. Poređenje ide preko <c>FixedTimeEquals</c> da trajanje
    /// provjere ne oda koliko se znakova poklopilo.
    /// </summary>
    public static string Hash(string kljuc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kljuc)));

    public Restoran? PronadjiPoKljucu(string kljuc)
    {
        if (string.IsNullOrWhiteSpace(kljuc)) return null;

        var trazeni = Encoding.UTF8.GetBytes(Hash(kljuc));
        foreach (var (hash, restoran) in _po_hashu)
        {
            if (CryptographicOperations.FixedTimeEquals(trazeni, Encoding.UTF8.GetBytes(hash)))
                return restoran;
        }
        return null;
    }

    public Restoran? PronadjiPoId(Guid id) => _po_id.GetValueOrDefault(id);

    public IReadOnlyList<Restoran> Svi() => [.. _po_id.Values];
}

/// <summary>
/// Ključevi iz baze servera (mostovi upareni kroz web) i iz konfiguracije
/// (razvojni most). Restorani se traže prvo u bazi.
/// </summary>
public sealed class SlozeniKljucStore(KonfigKljucStore konfig, Analitika.Server.Nalozi.ServerBaza baza) : IKljucStore
{
    public Restoran? PronadjiPoKljucu(string kljuc)
    {
        if (string.IsNullOrWhiteSpace(kljuc)) return null;

        // Ključ ima 256 bita slučajnosti, pa se hash može tražiti direktno u bazi:
        // poređenje po vremenu ne odaje ništa upotrebljivo.
        return baza.RestoranPoHashuKljuca(KonfigKljucStore.Hash(kljuc)) ?? konfig.PronadjiPoKljucu(kljuc);
    }

    public Restoran? PronadjiPoId(Guid id) => baza.Restoran(id) ?? konfig.PronadjiPoId(id);

    public IReadOnlyList<Restoran> Svi() => konfig.Svi();
}
