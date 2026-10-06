using Analitika.Shared.Upiti;

namespace Analitika.Server.Servisi;

/// <summary>Procjena prodaje jednog artikla za budući period.</summary>
public sealed record ProcjenaArtikla
{
    public double Sifra { get; init; }
    public required string Naziv { get; init; }

    /// <summary>Očekivana količina za cijeli traženi period.</summary>
    public decimal Ocekivano { get; init; }

    /// <summary>Donja i gornja granica raspona (P25–P75 iz historijske raspršenosti).</summary>
    public decimal Donja { get; init; }
    public decimal Gornja { get; init; }

    /// <summary>Na koliko je radnih dana podataka procjena zasnovana.</summary>
    public int DanaPodataka { get; init; }
}

/// <summary>Procjena potrošnje jedne sirovine.</summary>
public sealed record ProcjenaSirovine
{
    public double Barkod { get; init; }
    public required string Naziv { get; init; }
    public string? Jm { get; init; }
    public decimal Ocekivano { get; init; }
    public decimal Donja { get; init; }
    public decimal Gornja { get; init; }
}

public sealed record RezultatProjekcije
{
    public List<ProcjenaArtikla> Artikli { get; init; } = [];
    public List<ProcjenaSirovine> Sirovine { get; init; } = [];

    /// <summary>Sezonski množilac primijenjen na traženi period (1,0 = kao prosjek godine).</summary>
    public decimal SezonskiIndeks { get; init; }

    /// <summary>Koliko radnih dana historije stoji iza procjene — mjera povjerenja.</summary>
    public int DanaHistorije { get; init; }

    /// <summary>Ima li dovoljno podataka da sezonalnost bude izmjerena, a ne pretpostavljena.</summary>
    public bool SezonaIzmjerena { get; init; }

    public string? Upozorenje { get; init; }
}

/// <summary>
/// Procjena buduće prodaje i potrošnje sirovina.
///
/// Model je namjerno jednostavan i objašnjiv: sa godinu-dvije podataka složeniji
/// modeli (ARIMA, Prophet) prilagode se šumu i daju lažan osjećaj preciznosti.
///
///   procjena(dan) = medijana prodaje za taj dan u sedmici × sezonski indeks mjeseca
///
/// Medijana, ne prosjek — jedan praznik ili neradni dan ne smije pomjeriti procjenu.
/// </summary>
public static class Projekcija
{
    /// <summary>
    /// Koliko sedmica unazad ulazi u osnovicu.
    ///
    /// Četiri, a ne osam: prodaja u ugostiteljstvu se mijenja brzo, pa duži prozor vuče
    /// stare slabije sedmice i onda ih sezonski indeks preko mjere „podigne". Provjereno
    /// na stvarnim podacima — osmosedmična osnovica je za jedan artikal davala 71 komad
    /// za dvije sedmice, dok je stvarnost u zadnje dvije bila 51.
    /// </summary>
    private const int SedmicaOsnovice = 4;

    /// <summary>
    /// Najmanji broj dana sa prodajom da bi prozor bio upotrebljiv.
    ///
    /// Deset, a ne četrnaest: restoran koji ne radi nedjeljom ima svega dvanaest dana
    /// u dvije sedmice. Sa strožim pragom bi tekući mjesec ispao iz igre i osnovica bi
    /// nepotrebno posegnula preko granice mjeseca.
    /// </summary>
    private const int NajmanjeDana = 10;

