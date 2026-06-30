#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Data.Models;
using Chaos.Client.Definitions;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud.Panel;

/// <summary>
///     Inventory item grid panel (A key). Thin view that subscribes to
///     <see cref="Inventory" /> change events and renders item icons with dye colors.
///     Supports expand toggle that grows the panel upward (3->5 rows in small HUD, 1->5 rows in large HUD).
/// </summary>
public sealed class InventoryPanel : PanelBase
{
    private const int MAX_SLOTS = Inventory.MAX_SLOTS; //one real slot per grid cell (60) since the server bump
    private const ushort GOLD_SPRITE = 136;
    private const int EXPANDED_SLOTS = 5 * DEFAULT_COLUMNS;

    private readonly PanelSlot? GoldSlot;
    private long Gold = long.MinValue;

    //the grid cell the gold bag prefers (persisted); -1 / out-of-range falls back to the last visible cell
    private int GoldGridIndex = -1;

    public InventoryPanel(
        ControlPrefabSet hudPrefabSet,
        Texture2D? background = null,
        Texture2D? expandedBackground = null,
        int normalVisibleSlots = DEFAULT_VISIBLE_SLOTS,
        bool showGold = true,
        ClickedHandler? sideButtonAction = null,
        Func<String?>? sideButtonTooltipProvider = null)
        : base(
            hudPrefabSet,
            MAX_SLOTS,
            gridOffsetX: 7,
            gridOffsetY: 5,
            background: background,
            normalVisibleSlots: normalVisibleSlots,
            sideButtonAction: sideButtonAction,
            sideButtonTooltipProvider: sideButtonTooltipProvider)
    {
        Name = "Inventory";

        ConfigureExpand(expandedBackground ?? UiRenderer.Instance!.GetSpfTexture("_ninv5.spf"), EXPANDED_SLOTS);

        if (showGold)
        {
            //gold bag overlays a grid cell (the persisted one, else the last visible). Slot 0 marks it as the
            //gold bag everywhere: drag-to-world drops gold, and CompleteDragSwap below keeps its "swaps" client-side.
            GoldSlot = new PanelSlot
            {
                Name = "GoldBag",
                Slot = 0,
                Width = ICON_SIZE,
                Height = ICON_SIZE,
                NormalTexture = RenderIcon(GOLD_SPRITE),
                ZIndex = 1
            };

            GoldSlot.SlotName = $"Gold( {WorldState.Inventory.Gold} )";
            GoldSlot.DragStarted += OnDragStarted;
            AddChild(GoldSlot);

            GoldGridIndex = ClientSettings.GoldSlotIndex;
            PositionGoldSlot(GoldDisplayIndex());

            WorldState.Inventory.GoldChanged += OnGoldChanged;
        }

        //subscribe to state events
        WorldState.Inventory.SlotChanged += OnSlotChanged;
        WorldState.Inventory.Cleared += OnCleared;
    }

    public override void Dispose()
    {
        WorldState.Inventory.SlotChanged -= OnSlotChanged;
        WorldState.Inventory.Cleared -= OnCleared;

        if (GoldSlot is not null)
            WorldState.Inventory.GoldChanged -= OnGoldChanged;

        base.Dispose();
    }

    private void OnCleared()
    {
        foreach (var slot in Slots)
        {
            slot.NormalTexture?.Dispose();
            slot.NormalTexture = null;
            slot.SlotName = null;
            slot.CurrentDurability = 0;
            slot.MaxDurability = 0;
        }

        //everything is empty again - the bag returns to its preferred cell
        if (GoldSlot is not null)
            PositionGoldSlot(GoldDisplayIndex());
    }

    private void OnGoldChanged()
    {
        var gold = (long)WorldState.Inventory.Gold;

        if (gold == Gold)
            return;

        Gold = gold;
        GoldSlot!.SlotName = $"Gold( {gold} )";
    }

