<#
.SYNOPSIS
    Build, pack, and push the Vellum NuGet packages to NuGet.org.

.DESCRIPTION
    Vellum has two publishable projects:
      lib -> src/Vellum/Vellum.csproj           (NuGet id: Vellum)
      cli -> src/Vellum.Cli/Vellum.Cli.csproj   (NuGet id: Vellum.Cli, dotnet tool)

    The CLI depends on the library, so "all" publishes lib first then cli.

.PARAMETER ProjectNames
    Which package(s) to publish. Accepts "lib", "cli", "all", or the full
    project name (Vellum / Vellum.Cli). Positional — pass one or more.

.PARAMETER ApiKey
    Your NuGet.org API key. Can also be set via NUGET_API_KEY.

.PARAMETER Version
    Override the package version (e.g. "0.2.0") applied to every project.
    If omitted, each project uses the <Version> declared in its own .csproj.

.PARAMETER Source
    NuGet feed URL. Defaults to https://api.nuget.org/v3/index.json

.PARAMETER SkipBuild
    Skip the dotnet build step (use when you have already built in Release).

.PARAMETER ContinueOnError
    Continue publishing the remaining projects even if one fails.

.EXAMPLE
    .\publish-nuget.ps1 all -ApiKey "oy2abc..."

.EXAMPLE
    .\publish-nuget.ps1 lib cli -Version 0.2.0

.EXAMPLE
    $env:NUGET_API_KEY = "oy2abc..."; .\publish-nuget.ps1 lib
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $ProjectNames,

    [string] $ApiKey   = $env:NUGET_API_KEY,
    [string] $Version  = "",
    [string] $Source   = "https://api.nuget.org/v3/index.json",
    [switch] $SkipBuild,
    [switch] $ContinueOnError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── project registry ─────────────────────────────────────────────────────────
# Order in this hashtable defines the dependency-correct publish order used
# when "all" is requested.  lib first because cli depends on it.
$projects = [ordered]@{
    "lib" = @{
        Csproj      = Join-Path $repoRoot "src/Vellum/Vellum.csproj"
        DisplayName = "Vellum (library)"
    }
    "cli" = @{
        Csproj      = Join-Path $repoRoot "src/Vellum.Cli/Vellum.Cli.csproj"
        DisplayName = "Vellum.Cli (dotnet tool)"
    }
}

# Aliases that map to the canonical short names above.
$aliases = @{
    "lib"        = "lib"
    "library"    = "lib"
    "vellum"     = "lib"
    "cli"        = "cli"
    "tool"       = "cli"
    "vellum.cli" = "cli"
}

function Resolve-ProjectKey {
    param([Parameter(Mandatory)][string] $Name)

    $key = $Name.ToLowerInvariant()
    if ($key -eq "all") { return $projects.Keys }

    if ($aliases.ContainsKey($key)) { return ,$aliases[$key] }

    $available = ($projects.Keys + "all" + $aliases.Keys) | Sort-Object -Unique
    throw "Unknown project '$Name'. Available: $($available -join ', ')"
}

