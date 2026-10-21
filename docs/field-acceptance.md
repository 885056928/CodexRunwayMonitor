# 后续现场验收

## 2026-09-15 本机补充结果

本机 Windows 10 专业版 10.0.19045，两个实际显示器均为 2560×1440、100% 缩放。

运行扩展测试：

```powershell
dotnet run --project tests/Monitor.UiSmoke -c Release -- --extended --startup-registry
```

35 项窗口检查全部通过，覆盖原 15 项和新增内容：

- 将主窗口分别移到主屏和副屏，实际创建提醒并测量屏幕坐标；副屏触发的提醒仍位于主屏工作区中心。
- 验证提醒“知道了”按钮的中文 UI Automation 名称、键盘焦点和 Invoke 关闭行为。
- 检查设置各输入框的辅助功能名称，以及检查间隔到请求超时的 Tab 顺序。
- 测试 HKCU Run 写入、带空格路径引号和 --tray 参数、关闭自启动后的移除、恢复原注册表值。备份保留值是否存在、未展开的原始内容及注册表值类型。原值为 null，测试结束后仍为 null。

`--startup-registry` 会短暂修改本程序的 Run 值，并在 finally 中还原；执行前将原值写入 `artifacts/ui-smoke/startup-registry-backup.json`。该参数用于主动验收，普通烟雾测试不修改真实注册表。它验证注册操作，不等于完成注销后重新登录的现场测试。

证据：`artifacts/ui-extended-tests.txt`、`artifacts/ui-smoke/monitor-topology.json`、`artifacts/runtime-observation.json`。

## 真实持续运行和资源观测

发布版进程于 2026-09-15 18:15 启动，补充验收时已连续运行约 112 分钟。该时刻记录到 23 次请求、22 次成功、0 条错误，最新一轮请求尚在进行；相邻请求起始时间相差约 305.7–307.6 秒（每次请求完成后等待 5 分钟）。累计 CPU 时间约 2.19 秒，工作集约 91 MB。单点值不能证明长期内存趋势。

独立只读采样器已启动，计划采样 72 小时，每 60 秒一次。当前运行位置记录在 `artifacts/stability-current.json`，对应目录内：

- `samples.csv`：每次的内存、私有内存、CPU、线程、句柄、采样间隔和最近成功时间。
- `summary.json`：当前运行状态与最终汇总，`running` 表示仍在采样。
- `sampler.lock`：防止同一目录出现两个采样器。

采样器由 PowerShell 独立运行，不依赖 Codex 或模型，不发起网络请求、不修改监控配置。自身是额外的临时诊断进程，其资源占用不计入监控程序。

手动开启新的独立采样：

```powershell
./tools/Measure-Stability.ps1 -ExecutablePath "$PWD/artifacts/win-x64/CodexRunwayMonitor.exe" -OutputDirectory "$PWD/artifacts/stability/manual-run" -DurationHours 72 -IntervalSeconds 60
```

每次使用新的输出目录。应用退出、重启或实例数量异常会产生 failed 汇总；正常到时为 completed。completed 只代表采样任务按时结束，应结合样本、日志、间隔缺口和内存趋势评价应用是否通过稳定性验收。系统睡眠会暂停采样，墙钟过去 72 小时不等于持续清醒运行 72 小时。

采样器自身已完成短时间成功路径、进程不存在失败路径和重复目录保护回归测试。原有 samples.csv 或 summary.json 均不会被覆盖。

## 需要可中断桌面的测试窗口

以下操作会中断正在使用的桌面、改变用户显示设置，或需要额外设备。未在无明确时机时强制执行：

1. **真实睡眠/唤醒**：本机 powercfg /a 确认支持 S3 待机；休眠未启用。先记下最近成功时间，手动睡眠后唤醒，检查只恢复一次轮询、没有集中补发请求，且后续继续成功。建议与连续清醒采样分开记录。
2. **注销登录自启动**：通过应用设置开启登录启动，再注销/重新登录；确认程序处于托盘且只有一个实例，之后恢复原自启动偏好。注销会结束当前采样器，应重新开启一次采样并分别保留记录。
3. **混合 DPI / 改变主屏**：将一个屏幕调整为 150% 或 200% 后重新测试，验证提醒主屏位置、内容完整、设置按钮可达；完成后恢复原缩放和主屏设置。目前两屏均为 100%，不能把双屏通过写成混合 DPI 通过。
4. **Windows 11**：将整个发布文件夹复制到可用 Windows 11 x64 机器，执行同样验收。当前主机不是 Windows 11。
5. **完整屏幕阅读器验收**：目前通过 UI Automation 名称与 Invoke/焦点测试；还需使用 Narrator 或用户指定阅读器验证实际朗读和导航。

