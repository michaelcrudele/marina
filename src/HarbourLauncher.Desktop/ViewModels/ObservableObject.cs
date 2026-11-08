using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HarbourLauncher.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A command that runs an action. Errors are the action's job to handle.</summary>
public sealed class RelayCommand(Func<Task> run) : ICommand
{
    public RelayCommand(Action run) : this(() => { run(); return Task.CompletedTask; }) { }

    // Required by ICommand; these commands are always runnable (the UI hides them instead).
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await run();
}
