#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Controls.World.Popups.Dialog;
using Chaos.Client.Data;
using Chaos.Client.Models;
using Chaos.Client.Rendering;
using Chaos.Client.Rendering.Models;
using Chaos.Client.Rendering.Utility;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    public bool UsesNativeUi => true;

    //world renders into the 640x480 target here; the UI is drawn separately at native resolution in DrawNativeUi.
    public void DrawWorld(SpriteBatch spriteBatch, GameTime gameTime)
    {
        //keep the camera viewport matched to the (window-filling) render target, so the player stays centered and
        //the expanded margins fill with tiles
        if ((Camera.ViewportWidth != ChaosGame.WorldRenderWidth) || (Camera.ViewportHeight != ChaosGame.WorldRenderHeight))
            Camera.Resize(ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);

        //sort once per frame, cached via dirty flag and reused by all draw sub-passes
        var sortedEntities = WorldState.CurrentFrame.SortedEntities;

        //pre-render silhouettes (local-player overdraw) before world drawing
        //matches retail which redraws the local player at 50% alpha after foregrounds
        if (MapFile is not null && MapPreloaded)
        {
            SilhouetteRenderer.Clear();

            var player = WorldState.GetPlayerEntity();

            //silhouette the player unconditionally. when non-transparent and in the open, the overdraw
            //blends with the identical stripe-pass pixel (no visible change). behind foregrounds, the
            //overdraw shows the player through walls at 50%. transparent players compound multiplicatively:
            //~50% visible in the open, ~25% visible behind walls.
            if (player is not null)
                SilhouetteRenderer.AddSilhouette(player.Id);

            //also reveal enemies/NPCs and ground items through foreground, the same way the player is revealed: the
            //overlay is the same sprite at SILHOUETTE_ALPHA drawn over the already-drawn entity, so it is invisible in
            //the open and shows the entity at ~50% where a wall/foreground tile would otherwise hide it. Skip hidden /
            //stealthed / dead entities so we never reveal something that is meant to be unseen.
            foreach (var entity in sortedEntities)
            {
                if ((player is not null) && (entity.Id == player.Id))
                    continue;

                if (entity.IsHidden || entity.IsTransparent || entity.IsDead)
                    continue;

                //friendly NPCs (non-combat creatures: merchants, decorative critters) stay hidden behind walls unless
                //the player opts in. Enemies (CreatureType.Normal) and ground items always show through.
                if (!ClientSettings.ShowNpcsBehindWalls
                    && (entity.Type == ClientEntityType.Creature)
                    && (entity.CreatureType != CreatureType.Normal))
                    continue;

                if (entity.Type is ClientEntityType.Creature or ClientEntityType.GroundItem)
                    SilhouetteRenderer.AddSilhouette(entity.Id);
            }

            //pre-render silhouettes into a screen-sized rt (must happen before main rt drawing starts,
            //because rt switching discards the main rt's contents). DrawingForSilhouette routes transparent
            //entities through TRANSPARENT_SILHOUETTE_ALPHA so they compound correctly with the overlay.
            SilhouetteRenderer.PreRenderSilhouettes(batch =>
            {
                batch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, GlobalSettings.Sampler);
                DrawingForSilhouette = true;

                try
                {
                    foreach (var entityId in SilhouetteRenderer.SilhouetteEntityIds)
                    {
                        var silEntity = WorldState.GetEntity(entityId);

                        if (silEntity is not null)
                            DrawEntity(batch, silEntity);
                    }
                } finally
                {
                    DrawingForSilhouette = false;
                    batch.End();
                }
            });
        }

        //pass 1: world rendering, clipped to the hud viewport area with camera transform
        if (MapFile is not null && MapPreloaded)
        {
            var viewportRect = WorldRenderRect;
            Device.ScissorRectangle = viewportRect;

            var transform = Matrix.CreateTranslation(viewportRect.X, viewportRect.Y, 0);

            //background tiles + tile cursor: batched (many draws, no blend changes)
            spriteBatch.Begin(samplerState: GlobalSettings.Sampler, rasterizerState: ScissorRasterizerState, transformMatrix: transform);

            MapRenderer.DrawBackground(
                spriteBatch,
                MapFile,
                Camera,
                AnimationTick);
            DrawTileCursor(spriteBatch);
            spriteBatch.End();

            //foreground, entities, effects: immediate mode (per-stripe ordering, blend switches for additive effects)
            spriteBatch.Begin(
                SpriteSortMode.Immediate,
                BlendState.AlphaBlend,
                GlobalSettings.Sampler,
                null,
                ScissorRasterizerState,
                null,
                transform);
            DrawForegroundAndEntities(spriteBatch, sortedEntities);
            DrawTileCursorOverlay(spriteBatch); //cursor shows through walls/foreground, like the silhouetted entities
            DrawEffectsThroughWalls(spriteBatch); //spell/effect animations show through walls too, at the same opacity
            SilhouetteRenderer.DrawSilhouettes(spriteBatch);
            spriteBatch.End();

            //deferred lighting: night ambient + glow pools, multiplied over the world. Per lamp the glow is ray-marched
            //against the FEET of foreground below it (DrawLightOccluderMap) so those bottom pixels cast a real shadow
            //past them; then the object's full silhouette erases the glow so it isn't lit (DrawLightSilhouetteErase).
            if (DarknessRenderer.IsActive)
            {
                var occluderPad = Math.Clamp(TileLights.GatherMarginTiles * 14, 128, 512);

                LightingRenderer.Render(
                    spriteBatch,
                    Game.WorldTarget,
                    WorldRenderRect,
                    DarknessRenderer.AmbientMultiplier,
                    Lighting.Sources,
                    occluderPad,
                    DebugSettings.LightShadows, //blur only matters for softening shadow edges; skip it when shadows are off
                    light => DrawLightOccluderMap(spriteBatch, light, occluderPad),
                    light => DrawLightSilhouetteErase(spriteBatch, light, transform),
                    DarknessRenderer.GetSweepAmbientTexture()); //spatial ambient during a transition: day/night sweeps in from a top corner
            }

            //dusk/dawn glow, a very subtle warm bloom over the twilight-lit world (gates itself on DuskGlow). Runs
            //before the cool night-shadow tint so the bloom samples the warm lit world, not the cooled shadows
            DrawDuskBloom(spriteBatch);

            //night-shadow grade: mute and cool the shadows (after the bloom, using this frame's light buffer). The cool
            //floor is geometry-cropped to the map's screen parallelogram (its 4 corner tips, N/E/S/W) so the blue filter
            //never bleeds into the off-map void, regardless of any transparent holes in the floor sprites
            if (DarknessRenderer.IsActive)
                LightingRenderer.ApplyNightShadowTint(spriteBatch, Game.WorldTarget, WorldRenderRect, DarknessRenderer.NightShadowTint, GetMapScreenQuad());

            //night vignette, black edges pressing in as it gets dark (gates itself on the same night ramp)
            DrawNightVignette(spriteBatch);

            //weather overlay drawn after darkness so snowflakes/rain remain visible on dark maps
            if (WeatherRenderer.IsActive)
            {
                spriteBatch.Begin(
                    blendState: BlendState.AlphaBlend,
                    samplerState: GlobalSettings.Sampler,
                    rasterizerState: ScissorRasterizerState);
                var weatherViewport = WorldRenderRect;
                WeatherRenderer.Draw(spriteBatch, weatherViewport);
                spriteBatch.End();
            }

            //blind overlay: black out viewport, then redraw only the player character. drawn before
            //entity overlays so chat bubbles, name tags, chant text, etc. remain visible while blinded,
            //matching retail (which implements blind as a per-entity darkness mask rather than a
            //viewport fill, so its independent overlay panes are unaffected)
            if (WorldState.Attributes.Current?.Blind is true)
            {
                spriteBatch.Begin(
                    blendState: BlendState.AlphaBlend,
                    samplerState: GlobalSettings.Sampler,
                    rasterizerState: ScissorRasterizerState);
                RenderHelper.DrawRect(spriteBatch, WorldRenderRect, Color.Black);
                spriteBatch.End();

                var player = WorldState.GetPlayerEntity();

                if (player is not null)
                {
                    spriteBatch.Begin(
                        SpriteSortMode.Immediate,
                        BlendState.AlphaBlend,
                        GlobalSettings.Sampler,
                        null,
                        ScissorRasterizerState,
                        null,
                        transform);
                    DrawEntity(spriteBatch, player);
                    spriteBatch.End();
                }
            }

            //entity overlays (chat bubbles, health bars, name tags, chant text) drawn after darkness
            //so light level doesn't tint them, and after blind so they remain visible while blinded
            spriteBatch.Begin(
                SpriteSortMode.Immediate,
                BlendState.AlphaBlend,
                GlobalSettings.Sampler,
                null,
                ScissorRasterizerState,
                null,
                transform);

            Overlays.Draw(spriteBatch, Camera, MapFile.Height);
            spriteBatch.End();

            //spotlight the dialog speaker: dim the world (same look as the dialog dimmer) and re-draw the speaker NPC bright
            //on top, so it stands out above the darkening. World pass = correct world position + scale for the creature.
            if (SpeakerSpotlightActive)
            {
                spriteBatch.Begin(
                    SpriteSortMode.Immediate,
                    BlendState.AlphaBlend,
                    GlobalSettings.Sampler,
                    null,
                    ScissorRasterizerState,
                    null,
                    transform);
                DrawSpeakerSpotlight(spriteBatch);
                spriteBatch.End();
            }

            //snapshot draw count before debug draws so the reported count excludes debug visualizations
            DebugOverlay.SnapshotDrawCount();

            //debug overlay: entity hitboxes, tile grid, etc.
            if (DebugOverlay.IsActive)
            {
                spriteBatch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.AlphaBlend,
                    GlobalSettings.Sampler,
                    null,
                    ScissorRasterizerState,
                    null,
                    transform);

                DebugRenderer.Draw(
                    spriteBatch,
                    Camera,
                    MapFile,
                    MapRenderer.ForegroundExtraMargin,
                    sortedEntities,
                    WorldState.GetPlayerEntity(),
                    EntityHitBoxes,
                    WorldState.CurrentFrame.HoveredTile);
                spriteBatch.End();
            }
        }

        //tab map overlay drawn on top of world, under hud
        //tabmaprenderer manages its own spritebatch begin/end blocks (stencil passes for entity overlap)
        //NoTabMap map flag (0x40) suppresses both the toggle (InputHandlers) and the render
        //while an NPC dialog is open it hides like the rest of the HUD (OpenFraction stays > 0 through the open and close
        //animation, so it stays hidden until the dialog has fully closed, then reappears)
        var dialogOpenFraction = NpcSessionHost?.OpenFraction ?? 0f;

        if (TabMapVisible && MapFile is not null && !CurrentMapFlags.HasFlag(MapFlags.NoTabMap) && (dialogOpenFraction <= 0f))
        {
            var player = WorldState.GetPlayerEntity();

            //no player → no tab map this frame (avoids stamping baseline at (0,0) during transitions)
            if (player is not null)
            {
                var viewport = WorldRenderRect;
                var px = player.TileX;
                var py = player.TileY;

                var sourceCount = sortedEntities.Count;

                if (TabMapEntities.Length < sourceCount)
                    TabMapEntities = new TabMapEntity[sourceCount];

                var entityCount = 0;

                for (var i = 0; i < sourceCount; i++)
                {
                    var e = sortedEntities[i];

                    if (e.IsHidden)
                        continue;

                    TabMapEntities[entityCount++] = new TabMapEntity(
                        e.TileX,
                        e.TileY,
                        e.Type,
                        e.Id,
                        e.CreatureType);
                }

                TabMapRenderer.Draw(
                    spriteBatch,
                    Device,
                    viewport,
                    px,
                    py,
                    TabMapEntities,
                    entityCount,
                    WorldState.PlayerEntityId,
                    DarknessRenderer.IsFullBlackDark,
                    Lighting.Sources,
                    LightingSystem.BaselineVisibilityOffsets);
            }
        }

    }

    //UI overlay at native window resolution (drawn after the world target is stretched to fill the window).
    public void DrawNativeUi(SpriteBatch spriteBatch, GameTime gameTime)
    {
        var openFraction = NpcSessionHost?.OpenFraction ?? 0f;

        if (openFraction > 0f)
        {
            //dialog is opening/open: render all HUD children to an offscreen target so they can be drawn at
            //reduced alpha, while the dialog system (ZIndex >= 149999) draws at full alpha on top.
            var device = spriteBatch.GraphicsDevice;

            if (HudRenderTarget is null || HudRenderTarget.IsDisposed
                || (HudRenderTarget.Width != ChaosGame.UiWidth) || (HudRenderTarget.Height != ChaosGame.UiHeight))
            {
                HudRenderTarget?.Dispose();
                //PreserveContents: ScaleHost and DraggableWindow both call SetRenderTarget internally and restore via
                //GetRenderTargets()/SetRenderTargets(). Without PreserveContents, each such detour would discard the
                //pixels already drawn to this target, making earlier-drawn HUD elements vanish.
                HudRenderTarget = new RenderTarget2D(device, ChaosGame.UiWidth, ChaosGame.UiHeight,
                    false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }

            device.SetRenderTarget(HudRenderTarget);
            device.Clear(Color.Transparent);
            spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            DrawCameraEffects(spriteBatch);
            DrawDeathVignette(spriteBatch);
            //cast bar has the LOWEST UI priority: drawn first so every overlay, window, and tooltip sits on top of it
            DrawCastBar(spriteBatch);
            if (!NpcSession.Visible)
                Overlays.DrawChatBubblesNative(spriteBatch);
            if (MapFile is not null)
            {
                Overlays.DrawNameTagsNative(spriteBatch, Camera, MapFile.Height, Game.CreatureRenderer);
                Overlays.DrawChantOverlaysNative(spriteBatch, Camera, MapFile.Height, Game.CreatureRenderer);
            }
            Root!.EnsureChildOrder();

            foreach (var child in Root.Children)
                if (child.Visible && !child.SuppressDraw && (child.ZIndex < 149_999))
                    child.Draw(spriteBatch);

            DrawTargetingCursor(spriteBatch, gameTime);
            DrawDragIcon(spriteBatch);
            spriteBatch.End();

            //back to the backbuffer: draw HUD at faded alpha, then dialog system at full alpha
            device.SetRenderTarget(null);
            spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            spriteBatch.Draw(HudRenderTarget, new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight), Color.White * (1f - openFraction));

            foreach (var child in Root.Children)
                if (child.Visible && !child.SuppressDraw && (child.ZIndex >= 149_999))
                    child.Draw(spriteBatch);

            if ((NpcSessionHost is not null) && NpcSessionHost.Visible)
                NpcSession.DrawTextNative(spriteBatch, NpcSessionHost.ScreenX, NpcSessionHost.ScreenY, NpcSessionHost.Scale, NpcSessionHost.OpenFraction);

            ItemTooltip.Draw(spriteBatch);
            spriteBatch.End();
        }
        else
        {
            //normal path: no dialog, draw everything in one pass
            spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            DrawCameraEffects(spriteBatch);
            DrawDeathVignette(spriteBatch);
            //cast bar has the LOWEST UI priority: drawn first so every overlay, window, and tooltip sits on top of it
            DrawCastBar(spriteBatch);
            if (!NpcSession.Visible)
                Overlays.DrawChatBubblesNative(spriteBatch);
            if (MapFile is not null)
            {
                Overlays.DrawNameTagsNative(spriteBatch, Camera, MapFile.Height, Game.CreatureRenderer);
                Overlays.DrawChantOverlaysNative(spriteBatch, Camera, MapFile.Height, Game.CreatureRenderer);
            }
            Root!.Draw(spriteBatch);
            if ((NpcSessionHost is not null) && NpcSessionHost.Visible)
                NpcSession.DrawTextNative(spriteBatch, NpcSessionHost.ScreenX, NpcSessionHost.ScreenY, NpcSessionHost.Scale, NpcSessionHost.OpenFraction);

            ItemTooltip.Draw(spriteBatch);
            DrawTargetingCursor(spriteBatch, gameTime);
            DrawDragIcon(spriteBatch);
            spriteBatch.End();
        }
    }

    private const int CAST_BAR_FONT_SIZE = 16;

    //a cast progress bar while a multi-line spell is being chanted, so the few-second delay before the spell
    //fires reads as casting instead of nothing happening. Centered horizontally, just above the bottom hotbars
    //the spell name sits above the bar. Hidden for instant (0-line) casts (they never chant)
    private void DrawCastBar(SpriteBatch spriteBatch)
    {
        if (!CastingSystem.IsChanting)
            return;

        var progress = Math.Clamp(CastingSystem.ChantProgress, 0f, 1f);

        const int BAR_W = 240;
        const int BAR_H = 18;

        var cx = ChaosGame.UiWidth / 2;
        var barX = cx - (BAR_W / 2);
        //sit above the skill hotbar when it's laid out; otherwise fall back to a fixed spot near the bottom
        var anchorTop = SkillBar?.Y ?? (ChaosGame.UiHeight - 200);
        var barY = anchorTop - BAR_H - 34;

        var pixel = UIElement.GetPixel();

        //border + dark background
        spriteBatch.Draw(pixel, new Rectangle(barX - 2, barY - 2, BAR_W + 4, BAR_H + 4), Color.Black * 0.7f);
        spriteBatch.Draw(pixel, new Rectangle(barX, barY, BAR_W, BAR_H), new Color(22, 22, 30) * 0.92f);

        //fill (cornflower blue, like the chant text / snapped target)
        var fillW = (int)(BAR_W * progress);

        if (fillW > 0)
            spriteBatch.Draw(pixel, new Rectangle(barX, barY, fillW, BAR_H), new Color(90, 150, 240));

        //spell name centered above the bar, embossed like the targeting label
        var name = CastingSystem.ChantSpellName;

        if (!string.IsNullOrEmpty(name) && TtfTextRenderer.Available)
        {
            var line = TtfTextRenderer.GetLine(name, CAST_BAR_FONT_SIZE);

            if (line is { IsDisposed: false })
            {
                var tx = cx - (line.Width / 2);
                var ty = barY - line.Height - 2;
                spriteBatch.Draw(line, new Vector2(tx + 1, ty + 1), Color.Black * 0.75f);
                spriteBatch.Draw(line, new Vector2(tx, ty), Color.White);
            }
        }
    }

    //the map's 4 screen-space corner tips, N (top of tile 0,0), E (right of W-1,0), S (bottom of W-1,H-1), W (left of
    //0,H-1), forming the parallelogram the night-shadow grade is cropped to. Slightly past each tile's extreme point so
    //the edge tiles are fully inside
    private Vector2[] GetMapScreenQuad()
    {
        var w = MapFile!.Width;
        var h = MapFile.Height;
        var hw = DaLibConstants.HALF_TILE_WIDTH;
        var tw = DaLibConstants.TILE_WIDTH;
        var hh = DaLibConstants.HALF_TILE_HEIGHT;
        var th = DaLibConstants.TILE_HEIGHT;
        var off = new Vector2(WorldRenderRect.X, WorldRenderRect.Y);

        Vector2 P(int tx, int ty, float ox, float oy)
            => Camera.WorldToScreen(Camera.TileToWorld(tx, ty, h) + new Vector2(ox, oy)) + off;

        return
        [
            P(0, 0, hw, 0),          //N top tip
            P(w - 1, 0, tw, hh),     //E right tip
            P(w - 1, h - 1, hw, th), //S bottom tip
            P(0, h - 1, 0, hh)       //W left tip
        ];
    }

    //unused for this screen (ChaosGame calls DrawWorld + DrawNativeUi directly), but satisfies IScreen.
    public void Draw(SpriteBatch spriteBatch, GameTime gameTime)
    {
        DrawWorld(spriteBatch, gameTime);
        DrawNativeUi(spriteBatch, gameTime);
    }

    #region Swimming
    /// <summary>
    ///     Updates the entity's water tile state from the current map tile's gndattr data.
    /// </summary>
    private void UpdateEntityWaterState(WorldEntity entity)
    {
        if (MapFile is null
            || (entity.TileX < 0)
            || (entity.TileX >= MapFile.Width)
            || (entity.TileY < 0)
            || (entity.TileY >= MapFile.Height))
        {
            entity.GroundPaintHeight = 0;

            return;
        }

        var bgTileId = MapFile.Tiles[entity.TileX, entity.TileY].Background;

        if (DataContext.Tiles.GroundAttributes.TryGetValue(bgTileId, out var gndAttr))
        {
            entity.IsOnSwimmingTile = gndAttr.IsWalkBlocking;
            entity.GroundPaintHeight = gndAttr.PaintHeight;

            entity.GroundTintColor = new Color(
                gndAttr.R,
                gndAttr.G,
                gndAttr.B,
                gndAttr.A);

            //cache swim walk frame count for animation timing
            if (gndAttr.IsWalkBlocking)
            {
                var isFemale = entity.Appearance?.Gender == Gender.Female;
                var swimFrameCount = Game.AislingRenderer.GetSwimFrameCount(isFemale);
                var framesPerDir = swimFrameCount / 2;
                entity.SwimWalkFrames = Math.Max(framesPerDir - 1, 1);
            }
        } else
        {
            entity.IsOnSwimmingTile = false;
            entity.GroundPaintHeight = 0;
            entity.SwimWalkFrames = 0;
        }
    }
    #endregion

    #region Diagonal Stripe Rendering
    /// <summary>
    ///     Goes through foreground tiles, entities, and effects in diagonal stripe order (depth = x+y ascending). Per stripe draw
    ///     order: ground items, aislings, creatures, ground effects, entity effects, foreground tiles. Within each
    ///     category, entities draw in list order (arrival order, later arrivals on top).
    /// </summary>
    private void DrawForegroundAndEntities(SpriteBatch spriteBatch, IReadOnlyList<WorldEntity> sortedEntities)
    {
        if (MapFile is null)
            return;

        EntityHitBoxes.Clear();

        (var fgMinX, var fgMinY, var fgMaxX, var fgMaxY) = Camera.GetVisibleTileBounds(
            MapFile.Width,
            MapFile.Height,
            MapRenderer.ForegroundExtraMargin);

        var minDepth = fgMinX + fgMinY;
        var maxDepth = fgMaxX + fgMaxY;
        var entityIndex = 0;
        var entityCount = sortedEntities.Count;

        //skip entities before the visible depth range
        while ((entityIndex < entityCount) && (sortedEntities[entityIndex].SortDepth < minDepth))
            entityIndex++;

        for (var depth = minDepth; depth <= maxDepth; depth++)
        {
            //collect entities at this depth stripe
            var stripeStart = entityIndex;

            while ((entityIndex < entityCount) && (sortedEntities[entityIndex].SortDepth == depth))
                entityIndex++;

            var stripeEnd = entityIndex;

            //1. ground items
            for (var i = stripeStart; i < stripeEnd; i++)
                if (sortedEntities[i].Type == ClientEntityType.GroundItem)
                    DrawEntity(spriteBatch, sortedEntities[i]);

            //2. aislings
            for (var i = stripeStart; i < stripeEnd; i++)
                if (sortedEntities[i].Type == ClientEntityType.Aisling)
                    DrawEntity(spriteBatch, sortedEntities[i]);

            //3. creatures
            for (var i = stripeStart; i < stripeEnd; i++)
                if (sortedEntities[i].Type == ClientEntityType.Creature)
                    DrawEntity(spriteBatch, sortedEntities[i]);

            //4. dying creature dissolves
            DrawDyingEffectsAtDepth(spriteBatch, depth);

            //5. ground-targeted effects
            DrawGroundEffectsAtDepth(spriteBatch, depth);

            //6. projectiles in flight
            DrawProjectilesAtDepth(spriteBatch, depth);

            //7. entity-attached effects
            for (var i = stripeStart; i < stripeEnd; i++)
                DrawEntityEffects(spriteBatch, sortedEntities[i]);

            //8. foreground tiles (on top, trees and buildings occlude entities behind them)
            var tileXStart = Math.Max(fgMinX, depth - fgMaxY);
            var tileXEnd = Math.Min(fgMaxX, depth - fgMinY);

            for (var tileX = tileXStart; tileX <= tileXEnd; tileX++)
                MapRenderer.DrawForegroundTile(
                    spriteBatch,
                    Device,
                    MapFile,
                    Camera,
                    tileX,
                    depth - tileX,
                    AnimationTick);
        }
    }

    //erase blend: dest *= (1 - srcAlpha). Where the foreground silhouette is opaque, the glow underneath is wiped to 0.
    private static readonly BlendState GlowEraseBlend = new()
    {
        ColorSourceBlend = Blend.Zero,
        ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.InverseSourceAlpha
    };

    //returns the per-light skip predicate: a tile never acts as an occluder/blocker when it is a light emitter, a
    //globally no-shadow tile ([no_shadow] in the ini), or this light's own configured ignore tile (e.g. its post)
    private static Func<int, bool> LightSkip(LightSource light)
    {
        var ignore = light.Ignore;

        return id => TileLights.IsEmitter(id)
                     || TileLights.IsGloballyNonOccluding(id)
                     || ((ignore is not null) && (Array.IndexOf(ignore, id) >= 0));
    }

    //how many iso-depth steps DOWN (in front) this light reaches, based on how high it sits above its tile (OffsetY).
    //A ground light (OffsetY 0) reaches 0 = its own depth only; the higher it is, the more rows of front foreground it
    //lights before the silhouette/occluder passes cut it off. Tunable px-per-step lives in DebugSettings.
    private static int LightDownReach(LightSource light)
    {
        var step = DebugSettings.LightDownReachPxPerStep;
        var raw = step <= 0f ? 0 : (int)MathF.Round(-light.OffsetY / step);

        return Math.Clamp(raw, 0, DebugSettings.LightDownReachMaxSteps);
    }

    //renders the shadow casters for one lamp into the bound (padded) occluder map: the in-tile foot (bottom pixels) of
    //every foreground sprite sitting below the lamp (iso depth tx+ty greater). The ray-march shader then blocks the
    //lamp's light from passing those pixels, a real cast shadow into the pool. A tall canopy doesn't cast (foot only)
    //the 3x3 offsets (times DILATE px) the occluder feet are stamped at, to widen thin bars (a fence is ~1-2px and the
    //ray-march steps ~5px apart, so without this it hits a bar at some fragments and steps over it at others = speckle)
    private static readonly (int X, int Y)[] DilateOffsets =
    [
        (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)
    ];

    private void DrawLightOccluderMap(SpriteBatch spriteBatch, LightSource light, int occluderPad)
    {
        if (MapFile is null || !MapPreloaded || !DebugSettings.LightShadows || (DebugSettings.LightShadowFootPx <= 0))
            return;

        //a raised light reaches down (greater iso depth = in front) by reach tiles before its glow is blocked/erased,
        //so a high lamp lights the foreground a few tiles below it. reach grows with how high the light sits (OffsetY)
        var lightDepth = light.TileX + light.TileY + LightDownReach(light);
        var skip = LightSkip(light);
        var foot = DebugSettings.LightShadowFootPx;

        var padTiles = 1 + (int)MathF.Ceiling(occluderPad / 14f);
        var (minX, minY, maxX, maxY) = Camera.GetVisibleTileBounds(MapFile.Width, MapFile.Height, padTiles);

        const int DILATE = 2; //pixels each thin bar grows by, so the march can't miss it

        //the occluder map is padded; shift so viewport pixel (0,0) lands at (pad,pad). Stamp the feet at a 3x3 set of
        //small offsets so thin occluders are dilated into something the march reliably samples = smooth shadows.
        foreach (var (ox, oy) in DilateOffsets)
        {
            var transform = Matrix.CreateTranslation(WorldRenderRect.X + occluderPad + (ox * DILATE), WorldRenderRect.Y + occluderPad + (oy * DILATE), 0);

            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, GlobalSettings.Sampler, null, null, null, transform);

            for (var ty = minY; ty <= maxY; ty++)
                for (var tx = minX; tx <= maxX; tx++)
                    if ((tx + ty) > lightDepth)
                        MapRenderer.DrawForegroundTile(spriteBatch, Device, MapFile, Camera, tx, ty, AnimationTick, skip, plain: true, footPx: foot);

            spriteBatch.End();
        }
    }

    //erases (from the bound glow buffer) the full silhouette of every foreground sprite below the lamp, so the object
    //itself never lights up (its front face stays dark, the lamp is behind it)
    private void DrawLightSilhouetteErase(SpriteBatch spriteBatch, LightSource light, Matrix transform)
    {
        if (MapFile is null || !MapPreloaded)
            return;

        var lightDepth = light.TileX + light.TileY + LightDownReach(light);
        var skip = LightSkip(light);

        spriteBatch.Begin(SpriteSortMode.Immediate, GlowEraseBlend, GlobalSettings.Sampler, null, null, null, transform);

        var (minX, minY, maxX, maxY) = Camera.GetVisibleTileBounds(MapFile.Width, MapFile.Height, MapRenderer.ForegroundExtraMargin);

        for (var ty = minY; ty <= maxY; ty++)
            for (var tx = minX; tx <= maxX; tx++)
                if ((tx + ty) > lightDepth)
                    MapRenderer.DrawForegroundTile(spriteBatch, Device, MapFile, Camera, tx, ty, AnimationTick, skip, plain: true);

        spriteBatch.End();
    }

    private static byte[] LoadEmbeddedBytes(string name)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                                 .GetManifestResourceStream(name)
                           ?? throw new System.InvalidOperationException($"Embedded resource '{name}' not found");
        using var ms = new System.IO.MemoryStream();
        stream.CopyTo(ms);

        return ms.ToArray();
    }

    private void DrawDyingEffectsAtDepth(SpriteBatch spriteBatch, int depth)
    {
        if (MapFile is null)
            return;

        foreach (var dying in WorldState.DyingEffects)
        {
            if (dying.IsComplete || ((dying.TileX + dying.TileY) != depth))
                continue;

            var tileWorld = Camera.TileToWorld(dying.TileX, dying.TileY, MapFile.Height);
            var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

            var texCenterX = dying.CenterX - Math.Min(0, (int)dying.Left);
            var texCenterY = dying.CenterY - Math.Min(0, (int)dying.Top);

            var anchorX = dying.Flip
                ? dying.SourceWidth - texCenterX - dying.CenterXOffset
                : texCenterX + dying.CenterXOffset;

            var drawX = tileCenterX - anchorX;
            var drawY = tileCenterY - texCenterY;
            var screenPos = Camera.WorldToScreen(new Vector2(drawX, drawY));

            var effects = dying.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            var sourceRect = new Rectangle(0, 0, dying.SourceWidth, dying.TextureHeight);

            spriteBatch.Draw(
                dying.Texture,
                screenPos,
                sourceRect,
                Color.White * dying.Alpha,
                0f,
                Vector2.Zero,
                1f,
                effects,
                0f);
        }
    }

    private void DrawGroundEffectsAtDepth(SpriteBatch spriteBatch, int depth)
    {
        foreach (var effect in WorldState.ActiveEffects)
        {
            if (effect.IsComplete)
                continue;

            //an effect anchored to a live entity is drawn by DrawEntityEffects (it follows the entity). Everything else
            //draws here at its captured tile: a ground-targeted effect, or an entity-targeted effect whose target was
            //removed (a killing-blow spell on a dying monster), so it still shows once the entity is gone
            if (effect.TargetEntityId is { } targetId && (WorldState.GetEntity(targetId) is not null))
                continue;

            if (!effect.TileX.HasValue || !effect.TileY.HasValue)
                continue;

            if ((effect.TileX.Value + effect.TileY.Value) != depth)
                continue;

            var tileWorld = Camera.TileToWorld(effect.TileX.Value, effect.TileY.Value, MapFile!.Height);

            DrawSingleEffect(
                spriteBatch,
                effect,
                tileWorld.X + DaLibConstants.HALF_TILE_WIDTH,
                tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT,
                Vector2.Zero);
        }
    }

    private EntityTintType ResolveEntityTint(WorldEntity entity)
    {
        if (entity.HitTintExpiryMs > 0)
            return EntityTintType.HitTint;

        //the auto-snapped cast target glows like a hovered entity even when the cursor isn't on it
        if ((CastTargetId == entity.Id) || (WorldState.CurrentFrame.ShowTintHighlight && (WorldState.CurrentFrame.HoveredEntityId == entity.Id)))
            return EntityTintType.Highlight;

        if (GroupHighlightedIds.Contains(entity.Id))
            return EntityTintType.Group;

        return EntityTintType.None;
    }

    private void DrawProjectilesAtDepth(SpriteBatch spriteBatch, int depth)
    {
        if (MapFile is null)
            return;

        foreach (var proj in WorldState.ActiveProjectiles)
        {
            if (proj.IsComplete)
                continue;

            var tile = Camera.WorldToTile(proj.CurrentX, proj.CurrentY, MapFile.Height);
            var projDepth = tile.X + tile.Y;

            if (projDepth != depth)
                continue;

            var frameIndex = proj.Direction * proj.FramesPerDirection + proj.CurrentFrameCycle;

            Game.EffectRenderer.DrawProjectile(
                spriteBatch,
                Camera,
                proj.MeffectId,
                frameIndex,
                proj.CurrentX + proj.ArcOffsetX,
                proj.CurrentY + proj.ArcOffsetY);
        }
    }

    private void DrawSingleEffect(
        SpriteBatch spriteBatch,
        Animation effect,
        float tileCenterX,
        float tileCenterY,
        Vector2 visualOffset,
        float alpha = 1f)
        => Game.EffectRenderer.Draw(
            spriteBatch,
            Device,
            Camera,
            effect.EffectId,
            effect.CurrentFrame,
            effect.BlendMode,
            tileCenterX,
            tileCenterY,
            visualOffset,
            alpha);

    //dusk/dawn bloom
    //full-res ping-pong targets for the separable blur, rebuilt on world-size change, disposed in UnloadContent. Full
    //res (not a downscale) so the blur kernel is centred per output pixel and moves with the world content (a low-res
    //blur grid is screen-locked and the glow crawls as the camera scrolls, same reason the shadow blur is full res)
    private RenderTarget2D? BloomA;
    private RenderTarget2D? BloomB;

    //accumulate weighted blur taps: dest.rgb += src.rgb (the per-tap weight is carried in the draw colour)
    private static readonly BlendState BloomAccumulate = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One
    };

    //add the blurred glow back onto the world, weighted by dest alpha (map coverage), so it never blooms past the map
    //edge into the black void. dest.rgb += src.rgb * dest.a, dest alpha preserved
    private static readonly BlendState BloomComposite = new()
    {
        ColorSourceBlend = Blend.DestinationAlpha,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One
    };

    //bloom blur: taps per side and pixel step between them (full-res, separable). The kernel is centred on each output
    //pixel so it is translation-invariant, it moves with the world content instead of crawling against a fixed grid
    private const int BLOOM_TAPS = 7;
    private const int BLOOM_STEP = 2;

    /// <summary>
    ///     A warm glow over the darkened world: the world target is blurred with a FULL-RES separable box blur, then
    ///     added back tinted amber. <see cref="DarknessRenderer.DuskGlow" /> ramps 0..1 from day to full night; the
    ///     strength runs DUSK_BLOOM_MAX at the faint end up to NIGHT_BLOOM_MAX, cubic-weighted toward night so dusk/dawn
    ///     stay a subtle cosy lift while at night the bloom is carried almost entirely by the bright lit spots (the
    ///     blurred frame is dark everywhere else), making lights pierce the darkness. The blur is full res (not a
    ///     downscale) so it does not crawl as the camera scrolls, and the add-back is masked by map coverage
    ///     (the world's alpha) so it never blooms into the off-map void. Free in daylight (the glow gate). The world
    ///     target survives the rebind because it is created with RenderTargetUsage.PreserveContents.
    /// </summary>
    private void DrawDuskBloom(SpriteBatch spriteBatch)
    {
        var glow = DarknessRenderer.DuskGlow;

        if (glow <= 0.005f)
            return;

        var device = spriteBatch.GraphicsDevice;
        var bound = device.GetRenderTargets();

        if ((bound.Length == 0) || bound[0].RenderTarget is not RenderTarget2D worldTarget)
            return;

        var w = worldTarget.Width;
        var h = worldTarget.Height;

        if (BloomA is null || BloomB is null || BloomA.IsDisposed || (BloomA.Width != w) || (BloomA.Height != h))
        {
            BloomA?.Dispose();
            BloomB?.Dispose();
            //16-bit/channel: the bloom samples the (16-bit) darkened world, blurs, and adds back onto it. An 8-bit blur
            //buffer would quantize the added glow and step as the night deepens, keep the whole world chain 16-bit until
            //the dithered final blit
            BloomA = new RenderTarget2D(device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            BloomB = new RenderTarget2D(device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None);
        }

        var wgt = Color.White * (1f / ((2 * BLOOM_TAPS) + 1));
        var full = new Rectangle(0, 0, w, h);

        //horizontal blur: world -> BloomA. Offset the source rect (not the dest position) so every tap covers the whole
        //buffer, taps that run off the edge clamp to the edge texel (LinearClamp). Offsetting the dest instead left an
        //unwritten gap on each edge, so the accumulation summed fewer than (2*TAPS+1) taps there and the bloom was
        //under-weighted in an edge band (dark edges when the bloom ramps in). Full dest normalizes brightness to the edge
        device.SetRenderTarget(BloomA);
        device.Clear(Color.Transparent);
        spriteBatch.Begin(blendState: BloomAccumulate, samplerState: SamplerState.LinearClamp);

        for (var t = -BLOOM_TAPS; t <= BLOOM_TAPS; t++)
            spriteBatch.Draw(worldTarget, full, new Rectangle(t * BLOOM_STEP, 0, w, h), wgt);

        spriteBatch.End();

        //vertical blur: BloomA -> BloomB (same source-offset trick)
        device.SetRenderTarget(BloomB);
        device.Clear(Color.Transparent);
        spriteBatch.Begin(blendState: BloomAccumulate, samplerState: SamplerState.LinearClamp);

        for (var t = -BLOOM_TAPS; t <= BLOOM_TAPS; t++)
            spriteBatch.Draw(BloomA, full, new Rectangle(0, t * BLOOM_STEP, w, h), wgt);

        spriteBatch.End();

        //back onto the world target, additively, tinted warm, masked by map coverage (dest alpha). Strength ramps
        //faint to strong with the glow, the night boost cubic-weighted so it concentrates near full darkness
        var duskMax = DebugSettings.BloomDuskMax;
        var strength = (duskMax * glow) + ((DebugSettings.BloomNightMax - duskMax) * glow * glow * glow);

        device.SetRenderTargets(bound);
        spriteBatch.Begin(blendState: BloomComposite, samplerState: SamplerState.LinearClamp);
        spriteBatch.Draw(BloomB, new Vector2(0, 0), DebugSettings.BloomTint * strength);
        spriteBatch.End();
    }

    //night vignette
    private static Texture2D? NightVignetteTexture;
    private static float NightVignetteBuiltInner = -1f; //the VignetteInner the cached texture was built with

    /// <summary>
    ///     A black radial vignette over the world that deepens with the night: strength is the night ramp
    ///     (<see cref="DarknessRenderer.DuskGlow" />) squared, so dusk/dawn get only a hint while full night presses
    ///     in dark from the edges. Drawn in the world pass right after the darkness overlay and bloom, so it
    ///     upscales chunky with the pixels and the bloom's lights still pierce it. Free in daylight (the gate).
    /// </summary>
    private void DrawNightVignette(SpriteBatch spriteBatch)
    {
        var glow = DarknessRenderer.DuskGlow;
        //any carried lantern fades the night vignette out (eased)
        var strength = DebugSettings.VignetteMax * glow * glow * (1f - DarknessRenderer.LanternEffectRelief);

        if (strength <= 0.005f)
            return;

        var device = spriteBatch.GraphicsDevice;
        var bound = device.GetRenderTargets();

        if ((bound.Length == 0) || bound[0].RenderTarget is not RenderTarget2D worldTarget)
            return;

        spriteBatch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.LinearClamp);

        spriteBatch.Draw(
            EnsureNightVignette(device),
            new Rectangle(0, 0, worldTarget.Width, worldTarget.Height),
            Color.White * strength);

        spriteBatch.End();
    }

    //a premultiplied radial BLACK vignette built once: clear in the centre, deepening toward the edges/corners.
    //Stretched to the world target each draw and tinted by the live night strength.
    private static Texture2D EnsureNightVignette(GraphicsDevice device)
    {
        var inner = DebugSettings.VignetteInner;

        //rebuild only when the inner-clear fraction changes (the debug slider moved)
        if (NightVignetteTexture is not null && Math.Abs(NightVignetteBuiltInner - inner) < 0.001f)
            return NightVignetteTexture;

        NightVignetteTexture?.Dispose();

        const int SIZE = 256;
        var pixels = new Color[SIZE * SIZE];
        var centre = (SIZE - 1) / 2f;

        for (var y = 0; y < SIZE; y++)
            for (var x = 0; x < SIZE; x++)
            {
                var dx = x - centre;
                var dy = y - centre;

                //normalized to the edge midpoint (d = 1 there, clamped past it), not the corner (corner
                //normalization left the screen edges at roughly half strength and the vignette read as nothing)
                var d = MathF.Sqrt((dx * dx) + (dy * dy)) / centre; //0 centre .. 1 edge midpoint .. clamped corner
                var t = Math.Clamp((d - inner) / (1f - inner), 0f, 1f);
                t = t * t * (3f - (2f * t)); //smoothstep
                pixels[(y * SIZE) + x] = new Color(0f, 0f, 0f, t); //premultiplied black at alpha t
            }

        NightVignetteTexture = new Texture2D(device, SIZE, SIZE);
        NightVignetteTexture.SetData(pixels);
        NightVignetteBuiltInner = inner;

        return NightVignetteTexture;
    }

    //through-walls overlay: re-draw every active effect/projectile/dying dissolve at the Behind-walls opacity alpha
    //after the foreground, so an animation shows semi-transparently where a wall/foreground tile would otherwise hide
    //it, the same trick the entity silhouettes and the tile cursor use. No depth gating: it overlays the whole world.
    //On open ground this adds a faint second copy of the effect over the full-intensity stripe draw (additive effects
    //read slightly brighter), the alpha is the user's slider so 0 = off, low values keep the open-ground change subtle
    private void DrawEffectsThroughWalls(SpriteBatch spriteBatch)
    {
        if (MapFile is null)
            return;

        var alpha = SilhouetteRenderer.SilhouetteAlpha;

        if (alpha <= 0f)
            return;

        foreach (var effect in WorldState.ActiveEffects)
        {
            if (effect.IsComplete)
                continue;

            //an effect attached to a live entity follows it, otherwise it sits at its captured tile (ground effect, or a
            //killing-blow effect whose target has been removed)
            if (effect.TargetEntityId is { } targetId && (WorldState.GetEntity(targetId) is { } target))
            {
                var entWorld = Camera.TileToWorld(target.TileX, target.TileY, MapFile.Height);

                DrawSingleEffect(
                    spriteBatch,
                    effect,
                    entWorld.X + DaLibConstants.HALF_TILE_WIDTH,
                    entWorld.Y + DaLibConstants.HALF_TILE_HEIGHT,
                    target.VisualOffset,
                    alpha);

                continue;
            }

            if (!effect.TileX.HasValue || !effect.TileY.HasValue)
                continue;

            var tileWorld = Camera.TileToWorld(effect.TileX.Value, effect.TileY.Value, MapFile.Height);

            DrawSingleEffect(
                spriteBatch,
                effect,
                tileWorld.X + DaLibConstants.HALF_TILE_WIDTH,
                tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT,
                Vector2.Zero,
                alpha);
        }

        //projectiles in flight
        foreach (var proj in WorldState.ActiveProjectiles)
        {
            if (proj.IsComplete)
                continue;

            var frameIndex = proj.Direction * proj.FramesPerDirection + proj.CurrentFrameCycle;

            Game.EffectRenderer.DrawProjectile(
                spriteBatch,
                Camera,
                proj.MeffectId,
                frameIndex,
                proj.CurrentX + proj.ArcOffsetX,
                proj.CurrentY + proj.ArcOffsetY,
                alpha);
        }

        //dying-creature dissolves
        foreach (var dying in WorldState.DyingEffects)
        {
            if (dying.IsComplete)
                continue;

            var tileWorld = Camera.TileToWorld(dying.TileX, dying.TileY, MapFile.Height);
            var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

            var texCenterX = dying.CenterX - Math.Min(0, (int)dying.Left);
            var texCenterY = dying.CenterY - Math.Min(0, (int)dying.Top);

            var anchorX = dying.Flip
                ? dying.SourceWidth - texCenterX - dying.CenterXOffset
                : texCenterX + dying.CenterXOffset;

            var screenPos = Camera.WorldToScreen(new Vector2(tileCenterX - anchorX, tileCenterY - texCenterY));
            var fx = dying.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            var sourceRect = new Rectangle(0, 0, dying.SourceWidth, dying.TextureHeight);

            spriteBatch.Draw(dying.Texture, screenPos, sourceRect, Color.White * (dying.Alpha * alpha), 0f, Vector2.Zero, 1f, fx, 0f);
        }
    }
    #endregion

    #region Entity Rendering
    //re-draws the focused dialog speaker on top of the dim so the NPC you are talking to stands out clearly. Draws the
    //dim itself (the native DialogDimmer skips its base+vignette via SuppressBaseDim while this runs, to avoid doubling),
    //then the speaker bright over it. In the world pass, so the creature renders at the correct world position + scale.
    private void DrawSpeakerSpotlight(SpriteBatch spriteBatch)
    {
        if (!SpeakerSpotlightActive || (MapFile is null))
            return;

        if (WorldState.GetEntity(CameraFocusEntityId!.Value) is not { } npc)
            return;

        var fraction = NpcSessionHost?.OpenFraction ?? 1f;

        if (fraction <= 0f)
            return;

        //dim the world view DARKER than the normal dialog dim (the speaker is spotlit bright on top, so the rest can go
        //quite dark for a strong focus), then re-draw the speaker over it
        DialogDimmer.DrawBaseAndVignette(
            spriteBatch,
            new Rectangle(0, 0, WorldRenderRect.Width, WorldRenderRect.Height),
            fraction,
            SPOTLIGHT_BASE_ALPHA,
            SPOTLIGHT_VIGNETTE_ALPHA);

        var tileWorld = Camera.TileToWorld(npc.TileX, npc.TileY, MapFile.Height);
        var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
        var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

        //speaker at full alpha while the dialog is opening or fully open, only fade out with the dialog on close so
        //the NPC appears at full brightness immediately on first click, not fading in from invisible
        EntityAlphaMul = (NpcSessionHost?.IsFadingOut == true) ? fraction : 1f;

        if (npc.Type == ClientEntityType.Aisling)
            DrawAisling(spriteBatch, npc, tileCenterX, tileCenterY);
        else
            DrawCreature(spriteBatch, npc, tileCenterX, tileCenterY);

        EntityAlphaMul = 1f;
    }

    //the spotlight dims the world MORE than the plain dialog dim (the speaker is lit bright on top, so a deep darken reads
    //as a strong spotlight). Tunable.
    private const float SPOTLIGHT_BASE_ALPHA = 0.82f;
    private const float SPOTLIGHT_VIGNETTE_ALPHA = 0.4f;

    private void DrawEntity(SpriteBatch spriteBatch, WorldEntity entity)
    {
        if (MapFile is null)
            return;

        var tileWorldPos = Camera.TileToWorld(entity.TileX, entity.TileY, MapFile.Height);
        var tileCenterX = tileWorldPos.X + DaLibConstants.HALF_TILE_WIDTH;
        var tileCenterY = tileWorldPos.Y + DaLibConstants.HALF_TILE_HEIGHT;

        //hit shake: 200ms delay then 4 frames (~16ms each) of flat left/right offset, no decay.
        const float HIT_SHAKE_ACTIVE = 64f; //4 × 16ms

        if (entity.HitShakeMs > 0 && entity.HitShakeMs <= HIT_SHAKE_ACTIVE)
        {
            var direction = ((int)(entity.HitShakeMs / 16f) % 2 == 0) ? 1f : -1f;
            tileCenterX += 2f * direction;
        }

        //cast a soft directional ground shadow under the entity, away from the nearest lamp (night only). Disabled for
        //now (flip EntityGroundShadows to re-enable)
        if (EntityGroundShadows && entity.Type is ClientEntityType.Aisling or ClientEntityType.Creature)
            DrawGroundShadow(spriteBatch, entity, tileCenterX, tileCenterY);

        var entityTextureBottom = 0;

        switch (entity.Type)
        {
            case ClientEntityType.Aisling:
                entityTextureBottom = DrawAisling(
                    spriteBatch,
                    entity,
                    tileCenterX,
                    tileCenterY);

                break;

            case ClientEntityType.Creature:
                entityTextureBottom = DrawCreature(
                    spriteBatch,
                    entity,
                    tileCenterX,
                    tileCenterY);

                break;

            case ClientEntityType.GroundItem:
                DrawGroundItem(
                    spriteBatch,
                    entity,
                    tileCenterX,
                    tileCenterY);

                return; //ground items don't get hitboxes
        }

        if (entityTextureBottom <= 0)
            return;

        //hitbox: 28px wide centered on tile screen x, 60px tall bottom-aligned to texture bottom
        var tileScreenPos = Camera.WorldToScreen(new Vector2(tileCenterX + entity.VisualOffset.X, tileCenterY + entity.VisualOffset.Y));
        var hitboxX = (int)tileScreenPos.X - HITBOX_WIDTH / 2;
        var hitboxY = entityTextureBottom - HITBOX_HEIGHT;

        EntityHitBoxes.Add(
            new EntityHitBox(
                entity.Id,
                new Rectangle(
                    hitboxX,
                    hitboxY,
                    HITBOX_WIDTH,
                    HITBOX_HEIGHT)));
    }

    //toggle for the entity ground drop-shadows (player/NPC/monster shadow cast away from the nearest lamp). Off for
    //now. static readonly (not const) so flipping it doesn't trip the unreachable-code warning
    private static readonly bool EntityGroundShadows = false;

    private const int ShadowBlobSize = 64;
    private Texture2D? ShadowBlob;

    //a soft round blob (white, radial alpha falloff) - tinted black + stretched into a ground shadow ellipse at draw time
    private Texture2D GetShadowBlob()
    {
        if (ShadowBlob is { IsDisposed: false })
            return ShadowBlob;

        var px = new Color[ShadowBlobSize * ShadowBlobSize];
        var c = (ShadowBlobSize - 1) / 2f;

        for (var y = 0; y < ShadowBlobSize; y++)
            for (var x = 0; x < ShadowBlobSize; x++)
            {
                var dx = (x - c) / c;
                var dy = (y - c) / c;
                var d = MathF.Sqrt((dx * dx) + (dy * dy));
                var a = Math.Clamp(1f - d, 0f, 1f);
                a *= a; //soften the edge

                px[(y * ShadowBlobSize) + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(a * 255f));
            }

        ShadowBlob = new Texture2D(Device, ShadowBlobSize, ShadowBlobSize);
        ShadowBlob.SetData(px);

        return ShadowBlob;
    }

    //draws a soft directional ground shadow for an entity, cast away from the nearest lamp. Night only; strength fades
    //with distance from the lamp so the shadow appears as you enter a pool and fades as you leave it.
    private void DrawGroundShadow(SpriteBatch spriteBatch, WorldEntity entity, float tileCenterX, float tileCenterY)
    {
        if (entity.IsHidden || !DarknessRenderer.IsActive)
            return;

        var feet = Camera.WorldToScreen(new Vector2(tileCenterX + entity.VisualOffset.X, tileCenterY + entity.VisualOffset.Y));

        if (MapFile is null)
            return;

        //nearest lamp whose pool reaches the entity
        var bestDistSq = float.MaxValue;
        var bestRadius = 0f;
        var bestIntensity = 0f;
        var bestTileX = 0;
        var bestTileY = 0;
        var found = false;

        foreach (var s in Lighting.Sources)
        {
            //neutral entity lanterns (Tint == null) light but should not throw a cast shadow; only real placed lights do
            if (s.Tint is null)
                continue;

            var radius = Math.Max(s.PixelMask.Width, s.PixelMask.Height) * 0.5f;
            var dx = feet.X - s.ScreenPosition.X;
            var dy = feet.Y - s.ScreenPosition.Y;
            var distSq = (dx * dx) + (dy * dy);

            if ((distSq < radius * radius) && (distSq < bestDistSq))
            {
                bestDistSq = distSq;
                bestRadius = radius;
                bestIntensity = s.Intensity;
                bestTileX = s.TileX;
                bestTileY = s.TileY;
                found = true;
            }
        }

        if (!found)
            return;

        var dist = MathF.Sqrt(bestDistSq);

        //cast the shadow AWAY from the lamp's GROUND position (its tile), not the raised glow point. The glow sits ~48px
        //above the lantern (the config offset), so referencing it makes "above the lamp" need a big climb before the
        //shadow flips up. Both the feet and the lamp tile are on the ground, so this vector is the natural on-ground
        //"away from the lamp" direction (already foreshortened by the iso projection), and the shadow follows intuition.
        var lampWorld = Camera.TileToWorld(bestTileX, bestTileY, MapFile.Height);
        var lampGround = Camera.WorldToScreen(new Vector2(lampWorld.X + DaLibConstants.HALF_TILE_WIDTH, lampWorld.Y + DaLibConstants.HALF_TILE_HEIGHT));
        var dir = new Vector2(feet.X - lampGround.X, feet.Y - lampGround.Y);
        var dirLen = dir.Length();
        dir = dirLen > 0.001f ? dir / dirLen : new Vector2(0f, 1f);

        var proximity = Math.Clamp(1f - (dist / bestRadius), 0f, 1f);
        var alpha = 0.62f * proximity * bestIntensity; //strongest in the bright pool, fading out toward its rim

        if (alpha < 0.02f)
            return;

        //length grows with distance from the lamp, like a real overhead light: standing under it the light is nearly
        //straight overhead so the shadow is stubby, further out the light comes in at a low angle and the shadow
        //stretches
        var length = Math.Min(10f + (dist * 0.30f), 52f);
        var width = 12f + (length * 0.30f);
        var angle = MathF.Atan2(dir.Y, dir.X);

        spriteBatch.Draw(
            GetShadowBlob(),
            feet,
            null,
            Color.Black * alpha,
            angle,
            new Vector2(0f, ShadowBlobSize / 2f), //origin at the left-centre so the ellipse extends from the feet along dir
            new Vector2(length / ShadowBlobSize, width / ShadowBlobSize),
            SpriteEffects.None,
            0f);
    }

    /// <summary>
    ///     Draws a creature entity. Returns the screen-space Y of the texture bottom edge, or 0 if not drawn.
    /// </summary>
    private int DrawCreature(
        SpriteBatch spriteBatch,
        WorldEntity entity,
        float tileCenterX,
        float tileCenterY)
    {
        var creatureRenderer = Game.CreatureRenderer;
        var animInfo = creatureRenderer.GetAnimInfo(entity.SpriteId);

        if (animInfo is null)
            return 0;

        var info = animInfo.Value;
        (var frameIndex, var flip) = AnimationSystem.GetCreatureFrame(entity, in info);

        //transparent entities draw faded in both passes so they compound multiplicatively with occlusion:
        //stripe at TRANSPARENT_ALPHA + silhouette RT at TRANSPARENT_SILHOUETTE_ALPHA → ~50% open, ~25% behind FG.
        //non-transparent entities draw opaque in both passes → 100% open, ~50% behind FG.
        var alpha = entity.IsHidden
            ? 0f
            : entity.IsTransparent
                ? DrawingForSilhouette ? TRANSPARENT_SILHOUETTE_ALPHA : TRANSPARENT_ALPHA
                : 1f;

        alpha *= EntityAlphaMul; //transient fade for the spotlit speaker (1 for every normal draw)

        var tint = ResolveEntityTint(entity);

        //mirror the aisling convention, swimming tiles replace the normal sprite path and must not double-tint the creature
        var groundPaintHeight = entity.IsOnSwimmingTile ? 0 : entity.GroundPaintHeight;

        return creatureRenderer.Draw(
            spriteBatch,
            Camera,
            entity.SpriteId,
            frameIndex,
            flip,
            tileCenterX,
            tileCenterY,
            entity.VisualOffset,
            tint,
            groundPaintHeight,
            entity.GroundTintColor,
            alpha);
    }

    private int DrawAisling(
        SpriteBatch spriteBatch,
        WorldEntity entity,
        float tileCenterX,
        float tileCenterY)
    {
        //hidden aislings have no visual (body sprite 0, all equipment 0) but are still present for
        //hit-testing, skip the draw and anchor the hitbox bottom to the tile center (feet position)
        if (entity.IsHidden)
        {
            var tileScreenPos = Camera.WorldToScreen(new Vector2(tileCenterX + entity.VisualOffset.X, tileCenterY + entity.VisualOffset.Y));

            return (int)tileScreenPos.Y;
        }

        //morphed aislings (creature form) render as creatures, swimming overrides morphs too
        if (entity.Appearance is null && entity is { SpriteId: > 0, IsOnSwimmingTile: false })
            return DrawCreature(
                spriteBatch,
                entity,
                tileCenterX,
                tileCenterY);

        if (entity.Appearance is null && !entity.IsOnSwimmingTile)
            return 0;

        var appearance = entity.Appearance ?? default;
        (var frameIndex, var flip, var animSuffix, var isFrontFacing) = AnimationSystem.GetAislingFrame(entity);

        //swimming override, single sprite replaces all aisling layers, driven by existing animation state
        if (entity.IsOnSwimmingTile)
        {
            var isFemale = entity.Appearance?.Gender == Gender.Female;
            var dirIndex = isFrontFacing ? 1 : 0;

            var swimFrameCount = Game.AislingRenderer.GetSwimFrameCount(isFemale);
            var framesPerDir = swimFrameCount / 2;

            if (framesPerDir <= 0)
                return 0;

            //walking: use walk frame index directly. idle: use idleanimtick for continuous cycling.
            //frame 0 is the idle/standing pose, skip it so the swim animation only cycles walk frames (1..n)
            var walkFrames = framesPerDir - 1;

            var animIndex = walkFrames > 0
                ? 1 + (entity.AnimState == EntityAnimState.Walking ? entity.AnimFrameIndex % walkFrames : entity.IdleAnimTick % walkFrames)
                : 0;

            var swimFrame = dirIndex * framesPerDir + animIndex;

            return Game.AislingRenderer.DrawSwimming(
                spriteBatch,
                Camera,
                isFemale,
                swimFrame,
                flip,
                tileCenterX,
                tileCenterY,
                entity.VisualOffset);
        }

        //rest position override, single spf sprite replaces all aisling layers
        if (entity.RestPosition != RestPosition.None)
            return Game.AislingRenderer.DrawResting(
                spriteBatch,
                Camera,
                entity.Appearance?.Gender == Gender.Female,
                entity.RestPosition,
                isFrontFacing,
                flip,
                tileCenterX,
                tileCenterY,
                entity.VisualOffset,
                entity.ActiveEmoteFrame);

        var emotionFrame = entity.ActiveEmoteFrame;
        var groundPaintHeight = entity.IsOnSwimmingTile ? 0 : entity.GroundPaintHeight;

        var tint = ResolveEntityTint(entity);

        //transparent aislings draw faded in both passes so they compound multiplicatively with occlusion:
        //stripe at TRANSPARENT_ALPHA + silhouette RT at TRANSPARENT_SILHOUETTE_ALPHA → ~50% open, ~25% behind FG.
        //non-transparent aislings draw opaque in both passes → 100% open, ~50% behind FG.
        var alpha = entity.IsTransparent
            ? DrawingForSilhouette ? TRANSPARENT_SILHOUETTE_ALPHA : TRANSPARENT_ALPHA
            : 1f;

        //transparent wins over dead, an invisible ghost isn't a sensible visual state, and stacking both alpha
        //modulations would produce an effectively-invisible result
        var isDead = entity.IsDead && !entity.IsTransparent;

        var drawParams = new AislingDrawParams(
            entity.Id,
            appearance,
            frameIndex,
            flip,
            isFrontFacing,
            animSuffix,
            emotionFrame,
            groundPaintHeight,
            entity.GroundTintColor,
            tileCenterX,
            tileCenterY,
            entity.VisualOffset,
            tint,
            isDead,
            alpha);

        return Game.AislingRenderer.Draw(spriteBatch, Camera, in drawParams);
    }

    private void DrawEntityEffects(SpriteBatch spriteBatch, WorldEntity entity)
    {
        if (MapFile is null)
            return;

        var tileWorldPos = Camera.TileToWorld(entity.TileX, entity.TileY, MapFile.Height);
        var tileCenterX = tileWorldPos.X + DaLibConstants.HALF_TILE_WIDTH;
        var tileCenterY = tileWorldPos.Y + DaLibConstants.HALF_TILE_HEIGHT;

        foreach (var effect in WorldState.ActiveEffects)
        {
            if ((effect.TargetEntityId != entity.Id) || effect.IsComplete)
                continue;

            DrawSingleEffect(
                spriteBatch,
                effect,
                tileCenterX,
                tileCenterY,
                entity.VisualOffset);
        }
    }

    private void DrawGroundItem(
        SpriteBatch spriteBatch,
        WorldEntity entity,
        float tileCenterX,
        float tileCenterY)
    {
        //swim tiles don't normally host ground items, but mirror the aisling/creature convention for safety.
        var groundPaintHeight = entity.IsOnSwimmingTile ? 0 : entity.GroundPaintHeight;

        Game.ItemRenderer.Draw(
            spriteBatch,
            Camera,
            entity.SpriteId,
            entity.ItemColor,
            tileCenterX,
            tileCenterY,
            groundPaintHeight,
            entity.GroundTintColor);
    }

    /// <summary>
    ///     Creates a texture containing a dashed ellipse inscribed in the isometric tile diamond. Gaps at the 4 cardinal
    ///     directions (top, right, bottom, left of the ellipse).
    /// </summary>
    private static Texture2D CreateTileCursorTexture(GraphicsDevice device, Color color)
    {
        const int WIDTH = DaLibConstants.HALF_TILE_WIDTH * 2; //56
        const int HEIGHT = DaLibConstants.HALF_TILE_HEIGHT * 2; //28

        var pixels = new Color[WIDTH * HEIGHT];

        var cx = WIDTH / 2;
        var cy = HEIGHT / 2;

        //top-right quarter only.
        //these are offsets from the center.
        //tweak these until the shape matches exactly how you want.
        Span<Point> quarter =
        [
            new(-6, -8),
            new(-7, -8),
            new(-8, -8),
            new(-9, -8),
            new(-10, -8),
            new(-11, -7),
            new(-12, -7),
            new(-13, -6),
            new(-14, -6),
            new(-15, -5),
            new(-16, -5),
            new(-17, -4),
            new(-17, -3)
        ];

        ImageUtil.DrawProjectedQuadrants(
            pixels,
            WIDTH,
            HEIGHT,
            cx,
            cy,
            quarter,
            color);

        var texture = new Texture2D(device, WIDTH, HEIGHT);
        texture.SetData(pixels);

        return texture;
    }

    private PanelBase? GetDraggingPanel()
    {
        if (WorldHud.Inventory.IsDragging)
            return WorldHud.Inventory;

        if (WorldHud.SkillBook.IsDragging)
            return WorldHud.SkillBook;

        if (WorldHud.SkillBookAlt.IsDragging)
            return WorldHud.SkillBookAlt;

        if (WorldHud.SpellBook.IsDragging)
            return WorldHud.SpellBook;

        if (WorldHud.SpellBookAlt.IsDragging)
            return WorldHud.SpellBookAlt;

        if (WorldHud.Tools.WorldSkills.IsDragging)
            return WorldHud.Tools.WorldSkills;

        if (WorldHud.Tools.WorldSpells.IsDragging)
            return WorldHud.Tools.WorldSpells;

        //the fixed hotbars
        if (InvBarPanel?.IsDragging == true)
            return InvBarPanel;

        if (SkillBarPanel?.IsDragging == true)
            return SkillBarPanel;

        if (SpellBarPanel?.IsDragging == true)
            return SpellBarPanel;

        //the full Skills/Spells book windows
        if (SkillWinPanel?.IsDragging == true)
            return SkillWinPanel;

        if (SpellWinPanel?.IsDragging == true)
            return SpellWinPanel;

        return null;
    }

    private const int TARGETING_FONT_SIZE = 13;
    private const float TARGETING_PULSE_SPEED = 4f;
    private const int TARGETING_BEZIER_SEGMENTS = 28;
    private const float TARGETING_LINE_THICKNESS = 2f;

    //draws:
    //  - a size-pulsing icon overlaid on the hotbar slot (grows from center, always full alpha)
    //  - a cubic bezier line from the slot to the cursor (or snapped to the hovered target entity)
    //  - "Select target" label near the cursor/target
    private void DrawTargetingCursor(SpriteBatch spriteBatch, GameTime gameTime)
    {
        if (!CastingSystem.IsTargeting || MapFile is null)
            return;

        var targetingSlot = CastingSystem.TargetingSlot;

        if (targetingSlot is null)
            return;

        //CastingSystem.TargetingSlot always points to the slot inside WorldHud.SpellBook (the hidden HUD), which has no
        //ScaleHost ancestor. Find the visual slot by number in the first visible ScaleHost-wrapped panel instead.
        var slotNum = targetingSlot.Slot;
        PanelSlot? visualSlot = null;
        ScaleHost? scaleHost = null;

        //spell hotbar (most common case)
        if (SpellBar?.Visible == true && SpellBarPanel is not null)
        {
            var c = SpellBarPanel.GetSlotControl(slotNum);

            if (c is not null)
            {
                visualSlot = c;
                scaleHost = SpellBar;
            }
        }

        //spell book window (fallback if hotbar slot isn't there)
        if (visualSlot is null && SpellHost?.Visible == true && SpellWinPanel is not null)
        {
            var c = SpellWinPanel.GetSlotControl(slotNum);

            if (c is not null)
            {
                visualSlot = c;
                scaleHost = SpellHost;
            }
        }

        if (visualSlot is null || scaleHost is null)
            return;

        PanelSlot vs = visualSlot!; //non-nullable alias, null was just checked above
        var icon = vs.NormalTexture ?? targetingSlot.NormalTexture;

        if (icon is null || icon.IsDisposed)
            return;

        //compute the slot's actual on-screen center accounting for the ScaleHost magnification:
        //  actual = hostOrigin + (nativePos - hostOrigin) * scale
        var hostScale = scaleHost.Scale;
        var ox = (float)scaleHost.ScreenX;
        var oy = (float)scaleHost.ScreenY;
        var slotCenterX = ox + (vs.ScreenX + vs.Width * 0.5f - ox) * hostScale;
        var slotCenterY = oy + (vs.ScreenY + vs.Height * 0.5f - oy) * hostScale;

        //the bezier always snaps to the closest target (no matter where the cursor is), that's what a plain cast hits
        //falls back to the cursor only when there's no valid target in range
        Vector2 endPoint;
        var isSnapped = false;
        var closest = CastTargetId is { } ctId ? WorldState.GetEntity(ctId) : null;

        if (closest is not null)
        {
            var tileWorld = Camera.TileToWorld(closest.TileX, closest.TileY, MapFile.Height);

            var worldScreen = Camera.WorldToScreen(new Vector2(
                tileWorld.X + DaLibConstants.HALF_TILE_WIDTH,
                tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT));

            endPoint = new Vector2(
                worldScreen.X * ChaosGame.WorldDrawRect.Width / ChaosGame.WorldRenderWidth + ChaosGame.WorldDrawRect.X,
                worldScreen.Y * ChaosGame.WorldDrawRect.Height / ChaosGame.WorldRenderHeight + ChaosGame.WorldDrawRect.Y);
            isSnapped = true;
        } else
            endPoint = new Vector2(InputBuffer.MouseX, InputBuffer.MouseY);

        //cubic bezier: arc upward from the slot. draw BEFORE the icon so the icon sits on top.
        //three-pass line: black outline, then soft halo, then bright core.
        var startPoint = new Vector2(slotCenterX, slotCenterY);

        if (ClientSettings.SpellTargetLine)
        {
            var dist = Vector2.Distance(startPoint, endPoint);
            var cp1 = startPoint + new Vector2(0f, -MathF.Min(220f, dist * 0.45f));
            var cp2 = endPoint + new Vector2(0f, MathF.Min(80f, dist * 0.12f));
            //Color.Orange (255,165,0) matches the chat orange. blue when snapped to a target entity.
            var lineCore = isSnapped ? new Color(60, 140, 255) : Color.Orange;
            DrawBezierLine(spriteBatch, startPoint, cp1, cp2, endPoint, TARGETING_BEZIER_SEGMENTS, Color.Black * 0.80f, 5.5f);
            DrawBezierLine(spriteBatch, startPoint, cp1, cp2, endPoint, TARGETING_BEZIER_SEGMENTS, lineCore * 0.30f, 4.0f);
            DrawBezierLine(spriteBatch, startPoint, cp1, cp2, endPoint, TARGETING_BEZIER_SEGMENTS, lineCore * 0.90f, 1.5f);
        }

        //icon overlay: pulse from 1.0x to 1.5x the SLOT-SCALED native size, centered on the slot center
        var t = (float)gameTime.TotalGameTime.TotalSeconds;
        var iconScale = 1f + 0.5f * (0.5f + 0.5f * MathF.Sin(t * TARGETING_PULSE_SPEED));
        var iconW = (int)(vs.Width * hostScale * iconScale);
        var iconH = (int)(vs.Height * hostScale * iconScale);
        var iconX = (int)(slotCenterX - iconW * 0.5f);
        var iconY = (int)(slotCenterY - iconH * 0.5f);
        spriteBatch.Draw(icon, new Rectangle(iconX, iconY, iconW, iconH), Color.White);

        //two center-aligned lines below the snapped target: "Cast" then the spell name (the spell auto-targets the
        //closest, so there's nothing to select, you're just casting it)
        var castLine = TtfTextRenderer.GetLine("Cast", TARGETING_FONT_SIZE);
        var spellName = targetingSlot.AbilityName;
        var nameLine = string.IsNullOrEmpty(spellName) ? null : TtfTextRenderer.GetLine(spellName, TARGETING_FONT_SIZE);
        var lineH = TtfTextRenderer.LineHeight(TARGETING_FONT_SIZE);
        var labelY = (int)(endPoint.Y + 22f);

        void DrawCenteredLabel(Texture2D? tex, int y)
        {
            if (tex is null || tex.IsDisposed)
                return;

            var x = (int)(endPoint.X - tex.Width * 0.5f);
            spriteBatch.Draw(tex, new Vector2(x + 1, y + 1), Color.Black * 0.75f);
            spriteBatch.Draw(tex, new Vector2(x, y), Color.White);
        }

        DrawCenteredLabel(castLine, labelY);
        DrawCenteredLabel(nameLine, labelY + lineH);
    }

    //draws a cubic bezier as a polyline of short rotated rectangles using a 1x1 pixel texture
    private static void DrawBezierLine(
        SpriteBatch spriteBatch,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        int segments,
        Color color,
        float thickness)
    {
        var pixel = UIElement.GetPixel();
        var origin = new Vector2(0f, 0.5f); //center thickness on the start point
        var prev = p0;

        for (var i = 1; i <= segments; i++)
        {
            var t = i / (float)segments;
            var mt = 1f - t;
            var next = (mt * mt * mt * p0)
                       + (3f * mt * mt * t * p1)
                       + (3f * mt * t * t * p2)
                       + (t * t * t * p3);

            var seg = next - prev;
            var len = seg.Length();

            if (len > 0.1f)
                spriteBatch.Draw(
                    pixel,
                    prev,
                    null,
                    color,
                    MathF.Atan2(seg.Y, seg.X),
                    origin,
                    new Vector2(len, thickness),
                    SpriteEffects.None,
                    0f);

            prev = next;
        }
    }

    private void DrawDragIcon(SpriteBatch spriteBatch)
    {
        var dragging = GetDraggingPanel();

        if (dragging?.DragTexture is { } panelIcon)
        {
            //follow the real cursor and match the source panel's magnification (e.g. the 2x inventory), so the ghost
            //sits under the mouse at the same size the item is shown. DragX/Y are in the panel's native space now.
            var scale = 1f;

            for (var p = dragging.Parent; p is not null; p = p.Parent)
                scale *= p.ContentScale;

            var w = (int)(panelIcon.Width * scale);
            var h = (int)(panelIcon.Height * scale);

            spriteBatch.Draw(panelIcon, new Rectangle(InputBuffer.MouseX - (w / 2), InputBuffer.MouseY - (h / 2), w, h), Color.White * 0.7f);

            return;
        }

        //equipment drag ghost: draw at the equipment book's scale so the icon appears the same size as in the book
        if (EquipmentDragIcon is { } eqIcon && !eqIcon.IsDisposed)
        {
            var scale = StatusBookHost?.Scale ?? 1f;
            var w = (int)(eqIcon.Width * scale);
            var h = (int)(eqIcon.Height * scale);

            spriteBatch.Draw(eqIcon, new Rectangle(InputBuffer.MouseX - (w / 2), InputBuffer.MouseY - (h / 2), w, h), Color.White * 0.7f);

            return;
        }

        //ground item drag ghost: draw at the same size the item renders on the ground (no extra scale)
        if (GroundItemDragIcon is { } groundIcon && !groundIcon.IsDisposed)
        {
            var w = groundIcon.Width;
            var h = groundIcon.Height;
            spriteBatch.Draw(groundIcon, new Rectangle(InputBuffer.MouseX - (w / 2), InputBuffer.MouseY - (h / 2), w, h), Color.White * 0.7f);
        }
    }

    //ground pass (full alpha): drawn under the foreground, so on open ground the cursor is fully visible
    private void DrawTileCursor(SpriteBatch spriteBatch) => DrawTileCursors(spriteBatch, 1f);

    //overlay pass (reduced alpha): drawn AFTER the foreground so the cursor shows THROUGH walls/foreground the same
    //way silhouetted entities do. On open ground it just overlays the full-alpha ground draw (no visible change);
    //behind foreground, where the ground draw was hidden, it reveals the cursor at ~50%.
    private void DrawTileCursorOverlay(SpriteBatch spriteBatch) => DrawTileCursors(spriteBatch, SilhouetteRenderer.SilhouetteAlpha);

    private void DrawTileCursors(SpriteBatch spriteBatch, float alpha)
    {
        //no ground/tile cursor at all while an NPC dialog is open (the world behind the modal is inert)
        if (MapFile is null || TileCursorTexture is null || NpcSession.Visible)
            return;

        //red cursor around the enemy we are set to auto-attack: combat state, not cursor-driven, so it stays even when
        //the pointer is over UI
        (int X, int Y)? attackTile = null;

        if (Pathfinding.TargetEntityId is { } targetId && (WorldState.GetEntity(targetId) is { } target))
        {
            attackTile = (target.TileX, target.TileY);
            DrawTileCursorAt(spriteBatch, target.TileX, target.TileY, TileCursorAttackColor * alpha);
        }

        //while a spell is readied, highlight the auto-snapped CLOSEST target (to the cursor) in blue - even when the
        //cursor isn't on it - so you can see what the cast will hit. This is the target a plain cast/click confirms.
        if ((CastTargetId is { } ctid) && (WorldState.GetEntity(ctid) is { } castTarget))
        {
            DrawTileCursorAt(spriteBatch, castTarget.TileX, castTarget.TileY, TileCursorDragColor * alpha);

            return;
        }

        //the cursor-driven hover/drag marker is suppressed while the pointer is over any UI control (only the combat
        //ring above survives), matching the inert world hover
        if (PointerOverUi || (WorldState.CurrentFrame.HoveredTile is not { } hoverTile))
            return;

        //the red attack marker takes priority: don't draw the hover/drag cursor on the same tile
        if (attackTile is { } at && (at.X == hoverTile.X) && (at.Y == hoverTile.Y))
            return;

        var dragging = WorldState.CurrentFrame.UseDragCursor;

        //don't draw the ground cursor on non-walkable tiles (walls / closed doors); the drag cursor is unaffected
        if (!dragging && IsTileWallBlocked(hoverTile.X, hoverTile.Y))
            return;

        DrawTileCursorAt(spriteBatch, hoverTile.X, hoverTile.Y, (dragging ? TileCursorDragColor : TileCursorHoverColor) * alpha);
    }

    private void DrawTileCursorAt(SpriteBatch spriteBatch, int tileX, int tileY, Color color)
    {
        var tileWorld = Camera.TileToWorld(tileX, tileY, MapFile!.Height);
        var tileScreen = Camera.WorldToScreen(new Vector2(tileWorld.X, tileWorld.Y));
        spriteBatch.Draw(TileCursorTexture!, new Vector2((int)tileScreen.X, (int)tileScreen.Y), color);
    }

    #endregion

}