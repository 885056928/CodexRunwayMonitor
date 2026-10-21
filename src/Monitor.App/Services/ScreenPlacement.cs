using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Monitor.App;

public static class ScreenPlacement
{
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static void Clamp(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
        window.Width = Math.Min(window.Width, area.Width);
    }
    public static void CenterPrimary(Window window)
    {
        Clamp(window);
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        var area = screen.WorkingArea;
        var handle = new WindowInteropHelper(window).Handle;
        // 先移动到主屏，让 PerMonitorV2 更新窗口 DPI，再用目标屏缩放计算居中位置。
        SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, 0, 0, NoSize | NoZOrder | NoActivate);
        window.UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var width = Math.Min(area.Width, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Min(area.Height, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height, NoZOrder | NoActivate);
    }
}
