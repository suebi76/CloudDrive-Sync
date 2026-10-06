using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// A list of cards whose texts and buttons screen readers can reach. WPF describes the entries of a plain item list as
/// "data items" and can keep an empty, outdated picture of them; this list lets screen readers see the cards' content
/// directly.
/// </summary>
public sealed class CardList : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
