#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Models;
using Chaos.Client.Rendering;
using Chaos.Client.Rendering.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud.Panel.Slots;

/// <summary>
///     A single slot in an icon grid panel (inventory, skill book, spell book). Extends UIButton with cooldown overlay
///     rendering, double-click detection, and drag-and-drop support. The parent panel creates one PanelSlotControl per
///     visible grid cell and manages layout, slot assignment, and drag state.
/// </summary>
public class PanelSlot : UIButton
{
    private bool DoubleClickFired;
    private bool _isSlotHovered;

    internal bool IsSlotHovered
    {
        get => _isSlotHovered;
        set
        {
            _isSlotHovered = value;

            if (value && NormalTexture is not null)
            {
                HoverTexture?.Dispose();
                HoverTexture = ImageUtil.BuildWhiteTinted(NormalTexture.GraphicsDevice, NormalTexture);
            } else
            {
                HoverTexture?.Dispose();
                HoverTexture = null;
            }
        }
    }

    private new Texture2D? HoverTexture;

    public new Texture2D? NormalTexture
    {
        get => base.NormalTexture;
        set
        {
            base.NormalTexture = value;
            HoverTexture?.Dispose();
            HoverTexture = null;
        }
    }

    /// <summary>
    ///     Cooldown progress from 0 (fully cooled down) to 1 (just started, fully on cooldown).
    /// </summary>
    public float CooldownPercent { get; set; }

    /// <summary>Seconds of cooldown remaining (drives the "Ns" text drawn on the icon). 0 = ready.</summary>
    public float CooldownSeconds { get; set; }

    //the dark-blue "still cooling" meter drawn over the remaining-cooldown portion of a Progressive slot
    private static readonly Color CooldownMeterColor = new Color(10, 22, 70) * 0.66f;

    /// <summary>
    ///     How the cooldown overlay is rendered.
    /// </summary>
    public CooldownStyle CooldownStyle { get; set; }

    /// <summary>
    ///     Blue-tinted copy of the normal icon used as the cooldown overlay. For <see cref="CooldownStyle.Progressive" />
    ///     this is progressively revealed top-to-bottom over the grey base; for <see cref="CooldownStyle.Swap" /> it
    ///     replaces the normal icon entirely for the duration.
    /// </summary>
    public Texture2D? CooldownTexture { get; set; }

    public int CurrentDurability { get; set; }

    /// <summary>
    ///     Grey-tinted copy of the normal icon shown underneath <see cref="CooldownStyle.Progressive" /> cooldowns.
    /// </summary>
    public Texture2D? GreyTexture { get; set; }

    public bool IsDropTarget { get; set; }

    public int MaxDurability { get; set; }

    /// <summary>
    ///     Stack count for a stackable item. Rendered as a small grey "xN" in the lower-right corner when greater than 1
    ///     (0/1 shows nothing). Only inventory slots set this; skills/spells don't stack.
    /// </summary>
    public int StackCount { get; set; }

    /// <summary>
    ///     The 1-based slot number this control represents.
    /// </summary>
    public byte Slot { get; init; }

    /// <summary>
    ///     Display name of the item/skill/spell in this slot. Used for hover tooltips by the parent.
    /// </summary>
    public string? SlotName { get; set; }

