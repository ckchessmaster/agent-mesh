#!/usr/bin/env bash
set -e

export PATH="/usr/local/bin:/root/.local/bin:$PATH"
export REMOTE_CONTAINERS="${REMOTE_CONTAINERS:-true}"

# Allow direct shell execution when invoked with bash or sh
if [ "$1" = "bash" ] || [ "$1" = "sh" ] || [ "$1" = "/bin/bash" ] || [ "$1" = "/bin/sh" ]; then
    exec "$@"
fi

# Configure defaults
export AGENTMESH_URL="${AGENTMESH_URL:-http://host.docker.internal:5000}"
export AGENTMESH_TOKEN="${AGENTMESH_TOKEN:-}"
export AGENTMESH_PERSONA_ID="${AGENTMESH_PERSONA_ID:-}"
export AGENTMESH_DRIVER_ID="${AGENTMESH_DRIVER_ID:-}"

# Allow persona ID to be passed as first argument if not defined in environment
if [ -z "$AGENTMESH_PERSONA_ID" ] && [ -n "$1" ] && [[ "$1" != -* ]]; then
    export AGENTMESH_PERSONA_ID="$1"
    shift
fi

# Ensure workspace directory exists and is marked safe for git operations
mkdir -p /workspace
cd /workspace
git config --global --add safe.directory '*' 2>/dev/null || true

# Ensure persistent workspace state directories exist with restricted permissions
mkdir -p /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip /workspace/.agentmesh/state/.gemini /workspace/.agentmesh/state/.claude /workspace/.agentmesh/state/.copilot /workspace/.agentmesh/state/.local/bin
chmod 700 /workspace/.agentmesh/state 2>/dev/null || true
chmod 700 /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip /workspace/.agentmesh/state/.gemini /workspace/.agentmesh/state/.claude /workspace/.agentmesh/state/.copilot /workspace/.agentmesh/state/.local 2>/dev/null || true

# If ~/.config, ~/.aip, ~/.gemini, ~/.claude, or ~/.copilot was previously created as a symlink, remove it so credential managers have real directories
if [ -L /root/.config ]; then rm -f /root/.config; fi
if [ -L /root/.aip ]; then rm -f /root/.aip; fi
if [ -L /root/.gemini ]; then rm -f /root/.gemini; fi
if [ -L /root/.claude ]; then rm -f /root/.claude; fi
if [ -L /root/.copilot ]; then rm -f /root/.copilot; fi
mkdir -p /root/.config /root/.aip /root/.gemini /root/.claude /root/.copilot /root/.agentmesh /root/.local/bin
chmod 700 /root/.config /root/.aip /root/.gemini /root/.claude /root/.copilot /root/.agentmesh 2>/dev/null || true

# Restore persistent state from workspace volume into local directories BEFORE applying fresh credentials
if [ -d /workspace/.agentmesh/state ]; then
    for sdir in /workspace/.agentmesh/state/.*; do
        bname=$(basename "$sdir")
        if [ "$bname" != "." ] && [ "$bname" != ".." ] && [ -d "$sdir" ]; then
            if [ -L "/root/$bname" ]; then rm -f "/root/$bname"; fi
            mkdir -p "/root/$bname"
            cp -a "$sdir/." "/root/$bname/" 2>/dev/null || true
            chmod 700 "/root/$bname" 2>/dev/null || true
        fi
    done
