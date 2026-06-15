#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     A small dark/bronze checkbox with a label to its right, matching the <see cref="DraggableWindow" /> chrome.
///     Reusable in the menu/options UI. Clicking anywhere on it toggles and raises <see cref="Changed" />.
/// </summary>
public sealed class Checkbox : UIPanel
{
    private const int BOX = 16;
    private const int ROW_H = BOX + 6; //the control is a touch taller than the 16px box so a label descender ('g') isn't clipped

    private static readonly Color BorderClr = new(88, 72, 46);
    private static readonly Color MarkClr = new(196, 168, 110);
    private static readonly Color BoxBg = new(18, 16, 12);

    private readonly UILabel Label;

    public bool Checked { get; private set; }

    /// <summary>Sets the checked state WITHOUT raising <see cref="Changed" /> (for re-using one checkbox across contexts).</summary>
    public void SetChecked(bool value) => Checked = value;

    public event Action<bool>? Changed;

    public Checkbox(string label, int width, bool isChecked)
    {
        Width = width;
        Height = ROW_H;
        Checked = isChecked;

        Label = new UILabel
        {
            Text = label,
            X = BOX + 8,
            Y = 0,
            Width = width - BOX - 8,
            Height = ROW_H,
            CustomFontSize = MenuButton.MenuFontSize, //match the rest of the menu/options UI (Cinzel)
            VerticalAlignment = VerticalAlignment.Center,
            ForegroundColor = new Color(200, 198, 190),
            IsHitTestVisible = false
        };

        AddChild(Label);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        //draws the label child + updates ClipRect
        base.Draw(spriteBatch);

        var x = ScreenX;
        var y = ScreenY + (Height - BOX) / 2; //box stays 16px, centered in the slightly taller control

        //box fill + 1px border
        DrawRectClipped(spriteBatch, new Rectangle(x, y, BOX, BOX), BoxBg);
        DrawRectClipped(spriteBatch, new Rectangle(x, y, BOX, 1), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(x, y + BOX - 1, BOX, 1), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(x, y, 1, BOX), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(x + BOX - 1, y, 1, BOX), BorderClr);

        if (Checked)
            DrawRectClipped(spriteBatch, new Rectangle(x + 4, y + 4, BOX - 8, BOX - 8), MarkClr);
    }

    public override void OnClick(ClickEvent e)
    {
        if (e.Button != MouseButton.Left)
            return;

        Checked = !Checked;
        Changed?.Invoke(Checked);
        e.Handled = true;
    }
}
