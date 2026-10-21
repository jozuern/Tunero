# Checks a release the way the app's updater does, and prints the exe's SHA-256 (lowercase) when it passes.
#
#   pwsh scripts/verify-release.ps1 -Version 0.8.2
#   pwsh scripts/verify-release.ps1 -Version 0.8.2 -Folder assets     # files already downloaded (the workflows)
#
# Downloads Tunero.exe, Tunero.exe.sha256 and Tunero.exe.sig of the release (or takes them from -Folder), hashes the exe
# itself, compares with the .sha256 file and checks that one of the app's keys (src/Optimizer.Core/Updates/update-key*.pem:
# the release key and the offline backup key) signed exactly
# "PCOptimizer <version>\n<sha256>\n" (UpdateSignature.Statement). The one copy of this check: release.yml runs it on the
# files it just signed, winget.yml and chocolatey.yml on the published assets, and the package scripts
# (chocolatey-package.ps1, scoop-manifest.ps1, winget-manifest.ps1) take the hash from here when none is given: a
# .sha256 file alone proves nothing, whoever can replace the exe can replace it too.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Repository = "jozuern/Tunero",
    [string]$KeyFolder = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Optimizer.Core\Updates"),
    # A folder that already holds Tunero.exe, Tunero.exe.sha256 and Tunero.exe.sig: nothing is downloaded or deleted.
    [string]$Folder
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 0.8.2." }

function Test-Release([string]$exe) {
    $listed = ((Get-Content -Raw "$exe.sha256") -split '\s+')[0].ToLowerInvariant()
    $signature = [Convert]::FromBase64String((Get-Content -Raw "$exe.sig").Trim())
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $listed) { throw "Tunero.exe has SHA-256 $hash, the release lists $listed." }
    $statement = [Text.Encoding]::UTF8.GetBytes("PCOptimizer $Version`n$hash`n")
    $keys = @(Get-ChildItem $KeyFolder -Filter 'update-key*.pem')
    if ($keys.Count -eq 0) { throw "No update key (update-key*.pem) in $KeyFolder." }
    $matched = $false
    foreach ($pem in $keys) {
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $key.ImportFromPem((Get-Content -Raw $pem.FullName))
            if ($key.VerifyData($statement, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256)) { $matched = $true }
        } finally { $key.Dispose() }
    }
    if (-not $matched) { throw "The release signature matches none of the app's update keys for v$Version." }
    $hash
}

if ($Folder) {
    Test-Release (Join-Path $Folder "Tunero.exe")
    return
}

$base = "https://github.com/$Repository/releases/download/v$Version/Tunero.exe"
$temp = Join-Path ([IO.Path]::GetTempPath()) "tunero-verify-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $temp | Out-Null
try {
    $exe = Join-Path $temp "Tunero.exe"
    foreach ($suffix in "", ".sha256", ".sig") { Invoke-WebRequest "$base$suffix" -OutFile "$exe$suffix" -UseBasicParsing }
    Test-Release $exe
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
