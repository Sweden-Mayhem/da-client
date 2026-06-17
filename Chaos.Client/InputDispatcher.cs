#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client;

public sealed class InputDispatcher
{
    private const float DOUBLE_CLICK_MS = 500f;
    private const int DRAG_THRESHOLD_SQ = 16; //4px squared

    //panels that take part in keyboard dispatch
    //last element is the top, which is the active keyboard target
    private readonly List<UIPanel> ControlStack = [];

    //focus that was active before each panel was pushed
    //lets RemoveControl put it back when the panel pops instead of routing keys to a panel that takes no text
    private readonly Dictionary<UIPanel, UIElement?> StashedFocus = [];

    //the single element (usually a textbox) that gets keyboard events before the control stack
    private UIElement? ExplicitFocusElement;


    //hover state
    private UIElement? HoveredElement;

    //mouse capture state
    public UIElement? CapturedElement { get; private set; }
    private Point MouseDownPosition;
    private MouseButton MouseDownButton;

    //click synthesis
    private UIElement? LastClickTarget;
    private float LastClickTime;

    //drag state
    private bool DragActive;
    private object? DragPayload;

    //previous mouse position for delta
    private int PreviousMouseX;
    private int PreviousMouseY;

    //reused each frame to avoid allocating per frame
    //safe because dispatch is synchronous, so an event is done before the next one starts
    private readonly MouseDownEvent MouseDown = new();
    private readonly MouseUpEvent MouseUp = new();
    private readonly ClickEvent Click = new();
    private readonly DoubleClickEvent DoubleClick = new();
    private readonly MouseMoveEvent MouseMove = new();
    private readonly MouseScrollEvent MouseScroll = new();
    private readonly KeyDownEvent KeyDown = new();
    private readonly KeyUpEvent KeyUp = new();
    private readonly TextInputEvent TextInput = new();
    private readonly DragStartEvent DragStart = new();
    private readonly DragMoveEvent DragMove = new();
    private readonly DragDropEvent DragDrop = new();

    //used to drop OS key-repeat KeyDowns
    //cleared at the top of ProcessInput so the same set is reused every frame
    private readonly HashSet<Keys> DispatchedKeys = [];

    /// <summary>
    ///     Lets UI controls push or remove themselves from the control stack
    /// </summary>
    public static InputDispatcher? Instance { get; private set; }

    public InputDispatcher()
    {
        Instance = this;
        UITextBox.TextBoxFocusGained += OnTextBoxFocusGained;
    }

    /// <summary>
    ///     Sends keyboard events to the newly focused textbox by making it the explicit focus target
    /// </summary>
    private void OnTextBoxFocusGained(UITextBox textBox) => SetExplicitFocus(textBox);

    //── explicit focus ──

    /// <summary>
    ///     Current explicit focus target, usually a textbox
    ///     When set it gets keyboard events first, before the control stack
    /// </summary>
    public UIElement? ExplicitFocus => ExplicitFocusElement;

    /// <summary>The element under the cursor, or null
    ///     Hover only updates on mouse move, so a window can close while the cursor sits still over it
    ///     Return null in that case so a stale hover never leaves a tooltip stuck on screen</summary>
    public UIElement? Hovered => IsChainVisible(HoveredElement) ? HoveredElement : null;

    //true only when the element exists and it plus every ancestor is still visible
    private static bool IsChainVisible(UIElement? element)
    {
        if (element is null)
            return false;

        for (var e = element; e is not null; e = e.Parent)
            if (!e.Visible)
                return false;

        return true;
    }

    /// <summary>
    ///     The chat input textbox
    ///     Focus on it survives popups so typing is never cut off by one
    /// </summary>
    public UITextBox? ChatInputTextBox { get; set; }

    /// <summary>
    ///     The chat window that holds the chat input
    ///     While the input is focused, mouse events go to the whole window not just the input line
    ///     so the player can click the tabs, scrollback or pin
    /// </summary>
    public UIPanel? ChatInputContainer { get; set; }

    /// <summary>
    ///     True while a drag is in progress
    /// </summary>
    public bool IsDragging => DragActive;

    /// <summary>
    ///     The active drag payload, or null
    /// </summary>
    public object? ActiveDragPayload => DragPayload;

    /// <summary>
    ///     Sets explicit focus to the given element
    ///     Pass null to clear
    /// </summary>
    public void SetExplicitFocus(UIElement? element) => ExplicitFocusElement = element;

