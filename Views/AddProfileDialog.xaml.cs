using System;
using System.IO;
using System.Windows;

namespace sWinShortcuts.Views;

public partial class AddProfileDialog : Window
{
    public AddProfileDialog()
    {
        InitializeComponent();
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

    private static string NormalizeExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(value.Trim());
        return fileName?.Trim() ?? string.Empty;
    }
}
