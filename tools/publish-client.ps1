param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/usage-client')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$publishParent = [System.IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrEmpty($publishParent) -or $outputPath -eq $projectRoot -or $outputPath -eq [System.IO.Path]::GetPathRoot($outputPath).TrimEnd('\', '/')) {
    throw '发布目标不能是磁盘根目录或项目根目录。'
}

function Assert-ClientStopped {
    $executable = Join-Path $outputPath 'CodexRunwayMonitor.exe'
    foreach ($process in @(Get-Process -Name CodexRunwayMonitor -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrEmpty($process.Path) -or [string]::Equals($process.Path, $executable, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw '客户端仍在运行，或无法确认其路径。请从系统托盘退出客户端后重新发布。'
        }
    }
}

function Remove-PublishTemporaryDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $resolved = (Resolve-Path -LiteralPath $path).ProviderPath
    if ($resolved -notin @($stagePath, $backupPath) -or [System.IO.Path]::GetDirectoryName($resolved) -ne $publishParent) {
        throw '临时目录路径校验失败，已停止清理。'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Assert-OutputHasNoLinks {
    if (-not (Test-Path -LiteralPath $outputPath)) { return }
    $rootItem = Get-Item -LiteralPath $outputPath -Force
    if ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw '发布目录包含目录链接或符号链接，请移走链接后再发布。'
    }
    # Windows PowerShell 的递归清理可能沿链接操作外部目录，因此替换前拒绝链接。
    Get-ChildItem -LiteralPath $outputPath -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw '发布目录包含目录链接或符号链接，请移走链接后再发布。'
        }
    }
}

Assert-ClientStopped
Assert-OutputHasNoLinks
Get-Command dotnet -ErrorAction Stop | Out-Null
[System.IO.Directory]::CreateDirectory($publishParent) | Out-Null
$lockDirectory = Join-Path $projectRoot 'artifacts'
[System.IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
$publishLock = $null
try {
    # 多次双击必须串行，避免同时写入项目共用的 bin/obj。
    $publishLock = [System.IO.File]::Open((Join-Path $lockDirectory '.usage-client-publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
}
catch {
    throw '无法取得发布锁，请等待正在进行的发布结束，并检查 artifacts 目录权限。'
}

$runId = [guid]::NewGuid().ToString('N')
$stagePath = [System.IO.Path]::GetFullPath((Join-Path $publishParent ('.usage-client-publish-' + $runId)))
$backupPath = [System.IO.Path]::GetFullPath((Join-Path $publishParent ('.usage-client-backup-' + $runId)))
foreach ($path in @($stagePath, $backupPath)) {
    if ([System.IO.Path]::GetDirectoryName($path) -ne $publishParent -or $path -eq $outputPath) {
        $publishLock.Dispose()
        throw '发布临时路径校验失败。'
    }
}

$previousMoved = $false
$published = $false
try {
    Write-Host '正在发布最新 Windows x64 自包含客户端，请稍候…'
    & dotnet publish (Join-Path $projectRoot 'src/Monitor.App/Monitor.App.csproj') -c Release -r win-x64 --self-contained true -o $stagePath
    if ($LASTEXITCODE -ne 0) { throw '客户端编译或发布失败，原发布目录未替换。' }
    foreach ($name in @('CodexRunwayMonitor.exe', 'CodexRunwayMonitor.dll', 'coreclr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagePath $name) -PathType Leaf)) {
            throw ('发布结果缺少必要文件：' + $name)
        }
    }

    # 编译结束后再读取现有配置，避免丢失用户在等待期间修改的地址和接入码。
    Assert-OutputHasNoLinks
    if (Test-Path -LiteralPath $outputPath) {
        Get-ChildItem -LiteralPath $outputPath -File -Recurse -Force | ForEach-Object {
            $relative = $_.FullName.Substring($outputPath.Length + 1)
            $target = [System.IO.Path]::GetFullPath((Join-Path $stagePath $relative))
            if (-not $target.StartsWith($stagePath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw '保留文件的目标路径越出临时发布目录。'
            }
            if ($relative -eq 'usage-endpoint.json' -or -not (Test-Path -LiteralPath $target)) {
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
        }
    }

    # 构建期间可能重新启动客户端，替换前必须再次确认。
    Assert-ClientStopped
    if (Test-Path -LiteralPath $outputPath) {
        Move-Item -LiteralPath $outputPath -Destination $backupPath
        $previousMoved = $true
    }
    Move-Item -LiteralPath $stagePath -Destination $outputPath
    $published = $true
    Write-Host ('发布完成：' + $outputPath)
    Write-Host '原有统计配置已保留。现在可以运行“启动监控.cmd”，或复制整个 usage-client 文件夹进行分发。'
}
catch {
    if ($previousMoved -and -not (Test-Path -LiteralPath $outputPath)) {
        try {
            Move-Item -LiteralPath $backupPath -Destination $outputPath
        }
        catch {
            Write-Warning ('原版本保留在 ' + $backupPath + '，自动恢复失败，请保留此目录。')
            throw
        }
    }
    throw
}
finally {
    try {
        Remove-PublishTemporaryDirectory $stagePath
        if ($published) { Remove-PublishTemporaryDirectory $backupPath }
    }
    finally {
        $publishLock.Dispose()
    }
}
