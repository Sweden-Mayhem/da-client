namespace Chaos.Client.Controls.Components;

/// <summary>
///     A transparent, non-interactive rectangular region that gives a title+body info tooltip on hover. Use it to put
///     hover information over parts of a panel's BAKED background art (text/labels painted into the image, like the
///     "STR"/"HP"/"Level" words on the stats panel) where there is no real control to hover. It renders nothing and
///     never handles input; the unified tooltip resolver finds it by polling the panel via
///     <see cref="UIPanel.HitInfoHotspot" />. Add one as a child of any panel, positioned over the art region.
/// </summary>
public sealed class InfoHotspot : UIElement
{
    public string Title { get; }
    public string Body { get; }

    public InfoHotspot(string title, string body)
    {
        Title = title;
        Body = body;
        IsHitTestVisible = false; //never capture clicks or drag, this only feeds the tooltip resolver
        SuppressDraw = true;      //draws nothing (it sits over baked art)
    }

    //hit-test directly against ScreenBounds: we never draw, so ClipRect (used by the base ContainsPoint) is never
    //recomputed. ScreenBounds is in the same panel-local space the value-label hit-tests use, so a resolver that
    //maps the cursor for the value labels (e.g. MapToStats) feeds this correctly too.
    public override bool ContainsPoint(int screenX, int screenY)
    {
        if (!Visible)
            return false;

        var b = ScreenBounds;

        return (screenX >= b.X) && (screenX < b.Right) && (screenY >= b.Y) && (screenY < b.Bottom);
    }
}
