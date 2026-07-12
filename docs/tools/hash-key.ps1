param(
    [Parameter(Mandatory = $true)]
    [string]$Key
)

$builder = New-Object System.Text.StringBuilder
foreach ($character in $Key.Normalize([Text.NormalizationForm]::FormKC).ToCharArray()) {
    $category = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($character)
    if ([char]::IsWhiteSpace($character) -or [char]::IsControl($character) -or $category -eq [Globalization.UnicodeCategory]::Format) {
        continue
    }

    $code = [int][char]$character
    if ($code -in @(0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015, 0x2212)) {
        [void]$builder.Append('-')
    }
    else {
        [void]$builder.Append([char]::ToUpperInvariant($character))
    }
}

$normalized = $builder.ToString()
$bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
$sha = [Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash($bytes)
[BitConverter]::ToString($hash).Replace('-', '')
