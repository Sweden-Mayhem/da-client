#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     A single legend mark row with an icon (UIImage) and text label (UILabel). The text is vertically centered relative
///     to the icon.
/// </summary>
public sealed class LegendMarkControl : UIPanel
{
    private const int ICON_TEXT_GAP = 5;

    private readonly UIImage Icon;
    private readonly UILabel Label;

    public LegendMarkControl()
    {
        Icon = new UIImage
        {
            Name = "MarkIcon"
        };

        //the legend list sits in the book's magnifying ScaleHost, so paint in crisp native TTF, not the upscaled bitmap font
        Label = new UILabel
        {
            Name = "MarkText"
        }.Native(11);

        AddChild(Icon);
        AddChild(Label);
    }

    public void Clear()
    {
        Icon.Texture = null;
        Label.Text = string.Empty;
    }

    public void SetMark(
        Texture2D? icon,
        string text,
        Color color,
        int iconWidth,
        int iconHeight)
    {
        Icon.Texture = icon;
        Icon.Width = iconWidth;
        Icon.Height = iconHeight;

        //the native-TTF clip = the label's own box, so a CHAR_HEIGHT-tall box cut descenders ("g","y"). Give it a few
        //extra pixels of clip height (centered against the icon) so the full glyph including the descender shows.
        var textHeight = TextRenderer.CHAR_HEIGHT + 4;
        Label.X = iconWidth + ICON_TEXT_GAP;
        Label.Y = (iconHeight - textHeight) / 2;
        Label.Width = Width - Label.X;
        Label.Height = textHeight;
        Label.Text = text;
        Label.ForegroundColor = color;
    }

}