    /// <summary>
    ///     Clears explicit focus
    /// </summary>
    public void ClearExplicitFocus() => SetExplicitFocus(null);

    //── control stack ──

    /// <summary>
    ///     Pushes a panel onto the control stack
    ///     If it is already there it is moved to the top
    ///     The top panel gets keyboard events when explicit focus does not handle them
    /// </summary>
    public void PushControl(UIPanel panel)
    {
        var alreadyOnStack = ControlStack.Contains(panel);
        ControlStack.Remove(panel);
        ControlStack.Add(panel);

        UIElement? stashed = null;

        //focus follows the stack top, clear stale focus below it so keys don't leak, like escape hitting a textbox behind a popup
        //the chat input textbox is sticky though, popups must not break typing
        if (ExplicitFocusElement is not null
            && (ExplicitFocusElement != ChatInputTextBox)
            && (ExplicitFocusElement != panel)
            && !IsDescendantOf(panel, ExplicitFocusElement))
        {
            stashed = ExplicitFocusElement;
            ClearExplicitFocus();
        }

        //only stash on the first push
        //a re-push must keep the original stash so the focus from before the first push can still be restored on pop
        if (!alreadyOnStack)
            StashedFocus[panel] = stashed;
    }

    /// <summary>
    ///     Removes a panel from the control stack
    ///     If the removed panel holds the focused element, clears explicit focus
    /// </summary>
    public void RemoveControl(UIPanel panel)
    {
        ControlStack.Remove(panel);
        StashedFocus.Remove(panel, out var stashed);

        if (ExplicitFocusElement is not null && IsDescendantOf(panel, ExplicitFocusElement))
            ClearExplicitFocus();

        //prefer the focus active before this panel pushed, usually a textbox below it
        //without this the new top panel becomes the focus target and typing does nothing until you click the box again
        if (stashed is not null && IsEffectivelyVisible(stashed))
        {
            //if a textbox inside the popup grabbed IsFocused, the stashed textbox now has IsFocused false
            //re-assert it so OnTextInput, which checks IsFocused, actually runs
            if (stashed is UITextBox tb && !tb.IsFocused)
                tb.IsFocused = true;
            else
                SetExplicitFocus(stashed);

            return;
        }

        //focus follows the stack top, so after removal the new top becomes the focus target
        //when the stack is empty, leave focus clear so root-level hotkeys take over
        //don't override the chat input textbox, it owns its own focus
        if (TopControl is { } newTop
            && IsEffectivelyVisible(newTop)
            && (ExplicitFocusElement != ChatInputTextBox))
            SetExplicitFocus(newTop);
    }

    /// <summary>
    ///     The top panel on the control stack, or null if the stack is empty
    /// </summary>
    public UIPanel? TopControl => ControlStack.Count > 0 ? ControlStack[^1] : null;

    //true while the last press was swallowed by a pointer modal, so its release and click are swallowed too
    private bool ModalSwallowedPress;

    //the top stack entry when it is a visible modal, it owns the pointer
    private UIPanel? ActivePointerModal => TopControl is { IsModal: true } top && IsEffectivelyVisible(top) ? top : null;

    /// <summary>
    ///     Number of panels on the control stack
    /// </summary>
    public int ControlStackCount => ControlStack.Count;

    /// <summary>
    ///     Resets all dispatcher state so stale elements don't carry across screen changes
    /// </summary>
    public void Clear()
    {
        ControlStack.Clear();
        StashedFocus.Clear();
        ClearExplicitFocus();
        CapturedElement = null;

        DragActive = false;
        DragPayload = null;
        HoveredElement = null;
    }

    //── main per-frame entry point ──