    public static RezultatProjekcije Izracunaj(
        IReadOnlyList<DnevnaProdaja> historija,
        IReadOnlyList<Normativ> normativi,
        IReadOnlyDictionary<double, string> nazivi,
        DateOnly danas,
        DateOnly ciljOd,
        DateOnly ciljDo)
    {
        if (historija.Count == 0)
            return new RezultatProjekcije { Upozorenje = "Nema historijskih podataka za procjenu." };

        var daniHistorije = historija.Select(h => h.Datum).Distinct().ToList();
        var (faktorDana, indeksi, izmjerena) = Obrasci(historija, daniHistorije);

        var osnovica = Osnovica(historija, danas);
        var ciljaniDani = Dani(ciljOd, ciljDo).ToList();

        // Svi kalendarski dani prozora, i oni bez ijedne prodaje. Budući period će
        // isto tako sadržati zatvorene dane, pa obje strane računa moraju brojati
        // dane na isti način — inače projekcija sistematski precjenjuje.
        var radniDani = Dani(osnovica.Min(h => h.Datum), osnovica.Max(h => h.Datum)).ToList();

        var artikli = new List<ProcjenaArtikla>();

        foreach (var grupa in osnovica.GroupBy(h => h.Sifra))
        {
            var prodano = grupa.ToDictionary(x => x.Datum, x => x.Kolicina);

            // Svaki radni dan se očisti od dana u sedmici i od sezone, pa ostane samo
            // "nivo" tog artikla. Medijana ide preko svih dana prozora, ne po danu u
            // sedmici — kratka osnovica daje svega dva uzorka po danu, pa bi jedan
            // zatvoren dan prepolovio procjenu.
            // Nivo = omjer zbirova, ne prosjek omjera. Prosjek omjera daje veću težinu
            // slabim danima i sistematski precjenjuje: na stvarnim podacima je za jedan
            // artikal davao 81 komad umjesto 55. Omjer zbirova vjerno reprodukuje stvarnu
            // prodaju kad je budući period sličan prošlom.
            decimal zbirProdaje = 0, zbirTezina = 0;
            var nivoi = new List<decimal>();

            foreach (var d in radniDani)
            {
                var tezina = Faktor(faktorDana, d.DayOfWeek) * Indeks(indeksi, d.Month);
                if (tezina <= 0) continue;

                var kolicina = prodano.GetValueOrDefault(d, 0m);
                zbirProdaje += kolicina;
                zbirTezina += tezina;
                nivoi.Add(kolicina / tezina);
            }

            if (zbirTezina <= 0) continue;

            var nivo = zbirProdaje / zbirTezina;
            if (nivo <= 0) continue;

            // Raspon i dalje dolazi iz raspršenosti pojedinačnih dana.
            var nivoDonji = Kvantil(nivoi, 0.25m);
            var nivoGornji = Kvantil(nivoi, 0.75m);

            decimal ocekivano = 0, donja = 0, gornja = 0;

            foreach (var dan in ciljaniDani)
            {
                // Nivo se vraća na stvarni dan: obrazac sedmice × sezona mjeseca.
                var mnozilac = Faktor(faktorDana, dan.DayOfWeek) * Indeks(indeksi, dan.Month);

                ocekivano += nivo * mnozilac;
                donja     += nivoDonji * mnozilac;
                gornja    += nivoGornji * mnozilac;
            }

            artikli.Add(new ProcjenaArtikla
            {
                Sifra = grupa.Key,
                Naziv = nazivi.GetValueOrDefault(grupa.Key, $"Šifra {grupa.Key:0}"),
                Ocekivano = Math.Round(ocekivano, 1),
                Donja = Math.Round(donja, 1),
                Gornja = Math.Round(gornja, 1),
                DanaPodataka = grupa.Select(x => x.Datum).Distinct().Count()
            });
        }

        return new RezultatProjekcije
        {
            Artikli = [.. artikli.OrderByDescending(a => a.Ocekivano)],
            Sirovine = Sirovine(artikli, normativi),
            SezonskiIndeks = Math.Round(ProsjecniIndeks(indeksi, ciljaniDani) / Indeks(indeksi, danas.Month), 2),
            DanaHistorije = daniHistorije.Count,
            SezonaIzmjerena = izmjerena,
            Upozorenje = Upozorenje(daniHistorije, izmjerena, artikli.Count)
        };
    }

