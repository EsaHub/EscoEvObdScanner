# Builds a self-contained EscoEvObdScanner.App and packs it into an MSI.
#   .\build-installer.ps1              -> version from Directory.Build.props
#   .\build-installer.ps1 -Version 0.2.0
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$app = Join-Path $root "publish\app"

# a running copy locks its exe: the delete would stop half-way and leave publish\app without its car files and help
if (Get-Process EscoEvObdScanner.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) }) {
    throw "Close the app first: publish\app\EscoEvObdScanner.App.exe is running."
}
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

# PathMap: the binaries record source paths as /_/..., not the build machine's folders
dotnet publish "$root\EscoEvObdScanner.App\EscoEvObdScanner.App.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    "-p:PathMap=$root=/_/" -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

dotnet build "$root\EscoEvObdScanner.Installer\EscoEvObdScanner.Installer.wixproj" -c Release `
    "-p:PublishDir=$app" -p:ProductVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

# the Release build of this run (bin\<platform>\Release), not an older or Debug one
$msi = Get-ChildItem "$root\EscoEvObdScanner.Installer\bin" -Filter EscoEvObdScanner.msi -Recurse |
    Where-Object { $_.FullName -match '\\Release\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw "installer not found under EscoEvObdScanner.Installer\bin" }
Copy-Item $msi.FullName (Join-Path $root "publish\EscoEvObdScanner-$Version.msi") -Force
Write-Host "Done: publish\EscoEvObdScanner-$Version.msi and publish\app\EscoEvObdScanner.App.exe"
