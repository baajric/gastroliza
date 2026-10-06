# Instalacija mosta Gastroliza na računar sa bazom kase.
#
# Pokreće se dvoklikom na Instaliraj.cmd (traži administratorska prava).
# Kod i adresa servera stoje u uparivanje.json iz preuzetog paketa; bez njega ih skripta pita.
#
# Koraci: kopira most u Program Files, uparuje ga kodom, registruje Windows servis pod
# vlastitim nalogom, daje tom nalogu pravo čitanja baze kase i pokreće servis.

[CmdletBinding()]
param(
    [string] $Kod,
    [string] $Server
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$naziv = 'Analitika Most'
$nalog = "NT SERVICE\$naziv"
$izvor = Split-Path -Parent $MyInvocation.MyCommand.Path
$cilj  = Join-Path $env:ProgramFiles 'Gastroliza Most'

function Korak([string] $tekst) {
    Write-Host ''
    Write-Host "» $tekst" -ForegroundColor Cyan
}

try {
    $ja = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $ja.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Instalaciju treba pokrenuti kao administrator (dvoklik na Instaliraj.cmd).'
    }

    $putanjaUparivanja = Join-Path $izvor 'uparivanje.json'
    if (Test-Path $putanjaUparivanja) {
        $u = Get-Content $putanjaUparivanja -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $Kod)    { $Kod = $u.Kod }
        if (-not $Server) { $Server = $u.AdresaServera }
    }
    if (-not $Server) { $Server = Read-Host 'Adresa servera (npr. https://app.gastroliza.ba)' }
    if (-not $Kod)    { $Kod = Read-Host 'Kod sa stranice „Poveži kasu"' }

    # --- 1. Program ------------------------------------------------------------
    Korak "Kopiram most u $cilj"
    $postoji = Get-Service -Name $naziv -ErrorAction SilentlyContinue
    if ($postoji -and $postoji.Status -ne 'Stopped') {
        Stop-Service -Name $naziv -Force
        Start-Sleep -Seconds 2
    }
    New-Item -ItemType Directory -Force -Path $cilj | Out-Null
    Get-ChildItem -Path $izvor -File |
        Where-Object { $_.Name -ne 'uparivanje.json' } |
        Copy-Item -Destination $cilj -Force
    # Fajlovi iz preuzetog zipa nose oznaku „sa interneta" — Windows bi ih inače blokirao.
    Get-ChildItem -Path $cilj -Recurse | Unblock-File
    $exe = Join-Path $cilj 'Analitika.Agent.exe'

    # --- 2. Uparivanje -----------------------------------------------------------
    Korak 'Uparujem most sa restoranom'
    & $exe upari $Kod $Server
    if ($LASTEXITCODE -ne 0) { throw 'Uparivanje nije uspjelo — preuzmi most ponovo na stranici „Poveži kasu" (kod važi 30 minuta).' }

    # --- 3. Servis ---------------------------------------------------------------
    Korak 'Registrujem Windows servis'
    if ($postoji) {
        sc.exe delete "$naziv" | Out-Null
        Start-Sleep -Seconds 2
    }
    New-Service -Name $naziv `
                -BinaryPathName "`"$exe`"" `
                -DisplayName 'Gastroliza Most' `
                -Description 'Most između baze kase i aplikacije Gastroliza. Šalje samo zbirne rezultate, izlaznom vezom.' `
                -StartupType Automatic | Out-Null

    # Vlastiti virtuelni nalog umjesto LocalSystema — dobija tačno ona prava koja mu damo.
    sc.exe config "$naziv" obj= "$nalog" | Out-Null
    # Ako padne internet ili se server restartuje, servis se sam vraća.
    sc.exe failure "$naziv" reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    if (-not [Diagnostics.EventLog]::SourceExists($naziv)) {
        New-EventLog -LogName Application -Source $naziv
    }

    # Servis mora moći pročitati ključ i postavke koje je upisalo uparivanje.
    icacls (Join-Path $env:ProgramData 'Analitika') /grant "${nalog}:(OI)(CI)RX" | Out-Null

    # --- 4. Pristup bazi kase --------------------------------------------------
    Korak 'Dajem mostu pravo čitanja baze kase'
    & $exe postavi-sql $nalog
    if ($LASTEXITCODE -ne 0) { throw 'Pristup SQL Serveru nije podešen (vidi poruku iznad).' }

    # --- 5. Pokretanje -----------------------------------------------------------
    Korak 'Pokrećem most'
    Start-Service -Name $naziv
    Start-Sleep -Seconds 5
    $stanje = (Get-Service -Name $naziv).Status
    if ($stanje -ne 'Running') {
        # Jednostruki navodnici: PowerShell „ i " tretira kao kraj stringa u dvostrukim.
        throw ('Servis se nije pokrenuo (stanje: ' + $stanje + '). Pogledaj Event Viewer → Windows Logs → Application, izvor „' + $naziv + '".')
    }

    Write-Host ''
    Write-Host 'Most je instaliran i radi.' -ForegroundColor Green
    Write-Host 'Vrati se na stranicu „Poveži kasu" — za nekoliko sekundi piše „Kasa je povezana".'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "GREŠKA: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
