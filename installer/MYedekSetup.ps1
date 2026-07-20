param(
    [string]$ManifestUrl = "https://raw.githubusercontent.com/kul72107/Yedek-app/main/latest.json"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

[System.Windows.Forms.Application]::EnableVisualStyles()

try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$productName = "MYedek"
$publisher = "ResurrectSoft"
$defaultInstallDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "ModernYedek"

function New-Label {
    param([string]$Text, [int]$X, [int]$Y, [int]$Width, [int]$Height = 22)

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    $label.Size = New-Object System.Drawing.Size($Width, $Height)
    $label
}

function Set-Status {
    param([string]$Text, [int]$Value)

    $statusLabel.Text = $Text
    $progressBar.Value = [Math]::Max($progressBar.Minimum, [Math]::Min($progressBar.Maximum, $Value))
    [System.Windows.Forms.Application]::DoEvents()
}

function New-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory,
        [string]$IconPath
    )

    $shortcutDir = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path $shortcutDir)) {
        New-Item -ItemType Directory -Force -Path $shortcutDir | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $IconPath
    $shortcut.Description = "MYedek yedekleme uygulamasi"
    $shortcut.Save()
}

function Stop-InstalledApp {
    param([string]$InstallDir)

    $fullInstallDir = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd("\") + "\"
    $running = Get-Process -Name "ModernYedek.App" -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith($fullInstallDir, [StringComparison]::OrdinalIgnoreCase)
        } catch {
            $false
        }
    }

    if (-not $running) {
        return
    }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        "MYedek su anda calisiyor. Kuruluma devam etmek icin kapatilacak.",
        "MYedek Kurulum",
        [System.Windows.Forms.MessageBoxButtons]::OKCancel,
        [System.Windows.Forms.MessageBoxIcon]::Information)

    if ($answer -ne [System.Windows.Forms.DialogResult]::OK) {
        throw "Kurulum iptal edildi. MYedek kapatilmadan dosyalar guncellenemez."
    }

    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

