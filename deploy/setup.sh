#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Superseded by deploy/install.sh.
#
# The old script wrote a .env whose variable names did not match compose.yaml, generated
# federation keys in a format NSec rejects, and covered only six of the nine services. It
# is kept as a forwarding shim so existing runbooks and bookmarks keep working.
# ---------------------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "deploy/setup.sh has been replaced by deploy/install.sh - running that instead."
echo
exec "$SCRIPT_DIR/install.sh" "$@"
