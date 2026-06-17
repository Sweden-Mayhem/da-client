#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     A horizontal float slider with a draggable thumb, drawn in the dark/bronze <see cref="DraggableWindow" /> style.
///     Reports a continuous (or step-snapped) value in [<see cref="Min" />, <see cref="Max" />] via <see cref="Changed" />.
/// </summary>
public sealed class Slider : UIPanel
{
    private const int TRACK_H = 4;
    private const int THUMB_W = 9;
    private const int THUMB_H = 16;

    private static readonly Color TrackClr = new(54, 46, 32);
    private static readonly Color FillClr = new(120, 98, 60);
    private static readonly Color ThumbClr = new(196, 168, 110);
    private static readonly Color BorderClr = new(88, 72, 46);

    private readonly float Min;
    private readonly float Max;
    private readonly float Step; //0 = continuous

    private bool Dragging;

    public bool IsHovered { get; private set; }

    public float Value { get; private set; }

    public event Action<float>? Changed;

    public Slider(int width, float min, float max, float value, float step = 0f)
    {
        Width = width;
        Height = THUMB_H;
        Min = min;
        Max = max;
        Step = step;
        Value = Math.Clamp(value, min, max);
    }

    private int TrackLeft => THUMB_W / 2;
    private int TrackWidth => Width - THUMB_W;
    private float Ratio => Max > Min ? (Value - Min) / (Max - Min) : 0f;

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        UpdateClipRect();

        var trackY = ScreenY + (Height - TRACK_H) / 2;
        var fillW = (int)(Ratio * TrackWidth);

        DrawRectClipped(spriteBatch, new Rectangle(ScreenX + TrackLeft, trackY, TrackWidth, TRACK_H), TrackClr);
        DrawRectClipped(spriteBatch, new Rectangle(ScreenX + TrackLeft, trackY, fillW, TRACK_H), FillClr);

        //thumb: a filled bronze box with a darker 1px border
        var tx = ScreenX + TrackLeft + fillW - THUMB_W / 2;
        var ty = ScreenY + (Height - THUMB_H) / 2;
        DrawRectClipped(spriteBatch, new Rectangle(tx, ty, THUMB_W, THUMB_H), ThumbClr);
        DrawRectClipped(spriteBatch, new Rectangle(tx, ty, THUMB_W, 1), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(tx, ty + THUMB_H - 1, THUMB_W, 1), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(tx, ty, 1, THUMB_H), BorderClr);
        DrawRectClipped(spriteBatch, new Rectangle(tx + THUMB_W - 1, ty, 1, THUMB_H), BorderClr);

        var capturedElement = InputDispatcher.Instance?.CapturedElement;

        if (capturedElement == this || capturedElement==null && IsHovered)
        {
            DrawRect(spriteBatch, new Rectangle(ScreenX, ScreenY, Width, Height), ImageUtil.ButtonHoverTint);
        }
    }

    public override void OnMouseEnter()
    {
        IsHovered = true;
    }

    public override void OnMouseLeave()
    {
        IsHovered = false;
    }

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (e.Button != MouseButton.Left)
            return;

        Dragging = true;
        SetFromMouse(e.ScreenX);
        e.Handled = true;
    }

    public override void OnMouseMove(MouseMoveEvent e)
    {
        if (!Dragging)
            return;

        SetFromMouse(e.ScreenX);
        e.Handled = true;
    }

    public override void OnMouseUp(MouseUpEvent e)
    {
        if (e.Button == MouseButton.Left)
            Dragging = false;
    }

    private void SetFromMouse(int screenX)
    {
        var ratio = Math.Clamp((float)(screenX - (ScreenX + TrackLeft)) / TrackWidth, 0f, 1f);
        var value = Min + ratio * (Max - Min);

        if (Step > 0f)
            value = Min + (float)Math.Round((value - Min) / Step) * Step;

        value = Math.Clamp(value, Min, Max);

        if (Math.Abs(value - Value) < 0.0001f)
            return;

        Value = value;
        Changed?.Invoke(value);
    }
}
