#!/usr/bin/env bash
# Shared helpers for start.sh / start-fresh.sh / stop.sh.
# Designed to work in Git Bash on Windows as well as macOS/Linux.

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN_DIR="$REPO_ROOT/.run"
mkdir -p "$RUN_DIR"

# True when running under MSYS / Git Bash on Windows.
is_windows() {
  case "${OSTYPE:-}" in
    msys*|cygwin*|win32*) return 0 ;;
    *) return 1 ;;
  esac
}

log() { printf '\033[36m[%s]\033[0m %s\n' "${1:-info}" "${2:-}"; }
warn() { printf '\033[33m[%s]\033[0m %s\n' "${1:-warn}" "${2:-}" >&2; }

# Tree-kill a PID. On Windows we need taskkill /T to also reap child processes
# (dotnet run / npm run dev each spawn worker children that survive a plain kill).
kill_tree() {
  local pid="$1"
  [ -z "$pid" ] && return 0
  if is_windows; then
    taskkill //F //T //PID "$pid" >/dev/null 2>&1 || return 1
  else
    pkill -TERM -P "$pid" 2>/dev/null || true
    kill -TERM "$pid" 2>/dev/null || true
    sleep 1
    kill -KILL "$pid" 2>/dev/null || true
  fi
}

# Stop a service tracked by a pid file under .run/. No-op if the file is
# missing or the process is already gone.
stop_pidfile() {
  local name="$1"
  local pidfile="$RUN_DIR/$name.pid"
  if [ ! -f "$pidfile" ]; then
    return 0
  fi
  local pid
  pid="$(cat "$pidfile")"
  if [ -n "$pid" ]; then
    log "$name" "stopping pid $pid"
    kill_tree "$pid" || warn "$name" "kill_tree failed (pid $pid may already be gone)"
  fi
  rm -f "$pidfile"
}

# Start a long-running command in the background, redirect output to .run/<name>.log,
# and write the PID to .run/<name>.pid. Args after the name are the command itself.
start_bg() {
  local name="$1"
  shift
  local logfile="$RUN_DIR/$name.log"
  local pidfile="$RUN_DIR/$name.pid"
  if [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; then
    warn "$name" "already running (pid $(cat "$pidfile")) — leaving it alone"
    return 0
  fi
  log "$name" "starting (logs: $logfile)"
  nohup "$@" >"$logfile" 2>&1 &
  echo $! >"$pidfile"
  log "$name" "pid $(cat "$pidfile")"
}

# Wait for postgres inside the docker container to accept connections.
wait_postgres() {
  log postgres "waiting for healthcheck"
  for _ in $(seq 1 60); do
    if docker compose -f "$REPO_ROOT/docker-compose.yml" exec -T postgres pg_isready -U postgresuser -d videoorganizer >/dev/null 2>&1; then
      log postgres "ready"
      return 0
    fi
    sleep 1
  done
  warn postgres "did not become ready within 60s"
  return 1
}
