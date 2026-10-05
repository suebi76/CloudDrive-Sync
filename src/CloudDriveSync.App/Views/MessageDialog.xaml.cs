using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>A question or a message in CloudDrive-Sync's own style (instead of the old Windows message box).</summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.None;

    private MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>Empty texts hide the button. With <paramref name="danger"/>, Enter does not confirm.</summary>
    public static DialogChoice Show(Window? owner, string title, string text, string primary, string? secondary, string close, bool danger)
    {
        var dialog = new MessageDialog();
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Heading.Text = title;
        dialog.Body.Text = text;
        Setup(dialog.PrimaryButton, primary);
        Setup(dialog.SecondaryButton, secondary);
        Setup(dialog.CloseButton, close);
        dialog.PrimaryButton.IsDefault = !danger;
        if (string.IsNullOrEmpty(close)) dialog.PrimaryButton.IsCancel = true;
        dialog.ShowDialog();
        return dialog._choice;
    }

    private static void Setup(System.Windows.Controls.Button button, string? text)
    {
        button.Content = text;
        button.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Primary;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Secondary;
        Close();
    }
}
