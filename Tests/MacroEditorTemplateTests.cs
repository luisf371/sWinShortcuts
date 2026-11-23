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
using sWinShortcuts.Controls;
using sWinShortcuts.Converters;
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
    public Task SupportedLoadedKey_MacroShortcutCaptureBoxShowsKeyAndCanReassign(Key key) =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            using var macro = new MacroViewModel(new MacroDefinition { ShortcutKey = key }, () => true);
            var box = new KeyCaptureBox { Mode = KeyCaptureMode.Combo, AllowNone = true, NoneText = "No shortcut" };
            box.SetBinding(KeyCaptureBox.ChordProperty,
                new Binding(nameof(MacroViewModel.Shortcut)) { Source = macro, Mode = BindingMode.TwoWay });
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.Equal(key, macro.ShortcutKey);
            Assert.Equal(KeyChord.FromKey(key), box.Chord);
            Assert.Equal(KeyDisplayConverter.ToDisplayText(key), box.DisplayBox.Text);
            box.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.None);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.None, macro.ShortcutKey);
            Assert.Equal("No shortcut", box.DisplayBox.Text);
            box.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.FromKey(key, ModifierKeys.Control | ModifierKeys.Windows));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(key, macro.ShortcutKey);
            Assert.Equal(ModifierKeys.Control | ModifierKeys.Windows, macro.ShortcutModifiers);
            Assert.Equal("Ctrl+Win+" + KeyDisplayConverter.ToDisplayText(key), box.DisplayBox.Text);
        });

    [Fact]
    public Task RecordedUncommonKey_StepCaptureBoxShowsKeyAndCanReassign() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            using var macro = new MacroViewModel(new MacroDefinition(), () => true);
            var box = new KeyCaptureBox { Validator = MacroViewModel.KeyValidator };
            box.SetBinding(KeyCaptureBox.ChordProperty,
                new Binding($"{nameof(MacroViewModel.SelectedStep)}.{nameof(MacroStepViewModel.Key)}")
                { Source = macro, Mode = BindingMode.TwoWay, Converter = new KeyChordConverter() });
            macro.InsertRecording(0, [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.PrintScreen }]);
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.Equal(Key.PrintScreen, macro.SelectedStep!.Key);
            Assert.Equal(KeyChord.FromKey(Key.PrintScreen), box.Chord);
            Assert.Equal("Print Screen", box.DisplayBox.Text);
            box.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.FromKey(Key.Pause));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.Pause, macro.SelectedStep.Key);
            box.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.FromKey(Key.PrintScreen));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(Key.PrintScreen, macro.SelectedStep.Key);
            Assert.Equal("Print Screen", box.DisplayBox.Text);
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
            // Same section box and header switch as the other tabs. Off disables and shades the whole body,
            // including management and the empty state, while the switch, notices and Stop stay outside it.
            var section = Assert.Single(controls.OfType<GroupBox>());
            Assert.Equal(new Thickness(16), section.Padding);
            var master = Assert.Single(controls.OfType<CheckBox>(), box => AutomationProperties.GetName(box) == "Enable macros for this profile");
            Assert.Same(section.Header, master.Parent);
            var gated = Descendants(section).OfType<FrameworkElement>()
                .Where(element => ReferenceEquals(element.Style, host.Resources["FeatureToggleContentStyle"])).ToArray();
            Assert.Equal(3, gated.Length);
            // Materialize a button's menu without a native popup: the real resources, its placement target, and a layout pass.
            async Task<MenuItem[]> MenuItemsAsync(string buttonName)
            {
                var owner = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == buttonName);
                var menu = owner.ContextMenu!;
                if (!menu.Resources.MergedDictionaries.Contains(host.Resources)) menu.Resources.MergedDictionaries.Add(host.Resources);
                menu.PlacementTarget = owner;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                menu.Measure(new Size(320, 480));
                menu.Arrange(new Rect(0, 0, 320, 480));
                menu.UpdateLayout();
                return menu.Items.OfType<MenuItem>().ToArray();
            }
            static MenuItem MenuItemNamed(MenuItem[] items, string name) => Assert.Single(items, item => AutomationProperties.GetName(item) == name);
            var body = new[] { "Selected macro", "Enable selected macro", "New macro", "Ordered macro steps", "Create first macro", "Playback options" }
                .Select(name => Assert.Single(controls, control => AutomationProperties.GetName(control) == name)).ToArray();
            Assert.All(body, control => Assert.Contains(gated, part => part.IsAncestorOf(control)));
            Assert.All(new FrameworkElement[]
            {
                master,
                Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Stop recording"),
                Assert.Single(Descendants(view).OfType<TextBlock>(), block => AutomationProperties.GetName(block) == "Macro load error")
            }, element => Assert.DoesNotContain(gated, part => part.IsAncestorOf(element)));
            Assert.False(profile.Macros.IsEnabled);
            Assert.True(master.IsEnabled);
            Assert.All(gated, part => Assert.Equal(0.75, part.Opacity));
            Assert.All(body, control => Assert.False(control.IsEnabled));
            master.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(profile.Macros.IsEnabled);
            Assert.All(gated, part => Assert.Equal(1d, part.Opacity));
            Assert.All(body, control => Assert.True(control.IsEnabled));
            // The selected macro's own switch is separate from the section switch; a macro that is off stays editable.
            var macroSwitch = Assert.IsType<CheckBox>(body[1]);
            Assert.NotSame(master, macroSwitch);
            macroSwitch.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(macro.IsEnabled);
            macroSwitch.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.False(macro.IsEnabled);
            Assert.True(profile.Macros.IsEnabled);
            Assert.True(Assert.Single(controls.OfType<KeyCaptureBox>(), box => AutomationProperties.GetName(box) == "Macro shortcut").IsEnabled);
            // Names change only through the naming dialog: no permanent Name field, and both New entry points and
            // Rename request a name from the view model. A stand-in request replaces the modal window here.
            Assert.DoesNotContain(controls.OfType<TextBox>(), box => AutomationProperties.GetName(box) == "Macro label");
            // The rarer commands sit under More, bound to the same editor commands.
            var more = await MenuItemsAsync("More macro commands");
            var rename = MenuItemNamed(more, "Rename macro");
            Assert.True(rename.IsEnabled);
            Assert.Same(profile.Macros.RenameMacroCommand, rename.Command);
            Assert.Same(profile.Macros.DuplicateMacroCommand, MenuItemNamed(more, "Duplicate macro").Command);
            Assert.Same(profile.Macros.ClearStepsCommand, MenuItemNamed(more, "Clear all steps").Command);
            Assert.Same(profile.Macros.DeleteMacroCommand, MenuItemNamed(more, "Delete macro").Command);
            var newButtons = controls.OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) is "New macro" or "Create first macro").ToArray();
            Assert.Equal(2, newButtons.Length);
            Assert.All(newButtons, button => Assert.Same(profile.Macros.NewMacroCommand, button.Command));
            var requests = new List<string?>();
            profile.Macros.ConfigureNaming(label =>
            {
                requests.Add(label);
                return label is null ? null : "Renamed";
            });
            foreach (var button in newButtons) button.Command.Execute(null);
            rename.Command.Execute(null);
            Assert.Equal(new string?[] { null, null, "New macro" }, requests);
            Assert.Same(macro, Assert.Single(profile.Macros.Definitions));
            Assert.Equal("Renamed", macro.Label);
            // The shortcut is one [read-only box][Assign][Clear] capture field: no modifier checkboxes or key list remain.
            var shortcutPicker = Assert.Single(controls.OfType<KeyCaptureBox>(), box => AutomationProperties.GetName(box) == "Macro shortcut");
            Assert.Equal(KeyCaptureMode.Combo, shortcutPicker.Mode);
            Assert.True(shortcutPicker.AcceptsMouseButtons);
            Assert.True(shortcutPicker.AcceptsLeftButton);
            Assert.True(shortcutPicker.DisplayBox.IsReadOnly);
            Assert.False(shortcutPicker.DisplayBox.Focusable);
            Assert.Equal(Visibility.Visible, shortcutPicker.ClearButton.Visibility);
            Assert.Equal("Assign", shortcutPicker.AssignButton.Content);
            Assert.Equal("No shortcut", shortcutPicker.DisplayBox.Text);
            Assert.DoesNotContain(controls.OfType<CheckBox>(), box => AutomationProperties.GetName(box).StartsWith("Shortcut ", StringComparison.Ordinal));
            foreach (var button in MacroViewModel.MouseButtons)
            {
                var trigger = InputTrigger.FromMouseButton(button);
                shortcutPicker.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.FromTrigger(trigger));
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(trigger, macro.ShortcutTrigger);
                Assert.Equal(Key.None, macro.ShortcutKey);
                Assert.Equal(button, profile.Model.Macros.Definitions[0].ShortcutMouseButton);
            }
            macro.Shortcut = KeyChord.FromTrigger(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.Middle),
                ModifierKeys.Control | ModifierKeys.Alt);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal("Ctrl+Alt+Middle Mouse Button", macro.ShortcutText);
            Assert.Equal(macro.Shortcut, shortcutPicker.Chord);
            Assert.Equal("Ctrl+Alt+Middle Mouse Button", shortcutPicker.DisplayBox.Text);
            host.UpdateLayout();
            var shortcutBounds = shortcutPicker.TransformToAncestor(host).TransformBounds(new Rect(shortcutPicker.RenderSize));
            Assert.True(new Rect(host.RenderSize).Contains(shortcutBounds));
            macro.ShortcutModifiers = ModifierKeys.None;
            var selector = Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Selected macro");
            var originalEnabled = macro.IsEnabled;
            var originalDuration = macro.SelectedStep!.DurationText;
            macro.ShortcutKey = Key.F24;
            macro.SelectedStep.Key = Key.A;
            // Materialize the real selector item without opening a native popup. Selected shortcut
            // and status text must remain readable in ready, off, and unsaved-draft states.
            var popup = (System.Windows.Controls.Primitives.Popup)selector.Template.FindName("Popup", selector);
            popup.Child.Measure(new Size(600, 480));
            popup.Child.Arrange(new Rect(0, 0, 600, popup.Child.DesiredSize.Height));
            popup.Child.UpdateLayout();
            var entry = Assert.IsType<ComboBoxItem>(selector.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(entry.IsSelected);
            // Playback choices live in one menu: Once/Loop is a radio pair bound both ways, and the short status
            // ("Ready", "Off") yields to validation messages.
            var playback = await MenuItemsAsync("Playback options");
            var toggleLoop = MenuItemNamed(playback, "Toggle loop");
            var playOnce = MenuItemNamed(playback, "Play once per press");
            var macroStatus = Assert.IsType<TextBlock>(view.FindName("MacroStatus"));
            Assert.True(playOnce.IsChecked);
            Assert.False(toggleLoop.IsChecked);
            Assert.False(macro.ToggleMode);
            foreach (var state in new[] { "Ready", "Off", "Not saved" })
            {
                macro.IsEnabled = state != "Off";
                macro.SelectedStep.DurationText = state == "Not saved" ? "invalid" : originalDuration;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                popup.Child.UpdateLayout();
                var texts = Descendants(entry).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible).ToArray();
                Assert.Contains(texts, text => text.Text == "F24");
                if (state != "Ready") Assert.Contains(texts, text => text.Text == state);
                foreach (var text in texts) AssertReadableText(text, entry.Background);
                foreach (var loop in new[] { true, false })
                {
                    (loop ? toggleLoop : playOnce).SetCurrentValue(MenuItem.IsCheckedProperty, true);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Assert.Equal(loop, macro.ToggleMode);
                    Assert.Equal(!loop, playOnce.IsChecked);
                    Assert.Equal(state switch { "Ready" => "Ready", "Off" => "Off", _ => macro.ValidationMessage }, macroStatus.Text);
                }
            }
            // A menu click on the checked item writes false; the pair stays checked.
            playOnce.SetCurrentValue(MenuItem.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(playOnce.IsChecked);
            Assert.False(macro.ToggleMode);
            // The second header row fits at the minimum width.
            host.UpdateLayout();
            var playbackButton = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Playback options");
            var playbackBounds = playbackButton.TransformToAncestor(host).TransformBounds(new Rect(playbackButton.RenderSize));
            Assert.True(new Rect(host.RenderSize).Contains(playbackBounds), $"Playback options exceed the view: {playbackBounds}.");
            macro.IsEnabled = originalEnabled;
            macro.ShortcutKey = Key.None;
            macro.SelectedStep.Key = Key.None;
            macro.SelectedStep.DurationText = originalDuration;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            var stop = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Stop recording");
            Assert.True(stop.Focusable);
            Assert.False(stop.IsEnabled);
            Assert.DoesNotContain(controls.OfType<Button>(), button => AutomationProperties.GetName(button).Contains("Stop playback", StringComparison.Ordinal));
            // Selector, press button, step action and step button; every key field is a capture box.
            Assert.Equal(4, controls.OfType<ComboBox>().Count());
            Assert.Equal(new[] { "Macro shortcut", "Press key", "Step key" },
                controls.OfType<KeyCaptureBox>().Select(box => AutomationProperties.GetName(box)).Order().ToArray());
            var cancelOnMovement = MenuItemNamed(playback, "Cancel on mouse movement");
            Assert.False(cancelOnMovement.IsChecked);
            cancelOnMovement.SetCurrentValue(MenuItem.IsCheckedProperty, true);
            Assert.True(macro.ToDefinition().CancelOnMouseMovement);
            Assert.Equal("Once · stops on move", macro.PlaybackSummary);
            Assert.Contains(controls.OfType<ListBoxItem>(), item => AutomationProperties.GetName(item).StartsWith("Step 1: Key press", StringComparison.Ordinal));

            macro.InsertStepCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(1, macro.SelectedIndex);
            macro.MoveStepUpCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(0, macro.SelectedIndex);

            macro.SelectedStep!.Kind = MacroStepKind.MouseClick;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var keyChoice = Assert.Single(controls.OfType<KeyCaptureBox>(), box => AutomationProperties.GetName(box) == "Step key");
            Assert.Equal(KeyCaptureMode.SingleKey, keyChoice.Mode);
            var buttonChoice = Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Step mouse button");
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(keyChoice.Parent).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(buttonChoice.Parent).Visibility);
            host.UpdateLayout();
            var coordinatePicker = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Pick screen coordinates after three seconds");
            var pickerBottom = coordinatePicker.TransformToAncestor(host).Transform(new Point(0, coordinatePicker.ActualHeight)).Y;
            Assert.True(pickerBottom <= host.ActualHeight, $"Coordinate picker bottom {pickerBottom} exceeds viewport {host.ActualHeight}.");

            // Move to is the tallest move inspector: position, picker, then the held-button choice. The choice sits
            // below the picker, so the picker stays in view (the inspector scrolls for the rest) and the choices fit its width.
            macro.SelectedStep!.Kind = MacroStepKind.MoveTo;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            var moveAnyway = Assert.Single(Descendants(view).OfType<RadioButton>(), radio => AutomationProperties.GetName(radio) == "If a mouse button is held, move anyway");
            // Choice → segment row → segmented frame → labelled group.
            var heldGroup = Assert.IsType<StackPanel>(Assert.IsType<Border>(Assert.IsType<StackPanel>(moveAnyway.Parent).Parent).Parent);
            Assert.Equal(Visibility.Visible, heldGroup.Visibility);
            Assert.True(moveAnyway.ActualHeight > 0);
            var movePicker = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Pick screen coordinates after three seconds");
            var movePickerBottom = movePicker.TransformToAncestor(host).Transform(new Point(0, movePicker.ActualHeight)).Y;
            Assert.True(movePickerBottom <= host.ActualHeight, $"Move to picker bottom {movePickerBottom} exceeds viewport {host.ActualHeight}.");
            var inspector = Assert.IsAssignableFrom<FrameworkElement>(heldGroup.Parent);
            var heldRight = moveAnyway.TransformToAncestor(inspector).Transform(new Point(moveAnyway.ActualWidth, 0)).X;
            Assert.True(heldRight <= inspector.ActualWidth, $"Held-button choice right edge {heldRight} exceeds inspector width {inspector.ActualWidth}.");
            macro.SelectedStep!.Kind = MacroStepKind.MouseClick;
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            // Stop stays operable with the section off and the profile disabled while recording.
            master.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            profile.IsEnabled = false;
            profile.Macros.SetRecordingDestination(macro);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();

            Assert.False(profile.Macros.IsEnabled);
            Assert.All(gated, part => Assert.False(part.IsEnabled));
            Assert.False(master.IsEnabled);
            Assert.True(stop.IsEnabled);
            // The playback menu opens only from its button, which is off while recording.
            Assert.False(playbackButton.IsEnabled);
            Assert.False(Assert.Single(controls.OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "Selected macro").IsEnabled);
            Assert.True(view.ActualWidth <= 650);
            Assert.True(stop.TransformToAncestor(host).Transform(new Point(0, 0)).Y < 480);
            Assert.All(controls.OfType<Button>(), button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));

            profile.Macros.SetRecordingDestination(null);
            profile.IsEnabled = true;
            // The section switch must still be able to turn itself back on.
            Assert.True(master.IsEnabled);
            master.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(profile.Macros.IsEnabled);
            Assert.All(gated, part => Assert.True(part.IsEnabled));
            var add = Assert.Single(controls.OfType<Button>(), button => AutomationProperties.GetName(button) == "Add step");
            var menu = add.ContextMenu!;
            // A detached popup normally resolves these resources through Application.Current.
            menu.Resources.MergedDictionaries.Add(host.Resources);
            // The menu must follow the button's current macro even when opened through the native
            // context-menu gesture. Setting its placement target does not open a native popup.
            menu.PlacementTarget = add;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Same(macro, menu.DataContext);
            // New now asks the stand-in naming request above, which cancels; create these fixtures directly.
            var second = profile.Macros.AddMacro("Second")!;
            menu.Measure(new Size(240, 480));
            menu.Arrange(new Rect(0, 0, 240, 480));
            menu.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Same(second, menu.DataContext);
            var actions = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(Enum.GetValues<MacroStepKind>().Order(), actions.Select(item => (MacroStepKind)item.CommandParameter).Order());
            // Activate the actual template trigger without a native window or physical pointer.
            var highlightKey = Assert.IsType<DependencyPropertyKey>(typeof(MenuItem).GetField("IsHighlightedPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            foreach (var item in actions)
            {
                item.SetValue(highlightKey, true);
                menu.UpdateLayout();
                Assert.True(item.IsEnabled);
                var background = Assert.IsType<Border>(item.Template.FindName("BackgroundBorder", item)).Background;
                var header = Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Text == (string)item.Header);
                AssertReadableText(header, background);
                Assert.True(ContrastRatio(Assert.IsType<System.Windows.Shapes.Path>(item.Icon).Stroke, background) >= 3);
                item.SetValue(highlightKey, false);
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

            var grouped = profile.Macros.AddMacro("Grouped")!;
            grouped.ShortcutKey = Key.F7;
            grouped.InsertRecording(0,
            [
                new() { Kind = MacroStepKind.KeyDown, Key = Key.A },
                new() { Kind = MacroStepKind.Wait, DurationMs = 25 },
                new() { Kind = MacroStepKind.KeyUp, Key = Key.A },
                new() { Kind = MacroStepKind.Wait, DurationMs = 50 }
            ]);
            // A take leaves its last row selected so the next one follows it; select the press to edit it.
            Assert.Same(grouped.Steps[3], grouped.SelectedStep);
            grouped.SelectedStep = grouped.Steps[0];
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Equal(2, list.Items.Count);
            Assert.Same(grouped.Steps[0], list.SelectedItem);
            var groupedRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal("Steps 1 to 3: Key press. A 25 ms hold", AutomationProperties.GetName(groupedRow));
            var pressKey = Assert.Single(Descendants(view).OfType<KeyCaptureBox>(), box => AutomationProperties.GetName(box) == "Press key");
            Assert.Equal("A", pressKey.DisplayBox.Text);
            pressKey.SetCurrentValue(KeyCaptureBox.ChordProperty, KeyChord.FromKey(Key.B));
            Assert.Equal(Key.B, grouped.Steps[0].Key);
            Assert.Equal(Key.B, grouped.Steps[2].Key);
            var hold = Assert.Single(Descendants(view).OfType<TextBox>(), box => AutomationProperties.GetName(box) == "Press hold in milliseconds");
            var visible = grouped.VisibleSteps;
            foreach (var text in new[] { "letters", "1", "12", "120" })
            {
                hold.SetCurrentValue(TextBox.TextProperty, text);
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                host.UpdateLayout();
                Assert.Same(visible, grouped.VisibleSteps);
                Assert.Same(grouped.Steps[0], list.SelectedItem);
                Assert.Equal(text, grouped.Steps[1].DurationText);
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(Assert.IsType<StackPanel>(hold.Parent).Parent).Visibility);
            }

            // Wait times sits in the sequence toolbar beside Add step; the list header switches between presses and
            // every step, and Clear (under More) follows the selected macro.
            var bulk = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Set wait times");
            Assert.Same(add.Parent, bulk.Parent);
            var collapse = Assert.Single(Descendants(view).OfType<RadioButton>(), radio => AutomationProperties.GetName(radio) == "Group presses");
            var showEvery = Assert.Single(Descendants(view).OfType<RadioButton>(), radio => AutomationProperties.GetName(radio) == "Show every step");
            Assert.True(collapse.IsChecked);
            Assert.False(showEvery.IsChecked);
            Assert.True(MenuItemNamed(await MenuItemsAsync("More macro commands"), "Clear all steps").IsEnabled);
            var listTop = list.TransformToAncestor(host).Transform(new Point(0, 0)).Y;
            bulk.Command.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            var duration = Assert.Single(Descendants(view).OfType<TextBox>(), box => AutomationProperties.GetName(box) == "Wait time in milliseconds");
            var apply = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Apply wait time");
            var close = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Cancel wait time editor");
            var status = Assert.Single(Descendants(view).OfType<TextBlock>(), block => AutomationProperties.GetName(block) == "Wait time edit status");
            var scopes = Descendants(view).OfType<RadioButton>()
                .Where(radio => AutomationProperties.GetName(radio).StartsWith("Change ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { "Change press holds", "Change Wait steps between actions", "Change press holds and Wait steps" },
                scopes.Select(AutomationProperties.GetName));
            // Press holds open first with the suggested 50 ms; the Wait after the press keeps its own timing.
            Assert.Equal(new bool?[] { true, false, false }, scopes.Select(scope => scope.IsChecked));
            Assert.Equal("50", duration.Text);
            Assert.Equal(string.Empty, status.Text);
            Assert.True(apply.IsEnabled);
            duration.SetCurrentValue(TextBox.TextProperty, "invalid");
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(Validation.GetHasError(duration));
            Assert.False(apply.IsEnabled);
            Assert.Equal(120, grouped.Steps[1].DurationMs);
            duration.SetCurrentValue(TextBox.TextProperty, "40");
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.True(apply.IsEnabled);
            apply.Command.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Equal("Set to 40 ms", status.Text);
            Assert.True(grouped.IsWaitEditorOpen);
            Assert.Equal(40, grouped.Steps[1].DurationMs);
            Assert.Equal(50, grouped.Steps[3].DurationMs);
            // The actual radio bindings retarget the editor.
            scopes[1].SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(MacroWaitScope.BetweenSteps, grouped.WaitScope);
            Assert.Equal(new bool?[] { false, true, false }, scopes.Select(scope => scope.IsChecked));
            Assert.Equal(string.Empty, status.Text);
            host.UpdateLayout();
            var sequencePanel = Assert.IsType<Border>(Assert.IsType<Grid>(Assert.IsType<Grid>(list.Parent).Parent).Parent);
            var panelBounds = sequencePanel.TransformToAncestor(host).TransformBounds(new Rect(sequencePanel.RenderSize));
            foreach (var control in new FrameworkElement[] { collapse, showEvery })
            {
                var bounds = control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize));
                Assert.True(panelBounds.Contains(bounds), $"{AutomationProperties.GetName(control)} exceeds the sequence panel {panelBounds}: {bounds}.");
            }
            // The bar takes over the toolbar row at the same height: the commands hide, every bar control fits in
            // the one row at the minimum width, and the step list does not move.
            Assert.Equal(Visibility.Collapsed, Assert.IsType<DockPanel>(bulk.Parent).Visibility);
            var bar = Assert.Single(Descendants(view).OfType<Border>(), border => AutomationProperties.GetName(border) == "Wait time editor");
            var barBounds = bar.TransformToAncestor(host).TransformBounds(new Rect(bar.RenderSize));
            FrameworkElement[] barControls = [duration, apply, close, .. scopes];
            foreach (var control in barControls)
            {
                var bounds = control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize));
                Assert.True(barBounds.Contains(bounds), $"{AutomationProperties.GetName(control)} exceeds the wait time bar {barBounds}: {bounds}.");
            }
            Assert.Equal(listTop, list.TransformToAncestor(host).Transform(new Point(0, 0)).Y, 3);
            var durationRight = duration.TransformToAncestor(host).Transform(new Point(duration.ActualWidth, 0)).X;
            var applyLeft = apply.TransformToAncestor(host).Transform(new Point(0, 0)).X;
            Assert.True(durationRight < applyLeft, "Bulk duration field overlaps Apply.");
            // The shaded bar ends just after Cancel instead of spanning the row.
            var closeRight = close.TransformToAncestor(host).Transform(new Point(close.ActualWidth, 0)).X;
            Assert.InRange(barBounds.Right - closeRight, 0, 4);
            close.Command.Execute(null);
            var expand = Assert.Single(Descendants(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Show all steps");
            Assert.True(expand.IsEnabled);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(expand.Parent).Visibility);
            expand.Command.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.False(grouped.CollapseSteps);
            // The view switch follows the expand link.
            Assert.True(showEvery.IsChecked);
            Assert.False(collapse.IsChecked);
            Assert.Equal(4, list.Items.Count);
            Assert.Same(grouped.Steps[0], list.SelectedItem);
            list.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, grouped.Steps[1]);
            grouped.CollapseSteps = true;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Same(grouped.Steps[0], grouped.SelectedStep);
            Assert.Same(grouped.SelectedStep, list.SelectedItem);
            grouped.DuplicateStepCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Equal(7, grouped.Steps.Count);
            Assert.Equal(3, list.Items.Count);
            Assert.Same(grouped.Steps[3], list.SelectedItem);

            var brokenModel = ProfileFactory.CreateCustomProfile("Unreadable", "unreadable.exe");
            brokenModel.IsPersistenceSuspended = true;
            brokenModel.Macros.LoadError = "Unsupported macro format version. The source is preserved.";
            using var broken = new ProfileViewModel(brokenModel, new FakeDisplayService(), new RecordingColorControlService());
            host.Content = broken;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.Contains(Descendants(host).OfType<TextBlock>(), block => block.Visibility == Visibility.Visible &&
                block.ActualHeight > 0 && block.Text == brokenModel.Macros.LoadError);
            // A read-only profile locks the section switch too, so the first-macro hint stays hidden there.
            const string enableHint = "Turn on Macros above to create one.";
            Assert.DoesNotContain(Descendants(host).OfType<TextBlock>(), block => block.Text == enableHint && block.Visibility == Visibility.Visible);

            using var fresh = new ProfileViewModel(ProfileFactory.CreateCustomProfile("Fresh", "fresh.exe"),
                new FakeDisplayService(), new RecordingColorControlService());
            host.Content = fresh;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            host.UpdateLayout();
            Assert.False(fresh.Macros.IsEnabled);
            Assert.Contains(Descendants(host).OfType<TextBlock>(), block => block.Text == enableHint &&
                block.Visibility == Visibility.Visible && block.ActualHeight > 0);
        });

    private static void AssertReadableText(TextBlock text, Brush background)
    {
        var contrast = ContrastRatio(text.Foreground, background);
        Assert.True(contrast >= 4.5, $"Text '{text.Text}' has contrast {contrast:F2}:1.");
    }

    private static double ContrastRatio(Brush foreground, Brush background)
    {
        var foregroundLuminance = Luminance(Assert.IsType<SolidColorBrush>(foreground).Color);
        var backgroundLuminance = Luminance(Assert.IsType<SolidColorBrush>(background).Color);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
            (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

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
