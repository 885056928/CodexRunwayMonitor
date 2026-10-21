[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(0.0001, 168)][double]$DurationHours = 72,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 60,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'CodexRunwayMonitor')
)

$ErrorActionPreference = 'Stop'
$expectedPath = [IO.Path]::GetFullPath($ExecutablePath)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$csvPath = Join-Path $outputPath 'samples.csv'
$summaryPath = Join-Path $outputPath 'summary.json'
$lockPath = Join-Path $outputPath 'sampler.lock'
$lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
if ((Test-Path -LiteralPath $csvPath) -or (Test-Path -LiteralPath $summaryPath)) {
    $lock.Dispose()
    throw 'Use a new output directory for each run; existing samples and summary will not be overwritten.'
}
$started = [DateTimeOffset]::Now
$deadline = $started.AddHours($DurationHours)
$count = 0
$peakMemory = 0.0
$firstMemory = $null
$lastMemory = $null
$previousCpu = $null
$previousTime = $null
$firstPid = $null
$maxCpuPercent = 0.0
$result = 'running'
$reason = $null
$lastSuccess = $null

function Write-Summary {
    $summary = [ordered]@{
        Status = $result; Reason = $reason; StartedAt = $started.ToString('O')
        UpdatedAt = [DateTimeOffset]::Now.ToString('O'); PlannedEndAt = $deadline.ToString('O')
        ExecutablePath = $expectedPath; ProcessId = $firstPid; SampleCount = $count
        FirstWorkingSetMB = $firstMemory; LastWorkingSetMB = $lastMemory; PeakWorkingSetMB = $peakMemory
        PeakCpuPercentOneCore = $maxCpuPercent; LastSuccessfulCheckAt = $lastSuccess
        Note = 'Read-only sampler. Sleep suspends sampling; elapsed wall time alone does not prove continuous awake operation. CPU percentage uses one logical core as 100%.'
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath ($summaryPath + '.tmp') -Encoding utf8
    Move-Item -LiteralPath ($summaryPath + '.tmp') -Destination $summaryPath -Force
}

try {
    while ($true) {
        $now = [DateTimeOffset]::Now
        $process = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($expectedPath)) -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $expectedPath })
        if ($process.Count -ne 1) { $result = 'failed'; $reason = "Expected one application process, found $($process.Count)."; break }
        $current = $process[0]
        if ($null -eq $firstPid) { $firstPid = $current.Id }
        if ($current.Id -ne $firstPid) { $result = 'failed'; $reason = 'Application restarted during observation.'; break }
        $memory = [math]::Round($current.WorkingSet64 / 1MB, 2)
        $cpu = $current.TotalProcessorTime.TotalSeconds
        $cpuPercent = 0.0
        $sampleGap = 0.0
        if ($null -ne $previousTime) {
            $sampleGap = ($now - $previousTime).TotalSeconds
            if ($sampleGap -gt 0) { $cpuPercent = [math]::Round(100 * ($cpu - $previousCpu) / $sampleGap, 4) }
        }
        if ($null -eq $firstMemory) { $firstMemory = $memory }
        $lastMemory = $memory; $peakMemory = [math]::Max($peakMemory, $memory)
        $maxCpuPercent = [math]::Max($maxCpuPercent, $cpuPercent)
        $recentLogs = @(Get-ChildItem -LiteralPath (Join-Path $DataDirectory 'Logs') -Filter app.log -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2)
        $successes = @($recentLogs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 500 -Encoding utf8 } | Where-Object { $_ -match 'HTTP 200' } | Sort-Object)
        if ($successes.Count -gt 0) { $lastSuccess = ($successes[-1] -split ' ')[0] }
        [pscustomobject][ordered]@{
            Time = $now.ToString('O'); ProcessId = $current.Id; ProcessStartedAt = $current.StartTime.ToString('O')
            WorkingSetMB = $memory; PrivateMemoryMB = [math]::Round($current.PrivateMemorySize64 / 1MB, 2)
            CpuSeconds = $cpu; CpuPercentOneCore = $cpuPercent; HandleCount = $current.HandleCount
            ThreadCount = $current.Threads.Count; SampleGapSeconds = [math]::Round($sampleGap, 2)
            GapExceededTwiceInterval = ($sampleGap -gt 2 * $IntervalSeconds); LastSuccessfulCheckAt = $lastSuccess
        } | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Append -Encoding utf8
        $count++; $previousCpu = $cpu; $previousTime = $now
        if ($now -ge $deadline) { $result = 'completed'; break }
        Write-Summary
        Start-Sleep -Seconds ([math]::Min($IntervalSeconds, [math]::Max(1, [math]::Ceiling(($deadline - $now).TotalSeconds))))
    }
}
catch { $result = 'failed'; $reason = $_.Exception.Message }
finally { Write-Summary; $lock.Dispose() }
if ($result -eq 'failed') { Write-Error $reason; exit 1 }
