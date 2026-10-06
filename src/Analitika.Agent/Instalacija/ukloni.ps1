# Uklanja most Gastroliza sa ovog računara: servis, program i ključ uparivanja.
# Baza kase se ne dira. Lokalna baza Analitika (normativi, uvezene godine) ostaje,
# da ponovna instalacija ništa ne izgubi.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$naziv = 'Analitika Most'

try {
    $servis = Get-Service -Name $naziv -ErrorAction SilentlyContinue
    if ($servis) {
        if ($servis.Status -ne 'Stopped') { Stop-Service -Name $naziv -Force }
        sc.exe delete "$naziv" | Out-Null
        Write-Host 'Servis je uklonjen.'
    }

    $program = Join-Path $env:ProgramFiles 'Gastroliza Most'
    if (Test-Path $program) { Remove-Item $program -Recurse -Force; Write-Host 'Program je obrisan.' }

    $kljuc = Join-Path $env:ProgramData 'Analitika\most.kljuc'
    if (Test-Path $kljuc) { Remove-Item $kljuc -Force; Write-Host 'Ključ uparivanja je obrisan.' }

    Write-Host ''
    Write-Host 'Most je uklonjen.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "GREŠKA: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