    /// <summary>
    ///     Runs once a frame
    ///     Reads buffered input, makes events and dispatches them
    /// </summary>
    public void ProcessInput(UIPanel root, GameTime gameTime)
    {
        var totalMs = (float)gameTime.TotalGameTime.TotalMilliseconds;
        var mouseX = InputBuffer.MouseX;
        var mouseY = InputBuffer.MouseY;
        var modifiers = InputBuffer.CurrentModifiers;

        //when a textbox has focus, keep hover, move and wheel inside the panel that holds it
        //a click outside isn't swallowed, it just defocuses the box so the cursor is never trapped in a focused field
        var mouseBlocked = false;

        if (ExplicitFocusElement is not null)
        {
            //the chat input's allowed area is the whole chat window so its tabs, scrollback and pin work while typing
            //every other textbox stays restricted to its own panel
            var containingPanel = (ExplicitFocusElement == ChatInputTextBox) && (ChatInputContainer is not null)
                ? ChatInputContainer
                : FindContainingStackEntry(ExplicitFocusElement) ?? ExplicitFocusElement.Parent;

            //map the cursor through any ScaleHost ancestor so a click inside a scaled window isn't treated as outside
            //otherwise it would eat the click and block things like a drag
            if (containingPanel is not null && !ContainsMapped(containingPanel, mouseX, mouseY))
                mouseBlocked = true;
        }

        if (!mouseBlocked)
        {
            //── mouse moved, do mousemove and hover tracking ──
            if ((mouseX != PreviousMouseX) || (mouseY != PreviousMouseY))
            {
                var deltaX = mouseX - PreviousMouseX;
                var deltaY = mouseY - PreviousMouseY;
                PreviousMouseX = mouseX;
                PreviousMouseY = mouseY;

                //hit-test once for hover tracking, drag-move, and free-movement dispatch
                var hitUnderCursor = HitTest(root, mouseX, mouseY);

                if (CapturedElement is not null)
                {
                    //send mousemove to the captured element, used by scrollbar drag, text selection and so on
                    MouseMove.Reset();
                    (MouseMove.ScreenX, MouseMove.ScreenY) = MapToTarget(CapturedElement, mouseX, mouseY);
                    MouseMove.DeltaX = deltaX;
                    MouseMove.DeltaY = deltaY;
                    MouseMove.Modifiers = modifiers;
                    MouseMove.Target = CapturedElement;
                    CapturedElement.OnMouseMove(MouseMove);

                    //check if we've moved far enough to start a drag
                    if (!DragActive)
                    {
                        var dx = mouseX - MouseDownPosition.X;
                        var dy = mouseY - MouseDownPosition.Y;

                        if (((dx * dx) + (dy * dy)) >= DRAG_THRESHOLD_SQ)
                        {
                            DragStart.Reset();
                            (DragStart.ScreenX, DragStart.ScreenY) = MapToTarget(CapturedElement, mouseX, mouseY);
                            DragStart.Button = MouseDownButton;
                            DragStart.Source = CapturedElement;
                            DragStart.Target = CapturedElement;
                            CapturedElement.OnDragStart(DragStart);

                            if (DragStart.Payload is not null)
                            {
                                DragActive = true;
                                DragPayload = DragStart.Payload;
                            }
                        }
                    }

                    //dragmove goes to the element under the cursor, not the captured one
                    if (DragActive)
                    {
                        DragMove.Reset();
                        DragMove.ScreenX = mouseX;
                        DragMove.ScreenY = mouseY;
                        DragMove.Button = MouseDownButton;
                        DragMove.Payload = DragPayload;
                        DispatchBubble(hitUnderCursor ?? root, DragMove);
                    }
                } else
                {
                    //no capture, send mousemove to the hit-tested element for hover effects
                    MouseMove.Reset();
                    MouseMove.ScreenX = mouseX;
                    MouseMove.ScreenY = mouseY;
                    MouseMove.DeltaX = deltaX;
                    MouseMove.DeltaY = deltaY;
                    MouseMove.Modifiers = modifiers;
                    DispatchBubble(hitUnderCursor ?? root, MouseMove);
                }

                //hover tracking, reuse the cached hit-test result
                var newHover = hitUnderCursor;

                if (newHover != HoveredElement)
                {
                    HoveredElement?.OnMouseLeave();
                    HoveredElement = newHover;
                    HoveredElement?.OnMouseEnter();
                }
            }

        } else
        {
            //mouse is blocked, still track position so next frame's delta is right
            PreviousMouseX = mouseX;
            PreviousMouseY = mouseY;
        }

        //keyboard, mouse button and wheel all come through in true OS order, which matters for macros that mix input kinds
        //a key down comes before its character, so if a KeyDown gains focus we drop the next TextInput from the new box
        var events = InputBuffer.Events;

        var suppressNextTextInput = false;
        DispatchedKeys.Clear();

        foreach (var evt in events)
            switch (evt.Kind)
            {
                case BufferedInputKind.MouseButton:
                {
                    //a press outside the focused field's panel defocuses it then acts as a normal click
                    //the lost-focus event lets the wrapping control reset or close itself, no trap that freezes input
                    if (mouseBlocked && evt.IsPress)
                    {
                        UITextBox.Blur();
                        ClearExplicitFocus();
                        mouseBlocked = false;
                    }

                    if (!mouseBlocked)
                        ProcessMouseButton(
                            root,
                            evt.X,
                            evt.Y,
                            totalMs,
                            evt.Modifiers,
                            evt.Button,
                            evt.IsPress,
                            !evt.IsPress);

                    break;
                }

                case BufferedInputKind.MouseWheel:
                {
                    //hit-test at the cursor position the scroll happened at
                    //so a scroll that lands in a different panel than the frame-end cursor still routes right
                    if (!mouseBlocked)
                    {
                        var scrollTarget = HitTest(root, evt.X, evt.Y);

                        MouseScroll.Reset();
                        MouseScroll.ScreenX = evt.X;
                        MouseScroll.ScreenY = evt.Y;
                        MouseScroll.Delta = evt.WheelDelta;
                        MouseScroll.Modifiers = evt.Modifiers;
                        DispatchBubble(scrollTarget ?? root, MouseScroll);
                    }

                    break;
                }

                case BufferedInputKind.KeyDown:
                {
                    //OS key-repeat sends repeated key downs for one held press
                    //DispatchedKeys is cleared per key on KeyUp so a real re-press in the same frame still goes through
                    if (!DispatchedKeys.Add(evt.Key))
                        break;

                    var focusBefore = ExplicitFocusElement;

                    KeyDown.Reset();
                    KeyDown.Key = evt.Key;
                    KeyDown.Modifiers = evt.Modifiers;
                    DispatchKeyboardEvent(root, KeyDown);

                    if ((focusBefore is null) && (ExplicitFocusElement is not null))
                        suppressNextTextInput = true;

                    break;
                }

                case BufferedInputKind.KeyUp:
                {
                    DispatchedKeys.Remove(evt.Key);

                    KeyUp.Reset();
                    KeyUp.Key = evt.Key;
                    KeyUp.Modifiers = evt.Modifiers;
                    DispatchKeyboardEvent(root, KeyUp);

                    break;
                }

                case BufferedInputKind.TextInput:
                {
                    if (suppressNextTextInput)
                    {
                        suppressNextTextInput = false;

                        break;
                    }

                    TextInput.Reset();
                    TextInput.Character = evt.Character;
                    DispatchKeyboardEvent(root, TextInput);

                    break;
                }
            }
    }

