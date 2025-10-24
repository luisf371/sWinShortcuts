using System;
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

    public string ExecutablePath => ExecutableTextBox.Text.Trim();

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
            ExecutableTextBox.Text = dialog.FileName;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            System.Windows.MessageBox.Show(this, "Please enter a profile name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            System.Windows.MessageBox.Show(this, "Please enter an executable path or file name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = ExecutablePath;
        if (!File.Exists(path) && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(this, "The executable must end with .exe or point to an existing file.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

