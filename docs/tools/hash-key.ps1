param(
    [Parameter(Mandatory = $true)]
    [string]$Key
)

$normalized = $Key.Trim().ToUpperInvariant()
$bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
$hash = [Security.Cryptography.SHA256]::HashData($bytes)
[Convert]::ToHexString($hash)
