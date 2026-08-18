#!/usr/bin/env bash
# =====================================================================================
#  Venta / Echo self-hosted installer - Linux
#
#  Produces a complete, auto-booting deployment of the whole stack:
#
#    infrastructure   PostgreSQL, Redis, RabbitMQ, ScyllaDB (optional), MinIO (optional)
#    services         Identity, Guild, Messaging, Social, Federation, Bots, Import,
#                     Isle (optional) and the Echo gateway
#    edge             Caddy in front of everything for TLS termination, with automatic
#                     Let's Encrypt issuance and renewal
#    lifecycle        systemd unit + docker restart policies, so the stack comes back
#                     on reboot, and a `ventactl` helper for day-to-day operation
#
#  Usage:
#      sudo ./install.sh                       interactive
#      sudo ./install.sh --non-interactive \
#           --domain chat.example.com --acme-email admin@example.com
#
#  Run it again at any time: existing secrets in deploy/.env are preserved (rotating the
#  federation keypair would break every instance you are already federated with), images
#  are refreshed, and the stack is restarted.
# =====================================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
ENV_FILE="$SCRIPT_DIR/.env"
GENERATED_DIR="$SCRIPT_DIR/generated"
COMPOSE_FILE="$SCRIPT_DIR/compose.yaml"
PROJECT_NAME="venta"
VENTACTL_PATH="/usr/local/bin/ventactl"
SYSTEMD_UNIT="/etc/systemd/system/venta-stack.service"

# ANSI-C quoting, not '\033[...]': these are interpolated into here-documents as well as
# printf format strings, and only a real escape character renders in both.
GREEN=$'\033[0;32m'; CYAN=$'\033[0;36m'; YELLOW=$'\033[1;33m'; RED=$'\033[0;31m'
BOLD=$'\033[1m'; DIM=$'\033[2m'; NC=$'\033[0m'

log()   { printf "${CYAN}==>${NC} %s\n" "$*"; }
ok()    { printf "${GREEN} ok${NC} %s\n" "$*"; }
warn()  { printf "${YELLOW}  ! ${NC}%s\n" "$*"; }
die()   { printf "${RED}error:${NC} %s\n" "$*" >&2; exit 1; }
step()  { printf "\n${BOLD}%s${NC}\n" "$*"; }

# ── Defaults / CLI flags ─────────────────────────────────────────────────────────────
NON_INTERACTIVE=false
RECONFIGURE=false
SKIP_DEPS=false
NO_START=false
UNINSTALL=false

ARG_DOMAIN=""
ARG_STORAGE_DOMAIN=""
ARG_DOCS_DOMAIN=""
ARG_ADMIN_DOMAIN=""
ARG_SUPPORT_DOMAIN=""
ARG_STATUS_DOMAIN=""
ARG_AUTH_DOMAIN=""
ARG_WIKI_DOMAIN=""
ARG_INSTANCE_NAME=""
ARG_ACME_EMAIL=""
ARG_TLS_MODE=""              # letsencrypt | local | external-proxy
ARG_IMAGE_SOURCE=""          # registry | build
ARG_IMAGE_PREFIX="ghcr.io/alpinebits-ch"
ARG_IMAGE_TAG="latest"
ARG_EXTERNAL_DB=""           # yes | no
ARG_DB_HOST=""; ARG_DB_PORT=""; ARG_DB_USER=""; ARG_DB_PASSWORD=""
ARG_SCYLLA=""                # yes | no
ARG_ISLE=""                  # yes | no
ARG_EXTERNAL_STORAGE=""      # yes | no

usage() {
    cat <<'USAGE'
Venta self-hosted installer (Linux)

  --domain <host>              public hostname for the API (e.g. chat.example.com)
  --storage-domain <host>      public hostname for attachments (default: storage.<domain>)
  --docs-domain <host>         public hostname for the API reference (default: docs.<domain>)
  --admin-domain <host>        public hostname for the moderation console (default: admin.<domain>)
  --support-domain <host>      public hostname for the support site (default: support.<domain>)
  --status-domain <host>       public hostname for the status page (default: status.<domain>)
  --auth-domain <host>         public hostname for sign-in / SSO (default: auth.<domain>)
  --wiki-domain <host>         public hostname for published guild wikis (default: wiki.<domain>)
  --instance-name <name>       federation display name for this instance
  --acme-email <email>         contact address for Let's Encrypt
  --tls <mode>                 letsencrypt (default) | local | external-proxy
  --image-source <src>         registry (default) | build
  --image-prefix <prefix>      container registry namespace (default ghcr.io/alpinebits-ch)
  --image-tag <tag>            image tag to deploy (default latest)
  --external-postgres          use an existing PostgreSQL server
  --db-host/--db-port/--db-user/--db-password
                               external PostgreSQL connection details
  --scylla / --no-scylla       enable/disable the ScyllaDB message store
  --isle / --no-isle           enable/disable the Isle game-server integration
  --external-storage           use external S3-compatible storage instead of MinIO
  --non-interactive            never prompt; use flags and defaults
  --reconfigure                re-run the questionnaire instead of reusing deploy/.env
  --skip-dependencies          do not attempt to install Docker/openssl
  --no-start                   write configuration but do not boot the stack
  --uninstall                  stop the stack and remove the systemd unit (keeps data)
  -h, --help                   this message
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --domain)             ARG_DOMAIN="$2"; shift 2 ;;
        --storage-domain)     ARG_STORAGE_DOMAIN="$2"; shift 2 ;;
        --docs-domain)        ARG_DOCS_DOMAIN="$2"; shift 2 ;;
        --admin-domain)       ARG_ADMIN_DOMAIN="$2"; shift 2 ;;
        --support-domain)     ARG_SUPPORT_DOMAIN="$2"; shift 2 ;;
        --status-domain)      ARG_STATUS_DOMAIN="$2"; shift 2 ;;
        --auth-domain)        ARG_AUTH_DOMAIN="$2"; shift 2 ;;
        --wiki-domain)        ARG_WIKI_DOMAIN="$2"; shift 2 ;;
        --instance-name)      ARG_INSTANCE_NAME="$2"; shift 2 ;;
        --acme-email)         ARG_ACME_EMAIL="$2"; shift 2 ;;
        --tls)                ARG_TLS_MODE="$2"; shift 2 ;;
        --image-source)       ARG_IMAGE_SOURCE="$2"; shift 2 ;;
        --image-prefix)       ARG_IMAGE_PREFIX="$2"; shift 2 ;;
        --image-tag)          ARG_IMAGE_TAG="$2"; shift 2 ;;
        --external-postgres)  ARG_EXTERNAL_DB="yes"; shift ;;
        --db-host)            ARG_DB_HOST="$2"; shift 2 ;;
        --db-port)            ARG_DB_PORT="$2"; shift 2 ;;
        --db-user)            ARG_DB_USER="$2"; shift 2 ;;
        --db-password)        ARG_DB_PASSWORD="$2"; shift 2 ;;
        --scylla)             ARG_SCYLLA="yes"; shift ;;
        --no-scylla)          ARG_SCYLLA="no"; shift ;;
        --isle)               ARG_ISLE="yes"; shift ;;
        --no-isle)            ARG_ISLE="no"; shift ;;
        --external-storage)   ARG_EXTERNAL_STORAGE="yes"; shift ;;
        --non-interactive)    NON_INTERACTIVE=true; shift ;;
        --reconfigure)        RECONFIGURE=true; shift ;;
        --skip-dependencies)  SKIP_DEPS=true; shift ;;
        --no-start)           NO_START=true; shift ;;
        --uninstall)          UNINSTALL=true; shift ;;
        -h|--help)            usage; exit 0 ;;
        *) die "unknown option: $1 (try --help)" ;;
    esac
done

# ── Helpers ──────────────────────────────────────────────────────────────────────────
ask() {
    local prompt="$1" default="${2:-}" answer=""
    if [[ "$NON_INTERACTIVE" == true || ! -t 0 ]]; then
        printf '%s' "$default"; return
    fi
    read -r -p "$(printf "${BOLD}?${NC} %s ${DIM}[%s]${NC}: " "$prompt" "$default")" answer </dev/tty || true
    printf '%s' "${answer:-$default}"
}

