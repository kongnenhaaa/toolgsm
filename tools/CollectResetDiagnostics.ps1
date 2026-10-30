param(
    [Parameter(Mandatory = $true)]
    [datetime] $IncidentTime,

    [ValidateRange(1, 1440)]
    [int] $WindowMinutes = 30,

    [string] $ToolExePath,

    [string] $OutputPath = (Join-Path $PWD 'ToolGSM-reset-diagnostics.json')
)

$ErrorActionPreference = 'Stop'
$start = $IncidentTime.AddMinutes(-$WindowMinutes)
$end = $IncidentTime.AddMinutes($WindowMinutes)

function Read-Events([string] $logName) {
    try {
        @(Get-WinEvent -FilterHashtable @{
            LogName = $logName
            StartTime = $start
            EndTime = $end
        } -ErrorAction Stop | Where-Object {
            $_.Id -in 7, 11, 17, 18, 19, 41, 46, 51, 55, 129, 153,
                157, 161, 219, 1000, 1001, 1074, 6005, 6006, 6008,
                10110, 10111 -or
            $_.ProviderName -match 'WHEA|BugCheck|Kernel-Power|Kernel-PnP|USB|DriverFramework|Display|volmgr|disk|storport'
        } | ForEach-Object {
            $values = @{}
            if ($_.Id -eq 41) {
                try {
                    $xml = [xml]$_.ToXml()
                    foreach ($entry in $xml.Event.EventData.Data) {
                        if ($entry.Name -in 'BugcheckCode', 'PowerButtonTimestamp',
                                'BugcheckParameter1', 'BugcheckParameter2',
                                'BugcheckParameter3', 'BugcheckParameter4') {
                            $values[$entry.Name] = $entry.'#text'
                        }
                    }
                } catch { }
            }
            [pscustomobject]@{
                TimeCreated = $_.TimeCreated
                ProviderName = $_.ProviderName
                Id = $_.Id
                Level = $_.LevelDisplayName
                BugcheckCode = $values['BugcheckCode']
                BugcheckParameters = @(
                    $values['BugcheckParameter1'],
                    $values['BugcheckParameter2'],
                    $values['BugcheckParameter3'],
                    $values['BugcheckParameter4'])
                PowerButtonTimestamp = $values['PowerButtonTimestamp']
                Message = $_.Message
            }
        } | Select-Object -First 500)
    }
    catch {
        @([pscustomobject]@{ Error = $_.Exception.Message })
    }
}

$drivers = try {
    @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop |
        Where-Object { $_.DeviceClass -in 'PORTS', 'USB' } |
        Select-Object DeviceName, DeviceClass, Manufacturer, DriverProviderName,
            DriverVersion, DriverDate, InfName)
}
catch {
    @([pscustomobject]@{ Error = $_.Exception.Message })
}

$minidumps = try {
    @(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Minidump')
        -Filter '*.dmp' -File -ErrorAction Stop |
        Where-Object { $_.LastWriteTime -ge $start -and $_.LastWriteTime -le $end } |
        Select-Object Name, Length, LastWriteTime)
}
catch { @() }

$memoryDump = try {
    Get-Item -LiteralPath (Join-Path $env:WINDIR 'MEMORY.DMP') -ErrorAction Stop |
        Select-Object FullName, Length, LastWriteTime
} catch { $null }

$recoverySettings = try {
    Get-CimInstance Win32_OSRecoveryConfiguration -ErrorAction Stop |
        Select-Object AutoReboot, DebugInfoType, DebugFilePath,
            MiniDumpDirectory, WriteToSystemLog
} catch { [pscustomobject]@{ Error = $_.Exception.Message } }

$pageFileSettings = try {
    Get-CimInstance Win32_ComputerSystem -ErrorAction Stop |
        Select-Object AutomaticManagedPagefile
} catch { [pscustomobject]@{ Error = $_.Exception.Message } }

$tool = $null
if ($ToolExePath) {
    try {
        $item = Get-Item -LiteralPath $ToolExePath -ErrorAction Stop
        $tool = [pscustomobject]@{
            Version = $item.VersionInfo.FileVersion
            Modified = $item.LastWriteTime
            Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    catch { $tool = [pscustomobject]@{ Error = $_.Exception.Message } }
}

$os = try {
    Get-CimInstance Win32_OperatingSystem -ErrorAction Stop |
        Select-Object Caption, Version, BuildNumber, LastBootUpTime
} catch { [pscustomobject]@{ Error = $_.Exception.Message } }

$report = [pscustomobject]@{
    IncidentTime = $IncidentTime
    WindowStart = $start
    WindowEnd = $end
    Os = $os
    Tool = $tool
    SystemEvents = Read-Events 'System'
    ApplicationEvents = Read-Events 'Application'
    UsbAndComDrivers = $drivers
    MinidumpFiles = $minidumps
    MemoryDumpFile = $memoryDump
    RecoverySettings = $recoverySettings
    PageFileSettings = $pageFileSettings
}

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
Write-Output $resolvedOutput
