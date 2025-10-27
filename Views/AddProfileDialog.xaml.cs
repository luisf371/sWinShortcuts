using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WpfButton = System.Windows.Controls.Button;

namespace sWinShortcuts.Views;

public partial class AddProfileDialog : Window
{
    public AddProfileDialog()
    {
        InitializeComponent();
    }

    public void Configure(AddProfileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            Title = options.Title;
        }

        if (!string.IsNullOrWhiteSpace(options.PrimaryButtonText))
        {
            PrimaryButton.Content = options.PrimaryButtonText;
        }

        var profileName = options.ProfileName ?? string.Empty;
        NameTextBox.Text = profileName.Trim();
        NameTextBox.IsReadOnly = options.IsProfileNameReadOnly;
        NameTextBox.IsHitTestVisible = !options.IsProfileNameReadOnly;
        NameTextBox.Focusable = !options.IsProfileNameReadOnly;

        var executable = NormalizeExecutable(options.ExecutableName);
        ExecutableTextBox.Text = executable;

        if (options.IsProfileNameReadOnly)
        {
            ExecutableTextBox.Focus();
            ExecutableTextBox.SelectAll();
        }
    }

    public string ProfileName => NameTextBox.Text.Trim();

    public string ExecutableName => NormalizeExecutable(ExecutableTextBox.Text);

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Executable",
            Filter = "Executable Files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ExecutableTextBox.Text = NormalizeExecutable(dialog.FileName);
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            System.Windows.MessageBox.Show(this, "Please enter a profile name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var executable = ExecutableName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            System.Windows.MessageBox.Show(this, "Please enter an executable file name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(this, "The executable must end with .exe.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecutableTextBox.Text = executable;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnShowProcessesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false
        };

        var processes = GetRunningExecutables();
        if (processes.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No processes available",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var entry in processes)
            {
                var menuItem = new MenuItem
                {
                    Header = $"{entry.Executable} ({FormatMemory(entry.WorkingSet)})"
                };
                menuItem.Click += (_, _) => ExecutableTextBox.Text = NormalizeExecutable(entry.Executable);
                menu.Items.Add(menuItem);
            }
        }

        menu.IsOpen = true;
    }

    private static string NormalizeExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(value.Trim());
        return fileName?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<ProcessEntry> GetRunningExecutables()
    {
        return Process.GetProcesses()
            .Select(CreateProcessEntry)
            .OfType<ProcessEntry>()
            .OrderByDescending(entry => entry.WorkingSet)
            .ToList();
    }

    private static ProcessEntry? CreateProcessEntry(Process process)
    {
        string? executable = TryGetExecutableName(process);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        long workingSet;
        try
        {
            workingSet = process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new ProcessEntry(executable, workingSet);
    }

    private static string? TryGetExecutableName(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return Path.GetFileName(path);
            }
        }
        catch (Win32Exception)
        {
            // Access denied. Fallback to process name.
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var name = process.ProcessName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return $"{name}.exe";
    }

    private static string FormatMemory(long bytes)
    {
        const double OneGiB = 1024d * 1024d * 1024d;
        const double OneMiB = 1024d * 1024d;

        if (bytes >= OneGiB)
        {
            return $"{bytes / OneGiB:0.0} GB";
        }

        return $"{bytes / OneMiB:0.0} MB";
    }

    private sealed record ProcessEntry(string Executable, long WorkingSet);
}

public sealed record AddProfileDialogOptions(
    string? Title,
    string? PrimaryButtonText,
    string? ProfileName,
    string? ExecutableName,
    bool IsProfileNameReadOnly);