fi
if [ -d /workspace/.agentmesh/state/.local/bin ]; then
    mkdir -p /root/.local/bin
    cp -a /workspace/.agentmesh/state/.local/bin/. /root/.local/bin/ 2>/dev/null || true
    chmod 755 /root/.local/bin/* 2>/dev/null || true
fi

# Ensure Google AI Pro OAuth token is in place if mounted from Secret volume or passed via environment
if [ -f /etc/agentmesh-creds/antigravity-oauth-token ] && [ ! -s /root/.gemini/antigravity-cli/antigravity-oauth-token ]; then
    mkdir -p /root/.gemini/antigravity-cli
    cp /etc/agentmesh-creds/antigravity-oauth-token /root/.gemini/antigravity-cli/antigravity-oauth-token
    chmod 600 /root/.gemini/antigravity-cli/antigravity-oauth-token
elif [ -n "$ANTIGRAVITY_OAUTH_TOKEN" ] && [ ! -s /root/.gemini/antigravity-cli/antigravity-oauth-token ]; then
    mkdir -p /root/.gemini/antigravity-cli
    echo "$ANTIGRAVITY_OAUTH_TOKEN" > /root/.gemini/antigravity-cli/antigravity-oauth-token
    chmod 600 /root/.gemini/antigravity-cli/antigravity-oauth-token
fi

# 1. Configure Persona GitHub PAT for git & gh CLI (strictly separate from Copilot CLI auth)
PERSONA_GH_TOKEN="${AGENTMESH_GITHUB_TOKEN:-${GH_TOKEN:-$GITHUB_TOKEN}}"
if [ -n "$PERSONA_GH_TOKEN" ]; then
    export AGENTMESH_GITHUB_TOKEN="$PERSONA_GH_TOKEN"
    mkdir -p /root/.agentmesh /root/.config/gh
    printf '%s\n' "$PERSONA_GH_TOKEN" > /root/.agentmesh/github-pat
    chmod 600 /root/.agentmesh/github-pat
    cat <<EOF > /root/.config/gh/hosts.yml
github.com:
    users:
        git:
            oauth_token: $PERSONA_GH_TOKEN
    oauth_token: $PERSONA_GH_TOKEN
    user: git
    git_protocol: https
EOF
    chmod 600 /root/.config/gh/hosts.yml
    env -u GITHUB_TOKEN -u GH_TOKEN gh auth setup-git 2>/dev/null || gh auth setup-git 2>/dev/null || true
fi

# 2. Configure GitHub Copilot CLI auth separately (never use GH_TOKEN/GITHUB_TOKEN or classic ghp_ PATs)
if [[ "${COPILOT_GITHUB_TOKEN:-}" == ghp_* ]] || [[ "${COPILOT_GITHUB_TOKEN:-}" == GHP_* ]]; then
    unset COPILOT_GITHUB_TOKEN
elif [ -z "${COPILOT_OAUTH_TOKEN:-}" ] && [ -n "$PERSONA_GH_TOKEN" ] && [ "${COPILOT_GITHUB_TOKEN:-}" = "$PERSONA_GH_TOKEN" ]; then
    unset COPILOT_GITHUB_TOKEN
fi

# Ensure storeTokenPlaintext: true in ~/.copilot/settings.json (headless containers have no OS
# keyring, so interactive logins and re-runs need plaintext persistence). Comment-tolerant: the Copilot
# CLI writes these files as JSONC with "//" header lines.
#
# NOTE: This entrypoint deliberately does NOT seed or rewrite ~/.copilot/config.json anymore.
# The real Copilot CLI keys token entries by "https://github.com:<login>" and resolves the active
# sign-in through lastLoggedInUser/loggedInUsers (camelCase); historical jq/C# seeders wrote bare
# host keys and only understood legacy snake_case records, producing files the CLI rejects wholesale
# ("No authentication information found") while AgentMesh checks still passed. Config.json is now
# owned by the Copilot CLI itself (interactive login) and by AgentMesh's API-side native-format
# seeding; chat invocations additionally always receive COPILOT_GITHUB_TOKEN in the environment,
# which the CLI checks first and which wins over any file state.
for cp_settings in /root/.copilot/settings.json /workspace/.agentmesh/state/.copilot/settings.json; do
    mkdir -p "$(dirname "$cp_settings")"
    if [ -s "$cp_settings" ]; then
        if command -v jq >/dev/null 2>&1; then
            TMP_SET=$(mktemp)
            if sed '/^[[:space:]]*\/\//d' "$cp_settings" | jq '.storeTokenPlaintext = true' > "$TMP_SET" 2>/dev/null; then
                mv "$TMP_SET" "$cp_settings"
            else
                rm -f "$TMP_SET"
                printf '{"storeTokenPlaintext":true}\n' > "$cp_settings"
            fi
        fi
    else
        printf '{"storeTokenPlaintext":true}\n' > "$cp_settings"
    fi
    chmod 600 "$cp_settings" 2>/dev/null || true
done

if [ -n "$GIT_AUTHOR_NAME" ]; then
    git config --global user.name "$GIT_AUTHOR_NAME" 2>/dev/null || true
fi

if [ -n "$GIT_AUTHOR_EMAIL" ]; then
    git config --global user.email "$GIT_AUTHOR_EMAIL" 2>/dev/null || true
fi

# Wait for connectivity sidecar if configured
if [ "$SIDECAR_ENABLED" = "true" ]; then
    echo "Connectivity sidecar detected. Waiting for network readiness..."
    READY=0
    for i in {1..30}; do
        if [ -d /sys/class/net/tailscale0 ] || grep -q "tailscale0:" /proc/net/dev 2>/dev/null || (command -v ip >/dev/null 2>&1 && ip addr show tailscale0 >/dev/null 2>&1) || [ "$SIDECAR_USERSPACE" = "true" ]; then
            echo "Connectivity sidecar network interface ready."
            READY=1
            break
        fi
        sleep 1
    done
    if [ "$READY" -ne 1 ]; then
        echo "Warning: Connectivity sidecar did not establish tailscale0 interface within 30s. Proceeding..."
    fi

    # When running Tailscale in tun mode, ensure MagicDNS (100.100.100.100) is configured in /etc/resolv.conf
    if [ -d /sys/class/net/tailscale0 ] || grep -q "tailscale0:" /proc/net/dev 2>/dev/null || (command -v ip >/dev/null 2>&1 && ip addr show tailscale0 >/dev/null 2>&1); then
        if ! grep -q "100.100.100.100" /etc/resolv.conf 2>/dev/null; then
            CURRENT_RESOLV=$(cat /etc/resolv.conf 2>/dev/null || true)
            {
                echo "nameserver 100.100.100.100"
                echo "$CURRENT_RESOLV"
            } > /etc/resolv.conf 2>/dev/null || true
            echo "Configured Tailscale MagicDNS resolver (100.100.100.100) in /etc/resolv.conf."
        fi
    fi
fi

# Ensure ~/.bashrc exports PATH and clean credential separation for interactive shells
touch /root/.bashrc
sed -i '/^export COPILOT_GITHUB_TOKEN=/d;/^export AGENTMESH_GITHUB_TOKEN=/d;/^export GH_TOKEN=/d;/^export GITHUB_TOKEN=/d' /root/.bashrc 2>/dev/null || true

if ! grep -q '/root/.local/bin' /root/.bashrc 2>/dev/null; then
    echo 'export PATH="/root/.local/bin:/usr/local/bin:$PATH"' >> /root/.bashrc
fi

if [ -n "${COPILOT_GITHUB_TOKEN:-}" ]; then
    echo "export COPILOT_GITHUB_TOKEN=\"$COPILOT_GITHUB_TOKEN\"" >> /root/.bashrc
fi
if [ -n "${AGENTMESH_GITHUB_TOKEN:-}" ]; then
    echo "export AGENTMESH_GITHUB_TOKEN=\"$AGENTMESH_GITHUB_TOKEN\"" >> /root/.bashrc
fi
if [ -n "${GH_TOKEN:-}" ]; then
    echo "export GH_TOKEN=\"$GH_TOKEN\"" >> /root/.bashrc
fi
if [ -n "${GITHUB_TOKEN:-}" ]; then
    echo "export GITHUB_TOKEN=\"$GITHUB_TOKEN\"" >> /root/.bashrc
fi
if ! grep -q 'REMOTE_CONTAINERS' /root/.bashrc 2>/dev/null; then
    echo 'export REMOTE_CONTAINERS="true"' >> /root/.bashrc
fi
if ! grep -q '# agentmesh-copilot-wrapper-v4' /root/.bashrc 2>/dev/null; then
    sed -i '/^# agentmesh-copilot-wrapper/,/^}/d;/^copilot() {/,/^}/d' /root/.bashrc 2>/dev/null || true
    cat << 'EOF' >> /root/.bashrc
# agentmesh-copilot-wrapper-v4
copilot() {
    if [ "$1" = "login" ]; then
        local _iso_gh
        _iso_gh=$(mktemp -d 2>/dev/null || echo "/tmp/copilot-gh-iso-$$")
        command env -u GITHUB_TOKEN -u GH_TOKEN -u COPILOT_GITHUB_TOKEN -u COPILOT_OAUTH_TOKEN GH_CONFIG_DIR="$_iso_gh" copilot login --device-code "${@:2}"
        local _rc=$?
        rm -rf "$_iso_gh" 2>/dev/null || true
        return $_rc
    elif [[ "${COPILOT_GITHUB_TOKEN:-}" == ghp_* ]] || [[ "${COPILOT_GITHUB_TOKEN:-}" == GHP_* ]] || { [ -n "${AGENTMESH_GITHUB_TOKEN:-}" ] && [ "${COPILOT_GITHUB_TOKEN:-}" = "${AGENTMESH_GITHUB_TOKEN}" ]; }; then
        command env -u GITHUB_TOKEN -u GH_TOKEN -u COPILOT_GITHUB_TOKEN copilot "$@"
    else
        command env -u GITHUB_TOKEN -u GH_TOKEN copilot "$@"
    fi
}
EOF
fi

# Helper to synchronize state back to workspace volume
sync_persona_state() {
    mkdir -p /workspace/.agentmesh/state/.config
    if [ -d /root/.config ]; then
        cp -au /root/.config/. /workspace/.agentmesh/state/.config/ 2>/dev/null || true
    fi
    if [ -d /root/.aip ]; then
        mkdir -p /workspace/.agentmesh/state/.aip
        cp -au /root/.aip/. /workspace/.agentmesh/state/.aip/ 2>/dev/null || true
    fi
    if [ -d /root/.gemini ]; then
        mkdir -p /workspace/.agentmesh/state/.gemini
        (
            shopt -s dotglob nullglob
            for gitem in /root/.gemini/*; do
                gname=$(basename "$gitem")
                if [ "$gname" = "antigravity-cli" ] && [ -d "$gitem" ]; then
                    mkdir -p /workspace/.agentmesh/state/.gemini/antigravity-cli
                    for aitem in "$gitem"/*; do
                        aname=$(basename "$aitem")
                        case "$aname" in
                            log|crashes|cache) continue ;;
                        esac
                        cp -au "$aitem" /workspace/.agentmesh/state/.gemini/antigravity-cli/ 2>/dev/null || true
                    done
                else
                    cp -au "$gitem" /workspace/.agentmesh/state/.gemini/ 2>/dev/null || true
                fi
            done
        )
    fi
    if [ -d /root/.claude ]; then
        mkdir -p /workspace/.agentmesh/state/.claude
        cp -au /root/.claude/. /workspace/.agentmesh/state/.claude/ 2>/dev/null || true
    fi
    if [ -d /root/.copilot ]; then
        mkdir -p /workspace/.agentmesh/state/.copilot
        cp -au /root/.copilot/. /workspace/.agentmesh/state/.copilot/ 2>/dev/null || true
    fi
    if [ -d /root/.local/bin ]; then
        mkdir -p /workspace/.agentmesh/state/.local/bin
        cp -au /root/.local/bin/. /workspace/.agentmesh/state/.local/bin/ 2>/dev/null || true
        chmod 700 /workspace/.agentmesh/state/.local 2>/dev/null || true
    fi
    # Also synchronize any additional custom state directories in /workspace/.agentmesh/state
    if [ -d /workspace/.agentmesh/state ]; then
        for sdir in /workspace/.agentmesh/state/.*; do
            bname=$(basename "$sdir")
            case "$bname" in
                .|..|.config|.aip|.gemini|.claude|.copilot|.local) continue ;;
            esac
            if [ -d "$sdir" ] && [ -d "/root/$bname" ]; then
                cp -au "/root/$bname/." "$sdir/" 2>/dev/null || true
                chmod 700 "$sdir" 2>/dev/null || true
            fi
        done
    fi
    chmod 700 /workspace/.agentmesh/state 2>/dev/null || true
}

# Ensure MagicDNS is added to /etc/resolv.conf if tailscale network interface exists
ensure_tailscale_dns() {
    if [ "$SIDECAR_ENABLED" = "true" ] && ([ -d /sys/class/net/tailscale0 ] || grep -q "tailscale0:" /proc/net/dev 2>/dev/null); then
        if ! grep -q "100.100.100.100" /etc/resolv.conf 2>/dev/null; then
            CURRENT_RESOLV=$(cat /etc/resolv.conf 2>/dev/null || true)
            {
                echo "nameserver 100.100.100.100"
                echo "$CURRENT_RESOLV"
            } > /etc/resolv.conf 2>/dev/null || true
            echo "Configured Tailscale MagicDNS resolver (100.100.100.100) in /etc/resolv.conf."
        fi
    fi
}

# Run companion setup script if configured (e.g. install helper CLI)
run_companion_setup() {
    if [ -z "$COMPANION_SETUP" ]; then
        return 0
    fi
    echo "Executing companion setup script..."
    local setup_ok=0
    for attempt in {1..15}; do
        ensure_tailscale_dns
        if ( set -o pipefail; eval "$COMPANION_SETUP" ); then
            setup_ok=1
            hash -r 2>/dev/null || true
            echo "Companion setup completed successfully."
            break
        fi
        echo "Companion setup attempt $attempt/15 failed. Retrying in 2s..."
        sleep 2
    done
    if [ "$setup_ok" -ne 1 ]; then
        echo "Warning: Companion setup failed after 15 attempts. Proceeding..."
    fi
    sync_persona_state
}

if [ -n "$COMPANION_SETUP" ]; then
    run_companion_setup
fi

# Start background periodic state synchronization worker
(
    while true; do
        sleep 60
        sync_persona_state 2>/dev/null || true
    done
) &
SYNC_PID=$!

# Start companion background daemon supervisor if configured (e.g. token refresh or proxy)
if [ -n "$COMPANION_COMMAND" ]; then
    echo "Starting companion daemon supervisor: $COMPANION_COMMAND"
    (
        COMPANION_BIN=$(echo "$COMPANION_COMMAND" | awk '{print $1}')
        while true; do
            # If the companion executable is not on PATH, attempt setup/recovery instead of crash looping
            if [ -n "$COMPANION_BIN" ] && ! command -v "$COMPANION_BIN" >/dev/null 2>&1; then
                ensure_tailscale_dns
                if [ -n "$COMPANION_SETUP" ]; then
                    echo "Companion binary '$COMPANION_BIN' not found on PATH. Attempting companion setup..."
                    SETUP_OUT=$(eval "$COMPANION_SETUP" 2>&1) && SETUP_OK=1 || SETUP_OK=0
                    if [ "$SETUP_OK" -eq 1 ]; then
                        echo "Companion setup succeeded."
                    else
                        echo "Companion setup failed: $SETUP_OUT"
                        if echo "$SETUP_OUT" | grep -qi "Could not resolve host"; then
                            echo "Note: Host resolution failed. Verify Tailscale sidecar connection and MagicDNS."
                        fi
                    fi
                    hash -r 2>/dev/null || true
                    sync_persona_state 2>/dev/null || true
                fi
                if ! command -v "$COMPANION_BIN" >/dev/null 2>&1; then
                    echo "Waiting for companion binary '$COMPANION_BIN' to become available..."
                    sleep 5
                    continue
                fi
                echo "Companion binary '$COMPANION_BIN' is now available."
            fi

            eval "$COMPANION_COMMAND" || true
            sleep 2
        done
    ) &
    COMPANION_PID=$!
fi

# Await companion daemon readiness port if configured
if [ -n "$COMPANION_PORT" ]; then
    echo "Waiting for companion daemon on port $COMPANION_PORT..."
    PORT_READY=0
    for i in {1..15}; do
        if timeout 1 bash -c "</dev/tcp/127.0.0.1/$COMPANION_PORT" >/dev/null 2>&1 || \
           curl -s "http://127.0.0.1:$COMPANION_PORT/" >/dev/null 2>&1; then
            echo "Companion daemon ready on port $COMPANION_PORT."
            PORT_READY=1
            break
        fi
        sleep 1
    done
    if [ "$PORT_READY" -ne 1 ]; then
        echo "Warning: Companion daemon did not respond on port $COMPANION_PORT within 15s. Proceeding in background..."
    fi
fi

cleanup() {
    echo "Persisting persona state before termination..."
    if [ -n "$RUNNER_PID" ] && kill -0 "$RUNNER_PID" 2>/dev/null; then
        kill -TERM "$RUNNER_PID" 2>/dev/null || true
        wait "$RUNNER_PID" 2>/dev/null || true
    fi
    sync_persona_state
    if [ -n "$COMPANION_PID" ] && kill -0 "$COMPANION_PID" 2>/dev/null; then
        kill "$COMPANION_PID" 2>/dev/null || true
    fi
    if [ -n "$SYNC_PID" ] && kill -0 "$SYNC_PID" 2>/dev/null; then
        kill "$SYNC_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT SIGTERM SIGINT

echo "=========================================================="
echo " AgentMesh Dedicated Persona Environment"
echo " Persona:   ${AGENTMESH_PERSONA_ID:-<undefined>}"
echo " Driver:    ${AGENTMESH_DRIVER_ID:-<auto-resolve>}"
echo " Mesh URL:  ${AGENTMESH_URL}"
echo " Workspace: $(pwd)"
echo " Host OS:   $(uname -s) $(uname -m)"
echo "=========================================================="

if [ -z "$AGENTMESH_PERSONA_ID" ]; then
    echo "ERROR: AGENTMESH_PERSONA_ID must be set for dedicated persona environment."
    exit 1
fi

# Ensure GitHub Copilot CLI is installed if missing and npm is available
if command -v npm >/dev/null 2>&1 && ! command -v copilot >/dev/null 2>&1; then
    echo "Ensuring GitHub Copilot CLI is available..."
    npm install -g @github/copilot@1.0.112 2>/dev/null || true   # pinned: credential file format is validated against this CLI version
    hash -r 2>/dev/null || true
fi

ARGS=(run --persona "$AGENTMESH_PERSONA_ID" --url "$AGENTMESH_URL" --headless)

if [ -n "$AGENTMESH_DRIVER_ID" ]; then
    ARGS+=(--driver "$AGENTMESH_DRIVER_ID")
fi

if [ -n "$AGENTMESH_TOKEN" ]; then
    ARGS+=(--token "$AGENTMESH_TOKEN")
fi

if [ -n "$AGENTMESH_MODEL" ]; then
    ARGS+=(--model "$AGENTMESH_MODEL")
fi

agentmesh "${ARGS[@]}" "$@" &
RUNNER_PID=$!
wait "$RUNNER_PID"
