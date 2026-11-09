# GrowCam installer for Windows.
# Run once from the project folder in an ADMIN PowerShell:
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#
# Prerequisites (one-time, see README): Python 3.11+, Git, ffmpeg, NSSM
#   winget install Python.Python.3.12 Git.Git Gyan.FFmpeg NSSM.NSSM

$ErrorActionPreference = "Stop"
$Project = Split-Path -Parent $PSScriptRoot
$Venv = Join-Path $Project ".venv"
$Python = Join-Path $Venv "Scripts\python.exe"
$Service = "GrowCam"
$Task = "GrowCam Updater"
$Port = 8080

Set-Location $Project

foreach ($tool in "python", "git", "ffmpeg", "nssm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' was not found. Install it first (see README) and open a new PowerShell."
    }
}

Write-Host "1/5 Creating virtual environment ..."
if (-not (Test-Path $Python)) { python -m venv $Venv }
& $Python -m pip install --disable-pip-version-check -q --upgrade pip
& $Python -m pip install --disable-pip-version-check -q -r requirements.txt

Write-Host "2/5 Preparing configuration ..."
if (-not (Test-Path ".env")) {
    Copy-Item ".env.example" ".env"
    Write-Host "    Created .env - please fill in camera and API key, then run this script again." -ForegroundColor Yellow
}
New-Item -ItemType Directory -Force -Path (Join-Path $Project "data") | Out-Null

# The service and the updater run as SYSTEM: allow git to use this folder and
# the read-only deploy key regardless of which user owns it.
git config --system --add safe.directory ($Project -replace "\\", "/")
$Key = Join-Path $Project ".deploy\id_ed25519"
if (Test-Path $Key) {
    git config core.sshCommand ("ssh -i '" + ($Key -replace "\\", "/") + "' -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new")
} else {
    Write-Host "    No deploy key found at .deploy\id_ed25519 - automatic updates won't work yet (see README)." -ForegroundColor Yellow
}

Write-Host "3/5 Installing Windows service '$Service' ..."
if (Get-Service $Service -ErrorAction SilentlyContinue) {
    nssm stop $Service | Out-Null
} else {
    nssm install $Service $Python "-m" "growcam" | Out-Null
}
nssm set $Service AppDirectory $Project | Out-Null
nssm set $Service DisplayName "GrowCam" | Out-Null
nssm set $Service Description "GrowCam - Growzelt-Kamera mit KI" | Out-Null
nssm set $Service Start SERVICE_AUTO_START | Out-Null
nssm set $Service AppStdout (Join-Path $Project "data\service.log") | Out-Null
nssm set $Service AppStderr (Join-Path $Project "data\service.log") | Out-Null
nssm set $Service AppRotateFiles 1 | Out-Null
nssm set $Service AppRotateBytes 1000000 | Out-Null
nssm start $Service | Out-Null

Write-Host "4/5 Registering updater task (every 15 minutes) ..."
$Updater = "`"$Python`" `"$(Join-Path $Project 'scripts\update.py')`""
schtasks /Create /TN $Task /SC MINUTE /MO 15 /RU SYSTEM /RL HIGHEST /TR $Updater /F | Out-Null

Write-Host "5/5 Allowing access from the home network (port $Port) ..."
if (-not (Get-NetFirewallRule -DisplayName "GrowCam" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "GrowCam" -Direction Inbound -Protocol TCP -LocalPort $Port -Profile Private -Action Allow | Out-Null
}

$Ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in "Dhcp", "Manual" -and $_.IPAddress -notlike "169.*" } | Select-Object -First 1).IPAddress
Write-Host ""
Write-Host "Done! Open GrowCam at:  http://localhost:$Port   (other devices: http://${Ip}:$Port)" -ForegroundColor Green