# ── api key guard (once, up front) ───────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Error @"
No API key provided. Pass it with -ApiKey or set the NUGET_API_KEY environment variable:

    `$env:NUGET_API_KEY = "oy2..."
    .\publish-nuget.ps1 $($ProjectNames -join ' ')
"@
}

function Publish-OneProject {
    param(
        [Parameter(Mandatory)][string] $Key,
        [string] $VersionOverride
    )

    $entry   = $projects[$Key]
    $csproj  = $entry.Csproj
    $display = $entry.DisplayName

    if (-not (Test-Path $csproj)) {
        throw "Project file not found: $csproj"
    }

    [xml]$proj = Get-Content $csproj

    $effectiveVersion = $VersionOverride
    if ([string]::IsNullOrWhiteSpace($effectiveVersion)) {
        $effectiveVersion = $proj.Project.PropertyGroup.Version | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($effectiveVersion)) {
            throw "Could not read <Version> from '$csproj'. Pass -Version explicitly."
        }
    }

    $packageId = $proj.Project.PropertyGroup.PackageId | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        $packageId = $proj.Project.PropertyGroup.AssemblyName | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($packageId)) {
            $packageId = [System.IO.Path]::GetFileNameWithoutExtension($csproj)
        }
    }

    $projectDir = Split-Path -Parent $csproj
    $nupkgDir   = Join-Path $projectDir "nupkg"
    $nupkgFile  = Join-Path $nupkgDir "$packageId.$effectiveVersion.nupkg"

    Write-Host ""
    Write-Host "=== Vellum NuGet Publisher ===" -ForegroundColor Cyan
    Write-Host "  Project  : $display"
    Write-Host "  Csproj   : $csproj"
    Write-Host "  Package  : $packageId"
    Write-Host "  Version  : $effectiveVersion"
    Write-Host "  Feed     : $Source"
    Write-Host ""

    if (-not $SkipBuild) {
        Write-Host "[1/3] Building in Release..." -ForegroundColor Yellow
        $buildArgs = @("build", $csproj, "-c", "Release", "--nologo", "/p:Version=$effectiveVersion")
        & dotnet @buildArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build failed for '$packageId'." }
    } else {
        Write-Host "[1/3] Skipping build (-SkipBuild)." -ForegroundColor DarkGray
    }

    Write-Host "[2/3] Packing..." -ForegroundColor Yellow

    if (Test-Path $nupkgDir) { Remove-Item $nupkgDir -Recurse -Force }
    New-Item -ItemType Directory -Path $nupkgDir | Out-Null

    $packArgs = @(
        "pack", $csproj,
        "-c", "Release",
        "--no-build",
        "-o", $nupkgDir,
        "--nologo",
        "/p:Version=$effectiveVersion"
    )
    & dotnet @packArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Pack failed for '$packageId'." }

    if (-not (Test-Path $nupkgFile)) {
        $nupkgFile = Get-ChildItem $nupkgDir -Filter "*.nupkg" |
                     Where-Object { -not $_.Name.EndsWith(".symbols.nupkg") } |
                     Select-Object -First 1 -ExpandProperty FullName
        if (-not $nupkgFile) { throw "No .nupkg file found in '$nupkgDir' after pack." }
    }

    $sizeMB = [math]::Round((Get-Item $nupkgFile).Length / 1MB, 1)
    Write-Host "  Produced : $nupkgFile ($sizeMB MB)" -ForegroundColor DarkGray

    Write-Host "[3/3] Pushing to NuGet..." -ForegroundColor Yellow
    & dotnet nuget push $nupkgFile `
        --api-key $ApiKey `
        --source $Source `
        --skip-duplicate | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Push failed for '$packageId'." }

    Write-Host ""
    Write-Host "Done! Package will appear on nuget.org within ~15 minutes." -ForegroundColor Green
    Write-Host "  https://www.nuget.org/packages/$packageId/$effectiveVersion"
    Write-Host ""

    return [pscustomobject]@{
        Key       = $Key
        PackageId = $packageId
        Version   = $effectiveVersion
        Status    = "Success"
        Error     = $null
    }
}

# ── expand "all" / aliases into a de-duplicated, order-preserving list ───────
$keysToPublish = [System.Collections.Generic.List[string]]::new()
$seen = @{}
foreach ($name in $ProjectNames) {
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    foreach ($resolved in (Resolve-ProjectKey -Name $name)) {
        if (-not $seen.ContainsKey($resolved)) {
            [void]$keysToPublish.Add($resolved)
            $seen[$resolved] = $true
        }
    }
}

if ($keysToPublish.Count -eq 0) {
    throw "No projects resolved from the given names."
}

# ── run for each requested project ───────────────────────────────────────────
$results = [System.Collections.Generic.List[object]]::new()
foreach ($key in $keysToPublish) {
    try {
        $result = Publish-OneProject -Key $key -VersionOverride $Version
        [void]$results.Add($result)
    }
    catch {
        $msg = $_.Exception.Message
        Write-Host ""
        Write-Host "FAILED: $key -> $msg" -ForegroundColor Red
        Write-Host ""
        [void]$results.Add([pscustomobject]@{
            Key       = $key
            PackageId = $null
            Version   = $null
            Status    = "Failed"
            Error     = $msg
        })
        if (-not $ContinueOnError) {
            break
        }
    }
}

# ── summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
foreach ($r in $results) {
    if ($r.Status -eq "Success") {
        Write-Host "  [OK]   $($r.PackageId) $($r.Version)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $($r.Key) -> $($r.Error)" -ForegroundColor Red
    }
}
Write-Host ""

$failed = @($results | Where-Object { $_.Status -ne "Success" })
if ($failed.Count -gt 0) {
    exit 1
}
