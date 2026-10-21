param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputDirectory = [System.IO.Path]::GetFullPath($OutputRoot)
$clientOutput = Join-Path $outputDirectory 'usage-client'
$adminOutput = Join-Path $outputDirectory 'usage-admin'
$serverOutput = Join-Path $outputDirectory 'usage-server'

# 客户端复用单独发布入口，保留已有上报配置；首次发布使用源码中的空配置。
& (Join-Path $PSScriptRoot 'publish-client.ps1') -OutputDirectory $clientOutput
& dotnet publish (Join-Path $projectRoot 'src/Monitor.UsageAdmin/Monitor.UsageAdmin.csproj') -c Release -r win-x64 --self-contained true -o $adminOutput
if ($LASTEXITCODE -ne 0) { throw '统计管理端发布失败。' }
# 无apphost的框架依赖包可由Ubuntu的.NET 10 ASP.NET Core运行时直接运行。
& dotnet publish (Join-Path $projectRoot 'src/Monitor.UsageServer/Monitor.UsageServer.csproj') -c Release --self-contained false -p:UseAppHost=false -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw '统计服务发布失败。' }

$deploymentOutput = Join-Path $outputDirectory 'usage-deployment'
[System.IO.Directory]::CreateDirectory($deploymentOutput) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'deploy/usage') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $deploymentOutput -Recurse -Force
}
Write-Host "本地发布完成：$clientOutput；$adminOutput；$serverOutput。部署模板位于 usage-deployment；尚未部署到服务器。"
