[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$DownloadUrl,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesUrl,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$PrivateKeyXmlBase64,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [int]$MinimumOsBuild = 19045
)

$ErrorActionPreference = 'Stop'
$published = [DateTime]::UtcNow.ToString('o')
$sha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$canonical = [string]::Join("`n", @($Version, $published, $MinimumOsBuild, $DownloadUrl, $sha256, $ReleaseNotesUrl))
$privateXml = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PrivateKeyXmlBase64))
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString($privateXml)
    $signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($canonical), 'SHA256'))
}
finally {
    $rsa.Dispose()
}

$manifest = [ordered]@{
    Version = $Version
    PublishedUtc = $published
    MinimumOsBuild = $MinimumOsBuild
    DownloadUrl = $DownloadUrl
    Sha256 = $sha256
    ReleaseNotesUrl = $ReleaseNotesUrl
    Signature = $signature
}
$json = $manifest | ConvertTo-Json
[IO.File]::WriteAllText($OutputPath, $json + "`n", (New-Object Text.UTF8Encoding($false)))
