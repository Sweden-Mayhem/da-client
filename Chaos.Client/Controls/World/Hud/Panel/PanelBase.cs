#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Data.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud.Panel;

/// <summary>
///     Base class for icon grid panels (inventory, skill book, spell book). Creates a grid of
///     <see cref="PanelSlot" /> children and manages slot assignment, drag-and-drop, and
///     the dragged icon ghost. Subclasses provide icon rendering.
/// </summary>
public abstract class PanelBase : ExpandablePanel, INativeTextDrawer
{
    protected const int ICON_SIZE = 32;
    protected const int CELL_WIDTH = 36;
    protected const int CELL_HEIGHT = 33;
    protected const int DEFAULT_COLUMNS = 12;
    protected const int DEFAULT_VISIBLE_SLOTS = 36;

    private static Texture2D? SlotNumberOverlay;

    //compact padding (large hud 1-row backgrounds need extra y before the grid)
    private readonly int CompactGridPadding;
    private readonly bool DrawSlotNumberOverlay;

    protected readonly int GridOffsetX;
    protected readonly int GridOffsetY;
    protected readonly int MaxSlots;
    protected readonly int NormalVisibleSlots;
    protected readonly int SlotOffset;

    /// <summary>
    ///     Number of grid columns. Defaults to 12 (standard inventory/skill/spell book layout). Sub-panels (e.g. the
    ///     H tab world skills/spells halves) may use a smaller value.
    /// </summary>
    protected int Columns { get; }

    /// <summary>
    ///     The slot controls owned by this panel, in cell order. Made available so wiring code (right-click handlers,
    ///     etc.) can go through each actual slot without guessing the slot-number window. Read-only, the
    ///     panel constructs the array once and never replaces or shuffles elements.
    /// </summary>
    public IReadOnlyList<PanelSlot> Slots { get; }

    /// <summary>
    ///     Optional per-slot key labels drawn at the top of each cell instead of the number sprite strip (e.g. "F1".. on
    ///     the inventory hotbar, "S+1".. on the spell hotbar). Null keeps the default 1..= number sprite.
    /// </summary>
    public string[]? SlotLabels { get; set; }

    //hotbar key labels: orange (matching the orange-bar message convention) over a black 1px outline plus a 50% drop
    //shadow further down, so they read over any slot art.
    private static readonly Color SlotLabelColor = Color.Orange;
    private static readonly Color SlotLabelShadowColor = Color.Black;
    private const int SlotLabelShadowOffsetY = 2; //drop shadow distance, further down than the 1px outline
    private const int SLOT_LABEL_FONT = 11; //TrueType pixel size for the per-slot key hint, painted natively at any scale
    private const int STACK_COUNT_FONT = 10; //TrueType pixel size for the "xN" stack count, painted natively at any scale
    private const int COOLDOWN_FONT = 15; //TrueType pixel size for the remaining-cooldown seconds drawn on a cooling icon

    private int DragMouseX;

    //drag state
    protected PanelSlot? DragSource { get; private set; }

    //expand state (slot-specific)
    private int ExpandedVisibleSlots;

    //hover tracking
    private PanelSlot? LastHoveredSlot;
    public PanelSlot? HoveredSlot => LastHoveredSlot;

    /// <summary>
    ///     Current mouse Y during drag.
    /// </summary>
    public int DragY { get; private set; }

    /// <summary>
    ///     True when the user is actively dragging a slot icon.
    /// </summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    ///     The HUD tab this panel belongs to. Set by the HUD control during tab registration.
    /// </summary>
    public HudTab Tab { get; set; }

    /// <summary>
    ///     The number of currently visible grid slots.
    /// </summary>
    public int VisibleSlotCount { get; protected set; }

    /// <summary>
    ///     The 1-based slot number being dragged, or 0 if not dragging.
    /// </summary>
    public byte DragSlot => DragSource?.Slot ?? 0;

    /// <summary>
    ///     The texture of the currently dragged icon, or null.
    /// </summary>
    public Texture2D? DragTexture => IsDragging ? DragSource?.NormalTexture : null;

    /// <summary>
    ///     Current mouse X during drag.
    /// </summary>
    public int DragX => DragMouseX;

    static private Texture2D? SideButtonNormalTexture;
    static private Texture2D? SideButtonPressedTexture;

