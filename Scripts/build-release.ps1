#requires -Version 5.1
<#
.SYNOPSIS
    Builds fork-local release zips for distribution via GitHub Releases on
    Majestic95/LunaMultiplayer. Mirrors the artifact shape produced by upstream's
    appveyor.yml (lines 62-97 of that file are the reference) so the Client
    zip extracted into a KSP install behaves identically - same GameData layout,
    same Plugins/, same 000_Harmony/ at GameData root, same Localization/, PartSync/,
    Button/, Icons/, Flags/ folders.

.DESCRIPTION
    Produces (into Releases/<Configuration>/):
      - LunaMultiplayer-Client-<Configuration>.zip
      - LunaMultiplayer-Server-win-x64-<Configuration>.zip
      - LunaMultiplayer-Server-linux-x64-<Configuration>.zip
      - LunaMultiplayer-Server-linux-arm64-<Configuration>.zip
      - LunaMultiplayer-Server-any-<Configuration>.zip          (portable, requires .NET 10 on host)

    Skipped vs upstream's appveyor.yml: linux-arm32 (deprecated upstream-side too)
    and the MasterServer zip (we don't ship a master-server registry for the
    private cohort - players connect by direct IP/host). Add either back if a
    later need surfaces; the publish step is one line.

.PARAMETER Configuration
    Build configuration to publish. Defaults to Release. Debug builds can be
    produced for soak diagnostics if needed.

.PARAMETER DotNet
    Path to the .NET 10 SDK dotnet.exe. Defaults to the user-installed location
    C:\Users\<user>\.dotnet\dotnet.exe (per CLAUDE.md "Build & Run").

.PARAMETER SkipBuildLmpClient
    Skip the LmpClient build step. Use when iterating on packaging only - the
    last Release build in LmpClient/bin/Release/ is reused.

.EXAMPLE
    .\Scripts\build-release.ps1
    Builds + zips everything with default settings.

.EXAMPLE
    .\Scripts\build-release.ps1 -Configuration Debug
    Produces Debug-flavoured zips for diagnostic soak.

.NOTES
    Distribution gotcha: LmpGlobal/RepoConstants.cs + LunaMultiplayer.version
    both point at upstream LunaMultiplayer/LunaMultiplayer. Testers who click
    "check for updates" in-game will downgrade to upstream's no-fixes build.
    Release notes MUST tell testers not to click update. When the cohort grows
    beyond a handful of people, fork-edit RepoConstants + regenerate the version
    file with our fork pointers (see project_release_distribution memory note).
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$DotNet = "$env:USERPROFILE\.dotnet\dotnet.exe",

    [switch]$SkipBuildLmpClient
)

$ErrorActionPreference = 'Stop'

# ---------- Locate the repo root ----------
$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Write-Host "Repo root: $RepoRoot" -ForegroundColor Cyan

# ---------- Preflight ----------
if (-not (Test-Path $DotNet)) {
    throw "dotnet.exe not found at $DotNet. Install the .NET 10 SDK (no admin needed) or pass -DotNet to point at an installed copy. See CLAUDE.md 'Build & Run' for the canonical install path."
}

$dotnetVersion = & $DotNet --version
if (-not ($dotnetVersion -like '10.*')) {
    throw "dotnet at $DotNet reports version $dotnetVersion, expected 10.x. The Server project pins .NET 10 in global.json."
}
Write-Host "dotnet: $dotnetVersion at $DotNet" -ForegroundColor Cyan

$kspLibs = Join-Path $RepoRoot 'External\KSPLibraries'
$requiredKspLibs = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.PhysicsModule.dll',
    'UnityEngine.IMGUIModule.dll'
)
foreach ($lib in $requiredKspLibs) {
    $libPath = Join-Path $kspLibs $lib
    if (-not (Test-Path $libPath)) {
        throw "Missing KSP-side DLL: $libPath. Copy from <KSP>\KSP_x64_Data\Managed\ (see CLAUDE.md 'Build & Run - LmpClient')."
    }
}
Write-Host "KSP libs present at $kspLibs" -ForegroundColor Cyan

# ---------- Output staging area ----------
$ReleasesRoot = Join-Path $RepoRoot "Releases\$Configuration"
$FinalFiles   = Join-Path $RepoRoot "FinalFiles\$Configuration"

# Clean stale staging from a prior run; keep the parent dirs.
if (Test-Path $FinalFiles) { Remove-Item -Recurse -Force $FinalFiles }
New-Item -ItemType Directory -Force -Path $FinalFiles  | Out-Null
New-Item -ItemType Directory -Force -Path $ReleasesRoot | Out-Null

