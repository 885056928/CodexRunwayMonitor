using System.ComponentModel;

namespace Monitor.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppController controller)
    {
        InitializeComponent();
        viewModel = new SettingsViewModel(controller);
        DataContext = viewModel;
        viewModel.FocusRequested += FocusField;
        viewModel.Saved += OnSaved;
        Closing += OnClosing;
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    private readonly SettingsViewModel viewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScreenPlacement.Clamp(this);
        Interval.Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.IsSaving) e.Cancel = true;
    }

    private void OnSaved() => DialogResult = true;

    private void FocusField(string propertyName)
    {
        var field = propertyName switch
        {
            nameof(SettingsViewModel.IntervalText) => Interval,
            nameof(SettingsViewModel.TimeoutText) => TimeoutSeconds,
            nameof(SettingsViewModel.AutoCloseText) => AutoClose,
            _ => ApiUrl
        };
        field.Focus();
        field.SelectAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        viewModel.FocusRequested -= FocusField;
        viewModel.Saved -= OnSaved;
    }
}
