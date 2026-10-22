#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Arquitectura = 'win-x64',
    [switch]$SinPruebas
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Esta aplicación y este script requieren Windows.' }
$raizProyecto = Split-Path -Parent $PSScriptRoot
Push-Location $raizProyecto
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Instala el SDK .NET 10 desde https://dotnet.microsoft.com/download/dotnet/10.0 y vuelve a abrir PowerShell.'
    }
    dotnet build LimpiadorUltraWindows.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación.' }
    if (-not $SinPruebas) {
        dotnet run --project tests/Limpiador.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas. No se publica un ejecutable.' }
    }
    $salida = Join-Path $raizProyecto "dist/$Arquitectura"
    dotnet publish src/Limpiador.App -c Release -r $Arquitectura --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $salida
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de la aplicación.' }
    dotnet publish src/Limpiador.Cli -c Release -r $Arquitectura --self-contained true -p:PublishSingleFile=true -o "$salida/diagnostico"
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del diagnóstico.' }
    dotnet publish tests/Limpiador.Tests -c Release -r $Arquitectura --self-contained true -p:PublishSingleFile=true -o "$salida/verificacion"
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de la verificación.' }
    Copy-Item README.md $salida
    New-Item -ItemType Directory -Path "$salida/docs" -Force | Out-Null
    Copy-Item "docs/*" "$salida/docs" -Recurse -Force
    Copy-Item scripts/verificar.cmd "$salida/verificar.cmd"
    $zip = Join-Path $raizProyecto "dist/LimpiadorUltraWindows-$Arquitectura.zip"
    Compress-Archive -Path "$salida/*" -DestinationPath $zip -Force
    $hash = Get-FileHash -Algorithm SHA256 $zip
    "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zip)" | Set-Content "$zip.sha256" -Encoding ascii
    $hash | Format-List
    Write-Host "Listo: $zip"
}
finally { Pop-Location }
