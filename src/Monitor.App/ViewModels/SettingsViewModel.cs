using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Monitor.App;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public SettingsViewModel(AppController controller)
    {
        this.controller = controller;
        // 编辑的是独立草稿；取消或校验失败时不能提前修改正在使用的设置。
        var settings = controller.Settings;
        apiBaseUrl = settings.ApiBaseUrl;
        intervalText = settings.CheckIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        timeoutText = settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        autoCloseText = settings.AutoCloseNotificationSeconds.ToString(CultureInfo.InvariantCulture);
        autoStartMonitoring = settings.AutoStartMonitoring;
        silentStart = settings.SilentStart;
        startWithWindows = settings.StartWithWindows;
        minimizeToTray = settings.MinimizeToTray;
        enableNotification = settings.EnableNotification;
        enableSound = settings.EnableSound;
        firstRunSilent = settings.FirstRunSilent;
        enableLogging = settings.EnableLogging;
        enableUsageReporting = settings.EnableUsageReporting;
        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, HandleSaveError, CanSave);
    }

    #region 属性

    private readonly AppController controller;
    private string apiBaseUrl;
    private string intervalText;
    private string timeoutText;
    private string autoCloseText;
    private bool autoStartMonitoring;
    private bool silentStart;
    private bool startWithWindows;
    private bool minimizeToTray;
    private bool enableNotification;
    private bool enableSound;
    private bool firstRunSilent;
    private bool enableLogging;
    private bool enableUsageReporting;
    private bool isSaving;
    private string errorText = "";

    public string ApiBaseUrl
    {
        get => apiBaseUrl;
        set => SetProperty(ref apiBaseUrl, value);
    }

    public string IntervalText
    {
        get => intervalText;
        set => SetProperty(ref intervalText, value);
    }

    public string TimeoutText
    {
        get => timeoutText;
        set => SetProperty(ref timeoutText, value);
    }

    public string AutoCloseText
    {
        get => autoCloseText;
        set => SetProperty(ref autoCloseText, value);
    }

    public bool AutoStartMonitoring
    {
        get => autoStartMonitoring;
        set => SetProperty(ref autoStartMonitoring, value);
    }

    public bool SilentStart
    {
        get => silentStart;
        set => SetProperty(ref silentStart, value);
    }

    public bool StartWithWindows
    {
        get => startWithWindows;
        set => SetProperty(ref startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => minimizeToTray;
        set => SetProperty(ref minimizeToTray, value);
    }

    public bool EnableNotification
    {
        get => enableNotification;
        set => SetProperty(ref enableNotification, value);
    }

    public bool EnableSound
    {
        get => enableSound;
        set => SetProperty(ref enableSound, value);
    }

    public bool FirstRunSilent
    {
        get => firstRunSilent;
        set => SetProperty(ref firstRunSilent, value);
    }

    public bool EnableLogging
    {
        get => enableLogging;
        set => SetProperty(ref enableLogging, value);
    }

    public bool IsSaving
    {
        get => isSaving;
        private set
        {
            SetProperty(ref isSaving, value);
            OnPropertyChanged(nameof(CanClose));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool EnableUsageReporting
    {
        get => enableUsageReporting;
        set => SetProperty(ref enableUsageReporting, value);
    }

    public bool CanClose => !IsSaving;

    public string ErrorText
    {
        get => errorText;
        private set => SetProperty(ref errorText, value);
    }

    public string IntervalLabel => $"检查间隔（分钟，{AppSettings.MinimumCheckIntervalMinutes}–{AppSettings.MaximumCheckIntervalMinutes}）";
    public string TimeoutLabel => $"请求超时（秒，{AppSettings.MinimumRequestTimeoutSeconds}–{AppSettings.MaximumRequestTimeoutSeconds}）";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? FocusRequested;
    public event Action? Saved;

    #endregion

    #region 命令

    public AsyncRelayCommand SaveCommand { get; }

    #endregion

    #region 方法

    private bool CanSave() => !IsSaving && !controller.Exiting;

    private async Task ExecuteSaveAsync()
    {
        var settings = BuildSettings();
        IsSaving = true;
        ErrorText = "正在保存…";
        try
        {
            await controller.SaveSettingsAsync(settings);
        }
        finally
        {
            // 模态窗口只允许在实际保存结束后关闭，避免退出打断文件和注册表更新。
            IsSaving = false;
        }
        Saved?.Invoke();
    }

    private AppSettings BuildSettings()
    {
        var settings = controller.Settings with
        {
            CheckIntervalMinutes = ParseNumber(IntervalText, nameof(IntervalText), "检查间隔", AppSettings.MinimumCheckIntervalMinutes, AppSettings.MaximumCheckIntervalMinutes),
            RequestTimeoutSeconds = ParseNumber(TimeoutText, nameof(TimeoutText), "请求超时", AppSettings.MinimumRequestTimeoutSeconds, AppSettings.MaximumRequestTimeoutSeconds),
            AutoCloseNotificationSeconds = ParseNumber(AutoCloseText, nameof(AutoCloseText), "自动关闭", AppSettings.MinimumAutoCloseNotificationSeconds, AppSettings.MaximumAutoCloseNotificationSeconds),
            ApiBaseUrl = ApiBaseUrl.Trim().TrimEnd('/'),
            AutoStartMonitoring = AutoStartMonitoring,
            SilentStart = SilentStart,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            EnableNotification = EnableNotification,
            EnableSound = EnableSound,
            FirstRunSilent = FirstRunSilent,
            EnableLogging = EnableLogging,
            EnableUsageReporting = EnableUsageReporting
        };
        try
        {
            settings.Validate();
        }
        catch (ArgumentException)
        {
            FocusRequested?.Invoke(nameof(ApiBaseUrl));
            throw;
        }
        return settings;
    }

    private int ParseNumber(string text, string propertyName, string label, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
        {
            FocusRequested?.Invoke(propertyName);
            throw new ArgumentException($"{label}请输入 {minimum}–{maximum} 之间的整数。");
        }
        return number;
    }

    private void HandleSaveError(Exception error)
    {
        ErrorText = error is ArgumentException ? error.Message : "保存失败，请检查文件或注册表权限。输入已保留。";
        controller.Log.WriteError("设置保存失败", error);
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    #endregion
}
