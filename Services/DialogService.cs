using System.IO;
using System.Windows;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

public sealed class DialogService : IDialogService
{
    public AddProfileDialogResult? ShowAddProfileDialog()
    {
        var dialog = new AddProfileDialog();

        if (System.Windows.Application.Current?.MainWindow is Window owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true
            ? new AddProfileDialogResult(dialog.ProfileName, dialog.ExecutableName)
            : null;
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
}
