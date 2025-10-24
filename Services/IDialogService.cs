namespace sWinShortcuts.Services;

public interface IDialogService
{
    AddProfileDialogResult? ShowAddProfileDialog();

    string? ShowOpenFileDialog(string title, string filter, string? initialPath = null);

    void ShowError(string message, string title);
}

public sealed record AddProfileDialogResult(string ProfileName, string ExecutablePath);
