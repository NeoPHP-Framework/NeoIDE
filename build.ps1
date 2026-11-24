<#
.SYNOPSIS
    Builds, packs or releases NeoIDE.

.DESCRIPTION
    .\build.ps1                       standalone NeoIDE (no .NET installation needed on the target machine):
                                        dist\NeoIDE\NeoIDE.exe  +  dist\NeoIDE-<version>-win-x64.zip
    .\build.ps1 -Pack                 Velopack package, like the CI: Releases\NeoIDE-win-Setup.exe
    .\build.ps1 -Version x -Release   publishes version x (GitHub Release + automatic update of the installed IDEs)

.PARAMETER Version
    Version number (default: the <Version> of NeoIDE.csproj).

.PARAMETER Install
    Also installs the standalone build for the current user (%LOCALAPPDATA%\Programs\NeoIDE)
    with a Start menu and a desktop shortcut. This copy does not update itself:
    install NeoIDE-win-Setup.exe from the GitHub releases for that.

.PARAMETER Pack
    Builds the Velopack package locally (needs vpk:  dotnet tool install -g vpk).

.PARAMETER Release
    Sets <Version> in NeoIDE.csproj, commits "Release v<Version>", tags v<Version> and pushes.
    The GitHub "Release" workflow then builds the full + delta packages and publishes the release;
    the installed NeoIDE pick it up within a few hours (or with Help > Check for updates).
    A version with a suffix (0.3.0-beta.1) is a pre-release, only offered on the beta channel.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 0.2.0 -Install
    .\build.ps1 -Version 0.2.0 -Release
    .\build.ps1 -Version 0.3.0-beta.1 -Release
#>
param(
    [string]$Version,
    [switch]$Install,
    [switch]$NoZip,
    [switch]$Pack,
    [switch]$Release
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "NeoIDE\NeoIDE.csproj"
$dist = Join-Path $root "dist"
$out = Join-Path $dist "NeoIDE"

# dotnet: PATH first, then the per-user SDK
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if ((Test-Path $userDotnet) -and (-not $dotnet -or -not (& $dotnet --list-sdks 2>$null | Select-String "^11\."))) { $dotnet = $userDotnet }
if (-not $dotnet -and -not $Release) { throw "dotnet SDK not found (install the .NET 11 SDK)." }

if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { $Version = "0.1.0" }

# ------------------------------------------------------------------ release (tag -> GitHub Actions)
if ($Release) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid version '$Version' (1.2.3 or 1.2.3-beta.1)." }
    Push-Location $root
    try {
        if (git status --porcelain) { throw "Commit or stash your changes first (git status is not clean)." }
        if (git tag --list "v$Version") { throw "Tag v$Version already exists." }
        $csproj = [IO.File]::ReadAllText($project)
        $updated = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
        if ($updated -ne $csproj) {
            [IO.File]::WriteAllText($project, $updated, (New-Object Text.UTF8Encoding($true)))
            git add $project
            git commit -q -m "Release v$Version"
        }
        git tag -a "v$Version" -m "NeoIDE $Version"
        git push -q origin HEAD
        if ($LASTEXITCODE -ne 0) { throw "git push failed." }
        git push -q origin "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "git push of the tag failed." }
        Write-Host "v$Version pushed: the release is being built on https://github.com/NeoPHP-Framework/NeoIDE/actions" -ForegroundColor Green
    }
    finally { Pop-Location }
    return
}

# ------------------------------------------------------------------ Velopack package (local)
if ($Pack) {
    $vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
    if (-not $vpk) { throw "vpk not found: dotnet tool install -g vpk" }
    $publish = Join-Path $dist "publish"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    Write-Host "Publishing NeoIDE $Version for Velopack..." -ForegroundColor Cyan
    & $dotnet publish $project -p:PublishProfile=Velopack-win-x64 -p:Version=$Version -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    $env:DOTNET_ROLL_FORWARD = "Major"   # vpk targets an older .NET
    & $vpk pack -u NeoIDE -v $Version -p $publish -e NeoIDE.exe -i (Join-Path $root "NeoIDE\Resources\neoide.ico") `
        --packTitle NeoIDE --packAuthors NeoPHP -o (Join-Path $root "Releases")
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
    Write-Host "  $(Join-Path $root 'Releases\NeoIDE-win-Setup.exe')" -ForegroundColor Green
    return
}

# ------------------------------------------------------------------ standalone build
if (Get-Process NeoIDE -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" }) {
    throw "NeoIDE is running from ${out}: close it before building."
}

Write-Host "Building NeoIDE $Version (self-contained, win-x64)..." -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish $project -p:PublishProfile=Release-win-x64 -p:Version=$Version -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $out "NeoIDE.exe"
if (-not (Test-Path $exe)) { throw "NeoIDE.exe was not produced." }
Write-Host ("  {0}  ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

if (-not $NoZip) {
    $zip = Join-Path $dist "NeoIDE-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
    Write-Host ("  {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA "Programs\NeoIDE"
    if (Get-Process NeoIDE -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" }) {
        throw "NeoIDE is running from ${target}: close it before installing."
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $out "*") $target -Recurse -Force
    $shell = New-Object -ComObject WScript.Shell
    foreach ($lnkDir in @([Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop"))) {
        $lnk = $shell.CreateShortcut((Join-Path $lnkDir "NeoIDE.lnk"))
        $lnk.TargetPath = Join-Path $target "NeoIDE.exe"
        $lnk.WorkingDirectory = $target
        $lnk.IconLocation = (Join-Path $target "NeoIDE.exe") + ",0"
        $lnk.Description = "The IDE for the NeoPHP framework"
        $lnk.Save()
    }
    Write-Host "  Installed in $target (Start menu + desktop shortcuts)" -ForegroundColor Green
}
Write-Host "Done." -ForegroundColor Cyan
