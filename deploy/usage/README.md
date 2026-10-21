# 使用情况服务部署与本地联调

此目录只提供文件和操作说明，不自动创建服务器、申请证书、修改防火墙或部署到公网。服务需要 .NET 10 ASP.NET Core 运行时，源码构建需要 .NET 10 SDK；没有额外 NuGet 依赖。

## 数据与边界

启用上报的客户端约每60秒发送随机安装ID、版本。服务从连接来源（或可信本机代理覆盖的来源头）读取IP，不使用请求体IP。最近一次心跳不足180秒算在线；管理界面展示在线客户端数、不同IP数，不能等同实际人数。共享公网IP可对应多个客户端，随机ID也不构成可信设备认证，统计不能用于计费、授权或实名人数判断。

在线记录和每IP限流计数仅在内存中，定期清除过期状态，无数据库或使用历史。重启后等待后续心跳恢复；关闭客户端上报后，最多经过在线窗口消失。服务不记录请求体、Authorization 或来源IP，并关闭可能包含来源IP或任意URL的ASP.NET Core请求诊断日志。Nginx模板在HTTP和HTTPS两处关闭访问日志，并禁用其可能包含来源IP的错误日志；若已有外围代理、云监控或journald采集策略，部署者需同时检查，避免另行保存来源IP。关闭这些诊断日志会减少故障定位信息，按本机日志策略审查后使用；不要通过更细分类的日志配置重新开启包含请求地址的框架日志。

每个来源IP默认每分钟600次（含未授权请求、管理列表），通过 `Usage__RequestsPerMinute` 配置正整数。健康检查不计入限流。在线记录上限10000，满载时已有安装仍能更新，新安装返回429；限流IP计数同样最多10000条。限流响应附 `Retry-After: 60`，容量拒绝也返回429；客户端不得密集重试。共享出口的客户端需要结合规模合理设置限额。

## Windows本地启动

1. 准备两份不同的32–128位非空白ASCII随机密钥，上报接入码给普通客户端，管理密钥只交给管理程序。禁止 `CHANGE_ME` 占位符、非ASCII、空白和控制字符。真实密钥不要写入仓库、脚本、命令行参数或终端历史。
2. 通过环境变量或当前进程内的安全输入设置 `Usage__ReportKey` 与 `Usage__AdminKey`。可在PowerShell中使用以下隐藏输入，输入值不会回显；退出PowerShell后环境变量随进程释放。

   ```powershell
   $reportSecret = Read-Host '上报接入码' -AsSecureString
   $adminSecret = Read-Host '管理密钥' -AsSecureString
   $env:Usage__ReportKey = [System.Net.NetworkCredential]::new('', $reportSecret).Password
   $env:Usage__AdminKey = [System.Net.NetworkCredential]::new('', $adminSecret).Password
   .\deploy\usage\start-local.ps1
   # 停止服务后清除当前进程的环境变量。
   Remove-Item Env:Usage__ReportKey, Env:Usage__AdminKey
   $reportSecret.Dispose()
   $adminSecret.Dispose()
   ```

3. 本地地址为 `http://127.0.0.1:5088`，`GET /health` 仅返回 `{"status":"ok"}`。脚本可用 `-Port` 修改端口。未配置两种密钥，服务拒绝启动。服务只允许绑定回环，不能通过配置对公网开放HTTP。
4. 普通客户端的 `usage-endpoint.json` 使用本机服务地址和上报接入码；管理程序使用同一服务地址和独立管理密钥。首次联调应使用合成数据和一次性测试密钥。

仅执行自动化本机合成测试时运行：

```powershell
dotnet run --project tests/Monitor.UsageServer.Tests/Monitor.UsageServer.Tests.csproj
```

测试会启动临时回环端口和使用代码中的合成密钥，不联网，不作为生产密钥来源。正常本地启动不会自动执行该测试。

## Ubuntu + systemd + Nginx

