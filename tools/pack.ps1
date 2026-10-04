<#
.SYNOPSIS
    Builds a self-contained, single-file Windows release and zips it.

.DESCRIPTION
    Produces one .exe that runs on any Windows 10/11 x64 machine with no
    compiler, no SDK and no .NET runtime installed. That is the whole point:
    a release asset should not require the reader to build anything.

    Self-contained rather than NativeAOT on purpose. NativeAOT needs the Visual
    Studio C++ build tools and the Windows SDK, and it fails the build outright
    without them. Self-contained needs nothing beyond the .NET SDK, so the
    release can always be produced. The trade-off is size (roughly 48 MB against
    a hypothetical single-digit MB) and a slightly higher idle footprint.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER Version
    Version stamped into the artifact name. Defaults to the current git tag.

.EXAMPLE
    .\tools\pack.ps1
    .\tools\pack.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime      = 'win-x64',
    [string] $Version      = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if (-not $Version) {
        $Version = (& git describe --tags --abbrev=0 2>$null)
        if (-not $Version) { $Version = '0.0.0-dev' }
    }
    $Version = $Version.TrimStart('v')

    $stage = Join-Path $repoRoot 'artifacts\publish'
    $dist  = Join-Path $repoRoot 'artifacts\dist'
    $name  = "FileJuggler-$Version-$Runtime"

    Write-Host "==> Version $Version, runtime $Runtime" -ForegroundColor Cyan

    # A stale running instance locks the output exe and makes MSBuild fail with
    # an error that looks unrelated to locking.
    Get-Process -Name 'Juggler.Ui' -ErrorAction SilentlyContinue |
        ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 500

    Write-Host '==> Publishing' -ForegroundColor Cyan

    # The version must be stamped onto the assembly, not just into the file name.
    # UpdateCheck reads it from the entry assembly to decide whether the published release is
    # newer than what is running, and Diagnostics prints it in a bug report. Without this the
    # exe reports 1.0.0 forever and every user is told they are up to date.
    $stamp = $Version -replace '^v', ''

    dotnet publish "src/Juggler.Ui/Juggler.Ui.csproj" `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:Version="$stamp" `
        -p:FileVersion="$stamp" `
        -p:InformationalVersion="$stamp" `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:GenerateDocumentationFile=false `
        -o $stage `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    # Fail loudly if the stamp did not land. A silently unstamped build ships an app that can
    # never notice an update, which is worse than a build that refuses to be packaged.
    $reported = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
        (Join-Path $stage 'Juggler.Ui.exe')).ProductVersion

    Write-Host "    stamped version : $reported"

    if (-not $reported.StartsWith($stamp)) {
        throw "expected version $stamp in the published exe but found '$reported'"
    }

    # Native debug symbols from the runtime pack still land in the output and are
    # not something a user should download.
    Get-ChildItem $stage -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

    $exe = Join-Path $stage 'Juggler.Ui.exe'
    if (-not (Test-Path $exe)) { throw "publish produced no Juggler.Ui.exe in $stage" }

    $loose = Get-ChildItem $stage -Filter '*.dll' -ErrorAction SilentlyContinue
    if ($loose) {
        throw "expected a single-file bundle but found loose DLLs: $($loose.Name -join ', ')"
    }

    Write-Host '==> Zipping' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "$name.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal

    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host ''
    Write-Host "Done: $zip ($sizeMb MB)" -ForegroundColor Green
    Write-Host 'Attach it to the release with:'
    Write-Host "  gh release upload v$Version `"$zip`" --clobber"
}
finally {
    Pop-Location
}