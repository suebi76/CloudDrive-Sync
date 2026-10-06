using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// For the "…" buttons: a normal click opens the button's context menu below it. The menu gets the button's data, so
/// its commands act on the item the button belongs to (a synchronisation, an account, a file).
/// </summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
        "OpensMenu", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnOpensMenuChanged));

    public static bool GetOpensMenu(DependencyObject element) => (bool)element.GetValue(OpensMenuProperty);

    public static void SetOpensMenu(DependencyObject element, bool value) => element.SetValue(OpensMenuProperty, value);

    private static void OnOpensMenuChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Button button) return;
        button.Click -= Open;
        if ((bool)e.NewValue) button.Click += Open;
    }

    private static void Open(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
