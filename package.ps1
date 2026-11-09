# Builds the plugin and makes the release zip: dist\ModManager-<version>.zip
# The zip holds BepInEx\plugins\ModManager\ModManager.dll, so it can be extracted straight into the game folder.
# Close the game first: it locks the plugin DLL.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$version = ([xml](Get-Content ModManager.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dll = Join-Path $PSScriptRoot 'bin\Release\net6.0\ModManager.dll'
New-Item -ItemType Directory -Force dist | Out-Null
$zipPath = Join-Path $PSScriptRoot "dist\ModManager-$version.zip"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dll, 'BepInEx/plugins/ModManager/ModManager.dll')
$zip.Dispose()
$stream.Dispose()
"Made $zipPath"
