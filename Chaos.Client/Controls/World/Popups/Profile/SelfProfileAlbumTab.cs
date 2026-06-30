#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     Album tab page (_nui_al): the player's server-stored screenshot gallery. A two-page book that fills up like a
///     photo album - the left page (PGA1) takes the first 6 pictures, then the right page (PGA2) the next 6. The
///     prefab's own PREV/NEXT button controls (clickable, like the shop's page buttons) sit over its printed Prev/Next
///     art and turn the page; the page numbers go in its printed PG1/PG2 boxes. Each image is lazily fetched by id and
///     cached (no re-download); clicking one raises OnViewImage so the screen opens a full-screen <see cref="AlbumImageViewer" />.
///     Images decode from JPEG the same way profile portraits do.
/// </summary>
public sealed class SelfProfileAlbumTab : PrefabPanel
{
    private const int COLS_PER_PAGE = 2;
    private const int ROWS = 3;
    private const int PER_PAGE = COLS_PER_PAGE * ROWS; //6
    private const int PER_SPREAD = PER_PAGE * 2;       //12, both pages
    private const int GAP = 3;            //horizontal gap between columns
    private const int VGAP = GAP - 1;     //vertical gap between rows (1px tighter)

    private static readonly Color HoverBorder = new(220, 200, 150);

    private readonly int CellH;
    private readonly int CellW;
    private readonly Rectangle ContentRect;
    private readonly UILabel EmptyLabel;
    private readonly List<Entry> Entries = [];
    private readonly UIButton? NextButton;
    private readonly UILabel PageLeftLabel;
    private readonly UILabel PageRightLabel;
    private readonly UIButton? PrevButton;
    private readonly HashSet<uint> Requested = [];
    private readonly Thumb[] Slots = new Thumb[PER_SPREAD];

    private int CurrentSpread;
    private int DataVersion;
    private bool HasManifest; //got the id list once; afterwards the server pushes updates, so don't re-request
    private int RenderedVersion = -1;

    /// <summary>Fired when the tab first opens - the screen should request the album manifest.</summary>
    public event Action? OnRequestManifest;

    /// <summary>Fired to lazily fetch one image's bytes by id (a thumbnail came into view).</summary>
    public event Action<uint>? OnRequestImage;

    /// <summary>Fired when a loaded thumbnail is clicked - the screen opens the full-screen viewer for that picture.</summary>
    public event Action<uint, Texture2D>? OnViewImage;

    public SelfProfileAlbumTab(string prefabName)
        : base(prefabName, false)
    {
        Name = prefabName;
        Visible = false;

        //the prefab lays out the two pages, the Prev/Next buttons and the page-number boxes for us
        var pga1 = RectOr("PGA1", new Rectangle(36, 34, 228, 240));
        var pga2 = RectOr("PGA2", new Rectangle(335, 34, 228, 240));
        var pg1 = RectOr("PG1", new Rectangle(79, 278, 42, 12));
        var pg2 = RectOr("PG2", new Rectangle(477, 278, 42, 12));

        ContentRect = new Rectangle(pga1.X, pga1.Y, pga2.Right - pga1.X, pga1.Height);
        CellW = (pga1.Width - ((COLS_PER_PAGE - 1) * GAP)) / COLS_PER_PAGE;
        CellH = (pga1.Height - ((ROWS - 1) * VGAP)) / ROWS;

        for (var i = 0; i < PER_SPREAD; i++)
        {
            //slot 0..5 fill the LEFT page (reading order), 6..11 the RIGHT page
            var pageRect = i < PER_PAGE ? pga1 : pga2;
            var inPage = i % PER_PAGE;

            var slot = new Thumb
            {
                Name = $"AlbumThumb{i}",
                X = pageRect.X + ((inPage % COLS_PER_PAGE) * (CellW + GAP)),
                Y = pageRect.Y + ((inPage / COLS_PER_PAGE) * (CellH + VGAP)),
                Width = CellW,
                Height = CellH,
                Visible = false
            };

            slot.Clicked = () => View(slot.EntryIndex);
            Slots[i] = slot;
            AddChild(slot);
        }

        //--- nav: the prefab's own PREV/NEXT button controls (clickable like the shop's page buttons); the page numbers
        //--- go in the printed PG1/PG2 boxes. The book art already has the Prev/Next labels + arrows. ---
        PrevButton = CreateButton("PREV");
        NextButton = CreateButton("NEXT");

        if (PrevButton is not null)
            PrevButton.Clicked += () => Page(-1);

        if (NextButton is not null)
            NextButton.Clicked += () => Page(1);

        PageLeftLabel = MakePageNumber(pg1);
        PageRightLabel = MakePageNumber(pg2);
        AddChild(PageLeftLabel);
        AddChild(PageRightLabel);

        EmptyLabel = new UILabel
        {
            X = ContentRect.X,
            Y = ContentRect.Y + (ContentRect.Height / 2) - 10,
            Width = ContentRect.Width,
            Height = 20,
            Text = "No pictures yet - press the screenshot key in-game to take one.",
            ForegroundColor = new Color(150, 140, 120),
            CustomFontSize = 13,
            RenderNative = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visible = false
        };

        AddChild(EmptyLabel);

        VisibilityChanged += visible =>
        {
            if (!visible)
                return;

            //keep the player where they last were in the book - CurrentSpread persists across opens (page 1 the first time)
            if (!HasManifest)
                OnRequestManifest?.Invoke();

            DataVersion++;
        };
    }

