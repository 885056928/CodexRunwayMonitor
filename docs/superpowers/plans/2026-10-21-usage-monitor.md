# 使用统计实施计划

目标：按已确认设计完成本地可运行的客户端上报、服务器 API、独立管理程序与部署文件。
设计：../specs/2026-10-21-usage-monitor-design.md。
约束：沿用 .NET 10，不新增 NuGet 包；不连接真实服务器；不改变原有重置检测、通知与额度；中文注释，WPF MVVM 区域和显式命令；保留既有修改。

- [x] 共享 DTO 和 HTTP 客户端、上报生命周期；测试默认开启、空端点、取消和心跳。
- [x] 服务端及独立可执行测试：密钥、来源 IP、在线窗口、分页、容量与限流；提供 Ubuntu 与本地部署文件。
- [x] 管理端及独立窗口测试：连接、刷新、分页、错误、断开清空密钥；复用主题与命令。
- [x] 原客户端设置和显式告知；初始化、设置变更、退出时接入上报服务。
- [x] 串行构建与全套本地测试、跨组件 HTTP 联调和独立审查，修复本次问题。
- [x] 更新 README、界面契约、测试报告，生成本地交付构建。目录无 Git，无法提交或推送。

共享接口源：src/Monitor.Core/UsageModels.cs；共享客户端将为 UsageAdminClient(HttpClient)，提供 GetSnapshotAsync(Uri endpoint,string adminKey,int page,int pageSize,CancellationToken ct=default)。地址验证为 UsageEndpoint.ValidateServerUrl(string)，返回 Uri，仅 HTTPS 或回环 HTTP。
