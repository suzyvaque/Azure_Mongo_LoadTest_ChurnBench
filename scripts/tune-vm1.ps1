#requires -RunAsAdministrator
<#
.SYNOPSIS
    Tune the load-generator host (VM1) for the connection-churn benchmark (test_instruction.md section 7.3).

.DESCRIPTION
    The benchmark opens a brand-new connection per Task and closes it (no pooling). Every closed
    client socket sits in TIME_WAIT for TcpTimedWaitDelay seconds, holding an ephemeral port. The
    sustainable connection-churn rate is therefore:

        churn capacity (conn/s) = (ephemeral port count) / TcpTimedWaitDelay

    Windows defaults (ephemeral 49152..65535 = 16,384 ports, TIME_WAIT 120 s) give only ~137 conn/s,
    far below the Scenario B burst target of >= 1,200 conn/s. This script widens the ephemeral port
    range and lowers TIME_WAIT so the host can sustain the churn target without port exhaustion.

    Defaults below (10000..65534 = 55,535 ports, TIME_WAIT 30 s) yield ~1,851 conn/s.

    The ephemeral port-range change is effective immediately. The TcpTimedWaitDelay / MaxUserPort
    registry values apply to new connections; a reboot guarantees they are fully in effect.

.PARAMETER StartPort
    First ephemeral port. Default 10000. Keep above well-known service ports you host on this VM.

.PARAMETER NumPorts
    Number of ephemeral ports. Default 55535 (i.e. 10000..65534).

.PARAMETER TimeWaitSeconds
    TcpTimedWaitDelay in seconds. Default 30 (the minimum Windows honors).

.PARAMETER Revert
    Restore Windows defaults (ephemeral 49152..65535, remove TcpTimedWaitDelay/MaxUserPort overrides).

.EXAMPLE
    # Apply benchmark tuning:
    powershell -ExecutionPolicy Bypass -File scripts\tune-vm1.ps1

.EXAMPLE
    # Restore defaults after the benchmark:
    powershell -ExecutionPolicy Bypass -File scripts\tune-vm1.ps1 -Revert
#>
[CmdletBinding()]
param(
    [int]$StartPort = 10000,
    [int]$NumPorts = 55535,
    [int]$TimeWaitSeconds = 30,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$tcpParams = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'

function Show-State {
    Write-Host '--- Current TCP host settings ---'
    netsh int ipv4 show dynamicport tcp
    $p = Get-ItemProperty -Path $tcpParams
    $twd = if ($null -ne $p.TcpTimedWaitDelay) { $p.TcpTimedWaitDelay } else { '120 (Windows default, key absent)' }
    $mup = if ($null -ne $p.MaxUserPort) { $p.MaxUserPort } else { '(Windows default, key absent)' }
    Write-Host "TcpTimedWaitDelay : $twd"
    Write-Host "MaxUserPort       : $mup"
}

if ($Revert) {
    Write-Host 'Reverting VM1 to Windows default TCP settings...'
    netsh int ipv4 set dynamicport tcp start=49152 num=16384 | Out-Null
    Remove-ItemProperty -Path $tcpParams -Name 'TcpTimedWaitDelay' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $tcpParams -Name 'MaxUserPort' -ErrorAction SilentlyContinue
    Write-Host 'Reverted. A reboot is recommended so TIME_WAIT returns to the 120 s default.'
    Show-State
    return
}

$endPort = $StartPort + $NumPorts - 1
$capacity = [math]::Floor($NumPorts / $TimeWaitSeconds)
Write-Host "Applying VM1 connection-churn tuning (section 7.3):"
Write-Host "  ephemeral ports : $StartPort..$endPort  (count $NumPorts)"
Write-Host "  TcpTimedWaitDelay: $TimeWaitSeconds s"
Write-Host "  => sustainable churn capacity ~$capacity conn/s"

netsh int ipv4 set dynamicport tcp start=$StartPort num=$NumPorts | Out-Null
New-ItemProperty -Path $tcpParams -Name 'TcpTimedWaitDelay' -PropertyType DWord -Value $TimeWaitSeconds -Force | Out-Null
New-ItemProperty -Path $tcpParams -Name 'MaxUserPort' -PropertyType DWord -Value $endPort -Force | Out-Null

Write-Host 'Applied. Ephemeral range is live now; TIME_WAIT applies to new connections (reboot to be certain).'
Show-State