    /// <summary>
    ///     Updates the album from the manifest ids (server order is oldest-first, which is also the fill order).
    ///     DIFFS against what we already have - the server pushes this after every add/delete, so cached thumbnails for
    ///     ids still present are kept (no re-decode, no re-fetch, no flicker); only removed ids are freed, only new ids
    ///     are fetched.
    /// </summary>
    public void SetAlbumManifest(IReadOnlyList<uint> ids)
    {
        HasManifest = true;

        var cached = new Dictionary<uint, Texture2D?>(Entries.Count);

        foreach (var entry in Entries)
            cached[entry.Id] = entry.Texture;

        var rebuilt = new List<Entry>(ids.Count);

        //oldest-first: the book fills from the front, newest pictures land on the last page
        foreach (var id in ids)
        {
            var entry = new Entry { Id = id };

            if (cached.Remove(id, out var tex))
                entry.Texture = tex; //already downloaded - reuse it

            rebuilt.Add(entry);
        }

        foreach (var tex in cached.Values)
            tex?.Dispose();

        Requested.ExceptWith(cached.Keys);

        Entries.Clear();
        Entries.AddRange(rebuilt);

        CurrentSpread = Math.Clamp(CurrentSpread, 0, LastSpread());
        DataVersion++;
    }

    /// <summary>Supplies the JPEG bytes for one image id (reply to a fetch); decoded into a thumbnail texture.</summary>
    public void SetAlbumImage(uint id, byte[] bytes)
    {
        foreach (var entry in Entries)
            if (entry.Id == id)
            {
                entry.Texture?.Dispose();
                entry.Texture = Decode(bytes);
                DataVersion++;

                return;
            }
    }

    /// <summary>Drops all images (e.g. on logout).</summary>
    public void ClearAll()
    {
        foreach (var entry in Entries)
            entry.Texture?.Dispose();

        Entries.Clear();
        Requested.Clear();
        CurrentSpread = 0;
        HasManifest = false; //next login re-fetches the manifest once
        DataVersion++;
    }

    private int LastSpread() => Entries.Count == 0 ? 0 : (Entries.Count - 1) / PER_SPREAD;

    private void Page(int delta)
    {
        var target = Math.Clamp(CurrentSpread + delta, 0, LastSpread());

        if (target == CurrentSpread)
            return;

        CurrentSpread = target;
        DataVersion++;
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        Page(-e.Delta);
        e.Handled = true;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        Refresh();
        base.Draw(spriteBatch);
    }

