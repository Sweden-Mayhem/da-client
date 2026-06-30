namespace Chaos.Client.Controls.Components;

/// <summary>
///     A full-window veil that swallows every mouse interaction so nothing behind it can be clicked or scrolled while
///     it is shown - e.g. the dim over the game while the off-thread profile-picture file dialog is open. Size it and
///     toggle <c>Visible</c>; being hit-testable (the default) and high-ZIndex makes it the click target, and these
///     overrides make sure the press is eaten rather than bubbling on. A modal that draws its own content over a veil
///     can subclass this to inherit the same swallowing instead of re-declaring these overrides.
/// </summary>
public class InputBlocker : UIPanel
{
    public override void OnMouseDown(MouseDownEvent e) => e.Handled = true;

    public override void OnMouseUp(MouseUpEvent e) => e.Handled = true;

    public override void OnClick(ClickEvent e) => e.Handled = true;

    public override void OnMouseScroll(MouseScrollEvent e) => e.Handled = true;
}