ask_secret() {
    local prompt="$1" answer=""
    if [[ "$NON_INTERACTIVE" == true || ! -t 0 ]]; then printf ''; return; fi
    read -r -s -p "$(printf "${BOLD}?${NC} %s: " "$prompt")" answer </dev/tty || true
    printf '\n' >&2
    printf '%s' "$answer"
}

ask_yes_no() {
    local prompt="$1" default="$2" answer
    answer="$(ask "$prompt (y/n)" "$default")"
    case "${answer,,}" in y|yes|true) echo "yes" ;; *) echo "no" ;; esac
}

# Values land in a .env that is both sourced by this script and parsed by compose, so
# strip the characters that would make either interpretation ambiguous.
sanitize() { printf '%s' "$1" | tr -d '"\\$`'; }

rand_hex() { openssl rand -hex "${1:-24}"; }
b64_file() { base64 < "$1" | tr -d '\n\r'; }

compose_cmd() {
    docker compose -p "$PROJECT_NAME" \
        --project-directory "$SCRIPT_DIR" \
        -f "$COMPOSE_FILE" \
        --env-file "$ENV_FILE" "$@"
}

require_root() {
    if [[ "$(id -u)" -ne 0 ]]; then
        die "this installer must run as root (try: sudo $0 $*)"
    fi
}

# ── Uninstall ────────────────────────────────────────────────────────────────────────
if [[ "$UNINSTALL" == true ]]; then
    require_root
    step "Uninstalling"
    if [[ -f "$ENV_FILE" ]]; then compose_cmd down || true; fi
    systemctl disable --now venta-stack.service 2>/dev/null || true
    rm -f "$SYSTEMD_UNIT" "$VENTACTL_PATH"
    systemctl daemon-reload 2>/dev/null || true
    ok "stack stopped, systemd unit and ventactl removed"
    warn "Data volumes were kept. Remove them with: docker volume rm \$(docker volume ls -q -f name=${PROJECT_NAME}_)"
    exit 0
fi

printf "${CYAN}${BOLD}"
cat <<'BANNER'
 ┌──────────────────────────────────────────────────────┐
 │        Venta / Echo  ·  self-hosted installer        │
 │        federated chat stack · Linux edition          │
 └──────────────────────────────────────────────────────┘
BANNER
printf "${NC}\n"

require_root "$@"

# =====================================================================================
# 1. Host dependencies
# =====================================================================================
step "1/9  Host dependencies"

detect_pkg_manager() {
    for m in apt-get dnf yum zypper pacman apk; do
        command -v "$m" >/dev/null 2>&1 && { echo "$m"; return; }
    done
    echo ""
}
PKG="$(detect_pkg_manager)"

pkg_install() {
    [[ $# -eq 0 ]] && return 0
    case "$PKG" in
        apt-get) DEBIAN_FRONTEND=noninteractive apt-get install -y "$@" ;;
        dnf)     dnf install -y "$@" ;;
        yum)     yum install -y "$@" ;;
        zypper)  zypper --non-interactive install "$@" ;;
        pacman)  pacman -Sy --noconfirm "$@" ;;
        apk)     apk add --no-cache "$@" ;;
        *)       warn "no supported package manager found; install manually: $*" ;;
    esac
}

