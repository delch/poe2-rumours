# Builds the shippable zip.
#
# The version is READ FROM THE PROJECT, never typed here. A zip whose name disagrees with the build inside it
# is worse than an unnamed one — it is a filename that lies, and three of those had already piled up before
# this script existed.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$version = (dotnet msbuild "$root\src\PoeRumours\PoeRumours.csproj" -getProperty:Version).Trim()
$zip = "$root\PoeRumours-$version-win-x64.zip"

Write-Host "packing $version"

# The running app locks its own exe, and Compress-Archive then fails half-way through.
Get-Process PoeRumours -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item -Recurse -Force "$root\dist" -ErrorAction SilentlyContinue
dotnet publish "$root\src\PoeRumours" -c Release -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$root\dist\*" -DestinationPath $zip -CompressionLevel Optimal

# Never hand over an archive nobody has opened. One was shipped in a format Explorer could not read, and one
# shipped without its readme; both would have been caught here.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
$names = $z.Entries | ForEach-Object { $_.FullName }
$z.Dispose()

foreach ($expected in @('PoeRumours.exe', 'README.txt', 'data\rumours.json', 'data\ui-strings.json')) {
    if ($names -notcontains $expected) { throw "$expected is missing from the zip" }
}

Write-Host "ok: $zip"
$names | ForEach-Object { Write-Host "  $_" }
