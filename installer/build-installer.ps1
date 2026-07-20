param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\..\Yedek-app"),
    [string]$OutputName = "MYedekSetup.exe"
)

$ErrorActionPreference = "Stop"

$iexpress = Join-Path $env:WINDIR "System32\iexpress.exe"
if (-not (Test-Path $iexpress)) {
    throw "iexpress.exe bulunamadi. Bu build Windows uzerinde calismalidir."
}

$outputDirFull = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $outputDirFull | Out-Null

$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("myedek-setup-build-" + [Guid]::NewGuid().ToString("N"))
$payloadDir = Join-Path $buildRoot "payload"
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "MYedekSetup.cmd") -Destination $payloadDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "MYedekSetup.ps1") -Destination $payloadDir -Force

    $sedPath = Join-Path $buildRoot "MYedekSetup.sed"
    $targetPath = Join-Path $outputDirFull $OutputName
    $payloadDirForSed = $payloadDir.TrimEnd("\") + "\"

    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$targetPath
FriendlyName=MYedek Setup
AppLaunched=MYedekSetup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="MYedekSetup.cmd"
FILE1="MYedekSetup.ps1"
[SourceFiles]
SourceFiles0=$payloadDirForSed
[SourceFiles0]
%FILE0%=
%FILE1%=
"@

    Set-Content -Path $sedPath -Value $sed -Encoding ASCII
    $process = Start-Process -FilePath $iexpress -ArgumentList @("/N", "/Q", $sedPath) -Wait -PassThru
    if ($process.ExitCode -ne 0 -and -not (Test-Path $targetPath)) {
        throw "iexpress build basarisiz oldu. ExitCode=$($process.ExitCode)"
    }

    for ($attempt = 0; $attempt -lt 60 -and -not (Test-Path $targetPath); $attempt++) {
        Start-Sleep -Milliseconds 500
    }

    if (-not (Test-Path $targetPath)) {
        throw "Installer uretilemedi: $targetPath"
    }

    Get-Item $targetPath
} finally {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}