    private void OnSlotChanged(byte slot)
    {
        var control = FindSlot(slot);

        if (control is null)
            return;

        var data = WorldState.Inventory.GetSlot(slot);

        if (data.IsOccupied)
        {
            control.NormalTexture?.Dispose();
            control.NormalTexture = UiRenderer.Instance!.GetItemIcon(data.Sprite, data.Color);
            control.SlotName = data.Name;
            control.CurrentDurability = data.CurrentDurability;
            control.MaxDurability = data.MaxDurability;
            control.StackCount = data.Stackable ? (int)data.Count : 0;
            control.Unidentified = data.Unidentified;
        } else
        {
            control.NormalTexture?.Dispose();
            control.NormalTexture = null;
            control.SlotName = null;
            control.CooldownPercent = 0;
            control.CurrentDurability = 0;
            control.MaxDurability = 0;
            control.StackCount = 0;
            control.Unidentified = false;
        }

        //the bag never covers an ITEM: if the server just filled the slot it was sitting on (it only ever sits on
        //empty ones), it steps aside to the nearest free cell
        if ((GoldSlot is not null)
            && data.IsOccupied
            && (DisplayedGoldCell >= 0)
            && (DisplayedGoldCell < Slots.Count)
            && (Slots[DisplayedGoldCell].Slot == slot))
            PositionGoldSlot(GoldDisplayIndex());
    }

    //the grid cell the bag is currently drawn in (always one whose server slot is empty)
    private int DisplayedGoldCell = -1;

    private void PositionGoldSlot(int gridIndex)
    {
        if ((gridIndex < 0) || (gridIndex >= Slots.Count))
            return;

        GoldSlot!.X = Slots[gridIndex].X;
        GoldSlot.Y = Slots[gridIndex].Y;
        DisplayedGoldCell = gridIndex;

        //hide the (empty) slot control underneath the bag so its hover/empty-cell art doesn't fight the bag's
        for (var i = 0; i < Slots.Count; i++)
            if (i < VisibleSlotCount)
                Slots[i].Visible = i != gridIndex;
    }

    protected override Texture2D RenderIcon(ushort spriteId) => UiRenderer.Instance!.GetItemIcon(spriteId);

    //true when the cell's SERVER slot holds no item (the bag may only sit on empty cells)
    private bool IsCellFree(int gridIndex)
        => (gridIndex >= 0)
           && (gridIndex < Math.Min(Slots.Count, VisibleSlotCount))
           && !WorldState.Inventory.GetSlot(Slots[gridIndex].Slot).IsOccupied;

    //the cell the bag shows in: the persisted preference when its slot is EMPTY, else the last free cell
    //(scanning backward from the corner, where the bag historically lives). A completely full inventory leaves it
    //on the preferred cell - there is nowhere better.
    private int GoldDisplayIndex()
    {
        var preferred = GoldGridIndex >= 0 ? Math.Min(GoldGridIndex, VisibleSlotCount - 1) : VisibleSlotCount - 1;

        if (IsCellFree(preferred))
            return preferred;

        for (var i = VisibleSlotCount - 1; i >= 0; i--)
            if (IsCellFree(i))
                return i;

        return preferred;
    }

    private void MoveGoldToCell(int gridIndex)
    {
        GoldGridIndex = gridIndex;
        PositionGoldSlot(GoldDisplayIndex());
        ClientSettings.GoldSlotIndex = gridIndex;
        ClientSettings.Save();
    }

    //trade placement: the destination slot is BECOMING empty (its item's swap was just sent), but the client state
    //won't show that until the server replies - the usual empty-cell fallback would race it and bounce the bag to
    //the corner. Trust the in-flight trade and place the bag exactly; the OnSlotChanged hook still rescues it if
    //the slot somehow ends up occupied.
    private void PlaceGoldAt(int gridIndex)
    {
        if ((gridIndex < 0) || (gridIndex >= Slots.Count))
            return;

        GoldGridIndex = gridIndex;
        PositionGoldSlot(gridIndex);
        ClientSettings.GoldSlotIndex = gridIndex;
        ClientSettings.Save();
    }

    private int CellOfSlot(byte slot)
    {
        for (var i = 0; i < Slots.Count; i++)
            if (Slots[i].Slot == slot)
                return i;

        return -1;
    }

