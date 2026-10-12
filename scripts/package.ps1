param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.5',
    [string]$Dotnet = 'dotnet',
    [string]$NuGetConfig = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$archive = Join-Path $artifacts "Toolbox-$Version-win-x64.zip"
if (Test-Path -LiteralPath $publish) {
    throw "Publish directory already exists: $publish. Archive or remove it explicitly before packaging again."
}
$arguments = @('publish', "$root/src/Toolbox.App/Toolbox.App.csproj", '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:Platform=x64', "-p:Version=$Version", '-p:RestoreLockedMode=true', '-o', $publish)
if ($NuGetConfig) { $arguments += "-p:RestoreConfigFile=$NuGetConfig" }
& $Dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
foreach ($required in @('Toolbox.exe', 'Toolbox.pri', 'App.xbf', 'MainWindow.xbf', 'Views/LibraryView.xbf', 'Views/ToolHomeView.xbf', 'Views/FileToolsView.xbf', 'Views/ImageViewerView.xbf', 'Toolbox.Platform.dll', 'ScreenRecorderLib.dll', 'Assets/Toolbox.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish output is missing $required" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools required to bundle the redistributable CRT.' }
$installations = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = $null
foreach ($installation in $installations) {
    $redist = Join-Path $installation 'VC/Redist/MSVC'
    if (!(Test-Path -LiteralPath $redist)) { continue }
    foreach ($versionFolder in (Get-ChildItem -LiteralPath $redist -Directory | Sort-Object Name -Descending)) {
        $candidate = Get-ChildItem -Path "$($versionFolder.FullName)/x64/Microsoft.VC*.CRT" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { $crt = $candidate; break }
    }
    if ($crt) { break }
}
if (!$crt) { throw 'x64 Visual C++ redistributable CRT directory not found.' }
Copy-Item -Path "$($crt.FullName)/*.dll" -Destination $publish
Copy-Item -LiteralPath "$root/README.md", "$root/LICENSE", "$root/THIRD_PARTY_NOTICES.md" -Destination $publish
Copy-Item -LiteralPath "$root/docs" -Destination $publish -Recurse
Copy-Item -LiteralPath "$root/licenses" -Destination $publish -Recurse

$packageDirectory = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$lock = Get-Content -LiteralPath "$root/src/Toolbox.App/packages.lock.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($framework in $lock.dependencies.Values) {
    foreach ($entry in $framework.GetEnumerator()) {
        if (!$entry.Value.ContainsKey('resolved')) { continue }
        $directory = Join-Path $packageDirectory "$($entry.Key.ToLowerInvariant())/$($entry.Value.resolved)"
        if (!(Test-Path -LiteralPath $directory)) { continue }
        $destination = Join-Path $publish "licenses/nuget/$($entry.Key)"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '(?i)(license|notice|copying)' } |
            Copy-Item -Destination $destination
    }
}
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File | Remove-Item
Compress-Archive -Path "$publish/*" -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path -Leaf $archive)" -Encoding ascii
Write-Output "Created $archive"
