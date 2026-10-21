# Observable UI contract

Authority: user requirements and docs/superpowers/specs/2026-09-15-monitor-design.md; API details in docs/api-verification.md.

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Form | SettingsViewModel + AppSettings.Validate; SettingsWindow owns focus and modal closure | Approved spec | modal, explicit save/cancel | WPF smoke + core invalid-settings tests |
| Scrollbar | App.xaml + Windows WPF ScrollViewer | DESIGN.md | native Windows vertical scrollbar | WPF layout smoke |
| Toast | NotificationService + NotificationWindow | User reminder requirement + approved spec | topmost nonmodal, automatic dismissal, explicit acknowledgement, 15-minute reminder | WPF smoke + virtual-clock reminder tests |
| Date | Display.Time | API verification | local time + UTC offset, explicit unknown | WPF smoke |

Entry → settings → edit → save → inline validation or persisted success → close and return focus. Cancel discards edits explicitly. Main status and tray always use MonitoringService snapshots; start/pause/check share AppController actions. API failure preserves latest valid event. Loading does not resize buttons. The independent usage administrator owns its read-only table and separate authenticated connection; no account or destructive data controls are in scope.

MainWindow and TrayService share the same MainViewModel commands. AsyncRelayCommand prevents repeated execution and reports failures at its ICommand entry point. SettingsViewModel owns the editable draft, save state and inline errors; input parsing and persisted settings validation share the numeric boundaries in AppSettings. A failed save retains both the entered draft and the previous active settings; successful save closes the modal only after the save operation finishes. Window positioning, field focus and modal closure remain view behaviors.

SilentStart appears directly above Windows login startup. When enabled, the next application start hides the main window and always starts monitoring. Tray restore still opens the main window. Default false preserves existing settings; --tray remains an explicit force-hide override. Saving the preference does not hide the current settings or main window.

An event notification defaults to automatic dismissal after 10 seconds. Only activating “知道了” acknowledges it; timeout, title-bar close and Escape keep it pending. Each unacknowledged close starts a new 15-minute delay, then the same event enters the existing notification queue and repeats until acknowledged. This flow does not issue API requests or change persistent event deduplication. Disabling notifications drops pending, queued and scheduled reminders without replay on re-enable. Disposal cancels every reminder and closes the active window. Pending acknowledgement is session-only and is not restored after restart. AutoCloseNotificationSeconds remains configurable, including 0 for manual closure.

Usage reporting defaults enabled and is explicitly disclosed in the client main window and settings. Saving opt-out cancels and awaits the active heartbeat; unsaved drafts have no effect. An empty deployment endpoint means no network or usage identity IO. Heartbeats are independent of original monitor pause and its API quota. No username or hardware identifier is collected. The server retains only online clients (last heartbeat less than 180 seconds old), not usage history.

UsageAdmin: enter HTTPS endpoint and masked admin key → connect → immediate page 1 request → refresh every 10 seconds or explicit refresh, previous/next page. Only one request is active. Endpoint and key are locked while connected. Input validation preserves the entered values; invalid public HTTP or non-ASCII/blank keys never start a connection. Network/authorization/JSON errors retain previous data with a stale label and actionable fixed text, without exposing server response bodies. Disconnect and close cancel and await active tasks, clear the key in both ViewModel and PasswordBox, and mark retained data stale. Authenticated tables and totals never claim unique human counts. Window smoke tests in tests/Monitor.UsageAdmin.UiSmoke cover loading, empty, error, pagination, timeout/cancellation and key cleanup.
