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

    [switch]$SkipBuildLmpClient,

    # Path to the Majestic95/LunaCompat fork working copy. LunaCompat ships
    # the per-agency Mod-compat S5 ([x] Science filter) and other client-side
    # mod integrations the Luna Multiplayer fork depends on for the per-
    # agency career experience. Defaults to a sibling directory; pass
    # explicitly when the fork is checked out elsewhere. Pass an empty
    # string or use -SkipBuildLunaCompat to skip LunaCompat bundling
    # entirely (the resulting Client zip will lack the LunaCompat.dll +
    # XML_COMPAT folder - usable but per-agency mod integrations are
    # absent).
    [string]$LunaCompatRepoPath = "$PSScriptRoot\..\..\luna-compat-perAgency",

    [switch]$SkipBuildLunaCompat
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

# ---------- Build + bundle LunaCompat sidecar ----------
# The Majestic95/LunaCompat fork hosts Mod-compat S5 ([x] Science!
# per-agency filter) and other client-side per-agency integrations that
# the main fork's LmpClient.dll exposes API surfaces for (AgencySystem +
# SettingsServerStructure.PerAgencyCareerEnabled). LunaCompat is a
# separate codebase but ships in the same Client zip so operators get a
# single-zip install. See [[project-lunacompat-s5-s6-pickup]] for the
# rationale.
#
# Build mechanics:
#   1. LunaCompat's csproj references $(KSPRoot)/GameData/{LunaMultiplayer
#      /Plugins/{LmpClient,LmpCommon}.dll, 000_Harmony/0Harmony.dll,
#      ModuleManager.4.2.3.dll}. The csproj also embeds KSPBuildTools
#      which resolves Assembly-CSharp / UnityEngine / mscorlib from
#      $(KSPRoot)/KSP_x64_Data/Managed/. Constructing a synthetic
#      build-only KSPRoot would require copying or junctioning the
#      entire KSP_x64_Data folder (~200MB), heavier than reusing
#      $env:KSPRoot directly.
#   2. We DEPLOY the freshly-built LmpClient.dll + LmpCommon.dll to
#      $env:KSPRoot/GameData/LunaMultiplayer/Plugins/ before invoking
#      the LunaCompat build. This guarantees LunaCompat picks up THIS
#      build's per-agency API surfaces (AgencySystem +
#      SettingsServerStructure.PerAgencyCareerEnabled) regardless of
#      what was previously deployed. Idempotent: same bytes deployed
#      twice = no-op; the operator's install ends up matching what's
#      about to ship.
#   3. ModuleManager.4.2.3.dll comes from the operator's $env:KSPRoot
#      install (operators install MM via CKAN). Fail-fast if missing.
#   4. After build, copy LunaCompat's Build/GameData/LunaCompat output
#      AND its XML_COMPAT splice (Build/GameData/LunaMultiplayer/
#      PartSync/LunaCompat) into the Client stage's GameData.
if ($SkipBuildLunaCompat) {
    Write-Host "Skipping LunaCompat build (-SkipBuildLunaCompat). The Client zip will NOT include LunaCompat - per-agency mod integrations (X-Science filter etc.) will be absent." -ForegroundColor Yellow
} elseif (-not $LunaCompatRepoPath -or -not (Test-Path $LunaCompatRepoPath)) {
    Write-Host "LunaCompat repo path '$LunaCompatRepoPath' not found. Skipping LunaCompat bundling. Either clone Majestic95/LunaCompat at that path, pass -LunaCompatRepoPath PATH, or pass -SkipBuildLunaCompat to suppress this warning." -ForegroundColor Yellow
} else {
    Write-Host "Building LunaCompat sidecar from $LunaCompatRepoPath..." -ForegroundColor Cyan

    $operatorKspRoot = $env:KSPRoot
    if (-not $operatorKspRoot) {
        throw "LunaCompat build requires `$env:KSPRoot to be set to a KSP install with ModuleManager.4.2.3.dll under GameData/ and KSP_x64_Data/ alongside. Either set the env var, or pass -SkipBuildLunaCompat."
    }
    $mmSrc = Join-Path $operatorKspRoot 'GameData\ModuleManager.4.2.3.dll'
    if (-not (Test-Path $mmSrc)) {
        throw "ModuleManager.4.2.3.dll not found at $mmSrc. Install ModuleManager 4.2.3 in the `$env:KSPRoot install, or pass -SkipBuildLunaCompat."
    }
    $kspManaged = Join-Path $operatorKspRoot 'KSP_x64_Data\Managed'
    if (-not (Test-Path $kspManaged)) {
        throw "Expected $kspManaged not found - the KSPRoot env var must point at the KSP install root (containing both GameData/ and KSP_x64_Data/), not at the GameData folder."
    }

    # Deploy freshly-built LMP client DLLs to the operator's KSP install
    # so LunaCompat compiles against THIS build. The operator's install
    # is intentionally treated as a build-time staging surface; this is
    # the same flow operators already use when iterating on LmpClient
    # changes manually (Scripts/CopyToKSPDirectory.bat).
    Write-Host "  Deploying freshly-built LmpClient/LmpCommon to $operatorKspRoot\GameData\LunaMultiplayer\Plugins\..." -ForegroundColor Cyan
    $lmpPluginsDeploy = Join-Path $operatorKspRoot 'GameData\LunaMultiplayer\Plugins'
    if (-not (Test-Path $lmpPluginsDeploy)) {
        throw "Expected $lmpPluginsDeploy not found. Install Luna Multiplayer in this KSP root first (any version - we'll overwrite the LMP DLLs); see CLAUDE.md 'Build & Run - LmpClient'."
    }
    foreach ($dllName in @('LmpClient.dll', 'LmpCommon.dll')) {
        $src = Join-Path $lmpClientBin $dllName
        if (Test-Path $src) {
            Copy-Item -Force -Path $src -Destination (Join-Path $lmpPluginsDeploy $dllName)
        }
    }

    $lcSolution = Join-Path $LunaCompatRepoPath 'LunaCompat.sln'
    if (-not (Test-Path $lcSolution)) {
        throw "LunaCompat.sln not found at $lcSolution. Either the LunaCompat repo path is wrong, or the fork structure has drifted."
    }

    & $DotNet build $lcSolution -c $Configuration -p:UseMultiDllLmp=true -nologo
    if ($LASTEXITCODE -ne 0) { throw "LunaCompat build failed (exit $LASTEXITCODE). Check the build log for unresolved references - typically a stale LmpClient.dll lacking the per-agency API surfaces." }

    # Stage LunaCompat output into the Client GameData. The csproj writes
    # to (LunaCompatRepoPath)/Build/GameData/LunaCompat AND
    # (LunaCompatRepoPath)/Build/GameData/LunaMultiplayer/PartSync/LunaCompat
    # (the XML_COMPAT splice). Both need to land in the Client stage.
    $lcBuildOutput = Join-Path $LunaCompatRepoPath 'Build\GameData'
    if (-not (Test-Path $lcBuildOutput)) {
        throw "LunaCompat build output not found at $lcBuildOutput. The csproj wires its output via the BinariesOutputRelativePath / XmlDestinationDir properties - if either was changed, this script needs updating."
    }

    $lcStagedFolder = Join-Path $lcBuildOutput 'LunaCompat'
    if (Test-Path $lcStagedFolder) {
        Write-Host "  Staging GameData/LunaCompat/ to Client zip..." -ForegroundColor Cyan
        Copy-Item -Recurse -Force -Path $lcStagedFolder -Destination $gameDataRoot

        # KSPBuildTools side-effect: LunaCompat's build emits a copy of
        # mscorlib.dll (~4MB) and System*.dll alongside the LunaCompat.dll
        # under Build/GameData/LunaCompat/. KSP's Mono runtime ignores these
        # at load time (it uses its own mscorlib from KSP_x64_Data/Managed/)
        # but their presence bloats the Client zip. Strip them post-copy.
        $strayBuildArtifacts = @(
            'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll',
            'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll',
            'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
            'UnityEngine.PhysicsModule.dll', 'UnityEngine.IMGUIModule.dll'
        )
        $stagedLcFolder = Join-Path $gameDataRoot 'LunaCompat'
        foreach ($stray in $strayBuildArtifacts) {
            $strayPath = Join-Path $stagedLcFolder $stray
            if (Test-Path $strayPath) { Remove-Item -Force $strayPath }
        }
    } else {
        Write-Host "  Warning: $lcStagedFolder missing - LunaCompat DLL won't be in the Client zip." -ForegroundColor Yellow
    }

    $lcXmlSplice = Join-Path $lcBuildOutput 'LunaMultiplayer\PartSync\LunaCompat'
    if (Test-Path $lcXmlSplice) {
        Write-Host "  Staging GameData/LunaMultiplayer/PartSync/LunaCompat/ to Client zip..." -ForegroundColor Cyan
        $partSyncDest = Join-Path $lmpFolder 'PartSync\LunaCompat'
        New-Item -ItemType Directory -Force -Path $partSyncDest | Out-Null
        Copy-Item -Recurse -Force -Path (Join-Path $lcXmlSplice '*') -Destination $partSyncDest
    }

    Write-Host "  LunaCompat bundled." -ForegroundColor Green
}

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