if [[ "$SKIP_DEPS" == false ]]; then
    missing=()
    command -v openssl >/dev/null 2>&1 || missing+=("openssl")
    command -v curl    >/dev/null 2>&1 || missing+=("curl")
    if [[ ${#missing[@]} -gt 0 ]]; then
        log "installing: ${missing[*]}"
        if [[ "$PKG" == "apt-get" ]]; then apt-get update -qq || true; fi
        pkg_install "${missing[@]}"
    fi

    if ! command -v docker >/dev/null 2>&1; then
        log "installing Docker Engine via get.docker.com"
        curl -fsSL https://get.docker.com | sh
    fi

    if ! docker compose version >/dev/null 2>&1; then
        log "installing the Docker Compose plugin"
        pkg_install docker-compose-plugin || true
    fi
fi

command -v docker >/dev/null 2>&1 || die "docker is not installed"
docker compose version >/dev/null 2>&1 || die "the 'docker compose' plugin is required (v2)"
command -v openssl >/dev/null 2>&1 || die "openssl is required to generate keys and certificates"

systemctl enable --now docker >/dev/null 2>&1 || true
docker info >/dev/null 2>&1 || die "the Docker daemon is not reachable"
ok "docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo '?') ready"

# =====================================================================================
# 2. Existing configuration
# =====================================================================================
step "2/9  Configuration"

REUSE_ENV=false
if [[ -f "$ENV_FILE" && "$RECONFIGURE" == false ]]; then
    REUSE_ENV=true
    log "found an existing deploy/.env - reusing it (pass --reconfigure to start over)"
    set -a
    # shellcheck disable=SC1090
    . "$ENV_FILE"
    set +a
fi

if [[ "$REUSE_ENV" == false ]]; then
    INSTANCE_NAME="$(sanitize "${ARG_INSTANCE_NAME:-$(ask 'Instance display name (shown to federated peers)' 'Venta')}")"

    INSTANCE_DOMAIN="$(sanitize "${ARG_DOMAIN:-$(ask 'Public hostname for the API (blank for a LAN-only install)' '')}")"

    if [[ -n "$ARG_TLS_MODE" ]]; then
        TLS_MODE="$ARG_TLS_MODE"
    elif [[ -z "$INSTANCE_DOMAIN" ]]; then
        TLS_MODE="local"
    elif [[ "$NON_INTERACTIVE" == true ]]; then
        TLS_MODE="letsencrypt"
    else
        printf "\n  How should TLS be handled?\n"
        printf "    1) Bundled Caddy with automatic Let's Encrypt certificates (recommended)\n"
        printf "    2) I already run my own reverse proxy in front of this host\n"
        printf "    3) No TLS - plain HTTP on the LAN (development only)\n"
        case "$(ask 'Selection' '1')" in
            2) TLS_MODE="external-proxy" ;;
            3) TLS_MODE="local" ;;
            *) TLS_MODE="letsencrypt" ;;
        esac
    fi

    case "$TLS_MODE" in
        letsencrypt|external-proxy)
            [[ -n "$INSTANCE_DOMAIN" ]] || die "--domain is required for TLS mode '$TLS_MODE'"
            STORAGE_DOMAIN="$(sanitize "${ARG_STORAGE_DOMAIN:-$(ask 'Public hostname for attachments/avatars' "storage.$INSTANCE_DOMAIN")}")"
            DOCS_DOMAIN="$(sanitize "${ARG_DOCS_DOMAIN:-$(ask 'Public hostname for the API reference' "docs.$INSTANCE_DOMAIN")}")"
            ADMIN_DOMAIN="$(sanitize "${ARG_ADMIN_DOMAIN:-$(ask 'Public hostname for the moderation console' "admin.$INSTANCE_DOMAIN")}")"
            SUPPORT_DOMAIN="$(sanitize "${ARG_SUPPORT_DOMAIN:-$(ask 'Public hostname for the support site' "support.$INSTANCE_DOMAIN")}")"
            STATUS_DOMAIN="$(sanitize "${ARG_STATUS_DOMAIN:-$(ask 'Public hostname for the status page' "status.$INSTANCE_DOMAIN")}")"
            AUTH_DOMAIN="$(sanitize "${ARG_AUTH_DOMAIN:-$(ask 'Public hostname for sign-in / SSO' "auth.$INSTANCE_DOMAIN")}")"
            WIKI_DOMAIN="$(sanitize "${ARG_WIKI_DOMAIN:-$(ask 'Public hostname for published guild wikis' "wiki.$INSTANCE_DOMAIN")}")"
            INSTANCE_URL="https://$INSTANCE_DOMAIN"
            STORAGE_PUBLIC_URL="https://$STORAGE_DOMAIN"
            ;;
        local)
            LAN_IP="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{print $7; exit}')"
            LAN_IP="${LAN_IP:-127.0.0.1}"
            # Not localhost: every service resolves INSTANCE_URL from *inside* its own
            # container to fetch Identity's OpenID metadata, and "localhost" there is the
            # container itself.
            INSTANCE_DOMAIN="$(sanitize "$(ask 'Address other machines reach this host on' "$LAN_IP")")"
            STORAGE_DOMAIN="$INSTANCE_DOMAIN"
            INSTANCE_URL="http://$INSTANCE_DOMAIN:8080"
            STORAGE_PUBLIC_URL="http://$INSTANCE_DOMAIN:9000"
            ;;
        *) die "unknown TLS mode '$TLS_MODE'" ;;
    esac

    ACME_EMAIL=""
    if [[ "$TLS_MODE" == "letsencrypt" ]]; then
        ACME_EMAIL="$(sanitize "${ARG_ACME_EMAIL:-$(ask "Contact e-mail for Let's Encrypt" "admin@$INSTANCE_DOMAIN")}")"
    fi

    # --- Database ---
    USE_EXTERNAL_DB="${ARG_EXTERNAL_DB:-$(ask_yes_no 'Use an existing external PostgreSQL server?' 'n')}"
    if [[ "$USE_EXTERNAL_DB" == "yes" ]]; then
        DATABASE_HOSTNAME="$(sanitize "${ARG_DB_HOST:-$(ask 'PostgreSQL host' '')}")"
        DATABASE_PORT="$(sanitize "${ARG_DB_PORT:-$(ask 'PostgreSQL port' '5432')}")"
        DATABASE_USERNAME="$(sanitize "${ARG_DB_USER:-$(ask 'PostgreSQL user (must be allowed to CREATE DATABASE)' 'postgres')}")"
        DATABASE_PASSWORD="$(sanitize "${ARG_DB_PASSWORD:-$(ask_secret 'PostgreSQL password')}")"
        [[ -n "$DATABASE_HOSTNAME" ]] || die "--db-host is required with --external-postgres"
    else
        DATABASE_HOSTNAME="postgres"
        DATABASE_PORT="5432"
        DATABASE_USERNAME="postgres"
        DATABASE_PASSWORD="$(rand_hex 24)"
    fi

    # --- Message store ---
    USE_SCYLLA="${ARG_SCYLLA:-$(ask_yes_no 'Enable the ScyllaDB message store? (needs ~4 GB RAM; Postgres is used otherwise)' 'y')}"

    # --- Object storage ---
    USE_EXTERNAL_STORAGE="${ARG_EXTERNAL_STORAGE:-$(ask_yes_no 'Use external S3-compatible storage instead of the bundled MinIO?' 'n')}"
    if [[ "$USE_EXTERNAL_STORAGE" == "yes" ]]; then
        BUCKET_NAME="$(sanitize "$(ask 'Bucket name' 'echo-chat')")"
        ACCESS_KEY_ID="$(sanitize "$(ask 'S3 access key id' '')")"
        SECRET_ACCESS_KEY="$(sanitize "$(ask_secret 'S3 secret access key')")"
        STORAGE_SERVICE_URL="$(sanitize "$(ask 'S3 endpoint URL' 'https://storage.googleapis.com')")"
        STORAGE_PUBLIC_URL="$(sanitize "$(ask 'Public base URL objects are served from' "$STORAGE_SERVICE_URL")")"
        STORAGE_USE_SERVICE_URL="true"
        STORAGE_REGION="$(sanitize "$(ask 'Region' 'us-east-1')")"
    else
        BUCKET_NAME="echo-chat"
        ACCESS_KEY_ID="venta_$(openssl rand -hex 6)"
        SECRET_ACCESS_KEY="$(rand_hex 24)"
        STORAGE_SERVICE_URL="http://minio:9000"
        STORAGE_USE_SERVICE_URL="true"
        STORAGE_REGION="us-east-1"
    fi

    # --- Optional modules ---
    ENABLE_ISLE="${ARG_ISLE:-$(ask_yes_no 'Enable the Isle game-server integration service?' 'n')}"
    ISLE_IP_ADDRESS="10.0.0.0"; ISLE_BRIDGE_PORT="8080"; ISLE_RCON_PORT="8888"; ISLE_RCON_PASSWORD=""
    if [[ "$ENABLE_ISLE" == "yes" ]]; then
        ISLE_IP_ADDRESS="$(sanitize "$(ask 'Isle dedicated-server address' '10.0.0.0')")"
        ISLE_BRIDGE_PORT="$(sanitize "$(ask 'IsleBridge plugin HTTP port' '8080')")"
        ISLE_RCON_PORT="$(sanitize "$(ask 'Isle RCON port' '8888')")"
        ISLE_RCON_PASSWORD="$(sanitize "$(ask_secret 'Isle RCON password')")"
    fi

    # --- Secrets ---
    REDIS_PASSWORD="$(rand_hex 24)"
    RABBITMQ_USERNAME="venta"
    RABBITMQ_PASSWORD="$(rand_hex 24)"
    SCYLLA_PASSWORD="$(rand_hex 20)"
    IDENTITY_KEY_PASSWORD="$(rand_hex 32)"

    IMAGE_SOURCE="${ARG_IMAGE_SOURCE:-registry}"
    IMAGE_PREFIX="$ARG_IMAGE_PREFIX"
    IMAGE_TAG="$ARG_IMAGE_TAG"

    AUTH_REQUIRE_USER_EMAIL_VERIFICATION="false"
    MICROSOFT_GRAPH_CLIENT_ID=""; MICROSOFT_GRAPH_CLIENT_SECRET=""
    LIVEKIT_API_KEY=""; LIVEKIT_API_SECRET=""
    LIVEKIT_REGION=""; LIVEKIT_SIGNALING_URL=""; LIVEKIT_API_URL=""
    DISCORD_IMPORT_BOT_TOKEN=""; DISCORD_IMPORT_CLIENT_ID=""
    STEAM_WEB_API_KEY=""
    SENTRY_URL=""; PERSONAL_ACCESS_TOKEN=""
    FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64=""; GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64=""
    APNS_BUNDLE_ID="gg.venta.mobile"; APNS_KEY_ID=""; APNS_TEAM_ID=""; APNS_AUTH_KEY_BASE_64=""; APNS_USE_SANDBOX="true"
else
    IMAGE_SOURCE="${ARG_IMAGE_SOURCE:-${IMAGE_SOURCE:-registry}}"
    if [[ "$ARG_IMAGE_TAG" != "latest" ]]; then IMAGE_TAG="$ARG_IMAGE_TAG"; fi
    IMAGE_TAG="${IMAGE_TAG:-latest}"
    IMAGE_PREFIX="${IMAGE_PREFIX:-$ARG_IMAGE_PREFIX}"
fi

