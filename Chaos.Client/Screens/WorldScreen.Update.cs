#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Data;
using Chaos.Client.Controls.World.Hud;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Data.Models;
using Chaos.Client.Extensions;
using Chaos.Client.Models;
using Chaos.Client.Systems;
using Chaos.Client.Utilities;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Extensions.Common;
using Chaos.Geometry.Abstractions.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Pathfinder = Chaos.Client.Systems.Pathfinder;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    public void Update(GameTime gameTime)
    {
        if (PendingLoginSwitch)
        {
            PendingLoginSwitch = false;

            //logout faded us to black (BeginLogout); guarantee full black for any other path that lands here, switch to
            //the lobby at black, then fade the fresh lobby in - so logout reads as world -> black -> lobby.
            Game.SnapToBlack();
            Game.Screens.Switch(new LobbyLoginScreen(true));
            Game.FadeFromBlack();

            return;
        }

        //a silent reconnect reached the world again: reload a clean WorldScreen through the proven login -> world
        //handoff (snap to black, switch; the fresh screen fades itself in once its first map finishes loading).
        if (PendingReconnectReload)
        {
            PendingReconnectReload = false;

            Game.SnapToBlack();
            Game.Screens.Switch(new WorldScreen());

            return;
        }

        //a silent reconnect gave up: drop to the lobby (it keeps retrying on its own, or shows the rejection message).
        if (PendingReconnectGiveUp)
        {
            PendingReconnectGiveUp = false;

            Game.SnapToBlack();
            Game.Screens.Switch(new LobbyLoginScreen(false, ReconnectGiveUpMessage));
            Game.FadeFromBlack();

            return;
        }

        //while reconnecting, the world is FROZEN: drive the reconnect timers + overlay and skip the whole world sim
        //(no animation, no input dispatch). Escape bails out to the lobby instead of waiting the full timeout.
        if (Reconnecting)
        {
            Reconnect?.Update((float)gameTime.ElapsedGameTime.TotalSeconds);

            if (InputBuffer.WasKeyPressed(Keys.Escape))
                Reconnect?.Cancel();

            UpdateReconnectOverlay();

            return;
        }

        //the profile-picture file dialog runs on its own thread (so the game stays connected); veil the game while it
        //is open so it reads as paused
        PortraitDim.Visible = PortraitPickerOpen;

        if (PortraitPickerOpen)
        {
            PortraitDim.Width = ChaosGame.UiWidth;
            PortraitDim.Height = ChaosGame.UiHeight;
        }

        //calm login intro: hold full black a beat after the first map loaded so the night/darkness LightLevel snaps in
        //unseen, then start the slow eased reveal. The world keeps simulating behind the black (no freeze) - it is only
        //~0.3s, so entities just slide a hair before they appear.
        if (PendingIntroReveal)
        {
            IntroHoldRemaining -= (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (IntroHoldRemaining <= 0f)
            {
                PendingIntroReveal = false;
                //the world is about to fade in: clear any tooltip (and its hover state) so a stale one from the lobby /
                //a slot the cursor happens to rest on doesn't pop into the calm reveal.
                ResetTooltips();
                Game.FadeFromBlack(INTRO_FADE_SECONDS);
            }
        }

        var elapsedMs = (float)gameTime.ElapsedGameTime.TotalMilliseconds;

        //global tile animation tick at 100ms resolution (matches tile animation table format)
        AnimationTick = (int)(gameTime.TotalGameTime.TotalMilliseconds / 100);
        MapRenderer.UpdatePaletteCycling(AnimationTick);

        //advance entity animations and active effects
        var smoothScroll = ClientSettings.ScrollLevel > 0;            //local player walk smoothing
        var smoothOthers = ClientSettings.SmoothCreatureMovement;     //enemies / NPCs / other players
        var player = WorldState.GetPlayerEntity();

        //animation advancement doesn't depend on sort order; go through entities unsorted to avoid a stale sort
        //(SortDepth is position-based; movement later in Update would invalidate any sort taken here).
        foreach (var entity in WorldState.GetEntities())
        {
            //update water tile state before animation so swimming idle tick advances
            UpdateEntityWaterState(entity);

            //smooth the player's walk if Smooth scrolling is on; smooth every OTHER entity if Smooth creature movement
            //is on. Either way the slide plays out over the same duration - smoothing only changes step vs pixel lerp.
            var isSmooth = entity == player ? smoothScroll : smoothOthers;
            AnimationSystem.Advance(entity, elapsedMs, isSmooth);

            //local player footsteps: one cue on leaving the tile, one at the walk midpoint
            if (entity == player)
                UpdatePlayerFootsteps(entity);

            //update creature optional standing animation cycle
            if (entity.Type == ClientEntityType.Creature)
            {
                var animInfo = Game.CreatureRenderer.GetAnimInfo(entity.SpriteId);

                if (animInfo.HasValue)
                {
                    var info = animInfo.Value;
                    AnimationSystem.UpdateCreatureIdleCycle(entity, in info);
                }
            }

            //tick emote overlay timer and cycle animated emote frames
            if (entity.ActiveEmoteFrame >= 0)
            {
                entity.EmoteElapsedMs += elapsedMs;
                entity.EmoteRemainingMs -= elapsedMs;

                if (entity.EmoteRemainingMs <= 0)
                {
                    entity.ActiveEmoteFrame = -1;
                    entity.EmoteFrameCount = 0;
                } else if (entity.EmoteFrameCount > 1)
                {
                    var frameDuration = entity.EmoteDurationMs / entity.EmoteFrameCount;
                    var frameIndex = (int)(entity.EmoteElapsedMs / frameDuration) % entity.EmoteFrameCount;
                    entity.ActiveEmoteFrame = entity.EmoteStartFrame + frameIndex;
                }
            }

            if (entity.HitTintExpiryMs > 0)
                entity.HitTintExpiryMs = Math.Max(0, entity.HitTintExpiryMs - elapsedMs);

            if (entity.HitShakeMs > 0)
                entity.HitShakeMs = Math.Max(0, entity.HitShakeMs - elapsedMs);
        }

        WorldState.UpdateEffects(elapsedMs);
        AdvanceProjectiles(elapsedMs);

        //group highlight auto-expire (1000ms flash)
        if (GroupHighlightedIds.Count > 0)
        {
            GroupHighlightTimer -= elapsedMs;

            if (GroupHighlightTimer <= 0)
            {
                GroupHighlightedIds.Clear();
                Game.AislingRenderer.ClearGroupTintCache();
                Game.CreatureRenderer.ClearTintCaches();
            }
        }

        //death is absolute: while HP is 0 nothing moves the player - no pathfinding, no queued step, no held keys
        //(the held-key polls below are also gated through GameplayInputAllowed). The Sgrios spirit walks at 1 HP.
        if (IsPlayerDead)
        {
            Pathfinding.Clear();
            ResumeChaseTargetId = null;
            QueuedWalkDirection = null;
        }

        //resume a chase paused for a skill/spell once the action has fully ended (no chant/targeting, body
        //animation done) - the same enemy is re-acquired IF it still exists. A manually chosen new target wins.
        if (ResumeChaseTargetId is { } resumeId && player is not null)
        {
            if (Pathfinding.HasTarget)
                ResumeChaseTargetId = null; //the player picked a new target while the action ran
            else if (!CastingSystem.IsActive && player.IsAtRest)
            {
                ResumeChaseTargetId = null;

                if (WorldState.GetEntity(resumeId) is not null)
                    Pathfinding.SetEntityTarget(resumeId);
            }
        }

        //execute queued walk when player becomes idle after walk animation.
        var movementHandled = false;

        //Modern controls: drive walking from the HELD movement key each frame, so continuous movement does not stutter
        //waiting for the OS key-repeat delay after the first step. MoveOrTurn steps when idle and queues mid-walk, so a
        //per-frame call gives smooth movement; it also clears any pathfinding (manual input wins). Classic is unchanged
        //(retail key-repeat stepping via the keydown handler).
        if (player is not null && ClientSettings.ModernControls && GameplayInputAllowed()
            && Keybindings.HeldMovement(out var heldTurnOnly) is { } heldAction)
        {
            MoveOrTurn(MovementDirection(heldAction), heldTurnOnly);
            movementHandled = true;
        }

        //Hold-to-walk: while the move button (right by default) is held over the world, the player continuously re-paths
        //toward the cursor. The button PRESS (OnRootMouseDown -> HandleWorldRightClick) lays the first path and seeds the
        //baseline; this poll re-aims it while held. Re-aims are evaluated only when AT REST, so the path is planned from
        //the player's true tile (mid-walk the tile is predicted forward, which made a 1-tile target read as "arrived").
        //Three triggers combine to stay responsive at any range:
        //  - halfWalked: once HALF the current path's steps are walked, refresh before a short path runs out;
        //  - idleReaim: idle with no path and the cursor now points at a NEW tile - the key case, since the camera
        //    follows the player so a held cursor maps to a fresh tile every arrival, keeping a near cursor stepping;
        //  - intervalElapsed: re-aim a far cursor at least once a second.
        //HeldWalkLastTarget gates idleReaim so an unreachable / own tile doesn't re-path every frame. Only plain
        //tile-walks run here; an entity chase keeps its own follow, a held keyboard key wins, a UI drag never walks.
        //count down the post-warp grace (set in FinalizeMapLoad) so a move button still held from before a warp doesn't
        //auto-path on the fresh map for a moment
        if (HeldWalkSuppressMs > 0f)
            HeldWalkSuppressMs = Math.Max(0f, HeldWalkSuppressMs - elapsedMs);

        //count down the post-warp KEYBOARD-movement grace (gates MoveOrTurn), same purpose for held arrow keys
        if (KeyMoveSuppressMs > 0f)
            KeyMoveSuppressMs = Math.Max(0f, KeyMoveSuppressMs - elapsedMs);

        if (player is not null && !movementHandled && !IsPlayerDead && GameplayInputAllowed()
            && (HeldWalkSuppressMs <= 0f)
            && !Pathfinding.TargetEntityId.HasValue && MapFile is not null && MapPreloaded
            && IsMoveButtonHeld() && !Game.Dispatcher.IsDragging
            && ((InputBuffer.CurrentModifiers & (KeyModifiers.Shift | KeyModifiers.Ctrl)) == 0)
            && WorldInputBounds.Contains(InputBuffer.MouseX, InputBuffer.MouseY)
            && !IsPointerOverUi(InputBuffer.MouseX, InputBuffer.MouseY)) //don't hold-walk while the cursor is over a window (chat/etc.)
        {
            HeldWalkRepathTimer += elapsedMs;

            if (player.IsAtRest)
            {
                var (cursorX, cursorY) = HeldWalkCursorTile();
                var remaining = Pathfinding.Path?.Count ?? 0;

                var halfWalked = (HeldWalkPathLength > 0) && (remaining <= (HeldWalkPathLength / 2));
                var idleReaim = (remaining == 0) && (HeldWalkLastTarget != (cursorX, cursorY));
                var intervalElapsed = HeldWalkRepathTimer >= HELD_WALK_REPATH_INTERVAL_MS;

                if (halfWalked || idleReaim || intervalElapsed)
                {
                    HeldWalkRepathToCursor(player, cursorX, cursorY);
                    HeldWalkRepathTimer = 0;
                }
            }
        } else
        {
            HeldWalkRepathTimer = 0;
            HeldWalkPathLength = 0;
            HeldWalkLastTarget = null;
        }

        //held-attack: fires Spacebar every SPACEBAR_INTERVAL_MS while the Assail key is held AND the
        //player is standing still. Moving pauses the attack; stopping resumes it automatically.
        if (player is not null && player.IsAtRest && GameplayInputAllowed()
            && Keybindings.IsActionHeld(GameAction.Assail))
            TryAssail();

        //reset spam-hint counters once the auto-attack target is cleared
        if (!Pathfinding.TargetEntityId.HasValue)
        {
            AutoAttackSpaceWarned = false;
            RedundantClickTargetId = null;
            RedundantClickCount = 0;
        }

        if (!movementHandled && player is not null && player.IsAtRest && QueuedWalkDirection.HasValue)
        {
            var queuedDir = QueuedWalkDirection.Value;
            QueuedWalkDirection = null;

            if (player.Direction != queuedDir)
            {
                Game.Connection.Turn(queuedDir);
                player.Direction = queuedDir;
            } else
                PredictAndWalk(player, queuedDir);

            movementHandled = true;
        }

        //execute next pathfinding step when player becomes idle (Pathfinding is force-cleared above while dead)
        if (!movementHandled && player is not null && player.IsAtRest)
        {
            if (Pathfinding.Path is { Count: > 0 })
            {
                //if chasing an entity that no longer exists, stop
                if (Pathfinding.TargetEntityId.HasValue && WorldState.GetEntity(Pathfinding.TargetEntityId.Value) is null)
                    Pathfinding.Clear();
                else if (Pathfinding.TargetEntityId is { } adjacentChaseId
                         && WorldState.GetEntity(adjacentChaseId) is { } adjacentChaseTarget
                         && Pathfinder.IsAdjacent(
                             player.TileX,
                             player.TileY,
                             adjacentChaseTarget.TileX,
                             adjacentChaseTarget.TileY))
                {
                    //the instant ANY step lands adjacent to the chase target, stop and attack - the planned goal may
                    //be a DIFFERENT adjacent tile (the late-turn preference favors the far-axis side), and walking
                    //past a perfectly good attacking position to reach it read as circling the enemy before striking
                    Pathfinding.Path = null; //the exhausted-path branch below turns toward the target and assails
                } else
                {
                    //PEEK, don't pop: a step is only used once we actually walk it. Popping a blocked step used
                    //to desync the path (the next pop was then 2 tiles away -> non-cardinal -> the chase was dropped
                    //"in its tracks"), which is the click-an-enemy-and-the-character-just-stops bug.
                    var nextPoint = Pathfinding.Path.Peek();
                    var dx = nextPoint.X - player.TileX;
                    var dy = nextPoint.Y - player.TileY;

                    var pathDir = (dx, dy) switch
                    {
                        (0, -1) => Direction.Up,
                        (1, 0)  => Direction.Right,
                        (0, 1)  => Direction.Down,
                        (-1, 0) => Direction.Left,
                        _       => (Direction?)null
                    };

                    if (!pathDir.HasValue)
                    {
                        //path head is no longer adjacent (server correction moved us); rebuild instead of giving up
                        RecoverPath(player);
                    } else if (IsClosedDoorAt(nextPoint.X, nextPoint.Y))
                    {
                        //a closed door stops the walker; opening it is the player's act
                        Pathfinding.Path = null;
                    } else if (!IsGameMaster && !IsTilePassable(nextPoint.X, nextPoint.Y))
                    {
                        //something stepped into the next tile; re-route around it, keeping the chase alive
                        RecoverPath(player);
                    } else
                    {
                        Pathfinding.Path.Pop();

                        if (player.Direction != pathDir.Value)
                        {
                            Game.Connection.Turn(pathDir.Value);
                            player.Direction = pathDir.Value;
                        }

                        PredictAndWalk(player, pathDir.Value);
                    }
                }
            } else if (Pathfinding.TargetEntityId.HasValue)
            {
                //path exhausted with entity target; check if adjacent and assail, or re-pathfind
                var target = WorldState.GetEntity(Pathfinding.TargetEntityId.Value);

                if (target is null)
                    Pathfinding.Clear();
                else if (Pathfinder.IsAdjacent(
                             player.TileX,
                             player.TileY,
                             target.TileX,
                             target.TileY))
                {
                    //adjacent; turn toward target and assail
                    var faceDir = Pathfinder.DirectionToward(
                        player.TileX,
                        player.TileY,
                        target.TileX,
                        target.TileY);

                    if (faceDir.HasValue && (player.Direction != faceDir.Value))
                    {
                        Game.Connection.Turn(faceDir.Value);
                        player.Direction = faceDir.Value;
                    }

                    Game.Connection.Spacebar();
                } else
                {
                    //entity moved; re-pathfind on 100ms timer
                    Pathfinding.RetargetTimer += elapsedMs;

                    if (Pathfinding.RetargetTimer >= 100f)
                    {
                        Pathfinding.RetargetTimer = 0;
                        PathfindToEntity(player, target);

                        //no path RIGHT NOW (the target is boxed in / something blocks every lane) is not a reason
                        //to drop the chase: keep the target and try again, a touch slower so a walled-off target
                        //doesn't burn A* every 100ms. The chase ends when the target dies/leaves or the player
                        //moves/cancels - never silently.
                        if (Pathfinding.Path is null)
                            Pathfinding.RetargetTimer = -400f;
                    }
                }
            }
        }

        //tick re-pathfind timer while walking toward an entity target (the target is force-cleared above while dead)
        if (Pathfinding.TargetEntityId.HasValue && player is not null && (player.AnimState == EntityAnimState.Walking))
            Pathfinding.RetargetTimer += elapsedMs;

        //match the camera viewport to the window-filling world render target BEFORE lighting/darkness compute, so
        //the light positions and the darkness texture use the same expanded space the world is actually drawn in
        Camera.Resize(ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);

        //camera follows the player's visual position (tile + walk interpolation offset) - either rigidly, or as a
        //smooth accel/decel pan when the alternative camera is on - and applies any active damage shake
        var dtSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
        UpdateCameraFollow(dtSeconds);
        UpdateCameraEffects(dtSeconds);
        UpdateDeathFx(dtSeconds);

        //when the dialog speaker is spotlit, the world pass draws the dim + the bright speaker; tell the native dimmer to
        //skip its own base+vignette so the view isn't darkened twice (it still draws the side-bars)
        if (DialogDim is not null)
            DialogDim.SuppressBaseDim = SpeakerSpotlightActive;

        //viewport-layer updates; must always run regardless of which ui panel has input focus
        //so that the world keeps animating visually behind open windows.
        if (MapFile is not null)
            Overlays.Update(
                Camera,
                MapFile.Height,
                Game.CreatureRenderer,
                gameTime);

        //gather light sources for this frame and feed them to the renderers. Gated on the darkness being active (a dark
        //dungeon, or a day/night map at night) so foreground-tile lights work on town maps, not just MapFlags.Darkness
        //dungeons. LightAnimTime drives the flame flicker and is reused by the resize-time re-gather in Wiring.
        LightAnimTime = (float)gameTime.TotalGameTime.TotalSeconds;
        Lighting.Gather(MapFile, Camera, DarknessRenderer.IsActive, DarknessRenderer.DuskGlow, LightAnimTime, DarknessRenderer.IsAlwaysDark);

        //a carried lantern behaves differently by MAP TYPE. Cycle map: gentle ambient lift (10/20%), vignette stays, and
        //the blue tint is removed only LOCALLY by the lantern's light pool (no global effect). Dark map: strong ambient
        //lift (60/90%) and BOTH the blue tint and the vignette are fully removed. DarknessRenderer eases all of it.
        var lanternSize = WorldState.GetPlayerEntity()?.LanternSize ?? LanternSize.None;
        var cycleMap = DarknessRenderer.HasDayNightCycle;

        var ambientLift = (lanternSize, cycleMap) switch
        {
            (LanternSize.Small, true)  => DarknessRenderer.LanternReliefCycleSmall,
            (LanternSize.Large, true)  => DarknessRenderer.LanternReliefCycleLarge,
            (LanternSize.Small, false) => DarknessRenderer.LanternReliefDarkSmall,
            (LanternSize.Large, false) => DarknessRenderer.LanternReliefDarkLarge,
            _                          => 0f
        };

        DarknessRenderer.SetLanternRelief(ambientLift, (lanternSize != LanternSize.None) && !cycleMap);

        //ease the darkness toward the latest light level (3s); runs even when inactive so a fade-in still animates.
        //The light buffer itself is rebuilt each frame in Draw (LightingRenderer), so there is no carve to update here.
        DarknessRenderer.AdvanceFade((float)gameTime.ElapsedGameTime.TotalSeconds);

        WeatherRenderer.Update(gameTime, WorldViewport);

        //resolve which tooltip (if any) the cursor is over and show it after the configurable delay
        UpdateTooltips((float)gameTime.ElapsedGameTime.TotalSeconds);

        //which entity the mouse is hovering over this frame
        //suppressed entirely while an NPC dialog is open OR the cursor is over any HUD/menu/window control: no hand
        //cursor, no hover name tag, no hover highlight - the world behind a modal (or under a panel) should be inert to
        //the mouse. Cached so DrawTileCursors can suppress the ground marker on the same condition.
        PointerOverUi = NpcSession.Visible || IsPointerOverUi(InputBuffer.MouseX, InputBuffer.MouseY);

        //TILE FIRST: whatever is on the ground-cursor tile (a creature/aisling, else loot) is the hover target; only an
        //empty tile falls back to the sprite under the cursor. So the name tag / highlight / hand cursor track the tile
        //you point at, not just landing on the tall sprite - matching the click-the-tile behavior. (ResolveCursorTarget
        //guards the still-null MapFile during the first frames after login.)
        var hoverEntity = PointerOverUi ? null : ResolveCursorTarget(InputBuffer.MouseX, InputBuffer.MouseY);

        //never treat the player's OWN sprite as a hover target: no hand cursor and no highlight over yourself
        var newHoveredId = hoverEntity?.Type is ClientEntityType.Aisling or ClientEntityType.Creature
                           && !hoverEntity.IsHidden
                           && (hoverEntity.Id != Game.Connection.AislingId)
            ? hoverEntity.Id
            : (uint?)null;

        //the hand cursor shows over anything interactable: an enemy / NPC / player (newHoveredId) or a ground item
        var overInteractable = newHoveredId.HasValue || (hoverEntity?.Type == ClientEntityType.GroundItem);

        //signs and doors: pixel-perfect check first, then fall back to tile check for highlighting
        var hoveredFg = !PointerOverUi ? FindSignDoorAtCursor(InputBuffer.MouseX, InputBuffer.MouseY) : null;

        if (hoveredFg.HasValue)
            overInteractable = true;

        //also match when the cursor is directly on a tile with a sign or door
        if (!hoveredFg.HasValue && !PointerOverUi && MapFile is not null)
        {
            (var gx, var gy) = ScreenToTile(InputBuffer.MouseX, InputBuffer.MouseY);

            if ((gx >= 0) && (gy >= 0) && (gx < MapFile.Width) && (gy < MapFile.Height))
            {
                var tile = MapFile.Tiles[gx, gy];

                if (IsSignOrDoorFg(tile.LeftForeground) || IsSignOrDoorFg(tile.RightForeground))
                {
                    overInteractable = true;
                    hoveredFg = (gx, gy);
                }
            }
        }

        HoveredFgTile = hoveredFg;

        Game.UseHandCursor = overInteractable;

        //clickable links in an open board post / mail message: show the hand cursor while hovering an https:// link (the
        //click itself is handled in the left-press event loop below)
        if ((ArticleRead.Visible && ArticleRead.TryGetLinkAt(InputBuffer.MouseX, InputBuffer.MouseY, out _))
            || (MailRead.Visible && MailRead.TryGetLinkAt(InputBuffer.MouseX, InputBuffer.MouseY, out _)))
            Game.UseHandCursor = true;

        //a clickable player name in the chat log (click-to-whisper): show the in-game hand cursor while hovering it
        //(NameAt self-gates on a visible line, so no IsHitTestVisible check is needed)
        if (ChatWin?.NameAt(InputBuffer.MouseX, InputBuffer.MouseY) is not null)
            Game.UseHandCursor = true;

        //tick casting timer (chant lines are sent on a 1-second interval)
        CastingSystem.Update(elapsedMs, Game.Connection);

        //spacebar assail is handled in OnRootKeyDown. the dispatcher delivers both the
        //initial press and os key-repeat keydowns through the event pipeline, so dialogs
        //that eat spacebar (via e.Handled = true) naturally block it.

        var skipDispatch = false;

        foreach (var evt in InputBuffer.Events)
        {
            if (evt is not { Kind: BufferedInputKind.MouseButton, Button: MouseButton.Left, IsPress: true })
                continue;

            var mx = evt.X;
            var my = evt.Y;

            //a click on an https:// link in an open board post / mail message opens it (and eats the click so it doesn't
            //also start a text selection / walk the world behind the window)
            var linkUrl = string.Empty;

            if (ArticleRead.Visible)
                ArticleRead.TryGetLinkAt(mx, my, out linkUrl);

            if ((linkUrl.Length == 0) && MailRead.Visible)
                MailRead.TryGetLinkAt(mx, my, out linkUrl);

            if (linkUrl.Length > 0)
            {
                Browser.Open(linkUrl);
                skipDispatch = true;
            } else if (ChatWin?.NameAt(mx, my) is { } whisperName)
            {
                //click a player's name in the chat log -> start a whisper to them (eat the click so it doesn't also walk)
                ActiveChatInput.Focus($"-> {whisperName}: ", TextColors.Whisper);
                skipDispatch = true;
            } else if (AislingContext.Visible && !AislingContext.ContainsPoint(mx, my))
            {
                AislingContext.Hide();
                skipDispatch = true;
            } else if (AbilityMetadataDetails.Visible && (AbilityDetailsHost is null || !AbilityDetailsHost.ContainsPoint(mx, my)))
            {
                //the popup is magnified by a ScaleHost, so hit-test the HOST's on-screen rect (the inner control's own
                //bounds are in native coords); hiding the inner fades the host out via the visibility sync.
                AbilityMetadataDetails.Hide();
                skipDispatch = true;
            } else if (SocialStatusPicker.Visible && (SocialStatusHost is null || !SocialStatusHost.ContainsPoint(mx, my)))
            {
                //magnified by SocialStatusHost, so hit-test the HOST rect; a click anywhere outside closes the picker.
                //(this scan runs before dispatch, so the click that OPENS it can't close it on the same frame.)
                SocialStatusPicker.Hide();
                skipDispatch = true;
            }

            break;
        }

        //Mouse 4 (X1) = cast on closest FRIENDLY, Mouse 5 (X2) = cast on closest ENEMY, swappable via the "Flip mouse
        //target buttons" option. Press-only; only act while a spell is readied (the cast methods no-op otherwise). They
        //force the right side so you can't mis-fire on the wrong type even if the cursor is nearer something else.
        var friendlyButton = ClientSettings.FlipMouseTargetButtons ? MouseButton.X2 : MouseButton.X1;
        var enemyButton = ClientSettings.FlipMouseTargetButtons ? MouseButton.X1 : MouseButton.X2;

        foreach (var evt in InputBuffer.Events)
        {
            if (evt is not { Kind: BufferedInputKind.MouseButton, IsPress: true })
                continue;

            if (evt.Button == friendlyButton)
                TargetFriendlyCast();
            else if (evt.Button == enemyButton)
                TargetEnemyCast();
        }

        //resolve (once per frame) the entity a readied spell would hit - the closest target to the ground cursor - so the
        //blue highlight (entity tint, ground ring, bezier line) all reference the same target.
        CastTargetId = CastingSystem.IsTargeting ? FindClosestTarget()?.Id : null;

        //keep the native-resolution UI sized to the window (it can be resized to any size)
        Root!.Width = ChaosGame.UiWidth;
        Root.Height = ChaosGame.UiHeight;

        AnchorHotbars(elapsedMs);
        AnchorNpcDialog();
        AnchorPopups();
        UpdateMinimap();

        if (!skipDispatch)
            Game.Dispatcher.ProcessInput(Root!, gameTime);

        //the input just processed may have switched screens (and unloaded us). Bail before the frame tail touches
        //torn-down WorldState. See IsUnloaded / UnloadContent.
        if (IsUnloaded)
            return;

        //all movement has been processed at this point; sort once and publish the frame state.
        PopulateFrameState(newHoveredId);

        Root!.Update(gameTime);

        UpdateFeedbackSounds();
    }

    //the chase target stashed by PauseChaseForAction while a skill/spell runs. resumed in Update once the action ends
    private uint? ResumeChaseTargetId;

    //tracks whether the player was walking on the previous frame (walk-start transition detection)
    private bool WasWalkingFootstep;

    //tracks whether the 50%-progress step has fired for the current walk, so we fire exactly once
    //at the midpoint of each tile (in addition to the start-of-walk step).
    private bool FiredMidStep;

    //plays a footstep when the player leaves a tile (walk starts) and another when the walk
    //reaches 50% progress (mid-tile). gives two regular steps per tile without relying on
    //animation frame timing or accumulators.
    private void UpdatePlayerFootsteps(WorldEntity player)
    {
        //no footsteps while dead (angel sprite) or swimming
        if (player.IsOnSwimmingTile || player.IsDead)
        {
            WasWalkingFootstep = false;

            return;
        }

        var isWalking = player.AnimState == EntityAnimState.Walking;

        if (isWalking && !WasWalkingFootstep)
        {
            //just left the tile; push-off step, and reset the mid-step tracker so the 50% step fires
            FiredMidStep = false;
            Game.SoundSystem.PlayFootstep();
        }

        if (isWalking && !FiredMidStep)
        {
            var totalDuration = Math.Max(
                AnimationSystem.MIN_WALK_DURATION_MS,
                player.AnimFrameCount * player.AnimFrameIntervalMs);

            if (player.AnimElapsedMs >= totalDuration * 0.5f)
            {
                Game.SoundSystem.PlayFootstep();
                FiredMidStep = true;
            }
        }

        WasWalkingFootstep = isWalking;
    }

    //plays a short sound whenever the inventory CHANGES (an item gained OR lost) and another whenever the player's
    //GOLD changes, while staying silent for the login inventory/gold fill and for inventory rearranges. The change is
    //measured per frame against a baseline, so a swap (an add + a remove applied in the same packet drain) cancels to
    //zero and is silent; only a real change in the total item count / gold sounds. Both are armed a short grace after
    //the first world entry so the initial fill never triggers them.
    private void UpdateFeedbackSounds()
    {
        var total = 0;

        for (byte slot = 1; slot <= Inventory.MAX_SLOTS; slot++)
        {
            var data = WorldState.Inventory.GetSlot(slot);

            if (data.IsOccupied)
                total += data.Stackable ? (int)data.Count : 1;
        }

        var gold = (long)WorldState.Inventory.Gold;

        if (ItemSoundArmed && (total != LastInventoryItemCount))
            Game.SoundSystem.PlaySound(SoundSystem.SoundItem);

        if (ItemSoundArmed && (gold != LastGold))
            Game.SoundSystem.PlaySound(SoundSystem.SoundMoney);

        LastInventoryItemCount = total;
        LastGold = gold;

        //arm AFTER updating the baselines, a grace past world entry, so the initial inventory/gold fill is never counted
        if (!ItemSoundArmed && HasEnteredWorld && ((Environment.TickCount - WorldEntryTick) >= ItemSoundArmDelayMs))
            ItemSoundArmed = true;
    }

    //sets each hotbar's slot labels from the player's ACTUAL bindings (skills = Skill1.., spells = Spell1.., inventory =
    //Item1..), short form (e.g. "1", "S+1", "F1"). Called at setup and on every Keybindings.Changed so a rebind in the
    //Controls window is reflected immediately.
    private void RefreshHotbarSlotLabels()
    {
        var skillLabels = Keybindings.SlotBarLabels(GameAction.Skill1);
        var spellLabels = Keybindings.SlotBarLabels(GameAction.Spell1);
        var invLabels   = Keybindings.SlotBarLabels(GameAction.Item1);

        if (SkillBarPanel is not null)
            SkillBarPanel.SlotLabels = skillLabels;

        if (SkillWinPanel is not null)
            SkillWinPanel.SlotLabels = skillLabels;

        if (SpellBarPanel is not null)
            SpellBarPanel.SlotLabels = spellLabels;

        if (SpellWinPanel is not null)
            SpellWinPanel.SlotLabels = spellLabels;

        if (InvBarPanel is not null)
            InvBarPanel.SlotLabels = invLabels;

        if (SmallHud?.Inventory is not null)
            SmallHud.Inventory.SlotLabels = invLabels;
    }

    //applies the live hotbar scale and re-centers the bars. inventory sits at top-center, skills + spells at bottom-center
    //(spells just under skills). Runs every frame so it follows window resizes and a future scale slider.
    private const float HOTBAR_TARGET_ALPHA = 0.35f;
    private const float HOTBAR_FADE_SPEED = 4f; //per second

    //drives the corner minimap: bakes the town-map inked texture for the current map (cached, async, no M window needed)
    //and points the minimap at it + the player's tile. Hidden when turned off or there is no map.
    private void UpdateMinimap()
    {
        //wait for the map to be fully preloaded before generating the overview. Generating from not-yet-created tile
        //textures renders garbage/blank on some drivers, which then gets baked into the inked map and cached for this map,
        //leaving the minimap AND town map blank until the next map change (re-entering the map worked because the tiles
        //were cached by then).
        if (!ClientSettings.ShowMinimap || (MapFile is null) || !MapPreloaded)
        {
            Minimap.Visible = false;
            MinimapWarmupFrames = MINIMAP_WARMUP_FRAMES;

            return;
        }

        //even once preloaded, give the freshly created tile textures a few frames to settle before baking the overview
        //(a texture rendered the same frame it is created can come out undefined), so the cached bake is always clean
        if (MinimapWarmupFrames > 0)
        {
            MinimapWarmupFrames--;
            Minimap.Visible = false;

            return;
        }

        if (ComputeBaseFollow() is not { } centerWorld)
        {
            Minimap.Visible = false;

            return;
        }

        MapOverview.Generate(Device, MapRenderer, MapFile, AnimationTick, includeBackground: false, separateBackground: true);
        MinimapWallLookup ??= MapWallAt;
        TownMapControl.EnsureInkedForMinimap(Device, MapOverview, MinimapWallLookup);

        //the current walk path (drawn as a dotted trail) and the move target (entity chase tile, else the path's
        //destination) for the flag marker
        Point? target = null;
        MinimapPathBuffer.Clear();

        if ((Pathfinding.TargetEntityId is { } tid) && (WorldState.GetEntity(tid) is { } te))
            target = new Point(te.TileX, te.TileY);

        if (Pathfinding.Path is { Count: > 0 } path)
        {
            foreach (var pt in path) //top -> bottom of the stack (the bottom is the destination tile)
                MinimapPathBuffer.Add(new Point(pt.X, pt.Y));

            target ??= MinimapPathBuffer[^1];
        }

        //show roughly the server's synced range around the player plus a margin: convert a tile radius to inked pixels
        var inkedRadius = MINIMAP_TILE_RADIUS * TownMapControl.InkedPixelsPerTile;
        Minimap.SetSource(TownMapControl, centerWorld, inkedRadius, WarpData.GetClusters(CurrentMapId), MinimapPathBuffer, target);
        Minimap.Visible = true;
    }

    //reused each frame to hand the current walk path to the minimap without allocating
    private readonly List<Point> MinimapPathBuffer = [];

    //how many tiles (radius) the minimap covers - about the synced range plus ~50%
    private const float MINIMAP_TILE_RADIUS = 16f;

    //frames to wait after a map is preloaded before baking the overview, so freshly created tile textures have settled
    private const int MINIMAP_WARMUP_FRAMES = 3;
    private int MinimapWarmupFrames = MINIMAP_WARMUP_FRAMES;

    //collision lookup (bounds-safe) for the town map / minimap inked build
    private bool MapWallAt(int x, int y)
        => (MapFile is not null) && (x >= 0) && (y >= 0) && (x < MapFile.Width) && (y < MapFile.Height) && IsTileWallBlocked(x, y);

    private void AnchorHotbars(float elapsedMs)
    {
        if ((InvBar is null) || (SkillBar is null) || (SpellBar is null))
            return;

        //dim the hotbars while awaiting a spell target so the cursor overlay reads clearly
        var alphaTarget = CastingSystem.IsTargeting ? HOTBAR_TARGET_ALPHA : 1f;
        var step = HOTBAR_FADE_SPEED * (elapsedMs / 1000f);
        TargetingHotbarAlpha = alphaTarget > TargetingHotbarAlpha
            ? MathF.Min(alphaTarget, TargetingHotbarAlpha + step)
            : MathF.Max(alphaTarget, TargetingHotbarAlpha - step);
        InvBar.ExternalAlpha = TargetingHotbarAlpha;
        SkillBar.ExternalAlpha = TargetingHotbarAlpha;
        SpellBar.ExternalAlpha = TargetingHotbarAlpha;

        var scale = ClientSettings.EffectiveHotbarScale;
        InvBar.Scale = scale;
        SkillBar.Scale = scale;
        SpellBar.Scale = scale;

        const int margin = 6;
        var w = ChaosGame.UiWidth;
        var h = ChaosGame.UiHeight;

        InvBar.X = (w - InvBar.Width) / 2;
        InvBar.Y = margin;

        SpellBar.X = (w - SpellBar.Width) / 2;
        SpellBar.Y = h - SpellBar.Height - margin;

        SkillBar.X = (w - SkillBar.Width) / 2;
        SkillBar.Y = SpellBar.Y - SkillBar.Height - 2;

        //hp/mp orbs: scaled by hotbar scale, bottom-aligned with the spell bar, flanking it
        if ((HpOrb is not null) && (MpOrb is not null))
        {
            const int ORB_NATIVE_W = 30;
            const int ORB_NATIVE_H = 101;
            const int ORB_GAP = 3;

            var orbW = (int)(ORB_NATIVE_W * scale);
            var orbH = (int)(ORB_NATIVE_H * scale);
            var spellBottom = SpellBar.Y + SpellBar.Height;

            HpOrb.Width = orbW;
            HpOrb.Height = orbH;
            HpOrb.X = SpellBar.X - orbW - ORB_GAP;
            HpOrb.Y = spellBottom - orbH;

            MpOrb.Width = orbW;
            MpOrb.Height = orbH;
            MpOrb.X = SpellBar.X + SpellBar.Width + ORB_GAP;
            MpOrb.Y = spellBottom - orbH;

            //ONE-TIME default placement: size the chat to fill the lower-left gap beside the HP orb.
            //Skipped if the player already has a saved position in config.
            if (!ChatDefaultSized && (ChatWin is not null) && (orbW > 0))
            {
                ChatDefaultSized = true;

                if (ClientSettings.ChatWindowOffsetX == int.MinValue)
                {
                    const int CHAT_MARGIN = 8;
                    const int ORB_CLEAR = 6;

                    var targetW = Math.Max(180, HpOrb.X - ORB_CLEAR - CHAT_MARGIN);
                    ChatWin.X = CHAT_MARGIN;
                    ChatWin.Resize(targetW, ChatWin.Height);
                    ChatWin.Y = h - ChatWin.Height - CHAT_MARGIN;
                    ChatWin.CommitPosition(); //persist the default so it survives the next resize
                }
            }
        }

        //status-effect bar: right edge, vertically centered. Magnified 2x the hotbar scale so the half-size icons read
        //at roughly a hotbar slot's size. Only visible (host syncs to the inner) while at least one effect is active.
        if (BuffBarHost is not null)
        {
            BuffBarHost.Scale = scale * 2f;
            BuffBarHost.X = w - BuffBarHost.Width - margin;
            BuffBarHost.Y = Math.Max(margin, (h - BuffBarHost.Height) / 2);
        }

        //SWM quest tracker: self-positions (draggable, persisted, rescale-relative) - see QuestTrackerControl.Update

        //feed the chat window's ClampToScreen with the current rects of every HUD element it must avoid
        ChatWindow.BeginHudRects();
        ChatWindow.AddHudRect(new Rectangle(InvBar.X, InvBar.Y, InvBar.Width, InvBar.Height));
        ChatWindow.AddHudRect(new Rectangle(SkillBar.X, SkillBar.Y, SkillBar.Width, SkillBar.Height));
        ChatWindow.AddHudRect(new Rectangle(SpellBar.X, SpellBar.Y, SpellBar.Width, SpellBar.Height));

        if (HpOrb?.Visible == true)
            ChatWindow.AddHudRect(new Rectangle(HpOrb.X, HpOrb.Y, HpOrb.Width, HpOrb.Height));

        if (MpOrb?.Visible == true)
            ChatWindow.AddHudRect(new Rectangle(MpOrb.X, MpOrb.Y, MpOrb.Width, MpOrb.Height));

        if (Minimap?.Visible == true)
            ChatWindow.AddHudRect(new Rectangle(Minimap.X, Minimap.Y, Minimap.Width, Minimap.Height));

        //menu button: compute from ClientSettings (WorldScreen has no MenuBar field)
        {
            var cx = w / 2;
            var mbOffX = ClientSettings.MenuButtonOffsetX;
            var mbOffY = ClientSettings.MenuButtonOffsetY;

            int mbX, mbY;

            if ((mbOffX != int.MinValue) && (mbOffY != int.MinValue))
            {
                mbX = cx + mbOffX;
                mbY = mbOffY >= 0 ? mbOffY : h + mbOffY;
            }
            else
            {
                mbX = 2;
                mbY = 2;
            }

            ChatWindow.AddHudRect(new Rectangle(mbX, mbY, 76, 22)); //BTN_W=76, ITEM_H=22
        }

    }

    //scales the NPC/sign dialog each frame while it is open and pins it to the BOTTOM of the screen (centered
    //horizontally). Best-fit = the same letterbox factor the world uses (min of width/640, height/480), so the dialog
    //keeps the same on-screen proportion it had in the classic 640x480 view. Runs every frame so it follows resizes.
    private void AnchorNpcDialog()
    {
        if ((NpcSessionHost is null) || !NpcSessionHost.Visible)
            return;

        var fit = Math.Min(
            ChaosGame.UiWidth / (float)ChaosGame.VIRTUAL_WIDTH,
            ChaosGame.UiHeight / (float)ChaosGame.VIRTUAL_HEIGHT);

        NpcSessionHost.Scale = fit; //ScaleHost clamps to >= 1

        //horizontally centered, but PINNED TO THE BOTTOM of the screen: the dialog bar + portrait sit at the bottom of
        //the 640x480 canvas, so bottom-anchoring the canvas puts them on the screen's bottom edge at any window size.
        //ContentYOffset (the first-appearance slide) then eases it up into place from just below.
        NpcSessionHost.X = (ChaosGame.UiWidth - NpcSessionHost.Width) / 2;
        NpcSessionHost.Y = ChaosGame.UiHeight - NpcSessionHost.Height + (int)NpcSession.ContentYOffset;
    }

    //keeps the gold/item amount prompts centered and at the current Window-size scale each frame while open (so they
    //follow a window resize or a live slider change). The exchange window is draggable and centers itself on each open.
    private void AnchorPopups()
    {
        AnchorScaledPopup(GoldDropHost);
        AnchorScaledPopup(ItemAmountHost);
        AnchorTextPopup();
    }

    //the sign/board popup: centered, but while the host fades open it slides UP from half its height below center
    //(and back down on close) - driven off OpenFraction so the slide and fade always agree
    private void AnchorTextPopup()
    {
        if ((TextPopupHost is null) || !TextPopupHost.Visible)
            return;

        TextPopupHost.Scale = ClientSettings.EffectiveWindowScale;

        var f = Math.Clamp(TextPopupHost.OpenFraction, 0f, 1f);
        var ease = 1f - ((1f - f) * (1f - f)); //ease-out: fast start, soft landing

        TextPopupHost.X = (ChaosGame.UiWidth - TextPopupHost.Width) / 2;
        TextPopupHost.Y = ((ChaosGame.UiHeight - TextPopupHost.Height) / 2) + (int)(TextPopupHost.Height * 0.5f * (1f - ease));
    }

    private static void AnchorScaledPopup(ScaleHost? host)
    {
        if ((host is null) || !host.Visible)
            return;

        host.Scale = ClientSettings.EffectiveWindowScale;
        host.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
    }

    private void PopulateFrameState(uint? newHoveredId)
    {
        //capture prev before Clear wipes it so the dirty-check still has last frame's value.
        var prevHoveredId = WorldState.CurrentFrame.HoveredEntityId;
        WorldState.CurrentFrame.Reset();

        if (newHoveredId != prevHoveredId)
        {
            Game.AislingRenderer.ClearTintedCache();
            Game.CreatureRenderer.ClearTintCaches();
        }

        //GetSortedEntities is self-caching via dirty flag, so this call is free when the sort is still valid.
        WorldState.CurrentFrame.SortedEntities = WorldState.GetSortedEntities();
        WorldState.CurrentFrame.HoveredEntityId = newHoveredId;
        WorldState.CurrentFrame.ShowTintHighlight = CastingSystem.IsTargeting || Game.Dispatcher.IsDragging;
        WorldState.CurrentFrame.UseDragCursor = Game.Dispatcher.IsDragging;

        //group-box overlay is drawn in 640x480 world space, so hit-test it with the converted mouse
        WorldState.CurrentFrame.HoveredGroupBoxId = Overlays
                                             .GetGroupBoxAtScreen(
                                                 ToWorldX(InputBuffer.MouseX),
                                                 ToWorldY(InputBuffer.MouseY))
                                             ?.EntityId;

        //the world fills the whole window now; only set the hovered tile when the (native) mouse is over it.
        var worldBounds = WorldInputBounds;

        if (MapFile is null
            || (InputBuffer.MouseX < worldBounds.X)
            || (InputBuffer.MouseX >= (worldBounds.X + worldBounds.Width))
            || (InputBuffer.MouseY < worldBounds.Y)
            || (InputBuffer.MouseY >= (worldBounds.Y + worldBounds.Height)))
            return;

        (var tileX, var tileY) = ScreenToTile(InputBuffer.MouseX, InputBuffer.MouseY);

        if ((tileX < 0) || (tileX >= MapFile.Width) || (tileY < 0) || (tileY >= MapFile.Height))
            return;

        WorldState.CurrentFrame.HoveredTile = new Point(tileX, tileY);
    }

    #region Projectile Advancement
    private void AdvanceProjectiles(float elapsedMs)
    {
        if (MapFile is null)
            return;

        for (var i = WorldState.ActiveProjectiles.Count - 1; i >= 0; i--)
        {
            var proj = WorldState.ActiveProjectiles[i];
            proj.ElapsedMs += elapsedMs;

            //defensive: a malformed projectile spec (StepDelayMs <= 0 or Step <= 0) would never
            //drain ElapsedMs or never reach the target, freezing the game thread inside this loop.
            if (proj.StepDelayMs <= 0f || proj.Step <= 0)
            {
                proj.IsComplete = true;
                WorldState.ActiveProjectiles.RemoveAt(i);

                continue;
            }

            //loop cap defends against pathological cases where the projectile genuinely
            //can't catch a moving target within one frame's worth of elapsed time.
            const int MAX_STEPS_PER_FRAME = 64;
            var steps = 0;

            while ((proj.ElapsedMs >= proj.StepDelayMs) && !proj.IsComplete && (steps < MAX_STEPS_PER_FRAME))
            {
                proj.ElapsedMs -= proj.StepDelayMs;
                AdvanceProjectileStep(proj);
                steps++;
            }

            if (proj.IsComplete)
                WorldState.ActiveProjectiles.RemoveAt(i);
        }
    }

    private const float HIT_TINT_FLASH_MS = 100f;

    private void AdvanceProjectileStep(Projectile proj)
    {
        var targetEntity = WorldState.GetEntity(proj.TargetEntityId);

        if (targetEntity is not null)
        {
            var targetWorld = Camera.TileToWorld(targetEntity.TileX, targetEntity.TileY, MapFile!.Height);
            proj.LastKnownTargetX = targetWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            proj.LastKnownTargetY = targetWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;
        }

        var dx = proj.LastKnownTargetX - proj.CurrentX;
        var dy = proj.LastKnownTargetY - proj.CurrentY;
        var distSq = dx * dx + dy * dy;
        var stepSq = (float)proj.Step * proj.Step;

        if (distSq <= stepSq)
        {
            proj.IsComplete = true;

            targetEntity?.HitTintExpiryMs = HIT_TINT_FLASH_MS;

            return;
        }

        var remainingDistance = MathF.Sqrt(distSq);
        var unitX = dx / remainingDistance;
        var unitY = dy / remainingDistance;

        proj.CurrentX += unitX * proj.Step;
        proj.CurrentY += unitY * proj.Step;
        proj.DistanceTraveled += proj.Step;

        if (proj is { ArcRatioV: not null, ArcRatioH: not null, InitialDistance: > 0 })
        {
            var progress = Math.Clamp(proj.DistanceTraveled / proj.InitialDistance, 0f, 1f);
            var arcHeight = proj.InitialDistance * proj.ArcRatioV.Value / proj.ArcRatioH.Value / 2f;
            var arcOffset = MathF.Sin(MathF.PI * progress) * arcHeight;

            //perpendicular to heading (rotate 90°)
            proj.ArcOffsetX = -unitY * arcOffset;
            proj.ArcOffsetY = unitX * arcOffset;
        }

        if (targetEntity is not null)
        {
            var projTile = Camera.WorldToTile(proj.CurrentX, proj.CurrentY, MapFile!.Height);

            proj.Direction = GetProjectileDirection(
                targetEntity.TileX - projTile.X,
                targetEntity.TileY - projTile.Y);
        }

        if (proj.FramesPerDirection > 1)
            proj.CurrentFrameCycle = (proj.CurrentFrameCycle + 1) % proj.FramesPerDirection;
    }
    #endregion

    /// <summary>
    ///     Returns the first visible modal panel among Root's children (highest ZIndex first), or null.
    /// </summary>
    private UIPanel? FindVisibleModal()
    {
        if (Root is null)
            return null;

        UIPanel? best = null;

        foreach (var child in Root.Children)
            if (child is UIPanel { Visible: true, IsModal: true } panel && (best is null || (panel.ZIndex > best.ZIndex)))
                best = panel;

        return best;
    }

    //hides the shared tooltip and clears all the hover targets + resolver state that would re-show it next frame. Called
    //when the world fades in after connecting so nothing lingers into the reveal.
    private void ResetTooltips()
    {
        ItemTooltip.Hide();
        TooltipKey = null;
        TooltipClickSuppressedKey = null;
        TooltipDelayTimer = 0f;
        HoveredInventorySlot = null;
        HoveredNpcItemName = null;
        HoveredAbilitySlot = null;
        HoveredOrb = null;

        //don't let a tooltip pop for whatever the still-stationary cursor rests on during the reveal; wait for a move
        TooltipSuppressedUntilMove = true;
        LastTooltipMouseX = InputBuffer.MouseX;
        LastTooltipMouseY = InputBuffer.MouseY;
    }

    private void UpdateTooltips(float dtSeconds)
    {
        //after a connect/reveal, stay fully suppressed until the cursor moves (then resume normal hover behaviour)
        if (TooltipSuppressedUntilMove)
        {
            if ((InputBuffer.MouseX != LastTooltipMouseX) || (InputBuffer.MouseY != LastTooltipMouseY))
                TooltipSuppressedUntilMove = false;
            else
            {
                if (ItemTooltip.Visible)
                    ItemTooltip.Hide();

                TooltipKey = null;
                TooltipDelayTimer = 0f;

                return;
            }
        }

        var show = ResolveTooltip(out var key);

        //left click: dismiss and lock out until the cursor leaves this target and re-enters
        if (InputBuffer.IsLeftButtonHeld && ItemTooltip.Visible)
        {
            ItemTooltip.Hide();
            TooltipClickSuppressedKey = key;
            TooltipDelayTimer = 0f;

            return;
        }

        //clear the per-target click suppression once the cursor moves to a different (or no) target
        if (TooltipClickSuppressedKey is not null && !Equals(TooltipClickSuppressedKey, key))
            TooltipClickSuppressedKey = null;

        if (key is null)
        {
            if (ItemTooltip.Visible)
                ItemTooltip.Hide();

            TooltipKey = null;
            TooltipDelayTimer = 0f;

            return;
        }

        //still hovering the element that was just clicked; stay hidden
        if (TooltipClickSuppressedKey is not null)
            return;

        var changed = !Equals(key, TooltipKey);
        TooltipKey = key;

        if (ItemTooltip.Visible)
        {
            if (changed || TooltipDynamic)
                show!(InputBuffer.MouseX, InputBuffer.MouseY); //switched target OR live content (cooldown) -> rebuild
            else
                ItemTooltip.UpdatePosition(InputBuffer.MouseX, InputBuffer.MouseY); //same target -> just follow the cursor
        } else
        {
            TooltipDelayTimer += dtSeconds;

            if (TooltipDelayTimer >= ClientSettings.TooltipDelaySeconds)
                show!(InputBuffer.MouseX, InputBuffer.MouseY);
        }
    }

    //returns the action that shows the hovered tooltip plus a key identifying its target (null = nothing to show)
    private Action<int, int>? ResolveTooltip(out object? key)
    {
        key = null;
        TooltipDynamic = false; //only a live cooldown sets this true (below), forcing a per-frame rebuild

        //0. an NPC shop/dialog item (the dialog reports its hovered item name). Shown first since the dialog sits on top
        if (NpcSession.Visible && (HoveredNpcItemName is { } npcName))
        {
            key = npcName;

            return (x, y) => ItemTooltip.Show(npcName, 0, 0, x, y);
        }

        //priority 0.5: a skill/spell in the NPC teaching list (ShowSkills/ShowSpells). Polled like book/stats tooltips.
        if (NpcSession.Visible)
        {
            var (dx, dy) = MapToDialog(InputBuffer.MouseX, InputBuffer.MouseY);

            if (NpcSession.MenuList.HitTeachAbility(dx, dy) is { } ta)
            {
                if (LookupAbility(ta.Name, ta.IsSpell) is { } teachEntry)
                {
                    key = teachEntry;
                    var (title, category, body) = AbilityInfo.Build(teachEntry);

                    return (x, y) => ItemTooltip.ShowInfo(title, category, body, x, y);
                }

                key = ta.Name;

                return (x, y) => ItemTooltip.ShowInfo(ta.Name, string.Empty, x, y);
            }
        }

        //blocking modals (NPC dialog, any modal popup) suppress the remaining hover tooltips. The other-player profile
        //is deliberately NOT in this list: like your own book it sits over the world without blocking, so your hotbar/
        //equipment tooltips keep working while it is open (the book's own art still occludes hover for anything directly
        //behind it). Its OWN equipped items get no tooltip - the profile packet only sends each item's sprite + color,
        //not its name, and the tooltip is looked up by name, so there is nothing to resolve.
        if (NpcSession.Visible || (FindVisibleModal() is not null))
            return null;

        //slot tooltips are suppressed while targeting a spell (the hotbars dim to signal targeting mode)
        if (CastingSystem.IsTargeting)
            return null;

        //a reward item in the open quest journal -> show that item's detail tooltip (looked up by name)
        if (QuestJournal is { Visible: true, HoveredRewardName: { } rewardName })
        {
            key = rewardName;

            return (x, y) => ItemTooltip.Show(rewardName, 0, 0, x, y);
        }

        //a reward item in the NPC quest-offer window -> the same item tooltip
        if (QuestOffer is { Visible: true, HoveredRewardName: { } offerReward })
        {
            key = offerReward;

            return (x, y) => ItemTooltip.Show(offerReward, 0, 0, x, y);
        }

        //1. an inventory slot (hovered slot is kept in sync by the panel's hover events)
        if (HoveredInventorySlot is { } slot && IsElementShown(slot))
        {
            key = slot;
            var name = slot.SlotName ?? string.Empty;
            var cur = slot.CurrentDurability;
            var max = slot.MaxDurability;

            return (x, y) => ItemTooltip.Show(name, cur, max, x, y);
        }

        //2. a skill/spell slot (hotbar or the K/P book windows) - show the ability's detail (the click-popup info).
        //IsElementShown guards the case where a K/P window is closed while the cursor is still over a slot (no mouse-leave
        //fires then, so the hovered-slot state would otherwise linger and stick the tooltip open).
        if (HoveredAbilitySlot is { AbilityName: { Length: > 0 } abilityName } abilitySlot && IsElementShown(abilitySlot))
        {
            var isSpell = abilitySlot is SpellSlot;

            //for a LEARNED spell the slot's CastLines (from the AddSpellToPane packet) is authoritative - it can
            //differ from the metafile (custom/test grants) and is never stale
            var liveLines = (abilitySlot as SpellSlot)?.CastLines;

            //live remaining cooldown on this slot (counts down) - shown prominently in the tooltip when active
            var cooldown = isSpell
                ? WorldState.SpellBook.GetCooldownRemainingSeconds(abilitySlot.Slot)
                : WorldState.SkillBook.GetCooldownRemainingSeconds(abilitySlot.Slot);

            //rebuild every frame while the countdown is running so it ticks; also rebuild the single frame it reaches 0
            //(was active last frame) so the "On cooldown" line is removed cleanly instead of freezing at ~0.
            var cdActive = cooldown > 0.001f;

            if (cdActive || HoverCooldownWasActive)
                TooltipDynamic = true;

            HoverCooldownWasActive = cdActive;

            if (LookupAbility(abilityName, isSpell) is { } entry)
            {
                key = entry;

                return (x, y) =>
                {
                    var (title, category, body) = AbilityInfo.Build(entry, liveLines, cooldown);
                    ItemTooltip.ShowInfo(title, category, body, x, y);
                };
            }

            //no metadata on file for this ability: still show its name and whatever the slot itself knows
            var label = abilitySlot.SlotName ?? abilityName;
            key = label;

            return (x, y) =>
            {
                var parts = new List<string>();

                if (liveLines is { } lines)
                    parts.Add($"<grey>Cast lines:</grey> {(lines > 0 ? lines.ToString() : "instant")}");

                if (cooldown > 0.001f)
                    parts.Add($"<orange>On cooldown: {AbilityInfo.FormatCooldownRemaining(cooldown)}</orange>");

                ItemTooltip.ShowInfo(label, string.Join("\n", parts), x, y);
            };
        }

        //3. the equipment book - an equipped item icon, else a stat value label. ONLY on the Equipment tab: the slots
        //live under the other tabs' content (e.g. the Album thumbnails), so without this an album picture would pop an
        //equipped item's tooltip.
        if (StatusBook.Visible && (StatusBook.ActiveTab == StatusBookTab.Equipment))
        {
            var (bx, by) = MapToBook(InputBuffer.MouseX, InputBuffer.MouseY);

            if ((StatusBook.HitEquipItem(bx, by) is { } eqSlot) && (WorldState.Equipment.GetSlot(eqSlot) is { } data))
            {
                key = eqSlot;
                var name = data.Name;
                var cur = data.CurrentDurability;
                var max = data.MaxDurability;

                return (x, y) => ItemTooltip.Show(name, cur, max, x, y);
            }

            if (StatusBook.HitEquipStat(bx, by) is { } eqKind)
            {
                key = eqKind;
                var (title, body) = StatInfo.Get(eqKind);

                return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
            }

            //the baked field-name words (STR/Name/Guild/...) on the equipment page carry no real control - InfoHotspots
            //over the art give them hover help, same as the Stats window
            if (StatusBook.HitEquipInfoHotspot(bx, by) is { } eqHotspot)
            {
                key = eqHotspot;

                return (x, y) => ItemTooltip.ShowInfo(eqHotspot.Title, eqHotspot.Body, x, y);
            }
        }

        //4. the Stats window - a stat value label
        if ((StatsWin?.Visible == true) && (StatsWinPanel is not null))
        {
            var (sx, sy) = MapToStats(InputBuffer.MouseX, InputBuffer.MouseY);

            if (StatsWinPanel.HitStatInfo(sx, sy) is { } statKind)
            {
                key = statKind;
                var (title, body) = StatInfo.Get(statKind);

                return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
            }

            //the baked field-name words (STR/HP/Level/...) carry no real control - InfoHotspots over the art give them
            //the same hover help. Checked after the value labels (they don't overlap; this is just priority order).
            if (StatsWinPanel.HitInfoHotspot(sx, sy) is { } hotspot)
            {
                key = hotspot;

                return (x, y) => ItemTooltip.ShowInfo(hotspot.Title, hotspot.Body, x, y);
            }

            //extended stats section (same window/ScaleHost, same coordinate mapping)
            if (ExtStatsWinPanel is not null)
            {
                if (ExtStatsWinPanel.HitStatInfo(sx, sy) is { } extKind)
                {
                    key = extKind;
                    var (title, body) = StatInfo.Get(extKind);

                    return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
                }

                if (ExtStatsWinPanel.HitInfoHotspot(sx, sy) is { } extHotspot)
                {
                    key = extHotspot;

                    return (x, y) => ItemTooltip.ShowInfo(extHotspot.Title, extHotspot.Body, x, y);
                }
            }
        }

        //5. hp/mp orbs
        if (HoveredOrb is { } orb)
        {
            key = orb;
            var (cur, max) = orb.GetValues();

            if (orb.Kind == OrbKind.Hp)
                return (x, y) => ItemTooltip.ShowInfo("Health", $"{cur} / {max}\nYour life force. Reaching zero is fatal.", x, y);
            else
                return (x, y) => ItemTooltip.ShowInfo("Mana", $"{cur} / {max}\nMagical energy used to cast spells.", x, y);
        }

        //5.5 a status-effect icon in the buff bar. Polled (the bar stays non-hit-testable so it never eats world clicks):
        //map the cursor into the bar's local space through its magnifier and ask which active effect it is over. The bar's
        //icon ids are SPELL icons (spell001.epf), so a unique spell with the same sprite names the effect; else generic.
        if ((BuffBar is not null) && (BuffBarHost is { Visible: true } buffHost) && (buffHost.Scale > 0f))
        {
            var lx = (int)((InputBuffer.MouseX - buffHost.ScreenX) / buffHost.Scale);
            var ly = (int)((InputBuffer.MouseY - buffHost.ScreenY) / buffHost.Scale);

            if (BuffBar.HitTest(lx, ly) is { } effectIcon)
            {
                key = ("effect", effectIcon);

                if (FindEffectSpellName(effectIcon) is { } effName)
                {
                    var desc = LookupAbility(effName, true)?.Description ?? string.Empty;

                    return (x, y) => ItemTooltip.ShowInfo(effName, "Active effect", desc, x, y);
                }

                return (x, y) => ItemTooltip.ShowInfo("Status Effect", "Active effect",
                    "An effect is currently affecting you.", x, y);
            }
        }

        //6. generic fallback: any hovered control (menu entries, window buttons, ...) that opted in with a Tooltip string
        //or a live TooltipProvider (e.g. a menu entry showing the action's CURRENT, rebindable hotkey).
        //Walk up so a hovered child label inherits its interactive parent's tooltip when it has none of its own.
        for (var hovered = Game.Dispatcher.Hovered; hovered is not null; hovered = hovered.Parent)
        {
            //a window's close/pin/resize gadgets are pass-through, so the window itself is the hovered element - ask it
            //what chrome the cursor is over so those gadgets get hover help too (null = over content/titlebar, skip)
            var tip = hovered is DraggableWindow dw
                ? dw.ChromeTooltipAt(InputBuffer.MouseX, InputBuffer.MouseY)
                : null;

            tip ??= hovered.TooltipProvider?.Invoke() ?? hovered.Tooltip;

            if (tip is { Length: > 0 } tipText)
            {
                key = tipText; //key on the text so moving close->pin->resize swaps content instantly
                //convention: the FIRST line is the title (white), everything after the first newline is the body
                //(wrapped, paragraph-aware via blank lines). A single-line tooltip is just a title, as before.
                var (title, body) = SplitTip(tipText);

                return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
            }
        }

        return null;
    }

    //splits a generic tooltip string into its title (first line) and body (the rest). Single-line -> title only.
    private static (string Title, string Body) SplitTip(string text)
    {
        var nl = text.IndexOf('\n');

        return nl < 0 ? (text, string.Empty) : (text[..nl], text[(nl + 1)..].TrimStart('\n'));
    }

    //the buff bar's icon ids are spell icons (spell001.epf), so a UNIQUE spell the player knows with the same sprite
    //names the effect. Returns null if no match or an ambiguous one (two different spells share the icon).
    private static string? FindEffectSpellName(byte icon)
    {
        string? found = null;

        for (byte i = 1; i <= SpellBook.MAX_SLOTS; i++)
        {
            ref readonly var slot = ref WorldState.SpellBook.GetSlot(i);

            if (!slot.IsOccupied || (slot.Sprite != icon) || slot.AbilityName is not { Length: > 0 } name)
                continue;

            if ((found is not null) && !string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                return null; //ambiguous

            found = name;
        }

        return found;
    }

    //true only if the element and every ancestor is visible (a hidden window makes its slots not actually on-screen)
    private static bool IsElementShown(UIElement? element)
    {
        for (; element is not null; element = element.Parent)
            if (!element.Visible)
                return false;

        return true;
    }

    //every class's parsed SClass metadata, merged and built lazily for cross-class lookups (see LookupAbility)
    private List<AbilityMetadata>? AllClassAbilityMetadata;

    //looks up a skill/spell's parsed metadata by name. checks the player's own class set first (the overwhelmingly
    //common case), then every class's SClass file. NPCs teach cross-class abilities (e.g. Devlin's Dachaidh), whose
    //detail lives in another class's metafile and used to come up blank in the teach-list hover tooltip
    private AbilityMetadataEntry? LookupAbility(string name, bool isSpell)
    {
        if (PlayerAbilityMetadata is { } meta && Find(meta) is { } own)
            return own;

        AllClassAbilityMetadata ??= DataContext.MetaFiles.GetAll("SClass")
                                               .Select(AbilityMetadata.Parse)
                                               .ToList();

        foreach (var classMeta in AllClassAbilityMetadata)
            if (Find(classMeta) is { } entry)
                return entry;

        return null;

        AbilityMetadataEntry? Find(AbilityMetadata m)
        {
            var first = isSpell ? m.Spells : m.Skills;
            var second = isSpell ? m.Skills : m.Spells;

            return FindIn(first) ?? FindIn(second);
        }

        AbilityMetadataEntry? FindIn(IReadOnlyList<AbilityMetadataEntry> list)
        {
            foreach (var entry in list)
                if (entry.Name.EqualsI(name))
                    return entry;

            return null;
        }
    }

}