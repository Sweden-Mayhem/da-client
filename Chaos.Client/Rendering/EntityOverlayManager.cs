#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.World.ViewPort;
using Chaos.Client.Models;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>
///     Manages all entity-anchored overlays: chat bubbles, health bars, name tags, group box texts, and chant overlays.
///     Each overlay is keyed by entity ID. Chat bubbles and chant overlays interlock, adding one replaces the other.
/// </summary>
public sealed class EntityOverlayManager
{
    //health bar y offset from entity tile center (higher = further above entity).
    //for creature sprites this is the "baseline" height, actual position is averaged with sprite top.
    private const int HEALTH_BAR_Y_OFFSET = 61;

    //chant overlay y offset from entity tile center. A bit BELOW the name tag (smaller offset = lower on screen) so the
    //chant starts just under "where the name is" and drifts up from there.
    private const int CHANT_Y_OFFSET = NAME_TAG_Y_OFFSET - 10;

    //chat bubble y offset from entity tile center. baseline for creature sprites (averaged with sprite top).
    //50 sits the tail on a player's head; monsters shift down a little too since this is their averaged baseline.
    private const int CHAT_BUBBLE_Y_OFFSET = 50;
    //extra native-pixel gap added to the name-height lift when a chat bubble and a name tag show at once
    private const int NAME_BUBBLE_GAP = 4;

    //name tag y offset from entity tile center (above health bars)
    private const int NAME_TAG_Y_OFFSET = 58;

    //group box y offset, sits 2px above name tags
    private const int GROUP_BOX_Y_OFFSET = 60;
    private static readonly Color NAME_TAG_SHADOW_COLOR = new(20, 20, 20);

