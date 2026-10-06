using Analitika.Agent;
using Analitika.Agent.Queries;

var builder = Host.CreateApplicationBuilder(args);

// Postavke ovog računara (adresa servera, veza na SQL) koje upisuje instalacija.
// Dolaze poslije appsettings.json, pa ga nadjačavaju, a nova verzija programa ih ne briše.
builder.Configuration.AddJsonFile(MasinskePostavke.Putanja, optional: true, reloadOnChange: false);

builder.Services.AddSingleton<LokalnaBaza>();
builder.Services.AddSingleton<Izvrsilac>();
builder.Services.AddSingleton<IKljucCuvar, DpapiKljucCuvar>();
builder.Services.AddHostedService<MostServis>();

// Da se most sam pokreće sa računarom, bez prijavljenog korisnika.
builder.Services.AddWindowsService(o => o.ServiceName = "Analitika Most");

var host = builder.Build();

// Naredbe instalacije pišu na konzolu — naša slova moraju stići neiskvarena.
if (args.Length > 0) Console.OutputEncoding = System.Text.Encoding.UTF8;

// Naredbe koje pokreće instalacija — jednom, pod nalogom administratora:
//   Analitika.Agent.exe upari <kod> [adresa-servera]    jednokratni kod sa stranice „Poveži kasu"
//   Analitika.Agent.exe postavi-sql [windows-nalog]     pravo čitanja POS baza za servis
if (args.Length >= 2 && args[0].Equals("upari", StringComparison.OrdinalIgnoreCase))
{
    var cuvar = host.Services.GetRequiredService<IKljucCuvar>();
    return await Uparivanje.PokreniAsync(args[1], args.Length >= 3 ? args[2] : null, builder.Configuration, cuvar);
}

if (args.Length >= 1 && args[0].Equals("postavi-sql", StringComparison.OrdinalIgnoreCase))
{
    return await PristupSql.PokreniAsync(args.Length >= 2 ? args[1] : @"NT SERVICE\Analitika Most");
}

host.Run();
return 0;