    private void ProcessMouseButton(
        UIPanel root,
        int mouseX,
        int mouseY,
        float totalMs,
        KeyModifiers modifiers,
        MouseButton button,
        bool wasPressed,
        bool wasReleased)
    {
        if (wasPressed)
        {
            var target = HitTest(root, mouseX, mouseY) ?? root;

            //a modal dialog on top of the stack owns the pointer, like the travel confirm
            //a press outside it is swallowed whole so nothing behind the dialog reacts until the player answers it
            if (ActivePointerModal is { } modal && (target != modal) && !IsDescendantOf(modal, target))
            {
                CapturedElement = null;
                ModalSwallowedPress = true;

                return;
            }

            ModalSwallowedPress = false;

            //set capture
            CapturedElement = target;

            MouseDownPosition = new Point(mouseX, mouseY);
            MouseDownButton = button;

            MouseDown.Reset();
            MouseDown.ScreenX = mouseX;
            MouseDown.ScreenY = mouseY;
            MouseDown.Button = button;
            MouseDown.Modifiers = modifiers;
            DispatchBubble(target, MouseDown);
        }

        if (wasReleased)
        {
            //the release that matches a modally swallowed press is swallowed too
            //otherwise the up would hit-test fresh and leak to whatever is under the cursor
            if (ModalSwallowedPress)
            {
                ModalSwallowedPress = false;

                return;
            }

            var wasDragging = DragActive;

            //drop
            if (DragActive && (button == MouseDownButton))
            {
                var dropTarget = HitTest(root, mouseX, mouseY);

                DragDrop.Reset();
                DragDrop.ScreenX = mouseX;
                DragDrop.ScreenY = mouseY;
                DragDrop.Button = button;
                DragDrop.Payload = DragPayload;
                DragDrop.DropTarget = dropTarget;
                DispatchBubble(dropTarget ?? root, DragDrop);

                DragActive = false;
                DragPayload = null;

            }

            //mouseup goes to the captured element
            var upTarget = CapturedElement ?? HitTest(root, mouseX, mouseY) ?? root;

            MouseUp.Reset();
            MouseUp.ScreenX = mouseX;
            MouseUp.ScreenY = mouseY;
            MouseUp.Button = button;
            MouseUp.Modifiers = modifiers;
            DispatchBubble(upTarget, MouseUp);

            //only make a click if the cursor is still inside the captured element and no drag happened
            if ((CapturedElement is not null) && !wasDragging && ContainsMapped(CapturedElement, mouseX, mouseY))
            {
                Click.Reset();
                Click.ScreenX = mouseX;
                Click.ScreenY = mouseY;
                Click.Button = button;
                Click.Modifiers = modifiers;
                DispatchBubble(CapturedElement, Click);

                //double-click
                if ((CapturedElement == LastClickTarget) && ((totalMs - LastClickTime) < DOUBLE_CLICK_MS))
                {
                    DoubleClick.Reset();
                    DoubleClick.ScreenX = mouseX;
                    DoubleClick.ScreenY = mouseY;
                    DoubleClick.Button = button;
                    DoubleClick.Modifiers = modifiers;
                    DispatchBubble(CapturedElement, DoubleClick);
                    LastClickTarget = null;
                    LastClickTime = 0;
                } else
                {
                    LastClickTarget = CapturedElement;
                    LastClickTime = totalMs;
                }
            }

            //let go of capture
            CapturedElement = null;
    
        }
    }

