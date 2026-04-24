<#
.SYNOPSIS
    Builds the Luna Multiplayer release zip artifacts locally, mirroring the
    AppVeyor pipeline (appveyor.yml) that produces the assets attached to a
    GitHub release (e.g. the 0.29.0 / 0.29.1 release zips).

.DESCRIPTION
    Replicates the before_build / build / after_build / artifacts sections of
    the AppVeyor configuration on a developer workstation. Output zips are
    written to the directory passed to -OutputDir (default: <repo>\release_files,
    which is gitignored).

    Produced zips per configuration (matching appveyor.yml):
        LunaMultiplayer-Client-<cfg>.zip
        LunaMultiplayer-Server-win-x64-<cfg>.zip
        LunaMultiplayer-Server-linux-x64-<cfg>.zip
        LunaMultiplayer-Server-linux-arm64-<cfg>.zip
        LunaMultiplayer-Server-linux-arm32-<cfg>.zip
        LunaMultiplayer-Server-any-<cfg>.zip
        LunaMultiplayerMasterServer-<cfg>.zip

    Historically the 0.29.0 GitHub release shipped only the platform-agnostic
    Client-Release.zip and Server-Release.zip pair. Today's pipeline produces
    the wider set above; this script publishes the same set so a 0.29.1 stable
    release can be assembled from the local output without waiting on
    AppVeyor.

.PARAMETER Configuration
    Build configuration to produce. 'Release', 'Debug', or 'All' (both).
    Defaults to 'Release'.

.PARAMETER OutputDir
    Destination directory for the final zip artifacts. Defaults to
    <repo>\release_files (gitignored).

.PARAMETER NoClean
    Skip wiping the FinalFiles staging directory before building.

.PARAMETER SkipClient
    Skip the LmpClient build/stage/zip steps.

.PARAMETER SkipServer
    Skip the per-RID Server publish/zip steps.

.PARAMETER SkipMasterServer
    Skip the MasterServer publish/zip step.

.EXAMPLE
    .\Scripts\Build-Release.ps1
    Produces only the Release-config zips into .\release_files\.

.EXAMPLE
    .\Scripts\Build-Release.ps1 -Configuration All
    Produces Debug and Release zips into .\release_files\.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'All')]
    [string]$Configuration = 'Release',

    [string]$OutputDir,

    [switch]$NoClean,
    [switch]$SkipClient,
    [switch]$SkipServer,
    [switch]$SkipMasterServer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Repo root is the parent of the Scripts directory this script lives in.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'release_files' }

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 60) -ForegroundColor DarkCyan
    Write-Host (' ' + $Title) -ForegroundColor Cyan
    Write-Host ('=' * 60) -ForegroundColor DarkCyan
}

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string]$Exe,
        [Parameter(ValueFromRemainingArguments)] $Arguments
    )
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "External tool '$Exe' exited with code $LASTEXITCODE"
    }
}

function Find-MSBuild {
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    )
    foreach ($vswhere in $candidates) {
        if (Test-Path $vswhere) {
            $path = & $vswhere -latest -products * `
                -requires Microsoft.Component.MSBuild `
                -find 'MSBuild\**\Bin\MSBuild.exe' |
                Select-Object -First 1
            if ($path -and (Test-Path $path)) { return $path }
        }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "MSBuild not found. Install Visual Studio 2022 (or Build Tools) with the .NET desktop workload."
}

