<#
  Updates the live panel on a Raspberry Pi (or any Docker host) over SSH. Run from the repository root, after the owner's OK:

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\update-pi.ps1              # this working tree, uncommitted files too
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\update-pi.ps1 -Release     # the latest published release
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\update-pi.ps1 -Rollback    # the image before the last update

  Working tree: compiles the panel on this PC for the Pi (linux-arm64), copies only the finished files, and the Pi wraps
  them in .NET's small runtime image under the name the compose file runs, then restarts the panel. No compiling or .NET
  SDK on the Pi, so it takes about a minute. The image it replaced is kept as gmm-webpanel:previous. -Release pulls the
  published image back, which also undoes a local build.

  The panel's data (the Docker volume) is never touched. Needs SSH key sign-in to the Pi, once, from this PC:

    ssh-keygen -t ed25519
    type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh <user>@<pi> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"

  and the Pi's user in the docker group (sudo usermod -aG docker <user>, then sign in again).
#>
param(
    [string]$Pi = 'nmorosco@gmmpanel.local',
    [string]$Url = 'https://morosco.io',
    [string]$Runtime = 'linux-arm64',   # The Pi's: 64-bit Raspberry Pi OS
    [switch]$Release,
    [switch]$Rollback
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path 'src\GmmPanel\GmmPanel.csproj')) { throw 'Run this from the repository root.' }

# Key sign-in only: a password prompt would hang the script
ssh -o BatchMode=yes -o ConnectTimeout=10 $Pi true
if ($LASTEXITCODE -ne 0) { throw "Can't sign in to $Pi with an SSH key. Set one up first: see the top of this script." }

$mode = if ($Rollback) { 'rollback' } elseif ($Release) { 'release' } else { 'build' }
$version = ([xml](Get-Content 'src\GmmPanel\GmmPanel.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'

# Runs on the Pi. It finds the panel's container, its compose folder and the image name it runs, so nothing is assumed
# about where the compose file lives.
$remote = @'
set -e
mode="$1"; version="$2"
id=$(docker ps -a --filter "label=com.docker.compose.service=panel" --format '{{.ID}}' | head -n1)
[ -n "$id" ] || { echo "No panel container found (a compose service named panel)."; exit 1; }
dir=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' "$id")
image=$(docker inspect -f '{{.Config.Image}}' "$id")
echo "Panel: $image in $dir"
cd "$dir"
case "$mode" in
  release)
    docker tag "$image" gmm-webpanel:previous
    docker compose pull panel ;;
  rollback)
    docker image inspect gmm-webpanel:previous >/dev/null 2>&1 || { echo "No previous image kept."; exit 1; }
    docker tag gmm-webpanel:previous "$image" ;;
  build)
    rm -rf /tmp/gmmpanel-build && mkdir -p /tmp/gmmpanel-build
    tar -xzf /tmp/gmmpanel-app.tgz -C /tmp/gmmpanel-build
    docker build -q -t gmm-webpanel:next /tmp/gmmpanel-build
    docker tag "$image" gmm-webpanel:previous
    docker tag gmm-webpanel:next "$image"
    rm -rf /tmp/gmmpanel-build /tmp/gmmpanel-app.tgz ;;
esac
docker compose up -d panel
# Space on the SD card: old images, build cache, and the .NET SDK image an older version of this script pulled
docker image rm mcr.microsoft.com/dotnet/sdk:10.0 >/dev/null 2>&1 || true
docker image prune -f >/dev/null
docker builder prune -af >/dev/null 2>&1 || true
df -h / | tail -n1 | awk '{print "SD card: " $4 " free"}'
'@

$temp = Join-Path $env:TEMP 'gmmpanel-pi'
New-Item -ItemType Directory -Force $temp | Out-Null
$script = Join-Path $temp 'update.sh'
[IO.File]::WriteAllText($script, $remote.Replace("`r`n", "`n"))   # bash needs LF line endings

if ($mode -eq 'build') {
    Write-Host "Compiling for the Pi ($Runtime)..."
    $stage = Join-Path $temp 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
    dotnet publish src/GmmPanel/GmmPanel.csproj -c Release -r $Runtime --self-contained false -p:Version="$version-local.$stamp" `
        -p:DebugType=none -o (Join-Path $stage 'app') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'The compile failed: nothing was changed.' }
    # The same runtime image and settings as the release Dockerfile, with the compiled files copied in
    $dockerfile = @"
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY app/ .
ENV Urls=http://0.0.0.0:8080 Panel__DataDirectory=/data ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
VOLUME /data
USER 1654
ENTRYPOINT ["dotnet", "gmmpanel.dll"]
"@
    [IO.File]::WriteAllText((Join-Path $stage 'Dockerfile'), $dockerfile.Replace("`r`n", "`n"))
    $archive = Join-Path $temp 'gmmpanel-app.tgz'
    tar -czf $archive -C $stage Dockerfile app
    if ($LASTEXITCODE -ne 0) { throw 'Packing failed: nothing was changed.' }
    Write-Host 'Copying to the Pi...'
    scp -q -o BatchMode=yes $archive "${Pi}:/tmp/gmmpanel-app.tgz"
    if ($LASTEXITCODE -ne 0) { throw 'Copying to the Pi failed: nothing was changed.' }
}
scp -q -o BatchMode=yes $script "${Pi}:/tmp/gmmpanel-update.sh"
Write-Host "Updating the Pi ($mode)..."
ssh -o BatchMode=yes $Pi "bash /tmp/gmmpanel-update.sh $mode $version-local.$stamp; rm -f /tmp/gmmpanel-update.sh"
if ($LASTEXITCODE -ne 0) { throw 'The update failed on the Pi. The running panel is unchanged unless the restart was reached; -Rollback puts the previous image back.' }

# It answers within a few seconds of starting
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 2
    try {
        $code = (Invoke-WebRequest -UseBasicParsing "$Url/" -TimeoutSec 10).StatusCode
        Write-Host "Live panel is up: $Url answered $code."
        exit 0
    } catch { }
}
throw "The panel didn't answer at $Url. Roll back: tools\update-pi.ps1 -Rollback"
