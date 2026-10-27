#!/usr/bin/env bash
set -e

export PATH="/usr/local/bin:/root/.local/bin:$PATH"

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

# Configure GitHub CLI and git identity if provided
if [ -n "$GITHUB_TOKEN" ] || [ -n "$GH_TOKEN" ]; then
    gh auth setup-git 2>/dev/null || true
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
mkdir -p /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip
chmod 700 /workspace/.agentmesh/state 2>/dev/null || true
chmod 700 /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip 2>/dev/null || true

# Ensure ~/.bashrc exports PATH for all interactive shells
if ! grep -q '/root/.local/bin' /root/.bashrc 2>/dev/null; then
    echo 'export PATH="/root/.local/bin:/usr/local/bin:$PATH"' >> /root/.bashrc
fi

# If ~/.config or ~/.aip was previously created as a symlink, remove it so credential managers have real directories
if [ -L /root/.config ]; then rm -f /root/.config; fi
if [ -L /root/.aip ]; then rm -f /root/.aip; fi
mkdir -p /root/.config /root/.aip
chmod 700 /root/.config /root/.aip 2>/dev/null || true

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

# Run companion setup script if configured (e.g. install helper CLI)
if [ -n "$COMPANION_SETUP" ]; then
    echo "Executing companion setup script..."
    eval "$COMPANION_SETUP" || echo "Warning: Companion setup exited with code $?"
    sync_persona_state
fi

# Start companion background daemon if configured (e.g. token refresh or proxy)
if [ -n "$COMPANION_COMMAND" ]; then
    echo "Starting companion daemon: $COMPANION_COMMAND"
    eval "$COMPANION_COMMAND" &
    COMPANION_PID=$!
    sleep 1
    if ! kill -0 "$COMPANION_PID" 2>/dev/null; then
        echo "Warning: Companion daemon process exited immediately."
    fi
fi

# Await companion daemon readiness port if configured
if [ -n "$COMPANION_PORT" ]; then
    echo "Waiting for companion daemon on port $COMPANION_PORT..."
    PORT_READY=0
    for i in {1..30}; do
        if timeout 1 bash -c "</dev/tcp/127.0.0.1/$COMPANION_PORT" >/dev/null 2>&1 || \
           curl -s "http://127.0.0.1:$COMPANION_PORT/" >/dev/null 2>&1; then
            echo "Companion daemon ready on port $COMPANION_PORT."
            PORT_READY=1
            break
        fi
        sleep 1
    done
    if [ "$PORT_READY" -ne 1 ]; then
        echo "Warning: Companion daemon did not respond on port $COMPANION_PORT within 30s. Proceeding..."
    fi
fi

cleanup() {
    sync_persona_state
    if [ -n "$COMPANION_PID" ] && kill -0 "$COMPANION_PID" 2>/dev/null; then
        kill "$COMPANION_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT

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

exec agentmesh "${ARGS[@]}" "$@"