function Find-SevenZip {
    $candidates = @(
        "${env:ProgramFiles}\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    )
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "7-Zip (7z.exe) not found. Install from https://www.7-zip.org/ or add it to PATH."
}

function Find-NuGet {
    param([Parameter(Mandatory)] [string]$RepoRoot)
    # Prefer a repo-local nuget.exe so the script is self-contained and never
    # interferes with whatever NuGet CLI a developer might have on PATH.
    $local = Join-Path $RepoRoot 'External\Nuget\nuget.exe'
    if (Test-Path $local) { return $local }

    $cmd = Get-Command nuget.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # Auto-download the official latest CLI. Small (~8 MB), single-file exe.
    Write-Host "nuget.exe not found - downloading latest CLI to $local"
    $dir = Split-Path $local -Parent
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $url = 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe'
    try {
        # TLS 1.2 is required by nuget.org on older PowerShell hosts.
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $local -UseBasicParsing
    } catch {
        throw "Failed to download nuget.exe from $url. Install it manually to $local. ($_)"
    }
    return $local
}

function New-StagingDirs {
    param([string]$ClientStage)
    $subs = @('Button', 'Plugins', 'Localization', 'PartSync', 'Icons', 'Flags')
    foreach ($sub in $subs) {
        New-Item -ItemType Directory -Force -Path (Join-Path $ClientStage $sub) | Out-Null
    }
}

# --- Resolve tools / preconditions -------------------------------------------------

Push-Location $RepoRoot
try {
    Write-Section "Luna Multiplayer release builder"

    $msbuild  = Find-MSBuild
    $sevenZip = Find-SevenZip
    $dotnet   = (Get-Command dotnet -ErrorAction Stop).Source
    $nuget    = Find-NuGet -RepoRoot $RepoRoot

    $versionPath = Join-Path $RepoRoot 'LunaMultiplayer.version'
    if (-not (Test-Path $versionPath)) { throw "Missing $versionPath" }
    $versionJson = Get-Content -Raw $versionPath | ConvertFrom-Json
    $version = '{0}.{1}.{2}' -f `
        $versionJson.VERSION.MAJOR, $versionJson.VERSION.MINOR, $versionJson.VERSION.PATCH

    $kspDir = Join-Path $RepoRoot 'External\KSPLibraries'
    if (-not (Test-Path (Join-Path $kspDir 'Assembly-CSharp.dll'))) {
        throw "KSP libraries not extracted in $kspDir. Extract KSPLibraries.7z (or place the KSP DLLs there) before building."
    }

    $harmonyDir = Join-Path $RepoRoot 'External\Dependencies\Harmony'
    if (-not (Test-Path $harmonyDir)) {
        throw "Harmony dependency missing at $harmonyDir."
    }

    $clientProj = Join-Path $RepoRoot 'LmpClient\LmpClient.csproj'
    if (-not (Test-Path $clientProj)) { throw "LmpClient project not found at $clientProj" }

    $configsToBuild = if ($Configuration -eq 'All') { @('Debug', 'Release') } else { @($Configuration) }
    $finalRoot = Join-Path $RepoRoot 'FinalFiles'

    Write-Host "Repo root      : $RepoRoot"
    Write-Host "Output dir     : $OutputDir"
    Write-Host "Version        : $version"
    Write-Host "Configurations : $($configsToBuild -join ', ')"
    Write-Host "MSBuild        : $msbuild"
    Write-Host "dotnet         : $dotnet"
    Write-Host "7-Zip          : $sevenZip"
    Write-Host "NuGet          : $nuget"

    if (-not $NoClean) {
        if (Test-Path $finalRoot) {
            Write-Host "Cleaning $finalRoot..."
            Remove-Item $finalRoot -Recurse -Force
        }

        # Wipe obj\ for LmpClient and every legacy non-SDK project it
        # transitively references. Stale `project.assets.json` files left over
        # from a previous `msbuild -restore` attempt cause NuGet's build-time
        # target to fail with the misleading
        #     "Your project does not reference '.NETFramework,Version=v4.6'"
        # error on subsequent builds, even after we switch to standalone
        # `nuget restore`. We keep this list explicit (rather than scanning the
        # whole repo) so we never accidentally nuke obj\ for SDK-style projects
        # that `dotnet publish` will manage on its own.
        $legacyObjDirs = @(
            (Join-Path $RepoRoot 'LmpClient\obj'),
            (Join-Path $RepoRoot 'Lidgren.Net\obj')
        )
        foreach ($dir in $legacyObjDirs) {
            if (Test-Path $dir) {
                Write-Host "Cleaning $dir..."
                Remove-Item $dir -Recurse -Force
            }
        }
    }
    New-Item -ItemType Directory -Force -Path $OutputDir  | Out-Null
    New-Item -ItemType Directory -Force -Path $finalRoot  | Out-Null

    foreach ($cfg in $configsToBuild) {
        $stage       = Join-Path $finalRoot $cfg
        $clientStage = Join-Path $stage 'LMPClient\GameData\LunaMultiplayer'
        New-Item -ItemType Directory -Force -Path $clientStage | Out-Null

        # Copy Harmony into GameData (mirrors the appveyor before_build xcopy /y /s).
        Copy-Item (Join-Path $harmonyDir '*') -Destination (Join-Path $stage 'LMPClient\GameData') -Recurse -Force

        if (-not $SkipClient) {
            # Restore LmpClient's packages.config via the standalone NuGet CLI.
            # This honors .nuget\nuget.config (which redirects repositoryPath to
            # External\Nuget\), populating the layout that LmpClient.csproj's
            # HintPaths expect (..\External\Nuget\<pkg>\lib\<tfm>\*.dll).
            #
            # We cannot use `msbuild -restore` here: that uses the modern
            # PackageReference restore flow, which scans every transitively
            # referenced project. Lidgren.Net.csproj is a legacy non-SDK csproj
            # with no packages.config and no PackageReferences, so the modern
            # restore emits the misleading
            #   "Your project does not reference '.NETFramework,Version=v4.6'"
            # error and fails the build.
            $clientPackagesConfig = Join-Path $RepoRoot 'LmpClient\packages.config'
            if (Test-Path $clientPackagesConfig) {
                Write-Section "NuGet restore LmpClient packages.config"
                Invoke-External $nuget 'restore' $clientPackagesConfig `
                    '-PackagesDirectory' (Join-Path $RepoRoot 'External\Nuget') `
                    '-SolutionDirectory' $RepoRoot `
                    '-NonInteractive' '-Verbosity' 'quiet'
            }

            # Build the LmpClient project ONLY (not the .sln). The .sln pulls in
            # SDK-style projects (Server, MasterServer, ...) and shared projects
            # (LmpCommon.shproj, LmpGlobal.shproj). Legacy MSBuild from VS Build
            # Tools doesn't ship the Microsoft.NET.Sdk resolver or the Shared
            # Project (CodeSharing) targets, which makes a full-solution build
            # explode. The Server / MasterServer projects are published below
            # via `dotnet publish`, which handles the SDK-style projects.
            #
            # SolutionDir must be explicitly supplied because LmpClient.csproj
            # has a PreBuildEvent that runs `xcopy "$(SolutionDir)External\..."`.
            # When MSBuild builds a .csproj directly (no .sln context),
            # $(SolutionDir) evaluates to *Undefined* and the xcopy fails.
            # MSBuild convention: SolutionDir always ends with a separator.
            Write-Section "MSBuild build LmpClient ($cfg)"
            $solutionDir = $RepoRoot.TrimEnd('\','/') + '\'
            Invoke-External $msbuild $clientProj `
                "/p:Configuration=$cfg" '/p:Platform=AnyCPU' `
                "/p:SolutionDir=$solutionDir" `
                '/m' '/v:minimal' '/nologo'

            Write-Section "Stage client artifacts ($cfg)"
            New-StagingDirs -ClientStage $clientStage

            Copy-Item (Join-Path $RepoRoot 'LMP Readme.txt') (Join-Path $stage 'LMP Readme.txt') -Force
            Copy-Item $versionPath (Join-Path $clientStage 'LunaMultiplayer.version') -Force

            # Top-level files in Resources\ (e.g. LMPButton.png) -> Button\
            Get-ChildItem (Join-Path $RepoRoot 'LmpClient\Resources') -File |
                Copy-Item -Destination (Join-Path $clientStage 'Button') -Force

            # LmpClient build output -> Plugins\
            $clientBin = Join-Path $RepoRoot "LmpClient\bin\$cfg"
            if (-not (Test-Path $clientBin)) {
                throw "LmpClient build output missing at $clientBin"
            }
            Get-ChildItem $clientBin -File |
                Copy-Item -Destination (Join-Path $clientStage 'Plugins') -Force

            # Recursive XML trees
            Copy-Item (Join-Path $RepoRoot 'LmpClient\Localization\XML\*') `
                      (Join-Path $clientStage 'Localization') -Recurse -Force
            Copy-Item (Join-Path $RepoRoot 'LmpClient\ModuleStore\XML\*') `
                      (Join-Path $clientStage 'PartSync')     -Recurse -Force

            # Icons / Flags top-level files only (matches xcopy without /s)
            Get-ChildItem (Join-Path $RepoRoot 'LmpClient\Resources\Icons') -File |
                Copy-Item -Destination (Join-Path $clientStage 'Icons') -Force
            Get-ChildItem (Join-Path $RepoRoot 'LmpClient\Resources\Flags') -File |
                Copy-Item -Destination (Join-Path $clientStage 'Flags') -Force
        }

        if (-not $SkipMasterServer) {
            Write-Section "Publish MasterServer ($cfg, linux, framework-dependent)"
            Invoke-External $dotnet 'publish' `
                (Join-Path $RepoRoot 'MasterServer\MasterServer.csproj') `
                '--configuration' $cfg `
                '--output' (Join-Path $stage 'LMPMasterServer') `
                '--os' 'linux' `
                '--self-contained' 'false' `
                '-p:PublishSingleFile=false'
        }

        if (-not $SkipServer) {
            $serverProj = Join-Path $RepoRoot 'Server\Server.csproj'
            $serverRids = @(
                @{ Tag = 'win-x64';     ZipName = 'win-x64';     OsArgs = @('--os','win');                    SelfContained = 'true';  PublishTrimmed = 'true'  },
                @{ Tag = 'linux-x64';   ZipName = 'linux-x64';   OsArgs = @('--os','linux');                  SelfContained = 'true';  PublishTrimmed = 'true'  },
                @{ Tag = 'linux-arm64'; ZipName = 'linux-arm64'; OsArgs = @('--os','linux','--arch','arm64'); SelfContained = 'true';  PublishTrimmed = 'true'  },
                @{ Tag = 'linux-arm';   ZipName = 'linux-arm32'; OsArgs = @('--os','linux','--arch','arm');   SelfContained = 'true';  PublishTrimmed = 'true'  },
                @{ Tag = 'any';         ZipName = 'any';         OsArgs = @();                                SelfContained = 'false'; PublishTrimmed = 'false' }
            )

            foreach ($rid in $serverRids) {
                Write-Section "Publish Server ($cfg, $($rid.Tag))"
                $publishOut = Join-Path $finalRoot ("{0}-{1}\LMPServer" -f $rid.Tag, $cfg)
                $publishArgs = @(
                    'publish', $serverProj,
                    '--configuration', $cfg,
                    '--output', $publishOut
                ) + $rid.OsArgs + @(
                    '--self-contained', $rid.SelfContained,
                    '-p:PublishSingleFile=false',
                    "-p:PublishTrimmed=$($rid.PublishTrimmed)"
                )
                Invoke-External $dotnet @publishArgs
            }
        }

        Write-Section "Package zip artifacts ($cfg)"
        $readme = Join-Path $stage 'LMP Readme.txt'

        if (-not $SkipClient) {
            $clientZip = Join-Path $OutputDir "LunaMultiplayer-Client-$cfg.zip"
            Remove-Item $clientZip -Force -ErrorAction SilentlyContinue
            Invoke-External $sevenZip 'a' '-bd' '-mx=7' $clientZip `
                $readme (Join-Path $stage 'LMPClient\GameData')
        }

        if (-not $SkipServer) {
            $serverZipMap = @(
                @{ Zip = "LunaMultiplayer-Server-win-x64-$cfg.zip";     Src = Join-Path $finalRoot "win-x64-$cfg\LMPServer"     },
                @{ Zip = "LunaMultiplayer-Server-linux-x64-$cfg.zip";   Src = Join-Path $finalRoot "linux-x64-$cfg\LMPServer"   },
                @{ Zip = "LunaMultiplayer-Server-linux-arm64-$cfg.zip"; Src = Join-Path $finalRoot "linux-arm64-$cfg\LMPServer" },
                @{ Zip = "LunaMultiplayer-Server-linux-arm32-$cfg.zip"; Src = Join-Path $finalRoot "linux-arm-$cfg\LMPServer"   },
                @{ Zip = "LunaMultiplayer-Server-any-$cfg.zip";         Src = Join-Path $finalRoot "any-$cfg\LMPServer"         }
            )
            foreach ($entry in $serverZipMap) {
                $dest = Join-Path $OutputDir $entry.Zip
                Remove-Item $dest -Force -ErrorAction SilentlyContinue
                Invoke-External $sevenZip 'a' '-bd' '-mx=7' $dest $readme $entry.Src
            }
        }

        if (-not $SkipMasterServer) {
            $msZip = Join-Path $OutputDir "LunaMultiplayerMasterServer-$cfg.zip"
            Remove-Item $msZip -Force -ErrorAction SilentlyContinue
            Invoke-External $sevenZip 'a' '-bd' '-mx=7' $msZip (Join-Path $stage 'LMPMasterServer')
        }
    }

    Write-Section "Done"
    Write-Host "Release artifacts written to: $OutputDir"
    Get-ChildItem $OutputDir -File | Sort-Object Name |
        Format-Table Name,
            @{ Name = 'Size (MB)'; Expression = { [Math]::Round($_.Length / 1MB, 2) } } -AutoSize
}
finally {
    Pop-Location
}
