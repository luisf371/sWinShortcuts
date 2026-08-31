using System.Windows;

namespace sWinShortcuts.Views;

public partial class ConfirmRemoveProfileDialog : Window
{
    public ConfirmRemoveProfileDialog()
    {
        InitializeComponent();
    }

    public void Configure(string profileName)
    {
        // Removal deletes durably from the store first (ProfileManager.RemoveProfileAsync) — hence
        // "permanently". The name is plain TextBlock content (no markup surface), matching how
        // profile names are already displayed in the sidebar.
        MessageText.Text = $"Remove profile \"{profileName}\"? Its shortcuts and settings will be permanently deleted.";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
