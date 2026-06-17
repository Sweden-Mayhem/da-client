#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Definitions;
using Chaos.Client.Networking;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.Networking.Entities.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.ViewPort;

/// <summary>
///     World-overview travel window. Shown when the server sends a WorldMap (SFieldMap) packet. A centered dark window
///     over a dimmed screen: a scrollable destination list on the left, and a zoomed, clipped view of the field overview
///     image on the right that PANS to the selected location (same fixed-duration smoothstep as the dialog-speaker camera).
///     Titlebar reads "Travel from {current} to {selected}". Buttons: Travel (asks "Travel to X?" via the shared OK/Cancel
///     dialog, then warps), Info (toggles curated notes, persisted), Close. Single click selects + pans; clicking the SAME
///     entry twice (or Travel / Enter) asks to travel.
/// </summary>
public sealed class WorldMap : UIPanel
{
    private const float ZOOM = 2.0f;
    private const float PAN_DURATION = 0.34f;
    private float CurrentZoom = 1f;

    private const int WIN_W = 900;
    private const int WIN_H = 580;
    private const int PAD = 14;
    private const int HEADER_H = 38;
    private const int BTN_W = 120;
    private const int BTN_H = 34;
    private const int BTN_GAP = 10;
    private const int LIST_W = 260;
    private const int HEADER_FONT = 20;
    private const int LIST_FONT = 18;
    private const int INFO_FONT = 16;
    private const int LABEL_FONT = 16;
    private const int BTN_FONT = 16;

    private static readonly Color DimColor = new(0, 0, 0, 160);
    private static readonly Color PanelBg = new Color(18, 16, 12) * 0.98f;
    private static readonly Color PanelBorder = new(88, 72, 46);
    private static readonly Color SubPanelBg = new Color(10, 9, 7) * 0.96f;
    private static readonly Color HeaderColor = new(214, 196, 156);
    private static readonly Color RowColor = new(206, 198, 182);
    private static readonly Color RowHoverColor = new(247, 220, 150);
    private static readonly Color RowSelectedBg = new Color(120, 96, 50) * 0.55f;
    private static readonly Color RowHoverBg = new Color(80, 72, 50) * 0.45f;
    private static readonly Color MarkerColor = new(100, 149, 237);

    private static readonly Dictionary<string, (int W, int H)> OriginalFieldSizes = new()
    {
        ["field001"] = (639, 479),
        ["field002"] = (640, 480),
        ["field003"] = (640, 480),
    };
    private static readonly Color MarkerSelectedColor = new(247, 142, 24);

    private readonly ConnectionManager Connection;
    private readonly MenuButton TravelButton;
    private readonly MenuButton InfoButton;

    private readonly List<Node> Nodes = [];
    private Texture2D? BackgroundTexture;
    private string CurrentMapName = string.Empty;
    private int CurrentMapId;

    private int SelectedIndex = -1;
    private int HoveredListIndex = -1;
    private int ScrollOffset;
    private bool ShowingInfo;
    private bool Confirming; //true while the shared OK/Cancel "Travel to X?" dialog is up

    private Vector2 ViewCenter;
    private Vector2 PanStart;
    private Vector2 PanTarget;
    private float PanT = 1f;
    private float ZoomStart = 1f;
    private float ZoomTarget = 1f;
    private float ZoomT = 1f;

    private Rectangle WindowRect;
    private Rectangle ListRect;
    private Rectangle MapRect;

    private readonly record struct Node(string Name, ushort MapId, int DestX, int DestY, ushort CheckSum, Point FieldPos);

    /// <summary>
    ///     Raised when the player asks to travel to a destination. The host shows the shared OK/Cancel dialog
    ///     ("Travel to {name}?") and invokes the supplied confirm/cancel callbacks. Keeps the in-game dialog graphics
    ///     instead of a bespoke confirm.
    /// </summary>
    public Action<string, Action, Action>? TravelRequested;

