using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sWinShortcuts.ViewModels;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace sWinShortcuts.Views;

public partial class MacrosView : UserControl
{
    public MacrosView() => InitializeComponent();
    private void Editor_Unloaded(object sender, RoutedEventArgs e) => (DataContext as MacrosViewModel)?.LeaveEditor();
    private void Editor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => (e.OldValue as MacrosViewModel)?.LeaveEditor();
    private void StopRecording_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => (DataContext as MacrosViewModel)?.BeginStopGesture();
    private void StopRecording_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) (DataContext as MacrosViewModel)?.BeginStopGesture(e.Key);
    }
}
