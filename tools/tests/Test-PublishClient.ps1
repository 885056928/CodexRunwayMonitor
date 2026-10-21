$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot '../publish-client.ps1'
if (-not (Test-Path -LiteralPath $publisher)) {
    throw 'FAIL: client-only publisher is missing.'
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('MonitorPublishTests-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$global:publishTest = @{}

# 仅替代编译器和进程查询；配置保留、目录替换与回滚都使用真实文件系统。
function global:dotnet {
    $global:publishTest.Calls++
    $outputIndex = [array]::IndexOf($args, '-o')
    if ($outputIndex -lt 0) { throw 'Publisher did not specify an output directory.' }
    $output = $args[$outputIndex + 1]
    [System.IO.Directory]::CreateDirectory($output) | Out-Null
    foreach ($name in @('CodexRunwayMonitor.exe', 'CodexRunwayMonitor.dll', 'coreclr.dll')) {
        [System.IO.File]::WriteAllText((Join-Path $output $name), 'new-build')
    }
    [System.IO.File]::WriteAllText((Join-Path $output 'usage-endpoint.json'), '{"ServerUrl":"","ReportKey":""}')
    if ($global:publishTest.EditConfiguration) {
        [System.IO.File]::WriteAllText((Join-Path $global:publishTest.Output 'usage-endpoint.json'), $global:publishTest.EditConfiguration)
    }
    if ($global:publishTest.StartDuringBuild) { $global:publishTest.Running = $true }
    $global:LASTEXITCODE = 0
    if ($global:publishTest.BuildFails) { $global:LASTEXITCODE = 1 }
}

function global:Get-Process {
    param($Name, $ErrorAction)
    if ($global:publishTest.Running) {
        [pscustomobject]@{ Path = (Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe') }
    }
}

function global:Move-Item {
    param($LiteralPath, $Destination)
    if ($global:publishTest.SwapFails -and (Split-Path $LiteralPath -Leaf).StartsWith('.usage-client-publish-')) {
        throw 'Simulated directory replacement failure.'
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}

function New-TestCase([string]$name) {
    $global:publishTest = @{ Calls = 0; Output = (Join-Path $testRoot $name) }
    [System.IO.Directory]::CreateDirectory($global:publishTest.Output) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe'), 'old-build')
    [System.IO.File]::WriteAllText((Join-Path $global:publishTest.Output 'usage-endpoint.json'), '{"ServerUrl":"https://example.test","ReportKey":"test-report-key-must-be-preserved"}')
}

function Check([bool]$value, [string]$message) {
    if (-not $value) { throw ('FAIL: ' + $message) }
    Write-Output ('PASS: ' + $message)
}

function Expect-Failure {
    $failed = $false
    try {
        & $publisher -OutputDirectory $global:publishTest.Output
    }
    catch {
        $failed = $true
    }
    Check $failed 'failed operation returns an error'
}

try {
    New-TestCase 'config preserved with spaces'
    $config = [System.IO.File]::ReadAllBytes((Join-Path $global:publishTest.Output 'usage-endpoint.json'))
    [System.IO.File]::WriteAllText((Join-Path $global:publishTest.Output 'user-note.txt'), 'keep user file')
    $hiddenNote = Join-Path $global:publishTest.Output 'hidden-user-note.txt'
    [System.IO.File]::WriteAllText($hiddenNote, 'keep hidden user file')
    [System.IO.File]::SetAttributes($hiddenNote, [System.IO.FileAttributes]::Hidden)
    & $publisher -OutputDirectory $global:publishTest.Output
    Check (([System.IO.File]::ReadAllText((Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe'))) -eq 'new-build') 'new client replaces the previous build'
    Check ([Convert]::ToBase64String($config) -eq [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $global:publishTest.Output 'usage-endpoint.json')))) 'endpoint configuration is preserved byte for byte'
    Check (Test-Path -LiteralPath (Join-Path $global:publishTest.Output 'user-note.txt')) 'unrelated files in the output directory are retained'
    Check (Test-Path -LiteralPath $hiddenNote) 'hidden user files are retained'

    New-TestCase 'build failure'
    $global:publishTest.BuildFails = $true
    Expect-Failure
    Check (([System.IO.File]::ReadAllText((Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe'))) -eq 'old-build') 'build failure leaves the previous version usable'

    New-TestCase 'running client'
    $global:publishTest.Running = $true
    Expect-Failure
    Check ($global:publishTest.Calls -eq 0) 'running target client is rejected before compilation'

    New-TestCase 'client starts during build'
    $global:publishTest.StartDuringBuild = $true
    Expect-Failure
    Check (([System.IO.File]::ReadAllText((Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe'))) -eq 'old-build') 'client started during compilation prevents replacement'

    New-TestCase 'concurrent publication'
    $lockPath = Join-Path $PSScriptRoot '../../artifacts/.usage-client-publish.lock'
    [System.IO.Directory]::CreateDirectory((Split-Path $lockPath -Parent)) | Out-Null
    $heldLock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Expect-Failure
        Check ($global:publishTest.Calls -eq 0) 'concurrent publication is rejected before compilation'
    }
    finally {
        $heldLock.Dispose()
    }

    New-TestCase 'replacement failure'
    $global:publishTest.SwapFails = $true
    Expect-Failure
    Check (([System.IO.File]::ReadAllText((Join-Path $global:publishTest.Output 'CodexRunwayMonitor.exe'))) -eq 'old-build') 'replacement failure rolls the previous directory back'

    New-TestCase 'linked directory'
    $external = Join-Path $testRoot 'linked target'
    [System.IO.Directory]::CreateDirectory($external) | Out-Null
    $externalNote = Join-Path $external 'keep.txt'
    [System.IO.File]::WriteAllText($externalNote, 'keep linked target')
    $junction = Join-Path $global:publishTest.Output 'external-link'
    New-Item -ItemType Junction -Path $junction -Value $external | Out-Null
    try {
        Expect-Failure
        Check ($global:publishTest.Calls -eq 0 -and (Test-Path -LiteralPath $externalNote)) 'linked output directories are rejected without touching their targets'
    }
    finally {
        if (Test-Path -LiteralPath $junction) { [System.IO.Directory]::Delete($junction) }
    }

    New-TestCase 'configuration edited during build'
    $global:publishTest.EditConfiguration = '{"ServerUrl":"https://changed.example.test","ReportKey":"new-test-value"}'
    & $publisher -OutputDirectory $global:publishTest.Output
    Check (([System.IO.File]::ReadAllText((Join-Path $global:publishTest.Output 'usage-endpoint.json'))) -eq $global:publishTest.EditConfiguration) 'configuration edited during compilation is retained'

    $global:publishTest = @{ Calls = 0; Output = (Join-Path $testRoot 'first publish') }
    & $publisher -OutputDirectory $global:publishTest.Output
    $firstConfig = Get-Content -LiteralPath (Join-Path $global:publishTest.Output 'usage-endpoint.json') -Raw | ConvertFrom-Json
    Check ($firstConfig.ServerUrl -eq '' -and $firstConfig.ReportKey -eq '') 'first publish uses the empty source configuration'
    Check (@(Get-ChildItem -LiteralPath $testRoot -Directory -Filter '.usage-client-*').Count -eq 0) 'temporary publish directories are cleaned up'
}
finally {
    $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).ProviderPath
    $expectedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot -ne $expectedTestRoot -or -not (Split-Path $resolvedTestRoot -Leaf).StartsWith('MonitorPublishTests-')) {
        throw 'Refusing to clean an unexpected test path.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
