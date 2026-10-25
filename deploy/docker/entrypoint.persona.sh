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
