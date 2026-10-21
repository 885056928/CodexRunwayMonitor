using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Monitor.UsageAdmin;

public partial class MainWindow : Window
{
    public MainWindow(AdminViewModel viewModel)
    {
        this.viewModel = viewModel;
        Resources.Add("LocalTimeConverter", new LocalTimeConverter());
        InitializeComponent();
        AdminPassword.Password = viewModel.AdminKey;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private readonly AdminViewModel viewModel;
    private bool closing;
    private bool allowClose;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var password = ((PasswordBox)sender).Password;
        if (viewModel.AdminKey != password) viewModel.AdminKey = password;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (AdminPassword.Password != viewModel.AdminKey)
        {
            AdminPassword.Password = viewModel.AdminKey;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        IsEnabled = false;
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception error)
        {
            viewModel.ReportOperationError(error);
        }
        finally
        {
            // 即使没有在途请求，仍等当前 Closing 事件返回后再关闭，避免 WPF 关闭重入。
            _ = Dispatcher.BeginInvoke(CompleteClose);
        }
    }

    private void CompleteClose()
    {
        allowClose = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        AdminPassword.Clear();
    }
}

internal sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTimeOffset time ? time.ToLocalTime() : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
