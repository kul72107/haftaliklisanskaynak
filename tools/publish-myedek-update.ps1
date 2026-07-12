param(
    [string]$Version,
    [string]$UpdateRepo = "D:\BITCH\Yedek-app",
    [string]$Notes = "MYedek otomatik yayin paketi.",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$SourceRoot = Split-Path -Parent $PSScriptRoot
$WorkspaceRoot = Split-Path -Parent $SourceRoot
$AppProject = Join-Path $SourceRoot "src\ModernYedek.App\ModernYedek.App.csproj"
$TestProject = Join-Path $SourceRoot "tests\ModernYedek.Tests\ModernYedek.Tests.csproj"
$TestProgram = Join-Path $SourceRoot "tests\ModernYedek.Tests\Program.cs"
$StagingRoot = Join-Path $WorkspaceRoot "output"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Utf8Text {
    param([string]$Path)

    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8Text {
    param(
        [string]$Path,
        [string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8NoBom)
}

function Invoke-Step {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Git {
    param(
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Invoke-Step -FilePath "git" -Arguments $Arguments -WorkingDirectory $WorkingDirectory
}

function Get-AppVersion {
    param([string]$ProjectPath)

    $text = Read-Utf8Text -Path $ProjectPath
    $match = [regex]::Match($text, "<Version>([^<]+)</Version>")
    if (-not $match.Success) {
        throw "Version tag not found in $ProjectPath"
    }

    return $match.Groups[1].Value
}

function Set-AppVersion {
    param(
        [string]$ProjectPath,
        [string]$OldVersion,
        [string]$NewVersion
    )

    $text = Read-Utf8Text -Path $ProjectPath
    $text = $text -replace "<Version>[^<]+</Version>", "<Version>$NewVersion</Version>"
    $text = $text -replace "<AssemblyVersion>[^<]+</AssemblyVersion>", "<AssemblyVersion>$NewVersion.0</AssemblyVersion>"
    $text = $text -replace "<FileVersion>[^<]+</FileVersion>", "<FileVersion>$NewVersion.0</FileVersion>"
    Write-Utf8Text -Path $ProjectPath -Text $text

    if (Test-Path -LiteralPath $TestProgram) {
        $testText = Read-Utf8Text -Path $TestProgram
        $testText = $testText.Replace($OldVersion, $NewVersion)
        Write-Utf8Text -Path $TestProgram -Text $testText
    }
}

function Commit-And-Push {
    param(
        [string]$RepoPath,
        [string[]]$Paths,
        [string]$Message
    )

    Invoke-Git -Arguments (@("add", "--") + $Paths) -WorkingDirectory $RepoPath

    Push-Location $RepoPath
    try {
        & git diff --cached --quiet
        $hasChanges = $LASTEXITCODE -ne 0
    }
    finally {
        Pop-Location
    }

    if ($hasChanges) {
        Invoke-Git -Arguments @("commit", "-m", $Message) -WorkingDirectory $RepoPath
    }
    else {
        Write-Host "No staged changes in $RepoPath"
    }

    Invoke-Git -Arguments @("fetch", "origin") -WorkingDirectory $RepoPath
    Invoke-Git -Arguments @("rebase", "origin/main") -WorkingDirectory $RepoPath
    Invoke-Git -Arguments @("push", "origin", "HEAD:main") -WorkingDirectory $RepoPath
}

if (-not (Test-Path -LiteralPath $UpdateRepo)) {
    throw "Update repo not found: $UpdateRepo"
}

$oldVersion = Get-AppVersion -ProjectPath $AppProject
if ([string]::IsNullOrWhiteSpace($Version)) {
    $parsed = [version]$oldVersion
    $Version = "$($parsed.Major).$($parsed.Minor).$($parsed.Build + 1)"
}

Write-Host "MYedek publish update: $oldVersion -> $Version"
Set-AppVersion -ProjectPath $AppProject -OldVersion $oldVersion -NewVersion $Version

Invoke-Step -FilePath "dotnet" -Arguments @("build", "ModernYedek.sln") -WorkingDirectory $SourceRoot
Invoke-Step -FilePath "dotnet" -Arguments @("run", "--project", $TestProject) -WorkingDirectory $SourceRoot

$staging = Join-Path $StagingRoot "ModernYedek-$Version-staging"
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

Invoke-Step -FilePath "dotnet" -Arguments @("publish", $AppProject, "-c", "Release", "-o", $staging) -WorkingDirectory $SourceRoot

$releaseDir = Join-Path $UpdateRepo "releases"
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$zipPath = Join-Path $releaseDir "ModernYedek-$Version.zip"
if ((Test-Path -LiteralPath $zipPath) -and -not $Force) {
    throw "Release zip already exists: $zipPath. Use -Force to overwrite."
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash

$manifest = [ordered]@{
    version = $Version
    mandatory = $true
    url = "https://raw.githubusercontent.com/kul72107/Yedek-app/main/releases/ModernYedek-$Version.zip"
    sha256 = $hash
    notes = $Notes
    publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$manifestPath = Join-Path $UpdateRepo "latest.json"
Write-Utf8Text -Path $manifestPath -Text (($manifest | ConvertTo-Json) + [Environment]::NewLine)

Commit-And-Push -RepoPath $SourceRoot -Paths @("src", "tests", "tools") -Message "Publish MYedek $Version source"
Commit-And-Push -RepoPath $UpdateRepo -Paths @("latest.json", "releases/ModernYedek-$Version.zip") -Message "Publish MYedek $Version update"

Write-Host "Done. Version: $Version"
Write-Host "Zip: $zipPath"
Write-Host "SHA256: $hash"
