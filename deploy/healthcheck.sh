#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Container health probe for the .NET services.
#
# The mcr.microsoft.com/dotnet/aspnet images have shipped without curl and wget since
# .NET 8, so the usual `curl -sf .../health` HEALTHCHECK can never succeed inside them -
# it fails with "executable not found" forever and the container is permanently marked
# unhealthy. This probe uses curl or wget when they happen to exist and otherwise falls
# back to a raw HTTP/1.1 request over bash's /dev/tcp, which needs nothing but bash.
#
# Usage: bash healthcheck.sh <path> [port]
# ---------------------------------------------------------------------------------------
set -u

path="${1:-/health}"
port="${2:-8080}"
url="http://127.0.0.1:${port}${path}"

if command -v curl >/dev/null 2>&1; then
    exec curl -fsS --max-time 5 -o /dev/null "$url"
fi

if command -v wget >/dev/null 2>&1; then
    exec wget -q -T 5 -O /dev/null "$url"
fi

exec 3<>"/dev/tcp/127.0.0.1/${port}" || exit 1
printf 'GET %s HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' "$path" >&3 || exit 1

if ! read -r status_line <&3; then
    exit 1
fi

case "$status_line" in
    *" 200"*) exit 0 ;;
    *)        exit 1 ;;
esac