    public WorldMap(ConnectionManager connection)
    {
        Connection = connection;
        Visible = false;
        ZIndex = 160_000; //above the HUD/menus; this is a modal travel screen
        IsHitTestVisible = true;

        TravelButton = new MenuButton("Travel", BTN_W, BTN_H) { CustomFontSize = BTN_FONT, Visible = false };
        InfoButton = new MenuButton("Info", BTN_W, BTN_H) { CustomFontSize = BTN_FONT, Visible = false };

        TravelButton.Clicked = _ => RequestTravel(SelectedIndex);
        InfoButton.Clicked = _ => ToggleInfo();

        AddChild(TravelButton);
        AddChild(InfoButton);
    }

    public void Show(WorldMapArgs args, string currentMapName, int currentMapId)
    {
        ClearBackground();
        Nodes.Clear();
        CurrentMapName = currentMapName ?? string.Empty;
        CurrentMapId = currentMapId;
        ScrollOffset = 0;
        ShowingInfo = ClientSettings.WorldMapShowInfo; //#3: on by default, persisted
        HoveredListIndex = -1;
        Confirming = false;

        BackgroundTexture = UiRenderer.Instance!.GetFieldImage(args.FieldName);

        var scaleX = 1f;
        var scaleY = 1f;

        if (BackgroundTexture is not null && OriginalFieldSizes.TryGetValue(args.FieldName, out var origSize))
        {
            scaleX = BackgroundTexture.Width / (float)origSize.W;
            scaleY = BackgroundTexture.Height / (float)origSize.H;
        }

        foreach (var node in args.Nodes)
            Nodes.Add(
                new Node(
                    node.Text,
                    node.MapId,
                    node.DestinationPoint.X,
                    node.DestinationPoint.Y,
                    node.CheckSum,
                    new Point((int)(node.ScreenPosition.X * scaleX), (int)(node.ScreenPosition.Y * scaleY))));

        //no item is selected by default; the view simply starts centered on the current location
        SelectedIndex = -1;
        TravelButton.Visible = false;

        Visible = true;
        Layout();

        if (BackgroundTexture is not null)
            CurrentZoom = Math.Max(0.5f, Math.Max(MapRect.Width / (float)BackgroundTexture.Width, MapRect.Height / (float)BackgroundTexture.Height));
        else
            CurrentZoom = 1f;

        var fw = BackgroundTexture?.Width ?? 640;
        var fh = BackgroundTexture?.Height ?? 480;
        var center = new Vector2(fw / 2f, fh / 2f);
        ViewCenter = center;
        PanStart = center;
        PanTarget = center;
        PanT = 1f;
        ZoomT = 1f;

        InputDispatcher.Instance?.PushControl(this);
    }

    public void HideMap()
    {
        if (!Visible)
            return;

        Visible = false;
        Confirming = false;
        InputDispatcher.Instance?.RemoveControl(this);
        Nodes.Clear();
        ClearBackground();
    }

    private void ClearBackground()
    {
        BackgroundTexture?.Dispose();
        BackgroundTexture = null;
    }

    public override void Dispose()
    {
        ClearBackground();
        base.Dispose();
    }

    //the node that warps back to the map the player came from (the "Travel from" location)
    private int CurrentLocationIndex() => Nodes.FindIndex(n => n.MapId == CurrentMapId);

    private void ToggleInfo()
    {
        if (Confirming)
            return;

        ShowingInfo = !ShowingInfo;
        ClientSettings.WorldMapShowInfo = ShowingInfo; //#3: persist
        ClientSettings.Save();
    }

    private void Select(int index)
    {
        if ((index < 0) || (index >= Nodes.Count))
            return;

        SelectedIndex = index;
        TravelButton.Visible = true;

        PanStart = ViewCenter;
        PanTarget = Nodes[index].FieldPos.ToVector2();
        PanT = 0f;
        ZoomStart = CurrentZoom;
        ZoomTarget = ZOOM;
        ZoomT = 0f;
    }

