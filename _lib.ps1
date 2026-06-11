# Shared helpers for start.ps1 / start-fresh.ps1 / stop.ps1.
# PowerShell 7+ recommended. Dot-source from each script: . "$PSScriptRoot\_lib.ps1"

$script:RepoRoot = $PSScriptRoot
$script:RunDir   = Join-Path $script:RepoRoot '.run'
New-Item -ItemType Directory -Force -Path $script:RunDir | Out-Null

function Log {
    param([string]$Tag = 'info', [string]$Message = '')
    Write-Host "[$Tag] " -ForegroundColor Cyan -NoNewline
    Write-Host $Message
}

function Warn {
    param([string]$Tag = 'warn', [string]$Message = '')
    Write-Host "[$Tag] " -ForegroundColor Yellow -NoNewline
    Write-Host $Message
}

# Read a KEY=VALUE .env file and set each variable in the current process.
# Mirrors `set -a; source .env; set +a` in the bash version.
function Import-DotEnv {
    param([string]$Path = (Join-Path $script:RepoRoot '.env'))
    if (-not (Test-Path $Path)) {
        Warn env ".env missing -- copy .env.example to .env first"
        exit 1
    }
    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { return }
        if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            $name  = $matches[1]
            $value = $matches[2].Trim()
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}

# Tree-kill a PID. taskkill /T reaps child processes that survive a plain kill
# (dotnet run / npm run dev each spawn workers).
function Stop-Tree {
    param([Parameter(Mandatory)][int]$ProcessId)
    & taskkill.exe /F /T /PID $ProcessId 2>&1 | Out-Null
}

# Stop a service tracked by a pid file under .run/. No-op if the file is
# missing or the process is already gone.
function Stop-Pidfile {
    param([Parameter(Mandatory)][string]$Name)
    $pidfile = Join-Path $script:RunDir "$Name.pid"
    if (-not (Test-Path $pidfile)) { return }
    $raw = (Get-Content $pidfile -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($raw) {
        $procId = 0
        if ([int]::TryParse($raw.Trim(), [ref]$procId) -and $procId -gt 0) {
            Log $Name "stopping pid $procId"
            Stop-Tree -ProcessId $procId
        }
    }
    Remove-Item $pidfile -ErrorAction SilentlyContinue
}

# Start a long-running command in the background, redirect combined output to
# .run/<name>.log, and write the PID to .run/<name>.pid. The PID is the cmd.exe
# wrapper that owns the tree -- Stop-Tree walks it on shutdown.
function Start-Bg {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [string]$WorkingDirectory = $script:RepoRoot
    )
    $logfile = Join-Path $script:RunDir "$Name.log"
    $pidfile = Join-Path $script:RunDir "$Name.pid"

    if (Test-Path $pidfile) {
        $existing = (Get-Content $pidfile -ErrorAction SilentlyContinue | Select-Object -First 1)
        $existingPid = 0
        if ($existing -and [int]::TryParse($existing.Trim(), [ref]$existingPid) -and
            (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
            Warn $Name "already running (pid $existingPid) -- leaving it alone"
            return
        }
    }

    Log $Name "starting (logs: $logfile)"
    $cmdLine = "$Command > `"$logfile`" 2>&1"
    $proc = Start-Process -FilePath 'cmd.exe' `
                          -ArgumentList '/c', $cmdLine `
                          -WorkingDirectory $WorkingDirectory `
                          -WindowStyle Hidden `
                          -PassThru
    Set-Content -Path $pidfile -Value $proc.Id
    Log $Name "pid $($proc.Id)"
}

# Wait for postgres inside the docker container to accept connections.
function Wait-Postgres {
    Log postgres "waiting for healthcheck"
    $compose = Join-Path $script:RepoRoot 'docker-compose.yml'
    for ($i = 0; $i -lt 60; $i++) {
        & docker compose -f $compose exec -T postgres pg_isready -U postgresuser -d videoorganizer 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Log postgres "ready"
            return
        }
        Start-Sleep -Seconds 1
    }
    Warn postgres "did not become ready within 60s"
    exit 1
}
