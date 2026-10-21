using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Tack.Controls;

/// <summary>
/// A StackPanel that screen readers see as a named group (panels are otherwise invisible to them),
/// for a set of radio buttons such as the spaces strip.
/// </summary>
public sealed class GroupPanel : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private sealed class GroupPeer(GroupPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(GroupPanel);
    }
}