    protected PanelBase(
        ControlPrefabSet hudPrefabSet,
        int maxSlots,
        CooldownStyle cooldownStyle = CooldownStyle.None,
        int slotOffset = 0,
        int columns = DEFAULT_COLUMNS,
        int? cellCount = null,
        int gridOffsetX = 8,
        int gridOffsetY = 6,
        Texture2D? background = null,
        int normalVisibleSlots = DEFAULT_VISIBLE_SLOTS,
        bool drawSlotNumberOverlay = true,
        bool loadFallbackBackground = true,
        int? compactGridPadding = null,
        ClickedHandler? sideButtonAction = null,
        Func<String?>? sideButtonTooltipProvider = null)
    {
        MaxSlots = maxSlots;
        GridOffsetX = gridOffsetX;
        NormalVisibleSlots = normalVisibleSlots;
        VisibleSlotCount = normalVisibleSlots;
        SlotOffset = slotOffset;
        Columns = columns;
        DrawSlotNumberOverlay = drawSlotNumberOverlay;

        if (background is not null)
            Background = background;
        else if (loadFallbackBackground && hudPrefabSet.Contains("InventoryBackground"))
        {
            //fallback path used by panels that share the prefab set's default inventory background.
            //sub-panels of a composite (e.g. ToolsPanel children) opt out via loadFallbackBackground=false
            //so they don't pick up the wrong art over their parent's background.
            var prefab = hudPrefabSet["InventoryBackground"];

            if (prefab.Images.Count > 0)
                Background = UiRenderer.Instance!.GetPrefabTexture(hudPrefabSet.Name, "InventoryBackground", 0);
        }

        //large hud compact (1-row) LivingInventoryBackground has +4 empty pixels at the top that the grid
        //must clear. auto-detection: a panel with its OWN background that is shorter than default (< 36 slots)
        //is treated as compact. sub-panels of a composite (e.g. ToolsPanel children) have no background of
        //their own, so the auto-detect returns 0, the parent composite must then pass an explicit
        //compactGridPadding when the parent's background is compact, so the children's initial y is bumped
        //by 4 AND the same 4px is reversed on expand (see SetExpanded below).
        CompactGridPadding = compactGridPadding ?? ((Background is not null) && (normalVisibleSlots < DEFAULT_VISIBLE_SLOTS) ? 4 : 0);
        GridOffsetY = gridOffsetY + CompactGridPadding;

        SlotNumberOverlay ??= UiRenderer.Instance!.GetSpfTexture("_ninvn.spf");

        //set panel dimensions from background or grid bounds so hit-testing works
        if (Background is not null)
        {
            Width = Background.Width;
            Height = Background.Height;
        }

        if (SideButtonNormalTexture is null || SideButtonPressedTexture is null)
        {
            SideButtonNormalTexture = ChaosGame.LoadTextureResource("panelBase_sideButton.png");
            SideButtonPressedTexture = ChaosGame.LoadTextureResource("panelBase_sideButton_pressed.png");
        }

        if (sideButtonAction is not null)
        {
            var sideButton = UIButton.CreateWithTexture(String.Empty, SideButtonNormalTexture, SideButtonPressedTexture);
            sideButton.X = Width;
            sideButton.TooltipProvider = sideButtonTooltipProvider;
            sideButton.Clicked += sideButtonAction;
            AddChild(sideButton);
            Width += sideButton.Width;
        }

        //slot count: explicit cellCount overrides the computed value
        var totalSlots = cellCount ?? Math.Min(maxSlots - SlotOffset, maxSlots);
        var slots = new PanelSlot[totalSlots];

        for (var i = 0; i < totalSlots; i++)
        {
            var slotIndex = SlotOffset + i;

            var col = i % Columns;
            var row = i / Columns;

            // ReSharper disable once VirtualMemberCallInConstructor
            var slot = CreateSlot((byte)(slotIndex + 1), $"Slot{slotIndex}", cooldownStyle);
            slot.X = GridOffsetX + col * CELL_WIDTH;
            slot.Y = GridOffsetY + row * CELL_HEIGHT;
            slot.Width = ICON_SIZE;
            slot.Height = ICON_SIZE;
            slot.Visible = i < normalVisibleSlots;

            slot.DoubleClicked += s => OnSlotClicked?.Invoke(s);
            slot.SingleClicked += s => OnSlotSingleClicked?.Invoke(s);
            slot.DragStarted += OnDragStarted;

            slots[i] = slot;
            AddChild(slot);
        }

        Slots = slots;
    }

    public virtual void ClearSlot(byte slot)
    {
        var control = FindSlot(slot);

        if (control is null)
            return;

        control.NormalTexture?.Dispose();
        control.NormalTexture = null;
        control.SlotName = null;
        control.CooldownPercent = 0;
        control.CurrentDurability = 0;
        control.MaxDurability = 0;
    }

