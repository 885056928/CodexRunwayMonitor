# API 实测记录

验证日期：2026-09-15。当前工作目录原为空，本机已安装 .NET SDK 10.0.401、WindowsDesktop Runtime 10.0.12，Windows 10 x64。

## 来源

- 文档：https://www.codexrunway.com/zh/api.html
- 基础地址：https://www.codexrunway.com/openapi/v1
- 最新完成事件：GET /records/latest?kind=reset_completed
- .NET LTS：https://dotnet.microsoft.com/en-us/platform/support/policy

## 真实成功响应

本机 curl 请求，HTTP 200，2026-09-15 09:39:44 UTC。无需鉴权或特殊请求头。

```json
{"ok":true,"data":{"id":"2098685367058612394","kind":"reset_completed","resetType":"global","announcedAt":"2026-09-12T08:09:17.000Z","effectiveAt":null,"text":"Reset all propagated. Sweet dreams.","confidence":0.96,"scope":{"plans":["all"],"windows":["unknown"]},"source":{"origin":"x","postId":"2098685367058612394","handle":"thsottiaux","url":"https://x.com/thsottiaux/status/2098685367058612394"},"schedulePrecision":null,"scheduleBasis":null,"scheduleWindow":null,"scheduleState":null,"completedAt":null,"completionRecordId":null,"fulfillmentOrigin":null,"relatedRecordIds":["2098612714704891959"]},"meta":{"generatedAt":"2026-09-15T09:36:02.321Z","lastSuccessfulCheckAt":"2026-09-15T09:36:02.321Z"}}
```

`id` 是字符串唯一标识，不能转换为整数；包括数字、op_ 和 manual: 格式。`kind` 才是事件类型，`resetType` 表示重置范围。`effectiveAt` 是实际重置时间，可合法为 null；`announcedAt` 仅为公告时间；`completedAt` 主要表示排期兑现确认时间。不能使用 meta 的更新时间作为事件标识。

## 真实错误响应

请求 GET /records/latest?kind=invalid，HTTP 422：

```json
{"ok":false,"error":"invalid_query","detail":"kind"}
```

## 文档确认但没有主动触发的错误

- 429 rate_limited：包含 retryAfter，响应头 Retry-After 指定至少等待秒数。
- 503 rate_limit_unavailable / data_unavailable。
- 500 internal_error；404 / 405 为 JSON 错误。
- 代理错误（例如 502）可能不返回 JSON，应先按 HTTP 状态处理。

滚动 3600 秒最多 20 次，同一出口 IP 与 MCP 查询共享额度。错误参数请求也计数。成功响应实测含 X-RateLimit-Limit、Remaining、Window、Reset。未进行耗尽额度实验。默认每 5 分钟为 12 次/小时，手动检查需要同样限流，不能无限自动重试。

`data:null` 表示当前无匹配记录，不能覆盖最后有效事件。字段缺失与显式 null 要区分；非法时间应作为数据错误处理。
