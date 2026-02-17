param(
    [Parameter(Mandatory = $true)][string]$File,
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [Parameter(Mandatory = $true)][string]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

& signtool sign /f "$PfxPath" /p "$PfxPassword" /fd SHA256 /tr "$TimestampUrl" /td SHA256 "$File"
exit $LASTEXITCODE
