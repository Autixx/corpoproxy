param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Key,
    [Parameter(Mandatory = $true)][string]$CodePath
)

$plain = [System.IO.File]::ReadAllBytes($InputPath)
$sha = [System.Security.Cryptography.SHA256Managed]::new()
try {
    $keyBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Key))
}
finally {
    $sha.Dispose()
}

$aes = [System.Security.Cryptography.AesManaged]::new()
$aes.Key = $keyBytes
$aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
$aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
$aes.GenerateIV()
$iv = $aes.IV

$encryptor = $aes.CreateEncryptor()
try {
    $cipher = $encryptor.TransformFinalBlock($plain, 0, $plain.Length)
}
finally {
    $encryptor.Dispose()
    $aes.Dispose()
}

$all = New-Object byte[] (16 + $cipher.Length)
[Array]::Copy($iv, 0, $all, 0, 16)
[Array]::Copy($cipher, 0, $all, 16, $cipher.Length)

$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}
[System.IO.File]::WriteAllBytes($OutputPath, $all)

$codeDir = Split-Path -Parent $CodePath
if ($codeDir -and -not (Test-Path $codeDir)) {
    New-Item -ItemType Directory -Path $codeDir | Out-Null
}

$escapedKey = $Key.Replace('"', '""')
$code = @"
namespace CorpVPN.Client.Services;
internal static class BuildSecrets
{
    internal const string SubscriptionsKey = "$escapedKey";
}
"@
[System.IO.File]::WriteAllText($CodePath, $code, [System.Text.UTF8Encoding]::new($false))
