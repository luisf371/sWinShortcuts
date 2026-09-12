using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Tests;

public sealed class CrosshairSliderTemplateTests
{
    [Fact]
    public async Task OffsetSliders_ActualXamlTemplate_LoadsWithPixelBounds()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.testdata"));
                var sliders = source.Descendants(wpf + "Slider")
                    .Where(slider => ((string?)slider.Attribute("Value"))?.StartsWith(
                        "{Binding CrosshairOffset", StringComparison.Ordinal) == true).ToArray();
                Assert.Equal(2, sliders.Length);

                // Load the actual controls through WPF's deferred template path, which compilation
                // alone does not exercise. No app startup, native hooks, or user settings are needed.
                var xaml = new XElement(wpf + "DataTemplate",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    new XAttribute(XNamespace.Xmlns + "models", "clr-namespace:sWinShortcuts.Models;assembly=sWinShortcuts"),
                    new XElement(wpf + "StackPanel", sliders));
                var template = (DataTemplate)XamlReader.Parse(xaml.ToString());
                var content = (StackPanel)template.LoadContent();
                content.Measure(new Size(400, 200));
                foreach (Slider slider in content.Children)
                {
                    Assert.Equal(-500d, slider.Minimum);
                    Assert.Equal(500d, slider.Maximum);
                }

                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
