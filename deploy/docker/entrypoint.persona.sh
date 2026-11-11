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

# Restore Google AI Pro OAuth token if mounted from Secret volume or passed via environment
if [ -f /etc/agentmesh-creds/antigravity-oauth-token ] && [ ! -s /root/.gemini/antigravity-cli/antigravity-oauth-token ]; then
    mkdir -p /root/.gemini/antigravity-cli
    cp /etc/agentmesh-creds/antigravity-oauth-token /root/.gemini/antigravity-cli/antigravity-oauth-token
    chmod 600 /root/.gemini/antigravity-cli/antigravity-oauth-token
elif [ -n "$ANTIGRAVITY_OAUTH_TOKEN" ] && [ ! -s /root/.gemini/antigravity-cli/antigravity-oauth-token ]; then
    mkdir -p /root/.gemini/antigravity-cli
    echo "$ANTIGRAVITY_OAUTH_TOKEN" > /root/.gemini/antigravity-cli/antigravity-oauth-token
    chmod 600 /root/.gemini/antigravity-cli/antigravity-oauth-token
fi

# Ensure workspace directory exists and is marked safe for git operations
mkdir -p /workspace
cd /workspace
git config --global --add safe.directory '*' 2>/dev/null || true

# Configure GitHub CLI, Copilot CLI, and git identity if provided
if [ -n "$GITHUB_TOKEN" ] || [ -n "$GH_TOKEN" ] || [ -n "$COPILOT_GITHUB_TOKEN" ]; then
    gh auth setup-git 2>/dev/null || true
    COPILOT_AUTH_TOKEN="${COPILOT_GITHUB_TOKEN:-${GH_TOKEN:-$GITHUB_TOKEN}}"
    if [ -n "$COPILOT_AUTH_TOKEN" ] && [ ! -s /root/.copilot/config.json ]; then
        mkdir -p /root/.copilot
        printf '{"token":"%s"}\n' "$COPILOT_AUTH_TOKEN" > /root/.copilot/config.json
        chmod 600 /root/.copilot/config.json
    fi
fi

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

# Ensure persistent workspace state directories exist with restricted permissions
mkdir -p /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip /workspace/.agentmesh/state/.gemini /workspace/.agentmesh/state/.claude /workspace/.agentmesh/state/.copilot /workspace/.agentmesh/state/.local/bin
chmod 700 /workspace/.agentmesh/state 2>/dev/null || true
chmod 700 /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip /workspace/.agentmesh/state/.gemini /workspace/.agentmesh/state/.claude /workspace/.agentmesh/state/.copilot /workspace/.agentmesh/state/.local 2>/dev/null || true

# Ensure ~/.bashrc exports PATH for all interactive shells
if ! grep -q '/root/.local/bin' /root/.bashrc 2>/dev/null; then
    echo 'export PATH="/root/.local/bin:/usr/local/bin:$PATH"' >> /root/.bashrc
fi

if [ -n "$COPILOT_GITHUB_TOKEN" ] && ! grep -q 'COPILOT_GITHUB_TOKEN' /root/.bashrc 2>/dev/null; then
    echo "export COPILOT_GITHUB_TOKEN=\"$COPILOT_GITHUB_TOKEN\"" >> /root/.bashrc
fi
if [ -n "$GH_TOKEN" ] && ! grep -q 'GH_TOKEN' /root/.bashrc 2>/dev/null; then
    echo "export GH_TOKEN=\"$GH_TOKEN\"" >> /root/.bashrc
fi
if [ -n "$GITHUB_TOKEN" ] && ! grep -q 'GITHUB_TOKEN' /root/.bashrc 2>/dev/null; then
    echo "export GITHUB_TOKEN=\"$GITHUB_TOKEN\"" >> /root/.bashrc
fi
if ! grep -q 'REMOTE_CONTAINERS' /root/.bashrc 2>/dev/null; then
    echo 'export REMOTE_CONTAINERS="true"' >> /root/.bashrc
fi
if ! grep -q 'copilot() {' /root/.bashrc 2>/dev/null; then
    cat << 'EOF' >> /root/.bashrc
copilot() {
    if [ "$1" = "login" ]; then
        env -u GITHUB_TOKEN -u GH_TOKEN -u COPILOT_GITHUB_TOKEN command copilot login --device-code "${@:2}"
    else
        command copilot "$@"
    fi
}
EOF
fi

# If ~/.config, ~/.aip, ~/.gemini, ~/.claude, or ~/.copilot was previously created as a symlink, remove it so credential managers have real directories
if [ -L /root/.config ]; then rm -f /root/.config; fi
if [ -L /root/.aip ]; then rm -f /root/.aip; fi
if [ -L /root/.gemini ]; then rm -f /root/.gemini; fi
if [ -L /root/.claude ]; then rm -f /root/.claude; fi
if [ -L /root/.copilot ]; then rm -f /root/.copilot; fi
mkdir -p /root/.config /root/.aip /root/.gemini /root/.claude /root/.copilot /root/.local/bin
chmod 700 /root/.config /root/.aip /root/.gemini /root/.claude /root/.copilot 2>/dev/null || true

# Restore persistent state from workspace volume into local directories
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

# Helper to synchronize state back to workspace volume
sync_persona_state() {
    mkdir -p /workspace/.agentmesh/state/.config
    if [ -d /root/.config ]; then
        cp -a /root/.config/. /workspace/.agentmesh/state/.config/ 2>/dev/null || true
    fi
    if [ -d /root/.aip ]; then
        mkdir -p /workspace/.agentmesh/state/.aip
        cp -a /root/.aip/. /workspace/.agentmesh/state/.aip/ 2>/dev/null || true
    fi
    if [ -d /root/.gemini ]; then
        mkdir -p /workspace/.agentmesh/state/.gemini
        cp -a /root/.gemini/. /workspace/.agentmesh/state/.gemini/ 2>/dev/null || true
    fi
    if [ -d /root/.claude ]; then
        mkdir -p /workspace/.agentmesh/state/.claude
        cp -a /root/.claude/. /workspace/.agentmesh/state/.claude/ 2>/dev/null || true
    fi
    if [ -d /root/.copilot ]; then
        mkdir -p /workspace/.agentmesh/state/.copilot
        cp -a /root/.copilot/. /workspace/.agentmesh/state/.copilot/ 2>/dev/null || true
    fi
    if [ -d /root/.local/bin ]; then
        mkdir -p /workspace/.agentmesh/state/.local/bin
        cp -a /root/.local/bin/. /workspace/.agentmesh/state/.local/bin/ 2>/dev/null || true
        chmod 700 /workspace/.agentmesh/state/.local 2>/dev/null || true
    fi
    # Also synchronize all existing state directories in /workspace/.agentmesh/state
    if [ -d /workspace/.agentmesh/state ]; then
        for sdir in /workspace/.agentmesh/state/.*; do
            bname=$(basename "$sdir")
            if [ "$bname" != "." ] && [ "$bname" != ".." ] && [ -d "$sdir" ] && [ -d "/root/$bname" ]; then
                cp -a "/root/$bname/." "$sdir/" 2>/dev/null || true
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
        sleep 10
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
    npm install -g @github/copilot 2>/dev/null || true
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
