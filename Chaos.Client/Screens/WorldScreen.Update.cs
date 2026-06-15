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

            //force full black, switch to the lobby while black, then fade the fresh lobby in
            //so logout reads as world to black to lobby
            Game.SnapToBlack();
            Game.Screens.Switch(new LobbyLoginScreen(true));
            Game.FadeFromBlack();

            return;
        }

        //reconnect reached the world again, so reload a clean WorldScreen through the login to world handoff
        //the fresh screen fades itself in once its first map finishes loading
        if (PendingReconnectReload)
        {
            PendingReconnectReload = false;

            Game.SnapToBlack();
            Game.Screens.Switch(new WorldScreen());

            return;
        }

        //reconnect gave up, so drop to the lobby which keeps retrying on its own or shows the rejection message
        if (PendingReconnectGiveUp)
        {
            PendingReconnectGiveUp = false;

            Game.SnapToBlack();
            Game.Screens.Switch(new LobbyLoginScreen(false, ReconnectGiveUpMessage));
            Game.FadeFromBlack();

            return;
        }

        //while reconnecting the world is frozen, so drive the reconnect timers and overlay only
        //Escape bails out to the lobby instead of waiting the full timeout
        if (Reconnecting)
        {
            Reconnect?.Update((float)gameTime.ElapsedGameTime.TotalSeconds);

            if (InputBuffer.WasKeyPressed(Keys.Escape))
                Reconnect?.Cancel();

            UpdateReconnectOverlay();

            return;
        }

        //hold full black a beat after the first map loaded so the night light level snaps in unseen
        //then start the slow reveal, the world keeps simulating behind the black
        if (PendingIntroReveal)
        {
            IntroHoldRemaining -= (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (IntroHoldRemaining <= 0f)
            {
                PendingIntroReveal = false;
                //clear any tooltip and its hover state so a stale one doesn't pop into the reveal
                ResetTooltips();
                Game.FadeFromBlack(INTRO_FADE_SECONDS);
            }
        }

        var elapsedMs = (float)gameTime.ElapsedGameTime.TotalMilliseconds;

        //global tile animation tick at 100ms resolution to match the tile animation table format
        AnimationTick = (int)(gameTime.TotalGameTime.TotalMilliseconds / 100);
        MapRenderer.UpdatePaletteCycling(AnimationTick);

        //advance entity animations and active effects
        var smoothScroll = ClientSettings.ScrollLevel > 0;            //local player walk smoothing
        var smoothOthers = ClientSettings.SmoothCreatureMovement;     //enemies, NPCs, other players
        var player = WorldState.GetPlayerEntity();

        //animation advancement doesn't depend on sort order, so go through unordered to avoid a stale sort
        //SortDepth comes from position, and movement later in Update would invalidate any sort taken here
        foreach (var entity in WorldState.GetEntities())
        {
            //update water tile state before animation so the swimming idle tick advances
            UpdateEntityWaterState(entity);

            //smooth the player's walk if scroll smoothing is on, smooth every other entity if creature smoothing is on
            //the slide plays out over the same duration either way, smoothing only changes step vs pixel lerp
            var isSmooth = entity == player ? smoothScroll : smoothOthers;
            AnimationSystem.Advance(entity, elapsedMs, isSmooth);

            //local player footsteps, one cue on leaving the tile and one at the walk midpoint
            if (entity == player)
                UpdatePlayerFootsteps(entity);

            //update the creature's optional standing animation cycle
            if (entity.Type == ClientEntityType.Creature)
            {
                var animInfo = Game.CreatureRenderer.GetAnimInfo(entity.SpriteId);

                if (animInfo.HasValue)
                {
                    var info = animInfo.Value;
                    AnimationSystem.UpdateCreatureIdleCycle(entity, in info);
                }
            }

            //tick the emote overlay timer and cycle animated emote frames
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

        //group highlight auto-expires after the 1000ms flash
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

        //while HP is 0 nothing moves the player, no pathfinding, no queued step, no held keys
        //the spirit form walks at 1 HP
        if (IsPlayerDead)
        {
            Pathfinding.Clear();
            ResumeChaseTargetId = null;
            QueuedWalkDirection = null;
        }

        //resume a chase that was paused for a skill or spell once the action fully ends
        //the same enemy is re-acquired if it still exists, a manually chosen new target wins
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

        //run the queued walk once the player becomes idle after the walk animation
        var movementHandled = false;

        //modern controls drive walking from the held movement key each frame so continuous movement does not stutter
        //waiting on the OS key-repeat delay, MoveOrTurn steps when idle and queues mid-walk and clears any pathfinding
        if (player is not null && ClientSettings.ModernControls && GameplayInputAllowed()
            && Keybindings.HeldMovement(out var heldTurnOnly) is { } heldAction)
        {
            MoveOrTurn(MovementDirection(heldAction), heldTurnOnly);
            movementHandled = true;
        }

        //hold-to-walk re-paths toward the cursor while the move button is held, re-aimed only at rest
        //count down the post-warp grace so a move button still held from before a warp doesn't auto-path on the new map
        if (HeldWalkSuppressMs > 0f)
            HeldWalkSuppressMs = Math.Max(0f, HeldWalkSuppressMs - elapsedMs);

        //count down the post-warp keyboard-movement grace, same purpose for held arrow keys
        if (KeyMoveSuppressMs > 0f)
            KeyMoveSuppressMs = Math.Max(0f, KeyMoveSuppressMs - elapsedMs);

        if (player is not null && !movementHandled && !IsPlayerDead && GameplayInputAllowed()
            && (HeldWalkSuppressMs <= 0f)
            && !Pathfinding.TargetEntityId.HasValue && MapFile is not null && MapPreloaded
            && IsMoveButtonHeld() && !Game.Dispatcher.IsDragging
            && ((InputBuffer.CurrentModifiers & (KeyModifiers.Shift | KeyModifiers.Ctrl)) == 0)
            && WorldInputBounds.Contains(InputBuffer.MouseX, InputBuffer.MouseY)
            && !IsPointerOverUi(InputBuffer.MouseX, InputBuffer.MouseY)) //don't hold-walk while the cursor is over a window
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

        //held-attack fires while the Assail key is held and the player is standing still
        //moving pauses the attack and stopping resumes it automatically
        if (player is not null && player.IsAtRest && GameplayInputAllowed()
            && Keybindings.IsActionHeld(GameAction.Assail))
            TryAssail();

        //reset the spam-hint counters once the auto-attack target is cleared
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

        //run the next pathfinding step when the player becomes idle
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
                    //the instant any step lands adjacent to the chase target, stop and attack
                    //the planned goal may be a different adjacent tile, no point walking past a good attacking spot
                    Pathfinding.Path = null; //the exhausted-path branch below turns toward the target and assails
                } else
                {
                    //peek, don't pop, a step is only used once we actually walk it
                    //popping a blocked step would desync the path and drop the chase
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
                        //path head is no longer adjacent after a server correction, rebuild instead of giving up
                        RecoverPath(player);
                    } else if (IsClosedDoorAt(nextPoint.X, nextPoint.Y))
                    {
                        //a closed door always stops the walk for everyone, GM included
                        //the path may lead into a door on purpose, opening it is the player's act
                        Pathfinding.Path = null;
                    } else if (!IsGameMaster && !IsTilePassable(nextPoint.X, nextPoint.Y))
                    {
                        //something stepped into the next tile, re-route around it and keep the chase alive
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
                //path exhausted with an entity target, check if adjacent and assail, otherwise re-pathfind
                var target = WorldState.GetEntity(Pathfinding.TargetEntityId.Value);

                if (target is null)
                    Pathfinding.Clear();
                else if (Pathfinder.IsAdjacent(
                             player.TileX,
                             player.TileY,
                             target.TileX,
                             target.TileY))
                {
                    //adjacent, so turn toward the target and assail
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
                    //entity moved, re-pathfind on a 100ms timer
                    Pathfinding.RetargetTimer += elapsedMs;

                    if (Pathfinding.RetargetTimer >= 100f)
                    {
                        Pathfinding.RetargetTimer = 0;
                        PathfindToEntity(player, target);

                        //no path right now is not a reason to drop the chase, keep the target and try again
                        //a touch slower so a walled-off target doesn't burn the pathfinder every 100ms
                        if (Pathfinding.Path is null)
                            Pathfinding.RetargetTimer = -400f;
                    }
                }
            }
        }

        //tick the re-pathfind timer while walking toward an entity target
        if (Pathfinding.TargetEntityId.HasValue && player is not null && (player.AnimState == EntityAnimState.Walking))
            Pathfinding.RetargetTimer += elapsedMs;

        //match the camera viewport to the window-filling world render target before the lighting and darkness compute
        //so the light positions and the darkness texture use the same expanded space the world is drawn in
        Camera.Resize(ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);

        //camera follows the player's visual position, either rigidly or as a smooth pan when the alternative camera is on
        //and applies any active damage shake
        var dtSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
        UpdateCameraFollow(dtSeconds);
        UpdateCameraEffects(dtSeconds);
        UpdateDeathFx(dtSeconds);

        //when the dialog speaker is spotlit the world pass draws the dim and the bright speaker
        //tell the native dimmer to skip its own base and vignette so the view isn't darkened twice
        if (DialogDim is not null)
            DialogDim.SuppressBaseDim = SpeakerSpotlightActive;

        //viewport-layer updates must always run no matter which UI panel has input focus
        //so the world keeps animating behind open windows
        if (MapFile is not null)
            Overlays.Update(
                Camera,
                MapFile.Height,
                Game.CreatureRenderer,
                gameTime);

        //gather light sources for this frame, gated on darkness being active so town maps light at night too
        //LightAnimTime drives the flame flicker and is reused by the resize-time re-gather in Wiring
        LightAnimTime = (float)gameTime.TotalGameTime.TotalSeconds;
        Lighting.Gather(MapFile, Camera, DarknessRenderer.IsActive, DarknessRenderer.DuskGlow, LightAnimTime, DarknessRenderer.IsAlwaysDark);

        //a carried lantern behaves differently by map type
        //cycle map gets a gentle ambient lift with the vignette kept, dark map gets a strong lift with no tint or vignette
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

        //ease the darkness toward the latest light level, runs even when inactive so a fade-in still animates
        //the light buffer itself is rebuilt each frame in Draw, so there is no carve to update here
        DarknessRenderer.AdvanceFade((float)gameTime.ElapsedGameTime.TotalSeconds);

        WeatherRenderer.Update(gameTime, WorldViewport);

        //resolve which tooltip the cursor is over and show it after the configurable delay
        UpdateTooltips((float)gameTime.ElapsedGameTime.TotalSeconds);

        //track the hovered entity, suppressed while a dialog is open or the cursor is over any HUD or window control
        //cached so DrawTileCursors can suppress the ground marker on the same condition
        PointerOverUi = NpcSession.Visible || IsPointerOverUi(InputBuffer.MouseX, InputBuffer.MouseY);

        //tile first, whatever is on the ground-cursor tile is the hover target, only an empty tile falls back to the
        //sprite under the cursor, so the name tag and hand cursor track the tile you point at
        var hoverEntity = PointerOverUi ? null : ResolveCursorTarget(InputBuffer.MouseX, InputBuffer.MouseY);

        //never treat the player's own sprite as a hover target, no hand cursor and no highlight over yourself
        var newHoveredId = hoverEntity?.Type is ClientEntityType.Aisling or ClientEntityType.Creature
                           && !hoverEntity.IsHidden
                           && (hoverEntity.Id != Game.Connection.AislingId)
            ? hoverEntity.Id
            : (uint?)null;

        //the hand cursor shows over anything interactable, an enemy, NPC, player, or a ground item
        var overInteractable = newHoveredId.HasValue || (hoverEntity?.Type == ClientEntityType.GroundItem);

        Game.UseHandCursor = overInteractable;

        //clickable links in an open board post or mail message show the hand cursor while hovering an https link
        //the click itself is handled in the left-press event loop below
        if ((ArticleRead.Visible && ArticleRead.TryGetLinkAt(InputBuffer.MouseX, InputBuffer.MouseY, out _))
            || (MailRead.Visible && MailRead.TryGetLinkAt(InputBuffer.MouseX, InputBuffer.MouseY, out _)))
            Game.UseHandCursor = true;

        //a clickable player name in the chat log shows the hand cursor while hovering it
        //NameAt self-gates on a visible line so no hit-test check is needed
        if (ChatWin?.NameAt(InputBuffer.MouseX, InputBuffer.MouseY) is not null)
            Game.UseHandCursor = true;

        //tick the casting timer, chant lines are sent on a 1-second interval
        CastingSystem.Update(elapsedMs, Game.Connection);

        //spacebar assail is handled in OnRootKeyDown, the dispatcher delivers both the initial press
        //and OS key-repeat keydowns through the event pipeline, so dialogs that eat it naturally block it

        var skipDispatch = false;

        foreach (var evt in InputBuffer.Events)
        {
            if (evt is not { Kind: BufferedInputKind.MouseButton, Button: MouseButton.Left, IsPress: true })
                continue;

            var mx = evt.X;
            var my = evt.Y;

            //a click on an https link in an open board post or mail message opens it
            //and eats the click so it doesn't also start a text selection or walk the world behind the window
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
                //click a player's name in the chat log to start a whisper to them, eat the click so it doesn't also walk
                ActiveChatInput.Focus($"-> {whisperName}: ", TextColors.Whisper);
                skipDispatch = true;
            } else if (AislingContext.Visible && !AislingContext.ContainsPoint(mx, my))
            {
                AislingContext.Hide();
                skipDispatch = true;
            } else if (AbilityMetadataDetails.Visible && (AbilityDetailsHost is null || !AbilityDetailsHost.ContainsPoint(mx, my)))
            {
                //the popup is magnified by a ScaleHost so hit-test the host's on-screen rect
                //hiding the inner fades the host out via the visibility sync
                AbilityMetadataDetails.Hide();
                skipDispatch = true;
            } else if (EventMetadataDetails.Visible && (EventDetailsHost is null || !EventDetailsHost.ContainsPoint(mx, my)))
            {
                EventMetadataDetails.Hide();
                skipDispatch = true;
            } else if (SocialStatusPicker.Visible && (SocialStatusHost is null || !SocialStatusHost.ContainsPoint(mx, my)))
            {
                //magnified by SocialStatusHost so hit-test the host rect, a click anywhere outside closes the picker
                //this scan runs before dispatch so the click that opens it can't close it on the same frame
                SocialStatusPicker.Hide();
                skipDispatch = true;
            }

            break;
        }

        //mouse 4 casts on the closest friendly, mouse 5 on the closest enemy, swappable via the flip-buttons option
        //press only, acts only while a spell is readied, forces the right side so you can't mis-fire on the wrong type
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

        //resolve once per frame the entity a readied spell would hit, the closest target to the ground cursor
        //so the blue highlight all references the same target
        CastTargetId = CastingSystem.IsTargeting ? FindClosestTarget()?.Id : null;

        //keep the native-resolution UI sized to the window, which can be resized to any size
        Root!.Width = ChaosGame.UiWidth;
        Root.Height = ChaosGame.UiHeight;

        AnchorHotbars(elapsedMs);
        AnchorNpcDialog();
        AnchorPopups();
        UpdateMinimap();

        if (!skipDispatch)
            Game.Dispatcher.ProcessInput(Root!, gameTime);

        //the input just processed may have switched screens and unloaded us
        //bail before the frame tail touches a torn-down WorldState
        if (IsUnloaded)
            return;

        //all movement has been processed by now, sort once and publish the frame state
        PopulateFrameState(newHoveredId);

        Root!.Update(gameTime);

        UpdateFeedbackSounds();
    }

    //the chase target stashed while a skill or spell runs, resumed in Update once the action ends
    private uint? ResumeChaseTargetId;

    //tracks whether the player was walking on the previous frame, for walk-start detection
    private bool WasWalkingFootstep;

    //tracks whether the midpoint step has fired for the current walk so we fire it exactly once per tile
    private bool FiredMidStep;

    //plays a footstep when the player leaves a tile and another at the walk midpoint
    //gives two steps per tile without relying on animation frame timing
    private void UpdatePlayerFootsteps(WorldEntity player)
    {
        //no footsteps while dead or swimming
        if (player.IsOnSwimmingTile || player.IsDead)
        {
            WasWalkingFootstep = false;

            return;
        }

        var isWalking = player.AnimState == EntityAnimState.Walking;

        if (isWalking && !WasWalkingFootstep)
        {
            //just left the tile, push-off step, and reset the mid-step tracker so the midpoint step fires
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

    //plays a short sound when the inventory or gold changes, measured per frame against a baseline so a swap stays silent
    //both are armed a short grace after world entry so the initial fill never triggers them
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

        //arm after updating the baselines, a grace past world entry, so the initial fill is never counted
        if (!ItemSoundArmed && HasEnteredWorld && ((Environment.TickCount - WorldEntryTick) >= ItemSoundArmDelayMs))
            ItemSoundArmed = true;
    }

    //sets each hotbar's slot labels from the player's actual bindings in short form
    //called at setup and on every keybindings change so a rebind shows up right away
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

    //applies the live hotbar scale and re-centers the bars, inventory top-center, skills and spells bottom-center
    //runs every frame so it follows window resizes and the scale slider
    private const float HOTBAR_TARGET_ALPHA = 0.35f;
    private const float HOTBAR_FADE_SPEED = 4f; //per second

    //drives the corner minimap, bakes the town-map inked texture for the current map and points the minimap at it
    //hidden when turned off or there is no map
    private void UpdateMinimap()
    {
        //wait for the map to be fully preloaded before generating the overview
        //baking from not-yet-created tile textures renders blank on some drivers and gets cached for this map
        if (!ClientSettings.ShowMinimap || (MapFile is null) || !MapPreloaded)
        {
            Minimap.Visible = false;
            MinimapWarmupFrames = MINIMAP_WARMUP_FRAMES;

            return;
        }

        //even once preloaded, give the new tile textures a few frames to settle before baking
        //a texture rendered the same frame it is created can come out undefined
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

        //the current walk path drawn as a dotted trail, plus the move target for the flag marker
        Point? target = null;
        MinimapPathBuffer.Clear();

        if ((Pathfinding.TargetEntityId is { } tid) && (WorldState.GetEntity(tid) is { } te))
            target = new Point(te.TileX, te.TileY);

        if (Pathfinding.Path is { Count: > 0 } path)
        {
            foreach (var pt in path) //top to bottom of the stack, the bottom is the destination tile
                MinimapPathBuffer.Add(new Point(pt.X, pt.Y));

            target ??= MinimapPathBuffer[^1];
        }

        //show roughly the server's synced range around the player plus a margin, as inked pixels
        var inkedRadius = MINIMAP_TILE_RADIUS * TownMapControl.InkedPixelsPerTile;
        Minimap.SetSource(TownMapControl, centerWorld, inkedRadius, WarpData.GetClusters(CurrentMapId), MinimapPathBuffer, target);
        Minimap.Visible = true;
    }

    //reused each frame to hand the current walk path to the minimap without allocating
    private readonly List<Point> MinimapPathBuffer = [];

    //radius in tiles the minimap covers, about the synced range plus half
    private const float MINIMAP_TILE_RADIUS = 16f;

    //frames to wait after a map is preloaded before baking, so new tile textures have settled
    private const int MINIMAP_WARMUP_FRAMES = 3;
    private int MinimapWarmupFrames = MINIMAP_WARMUP_FRAMES;

    //bounds-safe collision lookup for the town map and minimap inked build
    private bool MapWallAt(int x, int y)
        => (MapFile is not null) && (x >= 0) && (y >= 0) && (x < MapFile.Width) && (y < MapFile.Height) && IsTileWallBlocked(x, y);

    private void AnchorHotbars(float elapsedMs)
    {
        if ((InvBar is null) || (SkillBar is null) || (SpellBar is null))
            return;

        //dim the hotbars while waiting on a spell target so the cursor overlay reads clearly
        var alphaTarget = CastingSystem.IsTargeting ? HOTBAR_TARGET_ALPHA : 1f;
        var step = HOTBAR_FADE_SPEED * (elapsedMs / 1000f);
        TargetingHotbarAlpha = alphaTarget > TargetingHotbarAlpha
            ? MathF.Min(alphaTarget, TargetingHotbarAlpha + step)
            : MathF.Max(alphaTarget, TargetingHotbarAlpha - step);
        InvBar.ExternalAlpha = TargetingHotbarAlpha;
        SkillBar.ExternalAlpha = TargetingHotbarAlpha;
        SpellBar.ExternalAlpha = TargetingHotbarAlpha;

        var scale = ClientSettings.HotbarScale;
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

        //hp and mp orbs, scaled by the hotbar scale, bottom-aligned with the spell bar and flanking it
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

            //one-time default placement, size the chat to fill the lower-left gap up to the HP orb so it sits beside it
            //after this the player owns its size and position, we never move or resize it again
            if (!ChatDefaultSized && (ChatWin is not null) && (orbW > 0))
            {
                const int CHAT_MARGIN = 8;
                const int ORB_CLEAR = 6;

                var targetW = Math.Max(180, HpOrb.X - ORB_CLEAR - CHAT_MARGIN);
                ChatWin.X = CHAT_MARGIN;
                ChatWin.Resize(targetW, ChatWin.Height);
                ChatWin.Y = h - ChatWin.Height - CHAT_MARGIN;
                ChatDefaultSized = true;
            }
        }

        //status-effect bar at the right edge, vertically centered, magnified 2x the hotbar scale so the icons read
        //at roughly a slot's size, only visible while at least one effect is active
        if (BuffBarHost is not null)
        {
            BuffBarHost.Scale = scale * 2f;
            BuffBarHost.X = w - BuffBarHost.Width - margin;
            BuffBarHost.Y = Math.Max(margin, (h - BuffBarHost.Height) / 2);
        }
    }

    //scales the NPC or sign dialog each frame while open and pins it to the bottom, centered horizontally
    //best-fit is the same letterbox factor the world uses so it keeps its classic 640x480 proportion
    private void AnchorNpcDialog()
    {
        if ((NpcSessionHost is null) || !NpcSessionHost.Visible)
            return;

        var fit = Math.Min(
            ChaosGame.UiWidth / (float)ChaosGame.VIRTUAL_WIDTH,
            ChaosGame.UiHeight / (float)ChaosGame.VIRTUAL_HEIGHT);

        NpcSessionHost.Scale = fit; //ScaleHost clamps to >= 1

        //centered horizontally but pinned to the bottom, the dialog bar and portrait sit at the bottom of the canvas
        //ContentYOffset is the first-appearance slide that eases it up into place from just below
        NpcSessionHost.X = (ChaosGame.UiWidth - NpcSessionHost.Width) / 2;
        NpcSessionHost.Y = ChaosGame.UiHeight - NpcSessionHost.Height + (int)NpcSession.ContentYOffset;
    }

    //keeps the gold and item amount prompts centered and at the current window scale each frame while open
    //so they follow a window resize or a slider change
    private void AnchorPopups()
    {
        AnchorScaledPopup(GoldDropHost);
        AnchorScaledPopup(ItemAmountHost);
        AnchorTextPopup();
    }

    //the sign or board popup is centered, but while the host fades open it slides up from half its height below center
    //driven off OpenFraction so the slide and fade always agree
    private void AnchorTextPopup()
    {
        if ((TextPopupHost is null) || !TextPopupHost.Visible)
            return;

        TextPopupHost.Scale = ClientSettings.WindowScale;

        var f = Math.Clamp(TextPopupHost.OpenFraction, 0f, 1f);
        var ease = 1f - ((1f - f) * (1f - f)); //ease-out, fast start and soft landing

        TextPopupHost.X = (ChaosGame.UiWidth - TextPopupHost.Width) / 2;
        TextPopupHost.Y = ((ChaosGame.UiHeight - TextPopupHost.Height) / 2) + (int)(TextPopupHost.Height * 0.5f * (1f - ease));
    }

    private static void AnchorScaledPopup(ScaleHost? host)
    {
        if ((host is null) || !host.Visible)
            return;

        host.Scale = ClientSettings.WindowScale;
        host.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
    }

    /// <summary>
    ///     Publishes per-frame state like sort order and hover to <see cref="WorldState.CurrentFrame" /> after movement is done
    /// </summary>
    private void PopulateFrameState(uint? newHoveredId)
    {
        //capture prev before Reset wipes it so the dirty-check still has last frame's value
        var prevHoveredId = WorldState.CurrentFrame.HoveredEntityId;
        WorldState.CurrentFrame.Reset();

        if (newHoveredId != prevHoveredId)
        {
            Game.AislingRenderer.ClearTintedCache();
            Game.CreatureRenderer.ClearTintCaches();
        }

        //GetSortedEntities is self-caching via a dirty flag, so this call is free when the sort is still valid
        WorldState.CurrentFrame.SortedEntities = WorldState.GetSortedEntities();
        WorldState.CurrentFrame.HoveredEntityId = newHoveredId;
        WorldState.CurrentFrame.ShowTintHighlight = CastingSystem.IsTargeting || Game.Dispatcher.IsDragging;
        WorldState.CurrentFrame.UseDragCursor = Game.Dispatcher.IsDragging;

        //group-box overlay is drawn in 640x480 world space, so hit-test it with the converted mouse coords
        WorldState.CurrentFrame.HoveredGroupBoxId = Overlays
                                             .GetGroupBoxAtScreen(
                                                 ToWorldX(InputBuffer.MouseX),
                                                 ToWorldY(InputBuffer.MouseY))
                                             ?.EntityId;

        //the world fills the whole window now, only set the hovered tile when the native mouse is over it
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

            //guard against a malformed projectile spec that would never drain ElapsedMs or reach the target
            //and freeze the game thread inside this loop
            if (proj.StepDelayMs <= 0f || proj.Step <= 0)
            {
                proj.IsComplete = true;
                WorldState.ActiveProjectiles.RemoveAt(i);

                continue;
            }

            //step cap guards the case where the projectile can't catch a moving target within one frame's elapsed time
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

            //perpendicular to the heading
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
    ///     Returns the topmost visible modal panel among Root's children, or null
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

    /// <summary>
    ///     Resolves the single tooltip the cursor is over and shows it through the shared <see cref="ItemTooltip" />
    /// </summary>
    //hides the shared tooltip and clears the hover state that would re-show it next frame
    //called when the world fades in after connecting so nothing lingers into the reveal
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

        //don't let a tooltip pop for whatever the still cursor rests on during the reveal, wait for a move
        TooltipSuppressedUntilMove = true;
        LastTooltipMouseX = InputBuffer.MouseX;
        LastTooltipMouseY = InputBuffer.MouseY;
    }

    private void UpdateTooltips(float dtSeconds)
    {
        //after a connect or reveal, stay suppressed until the cursor moves, then resume normal hover behaviour
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

        //left click dismisses and locks out until the cursor leaves this target and re-enters
        if (InputBuffer.IsLeftButtonHeld && ItemTooltip.Visible)
        {
            ItemTooltip.Hide();
            TooltipClickSuppressedKey = key;
            TooltipDelayTimer = 0f;

            return;
        }

        //clear the per-target click suppression once the cursor moves to a different or no target
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

        //still hovering the element that was just clicked, stay hidden
        if (TooltipClickSuppressedKey is not null)
            return;

        var changed = !Equals(key, TooltipKey);
        TooltipKey = key;

        if (ItemTooltip.Visible)
        {
            if (changed || TooltipDynamic)
                show!(InputBuffer.MouseX, InputBuffer.MouseY); //switched target or live content, rebuild
            else
                ItemTooltip.UpdatePosition(InputBuffer.MouseX, InputBuffer.MouseY); //same target, just follow the cursor
        } else
        {
            TooltipDelayTimer += dtSeconds;

            if (TooltipDelayTimer >= ClientSettings.TooltipDelaySeconds)
                show!(InputBuffer.MouseX, InputBuffer.MouseY);
        }
    }

    //returns the action that shows the hovered tooltip plus a key identifying its target, null means nothing to show
    private Action<int, int>? ResolveTooltip(out object? key)
    {
        key = null;
        TooltipDynamic = false; //only a live cooldown sets this true below, forcing a per-frame rebuild

        //an NPC shop or dialog item, shown first since the dialog sits on top
        if (NpcSession.Visible && (HoveredNpcItemName is { } npcName))
        {
            key = npcName;

            return (x, y) => ItemTooltip.Show(npcName, 0, 0, x, y);
        }

        //a skill or spell in the NPC teaching list, polled like the book and stats tooltips
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

        //blocking modals suppress the remaining hover tooltips, the other-player profile is left out on purpose
        //so your hotbar and equipment tooltips keep working while it is open
        if (NpcSession.Visible || (FindVisibleModal() is not null))
            return null;

        //slot tooltips are suppressed while targeting a spell, the hotbars dim to signal targeting mode
        if (CastingSystem.IsTargeting)
            return null;

        //an inventory slot, kept in sync by the panel's hover events
        if (HoveredInventorySlot is { } slot)
        {
            key = slot;
            var name = slot.SlotName ?? string.Empty;
            var cur = slot.CurrentDurability;
            var max = slot.MaxDurability;

            return (x, y) => ItemTooltip.Show(name, cur, max, x, y);
        }

        //a skill or spell slot, show the ability's detail
        //IsElementShown guards the case where a book window closes while the cursor is still over a slot
        if (HoveredAbilitySlot is { AbilityName: { Length: > 0 } abilityName } abilitySlot && IsElementShown(abilitySlot))
        {
            var isSpell = abilitySlot is SpellSlot;

            //for a learned spell the slot's CastLines is authoritative, it can differ from the metafile and is never stale
            var liveLines = (abilitySlot as SpellSlot)?.CastLines;

            //live remaining cooldown on this slot, shown in the tooltip when active
            var cooldown = isSpell
                ? WorldState.SpellBook.GetCooldownRemainingSeconds(abilitySlot.Slot)
                : WorldState.SkillBook.GetCooldownRemainingSeconds(abilitySlot.Slot);

            //rebuild every frame while the countdown runs so it ticks, and one more frame when it reaches 0
            //so the cooldown line is removed cleanly instead of freezing near zero
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

            //no metadata on file for this ability, still show its name and whatever the slot itself knows
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

        //the equipment book, an equipped item icon or else a stat value label
        if (StatusBook.Visible)
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

            //the baked field-name words on the equipment page carry no real control
            //InfoHotspots over the art give them hover help, same as the Stats window
            if (StatusBook.HitEquipInfoHotspot(bx, by) is { } eqHotspot)
            {
                key = eqHotspot;

                return (x, y) => ItemTooltip.ShowInfo(eqHotspot.Title, eqHotspot.Body, x, y);
            }
        }

        //the Stats window, a stat value label
        if ((StatsWin?.Visible == true) && (StatsWinPanel is not null))
        {
            var (sx, sy) = MapToStats(InputBuffer.MouseX, InputBuffer.MouseY);

            if (StatsWinPanel.HitStatInfo(sx, sy) is { } statKind)
            {
                key = statKind;
                var (title, body) = StatInfo.Get(statKind);

                return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
            }

            //the baked field-name words carry no real control, InfoHotspots over the art give them the same hover help
            //checked after the value labels, this is just priority order
            if (StatsWinPanel.HitInfoHotspot(sx, sy) is { } hotspot)
            {
                key = hotspot;

                return (x, y) => ItemTooltip.ShowInfo(hotspot.Title, hotspot.Body, x, y);
            }
        }

        //hp and mp orbs
        if (HoveredOrb is { } orb)
        {
            key = orb;
            var (cur, max) = orb.GetValues();

            if (orb.Kind == OrbKind.Hp)
                return (x, y) => ItemTooltip.ShowInfo("Health", $"{cur} / {max}\nYour life force. Reaching zero is fatal.", x, y);
            else
                return (x, y) => ItemTooltip.ShowInfo("Mana", $"{cur} / {max}\nMagical energy used to cast spells.", x, y);
        }

        //a status-effect icon in the buff bar, polled since the bar stays non-hit-testable so it never eats world clicks
        //the bar's icon ids are spell icons, so a unique spell with the same sprite names the effect, else generic
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

        //generic fallback, any hovered control that opted in with a Tooltip string or a live TooltipProvider
        //walk up so a hovered child label inherits its parent's tooltip when it has none of its own
        for (var hovered = Game.Dispatcher.Hovered; hovered is not null; hovered = hovered.Parent)
        {
            //a window's close, pin, and resize gadgets are pass-through so the window itself is the hovered element
            //ask it what chrome the cursor is over, null means over content or titlebar so skip
            var tip = hovered is DraggableWindow dw
                ? dw.ChromeTooltipAt(InputBuffer.MouseX, InputBuffer.MouseY)
                : null;

            tip ??= hovered.TooltipProvider?.Invoke() ?? hovered.Tooltip;

            if (tip is { Length: > 0 } tipText)
            {
                key = tipText; //key on the text so moving between gadgets swaps content instantly
                //the first line is the title, everything after the first newline is the body
                //a single-line tooltip is just a title
                var (title, body) = SplitTip(tipText);

                return (x, y) => ItemTooltip.ShowInfo(title, body, x, y);
            }
        }

        return null;
    }

    //splits a generic tooltip string into its title from the first line and the body from the rest
    private static (string Title, string Body) SplitTip(string text)
    {
        var nl = text.IndexOf('\n');

        return nl < 0 ? (text, string.Empty) : (text[..nl], text[(nl + 1)..].TrimStart('\n'));
    }

    //the buff bar's icon ids are spell icons, so a unique known spell with the same sprite names the effect
    //returns null if there is no match or an ambiguous one where two spells share the icon
    private static string? FindEffectSpellName(byte icon)
    {
        string? found = null;

        for (byte i = 1; i <= SpellBook.MAX_SLOTS; i++)
        {
            ref readonly var slot = ref WorldState.SpellBook.GetSlot(i);

            if (!slot.IsOccupied || (slot.Sprite != icon) || slot.AbilityName is not { Length: > 0 } name)
                continue;

            if ((found is not null) && !string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                return null; //ambiguous match

            found = name;
        }

        return found;
    }

    //true only if the element and every ancestor is visible, a hidden window keeps its slots off-screen
    private static bool IsElementShown(UIElement? element)
    {
        for (; element is not null; element = element.Parent)
            if (!element.Visible)
                return false;

        return true;
    }

    //every class's parsed SClass metadata, merged, built lazily for cross-class lookups
    private List<AbilityMetadata>? AllClassAbilityMetadata;

    //looks up a skill or spell's parsed metadata by name, the player's own class set first then every class
    //NPCs teach cross-class abilities whose detail lives in another class's metafile
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