# ---------- Build LmpClient (net472, Mono - the KSP plugin DLL) ----------
if ($SkipBuildLmpClient) {
    Write-Host "Skipping LmpClient build (-SkipBuildLmpClient). Reusing LmpClient/bin/$Configuration." -ForegroundColor Yellow
} else {
    Write-Host "Building LmpClient ($Configuration, net472)..." -ForegroundColor Cyan
    & $DotNet build (Join-Path $RepoRoot 'LmpClient\LmpClient.csproj') -c $Configuration -nologo
    if ($LASTEXITCODE -ne 0) { throw "LmpClient build failed (exit $LASTEXITCODE)." }
}

$lmpClientBin = Join-Path $RepoRoot "LmpClient\bin\$Configuration"
if (-not (Test-Path (Join-Path $lmpClientBin 'LmpClient.dll'))) {
    throw "Expected LmpClient.dll under $lmpClientBin not found. Did the LmpClient build actually succeed?"
}

# ---------- Stage the Client GameData layout ----------
# Matches upstream appveyor.yml after_build: LMPClient/GameData/{000_Harmony,LunaMultiplayer/...}
Write-Host "Staging Client GameData layout..." -ForegroundColor Cyan
$clientStage   = Join-Path $FinalFiles 'LMPClient'
$gameDataRoot  = Join-Path $clientStage 'GameData'
$lmpFolder     = Join-Path $gameDataRoot 'LunaMultiplayer'

foreach ($d in @($clientStage, $gameDataRoot, $lmpFolder,
                 (Join-Path $lmpFolder 'Plugins'),
                 (Join-Path $lmpFolder 'Button'),
                 (Join-Path $lmpFolder 'Localization'),
                 (Join-Path $lmpFolder 'PartSync'),
                 (Join-Path $lmpFolder 'Icons'),
                 (Join-Path $lmpFolder 'Flags'))) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# 000_Harmony loads via folder-name-prefix ordering so it sits at GameData ROOT,
# not under LunaMultiplayer/.
$harmonySrc = Join-Path $RepoRoot 'External\Dependencies\Harmony'
Copy-Item -Recurse -Force -Path (Join-Path $harmonySrc '*') -Destination $gameDataRoot

# Plugin DLLs from the build output + the non-Harmony external dependencies.
Copy-Item -Force -Path (Join-Path $lmpClientBin '*.*') -Destination (Join-Path $lmpFolder 'Plugins') -Exclude '*.pdb'
$externalDeps = Get-ChildItem -Path (Join-Path $RepoRoot 'External\Dependencies') -File
foreach ($dep in $externalDeps) {
    Copy-Item -Force -Path $dep.FullName -Destination (Join-Path $lmpFolder 'Plugins')
}

# Texture / XML resources.
Copy-Item -Force -Path (Join-Path $RepoRoot 'LmpClient\Resources\*.png')             -Destination (Join-Path $lmpFolder 'Button')
Copy-Item -Recurse -Force -Path (Join-Path $RepoRoot 'LmpClient\Localization\XML\*') -Destination (Join-Path $lmpFolder 'Localization')
Copy-Item -Recurse -Force -Path (Join-Path $RepoRoot 'LmpClient\ModuleStore\XML\*')  -Destination (Join-Path $lmpFolder 'PartSync')
Copy-Item -Force -Path (Join-Path $RepoRoot 'LmpClient\Resources\Icons\*.*')         -Destination (Join-Path $lmpFolder 'Icons')
Copy-Item -Force -Path (Join-Path $RepoRoot 'LmpClient\Resources\Flags\*.*')         -Destination (Join-Path $lmpFolder 'Flags')

# Update-check metadata file.
Copy-Item -Force -Path (Join-Path $RepoRoot 'LunaMultiplayer.version') -Destination (Join-Path $lmpFolder 'LunaMultiplayer.version')

# Readme alongside the GameData folder.
$readmeSrc = Join-Path $RepoRoot 'LMP Readme.txt'
Copy-Item -Force -Path $readmeSrc -Destination (Join-Path $FinalFiles 'LMP Readme.txt')

