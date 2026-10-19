# Builds the Chocolatey package of a published release (package id "tunero", for the community repository).
#
#   pwsh scripts/chocolatey-package.ps1 -Version 0.8.0 [-OutDir artifacts/chocolatey] [-Sha256 <hash>]
#
# The package holds no binary: its install script downloads Tunero.exe from the GitHub release into the package folder
# and checks the SHA-256 (Chocolatey rules CPMR0027 and CPMR0073). Chocolatey puts a shim for every exe in its package
# folder on the PATH, so "tunero" starts the app; the empty Tunero.exe.gui makes the shim start it without waiting.
# choco removes the package folder and the shim on uninstall; the data folder (%ProgramData%\Tunero) stays, as with
# every other way of installing (Settings > Uninstall in the app removes it). chocolateyUninstall.ps1 (uninstall only, not
# upgrade) removes the app's re-apply task when it starts this package's exe: it would otherwise start a missing file
# with administrator rights at every sign-in.
# Writes artifacts/chocolatey/tunero/ and, when choco is installed, packs tunero.<version>.nupkg next to it. The
# chocolatey workflow pushes that file; to push by hand: choco push <file> --source https://push.chocolatey.org/
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\chocolatey"),
    # Checked against the release signature by verify-release.ps1 when not given (the workflow passes the hash it verified).
    [string]$Sha256
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 0.8.0." }

$repo = "https://github.com/jozuern/Tunero"
$tag = "v$Version"
$exeUrl = "$repo/releases/download/$tag/Tunero.exe"
if (-not $Sha256) { $Sha256 = & (Join-Path $PSScriptRoot "verify-release.ps1") -Version $Version }
$Sha256 = $Sha256.ToLowerInvariant()
if ($Sha256 -notmatch '^[0-9a-f]{64}$') { throw "No SHA-256 for $exeUrl." }

$dir = Join-Path $OutDir "tunero"
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $dir "tools") | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

# The icon comes through jsDelivr from the release tag (rule CPMR0076 rejects raw.githubusercontent.com).
$nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
  <metadata>
    <id>tunero</id>
    <version>$Version</version>
    <title>Tunero</title>
    <authors>Joshua Zürn</authors>
    <owners>jozuern</owners>
    <projectUrl>$repo</projectUrl>
    <projectSourceUrl>$repo/tree/$tag</projectSourceUrl>
    <packageSourceUrl>$repo/blob/main/scripts/chocolatey-package.ps1</packageSourceUrl>
    <docsUrl>$repo#readme</docsUrl>
    <bugTrackerUrl>$repo/issues</bugTrackerUrl>
    <iconUrl>https://cdn.jsdelivr.net/gh/jozuern/Tunero@$tag/docs/brand/tunero-icon-256.png</iconUrl>
    <licenseUrl>$repo/blob/$tag/LICENSE</licenseUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <copyright>Copyright (c) 2026 Joshua Zürn</copyright>
    <tags>gaming optimizer tweaks windows-11 privacy debloat startup services cleanup performance admin</tags>
    <summary>PC optimizer for Windows 11, gaming first, that explains every check and change with sources and can undo every setting it changes.</summary>
    <description><![CDATA[Tunero checks Windows 11 for settings that cost performance, privacy or battery life and offers tweaks with their impact, evidence and risk.

Every finding and every change comes with an explanation and sources in English and German. Changes are backed up first, verified after applying and can be undone, also after a restart. It also covers removing preinstalled apps, cleanup, startup entries, services, NVIDIA driver settings, DNS and a frame time benchmark.

Tunero is one exe without a background process, service or telemetry. It needs administrator rights because it changes system settings, and Windows 11 (25H2 or later).

Start it with ``tunero``. Updates: ``choco upgrade tunero``; the app's own "Update now" runs that command for you. Your changes and backups are kept in ``%ProgramData%\Tunero``; to remove them too, use Settings > Uninstall in the app.]]></description>
    <releaseNotes>$repo/releases/tag/$tag</releaseNotes>
  </metadata>
  <files>
    <file src="tools\**" target="tools" />
  </files>
</package>
"@
[IO.File]::WriteAllText((Join-Path $dir "tunero.nuspec"), $nuspec, $utf8)

$install = @"
`$ErrorActionPreference = 'Stop'
`$toolsDir = Split-Path -Parent `$MyInvocation.MyCommand.Definition

# Tunero is x64 only and runs on Windows 11 (build 26100 or later).
`$packageArgs = @{
  packageName    = `$env:ChocolateyPackageName
  fileFullPath   = Join-Path `$toolsDir 'Tunero.exe'
  url64bit       = '$exeUrl'
  checksum64     = '$Sha256'
  checksumType64 = 'sha256'
}
Get-ChocolateyWebFile @packageArgs
"@
[IO.File]::WriteAllText((Join-Path $dir "tools\chocolateyInstall.ps1"), $install, $utf8)
[IO.File]::WriteAllText((Join-Path $dir "tools\Tunero.exe.gui"), "", $utf8)

$uninstall = @"
`$ErrorActionPreference = 'Stop'
`$exe = Join-Path (Split-Path -Parent `$MyInvocation.MyCommand.Definition) 'Tunero.exe'

# The opt-in task "Apply my changes again" (one per account, in Task Scheduler's \Tunero\ folder) goes with this copy.
# A task that starts another copy (installed in Program Files) stays.
Get-ScheduledTask -TaskPath '\Tunero\' -ErrorAction SilentlyContinue |
  Where-Object { `$_.Actions | Where-Object { `$_.Execute -eq `$exe } } |
  Unregister-ScheduledTask -Confirm:`$false
"@
[IO.File]::WriteAllText((Join-Path $dir "tools\chocolateyUninstall.ps1"), $uninstall, $utf8)

Write-Host "Written: $dir"
if (Get-Command choco -ErrorAction SilentlyContinue) {
    choco pack (Join-Path $dir "tunero.nuspec") --out $OutDir
    if ($LASTEXITCODE -ne 0) { throw "choco pack failed." }
}
