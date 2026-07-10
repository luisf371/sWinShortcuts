using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public int ErrorCount { get; private set; }
    public string? LastError { get; private set; }

    // When true, ShowError itself throws — used to exercise the save-loop fault-cleanup path (an error
    // dialog failing during dispatcher shutdown must not leave a profile's edit lost / a task stuck).
    public bool ThrowOnError { get; set; }

    public AddProfileDialogResult? ShowAddProfileDialog(string? profileName = null, string? executableName = null) => null;

    public AddProfileDialogResult? ShowEditProfileDialog(string profileName, string executableName) => null;

    public string? ShowOpenFileDialog(string title, string filter, string? initialPath = null) => null;

    public void ShowError(string message, string title)
    {
        ErrorCount++;
        LastError = message;
        if (ThrowOnError)
        {
            throw new InvalidOperationException("dialog failed");
        }
    }
}
