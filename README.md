# CodexRunway Monitor

独立运行的 Windows 10 / 11 桌面工具：直接用 C# HttpClient 请求 CodexRunway 开放 API，默认每 5 分钟检查一次；发现新的重置完成记录后在主屏中央显示置顶提醒。

**不需要 Codex CLI、Codex Desktop、CC-Switch、AI 模型或本机 15721 端口。** 程序监控网站公开记录，不读取个人账户余额，也不会替用户执行额度重置。

## 直接运行

源码仓库：[885056928/CodexRunwayMonitor](https://github.com/885056928/CodexRunwayMonitor)。仓库保留源码、图标、测试和部署模板，不包含发布产物、运行日志或真实统计密钥。从仓库首次下载后，需要在 Windows 安装 .NET 10 SDK，再在项目根目录执行 `.\tools\publish-usage.ps1` 生成可运行程序。

1. 打开 `artifacts/usage-client` 文件夹。
2. 双击 `CodexRunwayMonitor.exe`，或运行项目根目录的 `启动监控.cmd`。
3. 启动后立即检查。首次有效事件默认只建立基线，因此不弹窗属于正常现象。
4. 关闭或最小化主窗口后，程序默认继续在系统托盘运行。双击托盘图标恢复窗口；右键选择“退出程序”才能退出。

发布目录是 Windows x64 自包含版本，**无需另装 .NET**。复制时请复制整个 `artifacts/usage-client` 文件夹，不要只复制 exe。默认不开机启动，在“打开设置”中开启后生效；开机启动路径指向当前 exe，移动文件夹后应重新关闭、开启该设置。

可用参数：`CodexRunwayMonitor.exe --tray`，启动时只显示托盘。重复启动不会创建第二个监控实例，而会恢复现有主窗口。

## 界面和操作

程序使用专属“循环箭头 + 铃铛”图标，应用于 exe、窗口和监控中的托盘状态；暂停和错误仍显示不同的状态图标。图标源图为 `src/Monitor.App/Resources/AppIcon.png`，Windows 多尺寸版本为同目录 `AppIcon.ico`，生成提示词保存在 `AppIcon.prompt.txt`。

- **立即检查**：发起一次异步检查；正在请求时不会叠加请求，冷却期内会显示可检查时间。
- **开始监控 / 暂停监控**：恢复时立即检查；暂停取消当前请求和定时任务，仍可手动检查。
- **打开设置**：保存后立即更新间隔和行为；正在监控时会重启当前调度并检查一次。
- **查看日志**：打开本地日志目录。
- **退出程序**：取消任务，关闭提醒和窗口，释放托盘、连接并刷新日志。

托盘图标区分监控、暂停、错误，并显示上次尝试、成功检查和最新重置时间。错误时保留最后有效事件。

提醒默认显示 10 秒后自动关闭。自动关闭不代表已确认：从关闭时起等待 15 分钟再次弹窗，此后每次未确认关闭都会重新等待 15 分钟，直到点击“知道了”（也可通过按钮的键盘操作确认）。标题栏关闭或 Esc 关闭同样不会确认。再次提醒与新事件共用窗口队列，不叠加弹窗，也不额外请求 API。

关闭通知会取消当前会话的待提醒记录，重新开启不会补发；退出程序会取消计时。待确认状态仅保存在当前进程内，重启后不恢复。本次仍沿用现有自动关闭设置，默认 10 秒；设为 0 时窗口等待用户关闭。

## 配置与状态

数据目录：`%LOCALAPPDATA%\CodexRunwayMonitor`。

| 文件 | 用途 |
|---|---|
| `settings.json` | 用户设置 |
| `events.json` | 已处理 ID 集合、最新有效事件、时间高水位 |
| `requests.json` | 最近一小时请求记录、服务端限流冷却时间 |
| `*.bak` | 上一次原子替换时保留的备份 |
| `*.corrupt-*` | 校验失败后保留的故障文件 |
| `Logs/yyyy-MM-dd/app.log` | 当天运行日志 |
| `Logs/yyyy-MM-dd/error.log` | 当天错误日志 |

推荐通过设置窗口修改。手动修改 JSON 前先退出程序；运行时不监视文件外部修改。

```json
{
  "ApiBaseUrl": "https://www.codexrunway.com/openapi/v1",
  "CheckIntervalMinutes": 5,
  "RequestTimeoutSeconds": 30,
  "AutoStartMonitoring": true,
  "SilentStart": false,
  "StartWithWindows": false,
  "MinimizeToTray": true,
  "EnableNotification": true,
  "EnableSound": true,
  "EnableLogging": true,
  "EnableUsageReporting": true,
  "AutoCloseNotificationSeconds": 10,
  "FirstRunSilent": true
}
```

| 设置 | 范围 / 行为 |
|---|---|
| ApiBaseUrl | HTTPS 地址，不允许 URL 用户名密码、查询参数或片段 |
| CheckIntervalMinutes | 3–1440 分钟，默认 5 |
| RequestTimeoutSeconds | 5–120 秒，默认 30 |
| AutoStartMonitoring | 每次程序启动是否开始监控 |
| SilentStart | 静默启动：不显示主窗口并自动开始后台监控；优先于 AutoStartMonitoring，默认 false |
| StartWithWindows | 当前用户登录启动，使用 HKCU Run，无需管理员权限 |
| MinimizeToTray | 默认 true；false 时关闭主窗口即退出 |
| EnableNotification | 关闭时仍更新已处理状态，重新打开不补发历史 |
| EnableSound | 默认播放 Windows 系统提示音 |
| EnableLogging | 关闭后不再追加日志 |
| EnableUsageReporting | 默认 true；明确告知后统计在线客户端与公网 IP，可在设置中关闭；未配置统计服务器不发送 |
| AutoCloseNotificationSeconds | 0–3600 秒，0 为手动关闭 |
| FirstRunSilent | 无本地事件状态时，首次记录是否静默 |

配置损坏时先尝试备份，再回退默认值并显示恢复提示。事件状态损坏且无可用备份时，默认重新静默建立基线。保存失败不弹窗、不推进有效事件状态，界面显示错误并等待下次检查。

## API 与事件判定

文档：https://www.codexrunway.com/zh/api.html

请求：`GET https://www.codexrunway.com/openapi/v1/records/latest?kind=reset_completed`。

无需密钥。成功结构为 `ok / data / meta`；事件唯一标识是字符串 `data.id`；类型是 `data.kind`；实际时间为可空的 `data.effectiveAt`。真实成功和错误 JSON 见 [API 验证记录](docs/api-verification.md)。

**实际最新记录的 effectiveAt 为 null。** 程序显示“实际重置时间未知”，另列公告时间 `announcedAt`，绝不将公告时间、页面时间或 meta 时间冒充实际重置时间。原始公开事件数据保存在 events.json 中。

同一出口 IP 的数据接口和 MCP 查询共享任意连续一小时最多 20 次的额度。默认 5 分钟为约 12 次/小时；本程序对手动、自动请求统一记录本地额度，429 时遵守 Retry-After/retryAfter，重启后继续冷却。其他客户端也可能消耗共享额度。网络失败、超时、HTTP 502/503 等不立即重试，等待下次轮询，避免高频请求。

去重规则：

1. 默认首次只保存基线。
2. 保存全部已处理 ID，重复记录或同 ID 内容修订不会生成新的事件提醒；未确认弹窗仍按 15 分钟规则再次提醒。
3. 未见 ID 但已知实际时间早于已保存高水位，视为历史记录并抑制提醒。
4. 未见 ID 且实际时间未知，只生成一次新的事件提醒；无法保证它不是站点补录的旧记录。未确认时仍按上述规则再次提醒。
5. 先原子保存处理状态，再异步向 UI 派发通知。暂停、网络异常、无匹配记录不会清除基线。

## 构建和测试

开发需要 Windows 和 .NET 10 SDK。应用与测试均不引入第三方 NuGet 包。

```powershell
dotnet build CodexRunwayMonitor.slnx -c Release
dotnet run --project tests/Monitor.Tests -c Release
dotnet run --project tests/Monitor.UiSmoke -c Release
```

测试项目是无需测试框架依赖的可执行单元/集成测试程序，失败返回非零退出码；**请运行上述 dotnet run 命令，dotnet test 不会执行这些用例。** WPF 烟雾测试会短暂显示测试窗口，使用临时数据和假 HTTP 响应，不污染真实事件状态或开机启动设置。

额外真实接口检查（会消耗一次共享 API 额度）：

```powershell
dotnet run --project tests/Monitor.Tests -c Release -- --live
```

修改源码后，**双击根目录 `发布最新版客户端.cmd`**，即可重新编译并发布 Windows x64 自包含客户端到 `artifacts/usage-client`。发布需要本机安装 .NET 10 SDK；重新生成 Debug/Release 不会自动更新此目录。

脚本会保留发布目录中现有的 `usage-endpoint.json`（服务器地址和上报接入码）及其他额外文件；首次发布使用源码中的空统计配置。运行中的目标客户端会阻止发布，并提示先从系统托盘退出。编译先在临时目录完成，再替换原目录；编译失败不影响旧版本，替换失败尝试恢复原目录。重复双击会通过发布锁避免同时构建，窗口会停留以便查看成功或错误信息。

命令行执行同一发布流程：

```powershell
.\tools\publish-client.ps1
```

发布脚本回归测试（仅使用临时文件和模拟编译器，不启动真实客户端或访问服务器）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/Test-PublishClient.ps1
```

源码：`src/Monitor.Core` 为 API、配置、状态、事件检测、调度和日志；`src/Monitor.App` 为 WPF、托盘和系统集成；`src/Monitor.UsageServer` 为使用统计 API；`src/Monitor.UsageAdmin` 为独立 Windows 管理端；`tests` 为可执行测试。测试证据和未完成的现场验收见 [测试报告](docs/test-results.md)。

## 使用统计与独立管理端（2026-10-21）

普通客户端主窗口持续显示统计状态，设置中的“允许发送使用统计（默认开启）”可关闭并保存。开启且发布配置有效时，每约 60 秒发送本地随机生成并持久化的安装 ID 和软件版本；服务器从连接获取公网 IP，不采集账号、计算机名或硬件信息。未配置统计服务器时不发送、不创建统计 ID；上报故障独立处理，不消耗原重置查询的请求额度，也不影响提醒。关闭并保存会取消并等待正在执行的上报；服务器在最后心跳满 180 秒后将其移出在线列表。

服务器只在内存保留当前在线状态，定期清除过期记录；重启后随心跳恢复。管理端显示在线客户端数、不同公网 IP 数，以及每个安装的 IP、版本和最后心跳。共享出口可能多人共用 IP，同一人也可能运行多个安装，因此这两个数都不能直接当作真实人数。

本地发布不会自动部署到真实服务器。准备好 HTTPS 后，按 [本地联调与 Ubuntu 部署说明](deploy/usage/README.md) 操作。服务仅监听回环端口，经同机 Nginx HTTPS 对外提供 API；上报接入码与管理密钥必须不同。管理密钥不能打包到普通客户端；独立管理端成功连接后会在当前 Windows 用户目录中加密保存连接信息。

发布者配置普通客户端目录中的 `usage-endpoint.json`，默认两项均为空：

```json
{
  "ServerUrl": "https://usage.example.com",
  "ReportKey": "CHANGE_ME_TO_A_RANDOM_REPORT_KEY_AT_LEAST_32_CHARACTERS"
}
```

以上是占位示例，实际部署必须换为服务器的真实 HTTPS 地址与随机上报接入码。真实配置仅在发布副本中填写，不提交源码。公网 HTTP 会被拒绝；本地联调允许 `http://127.0.0.1:5088`。修改发布配置后重启普通客户端。管理端输入同一地址和独立管理密钥，连接后每 10 秒刷新，每页 50 条；刷新失败保留最后快照并明确标为过期。

生成三个本地程序及部署模板：

```powershell
.\tools\publish-usage.ps1
```

该完整发布脚本复用上述客户端发布流程，同样保留已有的统计配置；日常仅更新普通客户端时直接双击 `发布最新版客户端.cmd`。

- `artifacts/usage-client/CodexRunwayMonitor.exe`：含统计开关的普通客户端，Windows x64 自包含。
- `artifacts/usage-admin/CodexUsageAdmin.exe`：独立统计管理程序，Windows x64 自包含。
- `artifacts/usage-server/Monitor.UsageServer.dll`：跨平台服务文件，Ubuntu 需 .NET 10 ASP.NET Core 运行时，使用 `dotnet Monitor.UsageServer.dll` 运行。
- `artifacts/usage-deployment`：systemd、Nginx、环境变量模板及联调说明。

分发程序须保留各自完整目录。已有 `artifacts/win-x64` 是此前发布副本，继续保留；此次新版本位于 `usage-client`。开发环境可直接运行 `dotnet run --project src/Monitor.UsageAdmin -c Release`。

根目录 `启动监控.cmd` 已指向此次新客户端；`启动统计管理.cmd` 打开独立管理端。管理端首次显示未连接，填写部署好的地址和管理密钥后才会查询统计。

管理端成功获取首次有效快照后，自动将服务器地址和管理密钥成对保存到 `%LOCALAPPDATA%\CodexUsageAdmin\connection.json`，使用 Windows DPAPI 当前用户加密，不保存明文密钥。下次打开会自动填入地址和掩码密钥，点击“连接”即可，不会自动连接。连接失败或取消不会覆盖上次成功的信息；断开和退出仍清空当前会话密钥，加密文件继续保留。更新发布目录不影响该配置；将程序复制到另一台电脑或换 Windows 用户时需重新输入。读取或保存失败会单独提示，不影响手动连接及有效快照。需要移除已保存信息时，退出管理端后删除该文件及同目录的 `connection.json.bak` 和 `connection.json.corrupt-*`（若存在）。

新增本地验证命令（使用合成数据，不访问公网 API）：

```powershell
dotnet run --project tests/Monitor.UsageServer.Tests -c Release
dotnet run --project tests/Monitor.UsageAdmin.UiSmoke -c Release -- artifacts/usage-admin-ui-smoke
dotnet run --project tests/Monitor.UiSmoke -c Release -- artifacts/usage-client-ui --extended
```

## 代码规范与诊断改进（2026-10-21）

- 主窗口和托盘共用 `MainViewModel` 命令，异步操作保留可等待的 Task，并统一处理入口异常和重复执行。
- 设置草稿、校验、保存状态和错误提示由 `SettingsViewModel` 管理；窗口仅负责定位、输入焦点和模态关闭。保存失败保留输入与原设置，允许修正后重试。
- 设置表单和配置文件校验共用数值边界，配置键、默认值、监控间隔及事件处理顺序保持兼容。
- 错误日志补充异常原因、内部异常和堆栈；启动资源创建及激活已有实例的失败会记录诊断并显示提示。仍遵守用户的日志开关。
- 构造函数、ViewModel 区域和异常处理块按全局规范整理，并补充关键业务及时序的中文注释。

## 已知边界

- latest 接口每次只给一条记录，两次检查之间出现多条时可能跳过中间事件；这是本需求指定接口的能力边界。
- 持久化和窗口显示不能组成一个事务。进程如果恰好在保存后、弹窗前被强制结束，可能漏一次提醒；设计优先避免重启后重复提醒。
- 人为删除状态、磁盘损坏或恢复较旧备份会失去部分去重信息，无法保证和正常状态一样完整。
- Windows 锁屏或安全桌面期间，普通 WPF 窗口无法覆盖安全桌面；自动关闭计时仍可能到期。
- 日志按天分目录，不自动删除；可在程序退出后按需清理旧日志。原重置查询接口不需要密钥，禁止将凭据写入 API 地址。统计发布配置仅保存上报接入码，管理密钥必须独立保管，日志不记录统计密钥。
- 真实双屏（均 100% 缩放）已通过扩展验收；混合 DPI、实体机器睡眠/唤醒和持续多日运行仍需现场结果。72 小时资源采样已启动，进度与操作见 [后续现场验收](docs/field-acceptance.md)。
