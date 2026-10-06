using System.Globalization;
using Analitika.Server.Components;
using System.Threading.RateLimiting;
using Analitika.Server.Mostovi;
using Analitika.Server.Nalozi;
using Analitika.Server.Servisi;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;

// Cijela aplikacija radi u domaćoj kulturi: zarez je decimalni znak, tačka razdvaja
// hiljade, datum je 15.08.2026. Bez ovoga bi uneseno „0,18" bilo pročitano kao 18.
var kultura = Kultura();
CultureInfo.DefaultThreadCurrentCulture = kultura;
CultureInfo.DefaultThreadCurrentUICulture = kultura;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Mostovi drže dugotrajne veze; poruke znaju biti veće od podrazumijevanih 32 KB.
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 8 * 1024 * 1024;
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// --- Nalozi i prijava ---------------------------------------------------------
builder.Services.AddSingleton<ServerBaza>();
builder.Services.AddSingleton<KocnicaPrijave>();
builder.Services.AddMemoryCache();

// Kolačić prijave je potpisan ključevima koji se čuvaju uz bazu servera — bez toga
// bi svaki restart servera odjavio sve korisnike.
builder.Services.AddDataProtection()
    .SetApplicationName("Gastroliza")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "kljucevi")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/nalog/prijava";
        o.LogoutPath = "/nalog/odjava";
        o.Cookie.Name = "gastroliza";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Uparivanje mosta je bez prijave, pa ga kočimo po adresi: deset pokušaja u minuti
// je dovoljno za instalaciju, a premalo za pogađanje kodova.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("uparivanje", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "nepoznato",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// --- Mostovi -------------------------------------------------------------------
builder.Services.AddSingleton<KonfigKljucStore>();
builder.Services.AddSingleton<IKljucStore, SlozeniKljucStore>();
builder.Services.AddSingleton<MostRegistar>();
builder.Services.AddSingleton<MostKlijent>();
builder.Services.AddSingleton<PostavkeRestorana>();

// Period živi po korisničkoj sesiji, ne globalno.
builder.Services.AddScoped<StanjeAplikacije>();
builder.Services.AddScoped<Podaci>();

var app = builder.Build();

PripremiBazu(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();

    // U razvoju ostaje i http, da browser ne udara u upozorenje o samopotpisanom certifikatu.
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHub<MostHub>(MostHub.Putanja);
app.MapDijagnostiku();
app.MapNaloge();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Pravi bazu servera. U razvoju veže nalog iz konfiguracije za razvojni restoran,
/// da lokalni most (uparen razvojnim ključem) i dalje ima vlasnika koji se može prijaviti.
/// </summary>
static void PripremiBazu(WebApplication app)
{
    var baza = app.Services.GetRequiredService<ServerBaza>();
    baza.Pripremi();

    var nalog = app.Configuration.GetSection("RazvojniNalog");
    if (app.Environment.IsDevelopment() && nalog["Email"] is { } email && nalog["Lozinka"] is { } lozinka)
    {
        foreach (var r in app.Services.GetRequiredService<KonfigKljucStore>().Svi())
            baza.OsigurajRazvojniNalog(email, lozinka, r);
    }
}

static CultureInfo Kultura()
{
    // Na nekim sistemima bosanska kultura nije instalirana; hrvatska ima ista
    // pravila za brojeve i datume, pa služi kao zamjena.
    foreach (var oznaka in new[] { "bs-Latn-BA", "bs-BA", "hr-HR" })
    {
        try { return CultureInfo.GetCultureInfo(oznaka); }
        catch (CultureNotFoundException) { }
    }
    return CultureInfo.InvariantCulture;
}
