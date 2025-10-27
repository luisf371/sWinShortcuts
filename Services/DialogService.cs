using System;
using System.IO;
using System.Windows;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

public sealed class DialogService : IDialogService
{
    public AddProfileDialogResult? ShowAddProfileDialog(string? profileName = null, string? executableName = null)
    {
        var options = new AddProfileDialogOptions(
            Title: "Add Profile",
            PrimaryButtonText: "Add",
            ProfileName: profileName,
            ExecutableName: executableName,
            IsProfileNameReadOnly: false);

        return ShowProfileDialog(options);
    }

    public AddProfileDialogResult? ShowEditProfileDialog(string profileName, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var options = new AddProfileDialogOptions(
            Title: "Modify Profile",
            PrimaryButtonText: "Save",
            ProfileName: profileName,
            ExecutableName: executableName,
            IsProfileNameReadOnly: false);

        return ShowProfileDialog(options);
    }

    public string? ShowOpenFileDialog(string title, string filter, string? initialPath = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            if (File.Exists(initialPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(initialPath);
                dialog.FileName = Path.GetFileName(initialPath);
            }
            else if (Directory.Exists(initialPath))
            {
                dialog.InitialDirectory = initialPath;
            }
        }

        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public void ShowError(string message, string title)
    {
        System.Windows.MessageBox.Show(System.Windows.Application.Current?.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static AddProfileDialogResult? ShowProfileDialog(AddProfileDialogOptions options)
    {
        var dialog = new AddProfileDialog();
        dialog.Configure(options);

        if (System.Windows.Application.Current?.MainWindow is Window owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true
            ? new AddProfileDialogResult(dialog.ProfileName, dialog.ExecutableName)
            : null;
    }
}
