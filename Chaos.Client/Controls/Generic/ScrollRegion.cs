#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.Generic;

/// <summary>
///     A simple vertical scroll viewport: callers <see cref="Add" /> absolutely-positioned children and call
///     <see cref="SetContentHeight" />; the region clips them to its bounds and shows a scrollbar on the right when the
///     content overflows. Used by the market window's sidebar tree and detail panel. Content widths should use
///     <see cref="InnerWidth" /> (the region width minus the scrollbar gutter).
/// </summary>
public sealed class ScrollRegion : UIPanel
{
    private const int SCROLL_STEP = 52;

    private readonly UIPanel Inner;
    private readonly ScrollBarControl ScrollBar;

    /// <summary>The usable content width (region width minus the scrollbar gutter).</summary>
    public int InnerWidth { get; }

    public ScrollRegion(int width, int height)
    {
        Width = width;
        Height = height;
        IsPassThrough = true;
        InnerWidth = width - ScrollBarControl.DEFAULT_WIDTH;

        Inner = new UIPanel
        {
            X = 0,
            Y = 0,
            Width = InnerWidth,
            Height = height,
            IsPassThrough = true
        };
        AddChild(Inner);

        ScrollBar = new ScrollBarControl
        {
            X = width - ScrollBarControl.DEFAULT_WIDTH,
            Y = 0,
            Height = height,
            Visible = false
        };
        ScrollBar.OnValueChanged += v => Inner.Y = -v;
        AddChild(ScrollBar);
    }

    /// <summary>Repositions/resizes the viewport (e.g. to reclaim space when a search box above it is hidden). Call
    ///     <see cref="SetContentHeight" /> afterwards to re-evaluate the scrollbar.</summary>
    public void SetViewport(int y, int height)
    {
        Y = y;
        Height = height;
        ScrollBar.Height = height;
        Inner.Y = 0;
    }

    /// <summary>Removes all content and resets the scroll position.</summary>
    public void Clear()
    {
        Inner.Children.Clear();
        Inner.Y = 0;
        ScrollBar.Visible = false;
        ScrollBar.Enabled = false;
    }

    /// <summary>Adds a content child (positioned in content space, 0..contentHeight).</summary>
    public void Add(UIElement child) => Inner.AddChild(child);

    /// <summary>Sets the total content height; shows/hides the scrollbar accordingly.</summary>
    public void SetContentHeight(int contentHeight)
    {
        Inner.Height = Math.Max(contentHeight, Height);
        var needs = contentHeight > Height;
        ScrollBar.Visible = needs;
        ScrollBar.Enabled = needs;

        if (needs)
        {
            ScrollBar.TotalItems = contentHeight;
            ScrollBar.VisibleItems = Height;
            ScrollBar.MaxValue = contentHeight - Height;

            if (-Inner.Y > ScrollBar.MaxValue)
            {
                Inner.Y = -ScrollBar.MaxValue;
                ScrollBar.Value = ScrollBar.MaxValue;
            }
        } else
        {
            Inner.Y = 0;
            ScrollBar.Value = 0;
        }
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (!ScrollBar.Visible)
            return;

        var nv = Math.Clamp(ScrollBar.Value - e.Delta * SCROLL_STEP, 0, ScrollBar.MaxValue);

        if (nv != ScrollBar.Value)
        {
            ScrollBar.Value = nv;
            Inner.Y = -nv;
        }

        e.Handled = true;
    }
}
