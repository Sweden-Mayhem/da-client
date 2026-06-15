using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework.Graphics;

namespace Chaos.Client.Models;

public sealed class EquipmentDragPayload
{
    public required EquipmentSlot Slot { get; init; }
    public Texture2D? Icon { get; init; }
}
