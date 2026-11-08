# Builds HeatSync-ArrowFix and creates the release ZIP in .\artifacts
# Requires: .NET SDK 6+ (builds .NET Framework 4.8 targets via reference-assembly packages)
param([string]$Version = "1.1.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pub = Join-Path $root "artifacts\HeatSync-ArrowFix"
$zip = Join-Path $root "artifacts\HeatSync-ArrowFix-v$Version.zip"

dotnet build (Join-Path $root "src\HeatSyncArrowFix\HeatSyncArrowFix.csproj") -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = Join-Path $root "src\HeatSyncArrowFix\bin\Release\net48"

Remove-Item (Join-Path $root "artifacts") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pub | Out-Null
Copy-Item (Join-Path $bin "HeatSync-ArrowFix.exe"), (Join-Path $bin "HeatSyncFix.dll"), (Join-Path $bin "dnlib.dll") $pub
Copy-Item (Join-Path $root "dist\*") $pub
Copy-Item (Join-Path $root "LICENSE") (Join-Path $pub "LICENSE.txt")

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($pub, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Get-ChildItem $pub | Select-Object Name, Length | Format-Table -AutoSize
"ZIP: $zip  SHA256: " + (Get-FileHash $zip).Hash