    //── hit-test ──

    /// <summary>
    ///     Walks the element tree deepest child first, highest ZIndex first
    ///     Returns the deepest visible, enabled, hit-testable element under the cursor, or null
    /// </summary>
    public static UIElement? HitTest(UIPanel panel, int screenX, int screenY)
    {
        if (!panel.Visible || !panel.Enabled || !panel.IsHitTestVisible)
            return null;

        //sort children before hit-testing, ProcessInput runs before update so the update sort may not have run yet
        panel.EnsureChildOrder();

        //children are sorted by zindex ascending, so go in reverse for highest first
        var children = panel.Children;

        for (var i = children.Count - 1; i >= 0; i--)
        {
            var child = children[i];

            if (!child.Visible || !child.Enabled || !child.IsHitTestVisible)
                continue;

            if (child is UIPanel childPanel)
            {
                //skip panels that don't hold the cursor, children never reach beyond their parent
                if (!childPanel.ContainsPoint(screenX, screenY))
                    continue;

                //a ScaleHost draws its children scaled, so map the cursor back into the child's native space first
                //that keeps clicks lined up with the scaled visuals
                var cx = screenX;
                var cy = screenY;
                var scale = childPanel.ContentScale;

                if (scale != 1f)
                {
                    cx = childPanel.ScreenX + (int)((screenX - childPanel.ScreenX) / scale);
                    cy = childPanel.ScreenY + (int)((screenY - childPanel.ScreenY) / scale);
                }

                var hit = HitTest(childPanel, cx, cy);

                if (hit is not null)
                    return hit;
            } else if (child.ContainsPoint(screenX, screenY))
                return child;
        }

        //check the panel itself, pass-through panels only match children never themselves
        if (!panel.IsPassThrough && panel.ContainsPoint(screenX, screenY))
            return panel;

        return null;
    }

    private static readonly List<UIPanel> ScaleAncestors = [];

    /// <summary>
    ///     Maps a raw screen point into the target's native space, undoing any ScaleHost ancestor along the way
    ///     Elements inside a ScaleHost are drawn scaled but keep native coordinates
    ///     so their hit-tests, hover math and click guards must run against the unscaled point
    /// </summary>
    private static (int X, int Y) MapToTarget(UIElement? target, int x, int y)
    {
        ScaleAncestors.Clear();

        for (var p = target?.Parent; p is not null; p = p.Parent)
            if (p.ContentScale != 1f)
                ScaleAncestors.Add(p);

        //collected innermost first, apply outermost first so each scale undoes around its own native origin
        for (var i = ScaleAncestors.Count - 1; i >= 0; i--)
        {
            var p = ScaleAncestors[i];
            var s = p.ContentScale;
            x = p.ScreenX + (int)((x - p.ScreenX) / s);
            y = p.ScreenY + (int)((y - p.ScreenY) / s);
        }

        return (x, y);
    }

    private static bool ContainsMapped(UIElement element, int x, int y)
    {
        var (mx, my) = MapToTarget(element, x, y);

        return element.ContainsPoint(mx, my);
    }

    //── dispatch ──