# ---------- Build the Server publish outputs (one per RID) ----------
$serverProject = Join-Path $RepoRoot 'Server\Server.csproj'
$serverRids = @(
    @{ Name = 'win-x64';      OsArg = '--os win';                 SelfContained = '--self-contained true'  },
    @{ Name = 'linux-x64';    OsArg = '--os linux';               SelfContained = '--self-contained true'  },
    @{ Name = 'linux-arm64';  OsArg = '--os linux --arch arm64';  SelfContained = '--self-contained true'  },
    @{ Name = 'any';          OsArg = '';                         SelfContained = '--self-contained false' }
)

foreach ($rid in $serverRids) {
    $publishOut = Join-Path $FinalFiles "$($rid.Name)\LMPServer"
    Write-Host "Publishing Server ($($rid.Name))..." -ForegroundColor Cyan
    $cmd = "$DotNet publish `"$serverProject`" --configuration $Configuration --output `"$publishOut`" $($rid.OsArg) $($rid.SelfContained) -p:PublishSingleFile=false -p:PublishTrimmed=false --nologo"
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) { throw "Server publish for $($rid.Name) failed (exit $LASTEXITCODE)." }
}

# ---------- Compress to zip ----------
# Compress-Archive is built-in (no 7zip dependency) but does NOT preserve Unix
# execute bits. Linux/ARM hosts must `chmod +x Server` (or `dotnet Server.dll`
# for the portable build) after extracting. Document in the release notes.
Write-Host "Producing release zips..." -ForegroundColor Cyan

function New-ReleaseZip {
    param(
        [Parameter(Mandatory)][string]$ZipName,
        [Parameter(Mandatory)][string[]]$SourcePaths
    )
    $zipPath = Join-Path $ReleasesRoot $ZipName
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $SourcePaths -DestinationPath $zipPath -CompressionLevel Optimal
    $sizeBytes = (Get-Item $zipPath).Length
    "{0}  {1,10:N0} bytes" -f $ZipName, $sizeBytes | Write-Host -ForegroundColor Green
}

# Client zip: GameData/ + readme. Players extract GameData INTO their KSP folder.
New-ReleaseZip -ZipName "LunaMultiplayer-Client-$Configuration.zip" -SourcePaths @(
    (Join-Path $FinalFiles 'LMP Readme.txt'),
    (Join-Path $clientStage 'GameData')
)

# Server zips: LMPServer folder + readme.
foreach ($rid in $serverRids) {
    New-ReleaseZip -ZipName "LunaMultiplayer-Server-$($rid.Name)-$Configuration.zip" -SourcePaths @(
        (Join-Path $FinalFiles 'LMP Readme.txt'),
        (Join-Path $FinalFiles "$($rid.Name)\LMPServer")
    )
}

# ---------- Summary ----------
Write-Host ""
Write-Host "Done. Zips are in: $ReleasesRoot" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  Recommended (gh CLI):"
Write-Host "    gh release create <TAG> ``"
Write-Host "      --repo Majestic95/LunaMultiplayer ``"
Write-Host "      --title `"<TITLE>`" ``"
Write-Host "      --notes-file <path-to-release-notes.md> ``"
Write-Host "      --prerelease ``"
Write-Host "      $ReleasesRoot\LunaMultiplayer-Client-$Configuration.zip ``"
Write-Host "      $ReleasesRoot\LunaMultiplayer-Server-win-x64-$Configuration.zip ``"
Write-Host "      $ReleasesRoot\LunaMultiplayer-Server-linux-x64-$Configuration.zip ``"
Write-Host "      $ReleasesRoot\LunaMultiplayer-Server-linux-arm64-$Configuration.zip ``"
Write-Host "      $ReleasesRoot\LunaMultiplayer-Server-any-$Configuration.zip"
Write-Host ""
Write-Host "  IMPORTANT: the --repo flag is mandatory because this repo has both"
Write-Host "  origin (Majestic95) and upstream (LunaMultiplayer) remotes configured."
Write-Host "  Without --repo, gh defaults to upstream and fails with a misleading"
Write-Host "  'workflow scope may be required' error."
Write-Host ""
Write-Host "  Alternative (manual drag-drop):"
Write-Host "    1. https://github.com/Majestic95/LunaMultiplayer/releases/new"
Write-Host "    2. Tag with the fork version (e.g. v0.30.0-private-1)"
Write-Host "    3. Drag-drop the 5 zips from $ReleasesRoot"
Write-Host ""
Write-Host "  In the release notes: tell testers NOT to click 'check for updates'"
Write-Host "  in-game - it points at upstream LunaMultiplayer and will downgrade"
Write-Host "  them to the no-fixes build. See LmpGlobal/RepoConstants.cs."
Write-Host ""
