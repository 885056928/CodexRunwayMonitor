---
version: alpha
colors:
  background: "#F2F5F8"
  surface: "#FFFFFF"
  ink: "#183047"
  muted: "#526579"
  primary: "#146B83"
  border: "#D5DFE7"
  danger: "#AD3030"
typography:
  body:
    fontFamily: "Microsoft YaHei UI, Segoe UI, sans-serif"
  data:
    fontFamily: "Consolas, monospace"
rounded:
  panel: "8px"
spacing:
  unit: "8px"
---

# CodexRunway Monitor

## Overview

Chinese Windows utility for people who want to see whether monitoring is healthy at a glance. Product register: a quiet instrument panel. The signature is a narrow teal status rail next to a plain-language monitoring state. No marketing hero, decorative graphs, remote fonts, animations or fabricated metrics.

## Colors

Resources/Theme.xaml is the runtime token owner; App.xaml merges it. Keys BackgroundBrush, SurfaceBrush, InkBrush, MutedBrush, AccentBrush (primary), BorderBrush and DangerBrush map to the colors above. Status always includes words, never color alone. High contrast uses Windows system brushes.

## Typography

Body 14 DIP, section 18 DIP, title 26 DIP. Microsoft YaHei UI supports Chinese text; event ID uses Consolas. All times display local UTC offset, stored times remain UTC. Null actual reset time is explicitly unknown.

## Layout

Main window 820 × 710 DIP, minimum 640 × 520. Scrollable content, persistent action row; grouped labels and values. Settings and notifications reuse spacing and typography. Window dimensions must be clamped to the screen work area at high DPI.

## Elevation & Depth

White panels on cool gray, thin borders; native window chrome and focus cues. No decorative shadows.

## Shapes

Panels use 8 DIP corners, native controls retain recognizable Windows behavior.

## Components

Button/TextBox/CheckBox and ScrollViewer use shared App.xaml resources plus native keyboard, focus, scrollbar and disabled behavior. Settings are a modal owned window, notification is a topmost nonmodal window. Busy checks disable duplicate submission without moving controls.

The independent usage administrator shares Theme.xaml and the icon through linked source resources. It uses a 1100 × 760 DIP window, minimum 900 × 600, a masked key field, persistent connection controls, online client/IP totals and a virtualized read-only table. After the first valid authenticated snapshot, endpoint and key are encrypted together using Windows current-user DPAPI in LocalApplicationData/CodexUsageAdmin; reopening restores both inputs without connecting. Local persistence feedback is separate from network/snapshot status. Disconnect and exit clear the session key while retaining the encrypted saved connection. No chart implies a historical time series: only current online state exists. Loading, empty, authorization failure, stale snapshot, disconnected and paging states keep stable geometry. All date values show local time and UTC offset. Original client status includes a persistent usage disclosure and a settings opt-out; no credentials appear in that UI.

The application icon uses a navy rounded tile, a bright teal circular reset arrow and an ivory bell. It identifies reset monitoring and reminders without text. Resources/AppIcon.png is the generated master; Resources/AppIcon.ico contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel frames. The executable, WPF Window style and running tray state consume that ICO. Paused and error tray states retain their distinct Windows status icons. AppIcon.prompt.txt records the built-in image generation prompt.

## Do's and Don'ts

- Keep API error feedback visible while showing the last valid event.
- Use the same action labels in the main window and tray.
- Preserve form input on validation errors; show actionable error text.
- Do not imply this monitor resets an account or verifies a personal balance.