# A .env written by an older installer (or hand-edited) will not define everything the
# steps below read, and `set -u` would abort on the first one. Fill the gaps rather than
# forcing a full --reconfigure.
: "${INSTANCE_NAME:=Venta}"
: "${INSTANCE_DOMAIN:=}"
: "${TLS_MODE:=local}"
: "${ACME_EMAIL:=}"
: "${STORAGE_DOMAIN:=$INSTANCE_DOMAIN}"
# The gateway derives this itself when unset; setting it explicitly keeps the Caddyfile and the
# gateway in agreement when the instance is not on a bare domain.
: "${DOCS_DOMAIN:=${INSTANCE_DOMAIN:+docs.$INSTANCE_DOMAIN}}"
: "${ADMIN_DOMAIN:=${INSTANCE_DOMAIN:+admin.$INSTANCE_DOMAIN}}"
: "${SUPPORT_DOMAIN:=${INSTANCE_DOMAIN:+support.$INSTANCE_DOMAIN}}"
: "${STATUS_DOMAIN:=${INSTANCE_DOMAIN:+status.$INSTANCE_DOMAIN}}"
: "${AUTH_DOMAIN:=${INSTANCE_DOMAIN:+auth.$INSTANCE_DOMAIN}}"
: "${WIKI_DOMAIN:=${INSTANCE_DOMAIN:+wiki.$INSTANCE_DOMAIN}}"
# Left empty on purpose. The issuer defaults to auth.<instance host>, i.e. exactly AUTH_DOMAIN,
# and every service derives the same value - so setting it here would only create a second place
# for the two to disagree. See the AUTH_ISSUER_URL comment in compose.yaml.
: "${AUTH_ISSUER_URL:=}"
# The OIDC client allowlist. No prompt: it is a JSON array, nobody types one at an install
# prompt, and an instance with no partner sites correctly has none. See docs/specs/sso-integration.md.
: "${AUTH_CLIENTS:=}"
# Extra browser origins allowed to call the API. Additive to the built-ins and to the web client
# derived from INSTANCE_URL, so empty is right for a plain install. A site of your own on its own
# hostname needs its origin here as well as in AUTH_CLIENTS.
: "${CORS_ALLOWED_ORIGINS:=}"
# Extra hostnames that count as this instance in a posted link. Additive to INSTANCE_URL's host
# and the web client's, so empty is right unless invite links carry a third name - an apex or
# vanity domain that redirects into the app, say. See the compose.yaml comment.
: "${INSTANCE_LINK_HOSTS:=}"
: "${INSTANCE_URL:=http://${INSTANCE_DOMAIN:-127.0.0.1}:8080}"
: "${USE_EXTERNAL_DB:=no}"
: "${DATABASE_HOSTNAME:=postgres}"
: "${DATABASE_PORT:=5432}"
: "${DATABASE_USERNAME:=postgres}"
: "${DATABASE_PASSWORD:=$(rand_hex 24)}"
: "${USE_SCYLLA:=no}"
: "${SCYLLA_PASSWORD:=$(rand_hex 20)}"
: "${USE_EXTERNAL_STORAGE:=no}"
: "${BUCKET_NAME:=echo-chat}"
: "${ACCESS_KEY_ID:=venta_$(openssl rand -hex 6)}"
: "${SECRET_ACCESS_KEY:=$(rand_hex 24)}"
: "${STORAGE_PUBLIC_URL:=$INSTANCE_URL}"
: "${STORAGE_SERVICE_URL:=http://minio:9000}"
: "${STORAGE_USE_SERVICE_URL:=true}"
: "${STORAGE_REGION:=us-east-1}"
: "${REDIS_PASSWORD:=$(rand_hex 24)}"
: "${RABBITMQ_USERNAME:=venta}"
: "${RABBITMQ_PASSWORD:=$(rand_hex 24)}"
: "${IDENTITY_KEY_PASSWORD:=$(rand_hex 32)}"
: "${AUTH_REQUIRE_USER_EMAIL_VERIFICATION:=false}"
: "${ENABLE_ISLE:=no}"
# Shared secret the reverse proxy presents (as the X-Echo-Proxy-Auth header) to prove the
# X-Forwarded-For chain it wrote may be believed. Without it the gateway ignores forwarded
# headers and every anonymous caller shares one rate-limit bucket keyed on the proxy's own
# address. Assigned once and kept across re-runs on purpose: the value has to match what the
# generated Caddyfile sends, and rotating only one of the two fails silently.
: "${GATEWAY_PROXY_SECRET:=$(rand_hex 32)}"
# Alternative to the secret for deployments that DO have stable proxy addresses: a comma-
# separated list of addresses or CIDR ranges. Empty by default - container addresses here are
# reassigned on restart, so an allowlist would drift out of date without anything failing.
: "${GATEWAY_TRUSTED_PROXIES:=}"

# =====================================================================================
# 3. Cryptographic material
# =====================================================================================
step "3/9  Keys and certificates"

mkdir -p "$GENERATED_DIR"
chmod 700 "$GENERATED_DIR"

# --- Ed25519 federation keypair ------------------------------------------------------
# Federation.Application signs and verifies with NSec, importing both halves as
# KeyBlobFormat.Pkix*Text - i.e. PEM text, base64-encoded once more for transport in the
# environment. Random bytes (what the previous installer produced) are rejected outright
# by Key.Import, so every outbound event would throw on signing.
if [[ -z "${FEDERATION_PRIVATE_KEY_BASE_64:-}" || -z "${FEDERATION_PUBLIC_KEY_BASE_64:-}" ]]; then
    fed_priv="$GENERATED_DIR/federation-ed25519.key.pem"
    fed_pub="$GENERATED_DIR/federation-ed25519.pub.pem"
    openssl genpkey -algorithm ed25519 -out "$fed_priv" 2>/dev/null \
        || die "this OpenSSL build cannot generate Ed25519 keys (needs OpenSSL 1.1.1+)"
    openssl pkey -in "$fed_priv" -pubout -out "$fed_pub"
    chmod 600 "$fed_priv"
    FEDERATION_PRIVATE_KEY_BASE_64="$(b64_file "$fed_priv")"
    FEDERATION_PUBLIC_KEY_BASE_64="$(b64_file "$fed_pub")"
    ok "generated a new Ed25519 federation identity"
else
    ok "kept the existing federation keypair (peers stay valid)"
fi

