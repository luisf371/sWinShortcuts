using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sWinShortcuts.ViewModels;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using UserControl = System.Windows.Controls.UserControl;

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

    private void AddStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void WaitEditorOpen_Click(object sender, RoutedEventArgs e) => FocusWaitDuration();
    private void WaitEditorApply_Click(object sender, RoutedEventArgs e) => FocusWaitDuration();
    private void WaitEditorClose_Click(object sender, RoutedEventArgs e) => FocusWaitEditorButton();

    private void WaitEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MacroViewModel macro } || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Enter && !AllWaitDurationField.IsKeyboardFocusWithin) return;
        var command = e.Key switch
        {
            Key.Enter => macro.ApplyAllWaitTimesCommand,
            Key.Escape => macro.CloseWaitEditorCommand,
            _ => null
        };
        if (command is null) return;
        e.Handled = true;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        if (e.Key == Key.Escape) FocusWaitEditorButton();
        else FocusWaitDuration();
    }

    // Button Click is raised before its command runs, so move focus after bindings update.
    private void FocusWaitDuration() => Dispatcher.InvokeAsync(() =>
    {
        if (!AllWaitDurationField.IsVisible || !AllWaitDurationField.IsEnabled) return;
        AllWaitDurationField.Focus();
        AllWaitDurationField.SelectAll();
    }, DispatcherPriority.Loaded);

    private void FocusWaitEditorButton() => Dispatcher.InvokeAsync(() =>
    {
        if (!AllWaitEditorButton.IsVisible) return;
        if (AllWaitEditorButton.IsEnabled) AllWaitEditorButton.Focus();
        else StepList.Focus();
    }, DispatcherPriority.Loaded);

    // Commands, recordings and the problem link also move the selection; keep that row in view.
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is ListBox { SelectedItem: { } step } list) list.ScrollIntoView(step);
    }

    // Row commands rebuild the step collection, which discards the focused row container. Run keyboard
    // edits here so focus follows the step that is selected afterward instead of leaving the list.
    private void StepList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox { DataContext: MacroViewModel macro } list) return;
        var command = GetStepKeyCommand(macro, e.Key == Key.System ? e.SystemKey : e.Key, e.KeyboardDevice.Modifiers);
        if (command is null) return;
        e.Handled = true;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        FocusSelectedStep(list);
    }

    internal static ICommand? GetStepKeyCommand(MacroViewModel macro, Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.Delete, ModifierKeys.None) => macro.DeleteStepCommand,
        (Key.D, ModifierKeys.Control) => macro.DuplicateStepCommand,
        (Key.Up, ModifierKeys.Alt) => macro.MoveStepUpCommand,
        (Key.Down, ModifierKeys.Alt) => macro.MoveStepDownCommand,
        _ => null
    };

    private static void FocusSelectedStep(ListBox list)
    {
        if (list.SelectedItem is not { } step)
        {
            list.Focus();
            return;
        }
        list.ScrollIntoView(step);
        list.Dispatcher.InvokeAsync(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(step) is ListBoxItem row) row.Focus();
            else list.Focus();
        }, DispatcherPriority.Loaded);
    }
}
