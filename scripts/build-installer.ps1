<#
.SYNOPSIS
    Build the DesktopBox Windows installer (Inno Setup).

.DESCRIPTION
    One command, three steps:
      1. dotnet publish  -> publish\DesktopBox.exe        (single-file, self-contained)
      2. build_dll.bat   -> publish\DesktopBox.ShellMenu.exe (native right-click menu host)
      3. ISCC DesktopBox.iss -> release\DesktopBoxSetup.exe

    The installer version is read from publish\DesktopBox.exe (csproj <Version>),
    and the setup icon comes from src\DesktopBox\Resources\DesktopBox.ico.

.PARAMETER SkipPublish
    Reuse an existing publish\DesktopBox.exe instead of republishing.

.EXAMPLE
    .\scripts\build-installer.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $repoRoot
Write-Host "==> repo: $repoRoot" -ForegroundColor Cyan

# Some launchers (CI agents, nested/agent shells) strip the standard Windows folders from the
# environment. NuGet then dies with "Value cannot be null. (Parameter 'path1')" while resolving
# its config paths, which looks exactly like a broken NuGet install. Put them back first.
if (-not $env:ProgramFiles) { $env:ProgramFiles = 'C:\Program Files' }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }
if (-not $env:ProgramData) { $env:ProgramData = 'C:\ProgramData' }

function Resolve-Tool {
    param([string[]]$Candidates, [string]$Name, [string]$InstallHint)
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "$Name not found. $InstallHint"
}

$dotnet = Resolve-Tool -Name 'dotnet' -InstallHint 'Install the .NET 8 SDK.' -Candidates @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    'C:\Program Files\dotnet\dotnet.exe'
)

$iscc = Resolve-Tool -Name 'ISCC.exe' -InstallHint 'Install Inno Setup 6 (winget install JRSoftware.InnoSetup).' -Candidates @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)

# A running instance would lock publish\DesktopBox.exe.
Get-Process -Name DesktopBox -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

if (-not $SkipPublish) {
    Write-Host "`n==> [1/3] dotnet publish (multi-file, self-contained -> publish-installer)..." -ForegroundColor Green
    # The installer ships a MULTI-FILE self-contained build: no first-run extraction of the
    # single-file bundle into %TEMP%\.net (slow first start, paid again after temp cleanup),
    # and still needs no .NET runtime. The single-file green build stays in release.ps1 (zip).
    #
    # Wipe the output dir first: dotnet publish leaves stale files behind, and the .iss ships
    # "publish-installer\*" - leftovers (even a boxes.json from a smoke test) would be packaged.
    $payloadDir = Join-Path $repoRoot 'publish-installer'
    if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
    $publishArgs = @(
        'publish', 'src/DesktopBox/DesktopBox.csproj',
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        '-p:EnableCompressionInSingleFile=false',
        '-o', 'publish-installer', '--nologo'
    )
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        # Some machines have a broken NuGet environment - fall back to the existing obj assets.
        Write-Host "    publish failed, retrying with --no-restore..." -ForegroundColor Yellow
        & $dotnet @publishArgs '--no-restore'
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    }
}

$exe = Join-Path $repoRoot 'publish-installer/DesktopBox.exe'
if (-not (Test-Path $exe)) { throw "missing $exe" }

Write-Host "`n==> [2/3] build native ShellMenu helper..." -ForegroundColor Green
& cmd /c (Join-Path $repoRoot 'src\DesktopBox.ShellMenu\build_dll.bat')
if ($LASTEXITCODE -ne 0) { throw 'ShellMenu helper build failed' }

$helper = Join-Path $repoRoot 'publish/DesktopBox.ShellMenu.exe'
if (-not (Test-Path $helper)) { throw "missing $helper" }
Copy-Item $helper (Join-Path $repoRoot 'publish-installer/DesktopBox.ShellMenu.exe') -Force

Write-Host "`n==> [3/3] ISCC DesktopBox.iss..." -ForegroundColor Green
& $iscc (Join-Path $repoRoot 'DesktopBox.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $repoRoot 'release\DesktopBoxSetup.exe'
if (-not (Test-Path $setup)) { throw "missing $setup" }

$version = (Get-Item $exe).VersionInfo.FileVersion
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$payload = (Get-ChildItem (Join-Path $repoRoot 'publish-installer') -Recurse -File |
        Measure-Object -Property Length -Sum).Sum
Write-Host "`n==> installer ready: $setup (v$version, $size MB)" -ForegroundColor Cyan
Write-Host "    payload: $([math]::Round($payload / 1MB, 1)) MB multi-file self-contained (no %TEMP% extraction)" -ForegroundColor DarkGray
