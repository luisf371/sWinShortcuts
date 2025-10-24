using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed partial class WindowsLauncherEntryViewModel : ViewModelBase
{
    private readonly LauncherBinding _binding;

    public event EventHandler? Changed;

    public WindowsLauncherEntryViewModel(Key key, LauncherBinding binding)
    {
        Key = key;
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        path = _binding.Path;
        arguments = _binding.Arguments;
        runAsAdministrator = _binding.RunAsAdmin;
    }

    public Key Key { get; }

    [ObservableProperty]
    private string path;

    [ObservableProperty]
    private string arguments;

    [ObservableProperty]
    private bool runAsAdministrator;

    partial void OnPathChanged(string value)
    {
        _binding.Path = value ?? string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnArgumentsChanged(string value)
    {
        _binding.Arguments = value ?? string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnRunAsAdministratorChanged(bool value)
    {
        _binding.RunAsAdmin = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