    public override void Dispose()
    {
        CooldownTexture?.Dispose();
        CooldownTexture = null;
        GreyTexture?.Dispose();
        GreyTexture = null;
        HoverTexture?.Dispose();
        HoverTexture = null;

        base.Dispose();
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        base.Draw(spriteBatch);

        if (IsDropTarget)
        {
            DrawBorder(
                spriteBatch,
                new Rectangle(
                    ScreenX,
                    ScreenY,
                    Width,
                    Height),
                Color.Black);

            DrawBorder(
                spriteBatch,
                new Rectangle(
                    ScreenX + 1,
                    ScreenY + 1,
                    Width - 2,
                    Height - 2),
                Color.Black);

            DrawBorder(
                spriteBatch,
                new Rectangle(
                    ScreenX + 2,
                    ScreenY + 2,
                    Width - 4,
                    Height - 4),
                Color.Black);
        }

        //icon rendering with cooldown overlay
        var icon = NormalTexture;

        if (icon is null)
            return;

        DrawRectClipped(spriteBatch, new Rectangle(ScreenX + 1, ScreenY + 1, Width - 2, Height - 2), Color.Black * 0.5f);

        var pos = new Vector2(ScreenX, ScreenY);

        //Progressive draws its own meter over the plain icon and needs no pre-tinted texture; only Swap requires one
        if ((CooldownPercent > 0) && ((CooldownStyle == CooldownStyle.Progressive) || (CooldownTexture is not null)))
            switch (CooldownStyle)
            {
                case CooldownStyle.Swap:
                    DrawTexture(
                        spriteBatch,
                        CooldownTexture,
                        pos,
                        Color.White);

                    break;

                case CooldownStyle.Progressive:
                    //the full-color icon stays visible (recognizable), with a DARK-BLUE meter over the still-cooling
                    //portion that DRAINS like a liquid level: bottom-anchored, its height = remaining fraction, so the
                    //dark level drops as the cooldown counts to 0 and the icon clears from the top down. The remaining
                    //seconds are drawn on the icon by the parent's native-text pass (PanelBase.DrawNativeText).
                    DrawTexture(spriteBatch, icon, pos, Color.White);

                    var fillH = (int)Math.Ceiling(Height * CooldownPercent);

                    if (fillH > 0)
                        DrawRectClipped(spriteBatch, new Rectangle(ScreenX, ScreenY + Height - fillH, Width, fillH), CooldownMeterColor);

                    break;

                default:
                    DrawTexture(
                        spriteBatch,
                        icon,
                        pos,
                        Color.White);

                    break;
            }
        else
            DrawTexture(
                spriteBatch,
                icon,
                pos,
                Color.White);

        if (_isSlotHovered && HoverTexture is not null)
            DrawTexture(spriteBatch, HoverTexture, pos, Color.White * 0.25f);

        //the stack count ("xN") is painted by the parent grid's DrawNativeText (TTF at native resolution), not here
    }

    /// <summary>
    ///     Fired on double-click when the slot is occupied. Parameter is the 1-based slot number.
    /// </summary>
    public event PanelSlotDoubleClickedHandler? DoubleClicked;

    /// <summary>
    ///     Fired on single left-click regardless of whether the slot is empty. Parameter is the 1-based slot number.
    /// </summary>
    public event PanelSlotDoubleClickedHandler? SingleClicked;

    /// <summary>
    ///     Fired when a drag begins on this slot. Parameter is the 1-based slot number.
    /// </summary>
    public event PanelSlotDragStartedHandler? DragStarted;

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (e.Button == MouseButton.Left)
            DoubleClickFired = false;

        base.OnMouseDown(e);
    }

    public override void OnClick(ClickEvent e)
    {
        if (e.Button == MouseButton.Left)
        {
            SingleClicked?.Invoke(Slot);
            e.Handled = true;
        }
    }

    public override void OnDoubleClick(DoubleClickEvent e)
    {
        if ((e.Button == MouseButton.Left) && NormalTexture is not null && (CooldownPercent <= 0))
        {
            DoubleClicked?.Invoke(Slot);
            DoubleClickFired = true;
            e.Handled = true;
        }
    }

    public override void OnDragStart(DragStartEvent e)
    {
        if (NormalTexture is null || (CooldownPercent > 0) || DoubleClickFired)
            return;

        e.Payload = new SlotDragPayload
        {
            Source = this,
            SlotIndex = Slot,
            SourcePanel = (Parent as PanelBase)?.Tab ?? default
        };

        DragStarted?.Invoke(this);
    }

    public override void OnDragMove(DragMoveEvent e)
    {
        if (e.Payload is SlotDragPayload payload && (payload.Source.Parent == Parent))
            IsDropTarget = true;
    }

    public override void OnMouseLeave()
    {
        base.OnMouseLeave();
        IsDropTarget = false;
        (Parent as PanelBase)?.ForceHoverExit();
    }

    public override void OnDragDrop(DragDropEvent e)
    {
        IsDropTarget = false;

        if (e.Payload is not SlotDragPayload payload)
            return;

        //only accept drops from slots within the same parent panel
        if (Parent is not PanelBase panel || (payload.Source.Parent != Parent))
            return;

        //dropping on the same slot is a no-op, just end drag
        if (payload.SlotIndex == Slot)
        {
            panel.EndDrag();
            e.Handled = true;

            return;
        }

        panel.CompleteDragSwap(Slot);
        e.Handled = true;
    }

}