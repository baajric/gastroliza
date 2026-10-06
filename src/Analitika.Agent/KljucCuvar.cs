using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Analitika.Agent;

/// <summary>Čuva ključ mosta tako da ga druga aplikacija ni drugi korisnik ne mogu pročitati.</summary>
public interface IKljucCuvar
{
    string? Ucitaj();
    void Snimi(string kljuc);
    bool Postoji { get; }
}

/// <summary>
/// Ključ se šifruje Windows DPAPI-jem vezanim za mašinu, pa datoteka nije upotrebljiva
/// ako je neko prekopira na drugi računar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKljucCuvar(ILogger<DpapiKljucCuvar> log) : IKljucCuvar
{
    private static readonly byte[] Dodatak = "Analitika.Most.v1"u8.ToArray();

    private static string Putanja => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Analitika", "most.kljuc");

    public bool Postoji => File.Exists(Putanja);

    public string? Ucitaj()
    {
        if (!Postoji) return null;
        try
        {
            var sifrovano = File.ReadAllBytes(Putanja);
            var otvoreno = ProtectedData.Unprotect(sifrovano, Dodatak, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(otvoreno);
        }
        catch (CryptographicException ex)
        {
            // Datoteka je sa drugog računara ili oštećena — traži ponovno uparivanje.
            log.LogError(ex, "Ključ se ne može dešifrovati. Potrebno je ponovo upariti most.");
            return null;
        }
    }

    public void Snimi(string kljuc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Putanja)!);
        var sifrovano = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(kljuc), Dodatak, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(Putanja, sifrovano);
        log.LogInformation("Ključ je snimljen u {Putanja}", Putanja);
    }
}