# --- Identity token-signing certificate ----------------------------------------------
# In Production, Identity.Application loads a PKCS#12 bundle from IDENTITY_SIGNING_CERT
# for OpenIddict signing + encryption. Without one it falls back to OpenIddict's
# development certificate, which is regenerated on every container start - so every
# access token issued before a restart stops validating.
if [[ -z "${IDENTITY_SIGNING_CERT:-}" ]]; then
    cert_dir="$GENERATED_DIR/identity"
    mkdir -p "$cert_dir"; chmod 700 "$cert_dir"
    openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
        -keyout "$cert_dir/identity.key.pem" -out "$cert_dir/identity.crt.pem" \
        -subj "/CN=${INSTANCE_DOMAIN:-venta} Identity Signing" >/dev/null 2>&1
    openssl pkcs12 -export \
        -inkey "$cert_dir/identity.key.pem" -in "$cert_dir/identity.crt.pem" \
        -out "$cert_dir/identity.p12" -passout "pass:$IDENTITY_KEY_PASSWORD" >/dev/null 2>&1
    chmod 600 "$cert_dir"/*
    IDENTITY_SIGNING_CERT="$(b64_file "$cert_dir/identity.p12")"
    ok "generated a persistent OpenIddict signing certificate (10 year validity)"
else
    ok "kept the existing Identity signing certificate"
fi

# =====================================================================================
# 4. Derived settings
# =====================================================================================
step "4/9  Deployment layout"

PROFILES=()
if [[ "$USE_EXTERNAL_DB"      != "yes" ]]; then PROFILES+=("pg-local");      fi
if [[ "$USE_EXTERNAL_STORAGE" != "yes" ]]; then PROFILES+=("storage-local"); fi
if [[ "$USE_SCYLLA"           == "yes" ]]; then PROFILES+=("scylla");        fi
if [[ "$ENABLE_ISLE"          == "yes" ]]; then PROFILES+=("isle");          fi
if [[ "$TLS_MODE"     == "letsencrypt" ]]; then PROFILES+=("caddy");         fi
COMPOSE_PROFILES="$(IFS=,; printf '%s' "${PROFILES[*]:-}")"

USE_SCYLLA_DB="false"
if [[ "$USE_SCYLLA" == "yes" ]]; then USE_SCYLLA_DB="true"; fi

# Port publishing and in-network name resolution differ per TLS mode:
#   letsencrypt    only Caddy is public; it also carries a network alias for the public
#                  hostname so containers resolve INSTANCE_URL without NAT hairpinning
#   external-proxy the gateway/MinIO listen on loopback for the host's own proxy, and the
#                  public hostname is pointed back at the docker host
#   local          the gateway and MinIO are published on the LAN directly
case "$TLS_MODE" in
    letsencrypt)
        GATEWAY_BIND="127.0.0.1:8080"; MINIO_BIND="127.0.0.1:9000"
        HAIRPIN_HOST_ENTRY="venta-hairpin.invalid:127.0.0.1"
        ;;
    external-proxy)
        GATEWAY_BIND="127.0.0.1:8080"; MINIO_BIND="127.0.0.1:9000"
        HAIRPIN_HOST_ENTRY="${INSTANCE_DOMAIN}:host-gateway"
        ;;
    local)
        GATEWAY_BIND="0.0.0.0:8080"; MINIO_BIND="0.0.0.0:9000"
        HAIRPIN_HOST_ENTRY="venta-hairpin.invalid:127.0.0.1"
        ;;
esac

# =====================================================================================
# 5. Write deploy/.env
# =====================================================================================
step "5/9  Writing deploy/.env"

umask 077
cat > "$ENV_FILE" <<ENV
# =====================================================================================
#  Venta stack configuration - generated by deploy/install.sh on $(date -u '+%Y-%m-%dT%H:%M:%SZ')
#  Contains secrets. Keep mode 0600, keep out of version control.
#  Re-run the installer after editing, or apply with: ventactl up
# =====================================================================================

COMPOSE_PROJECT_NAME=$PROJECT_NAME
COMPOSE_PROFILES="$COMPOSE_PROFILES"

# ── Identity of this instance ────────────────────────────────────────────────────────
INSTANCE_NAME="$INSTANCE_NAME"
INSTANCE_DOMAIN="$INSTANCE_DOMAIN"
INSTANCE_URL="$INSTANCE_URL"
INSTANCE_VERSION="1.0.0"
TLS_MODE="$TLS_MODE"
ACME_EMAIL="${ACME_EMAIL:-}"
STORAGE_DOMAIN="${STORAGE_DOMAIN:-}"
DOCS_DOMAIN="${DOCS_DOMAIN:-}"
ADMIN_DOMAIN="${ADMIN_DOMAIN:-}"
SUPPORT_DOMAIN="${SUPPORT_DOMAIN:-}"
STATUS_DOMAIN="${STATUS_DOMAIN:-}"
AUTH_DOMAIN="${AUTH_DOMAIN:-}"
WIKI_DOMAIN="${WIKI_DOMAIN:-}"
# Extra hostnames a posted link may use for this instance, beyond INSTANCE_URL's host and the
# web client's. Set it if your invite links live on an apex or vanity domain, or their previews
# come back describing your marketing page instead of the server.
INSTANCE_LINK_HOSTS="${INSTANCE_LINK_HOSTS:-}"
ASPNETCORE_ENVIRONMENT="Production"

# ── Sign-in / SSO ────────────────────────────────────────────────────────────────────
# AUTH_ISSUER_URL empty means auth.<instance host>. Every service reads it, so if you ever
# set it, set it once here - a service left behind rejects every token the others accept.
AUTH_ISSUER_URL="${AUTH_ISSUER_URL:-}"
# Sites allowed to sign people in through this instance, as a JSON array on ONE line. Empty
# means none, which is correct until you have one. Worked example and the full field list:
# docs/specs/sso-integration.md. Single quotes - the value is full of double quotes.
#   AUTH_CLIENTS='[{"clientId":"wiki","displayName":"Wiki","redirectUris":["https://wiki.example.com/callback"],"scopes":["openid","profile","email"],"firstParty":true,"public":true}]'
AUTH_CLIENTS='${AUTH_CLIENTS:-}'
# Browser origins allowed to READ API responses, which is a separate grant from being allowed to
# sign people in. Additive; comma, semicolon or space separated. localhost:4200 and localhost:1420
# are built in, so a site of yours works in development whether or not this is set and then fails
# in production only, with a CORS error the browser shows and no server log at all.
#   CORS_ALLOWED_ORIGINS="https://isle.example.com"
CORS_ALLOWED_ORIGINS="${CORS_ALLOWED_ORIGINS:-}"

# ── Images ───────────────────────────────────────────────────────────────────────────
IMAGE_SOURCE="$IMAGE_SOURCE"
IMAGE_PREFIX="$IMAGE_PREFIX"
IMAGE_TAG="$IMAGE_TAG"

# ── Networking ───────────────────────────────────────────────────────────────────────
HTTP_BIND="0.0.0.0:80"
HTTPS_BIND="0.0.0.0:443"
GATEWAY_BIND="$GATEWAY_BIND"
MINIO_BIND="$MINIO_BIND"
MINIO_CONSOLE_BIND="127.0.0.1:9001"
RABBITMQ_MGMT_BIND="127.0.0.1:15672"
HAIRPIN_HOST_ENTRY="$HAIRPIN_HOST_ENTRY"

# ── Gateway / reverse-proxy trust ────────────────────────────────────────────────────
# Read by the echo container only - deliberately not shared with the other services, and
# stripped from the request before anything is proxied downstream.
#   GATEWAY_PROXY_SECRET    the reverse proxy sends this as the X-Echo-Proxy-Auth header;
#                           matching it is what makes X-Forwarded-For believable, which is
#                           what gives each caller their own rate-limit bucket. Leave it
#                           unset and every anonymous caller shares one bucket.
#   GATEWAY_TRUSTED_PROXIES optional address/CIDR allowlist, honoured in addition to the
#                           secret. Only useful where proxy addresses are stable.
GATEWAY_PROXY_SECRET="$GATEWAY_PROXY_SECRET"
GATEWAY_TRUSTED_PROXIES="${GATEWAY_TRUSTED_PROXIES:-}"

# ── PostgreSQL ───────────────────────────────────────────────────────────────────────
USE_EXTERNAL_DB="$USE_EXTERNAL_DB"
DATABASE_HOSTNAME="$DATABASE_HOSTNAME"
DATABASE_PORT="$DATABASE_PORT"
DATABASE_USERNAME="$DATABASE_USERNAME"
DATABASE_PASSWORD="$DATABASE_PASSWORD"

# ── Redis ────────────────────────────────────────────────────────────────────────────
REDIS_HOST="redis"
REDIS_PORT="6379"
REDIS_USERNAME=""
REDIS_PASSWORD="$REDIS_PASSWORD"

# ── RabbitMQ ─────────────────────────────────────────────────────────────────────────
RABBITMQ_HOST="rabbitmq"
RABBITMQ_PORT="5672"
RABBITMQ_USERNAME="$RABBITMQ_USERNAME"
RABBITMQ_PASSWORD="$RABBITMQ_PASSWORD"

# ── ScyllaDB (message store) ─────────────────────────────────────────────────────────
USE_SCYLLA="$USE_SCYLLA"
USE_SCYLLA_DB="$USE_SCYLLA_DB"
SCYLLA_HOST="scylladb"
SCYLLA_PORT="9042"
SCYLLA_USERNAME="cassandra"
SCYLLA_PASSWORD="$SCYLLA_PASSWORD"
SCYLLA_SMP="1"

# ── Object storage ───────────────────────────────────────────────────────────────────
USE_EXTERNAL_STORAGE="$USE_EXTERNAL_STORAGE"
BUCKET_NAME="$BUCKET_NAME"
ACCESS_KEY_ID="$ACCESS_KEY_ID"
SECRET_ACCESS_KEY="$SECRET_ACCESS_KEY"
STORAGE_PUBLIC_URL="$STORAGE_PUBLIC_URL"
STORAGE_SERVICE_URL="$STORAGE_SERVICE_URL"
STORAGE_USE_SERVICE_URL="$STORAGE_USE_SERVICE_URL"
STORAGE_REGION="$STORAGE_REGION"

# ── Auth ─────────────────────────────────────────────────────────────────────────────
AUTH_REQUIRE_USER_EMAIL_VERIFICATION="$AUTH_REQUIRE_USER_EMAIL_VERIFICATION"
IS_USER_HASH_GENERATION_ENABLED="true"
IDENTITY_KEY_PASSWORD="$IDENTITY_KEY_PASSWORD"
IDENTITY_SIGNING_CERT="$IDENTITY_SIGNING_CERT"
ACCOUNT_DELETION_GRACE_PERIOD_SECONDS="2592000"
ACCOUNT_DELETION_SWEEP_INTERVAL_SECONDS="300"

# ── Federation (Ed25519, PEM text, base64-encoded) ───────────────────────────────────
FEDERATION_PRIVATE_KEY_BASE_64="$FEDERATION_PRIVATE_KEY_BASE_64"
FEDERATION_PUBLIC_KEY_BASE_64="$FEDERATION_PUBLIC_KEY_BASE_64"

# ── Transactional e-mail (Microsoft Graph) ───────────────────────────────────────────
# Set both, then flip AUTH_REQUIRE_USER_EMAIL_VERIFICATION to "true" to require
# verified addresses at sign-up.
MICROSOFT_GRAPH_CLIENT_ID="${MICROSOFT_GRAPH_CLIENT_ID:-}"
MICROSOFT_GRAPH_CLIENT_SECRET="${MICROSOFT_GRAPH_CLIENT_SECRET:-}"

# ── Voice / video (LiveKit SFU) ──────────────────────────────────────────────────────
# Blank means no voice, which is a supported state: the voice endpoints answer 503 with a
# reason rather than failing obscurely. Point these at a LiveKit server to turn it on.
#
# LIVEKIT_API_SECRET is a root credential - it mints admin tokens for every room and the
# SFU verifies signatures offline, so there is no revocation short of rotating it.
LIVEKIT_API_KEY="${LIVEKIT_API_KEY:-}"
LIVEKIT_API_SECRET="${LIVEKIT_API_SECRET:-}"
# One node. REGION is any tag you like; SIGNALINGURL is public and handed to clients;
# APIURL is the control plane and must not be reachable from the internet.
LIVEKIT__NODES__0__REGION="${LIVEKIT_REGION:-}"
LIVEKIT__NODES__0__SIGNALINGURL="${LIVEKIT_SIGNALING_URL:-}"
LIVEKIT__NODES__0__APIURL="${LIVEKIT_API_URL:-}"

# ── License mode ─────────────────────────────────────────────────────────────────────
# "selfhost" means every limit is off: no plans, no tiers, no billing, nothing to buy and
# nothing to unlock. It is the default and it is what you want. Nothing here is checked
# against anything - there is no license key, nothing expires and nothing phones home.
LICENSE_MODE="${LICENSE_MODE:-selfhost}"

# ── Limits on this server ────────────────────────────────────────────────────────────
# A different question from the one above, and the only one worth thinking about: what will
# THIS machine do? Voice and video are forwarded by an SFU and paid for in egress, and a
# busy video room costs roughly forty times what the same room costs on audio alone - so if
# the bill is yours, these are the two numbers that decide it.
#
# Empty means no limit, which is how it ships. Set them and they apply on top of everything
# else, so nobody can exceed them however they joined.
#   VOICE_MAX_PARTICIPANTS   people in one voice room, e.g. 12
#   VOICE_VIDEO_CEILING      best video anyone may send: none, 480p30, 720p30, 1080p30,
#                            1080p60, 1440p60, 2160p60. Empty takes the stack's default of
#                            2160p60, and 4K is expensive until publishers send simulcast -
#                            1080p60 is the safe setting on a metered connection.
#                            ("none" leaves voice working and turns off camera and screenshare)
#   STORAGE_UPLOAD_MAX_BYTES largest single file anyone may upload, in bytes, e.g. 26214400
VOICE_MAX_PARTICIPANTS="${VOICE_MAX_PARTICIPANTS:-}"
VOICE_VIDEO_CEILING="${VOICE_VIDEO_CEILING:-}"
STORAGE_UPLOAD_MAX_BYTES="${STORAGE_UPLOAD_MAX_BYTES:-}"

# ── Push notifications ───────────────────────────────────────────────────────────────
FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64="${FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64:-}"
GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64="${GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64:-}"
APNS_BUNDLE_ID="${APNS_BUNDLE_ID:-gg.venta.mobile}"
APNS_KEY_ID="${APNS_KEY_ID:-}"
APNS_TEAM_ID="${APNS_TEAM_ID:-}"
APNS_AUTH_KEY_BASE_64="${APNS_AUTH_KEY_BASE_64:-}"
APNS_USE_SANDBOX="${APNS_USE_SANDBOX:-true}"

# ── Steam login ──────────────────────────────────────────────────────────────────────
STEAM_PUBLIC_BASE_URL="$INSTANCE_URL"
STEAM_PUBLIC_CALLBACK_PATH="/api/v1/identity/authentication/steam/callback"
STEAM_CLIENT_RETURN_URL="venta://steam-auth"
STEAM_WEB_API_KEY="${STEAM_WEB_API_KEY:-}"

# ── Discord import ───────────────────────────────────────────────────────────────────
DISCORD_IMPORT_BOT_TOKEN="${DISCORD_IMPORT_BOT_TOKEN:-}"
DISCORD_IMPORT_CLIENT_ID="${DISCORD_IMPORT_CLIENT_ID:-}"
DISCORD_IMPORT_PUBLIC_BASE_URL="$INSTANCE_URL"
DISCORD_IMPORT_PUBLIC_CALLBACK_PATH="/api/v1/imports/discord/callback"
DISCORD_IMPORT_CLIENT_RETURN_URL="venta://discord-import"

# ── The Isle integration ─────────────────────────────────────────────────────────────
ENABLE_ISLE="$ENABLE_ISLE"
ISLE_IP_ADDRESS="${ISLE_IP_ADDRESS:-10.0.0.0}"
ISLE_BRIDGE_PORT="${ISLE_BRIDGE_PORT:-8080}"
ISLE_RCON_PORT="${ISLE_RCON_PORT:-8888}"
ISLE_RCON_PASSWORD="${ISLE_RCON_PASSWORD:-}"

# ── Link previews ────────────────────────────────────────────────────────────────────
# When someone posts a URL, the unfurl service fetches that page and turns it into a preview
# card. Set to false if you would rather this server never made requests to third-party sites
# on your users' behalf - messages still send, they just show no preview.
UNFURL_ENABLED="${UNFURL_ENABLED:-true}"
# Leave this false. True disables the guard that stops a posted link from reaching your own
# private network - 127.0.0.1, your LAN, the cloud metadata endpoint - and any user could then
# make this server fetch them.
UNFURL_ALLOW_PRIVATE_TARGETS="false"
# Preview images are re-hosted here so that third-party sites never see your users' IP
# addresses. The URLs are stored in messages permanently, so changing this later leaves old
# previews pointing at the old address.
UNFURL_PUBLIC_BASE_URL="$INSTANCE_URL"

# ── The pantry's product lookup ──────────────────────────────────────────────────────
# Scanning a barcode a household has not seen before asks Open Food Facts, a public product
# database, what it is - which saves somebody typing a name for every packet in the bag. It works
# as shipped; nothing below has to be changed for it to run.
#
# Open Food Facts ask callers to identify themselves, and this is the address they are told. Put
# your own in: the lookups are your server's traffic, and this is who they would write to about
# it. An empty string turns the lookup off entirely.
PRODUCT_CATALOG_CONTACT_EMAIL="${PRODUCT_CATALOG_CONTACT_EMAIL:-hello@alpinebits.ch}"
# Set to false if this server should never ask a third party what your flatmates bought. Scans
# still work: they ask for a name the first time they see a code, and never again after that.
PRODUCT_CATALOG_LIVE_FILL_ENABLED="${PRODUCT_CATALOG_LIVE_FILL_ENABLED:-true}"

# ── Misc ─────────────────────────────────────────────────────────────────────────────
SENTRY_URL="${SENTRY_URL:-}"
PERSONAL_ACCESS_TOKEN="${PERSONAL_ACCESS_TOKEN:-}"
ENV
chmod 600 "$ENV_FILE"
umask 022
ok "wrote $ENV_FILE"

# =====================================================================================
# 6. Reverse proxy configuration
# =====================================================================================
step "6/9  Reverse proxy"

if [[ "$TLS_MODE" == "letsencrypt" ]]; then
    cat > "$GENERATED_DIR/Caddyfile" <<CADDY
# Generated by deploy/install.sh - edited copies are overwritten on re-run.
{
	email $ACME_EMAIL

	# Each published guild wiki answers on a hostname of its own beneath \$WIKI_DOMAIN. A wildcard
	# certificate would need the DNS-01 challenge, which needs a provider plugin and an API
	# credential this image does not carry - so those certificates are issued one at a time, on the
	# first request for a name. The ask endpoint is what keeps that bounded: the gateway answers 200
	# only for a slug some guild has actually published, so pointing an arbitrary name at this host
	# gets a refusal rather than a certificate.
	on_demand_tls {
		ask http://echo:8080/api/v1/wiki/certificate-check
	}
}

$INSTANCE_DOMAIN {
	encode zstd gzip

	request_body {
		max_size 100MB
	}

	# WebSockets (the /api/v1/ws/hub SignalR hub and the Discord-compatible bot gateway
	# at /api/discord/v10/gateway) are upgraded transparently by reverse_proxy.
	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		# Proves to the gateway that the X-Forwarded-For chain above was written by this
		# proxy, so it can rate-limit per real client instead of lumping everyone into one
		# bucket keyed on this container's address. header_up *sets*, so a client that sends
		# its own copy of this header has it overwritten here rather than merged. The gateway
		# strips it again before proxying to any backend service.
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
		flush_interval -1
	}
}

$STORAGE_DOMAIN {
	encode zstd gzip

	request_body {
		max_size 500MB
	}

	# Attachment URLs are path-style: {STORAGE_PUBLIC_URL}/{bucket}/{key}
	reverse_proxy minio:9000
}

# The API reference. Served by the gateway itself, which decides what to serve from the Host
# header - the docs exist only on this hostname, not under a path on the API domain - so this
# block must preserve the Host and point at the same container as the API.
$DOCS_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# The public support site: contact form, ban appeals, ticket lookup. Same container and the same
# Host-header gating as the docs above. Reached by people who cannot sign in - a banned account
# cannot get a token, which is the whole premise of an appeal - so nothing here may be put behind
# authentication at the proxy.
$SUPPORT_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# The moderation console. Also the same container - the console is a static page that calls the
# API same-origin, which is what keeps it out of the CORS allowlist.
#
# Access is decided by the account's own staff tier, checked against the database on every
# request, not by this block. If you want a second gate in front of it (an IP allowlist, mTLS,
# basic auth), this is the right place for it - but do not add one to $SUPPORT_DOMAIN above.
$ADMIN_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# The public status page. Same container, same Host-header gating, and read-only: nothing on this
# hostname accepts a write. It is the surface people reach when everything else is broken, so do
# not put it behind a gate of any kind - an outage page that needs the outage to be over before it
# will load is worth nothing.
$STATUS_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# Sign-in and SSO. Same container and the same Host-header gating as the sites above, but this
# hostname is not only a page: it is the OIDC issuer. /connect/**, /.well-known/openid-configuration
# and /.well-known/jwks all answer here, proxied through to Identity, and a partner site's server
# will fetch the last two from the outside.
#
# So, unlike the others, this block failing is not "a page is missing" - it is every sign-in
# through this instance failing. Two consequences worth stating:
#   * it must be reachable publicly and over real TLS. An OIDC client will refuse a plain-http
#     issuer, and this is where people type their password;
#   * do not put a gate of any kind in front of it (IP allowlist, basic auth), for the same
#     reason as $SUPPORT_DOMAIN: the people who need it are by definition not signed in yet.
$AUTH_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# Published guild wikis. Same container and the same Host-header gating, and read-only like the
# status page - but unlike every other site here, what it serves is prose somebody else wrote. The
# gateway renders it server-side under a policy that permits no script at all, and answers 404 on
# this hostname for anything that is not the wiki, so an XSS in a page has no same-origin path to
# the API. Do not add a rewrite, a gate, or anything that serves other content on this name.
#
# The apex serves no wiki of its own: it redirects /<slug> and /<slug>/<page> to the wiki's own
# hostname below. That path form is the one that keeps working with an ordinary certificate, so
# leave it in place even once the per-wiki names are issuing.
$WIKI_DOMAIN {
	encode zstd gzip

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}

# One hostname per published wiki. Certificates here are issued on demand, per name, gated by the
# ask endpoint in the global block above - a wildcard would need the DNS-01 challenge and a provider
# credential. A DNS record has to cover them: *.$WIKI_DOMAIN, pointing at this host.
*.$WIKI_DOMAIN {
	encode zstd gzip

	tls {
		on_demand
	}

	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
	}
}
CADDY
    ok "wrote $GENERATED_DIR/Caddyfile (Let's Encrypt, HTTP-01 on :80)"

    for port in 80 443; do
        if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
            ufw allow "$port"/tcp >/dev/null 2>&1 || true
        elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
            firewall-cmd --permanent --add-port="$port"/tcp >/dev/null 2>&1 || true
        fi
    done
    command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --reload >/dev/null 2>&1 || true
else
    mkdir -p "$GENERATED_DIR"
    : > "$GENERATED_DIR/Caddyfile"
    if [[ "$TLS_MODE" == "external-proxy" ]]; then
        cat <<PROXY

  Point your own reverse proxy at this host:

    ${BOLD}https://$INSTANCE_DOMAIN${NC}   ->  http://127.0.0.1:8080     (must forward WebSocket upgrades)
    ${BOLD}https://${STORAGE_DOMAIN}${NC}  ->  http://127.0.0.1:9000

  The gateway also serves six sites, each on its own hostname and nowhere else, all on the
  same 127.0.0.1:8080. Send them there too, preserving the Host header:

    ${BOLD}${DOCS_DOMAIN}${NC} ${BOLD}${ADMIN_DOMAIN}${NC} ${BOLD}${SUPPORT_DOMAIN}${NC} ${BOLD}${STATUS_DOMAIN}${NC} ${BOLD}${AUTH_DOMAIN}${NC} ${BOLD}${WIKI_DOMAIN}${NC}

  ${AUTH_DOMAIN} is the one that is more than a page: it is this instance's OIDC issuer, so
  /connect/** and /.well-known/openid-configuration answer there and partner sites fetch them
  from the outside. It needs public DNS and real TLS, and no gate in front of it.

  ${WIKI_DOMAIN} is the one that is more than one name: each published guild wiki answers on
  <slug>.${WIKI_DOMAIN}, so send ${BOLD}*.${WIKI_DOMAIN}${NC} to the same place. Your proxy
  needs a certificate covering those names - a wildcard, or per-name issuance gated on
  http://127.0.0.1:8080${BOLD}/api/v1/wiki/certificate-check?domain=<name>${NC}, which answers
  200 only for a wiki some guild has actually published. Until that certificate exists, do not
  advertise the per-wiki names: a browser meets a TLS warning rather than a missing page. The
  ${WIKI_DOMAIN}/<slug>/<page> form keeps working on the ordinary certificate and redirects.

  Forward the usual X-Forwarded-For / -Proto / -Host headers, and allow request bodies
  of at least 100 MB (500 MB on the storage host).

  Also set this header on requests to the gateway, replacing (not appending to) any copy
  the client sent - it is what lets the gateway believe your X-Forwarded-For and give each
  caller their own rate-limit bucket. Without it every anonymous caller shares one:

    ${BOLD}X-Echo-Proxy-Auth: $GATEWAY_PROXY_SECRET${NC}

  nginx:  proxy_set_header X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET";
  Caddy:  header_up X-Echo-Proxy-Auth "$GATEWAY_PROXY_SECRET"
  Traefik: a headers middleware with customRequestHeaders on that name.

  The value is in deploy/.env as GATEWAY_PROXY_SECRET. The gateway removes the header
  before proxying onward, so it never reaches the backend services.
PROXY
    else
        warn "no TLS: federation with other instances requires a public HTTPS endpoint"
    fi
fi

# =====================================================================================
# 7. ventactl + systemd
# =====================================================================================
step "7/9  Lifecycle management"

cat > "$VENTACTL_PATH" <<CTL
#!/usr/bin/env bash
# Venta stack control wrapper - generated by deploy/install.sh
set -euo pipefail
DEPLOY_DIR="$SCRIPT_DIR"
PROJECT="$PROJECT_NAME"

# COMPOSE_PROFILES decides which optional services exist at all, so it has to be in the
# environment for every single command - not just the ones that read other settings.
set -a
. "\$DEPLOY_DIR/.env"
set +a

dc() {
    docker compose -p "\$PROJECT" \\
        --project-directory "\$DEPLOY_DIR" \\
        -f "\$DEPLOY_DIR/compose.yaml" \\
        --env-file "\$DEPLOY_DIR/.env" "\$@"
}

case "\${1:-help}" in
    up|start)   dc up -d --remove-orphans ;;
    stop)       dc stop ;;
    down)       dc down ;;
    restart)    shift; dc restart "\$@" ;;
    ps|status)  dc ps ;;
    logs)       shift; dc logs -f --tail=200 "\$@" ;;
    config)     dc config ;;
    update)
        if [ "\${IMAGE_SOURCE:-registry}" = "build" ]; then
            (cd "\$DEPLOY_DIR/.." && git pull --ff-only) || true
            dc build --pull
        else
            dc pull
        fi
        dc up -d --remove-orphans
        ;;
    backup)
        out="\${2:-/var/backups/venta}"
        mkdir -p "\$out"
        stamp="\$(date -u +%Y%m%dT%H%M%SZ)"
        cp "\$DEPLOY_DIR/.env" "\$out/env-\$stamp.bak"
        chmod 600 "\$out/env-\$stamp.bak"
        if [ "\${USE_EXTERNAL_DB:-no}" = "yes" ]; then
            echo "external database: dump it with your own pg_dumpall against \$DATABASE_HOSTNAME"
        else
            dc exec -T postgres pg_dumpall -U "\$DATABASE_USERNAME" > "\$out/postgres-\$stamp.sql"
        fi
        echo "backup written to \$out"
        ;;
    federation-doc)
        curl -fsS "\$INSTANCE_URL/.well-known/federation" && echo
        ;;
    *)
        cat <<'USAGE'
ventactl <command>

  up | start        start the stack (also run at boot by venta-stack.service)
  stop              stop containers, keep them defined
  down              stop and remove containers (volumes are kept)
  restart [svc]     restart everything or one service
  ps | status       container status
  logs [svc]        follow logs
  update            pull/build the current images and restart
  backup [dir]      dump .env and the built-in PostgreSQL
  federation-doc    print this instance's public federation document
  config            render the fully-resolved compose configuration
USAGE
        ;;
esac
CTL
chmod 755 "$VENTACTL_PATH"
ok "installed $VENTACTL_PATH"

if command -v systemctl >/dev/null 2>&1; then
    cat > "$SYSTEMD_UNIT" <<UNIT
[Unit]
Description=Venta / Echo self-hosted stack
Documentation=file://$SCRIPT_DIR/README.md
Requires=docker.service
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=$SCRIPT_DIR
ExecStart=$VENTACTL_PATH up
ExecStop=$VENTACTL_PATH stop
TimeoutStartSec=0

[Install]
WantedBy=multi-user.target
UNIT
    # Never fatal: a container build host or a systemd-less init still gets a working
    # stack, since the containers' own restart policy brings them back with the daemon.
    if systemctl daemon-reload 2>/dev/null && systemctl enable venta-stack.service >/dev/null 2>&1; then
        ok "enabled venta-stack.service (starts the stack on boot)"
    else
        warn "could not enable venta-stack.service - start the stack with 'ventactl up'"
    fi
else
    warn "systemd not found - the containers' restart policy still brings them back with the Docker daemon"
fi

# =====================================================================================
# 8. Images and boot
# =====================================================================================
step "8/9  Images"

export COMPOSE_PROFILES

if [[ "$IMAGE_SOURCE" == "build" ]]; then
    if [[ ! -s "$REPO_ROOT/Messaging.Application/Credentials/sixlabors.lic" ]]; then
        warn "Messaging.Application/Credentials/sixlabors.lic is missing - a source build of the"
        warn "Messaging service needs a SixLabors ImageSharp license file there."
    fi
    log "building images from source (this takes a while)"
    compose_cmd build --pull
else
    log "pulling images from $IMAGE_PREFIX (tag: $IMAGE_TAG)"
    if ! compose_cmd pull; then
        warn "pull failed (private or unpublished registry?) - falling back to a source build"
        IMAGE_SOURCE="build"
        sed -i 's|^IMAGE_SOURCE=.*|IMAGE_SOURCE="build"|' "$ENV_FILE"
        compose_cmd build --pull
    fi
fi
ok "images ready"

step "9/9  Boot"

if [[ "$NO_START" == true ]]; then
    warn "--no-start given; run 'ventactl up' when you are ready"
    exit 0
fi

log "starting the stack"
compose_cmd up -d --remove-orphans

# Waiting on the gateway is enough of a smoke test: it only reports healthy once its own
# Wolverine host, database and Redis connection are up, and it actively health-checks
# every downstream service through YARP.
log "waiting for the gateway to report healthy (up to 5 minutes)"
deadline=$(( $(date +%s) + 300 ))
gateway_ok=false
while [[ $(date +%s) -lt $deadline ]]; do
    state="$(docker inspect -f '{{.State.Health.Status}}' "$(compose_cmd ps -q echo)" 2>/dev/null || echo starting)"
    if [[ "$state" == "healthy" ]]; then gateway_ok=true; break; fi
    sleep 5
done

if [[ "$gateway_ok" == true ]]; then
    ok "gateway healthy"
else
    warn "the gateway did not report healthy in time - check: ventactl logs echo"
fi

# =====================================================================================
# Summary
# =====================================================================================
probe() {
    local url="$1" label="$2"
    if curl -fsS --max-time 15 -o /dev/null "$url" 2>/dev/null; then
        printf "  ${GREEN}✓${NC} %-34s %s\n" "$label" "$url"
    else
        printf "  ${YELLOW}·${NC} %-34s %s ${DIM}(not answering yet)${NC}\n" "$label" "$url"
    fi
}

printf "\n${BOLD}Installation summary${NC}\n"
printf "  instance          %s\n" "$INSTANCE_NAME"
printf "  public URL        %s\n" "$INSTANCE_URL"
printf "  attachments       %s\n" "$STORAGE_PUBLIC_URL"
printf "  TLS               %s\n" "$TLS_MODE"
printf "  images            %s (%s:%s)\n" "$IMAGE_SOURCE" "$IMAGE_PREFIX" "$IMAGE_TAG"
printf "  profiles          %s\n" "${COMPOSE_PROFILES:-<none>}"
printf "  configuration     %s\n" "$ENV_FILE"

printf "\n${BOLD}Endpoint checks${NC}\n"
probe "$INSTANCE_URL/health"                          "gateway health"
probe "$INSTANCE_URL/.well-known/openid-configuration" "OpenID discovery"
probe "$INSTANCE_URL/.well-known/federation"           "federation document"

cat <<NEXT

${BOLD}Federating with another instance${NC}
  Your public key and capabilities are published at
      $INSTANCE_URL/.well-known/federation
  Start a handshake with a peer (admin token required):
      curl -X POST $INSTANCE_URL/api/v1/admin/federation/initiate \\
           -H "Authorization: Bearer <admin-token>" \\
           -H 'Content-Type: application/json' \\
           -d '{"host":"https://peer.example.com"}'
  Then approve inbound requests:
      curl $INSTANCE_URL/api/v1/admin/federation/instances -H "Authorization: Bearer <admin-token>"
      curl -X POST $INSTANCE_URL/api/v1/admin/federation/<id>/approve -H "Authorization: Bearer <admin-token>"

${BOLD}Day to day${NC}
  ventactl status | logs [service] | restart [service] | update | backup

NEXT

if [[ "$TLS_MODE" == "letsencrypt" ]]; then
    printf "${DIM}Certificates are issued and renewed by Caddy; make sure %s and %s\nresolve to this host's public IP and that ports 80/443 are reachable.${NC}\n\n" \
        "$INSTANCE_DOMAIN" "$STORAGE_DOMAIN"
fi

ok "done"
