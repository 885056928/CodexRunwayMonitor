param(
    [ValidateRange(1, 65535)]
    [int]$Port = 5088
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:Usage__ReportKey) -or [string]::IsNullOrWhiteSpace($env:Usage__AdminKey)) {
    throw '请先通过环境变量设置 Usage__ReportKey 与 Usage__AdminKey；不要将真实密钥写入脚本或命令历史。'
}

$usageProject = Join-Path $PSScriptRoot '../../src/Monitor.UsageServer/Monitor.UsageServer.csproj'
# 只绑定回环；命令行不传递密钥，避免在进程列表中泄漏。
& dotnet run --project $usageProject --no-launch-profile -- --urls "http://127.0.0.1:$Port"
if ($LASTEXITCODE -ne 0) {
    throw "本地服务退出，退出码 $LASTEXITCODE。"
}
