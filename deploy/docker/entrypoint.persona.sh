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

# Ensure persistent workspace state directories exist
mkdir -p /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip

# Symlink ~/.config to workspace state so any tool using standard XDG paths persists state
if [ ! -L /root/.config ]; then
    mkdir -p /root/.config
    cp -rn /root/.config/* /workspace/.agentmesh/state/.config/ 2>/dev/null || true
    rm -rf /root/.config
    ln -s /workspace/.agentmesh/state/.config /root/.config
fi

# Symlink ~/.aip to workspace state so aip credentials survive container restarts
if [ ! -L /root/.aip ]; then
    mkdir -p /root/.aip
    cp -rn /root/.aip/* /workspace/.agentmesh/state/.aip/ 2>/dev/null || true
    rm -rf /root/.aip
    ln -s /workspace/.agentmesh/state/.aip /root/.aip
fi

# Run companion setup script if configured (e.g. install aip helper)
if [ -n "$COMPANION_SETUP" ]; then
    echo "Executing companion setup script..."
    eval "$COMPANION_SETUP" || echo "Warning: Companion setup exited with code $?"
fi

# Start companion background daemon if configured (e.g. aip proxy)
if [ -n "$COMPANION_COMMAND" ]; then
    echo "Starting companion daemon: $COMPANION_COMMAND"
    eval "$COMPANION_COMMAND" &
    COMPANION_PID=$!
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
