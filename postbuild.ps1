param(
    [string]$Version,
    [string]$DllPath,
    [string]$RepoRoot
)

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$buildZipDir = Join-Path $RepoRoot "BuildZip\REPOFidelity"
$enc = New-Object System.Text.UTF8Encoding $false

function Update-ManifestVersion([string]$path, [string]$version) {
    $content = [System.IO.File]::ReadAllText($path, $enc)
    $content = $content -replace '"version_number":\s*"[^"]*"', "`"version_number`": `"$version`""
    [System.IO.File]::WriteAllText($path, $content, $enc)
}

New-Item -ItemType Directory -Path $buildZipDir -Force | Out-Null
Update-ManifestVersion (Join-Path $RepoRoot "manifest.json") $Version

Copy-Item -LiteralPath $DllPath -Destination $buildZipDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "README.md") -Destination $buildZipDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "CHANGELOG.md") -Destination $buildZipDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "manifest.json") -Destination $buildZipDir -Force

# Bundle nvngx_dlss.dll (NVIDIA permits redistribution with material additional functionality)
$outputDir = Split-Path $DllPath -Parent
$dlssSrc = Join-Path $outputDir "nvngx_dlss.dll"
if (Test-Path $dlssSrc) {
    Copy-Item -LiteralPath $dlssSrc -Destination $buildZipDir -Force
    Write-Host "Bundled nvngx_dlss.dll"
} else {
    Write-Host "WARN: nvngx_dlss.dll not found in output - DLSS will use fallback download"
}

# Bundle shader asset bundle
$shaderSrc = Join-Path $outputDir "repofidelity_shaders"
if (Test-Path $shaderSrc) {
    Copy-Item -LiteralPath $shaderSrc -Destination $buildZipDir -Force
    Write-Host "Bundled repofidelity_shaders"
}

# Bundle native bridge DLLs
foreach ($bridge in @("ngx_bridge.dll")) {
    $bridgeSrc = Join-Path $outputDir $bridge
    if (Test-Path $bridgeSrc) {
        Copy-Item -LiteralPath $bridgeSrc -Destination $buildZipDir -Force
        Write-Host "Bundled $bridge"
    }
}

$iconSrc = Join-Path $RepoRoot "icon.png"
if (Test-Path $iconSrc) {
    Copy-Item -LiteralPath $iconSrc -Destination (Join-Path $buildZipDir "icon.png") -Force
} else {
    Write-Host "SKIP: icon.png not found - add before uploading"
}

$zipPath = Join-Path $buildZipDir "REPOFidelity.zip"
$exclude = @(".zip", ".afphoto", ".psd", ".ai", ".xcf")
$filesToZip = Get-ChildItem -LiteralPath $buildZipDir -File | Where-Object { $_.Extension -notin $exclude }
if ($filesToZip.Count -gt 0) {
    Compress-Archive -Path $filesToZip.FullName -DestinationPath $zipPath -Force
    Write-Host "Packaged v$Version -> $zipPath"
}

try { [System.IO.File]::WriteAllText("$env:APPDATA\com.kesomannen.gale\repo\profiles\Development\BepInEx\LogOutput.log", "", $enc) }
catch { }
