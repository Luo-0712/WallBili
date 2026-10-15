param(
    [ValidateRange(1, 1440)][int]$Minutes = 120,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 10,
    [string]$OutputPath = 'wallbili-resources.csv'
)
$ErrorActionPreference = 'Stop'
# Read only. No process, wallpaper setting, or window hierarchy is modified.
$deadline = [DateTimeOffset]::Now.AddMinutes($Minutes)
$first = $true
while ([DateTimeOffset]::Now -lt $deadline) {
    $processes = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(WallBili\.Desktop|mpv|wallpaper32|wallpaper64|webwallpaper32|webwallpaper64|ui32|ui64)\.exe$'
    }
    $rows = foreach ($process in $processes) {
        # Do not record command lines: unrelated media programs can have URLs or credentials in argv.
        [PSCustomObject]@{
            Time = [DateTimeOffset]::Now.ToString('o')
            Process = $process.Name
            Pid = $process.ProcessId
            ParentPid = $process.ParentProcessId
            CpuSeconds = ([double]$process.KernelModeTime + [double]$process.UserModeTime) / 10000000
            WorkingSetBytes = $process.WorkingSetSize
            PrivateBytes = $process.PrivatePageCount
            Handles = $process.HandleCount
            Threads = $process.ThreadCount
        }
    }
    if ($rows) {
        if ($first) { $rows | Export-Csv -NoTypeInformation -Encoding utf8 $OutputPath; $first = $false }
        else { $rows | Export-Csv -NoTypeInformation -Encoding utf8 -Append $OutputPath }
    }
    Start-Sleep -Seconds $IntervalSeconds
}
Write-Host "Saved process samples to $OutputPath. Record GPU load separately in Task Manager for the same fixed video settings."