    //#7: ask via the shared in-game OK/Cancel dialog before warping. The caller has already selected the entry.
    private void RequestTravel(int index)
    {
        if ((index < 0) || (index >= Nodes.Count) || Confirming)
            return;

        Confirming = true;

        var target = index;
        TravelRequested?.Invoke(
            Nodes[index].Name,
            () =>
            {
                Confirming = false;
                TravelTo(target);
            },
            () => Confirming = false);
    }

    private void TravelToCurrent()
    {
        if (Confirming)
            return;

        var index = CurrentLocationIndex();

        if (index < 0)
            index = Nodes.Count > 0 ? 0 : -1;

        if (index >= 0)
            TravelTo(index);
        else
            HideMap();
    }

    private void TravelTo(int index)
    {
        if ((index < 0) || (index >= Nodes.Count))
            return;

        var node = Nodes[index];
        Connection.ClickWorldMapNode(node.MapId, node.DestX, node.DestY, node.CheckSum);
        HideMap();
    }

    private void Layout()
    {
        X = 0;
        Y = 0;
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;

        var w = Math.Min(WIN_W, ChaosGame.UiWidth - 20);
        var h = Math.Min(WIN_H, ChaosGame.UiHeight - 20);
        WindowRect = new Rectangle((ChaosGame.UiWidth - w) / 2, (ChaosGame.UiHeight - h) / 2, w, h);

        var contentTop = WindowRect.Y + HEADER_H + PAD;
        var contentBottom = WindowRect.Bottom - PAD - BTN_H - PAD;
        var contentH = Math.Max(40, contentBottom - contentTop);

        ListRect = new Rectangle(WindowRect.X + PAD, contentTop, LIST_W, contentH);
        var mapX = ListRect.Right + PAD;
        MapRect = new Rectangle(mapX, contentTop, WindowRect.Right - PAD - mapX, contentH);

        var by = WindowRect.Bottom - PAD - BTN_H;
        TravelButton.Visible = SelectedIndex >= 0;
        InfoButton.Visible = SelectedIndex >= 0;

        var bx = WindowRect.Right - PAD - BTN_W;
        InfoButton.X = bx;
        InfoButton.Y = by;
        bx -= BTN_W + BTN_GAP;
        TravelButton.X = bx;
        TravelButton.Y = by;
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible)
            return;

        Layout();

        if (PanT < 1f)
        {
            var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            PanT = Math.Clamp(PanT + (dt / PAN_DURATION), 0f, 1f);
            var eased = PanT * PanT * (3f - 2f * PanT); //smoothstep
            ViewCenter = Vector2.Lerp(PanStart, PanTarget, eased);
        }

        if (ZoomT < 1f)
        {
            var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            ZoomT = Math.Clamp(ZoomT + (dt / PAN_DURATION), 0f, 1f);
            var eased = ZoomT * ZoomT * (3f - 2f * ZoomT);
            CurrentZoom = ZoomStart + (ZoomTarget - ZoomStart) * eased;
        }

        //animate the "you are here" player icon on the current location's node
        PlayerFrameTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (PlayerFrameTimer >= MapMarkers.PLAYER_FRAME_INTERVAL)
        {
            PlayerFrameTimer -= MapMarkers.PLAYER_FRAME_INTERVAL;
            PlayerFrame = (PlayerFrame + 1) % MapMarkers.PLAYER_FRAME_COUNT;
        }

