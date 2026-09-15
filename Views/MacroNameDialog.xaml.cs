using System.Windows;
using System.Windows.Controls;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts.Views;

public partial class MacroNameDialog : Window
{
    private bool _edited;

    public MacroNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    // A null label names a new macro; otherwise the dialog renames that label, prefilled and selected.
    public void Configure(string? currentLabel)
    {
        var rename = currentLabel is not null;
        Title = rename ? "Rename Macro" : "New Macro";
        HeadingText.Text = Title;
        AcceptButton.Content = rename ? "Rename" : "Create";
        NameTextBox.Text = currentLabel ?? string.Empty;
        _edited = false;
        UpdateValidation();
    }

    public string MacroName => NameTextBox.Text.Trim();

    private void OnNameTextChanged(object sender, TextChangedEventArgs e)
    {
        _edited = true;
        UpdateValidation();
    }

    // Accept stays unavailable while the name breaks the saved label rules. The reason appears once the user
    // has typed, so an empty new-macro field does not open with an error.
    private void UpdateValidation()
    {
        var error = MacrosViewModel.GetLabelError(MacroName);
        AcceptButton.IsEnabled = error is null;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = error is not null && _edited ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (MacrosViewModel.GetLabelError(MacroName) is null)
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
