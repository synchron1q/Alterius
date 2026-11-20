<#
.SYNOPSIS
    Builds Alterius into compact Release packages (framework-dependent and/or self-contained).
.EXAMPLE
    .\build.ps1
    .\build.ps1 -Mode FrameworkDependent
    .\build.ps1 -Mode SelfContained -Runtime win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet("FrameworkDependent", "SelfContained", "Both")]
    [string]$Mode = "Both",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($null -eq $dotnetCmd) {
    Write-Error "dotnet SDK was not found in PATH. Install .NET 8 SDK (x64) from https://dotnet.microsoft.com/download/dotnet/8.0 and retry."
    exit 1
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "src\Alterius\Alterius.csproj"
$publishRoot = Join-Path $repoRoot "publish"
$sourceFfmpegDir = Join-Path $repoRoot "bin\ffmpeg"

if (-not (Test-Path $projectPath)) {
    Write-Error "Project file not found: $projectPath"
    exit 1
}

function Invoke-AgentPublish {
    param(
        [Parameter(Mandatory = $true)][string]$VariantName,
        [Parameter(Mandatory = $true)][bool]$IsSelfContained
    )

    $outDir = Join-Path $publishRoot $VariantName
    Write-Host ""
    Write-Host "=== Publishing $VariantName ($Configuration, $Runtime, SelfContained=$IsSelfContained) ===" -ForegroundColor Cyan

    $publishArgs = @(
        "publish", $projectPath,
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $outDir,
        "-p:PublishSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false"
    )

    if ($IsSelfContained) {
        $publishArgs += @(
            "--self-contained", "true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        $publishArgs += @(
            "--self-contained", "false"
        )
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $VariantName with exit code $LASTEXITCODE."
    }

    $targetFfmpegDir = Join-Path $outDir "bin\ffmpeg"
    if (-not (Test-Path $targetFfmpegDir)) {
        New-Item -ItemType Directory -Path $targetFfmpegDir -Force | Out-Null
    }

    $sourceFfmpegExe = Join-Path $sourceFfmpegDir "ffmpeg.exe"
    if (Test-Path $sourceFfmpegExe) {
        Copy-Item -Path (Join-Path $sourceFfmpegDir "*") -Destination $targetFfmpegDir -Recurse -Force
        Write-Host "Copied ffmpeg binaries from $sourceFfmpegDir -> $targetFfmpegDir" -ForegroundColor Green
    }
    else {
        $readmePlaceholder = "Place ffmpeg.exe (with libx264 and libopus support) in this folder before running Alterius.exe."
        Set-Content -Path (Join-Path $targetFfmpegDir "PLACE_FFMPEG_HERE.txt") -Value $readmePlaceholder -Encoding ASCII
        Write-Warning "ffmpeg.exe was not found at '$sourceFfmpegExe'. Copy ffmpeg.exe into '$targetFfmpegDir' before deployment."
    }

    Write-Host "Output ready: $outDir" -ForegroundColor Green
}

Write-Host "Restoring NuGet packages..." -ForegroundColor Cyan
& dotnet restore $projectPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

if ($Mode -eq "FrameworkDependent" -or $Mode -eq "Both") {
    Invoke-AgentPublish -VariantName "framework-dependent" -IsSelfContained $false
}

if ($Mode -eq "SelfContained" -or $Mode -eq "Both") {
    Invoke-AgentPublish -VariantName "self-contained" -IsSelfContained $true
}

Write-Host ""
Write-Host "Build completed successfully." -ForegroundColor Green
