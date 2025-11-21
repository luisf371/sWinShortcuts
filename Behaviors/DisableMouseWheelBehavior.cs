using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace sWinShortcuts.Behaviors;

public static class DisableMouseWheelBehavior
{
    public static readonly DependencyProperty DisableMouseWheelProperty =
        DependencyProperty.RegisterAttached(
            "DisableMouseWheel",
            typeof(bool),
            typeof(DisableMouseWheelBehavior),
            new PropertyMetadata(false, OnDisableMouseWheelChanged));

    // Internal property to keep track of the registered handler so we can remove it
    private static readonly DependencyProperty MouseWheelHandlerProperty =
        DependencyProperty.RegisterAttached(
            "MouseWheelHandler",
            typeof(MouseWheelEventHandler),
            typeof(DisableMouseWheelBehavior),
            new PropertyMetadata(null));

    public static bool GetDisableMouseWheel(DependencyObject obj)
    {
        return (bool)obj.GetValue(DisableMouseWheelProperty);
    }

    public static void SetDisableMouseWheel(DependencyObject obj, bool value)
    {
        obj.SetValue(DisableMouseWheelProperty, value);
    }

    private static void OnDisableMouseWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            // Register handler even for events that were already marked as handled
            var handler = new MouseWheelEventHandler(OnPreviewMouseWheel);
            element.SetValue(MouseWheelHandlerProperty, handler);
            element.AddHandler(UIElement.PreviewMouseWheelEvent, handler, true);
        }
        else
        {
            if (element.GetValue(MouseWheelHandlerProperty) is MouseWheelEventHandler handler)
            {
                element.RemoveHandler(UIElement.PreviewMouseWheelEvent, handler);
                element.ClearValue(MouseWheelHandlerProperty);
            }
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // If the dropdown is open, allow normal ComboBox scrolling
        if (sender is System.Windows.Controls.ComboBox comboBox && comboBox.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;

        var eventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };

        if (sender is DependencyObject dependencyObject)
        {
            var scrollViewer = FindParentScrollViewer(dependencyObject);
            scrollViewer?.RaiseEvent(eventArgs);
        }
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        ScrollViewer? result = null;
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                result = scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return result;
    }
}
