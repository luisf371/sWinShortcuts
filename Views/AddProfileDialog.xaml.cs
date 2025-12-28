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

        var executable = NormalizeExecutable(options.ExecutableName);
        ExecutableTextBox.Text = executable;

        if (!string.IsNullOrWhiteSpace(profileName))
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

    private async void OnShowProcessesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            MaxHeight = 400
        };

        // Add placeholder while loading
        var loadingItem = new MenuItem { Header = "Loading...", IsEnabled = false };
        menu.Items.Add(loadingItem);
        menu.IsOpen = true;

        try
        {
            // Run process fetching on background thread
            var processes = await System.Threading.Tasks.Task.Run(() =>
            {
                var list = new List<ProcessEntry>();
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        // Optimization: Use ProcessName directly instead of MainModule.FileName
                        // MainModule is very slow and often fails due to permissions.
                        var name = p.ProcessName;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        
                        // Heuristic: filter out obvious system/background noise if desired,
                        // but for now we just show everything non-empty.
                        
                        long memory = 0;
                        try { memory = p.WorkingSet64; } catch { /* ignore */ }
                        
                        list.Add(new ProcessEntry($"{name}.exe", memory));
                    }
                }
                
                // Group by name to remove duplicates (e.g. multiple chrome.exe processes)
                // and pick the one with highest memory usage to display.
                return list
                    .GroupBy(x => x.Executable)
                    .Select(g => new ProcessEntry(g.Key, g.Sum(x => x.WorkingSet)))
                    .OrderByDescending(x => x.WorkingSet)
                    .ToList();
            });

            menu.Items.Clear();

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
        }
        catch (Exception ex)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = $"Error: {ex.Message}", IsEnabled = false });
        }
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