    /// <summary>
    /// Bira podatke na kojima počiva procjena.
    ///
    /// Kad tekući mjesec ima dovoljno vlastitih dana, uzimaju se samo oni. Miješanje
    /// mjeseci traži da se stariji dani „podignu" sezonskim indeksom, a taj indeks je
    /// izveden iz ukupne prodaje i ne mora vrijediti za pojedini artikal — na stvarnim
    /// podacima je tako projekcija za jedan artikal ispala 76 komada umjesto 51.
    ///
    /// Tek kad tekući mjesec nema dovoljno dana (početak mjeseca), poseže se za
    /// prethodne sedmice, uz sezonsko izjednačavanje.
    /// </summary>
    private static List<DnevnaProdaja> Osnovica(IReadOnlyList<DnevnaProdaja> historija, DateOnly danas)
    {
        var tekuciMjesec = historija
            .Where(h => h.Datum.Year == danas.Year && h.Datum.Month == danas.Month)
            .ToList();

        if (Dana(tekuciMjesec) >= NajmanjeDana) return tekuciMjesec;

        var pocetak = danas.AddDays(-7 * SedmicaOsnovice);
        var prozor = historija.Where(h => h.Datum >= pocetak).ToList();

        // Klijent koji je tek počeo, ili je bio duže zatvoren — bolje sve nego ništa.
        return Dana(prozor) >= NajmanjeDana ? prozor : [.. historija];

        static int Dana(List<DnevnaProdaja> redovi) => redovi.Select(r => r.Datum).Distinct().Count();
    }

    /// <summary>Prevodi procjenu prodaje u procjenu potrošnje sirovina preko važećih recepata.</summary>
    private static List<ProcjenaSirovine> Sirovine(List<ProcjenaArtikla> artikli, IReadOnlyList<Normativ> normativi)
    {
        var poArtiklu = normativi
            .Where(n => n.VaziDo is null)
            .ToDictionary(n => n.ArtikalSifra, n => n.Stavke);

        var zbir = new Dictionary<double, (string Naziv, string? Jm, decimal O, decimal D, decimal G)>();

        foreach (var a in artikli)
        {
            if (!poArtiklu.TryGetValue(a.Sifra, out var stavke)) continue;

            foreach (var s in stavke)
            {
                var (naziv, jm, o, d, g) = zbir.GetValueOrDefault(
                    s.RepromaterijalBarkod, (s.Naziv, s.Jm, 0m, 0m, 0m));

                zbir[s.RepromaterijalBarkod] = (
                    naziv, jm,
                    o + a.Ocekivano * s.Kolicina,
                    d + a.Donja * s.Kolicina,
                    g + a.Gornja * s.Kolicina);
            }
        }

        return [.. zbir
            .Select(kv => new ProcjenaSirovine
            {
                Barkod = kv.Key,
                Naziv = kv.Value.Naziv,
                Jm = kv.Value.Jm,
                Ocekivano = Math.Round(kv.Value.O, 2),
                Donja = Math.Round(kv.Value.D, 2),
                Gornja = Math.Round(kv.Value.G, 2)
            })
            .OrderByDescending(s => s.Ocekivano)];
    }

