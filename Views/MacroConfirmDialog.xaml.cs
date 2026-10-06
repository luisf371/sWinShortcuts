using System.Windows;
using System.Windows.Automation;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts.Views;

public partial class MacroConfirmDialog : Window
{
    public MacroConfirmDialog()
    {
        InitializeComponent();
    }

    // Labels and macro names are plain TextBlock and button content, so they carry no markup.
    internal void Configure(MacroConfirmation request)
    {
        Title = request.Title;
        HeadingText.Text = request.Title;
        MessageText.Text = request.Message;
        ConfirmButton.Content = request.ActionText;
        AutomationProperties.SetName(ConfirmButton, request.ActionText);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
