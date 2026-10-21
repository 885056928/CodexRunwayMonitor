using System.Windows.Input;

namespace Monitor.App;

public sealed class AsyncRelayCommand : ICommand
{
    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.onError = onError;
        this.canExecute = canExecute;
    }

    private readonly Func<Task> execute;
    private readonly Action<Exception> onError;
    private readonly Func<bool>? canExecute;
    private bool executing;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !executing && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        // ICommand 必须返回 void；异常在这一 UI 入口处理，内部仍保留可等待的 Task。
        try
        {
            await ExecuteAsync();
        }
        catch (Exception error)
        {
            onError(error);
        }
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        executing = true;
        try
        {
            NotifyCanExecuteChanged();
            await execute();
        }
        finally
        {
            executing = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
