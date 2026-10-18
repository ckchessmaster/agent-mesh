#!/usr/bin/env bash
set -euo pipefail

API_URL="${API_URL:-http://localhost:5000}"
AUTH_HEADER="Authorization: Bearer mesh-default-secret-key"

echo "==================================================="
echo "  AgentMesh Live CLI Agent Trajectory Streamer"
echo "  Target API: ${API_URL}"
echo "==================================================="

echo "[1/6] Registering / Heartbeating live agent 'agy-cli'..."
curl -s -X POST "${API_URL}/api/agents" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "id": "agy-cli",
    "name": "Agy Live CLI Agent",
    "role": "Autonomous Terminal Engineer",
    "status": "online",
    "capabilitiesJson": "[\"run_command\",\"view_file\",\"replace_file_content\"]"
  }' > /dev/null

echo "[2/6] Creating new session owned by 'agy-cli'..."
SESSION_JSON=$(curl -s -X POST "${API_URL}/api/agents/agy-cli/sessions" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "title": "Live CLI Stream: Verify AgentMesh Architecture",
    "initialPrompt": "Audit the AgentMesh API endpoints and verify real-time SignalR streaming."
  }')

SESSION_ID=$(echo "${SESSION_JSON}" | grep -o '"id":"[^"]*"' | head -n 1 | cut -d'"' -f4)
echo "      Created Session ID: ${SESSION_ID}"
sleep 1

echo "[3/6] Streaming Thought (Chain-of-Thought)..."
curl -s -X POST "${API_URL}/api/sessions/${SESSION_ID}/events" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "senderAgentId": "agy-cli",
    "eventType": "thought",
    "content": "I need to inspect the registered controllers in src/AgentMesh.Api/Controllers to confirm that agent-owned session routes and trajectory event endpoints are active."
  }' > /dev/null
sleep 1.2

echo "[4/6] Streaming Tool Call (run_command)..."
curl -s -X POST "${API_URL}/api/sessions/${SESSION_ID}/events" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "senderAgentId": "agy-cli",
    "eventType": "tool_call",
    "toolName": "run_command",
    "content": "Listing API controllers and verifying build status",
    "toolArgsJson": "{\n  \"command\": \"ls -la src/AgentMesh.Api/Controllers\",\n  \"cwd\": \"/home/ckingdon/projects/agent-mesh\"\n}"
  }' > /dev/null
sleep 1.2

echo "[5/6] Streaming Tool Result..."
curl -s -X POST "${API_URL}/api/sessions/${SESSION_ID}/events" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "senderAgentId": "agy-cli",
    "eventType": "tool_result",
    "toolName": "run_command",
    "content": "Command exited with code 0 (4 controllers found)",
    "toolResultJson": "AgentsController.cs\nSessionsController.cs\nDevController.cs\nApprovalController.cs"
  }' > /dev/null
sleep 1.2

echo "[6/6] Streaming Final Assistant Response..."
curl -s -X POST "${API_URL}/api/sessions/${SESSION_ID}/events" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d '{
    "senderAgentId": "agy-cli",
    "eventType": "response",
    "content": "Verification complete! All trajectory events (Prompt, Thought, Tool Call, Tool Result, and Final Response) streamed in real time to the AgentMesh Session Cockpit."
  }' > /dev/null

echo "==================================================="
echo "  Done! Check http://localhost:3000 to view session."
echo "==================================================="