        base.Update(gameTime);
    }

    private int PlayerFrame;
    private float PlayerFrameTimer;

    private int RowHeight => TtfTextRenderer.LineHeight(LIST_FONT) + 8;
    private int VisibleRows => Math.Max(1, ListRect.Height / RowHeight);
    private int MaxScroll => Math.Max(0, Nodes.Count - VisibleRows);

    private (Rectangle Src, float ScaleX, float ScaleY) MapView(Rectangle dest)
    {
        var fieldW = BackgroundTexture?.Width ?? 640;
        var fieldH = BackgroundTexture?.Height ?? 480;

        var srcW = dest.Width / CurrentZoom;
        var srcH = dest.Height / CurrentZoom;

        var cx = srcW >= fieldW ? fieldW / 2f : Math.Clamp(ViewCenter.X, srcW / 2f, fieldW - srcW / 2f);
        var cy = srcH >= fieldH ? fieldH / 2f : Math.Clamp(ViewCenter.Y, srcH / 2f, fieldH - srcH / 2f);

        var left = (int)MathF.Round(cx - srcW / 2f);
        var top = (int)MathF.Round(cy - srcH / 2f);
        var w = Math.Max(1, (int)MathF.Round(srcW));
        var h = Math.Max(1, (int)MathF.Round(srcH));

        if (left < 0) { w += left; left = 0; }
        if (top < 0) { h += top; top = 0; }
        if (left + w > fieldW) w = fieldW - left;
        if (top + h > fieldH) h = fieldH - top;
        w = Math.Max(1, w);
        h = Math.Max(1, h);

        var src = new Rectangle(left, top, w, h);

        return (src, dest.Width / (float)src.Width, dest.Height / (float)src.Height);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        spriteBatch.Draw(GetPixel(), new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight), DimColor);
        Fill(spriteBatch, WindowRect, PanelBg);
        Border(spriteBatch, WindowRect, PanelBorder);

        var selectedName = (SelectedIndex >= 0) && (SelectedIndex < Nodes.Count) ? Nodes[SelectedIndex].Name : "...";
        var header = string.IsNullOrEmpty(CurrentMapName)
            ? $"Travel to {selectedName}"
            : $"Travel from {CurrentMapName} to {selectedName}";
        var hw = TtfTextRenderer.MeasureWidth(header, HEADER_FONT);
        DrawTtf(spriteBatch, header, WindowRect.X + (WindowRect.Width - hw) / 2, WindowRect.Y + 9, HEADER_FONT, HeaderColor);

        DrawList(spriteBatch);
        DrawMap(spriteBatch);

        if (ShowingInfo)
            DrawInfo(spriteBatch);

        base.Draw(spriteBatch); //buttons
    }

    private void DrawList(SpriteBatch spriteBatch)
    {
        Fill(spriteBatch, ListRect, SubPanelBg);
        Border(spriteBatch, ListRect, PanelBorder);

        var rowH = RowHeight;
        var first = ScrollOffset;
        var last = Math.Min(Nodes.Count, first + VisibleRows);

        for (var i = first; i < last; i++)
        {
            var rowY = ListRect.Y + (i - first) * rowH;
            var rowRect = new Rectangle(ListRect.X + 1, rowY, ListRect.Width - 2, rowH);

            if (i == SelectedIndex)
                Fill(spriteBatch, rowRect, RowSelectedBg);
            else if (i == HoveredListIndex)
                Fill(spriteBatch, rowRect, RowHoverBg);

            var color = (i == SelectedIndex) || (i == HoveredListIndex) ? RowHoverColor : RowColor;
            DrawTtf(spriteBatch, Nodes[i].Name, ListRect.X + 10, rowY + 4, LIST_FONT, color);
        }

        if (ScrollOffset > 0)
            DrawTtf(spriteBatch, "▲", ListRect.Right - 20, ListRect.Y + 3, LIST_FONT, PanelBorder);

        if (ScrollOffset < MaxScroll)
            DrawTtf(spriteBatch, "▼", ListRect.Right - 20, ListRect.Bottom - rowH, LIST_FONT, PanelBorder);
    }

    private void DrawMap(SpriteBatch spriteBatch)
    {
        Fill(spriteBatch, MapRect, Color.Black);

        if (BackgroundTexture is null)
            return;

        var mapContent = new Rectangle(MapRect.X + 1, MapRect.Y, MapRect.Width - 2, MapRect.Height - 1);
        var (src, scaleX, scaleY) = MapView(mapContent);
        spriteBatch.Draw(BackgroundTexture, mapContent, src, Color.White);

        Border(spriteBatch, MapRect, PanelBorder);

        MapMarkers.EnsureLoaded(spriteBatch.GraphicsDevice);

        for (var i = 0; i < Nodes.Count; i++)
        {
            var node = Nodes[i];
            var mx = mapContent.X + (int)((node.FieldPos.X - src.X) * scaleX);
            var my = mapContent.Y + (int)((node.FieldPos.Y - src.Y) * scaleY);
            var selected = i == SelectedIndex;

            var size = selected ? 22 : 16;
            var mark = MapMarkers.RedMark;
            var markH = mark is null ? size : Math.Max(1, (int)(size * (mark.Height / (float)mark.Width)));
            var box = new Rectangle(mx - size / 2, my - markH / 2, size, markH);

            if (!mapContent.Contains(box))
                continue;

            if (mark is not null)
            {
                spriteBatch.Draw(mark, new Rectangle(box.X + 2, box.Y + 2, size, markH), Color.Black * 0.55f);
                spriteBatch.Draw(mark, box, selected ? Color.White : new Color(235, 235, 235) * 0.85f);
            }
            else
                Fill(spriteBatch, box, selected ? MarkerSelectedColor : MarkerColor);

            //the current location carries the retail animated player icon standing on its node
            if ((node.MapId == CurrentMapId) && (MapMarkers.PlayerFrames is { Length: > 0 } frames))
            {
                var pw = frames[0].Width * 2;
                var ph = frames[0].Height * 2;
                MapMarkers.DrawPlayerFrame(spriteBatch, PlayerFrame, new Rectangle(mx - pw / 2, my - ph, pw, ph), Color.White);
            }

            if (selected)
            {
                var lw = TtfTextRenderer.MeasureWidth(node.Name, LABEL_FONT);
                var lx = Math.Clamp(mx - lw / 2, MapRect.X + 2, MapRect.Right - lw - 2);
                var ly = box.Y - TtfTextRenderer.LineHeight(LABEL_FONT) - 2;

                if (ly < MapRect.Y)
                    ly = box.Bottom + 2;

                Fill(spriteBatch, new Rectangle(lx - 3, ly - 1, lw + 6, TtfTextRenderer.LineHeight(LABEL_FONT) + 2), SubPanelBg);
                DrawTtf(spriteBatch, node.Name, lx, ly, LABEL_FONT, MarkerSelectedColor);
            }
        }
    }

    private void DrawInfo(SpriteBatch spriteBatch)
    {
        if ((SelectedIndex < 0) || (SelectedIndex >= Nodes.Count))
            return;

        var node = Nodes[SelectedIndex];
        var info = WorldMapInfo.Get(node.Name);

        var boxW = MapRect.Width - 20;
        var lineH = TtfTextRenderer.LineHeight(INFO_FONT);
        var wrapped = TtfTextRenderer.WrapText(info.Description, boxW - 20, INFO_FONT);
        var boxH = 14 + lineH * 2 + 8 + lineH * Math.Max(1, wrapped.Count) + 12;
        var box = new Rectangle(MapRect.X + 10, MapRect.Bottom - boxH - 10, boxW, boxH);

        Fill(spriteBatch, box, new Color(8, 7, 5) * 0.93f);
        Border(spriteBatch, box, PanelBorder);

        var ty = box.Y + 10;
        DrawTtf(spriteBatch, node.Name, box.X + 10, ty, INFO_FONT, HeaderColor);
        ty += lineH + 1;
        DrawTtf(spriteBatch, $"Recommended: {info.Recommended}", box.X + 10, ty, INFO_FONT, RowHoverColor);
        ty += lineH + 8;

        foreach (var line in wrapped)
        {
            DrawTtf(spriteBatch, line, box.X + 10, ty, INFO_FONT, RowColor);
            ty += lineH;
        }
    }

    //--- input ---

    public override void OnMouseMove(MouseMoveEvent e) => HoveredListIndex = Confirming ? -1 : ListRowAt(e.ScreenX, e.ScreenY);

    public override void OnMouseLeave() => HoveredListIndex = -1;

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (Confirming || !ListRect.Contains(InputBuffer.MouseX, InputBuffer.MouseY))
            return;

        ScrollOffset = Math.Clamp(ScrollOffset - Math.Sign(e.Delta), 0, MaxScroll);
        e.Handled = true;
    }

    public override void OnClick(ClickEvent e)
    {
        if (Confirming)
            return;

        var target = ListRowAt(e.ScreenX, e.ScreenY);

        if (target < 0)
            target = MarkerAt(e.ScreenX, e.ScreenY);

        if (target < 0)
            return;

        e.Handled = true;

        //click an entry that is NOT already selected -> select it and pan there. Click the entry that is ALREADY
        //selected -> travel to it (no re-pan). So rapid clicks on different entries only ever re-select.
        if (target == SelectedIndex)
            RequestTravel(target);
        else
            Select(target);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (Confirming) //the OK/Cancel dialog owns the keyboard while it is up
            return;

        switch (e.Key)
        {
            case Keys.Up:
                Select(SelectedIndex <= 0 ? Nodes.Count - 1 : SelectedIndex - 1);
                EnsureSelectedVisible();
                e.Handled = true;

                break;
            case Keys.Down:
                Select(SelectedIndex < 0 ? 0 : Math.Min(SelectedIndex + 1, Nodes.Count - 1));
                EnsureSelectedVisible();
                e.Handled = true;

                break;
            case Keys.Enter:
                RequestTravel(SelectedIndex);
                e.Handled = true;

                break;
        }
    }

    private void EnsureSelectedVisible()
    {
        if (SelectedIndex < 0)
            return;

        if (SelectedIndex < ScrollOffset)
            ScrollOffset = SelectedIndex;
        else if (SelectedIndex >= (ScrollOffset + VisibleRows))
            ScrollOffset = SelectedIndex - VisibleRows + 1;

        ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScroll);
    }

    private int ListRowAt(int screenX, int screenY)
    {
        if (!ListRect.Contains(screenX, screenY))
            return -1;

        var row = ScrollOffset + (screenY - ListRect.Y) / RowHeight;

        return (row >= 0) && (row < Nodes.Count) ? row : -1;
    }

    private int MarkerAt(int screenX, int screenY)
    {
        if (!MapRect.Contains(screenX, screenY) || (BackgroundTexture is null))
            return -1;

        var mapContent = new Rectangle(MapRect.X + 1, MapRect.Y, MapRect.Width - 2, MapRect.Height - 1);
        var (src, scaleX, scaleY) = MapView(mapContent);
        var best = -1;
        var bestDist = 16 * 16;

        for (var i = 0; i < Nodes.Count; i++)
        {
            var mx = mapContent.X + (int)((Nodes[i].FieldPos.X - src.X) * scaleX);
            var my = mapContent.Y + (int)((Nodes[i].FieldPos.Y - src.Y) * scaleY);
            var dx = mx - screenX;
            var dy = my - screenY;
            var dist = dx * dx + dy * dy;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    //--- draw helpers ---

    private static void DrawTtf(SpriteBatch spriteBatch, string text, int x, int y, int size, Color color)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var glyphs = TtfTextRenderer.GetLine(text, size);

        if (glyphs is not null)
            spriteBatch.Draw(glyphs, new Vector2(x, y), color);
        else
            TextRenderer.DrawText(spriteBatch, new Vector2(x, y), text, color);
    }

    private static void Fill(SpriteBatch spriteBatch, Rectangle rect, Color color) => spriteBatch.Draw(GetPixel(), rect, color);

    private static void Border(SpriteBatch spriteBatch, Rectangle r, Color color)
    {
        Fill(spriteBatch, new Rectangle(r.X, r.Y, r.Width, 1), color);
        Fill(spriteBatch, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), color);
        Fill(spriteBatch, new Rectangle(r.X, r.Y, 1, r.Height), color);
        Fill(spriteBatch, new Rectangle(r.Right - 1, r.Y, 1, r.Height), color);
    }
}