    /// <summary>
    ///     Configures expand support for this panel with a specific expanded slot count.
    /// </summary>
    public void ConfigureExpand(Texture2D? expandedBackground, int expandedVisibleSlots)
    {
        ExpandedVisibleSlots = expandedVisibleSlots;

        ConfigureExpand(expandedBackground);
    }

    protected virtual PanelSlot CreateSlot(byte slotNumber, string name, CooldownStyle cooldownStyle)
        => new()
        {
            Name = name,
            Slot = slotNumber,
            CooldownStyle = cooldownStyle
        };

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        //expandablepanel.draw handles expanded background + children, or normal background + children
        base.Draw(spriteBatch);

        //custom per-slot KEY HINTS (F1.. inventory, S+1.. spells) are TEXT and are painted by DrawNativeText at native
        //resolution (crisp at any window scale). Only the sprite-based number strip (skill slots, no custom labels) is
        //drawn here as art, since it scales like the rest of the pixel-art panel.
        if (DrawSlotNumberOverlay && (SlotLabels is null) && (SlotNumberOverlay is not null))
        {
            var overlayY = IsExpanded ? ScreenY + 3 : ScreenY + 3 + CompactGridPadding;
            DrawTexture(spriteBatch, SlotNumberOverlay, new Vector2(ScreenX - 17, overlayY), Color.White);
        }
    }

    /// <summary>
    ///     Paints the slot grid's TEXT - per-slot key hints (F1.., S+1..) and inventory stack counts ("xN") - in TTF at
    ///     native resolution. Called by WorldScreen's generic menu-text pass with the wrapping ScaleHost's screen origin +
    ///     scale (or 0/0/1 when un-magnified), so the text stays crisp whether the panel is a 1x hotbar or a 1.5x book.
    /// </summary>
    public void DrawNativeText(SpriteBatchEx spriteBatch, int screenOriginX, int screenOriginY, int nativeOriginX, int nativeOriginY, float scale, float alpha)
    {
        if (!TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f))
            return;

        int MapX(float sx) => screenOriginX + (int)((sx - nativeOriginX) * scale);
        int MapY(float sy) => screenOriginY + (int)((sy - nativeOriginY) * scale);

        var step = Math.Max(1, (int)MathF.Round(scale)); //1px outline/shadow/margin, scaled to screen pixels

        //key hints: 50% drop shadow, black 8-direction outline, orange fill - back to front
        if (DrawSlotNumberOverlay && (SlotLabels is not null))
        {
            var keyFont = Math.Max(1, (int)MathF.Round(SLOT_LABEL_FONT * scale));
            var overlayYn = IsExpanded ? ScreenY + 3 : ScreenY + 3 + CompactGridPadding;
            var shadowDown = Math.Max(1, (int)MathF.Round(SlotLabelShadowOffsetY * scale * 0.5f));
            //the black border hugs the glyphs tightly (a single screen pixel) instead of growing with the window scale,
            //so the I/K/P book key hints keep a thin, close outline at 1.5x/2x instead of a fat one.
            const int outlineStep = 1;

            for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count) && (i < SlotLabels.Length); i++)
            {
                if (SlotLabels[i].Length == 0)
                    continue;

                var tex = TtfTextRenderer.GetLine(SlotLabels[i], keyFont);

                if (tex is null)
                    continue;

                var pos = new Vector2(MapX(Slots[i].ScreenX), MapY(overlayYn));

                spriteBatch.Draw(tex, pos + new Vector2(0, shadowDown), SlotLabelShadowColor * (0.5f * alpha));

                for (var oy = -1; oy <= 1; oy++)
                    for (var ox = -1; ox <= 1; ox++)
                        if ((ox != 0) || (oy != 0))
                            spriteBatch.Draw(tex, pos + new Vector2(ox * outlineStep, oy * outlineStep), SlotLabelShadowColor * alpha);

                spriteBatch.Draw(tex, pos, SlotLabelColor * alpha);
            }
        }

        //stack counts: grey "xN" over a 1px black shadow, right-aligned in the slot's lower-right corner
        var stackFont = Math.Max(1, (int)MathF.Round(STACK_COUNT_FONT * scale));

        for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count); i++)
        {
            var slot = Slots[i];

            if (!slot.Visible || (slot.StackCount <= 1))
                continue;

            var tex = TtfTextRenderer.GetLine($"x{slot.StackCount}", stackFont);

            if (tex is null)
                continue;

            var pos = new Vector2(
                MapX(slot.ScreenX + slot.Width) - tex.Width - step,
                MapY(slot.ScreenY + slot.Height) - tex.Height - step);

            spriteBatch.Draw(tex, pos + new Vector2(step, step), Color.Black * alpha);
            spriteBatch.Draw(tex, pos, new Color(200, 200, 200) * alpha);
        }

        //unidentified marker: a gold "?" in the lower-right corner (where the stack count sits - unidentified items are
        //gear, which never stacks, so the two never appear on the same slot). Shared SlotBadge so the equipment paper-doll
        //draws the identical mark.
        for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count); i++)
        {
            var slot = Slots[i];

            if (slot.Visible && slot.Unidentified)
                SlotBadge.DrawUnidentified(spriteBatch, MapX(slot.ScreenX + slot.Width), MapY(slot.ScreenY + slot.Height), scale, alpha);
        }

        //remaining-cooldown seconds, centered on each cooling icon: white text, 8-dir black outline so it reads over the
        //dark-blue meter. ">=1s" shows whole seconds (rounded up); under a second shows one decimal (e.g. "0.4").
        var cdFont = Math.Max(1, (int)MathF.Round(COOLDOWN_FONT * scale));

        for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count); i++)
        {
            var slot = Slots[i];

            if (!slot.Visible || (slot.CooldownPercent <= 0) || (slot.CooldownSeconds <= 0.001f))
                continue;

            var secs = slot.CooldownSeconds;
            var text = secs >= 1f ? ((int)MathF.Ceiling(secs)).ToString() : secs.ToString("0.0");

            var tex = TtfTextRenderer.GetLine(text, cdFont);

            if (tex is null)
                continue;

            var pos = new Vector2(
                MapX(slot.ScreenX + (slot.Width / 2f)) - (tex.Width / 2f),
                MapY(slot.ScreenY + (slot.Height / 2f)) - (tex.Height / 2f));

            for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                    if ((ox != 0) || (oy != 0))
                        spriteBatch.Draw(tex, pos + new Vector2(ox, oy), Color.Black * alpha);

            spriteBatch.Draw(tex, pos, Color.White * alpha);
        }
    }

    /// <summary>
    ///     Finds the PanelSlotControl for a 1-based slot number, or null if out of range or not visible.
    /// </summary>
    protected PanelSlot? FindSlot(byte slot)
    {
        var index = slot - 1;

        if ((index < SlotOffset) || (index >= (SlotOffset + Slots.Count)))
            return null;

        var gridIndex = index - SlotOffset;

        return gridIndex < Slots.Count ? Slots[gridIndex] : null;
    }

    /// <summary>
    ///     Forces a hover exit event and clears the tracked hover state. Call when the panel is hidden while a slot is still
    ///     hovered.
    /// </summary>
    public void ForceHoverExit()
    {
        if (LastHoveredSlot is null)
            return;

        LastHoveredSlot.IsSlotHovered = false;
        LastHoveredSlot = null;
        OnSlotHoverExit?.Invoke();
    }

    /// <summary>
    ///     Returns the 1-based slot number at the given screen coordinates, or null if no slot is at that position.
    /// </summary>
    public byte? GetSlotAtPosition(int mouseX, int mouseY)
    {
        for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count); i++)
            if (Slots[i]
                    .ContainsPoint(mouseX, mouseY)
                && Slots[i].NormalTexture is not null)
                return Slots[i].Slot;

        return null;
    }

    /// <summary>
    ///     Returns the PanelSlotControl for a 1-based slot number, or null if out of range or not visible.
    /// </summary>
    public PanelSlot? GetSlotControl(byte slot) => FindSlot(slot);

    protected void OnDragStarted(PanelSlot source)
    {
        DragSource = source;
        IsDragging = true;
    }

    /// <summary>
    ///     Updates the ghost icon position during a drag. Called from the root-level DragMove handler.
    /// </summary>
    public void UpdateDragPosition(int screenX, int screenY)
    {
        DragMouseX = screenX;
        DragY = screenY;
    }

    /// <summary>
    ///     Raises <see cref="OnSlotSwapped" /> from a subclass - the inventory's gold bag trades places with real
    ///     items (the item moves into the bag's empty slot server-side) without being a draggable slot itself.
    /// </summary>
    protected void RaiseSlotSwapped(byte sourceSlot, byte targetSlot) => OnSlotSwapped?.Invoke(sourceSlot, targetSlot);

    /// <summary>
    ///     Completes a drag-and-drop onto another slot within this panel.
    /// </summary>
    public virtual void CompleteDragSwap(byte targetSlot)
    {
        if (!IsDragging || DragSource is null)
            return;

        var sourceSlot = DragSource.Slot;
        EndDrag();
        OnSlotSwapped?.Invoke(sourceSlot, targetSlot);
    }

    /// <summary>
    ///     Completes a drag-and-drop outside the panel (into the world viewport).
    /// </summary>
    public void CompleteDragOutside(int screenX, int screenY)
    {
        if (!IsDragging || DragSource is null)
            return;

        var sourceSlot = DragSource.Slot;
        EndDrag();
        OnSlotDroppedOutside?.Invoke(sourceSlot, screenX, screenY);
    }

    /// <summary>
    ///     Resets drag state without firing any events. Called when a drag is cancelled.
    /// </summary>
    public void EndDrag()
    {
        DragSource = null;
        IsDragging = false;
        DragMouseX = 0;
        DragY = 0;
    }

    /// <summary>
    ///     Fired when the user double-clicks an occupied slot. Parameter is the 1-based slot number.
    /// </summary>
    public event PanelSlotClickedHandler? OnSlotClicked;
    public event PanelSlotClickedHandler? OnSlotSingleClicked;

    /// <summary>
    ///     Fired when the user drags a slot icon and releases outside the panel.
    ///     Parameters: (slot, mouseX, mouseY).
    /// </summary>
    public event PanelSlotDroppedOutsideHandler? OnSlotDroppedOutside;

    /// <summary>
    ///     Fired when the hovered slot changes. Parameter is the slot name (or null when unhovered).
    /// </summary>
    public event PanelSlotHoverEnterHandler? OnSlotHoverEnter;

    public event PanelSlotHoverExitHandler? OnSlotHoverExit;

    /// <summary>
    ///     Fired when the user drags a slot icon onto another slot. Parameters are 1-based slot numbers (source, target).
    /// </summary>
    public event PanelSlotSwappedHandler? OnSlotSwapped;

    protected abstract Texture2D RenderIcon(ushort spriteId);

    /// <summary>
    ///     Sets the expand state. Adjusts slot visibility and Y positions for the expanded grid.
    /// </summary>
    public override void SetExpanded(bool expanded)
    {
        if ((expanded == IsExpanded) || (ExpandedVisibleSlots == 0))
            return;

        base.SetExpanded(expanded);

        //apply additional compactgridpadding shift for large hud compact backgrounds
        var padShift = expanded ? -CompactGridPadding : CompactGridPadding;

        var targetSlots = expanded ? ExpandedVisibleSlots : NormalVisibleSlots;

        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].Y += padShift;
            Slots[i].Visible = i < targetSlots;
        }

        VisibleSlotCount = targetSlots;
    }

    public virtual void SetSlot(byte slot, ushort sprite)
    {
        var control = FindSlot(slot);

        if (control is null)
            return;

        control.NormalTexture?.Dispose();
        control.NormalTexture = RenderIcon(sprite);
    }

    public void SetSlotName(byte slot, string? name)
    {
        var control = FindSlot(slot);

        if (control is null)
            return;

        if (control is AbilitySlotControl ability)
            ability.SetAbilityName(name);
        else
            control.SlotName = name;
    }

    public override void OnMouseMove(MouseMoveEvent e)
    {
        var hoveredSlot = FindHoveredSlot(e.ScreenX, e.ScreenY);

        if (hoveredSlot == LastHoveredSlot)
            return;

        if (LastHoveredSlot is not null)
        {
            LastHoveredSlot.IsSlotHovered = false;
            OnSlotHoverExit?.Invoke();
        }

        LastHoveredSlot = hoveredSlot;

        if (hoveredSlot is not null)
        {
            hoveredSlot.IsSlotHovered = true;
            OnSlotHoverEnter?.Invoke(hoveredSlot);
        }
    }

    protected virtual PanelSlot? FindHoveredSlot(int screenX, int screenY)
    {
        for (var i = 0; (i < VisibleSlotCount) && (i < Slots.Count); i++)
            if (Slots[i].NormalTexture is not null && Slots[i].ContainsPoint(screenX, screenY))
                return Slots[i];

        return null;
    }

    public override void OnMouseLeave()
    {
        if (LastHoveredSlot is null)
            return;

        LastHoveredSlot.IsSlotHovered = false;
        LastHoveredSlot = null;
        OnSlotHoverExit?.Invoke();
    }

}