1. 在构建机发布不含平台apphost的跨平台服务（也可使用项目根目录的 `tools/publish-usage.ps1` 一次生成三个程序及部署模板）：

   ```text
   dotnet publish src/Monitor.UsageServer/Monitor.UsageServer.csproj -c Release --self-contained false -p:UseAppHost=false -o artifacts/usage-server
   ```

2. 在服务器安装支持.NET 10的ASP.NET Core运行时和Nginx。创建无交互登录、无家目录的系统用户 `monitor-usage`；将发布目录放到 `/opt/monitor-usage`，文件由root拥有，服务账号仅需读取权限。模板假定dotnet路径 `/usr/bin/dotnet`，安装路径不同时调整 `ExecStart`。
3. 建立 `/etc/monitor-usage`，仅root可写。将 `usage.env.example` 复制为 `/etc/monitor-usage/usage.env`，替换两个占位符，权限设为 `root:root 0600`。可分别运行 `openssl rand -hex 32` 在受控环境生成密钥（每次64字符），不要粘贴到聊天、版本库或公开日志。systemd会为低权限服务读取该文件。
4. 将 `monitor-usage.service` 复制到 `/etc/systemd/system/`；使用 `systemctl daemon-reload`、`systemctl enable --now monitor-usage` 启用。先在服务器执行 `curl --fail http://127.0.0.1:5088/health` 验证健康；本地健康检查不含统计信息。根据需要查看systemd日志，但不得临时打开请求体或授权头日志。
5. 准备已解析的域名和合法TLS证书；将 `nginx.conf.example` 中域名与证书路径替换后加入站点配置，执行 `nginx -t` 验证再重载Nginx。证书申请、续期以及公网80/443入站配置按服务器既有运维流程处理。
6. Kestrel保持 `127.0.0.1:5088`，公网只接入Nginx HTTPS，禁止直接暴露5088。此模板只有一层本机反代。Nginx必须用 `$remote_addr` 覆盖 `X-Forwarded-For`，不能追加外部传入的头。服务仅信任IPv4/IPv6回环代理，最多读取一层；若前面再加CDN或负载均衡，应重新审查整条信任链，不能直接扩大可信代理列表。
7. 用合成心跳确认204，使用上报密钥请求管理接口应401，使用管理密钥心跳也应401。管理列表需要独立管理密钥；密钥只通过 `Authorization: Bearer ...` 发送，不能放进URL。确认Nginx不缓存管理响应，不保留访问IP日志。

## API

- `POST /api/usage/heartbeat`：上报Bearer密钥、`application/json`，`installationId` 非空GUID，`version` 是1–64个ASCII字母、数字、`.`、`+`、`-`、`_`。成功204，非法输入400，类型不匹配415，超过4KiB为413，超额429。
- `GET /api/usage/clients?page=1&pageSize=50`：管理Bearer密钥，页码正整数、每页1–100。默认1/50，超范围或重复分页参数400。返回 `serverTime`、`onlineClients`、`onlineIps`、`page`、`pageSize`、`clients`；条目按安装ID排序，包含 `installationId`、`ipAddress`、`version`、`lastSeen`。跨页数据会随心跳实时变化，超出页返回空数组。
- `GET /health`：无需密钥，只有健康状态，不公开使用统计。

服务端和代理以4KiB限制请求体；超限HTTP/1响应会显式关闭当前连接，避免服务端排空超限body失败后客户端继续复用连接。密钥字符校验复用Core，与普通客户端和管理端一致；身份验证采用固定长度摘要的常量时间比较。上报密钥泄漏时应轮换并同步客户端端点文件；管理密钥不得打包给普通客户端。变更环境文件后重启systemd服务，内存状态随之清空。

反向代理信任范围与 systemd 配置参考 Microsoft 官方文档：[Kestrel 安全注意事项](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/security-considerations)、[使用 Nginx 托管 ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?tabs=linux-ubuntu&view=aspnetcore-9.0)。实际安装路径、域名和证书需按服务器环境核对，本次尚未在 Ubuntu 上部署验证。
