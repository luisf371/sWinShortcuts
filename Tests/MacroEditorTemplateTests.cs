using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroEditorTemplateTests
{
    [Theory]
    [InlineData(Key.F13)]
    [InlineData(Key.F24)]
    [InlineData(Key.PrintScreen)]
    [InlineData(Key.Pause)]
    [InlineData(Key.NumLock)]
    public Task SupportedLoadedKey_MacroShortcutPickerRetainsSelectionAndCanReassign(Key key) =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            using var macro = new MacroViewModel(new MacroDefinition { ShortcutKey = key }, () => true);
            var picker = new ComboBox { ItemsSource = MacroViewModel.ShortcutKeyOptions };
            picker.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding(nameof(MacroViewModel.ShortcutKey)) { Source = macro, Mode = BindingMode.TwoWay });
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.Equal(key, macro.ShortcutKey);
            Assert.Equal(key, Assert.IsType<Key>(picker.SelectedItem));
            picker.SelectedItem = Key.None;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.None, macro.ShortcutKey);
            picker.SelectedItem = key;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(key, macro.ShortcutKey);
            Assert.Equal(key, Assert.IsType<Key>(picker.SelectedItem));
        });

    [Fact]
    public Task RecordedUncommonKey_StepPickerRetainsSelectionAndCanReassign() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            using var macro = new MacroViewModel(new MacroDefinition(), () => true);
            var picker = new ComboBox { ItemsSource = MacroViewModel.KeyOptions };
            picker.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding($"{nameof(MacroViewModel.SelectedStep)}.{nameof(MacroStepViewModel.Key)}")
                { Source = macro, Mode = BindingMode.TwoWay });
            macro.InsertRecording(0, [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.PrintScreen }]);
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.Equal(Key.PrintScreen, macro.SelectedStep!.Key);
            Assert.Equal(Key.PrintScreen, Assert.IsType<Key>(picker.SelectedItem));
            picker.SelectedItem = Key.Pause;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.Pause, macro.SelectedStep.Key);
            picker.SelectedItem = Key.PrintScreen;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.PrintScreen, macro.SelectedStep.Key);
            Assert.Equal(Key.PrintScreen, Assert.IsType<Key>(picker.SelectedItem));
        });

    [Fact]
    public Task ActualDeferredMacroTab_LoadsAtMinimumWindowSize_AndKeepsStopAvailable() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.testdata"));
            var brushes = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Brushes.xaml.testdata"));
            var styles = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Styles.xaml.testdata"));
            var macroTab = Assert.Single(source.Descendants(wpf + "TabItem"), item => (string?)item.Attribute("Header") == "Macros");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var namespaces = source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration)
                .Select(attribute => new XAttribute(attribute.Name,
                    attribute.Value.StartsWith("clr-namespace:sWinShortcuts.", StringComparison.Ordinal)
                        ? attribute.Value + ";assembly=sWinShortcuts" : attribute.Value));
            var xaml = new XElement(wpf + "ContentControl", namespaces,
                new XElement(wpf + "ContentControl.Resources",
                    new XElement(wpf + "ResourceDictionary",
                        // Keep the actual app resource order in one lexical scope. Separate BAML
                        // dictionary loads cannot resolve cross-dictionary StaticResources without
                        // Application.Current, which this isolated STA test intentionally avoids.
                        brushes.Root!.Elements().Select(element => new XElement(element)),
                        styles.Root!.Elements().Select(element => new XElement(element)),
                        source.Root.Element(wpf + "Window.Resources")!.Elements().Select(element => new XElement(element)),
                        new XElement(wpf + "DataTemplate", new XAttribute(x + "Key", "MacroTemplate"),
                            new XElement(wpf + "TabControl", new XAttribute("Style", "{StaticResource SettingsTabControlStyle}"),
                                new XAttribute("ItemContainerStyle", "{StaticResource SettingsTabItemStyle}"), new XElement(macroTab))))));
            // XElement stores expanded namespace names on each copied element; changing xmlns alone
            // would leave converter/view nodes in the original assembly-relative namespace.
            foreach (var element in xaml.Descendants())
            {
                var clrNamespace = element.Name.NamespaceName;
                if (clrNamespace.StartsWith("clr-namespace:sWinShortcuts.", StringComparison.Ordinal) &&
                    !clrNamespace.Contains(";assembly=", StringComparison.Ordinal))
                    element.Name = XName.Get(element.Name.LocalName, clrNamespace + ";assembly=sWinShortcuts");
            }
            var host = (ContentControl)XamlReader.Parse(xaml.ToString());
            using var profile = new ProfileViewModel(ProfileFactory.CreateCustomProfile("Game", "game.exe"),
                new FakeDisplayService(), new RecordingColorControlService());
            profile.Macros.NewMacroCommand.Execute(null);
            var macro = profile.Macros.SelectedMacro!;
            macro.InsertStepCommand.Execute(null);
            // Apply the real tab through WPF's deferred template loader with the actual app/window
            // resources already in scope. No application, hooks, or user settings are initialized.
            host.Content = profile;
            host.ContentTemplate = (DataTemplate)host.Resources["MacroTemplate"];
            host.Width = 650;
            host.Height = 480;
            host.FontFamily = new FontFamily("Segoe UI");
            host.FontSize = 14;
            host.Measure(new Size(650, 480));
            host.Arrange(new Rect(0, 0, 650, 480));
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var view = Assert.Single(Descendants(host).OfType<sWinShortcuts.Views.MacrosView>());
            var controls = Descendants(view).OfType<Control>().ToArray();
            var stop = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Stop recording");
            Assert.True(stop.Focusable);
            Assert.False(stop.IsEnabled);
            Assert.DoesNotContain(controls.OfType<Button>(), button => AutomationProperties.GetName(button).Contains("Stop playback", StringComparison.Ordinal));
            Assert.Equal(5, controls.OfType<ComboBox>().Count());
            var cancelOnMovement = Assert.Single(controls.OfType<CheckBox>(), box => AutomationProperties.GetName(box) == "Cancel on mouse movement");
            Assert.False(cancelOnMovement.IsChecked);
            cancelOnMovement.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            Assert.True(macro.ToDefinition().CancelOnMouseMovement);
            Assert.Contains(controls.OfType<ListBoxItem>(), item => AutomationProperties.GetName(item).StartsWith("Step 1: Key press", StringComparison.Ordinal));

            macro.InsertStepCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(1, macro.SelectedIndex);
            macro.MoveStepUpCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(0, macro.SelectedIndex);

            macro.SelectedStep!.Kind = MacroStepKind.MouseClick;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var keyChoice = Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Step key");
            var buttonChoice = Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Step mouse button");
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(keyChoice.Parent).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(buttonChoice.Parent).Visibility);
            host.UpdateLayout();
            var coordinatePicker = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Pick screen coordinates after three seconds");
            var pickerBottom = coordinatePicker.TransformToAncestor(host).Transform(new Point(0, coordinatePicker.ActualHeight)).Y;
            Assert.True(pickerBottom <= host.ActualHeight, $"Coordinate picker bottom {pickerBottom} exceeds viewport {host.ActualHeight}.");

            profile.IsEnabled = false;
            profile.Macros.SetRecordingDestination(macro);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();

            Assert.True(stop.IsEnabled);
            Assert.False(cancelOnMovement.IsEnabled);
            Assert.False(Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Selected macro").IsEnabled);
            Assert.True(view.ActualWidth <= 650);
            Assert.True(stop.TransformToAncestor(host).Transform(new Point(0, 0)).Y < 480);
            Assert.All(controls.OfType<Button>(), button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));

            profile.Macros.SetRecordingDestination(null);
            profile.IsEnabled = true;
            var add = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Add step");
            var menu = add.ContextMenu!;
            // The menu must follow the button's current macro even when opened through the native
            // context-menu gesture. Setting its placement target does not open a native popup.
            menu.PlacementTarget = add;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Same(macro, menu.DataContext);
            profile.Macros.NewMacroCommand.Execute(null);
            var second = profile.Macros.SelectedMacro!;
            menu.Measure(new Size(240, 480));
            menu.Arrange(new Rect(0, 0, 240, 480));
            menu.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Same(second, menu.DataContext);
            var actions = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(Enum.GetValues<MacroStepKind>().Order(), actions.Select(item => (MacroStepKind)item.CommandParameter).Order());
            foreach (var item in actions)
            {
                Assert.Same(second.AddStepCommand, item.Command);
                item.Command.Execute(item.CommandParameter);
                Assert.Equal(item.CommandParameter, second.SelectedStep!.Kind);
            }
            Assert.Equal(2, macro.Steps.Count);

            second.ShortcutKey = Key.F6;
            second.InsertRecording(second.Steps.Count, Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 2 },
                1000 - second.Steps.Count).ToArray());
            second.SelectedStep = second.Steps[^1];
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            var list = Assert.Single(Descendants(view).OfType<ListBox>(), box => AutomationProperties.GetName(box) == "Ordered macro steps");
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(999));
            Assert.InRange(Descendants(list).OfType<ListBoxItem>().Count(), 1, 80);
            second.SelectedStep.DurationText = "invalid";
            second.SelectedStep = second.Steps[0];
            second.ShowProblemStepCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Equal(999, second.SelectedIndex);
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(999));

            var brokenModel = ProfileFactory.CreateCustomProfile("Unreadable", "unreadable.exe");
            brokenModel.IsPersistenceSuspended = true;
            brokenModel.Macros.LoadError = "Unsupported macro format version. The source is preserved.";
            using var broken = new ProfileViewModel(brokenModel, new FakeDisplayService(), new RecordingColorControlService());
            host.Content = broken;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Contains(Descendants(host).OfType<TextBlock>(), block => block.Visibility == Visibility.Visible &&
                block.ActualHeight > 0 && block.Text == brokenModel.Macros.LoadError);
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
