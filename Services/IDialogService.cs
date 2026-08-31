namespace sWinShortcuts.Services;

public interface IDialogService
{
    AddProfileDialogResult? ShowAddProfileDialog(string? profileName = null, string? executableName = null);

    AddProfileDialogResult? ShowEditProfileDialog(string profileName, string executableName);

    string? ShowOpenFileDialog(string title, string filter, string? initialPath = null);

    bool ShowRemoveProfileConfirmation(string profileName);

    void ShowError(string message, string title);
}

public sealed record AddProfileDialogResult(string ProfileName, string ExecutableName);