function Write-UninstallEntry {
    param([string]$InstallDir, [string]$Version)

    $uninstallScript = Join-Path $InstallDir "Uninstall-MYedek.ps1"
    $script = @"
`$ErrorActionPreference = "SilentlyContinue"
Add-Type -AssemblyName System.Windows.Forms
`$installDir = "$($InstallDir.Replace('"', '""'))"
`$answer = [System.Windows.Forms.MessageBox]::Show("MYedek kaldirilsin mi?", "MYedek Kaldir", [System.Windows.Forms.MessageBoxButtons]::OKCancel, [System.Windows.Forms.MessageBoxIcon]::Question)
if (`$answer -ne [System.Windows.Forms.DialogResult]::OK) { exit 1 }
Get-Process -Name "ModernYedek.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -LiteralPath ([Environment]::GetFolderPath("DesktopDirectory") + "\MYedek.lnk") -Force
Remove-Item -LiteralPath ([Environment]::GetFolderPath("Programs") + "\MYedek\MYedek.lnk") -Force
Remove-Item -LiteralPath ([Environment]::GetFolderPath("Programs") + "\MYedek") -Force
Remove-Item -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MYedek" -Recurse -Force
Start-Process -FilePath "cmd.exe" -ArgumentList "/c timeout /t 1 /nobreak > nul & rmdir /s /q ""`$installDir"""" -WindowStyle Hidden
"@

    Set-Content -Path $uninstallScript -Value $script -Encoding UTF8

    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MYedek"
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "MYedek" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value $Version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "Publisher" -Value $publisher -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $InstallDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value (Join-Path $InstallDir "ModernYedek.App.exe") -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`"" -PropertyType String -Force | Out-Null
}

function Install-MYedek {
    $installDir = $installPathBox.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($installDir)) {
        throw "Kurulum klasoru bos olamaz."
    }

    $installDir = [Environment]::ExpandEnvironmentVariables($installDir)
    $installDir = [System.IO.Path]::GetFullPath($installDir)

    Set-Status "Manifest indiriliyor..." 8
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("MYedekSetup-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    try {
        $client = New-Object System.Net.WebClient
        $client.Headers.Add("User-Agent", "MYedekSetup/1.0")

        $manifestText = $client.DownloadString($ManifestUrl)
        $manifest = $manifestText | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($manifest.url) -or [string]::IsNullOrWhiteSpace($manifest.sha256)) {
            throw "latest.json eksik veya gecersiz."
        }

        Set-Status "MYedek $($manifest.version) indiriliyor..." 25
        $zipPath = Join-Path $workDir ("ModernYedek-" + $manifest.version + ".zip")
        $client.DownloadFile([string]$manifest.url, $zipPath)

        Set-Status "Indirilen dosya dogrulaniyor..." 48
        $actualHash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
        if (-not [string]::Equals($actualHash, [string]$manifest.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA256 dogrulamasi basarisiz. Dosya guvenli olmadigi icin kurulmadi."
        }

        Set-Status "Paket aciliyor..." 62
        $extractDir = Join-Path $workDir "extract"
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDir)

        Set-Status "Calisan uygulama kontrol ediliyor..." 72
        Stop-InstalledApp -InstallDir $installDir

        Set-Status "Dosyalar kuruluyor..." 82
        New-Item -ItemType Directory -Force -Path $installDir | Out-Null
        Copy-Item -Path (Join-Path $extractDir "*") -Destination $installDir -Recurse -Force

        $appExe = Join-Path $installDir "ModernYedek.App.exe"
        if (-not (Test-Path $appExe)) {
            throw "Kurulum tamamlanamadi. ModernYedek.App.exe bulunamadi."
        }

        Set-Status "Kisayollar olusturuluyor..." 90
        if ($desktopCheck.Checked) {
            New-Shortcut `
                -ShortcutPath (Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "MYedek.lnk") `
                -TargetPath $appExe `
                -WorkingDirectory $installDir `
                -IconPath $appExe
        }

        if ($startMenuCheck.Checked) {
            New-Shortcut `
                -ShortcutPath (Join-Path ([Environment]::GetFolderPath("Programs")) "MYedek\MYedek.lnk") `
                -TargetPath $appExe `
                -WorkingDirectory $installDir `
                -IconPath $appExe
        }

        Write-UninstallEntry -InstallDir $installDir -Version ([string]$manifest.version)

        Set-Status "Kurulum tamamlandi." 100
        if ($launchCheck.Checked) {
            Start-Process -FilePath $appExe -WorkingDirectory $installDir
        }

        [System.Windows.Forms.MessageBox]::Show(
            "MYedek $($manifest.version) kuruldu.",
            "MYedek Kurulum",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null

        $form.Close()
    } finally {
        if (Test-Path $workDir) {
            Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "MYedek Kurulum"
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.ClientSize = New-Object System.Drawing.Size(560, 330)

$title = New-Label -Text "MYedek Kurulum" -X 20 -Y 18 -Width 520 -Height 28
$title.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($title)

$summary = New-Label -Text "En guncel MYedek surumu GitHub'dan indirilir, dogrulanir ve bu bilgisayara kurulur." -X 22 -Y 55 -Width 510 -Height 34
$form.Controls.Add($summary)

$pathLabel = New-Label -Text "Kurulum klasoru" -X 22 -Y 102 -Width 160
$form.Controls.Add($pathLabel)

$installPathBox = New-Object System.Windows.Forms.TextBox
$installPathBox.Location = New-Object System.Drawing.Point(22, 126)
$installPathBox.Size = New-Object System.Drawing.Size(408, 26)
$installPathBox.Text = $defaultInstallDir
$form.Controls.Add($installPathBox)

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Text = "Degistir..."
$browseButton.Location = New-Object System.Drawing.Point(440, 124)
$browseButton.Size = New-Object System.Drawing.Size(96, 30)
$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "MYedek kurulum klasorunu secin"
    $dialog.SelectedPath = $installPathBox.Text
    if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $installPathBox.Text = $dialog.SelectedPath
    }
})
$form.Controls.Add($browseButton)

$desktopCheck = New-Object System.Windows.Forms.CheckBox
$desktopCheck.Text = "Masaustu kisayolu olustur"
$desktopCheck.Location = New-Object System.Drawing.Point(24, 170)
$desktopCheck.Size = New-Object System.Drawing.Size(250, 24)
$desktopCheck.Checked = $true
$form.Controls.Add($desktopCheck)

$startMenuCheck = New-Object System.Windows.Forms.CheckBox
$startMenuCheck.Text = "Baslat Menusu kisayolu olustur"
$startMenuCheck.Location = New-Object System.Drawing.Point(24, 198)
$startMenuCheck.Size = New-Object System.Drawing.Size(280, 24)
$startMenuCheck.Checked = $true
$form.Controls.Add($startMenuCheck)

$launchCheck = New-Object System.Windows.Forms.CheckBox
$launchCheck.Text = "Kurulumdan sonra MYedek'i baslat"
$launchCheck.Location = New-Object System.Drawing.Point(24, 226)
$launchCheck.Size = New-Object System.Drawing.Size(290, 24)
$launchCheck.Checked = $true
$form.Controls.Add($launchCheck)

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point(22, 262)
$progressBar.Size = New-Object System.Drawing.Size(514, 18)
$progressBar.Minimum = 0
$progressBar.Maximum = 100
$form.Controls.Add($progressBar)

$statusLabel = New-Label -Text "Kuruluma hazir." -X 22 -Y 286 -Width 330
$form.Controls.Add($statusLabel)

$installButton = New-Object System.Windows.Forms.Button
$installButton.Text = "Kur"
$installButton.Location = New-Object System.Drawing.Point(360, 292)
$installButton.Size = New-Object System.Drawing.Size(84, 30)
$installButton.Add_Click({
    try {
        $installButton.Enabled = $false
        $browseButton.Enabled = $false
        $installPathBox.Enabled = $false
        $desktopCheck.Enabled = $false
        $startMenuCheck.Enabled = $false
        $launchCheck.Enabled = $false
        Install-MYedek
    } catch {
        Set-Status "Kurulum basarisiz." 0
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            "MYedek Kurulum Hatasi",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        $installButton.Enabled = $true
        $browseButton.Enabled = $true
        $installPathBox.Enabled = $true
        $desktopCheck.Enabled = $true
        $startMenuCheck.Enabled = $true
        $launchCheck.Enabled = $true
    }
})
$form.Controls.Add($installButton)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = "Iptal"
$cancelButton.Location = New-Object System.Drawing.Point(452, 292)
$cancelButton.Size = New-Object System.Drawing.Size(84, 30)
$cancelButton.Add_Click({ $form.Close() })
$form.Controls.Add($cancelButton)

[System.Windows.Forms.Application]::Run($form)