    //name tags now draw at NATIVE resolution in the UI pass (TrueType, like the chat bubbles) so they stay crisp at
    //any window size. This is the native font size at 1.0x and the 8-direction (cardinal + diagonal) outline offsets
    //used to emboss them - a full surround so the name stays readable over any background.
    private const int NAME_TAG_FONT_SIZE = 17; //~10% larger than the old 15 (the Options "Names font size" slider still scales this)
    private const int CHANT_FONT_SIZE = 15; //TrueType pixel size for the overhead chant lines, drawn natively
    private const float CHANT_RISE_PIXELS = 34f; //how far (native px) a chant line drifts upward over its lifetime
    private const float CHANT_FADE_START = 0.35f; //fraction of lifetime after which the line begins fading out
    private static readonly Vector2[] NAME_TAG_OUTLINE =
    [
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)
    ];

    private readonly Dictionary<uint, ChantText> ChantOverlays = [];
    private readonly Dictionary<uint, ChatBubble> ChatBubbles = [];
    private readonly Dictionary<uint, GroupBox> GroupBoxes = [];
    private readonly Dictionary<uint, HealthBar> HealthBars = [];

    /// <summary>
    ///     Adds a chant overlay for the given entity, replacing any existing chant or chat bubble. A null/empty message clears
    ///     the existing chant without creating a new one.
    /// </summary>
    public void AddChantOverlay(uint entityId, string? message)
    {
        RemoveChantOverlay(entityId);

        if (string.IsNullOrEmpty(message))
            return;

        //chant replaces any active chat bubble
        RemoveChatBubble(entityId);

        ChantOverlays[entityId] = ChantText.Create(entityId, message);
    }

    /// <summary>
    ///     Adds a chat bubble for the given entity, replacing any existing bubble or chant overlay.
    /// </summary>
    public void AddChatBubble(uint entityId, string message, bool isShout)
    {
        //chat bubble replaces any active chant overlay
        RemoveChantOverlay(entityId);

        if (ChatBubbles.TryGetValue(entityId, out var existing))
            existing.Dispose();

        //"Bubble fade after" of 0 disables over-head bubbles entirely
        if (ClientSettings.BubbleFadeSeconds <= 0)
        {
            ChatBubbles.Remove(entityId);

            return;
        }

        ChatBubbles[entityId] = ChatBubble.Create(entityId, message, isShout);

        //a speech bubble appeared - play the chat cue (pitch-varied, at the "Chat" volume)
        SoundSystem.PlayChatBubble();
    }

    /// <summary>
    ///     Adds or resets a health bar for the given entity.
    /// </summary>
    public void AddOrResetHealthBar(uint entityId, byte healthPercent)
    {
        if (HealthBars.TryGetValue(entityId, out var existing))
            existing.Reset(healthPercent);
        else
            HealthBars[entityId] = new HealthBar(entityId, healthPercent);
    }

    /// <summary>
    ///     Disposes and clears all overlay caches. Call on map change or unload.
    /// </summary>
    public void Clear()
    {
        foreach (var bubble in ChatBubbles.Values)
            bubble.Dispose();
        ChatBubbles.Clear();

        foreach (var bar in HealthBars.Values)
            bar.Dispose();
        HealthBars.Clear();

        foreach (var overlay in ChantOverlays.Values)
            overlay.Dispose();
        ChantOverlays.Clear();

        GroupBoxes.Clear();
    }

    /// <summary>
    ///     Draws the world-pass overlays (chant overlays → health bars → group box texts). Call within a SpriteBatch
    ///     Begin/End block with the camera transform applied. Chat bubbles AND name tags are NOT drawn here - they render
    ///     at native resolution in the UI pass (see <see cref="DrawChatBubblesNative" /> / <see cref="DrawNameTagsNative" />)
    ///     so their TrueType text stays crisp at any window size.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Camera camera, int mapHeight)
    {
        //read the authoritative per-frame snapshot once; sub-methods reuse it
        var sortedEntities = WorldState.CurrentFrame.SortedEntities;

        //chant overlays are no longer drawn here - they render as crisp TTF in the native pass (DrawChantOverlaysNative)

        foreach (var bar in HealthBars.Values)
            bar.Draw(spriteBatch);

        DrawGroupBoxTexts(
            spriteBatch,
            camera,
            mapHeight,
            sortedEntities);
    }

    /// <summary>
    ///     Draws the chat bubbles at native resolution. Call from the native UI pass (no camera transform); their X/Y were
    ///     converted from world to backbuffer coordinates in <see cref="UpdateChatBubbles" />.
    /// </summary>
    public void DrawChatBubblesNative(SpriteBatch spriteBatch)
    {
        foreach (var bubble in ChatBubbles.Values)
            bubble.Draw(spriteBatch);
    }

    private void DrawGroupBoxTexts(
        SpriteBatch spriteBatch,
        Camera camera,
        int mapHeight,
        IReadOnlyList<WorldEntity> sortedEntities)
    {
        var hoveredGroupBoxId = WorldState.CurrentFrame.HoveredGroupBoxId;

        for (var i = 0; i < sortedEntities.Count; i++)
        {
            var entity = sortedEntities[i];

            if (entity.Type != ClientEntityType.Aisling)
                continue;

            if (string.IsNullOrEmpty(entity.GroupBoxText))
            {
                //Entity previously had a groupbox that was cleared (server sent a
                //DisplayAisling with empty GroupBoxText). Drop the cached panel
                //so it stops rendering and also falls out of hit-testing via
                //GetGroupBoxAtScreen.
                GroupBoxes.Remove(entity.Id);

                continue;
            }

            if (!GroupBoxes.TryGetValue(entity.Id, out var groupBox))
            {
                groupBox = new GroupBox(entity.Id);
                GroupBoxes[entity.Id] = groupBox;
            }

            groupBox.UpdateText(entity.GroupBoxText);
            groupBox.IsHovered = hoveredGroupBoxId == entity.Id;

            //position panel centered on entity, bottom edge at y offset
            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var entityWorldX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH + entity.VisualOffset.X;
            var entityWorldY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT + entity.VisualOffset.Y - GROUP_BOX_Y_OFFSET;

            var screenPos = camera.WorldToScreen(
                new Vector2(entityWorldX - GroupBox.PANEL_WIDTH / 2f, entityWorldY - GroupBox.PANEL_HEIGHT));

            groupBox.X = (int)screenPos.X;
            groupBox.Y = (int)screenPos.Y;
            groupBox.Draw(spriteBatch);
        }
    }

    /// <summary>
    ///     Draws entity name tags at native resolution (TrueType, crisp at any window size), each with a dark outline so
    ///     the name never blends into the background. Other players' names show always; NPC merchants show only while
    ///     hovered (and not during cast targeting / dragging), never the player's own character. Monsters get NO tag: the
    ///     DA visible-entities packet only sends a name for Merchants, so a monster's name arrives empty. The size is
    ///     <see cref="ClientSettings.NameFontScale" /> (Options "Names font size"). Call from the native UI pass.
    /// </summary>
    //whether an entity's name tag is currently shown: other players always; an NPC merchant only while hovered (and not
    //during cast targeting / dragging), never the player's own character, never a nameless monster. Shared by the name-tag
    //render and the chat-bubble layout, so a bubble can be lifted clear of the name when both are on screen.
    private static bool NameTagVisible(WorldEntity entity, uint? hoveredEntityId, uint playerEntityId, bool showTintHighlight)
    {
        var isMerchant = entity is { Type: ClientEntityType.Creature, NameTagStyle: NameTagStyle.NeutralHover };

        if ((entity.Type != ClientEntityType.Aisling) && !isMerchant)
            return false;

        if (string.IsNullOrEmpty(entity.Name))
            return false;

        var isHoverOnly = entity.NameTagStyle is NameTagStyle.NeutralHover or NameTagStyle.FriendlyHover;

        return !isHoverOnly || (!showTintHighlight && (hoveredEntityId == entity.Id) && (entity.Id != playerEntityId));
    }

    public void DrawNameTagsNative(SpriteBatch spriteBatch, Camera camera, int mapHeight, CreatureRenderer creatureRenderer)
    {
        if (!TtfTextRenderer.Available)
            return;

        var sortedEntities = WorldState.CurrentFrame.SortedEntities;
        var showTintHighlight = WorldState.CurrentFrame.ShowTintHighlight;
        var hoveredEntityId = WorldState.CurrentFrame.HoveredEntityId;
        var playerEntityId = WorldState.PlayerEntityId;
        var dr = ChaosGame.WorldDrawRect;
        var fontSize = Math.Max(8, (int)MathF.Round(NAME_TAG_FONT_SIZE * ClientSettings.NameFontScale));

        for (var i = 0; i < sortedEntities.Count; i++)
        {
            var entity = sortedEntities[i];

            if (!NameTagVisible(entity, hoveredEntityId, playerEntityId, showTintHighlight))
                continue;

            var glyphs = TtfTextRenderer.GetLine(entity.Name, fontSize);

            if (glyphs is null)
                continue;

            var nameColor = entity.NameTagStyle switch
            {
                NameTagStyle.NeutralHover  => LegendColors.CornflowerBlue,
                NameTagStyle.FriendlyHover => LegendColors.Lime,
                _                          => TextColors.Default
            };

            //anchor = a point above the entity's head in WORLD space, converted to native backbuffer coords (the same
            //transform the chat bubbles use). ResolveOverlayOffset lifts the tag above tall creature sprites.
            var offset = ResolveOverlayOffset(NAME_TAG_Y_OFFSET, entity, creatureRenderer);
            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var entityWorldX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH + entity.VisualOffset.X;
            var entityWorldY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT + entity.VisualOffset.Y - offset;

            var screenPos = camera.WorldToScreen(new Vector2(entityWorldX, entityWorldY));
            var nativeX = screenPos.X * dr.Width / Math.Max(1, ChaosGame.WorldRenderWidth) + dr.X;
            var nativeY = screenPos.Y * dr.Height / Math.Max(1, ChaosGame.WorldRenderHeight) + dr.Y;

            var pos = new Vector2((int)(nativeX - glyphs.Width / 2f), (int)nativeY);

            //dark 1px outline so the name stays readable over any background, then the colored name on top
            foreach (var off in NAME_TAG_OUTLINE)
                spriteBatch.Draw(glyphs, pos + off, NAME_TAG_SHADOW_COLOR);

            spriteBatch.Draw(glyphs, pos, nameColor);
        }
    }

    /// <summary>
    ///     Draws the chant overlays (the spell/skill chant lines above an entity's head) in TTF at native resolution. Call
    ///     from the native UI pass, mirroring the name tags: the head is anchored in world space, converted to backbuffer
    ///     coordinates, and the line block is stacked just above it - so the chant stays crisp instead of being upscaled
    ///     with the low-res world.
    /// </summary>
    public void DrawChantOverlaysNative(SpriteBatch spriteBatch, Camera camera, int mapHeight, CreatureRenderer creatureRenderer)
    {
        if (!TtfTextRenderer.Available || (ChantOverlays.Count == 0))
            return;

        var dr = ChaosGame.WorldDrawRect;
        var lineH = TtfTextRenderer.LineHeight(CHANT_FONT_SIZE);

        foreach (var overlay in ChantOverlays.Values)
        {
            var entity = WorldState.GetEntity(overlay.EntityId);

            if ((entity is null) || (overlay.Lines.Count == 0))
                continue;

            var offset = ResolveOverlayOffset(CHANT_Y_OFFSET, entity, creatureRenderer);
            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var entityWorldX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH + entity.VisualOffset.X;
            var entityWorldY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT + entity.VisualOffset.Y - offset;

            var screenPos = camera.WorldToScreen(new Vector2(entityWorldX, entityWorldY));
            var nativeX = screenPos.X * dr.Width / Math.Max(1, ChaosGame.WorldRenderWidth) + dr.X;
            var nativeY = screenPos.Y * dr.Height / Math.Max(1, ChaosGame.WorldRenderHeight) + dr.Y;

            //the line block sits just above the anchor; the long-chant (left-aligned) case keeps the block centered on
            //the entity by starting from its left edge. It drifts upward and fades out as the chant ages.
            var progress = overlay.Progress;
            var rise = (int)(progress * CHANT_RISE_PIXELS);
            var alpha = progress < CHANT_FADE_START ? 1f : 1f - (progress - CHANT_FADE_START) / (1f - CHANT_FADE_START);
            alpha = Math.Clamp(alpha, 0f, 1f);

            //start the block AT the anchor (name height) and drift it up over its lifetime, so the chant floats up out
            //of the name and fades, instead of sitting in a block above the head.
            var startY = (int)nativeY - rise;
            var maxW = 0;

            foreach (var l in overlay.Lines)
                maxW = Math.Max(maxW, TtfTextRenderer.MeasureWidth(l, CHANT_FONT_SIZE));

            var leftEdge = (int)(nativeX - maxW / 2f);
            var textColor = ChantText.ChantColor * alpha;
            var shadowColor = NAME_TAG_SHADOW_COLOR * alpha;

            for (var i = 0; i < overlay.Lines.Count; i++)
            {
                var tex = TtfTextRenderer.GetLine(overlay.Lines[i], CHANT_FONT_SIZE);

                if (tex is null)
                    continue;

                var lx = overlay.Centered ? (int)(nativeX - tex.Width / 2f) : leftEdge;
                var pos = new Vector2(lx, startY + (i * lineH));

                foreach (var off in NAME_TAG_OUTLINE)
                    spriteBatch.Draw(tex, pos + off, shadowColor);

                spriteBatch.Draw(tex, pos, textColor);
            }
        }
    }

    /// <summary>
    ///     Returns the entity ID and name if the given screen point hits a group box overlay. Used for click-to-view.
    /// </summary>
    /// <summary>
    ///     Returns the entity ID and name if the given viewport-relative point hits a group box overlay. Group box rects
    ///     are stored in the same coordinate space the world spriteBatch renders in (rebased by the viewport origin at
    ///     draw time), so callers with window-relative mouse coords must subtract the viewport origin before calling.
    /// </summary>
    public (uint EntityId, string EntityName)? GetGroupBoxAtScreen(int viewportX, int viewportY)
    {
        foreach ((var entityId, var groupBox) in GroupBoxes)
        {
            var rect = new Rectangle(
                groupBox.ScreenX,
                groupBox.ScreenY,
                GroupBox.PANEL_WIDTH,
                GroupBox.PANEL_HEIGHT);

            if (!rect.Contains(viewportX, viewportY))
                continue;

            var entity = WorldState.GetEntity(entityId);

            if (entity is null)
                continue;

            return (entityId, entity.Name);
        }

        return null;
    }

    private void RemoveChantOverlay(uint entityId)
    {
        if (ChantOverlays.Remove(entityId, out var existing))
            existing.Dispose();
    }

    private void RemoveChatBubble(uint entityId)
    {
        if (ChatBubbles.Remove(entityId, out var existing))
            existing.Dispose();
    }

    /// <summary>
    ///     Removes all overlays for a given entity (name tag, group box, chat bubble, health bar, chant). Call when an entity
    ///     is removed from the world.
    /// </summary>
    public void RemoveEntity(uint entityId)
    {
        GroupBoxes.Remove(entityId);
        RemoveChatBubble(entityId);
        RemoveChantOverlay(entityId);

        if (HealthBars.Remove(entityId, out var bar))
            bar.Dispose();
    }

    /// <summary>
    ///     Ticks bubble/bar/overlay timers, updates screen positions from entity world positions, and removes expired entries.
    /// </summary>
    public void Update(
        Camera camera,
        int mapHeight,
        CreatureRenderer creatureRenderer,
        GameTime gameTime)
    {
        UpdateChatBubbles(
            camera,
            mapHeight,
            creatureRenderer,
            gameTime);

        UpdateChantOverlays(
            camera,
            mapHeight,
            creatureRenderer,
            gameTime);

        UpdateHealthBars(
            camera,
            mapHeight,
            creatureRenderer,
            gameTime);
    }

    //for creature sprites, the overlay offset is the average of the baseline offset and the sprite's average top offset.
    //this lets small sprites pull overlays closer while tall sprites push them further up, without per-frame jitter.
    //for paperdoll aislings (and non-creature entities) the baseline offset is used unchanged.
    private static int ResolveOverlayOffset(int baselineOffset, WorldEntity entity, CreatureRenderer creatureRenderer)
    {
        if (!entity.IsRenderedAsCreatureSprite)
            return baselineOffset;

        var averageTop = creatureRenderer.GetAverageTopOffset(entity.SpriteId);

        return (baselineOffset + averageTop) / 2;
    }

    private void UpdateChantOverlays(
        Camera camera,
        int mapHeight,
        CreatureRenderer creatureRenderer,
        GameTime gameTime)
    {
        List<uint>? expired = null;

        foreach ((var entityId, var overlay) in ChantOverlays)
        {
            overlay.Update(gameTime);

            if (overlay.IsExpired)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var entity = WorldState.GetEntity(entityId);

            if (entity is null)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

            var offset = ResolveOverlayOffset(CHANT_Y_OFFSET, entity, creatureRenderer);

            var entityWorldX = tileCenterX + entity.VisualOffset.X;
            var entityWorldY = tileCenterY + entity.VisualOffset.Y - offset;

            var overlayX = entityWorldX - overlay.Width / 2f;
            var overlayY = entityWorldY - overlay.Height;

            var screenPos = camera.WorldToScreen(new Vector2(overlayX, overlayY));
            overlay.X = (int)screenPos.X;
            overlay.Y = (int)screenPos.Y;
        }

        if (expired is not null)
            foreach (var id in expired)
            {
                ChantOverlays[id]
                    .Dispose();
                ChantOverlays.Remove(id);
            }
    }

    private void UpdateChatBubbles(
        Camera camera,
        int mapHeight,
        CreatureRenderer creatureRenderer,
        GameTime gameTime)
    {
        List<uint>? expired = null;

        //for lifting a bubble clear of a visible name tag (same visibility rule the name render uses)
        var hoveredEntityId = WorldState.CurrentFrame.HoveredEntityId;
        var playerEntityId = WorldState.PlayerEntityId;
        var showTintHighlight = WorldState.CurrentFrame.ShowTintHighlight;

        foreach ((var entityId, var bubble) in ChatBubbles)
        {
            bubble.Update(gameTime);

            if (bubble.IsExpired)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var entity = WorldState.GetEntity(entityId);

            if (entity is null)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

            var offset = ResolveOverlayOffset(CHAT_BUBBLE_Y_OFFSET, entity, creatureRenderer);

            //anchor = the head point the bubble's tail tip should sit on, in WORLD space. The bubble itself is sized in
            //NATIVE pixels (drawn in the UI pass), so convert the anchor to backbuffer coords, then offset by the native
            //bubble size: centered horizontally, sitting fully above the anchor.
            var entityWorldX = tileCenterX + entity.VisualOffset.X;
            var entityWorldY = tileCenterY + entity.VisualOffset.Y - offset;

            var screenPos = camera.WorldToScreen(new Vector2(entityWorldX, entityWorldY));
            var dr = ChaosGame.WorldDrawRect;
            var nativeX = screenPos.X * dr.Width / Math.Max(1, ChaosGame.WorldRenderWidth) + dr.X;
            var nativeY = screenPos.Y * dr.Height / Math.Max(1, ChaosGame.WorldRenderHeight) + dr.Y;

            bubble.X = (int)(nativeX - bubble.Width / 2f);
            bubble.Y = (int)(nativeY - bubble.Height);

            //if this entity's NAME is also on screen, lift the bubble by one name-height (+ a small gap) so the name no
            //longer sits inside it. Just enough to clear the name, not the full anchor gap (which jumped it up too far).
            if (NameTagVisible(entity, hoveredEntityId, playerEntityId, showTintHighlight))
            {
                var nameFontSize = Math.Max(8, (int)MathF.Round(NAME_TAG_FONT_SIZE * ClientSettings.NameFontScale));
                bubble.Y -= TtfTextRenderer.LineHeight(nameFontSize) + NAME_BUBBLE_GAP;
            }

            //fade the bubble while the cursor is over it (in native UI coords, the space the bubble is drawn in) so the
            //player can see what is behind it
            bubble.Hovered = new Rectangle(bubble.X, bubble.Y, bubble.Width, bubble.Height).Contains(InputBuffer.MouseX, InputBuffer.MouseY);
        }

        if (expired is not null)
            foreach (var id in expired)
            {
                ChatBubbles[id]
                    .Dispose();
                ChatBubbles.Remove(id);
            }
    }

    private void UpdateHealthBars(
        Camera camera,
        int mapHeight,
        CreatureRenderer creatureRenderer,
        GameTime gameTime)
    {
        List<uint>? expired = null;

        foreach ((var entityId, var bar) in HealthBars)
        {
            bar.Update(gameTime);

            if (bar.IsExpired)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var entity = WorldState.GetEntity(entityId);

            if (entity is null)
            {
                (expired ??= []).Add(entityId);

                continue;
            }

            var offset = ResolveOverlayOffset(HEALTH_BAR_Y_OFFSET, entity, creatureRenderer);

            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapHeight);
            var entityWorldX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH + entity.VisualOffset.X;
            var entityWorldY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT + entity.VisualOffset.Y - offset;

            var screenPos = camera.WorldToScreen(new Vector2(entityWorldX - bar.Width / 2f, entityWorldY));
            bar.X = (int)screenPos.X + 1;
            bar.Y = (int)screenPos.Y;
        }

        if (expired is not null)
            foreach (var id in expired)
            {
                HealthBars[id]
                    .Dispose();
                HealthBars.Remove(id);
            }
    }
}