    /// <summary>
    ///     The gold bag behaves like a proper item in the grid: dragging it onto an occupied cell TRADES places with
    ///     that item (the item really moves into the bag's empty slot server-side), dropping an item onto the bag
    ///     trades the same way, and onto an empty cell it just relocates. Only dropping it OUTSIDE differs from a
    ///     real item: that drops its CONTENTS (the gold prompt), never the bag.
    /// </summary>
    public override void CompleteDragSwap(byte targetSlot)
    {
        if (IsDragging && ReferenceEquals(DragSource, GoldSlot))
        {
            EndDrag();

            var targetCell = CellOfSlot(targetSlot);
            var bagSlot = (DisplayedGoldCell >= 0) && (DisplayedGoldCell < Slots.Count) ? Slots[DisplayedGoldCell].Slot : (byte)0;

            if ((targetCell < 0) || (targetSlot == bagSlot) || (bagSlot == 0))
                return;

            //occupied target: the item moves into the bag's (empty) slot; the bag takes the item's cell
            if (WorldState.Inventory.GetSlot(targetSlot).IsOccupied)
            {
                RaiseSlotSwapped(targetSlot, bagSlot);
                PlaceGoldAt(targetCell); //trusts the in-flight trade (the cell still LOOKS occupied client-side)
            } else
                MoveGoldToCell(targetCell);

            return;
        }

        //an item dropped ONTO the bag: they trade places the same way
        if (targetSlot == 0)
        {
            if (IsDragging && DragSource is { Slot: > 0 } source)
            {
                var itemSlot = source.Slot;
                EndDrag();

                var bagSlot = (DisplayedGoldCell >= 0) && (DisplayedGoldCell < Slots.Count) ? Slots[DisplayedGoldCell].Slot : (byte)0;

                if ((bagSlot != 0) && (itemSlot != bagSlot))
                {
                    RaiseSlotSwapped(itemSlot, bagSlot);
                    PlaceGoldAt(CellOfSlot(itemSlot)); //trusts the in-flight trade (the cell still LOOKS occupied)
                }

                return;
            }

            EndDrag();

            return;
        }

        base.CompleteDragSwap(targetSlot);
    }

    /// <summary>
    ///     Dropping the GOLD BAG on a panel area with no slot control under it (the grid padding, or the cell the
    ///     bag itself vacated) relocates the bag to the cell under the cursor. Drops on a real slot go through the
    ///     slot's own handler into <see cref="CompleteDragSwap" />.
    /// </summary>
    public override void OnDragDrop(DragDropEvent e)
    {
        if (IsDragging && (GoldSlot is not null) && ReferenceEquals(DragSource, GoldSlot))
        {
            var cell = GridCellAt(e.ScreenX, e.ScreenY);

            //panel-level drops only land on non-slot areas (the bag's own hidden cell or grid padding); the bag
            //may only settle on an EMPTY cell
            if ((cell >= 0) && IsCellFree(cell))
            {
                EndDrag();
                MoveGoldToCell(cell);
                e.Handled = true;

                return;
            }
        }

        base.OnDragDrop(e);
    }

    //the visible grid cell under a screen point, or -1 when the point is outside every cell
    private int GridCellAt(int screenX, int screenY)
    {
        for (var i = 0; i < Math.Min(Slots.Count, VisibleSlotCount); i++)
        {
            var cx = ScreenX + Slots[i].X;
            var cy = ScreenY + Slots[i].Y;

            if ((screenX >= cx) && (screenX < cx + CELL_WIDTH) && (screenY >= cy) && (screenY < cy + CELL_HEIGHT))
                return i;
        }

        return -1;
    }

    public override void SetExpanded(bool expanded)
    {
        base.SetExpanded(expanded);

        if (GoldSlot is not null)
            PositionGoldSlot(GoldDisplayIndex());
    }

    protected override PanelSlot? FindHoveredSlot(int screenX, int screenY)
    {
        if (GoldSlot is not null && GoldSlot.Visible && GoldSlot.ContainsPoint(screenX, screenY))
            return GoldSlot;

        return base.FindHoveredSlot(screenX, screenY);
    }
}