    private void Refresh()
    {
        if (RenderedVersion == DataVersion)
            return;

        RenderedVersion = DataVersion;

        var totalPages = (Entries.Count + PER_PAGE - 1) / PER_PAGE;
        var showNav = Entries.Count > 0;

        EmptyLabel.Visible = Entries.Count == 0;

        if (PrevButton is not null)
            PrevButton.Visible = showNav && (CurrentSpread > 0);

        if (NextButton is not null)
            NextButton.Visible = showNav && (CurrentSpread < LastSpread());

        //left/right page numbers for the current spread (1-indexed), blank if that page has no pictures
        var leftPage = (CurrentSpread * 2) + 1;
        var rightPage = (CurrentSpread * 2) + 2;
        PageLeftLabel.Visible = showNav && (leftPage <= totalPages);
        PageRightLabel.Visible = showNav && (rightPage <= totalPages);
        PageLeftLabel.Text = leftPage.ToString();
        PageRightLabel.Text = rightPage.ToString();

        var start = CurrentSpread * PER_SPREAD;

        for (var i = 0; i < PER_SPREAD; i++)
        {
            var slot = Slots[i];
            var idx = start + i;

            if (idx < Entries.Count)
            {
                var entry = Entries[idx];
                slot.EntryIndex = idx;
                slot.Texture = entry.Texture;
                slot.Visible = true;

                if ((entry.Texture is null) && Requested.Add(entry.Id))
                    OnRequestImage?.Invoke(entry.Id);
            } else
                slot.Visible = false;
        }
    }

    private void View(int entryIndex)
    {
        if ((entryIndex < 0) || (entryIndex >= Entries.Count))
            return;

        var entry = Entries[entryIndex];

        if (entry.Texture is not null)
            OnViewImage?.Invoke(entry.Id, entry.Texture);
    }

    private Rectangle RectOr(string name, Rectangle fallback)
    {
        var r = GetRect(name);

        return r == Rectangle.Empty ? fallback : r;
    }

    private static UILabel MakePageNumber(Rectangle box) => new()
    {
        X = box.X,
        Y = box.Y - 2, //sits a touch high in the printed box

        Width = box.Width,
        Height = box.Height,
        ForegroundColor = new Color(228, 214, 178),
        CustomFontSize = 11,
        RenderNative = true, //crisp TTF at native resolution, not upscaled with the magnified book
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private static Texture2D? Decode(byte[] bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;

        using var img = SKImage.FromEncodedData(bytes);

        return img is null ? null : TextureConverter.ToTexture2D(img);
    }

    private static Rectangle Fit(Texture2D tex, Rectangle area)
    {
        var scale = Math.Min((float)area.Width / tex.Width, (float)area.Height / tex.Height);
        var w = Math.Max(1, (int)(tex.Width * scale));
        var h = Math.Max(1, (int)(tex.Height * scale));

        return new Rectangle(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h);
    }

    private static void DrawThumbBorder(SpriteBatchEx sb, Rectangle r, Color c)
    {
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, 1), c);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
        DrawRect(sb, new Rectangle(r.X, r.Y, 1, r.Height), c);
        DrawRect(sb, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
    }

    public override void Dispose()
    {
        foreach (var entry in Entries)
            entry.Texture?.Dispose();

        base.Dispose();
    }

    private sealed class Entry
    {
        public uint Id;
        public Texture2D? Texture;
    }

    //a clickable thumbnail cell: aspect-fits its texture, highlights on hover, raises Clicked when it has an image
    private sealed class Thumb : UIPanel
    {
        public Action? Clicked;
        public int EntryIndex;
        public Texture2D? Texture;
        private bool Hover;

        public Thumb() => BackgroundColor = new Color(10, 8, 5, 225); //a dark plate behind each picture

        public override void OnMouseDown(MouseDownEvent e)
        {
            if ((e.Button == MouseButton.Left) && (Texture is not null))
            {
                Clicked?.Invoke();
                e.Handled = true;
            }
        }

        public override void OnMouseEnter() => Hover = true;
        public override void OnMouseLeave() => Hover = false;

        public override void Draw(SpriteBatchEx spriteBatch)
        {
            if (!Visible)
                return;

            base.Draw(spriteBatch); //the dark plate

            var bounds = new Rectangle(ScreenX, ScreenY, Width, Height);

            if (Texture is not null)
                spriteBatch.Draw(Texture, Fit(Texture, bounds), Color.White);

            if (Hover)
                DrawThumbBorder(spriteBatch, bounds, HoverBorder);
        }
    }
}
