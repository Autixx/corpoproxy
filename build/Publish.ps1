param(
    [string]$Configuration = "Release"
)

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\CorpVPN.Client\CorpVPN.Client.csproj"
$outDir = Join-Path $root "artifacts\publish"

& dotnet publish $project -c $Configuration -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o $outDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $outDir"