    private static void DispatchBubble(UIElement target, InputEvent e)
    {
        e.Target = target;

        //map screen coords into the target's native space so handlers under a ScaleHost
        //such as inventory slot hover and click see coordinates that match where they were drawn
        switch (e)
        {
            case MouseEvent me:
                (me.ScreenX, me.ScreenY) = MapToTarget(target, me.ScreenX, me.ScreenY);

                break;
            case DragEvent de:
                (de.ScreenX, de.ScreenY) = MapToTarget(target, de.ScreenX, de.ScreenY);

                break;
        }

        var current = target;
        var isClickEvent = e is MouseDownEvent or MouseUpEvent or ClickEvent or DoubleClickEvent;

        while (current is not null)
        {
            DispatchSingle(current, e);

            if (e.Handled)
                return;

            //panels eat click events by default so popups and dialogs don't leak clicks to whatever is behind them
            //drag, move and scroll still bubble normally since OnRootDragDrop and similar need to reach root unhandled
            if (isClickEvent && current is UIPanel)
            {
                e.Handled = true;

                return;
            }

            current = current.Parent;
        }
    }

    /// <summary>
    ///     Two-phase keyboard dispatch
    ///     Phase 1 sends to ExplicitFocus if it is set and visible, with no bubbling, and falls through to phase 2 if unhandled
    ///     Phase 2 targets the top control or root and bubbles up normally
    /// </summary>
    private void DispatchKeyboardEvent(UIPanel root, InputEvent e)
    {
        //phase 0 is global hotkeys, always sent to root, skipping focus and the stack
        if (e is KeyDownEvent { Key: Keys.Enter, Alt: true })
        {
            DispatchSingle(root, e);

            if (e.Handled)
                return;
        }

        //phase 1 is explicit focus, single target, no bubbling
        if (ExplicitFocusElement is not null && IsEffectivelyVisible(ExplicitFocusElement))
        {
            DispatchSingle(ExplicitFocusElement, e);

            if (e.Handled)
                return;

            //phase 1.5, if the focused element is not a panel, bubble to its parent panel
            //this lets parent controls handle keys their children don't, like escape closing a text entry or chat
            if (ExplicitFocusElement is not UIPanel && ExplicitFocusElement!.Parent is { } parentPanel)
            {
                DispatchSingle(parentPanel, e);

                if (e.Handled)
                    return;
            }
        } else if (ExplicitFocusElement is not null)

            //explicit focus is no longer visible, clear it
            ClearExplicitFocus();

        //phase 2 is stack dispatch with bubbling
        var target = TopControl ?? root;
        DispatchBubble(target, e);
    }

    private static bool IsEffectivelyVisible(UIElement element)
    {
        var current = element;

        while (current is not null)
        {
            if (!current.Visible)
                return false;

            current = current.Parent;
        }

        return true;
    }

    private static void DispatchSingle(UIElement element, InputEvent e)
    {
        switch (e)
        {
            case MouseDownEvent md:
                element.OnMouseDown(md);

                break;
            case MouseUpEvent mu:
                element.OnMouseUp(mu);

                break;
            case ClickEvent click:
                element.OnClick(click);

                break;
            case DoubleClickEvent dbl:
                element.OnDoubleClick(dbl);

                break;
            case MouseMoveEvent move:
                element.OnMouseMove(move);

                break;
            case MouseScrollEvent scroll:
                element.OnMouseScroll(scroll);

                break;
            case KeyDownEvent kd:
                element.OnKeyDown(kd);

                break;
            case KeyUpEvent ku:
                element.OnKeyUp(ku);

                break;
            case TextInputEvent ti:
                element.OnTextInput(ti);

                break;
            case DragStartEvent ds:
                element.OnDragStart(ds);

                break;
            case DragMoveEvent dm:
                element.OnDragMove(dm);

                break;
            case DragDropEvent dd:
                element.OnDragDrop(dd);

                break;
        }
    }

    //── helpers ──

    /// <summary>
    ///     True if descendant is a child, grandchild and so on of ancestor
    ///     Returns false when descendant equals ancestor
    /// </summary>
    private static bool IsDescendantOf(UIPanel ancestor, UIElement descendant)
    {
        var current = descendant.Parent;

        while (current is not null)
        {
            if (current == ancestor)
                return true;

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    ///     Walks up from the element to find the nearest ancestor that is on the control stack
    /// </summary>
    private UIPanel? FindContainingStackEntry(UIElement element)
    {
        var current = element.Parent;

        while (current is not null)
        {
            if (ControlStack.Contains(current))
                return current;

            current = current.Parent;
        }

        return null;
    }

}