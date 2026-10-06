namespace Analitika.Shared.Upiti;

/// <summary>
/// Bijela lista upita koje most smije izvršiti. Server šalje samo ovo ime i parametre —
/// nikad SQL. Tako kompromitovan server ne može pokrenuti proizvoljan SQL kod klijenta.
/// </summary>
public enum ImeUpita
{
    /// <summary>Popis godišnjih POS baza zatečenih na serveru klijenta.</summary>
    PopisBaza,

    /// <summary>Zbirni pokazatelji za period: promet, broj računa, prosječna korpa.</summary>
    Sazetak,

    /// <summary>Promet po danima u periodu (za krivulju).</summary>
    PrometPoDanima,

    /// <summary>Promet po satu u danu (0–23), zbirno za period.</summary>
    PrometPoSatima,

    /// <summary>Rang lista artikala: količina, promet, broj pojavljivanja.</summary>
    RangArtikala,

    /// <summary>Rang lista konobara: promet, broj artikala, broj računa.</summary>
    RangKonobara,

    /// <summary>Udio grupa (Piće/Hrana) u prometu.</summary>
    PrometPoGrupama,

    /// <summary>Šifarnik artikala iz Robe.</summary>
    Artikli,

    /// <summary>Šifarnik repromaterijala.</summary>
    Repromaterijali,

    /// <summary>Prodane količine po artiklu (osnova za utrošak i projekcije).</summary>
    ProdajaPoArtiklu,

    /// <summary>Dnevna prodaja po artiklu — ulaz za projekcije.</summary>
    DnevnaProdajaPoArtiklu,

    /// <summary>Stanje zaliha repromaterijala.</summary>
    StanjeRepromaterijala,

    // --- Lokalna baza Analitika kod klijenta (jedino mjesto gdje most piše) ---

    /// <summary>Svi normativi, sa stavkama.</summary>
    NormativiSvi,

    /// <summary>Upisuje ili mijenja normativ; tijelo je u <see cref="ZahtjevUpita.TeretJson"/>.</summary>
    NormativSnimi,

    /// <summary>Zatvara normativ (postavlja VaziDo) umjesto da ga briše — historija ostaje.</summary>
    NormativZatvori,

    /// <summary>Artikli koji se prodaju a nemaju recept, sortirani po prodaji u periodu.</summary>
    ArtikliBezNormativa,

    /// <summary>Prepisuje postojeće normative iz POS baze u lokalnu bazu Analitika.</summary>
    UvoziPostojeceNormative,

    /// <summary>Čita postavke klijenta (izabrane godišnje baze).</summary>
    PostavkeUcitaj,

    /// <summary>Snima postavke klijenta.</summary>
    PostavkeSnimi,

    // --- Utrošak i projekcije ---

    /// <summary>Očekivani utrošak repromaterijala u periodu, iz normativa i prodaje.</summary>
    UtrosakRepromaterijala,

    /// <summary>Razlaganje utroška jednog repromaterijala po artiklima.</summary>
    UtrosakPoArtiklu,

    /// <summary>Dnevna prodaja artikala koji imaju recept — ulaz za projekciju.</summary>
    DnevnaProdajaSaNormativom,

    /// <summary>Promet po danu u sedmici i satu — mreža za toplotnu mapu gužve.</summary>
    PrometPoDanuISatu,

    // --- Konobari i godine podataka ---

    /// <summary>Sve kartice konobara iz kase (Glopos) i iz uvezenih godina — za preimenovanje.</summary>
    KonobariKase,

    /// <summary>Pregled godina: odakle dolaze podaci (živa baza, stara baza u kasi, uvezen fajl).</summary>
    GodinePodataka,

    /// <summary>Otvara uvoz prošle godine; stavke idu u privremenu tabelu dok se uvoz ne potvrdi.</summary>
    ArhivaUvozPocni,

    /// <summary>Jedan paket stavki uvoza; tijelo je u <see cref=ZahtjevUpita.TeretJson/>.</summary>
    ArhivaUvozDio,

    /// <summary>Potvrđuje uvoz: godina se zamjenjuje u jednoj transakciji.</summary>
    ArhivaUvozZavrsi,

    /// <summary>Odustaje od započetog uvoza i briše privremene stavke.</summary>
    ArhivaUvozOdustani,

    /// <summary>Briše uvezenu godinu.</summary>
    ArhivaObrisi
}
