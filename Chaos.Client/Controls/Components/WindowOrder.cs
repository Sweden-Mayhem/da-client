namespace Chaos.Client.Controls.Components;

/// <summary>
///     Shared front-to-back ordering for all floating windows (the <see cref="ScaleHost" />-wrapped book/group and the
///     programmatic <c>DraggableWindow</c>s). Every open or click runs <see cref="Next" /> and assigns the result to the
///     window's ZIndex, so the most recently touched window draws on top regardless of which kind it is. The base value
///     sits above the HUD/overlay layer (which uses small ZIndex values), so windows always render above the HUD.
/// </summary>
public static class WindowOrder
{
    private static int Counter = 1000;

    public static int Next() => ++Counter;
}
