#!/usr/bin/env bash
# Helper script to invoke expose-lan.ps1 from inside WSL with elevation.

set -e

ACTION="${1:-Setup}"
case "${ACTION,,}" in
  setup|start)
    ACTION_ARG="Setup"
    ;;
  teardown|stop|down)
    ACTION_ARG="Teardown"
    ;;
  status)
    ACTION_ARG="Status"
    ;;
  *)
    echo "Usage: $0 [setup|teardown|status]"
    exit 1
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PS_SCRIPT="$SCRIPT_DIR/expose-lan.ps1"

# Copy to Windows Temp directory to avoid any UNC / network share permission issues in elevated UAC sessions
WIN_USER="$(ls -1 /mnt/c/Users | grep -v -E 'Public|Default|All Users|desktop.ini|WsiAccount' | head -n 1)"
WIN_TEMP="/mnt/c/Users/$WIN_USER/AppData/Local/Temp"

if [ -d "$WIN_TEMP" ]; then
  cp "$PS_SCRIPT" "$WIN_TEMP/agentmesh-expose-lan.ps1"
  TARGET_WIN_PATH="C:\\Users\\$WIN_USER\\AppData\\Local\\Temp\\agentmesh-expose-lan.ps1"
else
  TARGET_WIN_PATH="$(wslpath -w "$PS_SCRIPT")"
fi

POWERSHELL_EXE="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"

if [ -f "$POWERSHELL_EXE" ]; then
  echo "Invoking Windows PowerShell with Administrator prompt..."
  "$POWERSHELL_EXE" -NoProfile -NonInteractive -Command \
    "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"$TARGET_WIN_PATH\" -Action $ACTION_ARG'" < /dev/null
  echo "Elevation prompt triggered. Please accept the Windows UAC prompt on your screen to complete $ACTION_ARG."
else
  echo "PowerShell not found at $POWERSHELL_EXE."
  echo "Please run this script directly from Windows PowerShell (Run as Administrator):"
  echo "  powershell.exe -ExecutionPolicy Bypass -File \"$TARGET_WIN_PATH\" -Action $ACTION_ARG"
fi
