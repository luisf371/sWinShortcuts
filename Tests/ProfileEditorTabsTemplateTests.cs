using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using sWinShortcuts.Factories;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class ProfileEditorTabsTemplateTests
{
    // Regression: binding the profile switch to each TabItem stopped shading the pages, because a TabControl
    // presents the selected content outside its TabItem and the page never inherits the item's IsEnabled.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProfileOff_ShadesEveryTabPage_WhileTabsAndScrollingStayAvailable(bool windowsProfile) =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var host = LoadProfileTabs();
            var model = windowsProfile ? ProfileFactory.CreateWindowsProfile() : ProfileFactory.CreateCustomProfile("Game", "game.exe");
            using var profile = new ProfileViewModel(model, new FakeDisplayService(), new RecordingColorControlService());
            host.Content = profile;
            host.ContentTemplate = (DataTemplate)host.Resources["ProfileTabs"];
            host.Measure(new Size(900, 700));
            host.Arrange(new Rect(0, 0, 900, 700));
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var tabs = Assert.Single(Descendants(host).OfType<TabControl>());
            // Macros gates its own body and keeps Stop recording reachable; its template test covers that.
            var pages = tabs.Items.OfType<TabItem>()
                .Where(item => item.Visibility == Visibility.Visible && (item.Header as string) != "Macros").ToArray();
            Assert.Equal(windowsProfile ? 3 : 4, pages.Length);

            foreach (var enabled in new[] { true, false, true })
            {
                profile.IsEnabled = enabled;
                foreach (var page in pages)
                {
                    tabs.SelectedItem = page;
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    host.UpdateLayout();
                    var scroller = Assert.IsType<ScrollViewer>(page.Content);
                    var body = Assert.IsType<StackPanel>(scroller.Content);
                    Assert.Equal(enabled, body.IsEnabled);
                    Assert.Equal(enabled ? 1d : 0.6, body.Opacity);
                    // A disabled profile stays reviewable: every tab opens and its page still scrolls.
                    Assert.True(scroller.IsEnabled);
                    Assert.True(page.IsEnabled);
                }
            }
        });

    // The actual tab strip with the app's resources in one lexical scope, as MacroEditorTemplateTests loads the
    // Macros tab: no Application, hooks, or user settings are initialized.
    private static ContentControl LoadProfileTabs()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.testdata"));
        var brushes = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Brushes.xaml.testdata"));
        var styles = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Styles.xaml.testdata"));
        var tabs = Assert.Single(source.Descendants(wpf + "TabControl"),
            control => (string?)control.Attribute("Style") == "{StaticResource SettingsTabControlStyle}");
        var namespaces = source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration)
            .Select(attribute => new XAttribute(attribute.Name,
                attribute.Value.StartsWith("clr-namespace:sWinShortcuts.", StringComparison.Ordinal)
                    ? attribute.Value + ";assembly=sWinShortcuts" : attribute.Value));
        var xaml = new XElement(wpf + "ContentControl", namespaces,
            new XElement(wpf + "ContentControl.Resources",
                new XElement(wpf + "ResourceDictionary",
                    brushes.Root!.Elements().Select(element => new XElement(element)),
                    styles.Root!.Elements().Select(element => new XElement(element)),
                    source.Root.Element(wpf + "Window.Resources")!.Elements().Select(element => new XElement(element)),
                    new XElement(wpf + "DataTemplate", new XAttribute(x + "Key", "ProfileTabs"), new XElement(tabs)))));
        static bool IsAppNamespace(string name) => name.StartsWith("clr-namespace:sWinShortcuts.", StringComparison.Ordinal) &&
            !name.Contains(";assembly=", StringComparison.Ordinal);
        foreach (var element in xaml.Descendants())
        {
            if (IsAppNamespace(element.Name.NamespaceName))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=sWinShortcuts");
            // Attached properties such as behaviors:ComboBoxKeySelectionBehavior.EnableKeySelection need the same.
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration &&
                IsAppNamespace(attribute.Name.NamespaceName)).ToList())
            {
                attribute.Remove();
                element.Add(new XAttribute(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=sWinShortcuts"),
                    attribute.Value));
            }
        }
        // XamlReader's runtime markup-extension scanner rejects a quote after the {} escape, as in
        // StringFormat={}{0:+0 '%'}, which the build-time compiler accepts. Those display-only labels do not
        // affect gating, so the harness drops them.
        foreach (var attribute in xaml.Descendants().Attributes()
            .Where(attribute => attribute.Value.Contains("StringFormat={}", StringComparison.Ordinal) && attribute.Value.Contains('\''))
            .ToList())
            attribute.Remove();
        var host = (ContentControl)XamlReader.Parse(xaml.ToString());
        host.FontFamily = new FontFamily("Segoe UI");
        host.FontSize = 14;
        return host;
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
