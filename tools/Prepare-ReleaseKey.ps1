[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyXmlBase64,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$privateXml = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PrivateKeyXmlBase64))
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString($privateXml)
    $publicXml = $rsa.ToXmlString($false)
    [IO.File]::WriteAllText($OutputPath, $publicXml, (New-Object Text.UTF8Encoding($false)))
}
finally {
    $rsa.Dispose()
}
