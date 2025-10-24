using System;
using System.Windows;
using System.Windows.Input;

namespace sWinShortcuts.Behaviors;

public static class ComboBoxKeySelectionBehavior
{
    public static readonly DependencyProperty EnableKeySelectionProperty =
        DependencyProperty.RegisterAttached(
            "EnableKeySelection",
            typeof(bool),
            typeof(ComboBoxKeySelectionBehavior),
            new PropertyMetadata(false, OnEnableKeySelectionChanged));

    public static bool GetEnableKeySelection(DependencyObject obj)
    {
        return (bool)obj.GetValue(EnableKeySelectionProperty);
    }

    public static void SetEnableKeySelection(DependencyObject obj, bool value)
    {
        obj.SetValue(EnableKeySelectionProperty, value);
    }

    private static void OnEnableKeySelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is System.Windows.Controls.ComboBox comboBox)
        {
            if ((bool)e.NewValue)
            {
                comboBox.PreviewKeyDown += OnComboBoxPreviewKeyDown;
                comboBox.DropDownOpened += OnDropDownOpened;
            }
            else
            {
                comboBox.PreviewKeyDown -= OnComboBoxPreviewKeyDown;
                comboBox.DropDownOpened -= OnDropDownOpened;
            }
        }
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox comboBox)
        {
            comboBox.Focus();
        }
    }

    private static void OnComboBoxPreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || !comboBox.IsDropDownOpen)
        {
            return;
        }

        // Get the key that was pressed
        var pressedKey = e.Key;
        
        // Check if this is a valid Key enum value
        if (Enum.IsDefined(typeof(Key), pressedKey))
        {
            // Search through ItemsSource for a matching key
            foreach (var item in comboBox.ItemsSource)
            {
                // Handle nullable Key?
                if (item is Key key && key == pressedKey)
                {
                    comboBox.SelectedItem = item;
                    comboBox.IsDropDownOpen = false;
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}