    /// <summary>
    /// Sezonski indeks po mjesecu: koliko je taj mjesec jači ili slabiji od tipičnog dana.
    /// Računa se na ukupnoj prodaji, ne po artiklu — pojedinačni artikal ima premalo podataka.
    ///
    /// Prije mjerenja sezone uklanja se uticaj dana u sedmici. Bez toga bi mjesec koji
    /// u posmatranom prozoru ima više vikenda izgledao „sezonski jači", iako je razlika
    /// samo u rasporedu dana.
    ///
    /// Svuda se koristi medijana, ne prosjek — jedan zatvoren dan ne smije pomjeriti indeks.
    /// </summary>
    private static (Dictionary<DayOfWeek, decimal> FaktorDana, Dictionary<int, decimal> Indeksi, bool Izmjerena)
        Obrasci(IReadOnlyList<DnevnaProdaja> historija, List<DateOnly> daniHistorije)
    {
        var poDanu = historija
            .GroupBy(h => h.Datum)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Kolicina));

        var tipicanDan = Kvantil([.. poDanu.Values], 0.50m);
        if (tipicanDan <= 0) return ([], [], false);

        // Obrazac sedmice se uzima iz cijele historije — tu ima dovoljno uzoraka po danu.
        var faktorDana = poDanu
            .GroupBy(kv => kv.Key.DayOfWeek)
            .ToDictionary(g => g.Key, g => Kvantil([.. g.Select(x => x.Value)], 0.50m) / tipicanDan);

        decimal BezDanaUSedmici(KeyValuePair<DateOnly, decimal> kv) =>
            kv.Value / Faktor(faktorDana, kv.Key.DayOfWeek);

        var indeksi = poDanu
            .GroupBy(kv => kv.Key.Month)
            .ToDictionary(g => g.Key, g => Kvantil([.. g.Select(BezDanaUSedmici)], 0.50m) / tipicanDan);

        // Sezonalnost je stvarno izmjerena tek kad historija pokriva puni godišnji ciklus.
        var raspon = daniHistorije.Max().DayNumber - daniHistorije.Min().DayNumber;
        return (faktorDana, indeksi, raspon >= 350);
    }

    /// <summary>
    /// Težina dana u sedmici. Dan koji se u historiji nikad ne pojavljuje je neradni —
    /// dobija nulu, jer bi inače projekcija naručivala robu i za dane kad je zatvoreno.
    /// </summary>
    private static decimal Faktor(Dictionary<DayOfWeek, decimal> faktori, DayOfWeek dan)
    {
        if (faktori.Count == 0) return 1m;
        return faktori.TryGetValue(dan, out var f) ? f : 0m;
    }

    private static decimal Indeks(Dictionary<int, decimal> indeksi, int mjesec)
    {
        if (indeksi.TryGetValue(mjesec, out var i) && i > 0) return i;

        // Mjesec bez podataka: uzima se najbliži poznati, umjesto da se pretpostavi prosjek.
        if (indeksi.Count == 0) return 1m;
        var najblizi = indeksi.Keys.OrderBy(m => Math.Min(Math.Abs(m - mjesec), 12 - Math.Abs(m - mjesec))).First();
        return indeksi[najblizi];
    }

    private static decimal ProsjecniIndeks(Dictionary<int, decimal> indeksi, List<DateOnly> dani) =>
        dani.Count == 0 ? 1m : dani.Average(d => Indeks(indeksi, d.Month));

    private static IEnumerable<DateOnly> Dani(DateOnly od, DateOnly doDatuma)
    {
        for (var d = od; d <= doDatuma; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Kvantil sa linearnom interpolacijom; medijana za 0,5.</summary>
    private static decimal Kvantil(List<decimal> uzorak, decimal kvantil)
    {
        if (uzorak.Count == 0) return 0;
        if (uzorak.Count == 1) return uzorak[0];

        var poredani = uzorak.OrderBy(x => x).ToList();
        var pozicija = (poredani.Count - 1) * kvantil;
        var donji = (int)Math.Floor(pozicija);
        var gornji = (int)Math.Ceiling(pozicija);

        return donji == gornji
            ? poredani[donji]
            : poredani[donji] + (poredani[gornji] - poredani[donji]) * (pozicija - donji);
    }

    private static string? Upozorenje(List<DateOnly> dani, bool sezonaIzmjerena, int brojArtikala)
    {
        if (brojArtikala == 0)
            return "Nijedan artikal sa receptom nije prodavan u posmatranom periodu.";

        if (dani.Count < 28)
            return $"Procjena počiva na samo {dani.Count} radnih dana — uzmi je kao grubu naznaku.";

        if (!sezonaIzmjerena)
        {
            var raspon = dani.Max().DayNumber - dani.Min().DayNumber;
            return $"Historija pokriva {raspon / 30} mjeseci, pa je sezonalnost izvedena iz trenda "
                 + "unutar godine, a ne iz stvarnog poređenja sa istim mjesecom lani. "
                 + "Procjena za ljetne mjesece nosi veću grešku.";
        }

        return null;
    }
}
