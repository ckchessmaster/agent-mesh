<#
.SYNOPSIS
    Expose or tear down WSL2 network ports to the local network (LAN) using Windows portproxy.

.DESCRIPTION
    Configures 'netsh interface portproxy' and Windows Defender Firewall rules so devices on your
    LAN can access services running inside WSL2. Automatically requests Administrator elevation if needed.

.PARAMETER Action
    'Setup' to forward ports and open firewall rules.
    'Teardown' to delete forwarded ports and remove firewall rules.
    'Status' to view current active portproxy mappings.

.PARAMETER Ports
    Array of ports to forward (default: 3000 for Vite Dashboard, 8085 for Dex OIDC).

.EXAMPLE
    .\scripts\expose-lan.ps1 -Action Setup
    .\scripts\expose-lan.ps1 -Action Teardown
    .\scripts\expose-lan.ps1 -Action Status
#>

[CmdletBinding()]
param(
    [ValidateSet("Setup", "Teardown", "Status")]
    [string]$Action = "Setup",

    [int[]]$Ports = @(3000)
)

# 1. Require Administrator Elevation (Self-elevate with UAC prompt if needed)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[*] Requesting Administrator elevation..." -ForegroundColor Yellow
    $portArgs = ($Ports -join ',')
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Action $Action -Ports $portArgs"
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

function Get-WslIp {
    try {
        $raw = (wsl.exe hostname -I).Trim()
        if ($raw) {
            $ip = ($raw -split '\s+')[0]
            if ($ip -match '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$') {
                return $ip
            }
        }
    } catch {
        Write-Error "Failed to query WSL IP: $_"
    }
    return $null
}

function Get-LanIp {
    $ipObj = Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
        $_.InterfaceAlias -notlike '*Loopback*' -and
        $_.InterfaceAlias -notlike '*vEthernet*' -and
        $_.InterfaceAlias -notlike '*Tailscale*' -and
        $_.IPAddress -notlike '169.254*'
    } | Select-Object -First 1

    if ($ipObj) {
        return $ipObj.IPAddress
    }
    return "localhost"
}

switch ($Action) {
    "Setup" {
        Write-Host "=== Setting up WSL2 LAN Port Forwarding ===" -ForegroundColor Cyan
        $wslIp = Get-WslIp

        if (-not $wslIp) {
            Write-Host "[!] Could not determine WSL2 IP address. Make sure WSL is running." -ForegroundColor Red
            pause
            exit 1
        }

        $lanIp = Get-LanIp
        Write-Host "[+] Detected WSL IP: $wslIp" -ForegroundColor Green
        Write-Host "[+] Detected Windows Host LAN IP: $lanIp" -ForegroundColor Green
        Write-Host ""

        foreach ($port in $Ports) {
            Write-Host "[*] Forwarding 0.0.0.0:$port -> $wslIp`:$port..." -ForegroundColor Yellow
            
            # Remove any existing mapping for this port
            netsh interface portproxy delete v4tov4 listenport=$port listenaddress=0.0.0.0 | Out-Null
            
            # Add new mapping
            netsh interface portproxy add v4tov4 listenport=$port listenaddress=0.0.0.0 connectport=$port connectaddress=$wslIp

            # Add / replace firewall rule
            $ruleName = "AgentMesh Port $port"
            Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -LocalPort $port -Protocol TCP -Action Allow | Out-Null
        }

        Write-Host ""
        Write-Host "=== Active Endpoints ===" -ForegroundColor Cyan
        Write-Host "  - Dashboard UI: http://$lanIp`:3000" -ForegroundColor Green
        if ($Ports -contains 8085) {
            Write-Host "  - Dex OIDC Auth: http://$lanIp`:8085" -ForegroundColor Green
        }
        Write-Host ""
        Write-Host "[+] Setup completed successfully." -ForegroundColor Green
    }

    "Teardown" {
        Write-Host "=== Tearing Down WSL2 LAN Port Forwarding ===" -ForegroundColor Cyan

        foreach ($port in $Ports) {
            Write-Host "[*] Removing portproxy and firewall rule for port $port..." -ForegroundColor Yellow
            netsh interface portproxy delete v4tov4 listenport=$port listenaddress=0.0.0.0 | Out-Null
            Remove-NetFirewallRule -DisplayName "AgentMesh Port $port" -ErrorAction SilentlyContinue | Out-Null
        }

        Write-Host ""
        Write-Host "[+] Teardown completed. Ports are no longer exposed on the LAN." -ForegroundColor Green
    }

    "Status" {
        Write-Host "=== Current Portproxy Configuration ===" -ForegroundColor Cyan
        netsh interface portproxy show v4tov4
        Write-Host ""
        Write-Host "=== Current AgentMesh Firewall Rules ===" -ForegroundColor Cyan
        Get-NetFirewallRule -DisplayName "AgentMesh Port *" -ErrorAction SilentlyContinue | Select-Object DisplayName, Enabled, Direction, Action | Format-Table -AutoSize
    }
}

if (-not $env:AGENTMESH_NON_INTERACTIVE) {
    Write-Host ""
    Write-Host "Press any key to close this window..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
