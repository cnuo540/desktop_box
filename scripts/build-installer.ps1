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

Write-Host "`n==> [3/4] ISCC DesktopBox.iss (core installer)..." -ForegroundColor Green
& $iscc (Join-Path $repoRoot 'DesktopBox.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Write-Host "`n==> [4/4] build elevated UAC bootstrapper..." -ForegroundColor Green
# The outer DesktopBoxSetup.exe carries a requireAdministrator manifest and embeds the core
# installer as RCDATA: Windows raises UAC first, then the core runs already elevated - which is
# what Inno's own bootstrap cannot do (it creates %TEMP%\is-*.tmp before elevating, and that step
# is denied on machines where an elevated, unsigned installer is blocked from writing there).
$versionForBootstrapper = (Get-Item $exe).VersionInfo.FileVersion
if (-not $versionForBootstrapper) { $versionForBootstrapper = (Get-Item $exe).VersionInfo.ProductVersion }
$parts = @($versionForBootstrapper.Split('.') + @('0', '0', '0', '0'))[0..3]
$versionRc = @"
VS_VERSION_INFO VERSIONINFO
 FILEVERSION $($parts -join ',')
 PRODUCTVERSION $($parts -join ',')
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "DesktopBox\0"
      VALUE "FileDescription", "DesktopBox Setup\0"
      VALUE "FileVersion", "$versionForBootstrapper\0"
      VALUE "InternalName", "DesktopBoxSetup\0"
      VALUE "OriginalFilename", "DesktopBoxSetup.exe\0"
      VALUE "ProductName", "DesktopBox\0"
      VALUE "ProductVersion", "$versionForBootstrapper\0"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot '_testtmp') | Out-Null
# Write without a BOM (rc.exe trips on one) and with a trailing newline (otherwise rc fails with
# "unexpected end of file" on the last line).
[System.IO.File]::WriteAllText((Join-Path $repoRoot '_testtmp\bootstrapper_version.rc'),
    $versionRc + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

& cmd /c (Join-Path $repoRoot 'scripts\build-bootstrapper.bat')
if ($LASTEXITCODE -ne 0) { throw 'UAC bootstrapper build failed' }

$setup = Join-Path $repoRoot 'release\DesktopBoxSetup.exe'
if (-not (Test-Path $setup)) { throw "missing $setup" }

$version = (Get-Item $exe).VersionInfo.FileVersion
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$payload = (Get-ChildItem (Join-Path $repoRoot 'publish-installer') -Recurse -File |
        Measure-Object -Property Length -Sum).Sum
Write-Host "`n==> installer ready: $setup (v$version, $size MB)" -ForegroundColor Cyan
Write-Host "    payload: $([math]::Round($payload / 1MB, 1)) MB multi-file self-contained (no %TEMP% extraction)" -ForegroundColor DarkGray
