#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Hud;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Data;
using Chaos.Client.Data.Models;
using Chaos.Client.Definitions;
using Chaos.Client.Extensions;
using Chaos.Client.Models;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Geometry.Abstractions;
using Chaos.Geometry.Abstractions.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Pathfinder = Chaos.Client.Systems.Pathfinder;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    #region UI Event Handlers
    //--- inventory ---

    private void HandleInventorySlotClicked(byte slot)
    {
        Game.Connection.UseItem(slot);
        HoveredInventorySlot = null;
        ItemTooltip.Hide();
    }

    //hotbar SINGLE-click: an EMPTY slot TOGGLES its window (Inventory/Skills/Spells); an occupied slot does nothing here
    //(double-click activates it, wired to OnSlotClicked). Wired to OnSlotSingleClicked for the three hotbars.
    private void HandleInvBarSlotClicked(byte slot)
    {
        if (string.IsNullOrEmpty(WorldState.Inventory.GetSlot(slot).Name))
            InventoryWindow?.Toggle();
    }

    private void HandleSkillBarSlotClicked(byte slot)
    {
        if (string.IsNullOrEmpty(SkillBarPanel?.GetSkillSlot(slot)?.AbilityName))
            SkillWin?.Toggle();
    }

    private void HandleSpellBarSlotClicked(byte slot)
    {
        if (string.IsNullOrEmpty(SpellBarPanel?.GetSpellSlot(slot)?.AbilityName))
            SpellWin?.Toggle();
    }

    //clicking the HP or MP orb toggles the Stats window
    private void ToggleStatsWindow() => StatsWin?.Toggle();

    //Cast the readied spell on a specific entity (no-op if not casting / entity gone).
    private void CastReadiedSpellOn(WorldEntity? target)
    {
        if (!CastingSystem.IsTargeting || (target is null))
            return;

        CastingSystem.SelectTarget(target.Id, target.TileX, target.TileY, Game.Connection);
    }

    //Cast-on-closest-friendly side button (Mouse 4 by default): force the readied spell onto the closest FRIENDLY
    //(yourself or a group member), so a heal/buff never lands on an enemy even if one is nearer the cursor.
    private void TargetFriendlyCast() => CastReadiedSpellOn(FindClosestFriendly());

    //Cast-on-closest-enemy side button (Mouse 5 by default): force the readied spell onto the closest ENEMY, so an
    //offensive spell never lands on you/a group member even if one is nearer the cursor.
    private void TargetEnemyCast() => CastReadiedSpellOn(FindClosestEnemy());

    //nearest hostile creature (CreatureType.Normal) to the GROUND CURSOR; null if none visible.
    private WorldEntity? FindClosestEnemy()
        => FindClosestEntityToCursor(e => (e.Type == ClientEntityType.Creature) && (e.CreatureType == CreatureType.Normal));

    //nearest visible group member or yourself to the ground cursor
    private WorldEntity? FindClosestFriendly()
        => FindClosestEntityToCursor(e => (e.Id == Game.Connection.AislingId)
                                          || ((e.Type == ClientEntityType.Aisling) && WorldState.Group.Members.Contains(e.Name)));

    //nearest valid target of any kind (including yourself) to the ground cursor; this is the default the cast/highlight
    //snaps to, so pointing near yourself snaps to you and pointing near an enemy snaps to it
    private WorldEntity? FindClosestTarget()
        => FindClosestEntityToCursor(e => e.Type is ClientEntityType.Creature or ClientEntityType.Aisling);

    //shared nearest-by-tile (Manhattan) scan over visible, alive entities matching a predicate, measured from the tile
    //under the cursor rather than the player so the snapped target follows where you're pointing
    private WorldEntity? FindClosestEntityToCursor(Func<WorldEntity, bool> match)
    {
        if (MapFile is null)
            return null;

        var (cx, cy) = ScreenToTile(InputBuffer.MouseX, InputBuffer.MouseY);

        WorldEntity? best = null;
        var bestDist = int.MaxValue;

        foreach (var e in WorldState.GetEntities())
        {
            if (e.IsHidden || e.IsDead || !match(e))
                continue;

            var d = Math.Abs(e.TileX - cx) + Math.Abs(e.TileY - cy);

            if (d < bestDist)
            {
                bestDist = d;
                best = e;
            }
        }

        return best;
    }

    private void HandleInventoryHoverEnter(PanelSlot slot)
        //just record the hovered slot; UpdateTooltips resolves it (with the configurable delay) each frame
        => HoveredInventorySlot = slot;

    private void HandleInventoryHoverExit() => HoveredInventorySlot = null;

    //skill/spell hotbar + K/P book hover: record the ability slot; UpdateTooltips shows its detail tooltip
    private void HandleAbilityHoverEnter(PanelSlot slot) => HoveredAbilitySlot = slot as AbilitySlotControl;

    private void HandleAbilityHoverExit() => HoveredAbilitySlot = null;

    //true when the cursor is over any interactive HUD/menu/window control, meaning anything the input dispatcher would
    //hand a click to other than the world root itself (the root is a non-pass-through panel that catches world clicks,
    //so it is always the hit when the cursor is over empty world). Used to suppress the ground marker, hand cursor,
    //and entity-name hover while the pointer is on a piece of UI, the same way a modal dialog does.
    private bool IsPointerOverUi(int screenX, int screenY)
    {
        if (Root is null)
            return false;

        var hit = InputDispatcher.HitTest(Root, screenX, screenY);

        return (hit is not null) && (hit != Root);
    }

    //maps a raw on-screen point into the exchange window's un-scaled (640x480-layout) coordinate space, so the few
    //WorldScreen-level raw-coord hit tests against the bare control still line up now that ExchangeHost magnifies it.
    //Identity when the host is absent. Mirrors how InputDispatcher.HitTest descends into a ScaleHost.
    private (int X, int Y) MapToExchange(int screenX, int screenY)
    {
        if (ExchangeHost is null)
            return (screenX, screenY);

        var scale = ExchangeHost.Scale;

        return (ExchangeHost.ScreenX + (int)((screenX - ExchangeHost.ScreenX) / scale),
                ExchangeHost.ScreenY + (int)((screenY - ExchangeHost.ScreenY) / scale));
    }

    private bool IsOverVisiblePopup(int screenX, int screenY)
    {
        if (Root is null)
            return false;

        foreach (var child in Root.Children)
        {
            if (child is not UIPanel { Visible: true, IsPassThrough: false } panel)
                continue;

            if ((panel == SmallHud) || (panel == LargeHud))
                continue;

            if (panel.ContainsPoint(screenX, screenY))
                return true;
        }

        return false;
    }

    private void HandleInventoryDropInViewport(byte slot, int mouseX, int mouseY)
    {
        //dropped onto the exchange window: add item to exchange. Test the HOST's (scaled) on-screen bounds, since the
        //exchange is now magnified + centered by ExchangeHost; the bare control's rect is the un-scaled 640x480 layout.
        if ((slot != 0) && Exchange.Visible && (ExchangeHost?.ContainsPoint(mouseX, mouseY) ?? false))
        {
            Game.Connection.SendExchangeInteraction(ExchangeRequestType.AddItem, Exchange.OtherUserId, slot);

            return;
        }

        //dropped anywhere on the equipment book: equip the item (server picks the correct slot by item type)
        if (slot != 0 && StatusBook.Visible && (StatusBookHost?.ContainsPoint(mouseX, mouseY) ?? false))
        {
            Game.Connection.UseItem(slot);

            return;
        }

        //block drops that land on any visible popup window
        if (IsOverVisiblePopup(mouseX, mouseY))
            return;

        var viewport = WorldInputBounds;

        //only drop if released within the world viewport
        if ((mouseX < viewport.X)
            || (mouseX >= (viewport.X + viewport.Width))
            || (mouseY < viewport.Y)
            || (mouseY >= (viewport.Y + viewport.Height)))
            return;

        if (MapFile is null)
            return;

        //check if dropped on an entity (give item/gold to npc/player); skip self (drop on ground instead).
        //tile-first: the occupant of the tile you drop on wins, else the sprite under the cursor
        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);
        var entity = ResolveCursorTarget(mouseX, mouseY);

        var droppedOnEntity = entity?.Type is ClientEntityType.Aisling or ClientEntityType.Creature
                              && (entity.Id != Game.Connection.AislingId);

        //gold bag (slot 0): show the gold amount popup (GoldDropHost owns its scale + centering, anchored each frame)
        if (slot == 0)
        {
            GoldDrop.ShowForTarget(droppedOnEntity ? entity!.Id : null, tileX, tileY);
            WorldHud.SetDescription($"Gold( {WorldState.Inventory.Gold} )");

            return;
        }

        if (droppedOnEntity)
        {
            var itemSlot = WorldState.Inventory.GetSlot(slot);
            Game.Connection.DropItemOnCreature(slot, entity!.Id, itemSlot.Stackable ? (byte)0 : (byte)1);

            return;
        }

        //stackable items with more than one: prompt for count before dropping
        var invSlot = WorldState.Inventory.GetSlot(slot);

        if (invSlot.Stackable && (invSlot.Count > 1))
        {
            var capturedSlot = slot;
            var capturedX = tileX;
            var capturedY = tileY;

            ActiveChatInput.ShowPrompt(
                $"Number of items to drop [ 0 - {(int)invSlot.Count} ]: ",
                12,
                text =>
                {
                    if (int.TryParse(text, out var count) && (count > 0))
                        Game.Connection.DropItem(capturedSlot, capturedX, capturedY, count);
                });

            return;
        }

        Game.Connection.DropItem(slot, tileX, tileY);
    }

    //--- skills / spells ---

    //using a skill or spell pauses the auto-attack chase so the action can complete; the Update loop re-acquires
    //the same enemy once the action ends (no chant/targeting, body animation done) if it still exists
    private void PauseChaseForAction()
    {
        if (Pathfinding.TargetEntityId is { } id)
        {
            ResumeChaseTargetId = id;
            Pathfinding.Clear();
        }
    }

    private void HandleSkillSlotClicked(byte slot)
    {
        HoveredAbilitySlot = null;
        ItemTooltip.Hide();

        var skillSlot = WorldHud.SkillBook.GetSkillSlot(slot)
                        ?? WorldHud.SkillBookAlt.GetSkillSlot(slot)
                        ?? WorldHud.Tools.WorldSkills.GetSkillSlot(slot);

        if (skillSlot is not null && (skillSlot.CooldownPercent > 0))
            return;

        //send chant line if one is set for this skill
        if (skillSlot is not null && !string.IsNullOrEmpty(skillSlot.Chant))
            Game.Connection.SendChant(skillSlot.Chant);

        PauseChaseForAction();
        Game.Connection.UseSkill(slot);
    }

    private void HandleSpellSlotClicked(byte slot)
    {
        HoveredAbilitySlot = null;
        ItemTooltip.Hide();

        //figure out which panel the slot came from
        var spellSlot = WorldHud.ActiveTab switch
        {
            HudTab.Spells    => WorldHud.SpellBook.GetSpellSlot(slot),
            HudTab.SpellsAlt => WorldHud.SpellBookAlt.GetSpellSlot(slot),
            HudTab.Tools     => WorldHud.Tools.WorldSpells.GetSpellSlot(slot),
            _                => WorldHud.SpellBook.GetSpellSlot(slot)
                                ?? WorldHud.SpellBookAlt.GetSpellSlot(slot)
                                ?? WorldHud.Tools.WorldSpells.GetSpellSlot(slot)
        };

        if (spellSlot is null || string.IsNullOrEmpty(spellSlot.AbilityName))
            return;

        if (spellSlot.CooldownPercent > 0)
            return;

        PauseChaseForAction();

        //notarget spells cast immediately (no cast mode)
        if (spellSlot.SpellType == SpellType.NoTarget)
        {
            if (spellSlot.CastLines == 0)
                Game.Connection.UseSpell(slot);
            else
            {
                //notarget with lines: begin chant sequence targeting self
                CastingSystem.BeginTargeting(spellSlot);

                var player = WorldState.GetPlayerEntity();

                CastingSystem.SelectTarget(
                    Game.Connection.AislingId,
                    player?.TileX ?? 0,
                    player?.TileY ?? 0,
                    Game.Connection);
            }

            return;
        }

        //enter cast mode and wait for target selection
        CastingSystem.BeginTargeting(spellSlot);
    }

    private void HandleSpellSlotDropped(byte slot, int mouseX, int mouseY)
    {
        if (IsOverVisiblePopup(mouseX, mouseY))
            return;

        //tile-first: lock onto whatever is on the dropped tile, else the sprite under the cursor
        var entity = ResolveCursorTarget(mouseX, mouseY);

        if (entity?.Type is not (ClientEntityType.Aisling or ClientEntityType.Creature))
            return;

        HandleSpellSlotClicked(slot);

        if (CastingSystem.IsTargeting)
            CastingSystem.SelectTarget(
                entity.Id,
                entity.TileX,
                entity.TileY,
                Game.Connection);
    }

    //--- hotkeys ---
    //Every key -> action mapping is player-configurable via the Keybindings system; OnRootKeyDown resolves the pressed
    //key to a GameAction and DispatchAction runs it. Only Enter (open chat) and Escape (cancel) are fixed. Emote hotkeys
    //were removed here (TODO: build an emote menu / radial wheel).

    //--- chant editing ---

    private void WireAbilityRightClicks(PanelBase panel)
    {
        foreach (var slotControl in panel.Slots)
            if (slotControl is AbilitySlotControl ability)
                ability.OnRightClick += s => OpenChantEdit(panel, s);
    }

    private void OpenChantEdit(PanelBase source, byte slot)
    {
        var control = source.GetSlotControl(slot) as AbilitySlotControl;

        if (control is null || string.IsNullOrEmpty(control.AbilityName))
            return;

        var isSpell = control is SpellSlot;

        string[] currentChants;
        int lineCount;

        if (control is SpellSlot spell)
        {
            currentChants = spell.Chants;
            lineCount = spell.CastLines;
        } else if (control is SkillSlot skill)
        {
            currentChants = [skill.Chant];
            lineCount = 1;
        } else
            return;

        ChantEdit.Show(
            slot,
            control.AbilityName,
            control.AbilityLevel ?? string.Empty,
            control.NormalTexture,
            currentChants,
            lineCount,
            isSpell);

        //the editor just sized itself for this line count; magnify the host by the current "Window size", center it on
        //the UI, and raise it above the window it was opened from (WindowOrder is the shared front-to-back counter).
        ChantEditHost.Scale = ClientSettings.EffectiveWindowScale;
        ChantEditHost.CenterOnUi();
        ChantEditHost.ZIndex = WindowOrder.Next();
    }

    private void HandleChantSet(byte slot, string[] chantLines, bool isSpell)
    {
        if (isSpell)
        {
            foreach (var panel in new[]
                     {
                         WorldHud.SpellBook,
                         WorldHud.SpellBookAlt,
                         WorldHud.Tools.WorldSpells
                     })
            {
                var spellSlot = panel.GetSpellSlot(slot);

                if (spellSlot is null)
                    continue;

                for (var i = 0; i < Math.Min(chantLines.Length, spellSlot.Chants.Length); i++)
                    spellSlot.Chants[i] = chantLines[i];
            }

            SaveSpellChants();
            WorldState.ReloadChants();
        } else
        {
            foreach (var panel in new[]
                     {
                         WorldHud.SkillBook,
                         WorldHud.SkillBookAlt,
                         WorldHud.Tools.WorldSkills
                     })
            {
                var skillSlot = panel.GetSkillSlot(slot);

                skillSlot?.Chant = chantLines.Length > 0 ? chantLines[0] : string.Empty;
            }

            SaveSkillChants();
            WorldState.ReloadChants();
        }
    }

    //--- cache / persistence helpers ---

    private void LoadPlayerFamilyList()
    {
        var family = DataContext.LocalPlayerSettings.LoadFamilyList();
        StatusBook.SetFamilyMembers(family);
        WorldList.SetFamilyNames(family);
    }

    private void SavePlayerFamilyList()
    {
        var family = StatusBook.GetFamilyMembers();

        if (family is not null)
        {
            DataContext.LocalPlayerSettings.SaveFamilyList(family);
            WorldList.SetFamilyNames(family);
        }
    }

    private void LoadPlayerFriendList()
    {
        var names = DataContext.LocalPlayerSettings.LoadFriendList();

        FriendsList.SetFriends(names);
        WorldList.SetFriendNames(names);

        //upload to the server so it can notify us which friends are online (and notify others we came online).
        //sent even when empty: others may have us on their list and want the "came online" push.
        Game.Connection.SendFriendList(names);
    }

    private void SavePlayerFriendList()
    {
        var names = FriendsList.GetFriendNames();
        DataContext.LocalPlayerSettings.SaveFriendList(names);
        WorldList.SetFriendNames(names);

        //keep the server's copy in sync after an edit (the server only announces once per session, so this
        //refresh does not re-trigger the friends-online notification)
        Game.Connection.SendFriendList(names);
    }

    private void LoadPlayerMacros()
    {
        var macros = DataContext.LocalPlayerSettings.LoadMacros();
        MacrosList.SetMacros(macros);
    }

    private void SaveSkillChants()
    {
        var entries = new List<SkillChantEntry>();

        for (byte i = 1; i <= SkillBook.MAX_SLOTS; i++)
        {
            var slot = WorldHud.SkillBook.GetSkillSlot(i)
                       ?? WorldHud.SkillBookAlt.GetSkillSlot(i)
                       ?? WorldHud.Tools.WorldSkills.GetSkillSlot(i);

            if (slot is null || string.IsNullOrEmpty(slot.AbilityName))
                continue;

            entries.Add(
                new SkillChantEntry
                {
                    Name = slot.AbilityName,
                    Chant = slot.Chant
                });
        }

        DataContext.LocalPlayerSettings.SaveSkillChants(entries);
    }

    private void SaveSpellChants()
    {
        var entries = new List<SpellChantEntry>();

        for (byte i = 1; i <= SpellBook.MAX_SLOTS; i++)
        {
            var slot = WorldHud.SpellBook.GetSpellSlot(i)
                       ?? WorldHud.SpellBookAlt.GetSpellSlot(i)
                       ?? WorldHud.Tools.WorldSpells.GetSpellSlot(i);

            if (slot is null || string.IsNullOrEmpty(slot.AbilityName))
                continue;

            var entry = new SpellChantEntry
            {
                Name = slot.AbilityName
            };
            Array.Copy(slot.Chants, entry.Chants, 10);
            entries.Add(entry);
        }

        DataContext.LocalPlayerSettings.SaveSpellChants(entries);
    }
    #endregion

    #region Root Event Handlers

    //closes the topmost open, closeable floating window; returns true if one was closed. The "focused" window
    //is simply the front-most one, since opening/clicking a window raises it to the top.
    private bool TryCloseFocusedWindow()
    {
        if (Root is null)
            return false;

        DraggableWindow? top = null;

        foreach (var child in Root.Children)
            if ((child is DraggableWindow { Closeable: true, IsOpen: true } w) && ((top is null) || (w.ZIndex > top.ZIndex)))
                top = w;

        if (top is null)
            return false;

        top.Close();

        return true;
    }

    private void OnRootKeyDown(KeyDownEvent e)
    {
        //the Controls window is capturing a key for rebinding; swallow everything so it only sets the bind
        if (Keybindings.IsCapturing)
            return;

        //Escape while awaiting a spell target cancels targeting (no server packets sent yet at this stage)
        if ((e.Key == Keys.Escape) && CastingSystem.IsTargeting)
        {
            CastingSystem.CancelTargeting();
            e.Handled = true;

            return;
        }

        //Escape closes the FOCUSED window. Opening (or clicking) a window brings it to front, so the topmost open
        //closeable window is the focused one; repeated Escape closes them front-to-back. Windows that handle their own
        //Escape via the control stack (books/group/etc.) handle it before this, so they're unaffected.
        if ((e.Key == Keys.Escape) && TryCloseFocusedWindow())
        {
            e.Handled = true;

            return;
        }

        //Enter (no modifiers) opens the chat input and is fixed, never rebindable. Skip if another text field owns
        //focus, otherwise pressing Enter inside a menu's input box would yank focus into chat instead.
        if ((e.Key == Keys.Enter) && (e.Modifiers == KeyModifiers.None))
        {
            if (ActiveChatInput.IsFocused)
            {
                e.Handled = true;

                return;
            }

            if (InputDispatcher.Instance?.ExplicitFocus is UITextBox)
                return;

            //honors the active chat tab: a normal line, or whisper entry when the Whisper tab is selected
            ActiveChatInput.FocusForTyping();
            e.Handled = true;

            return;
        }

        var action = Keybindings.Resolve(e.Key, e.Modifiers);

        if (action is null)
            return;

        var category = Keybindings.CategoryOf(action.Value);

        //system actions (fullscreen / screenshot / debug) are handled globally in ChaosGame, not here
        if (category == BindCategory.System)
            return;

        //panel toggles always run so a panel key can close its own popup or open over a dialog; gameplay actions
        //(movement, hotbar, misc) are suppressed while a modal popup owns the control stack
        if ((category != BindCategory.Panels) && (Game.Dispatcher.ControlStackCount > 0))
            return;

        DispatchAction(action.Value, e);
    }

    private void DispatchAction(GameAction action, KeyDownEvent e)
    {
        //while dead every world action (move, fight, hotbar) is inert; UI actions (panels, options, chat, log out) still work
        if (IsPlayerDead && IsWorldAction(action))
        {
            e.Handled = true;

            return;
        }

        switch (action)
        {
            case GameAction.MoveUp:
                MoveOrTurn(Direction.Up, Keybindings.IsTurnHeld(e.Modifiers));

                break;
            case GameAction.MoveDown:
                MoveOrTurn(Direction.Down, Keybindings.IsTurnHeld(e.Modifiers));

                break;
            case GameAction.MoveLeft:
                MoveOrTurn(Direction.Left, Keybindings.IsTurnHeld(e.Modifiers));

                break;
            case GameAction.MoveRight:
                MoveOrTurn(Direction.Right, Keybindings.IsTurnHeld(e.Modifiers));

                break;

            case GameAction.ToggleInventory:
                InventoryWindow?.Toggle();

                break;
            case GameAction.ToggleSkills:
                SkillWin?.Toggle();

                break;
            case GameAction.ToggleSpells:
                SpellWin?.Toggle();

                break;
            case GameAction.ToggleStats:
                StatsWin?.Toggle();

                break;
            case GameAction.ToggleActions:
                ActionsWin?.Toggle();

                break;
            case GameAction.ToggleEquipment:
                ToggleStatusBook(StatusBookTab.Equipment);

                break;
            case GameAction.ToggleLegend:
                ToggleStatusBook(StatusBookTab.Legend);

                break;
            case GameAction.ToggleGroup:
                ToggleGroupWindow();

                break;
            case GameAction.ToggleMarket:
                if (MarketWin is { Visible: true })
                {
                    MarketWin.ClearSearch(); //closing clears the Browse filter
                    MarketWin.Visible = false;
                } else
                    OpenMarket();

                break;
            case GameAction.ToggleTownMap:
                ToggleTownMap();

                break;
            case GameAction.ToggleMinimap:
                ToggleMinimap();

                break;
            case GameAction.ToggleTownMinimap:
                ClientSettings.ShowMinimap = !ClientSettings.ShowMinimap;
                ClientSettings.Save();

                break;
            case GameAction.ToggleOptions:
                OptionsWin?.Toggle();

                break;
            case GameAction.ToggleWorldList:
                ToggleWorldListPanel();

                break;
            case GameAction.ToggleSettings:
                ToggleMainOptionsPanel();

                break;
            case GameAction.ToggleBulletinBoard:
                ToggleBoardPanel();

                break;
            case GameAction.ToggleFriends:
                ToggleFriendsWindow();

                break;
            case GameAction.ToggleSocialStatus:
                ForceCloseOtherTogglePanels(Keys.R);
                ToggleSocialStatusPicker();

                break;
            case GameAction.ToggleEmotes:
                EmoteWin?.Toggle();

                break;

            case GameAction.Assail:
                TryAssail();

                break;

            case GameAction.TargetFriendly:
                TargetFriendlyCast();

                break;
            case GameAction.TargetEnemy:
                TargetEnemyCast();

                break;

            case GameAction.PickUpItem:
                TryInteract();

                break;
            case GameAction.UnequipWeaponShield:
                UnequipWeaponAndShield();

                break;
            case GameAction.FocusWhisper:
                ActiveChatInput.FocusWhisper();

                break;
            case GameAction.FlashGroup:
                FlashGroupMembers();

                break;
            case GameAction.MinimapZoomIn:
                if (TabMapVisible)
                    TabMapRenderer.ZoomIn();

                break;
            case GameAction.MinimapZoomOut:
                if (TabMapVisible)
                    TabMapRenderer.ZoomOut();

                break;

            case GameAction.LogOut:
                BeginLogout();

                break;

            default:
                //skill / spell / item hotbar slots and emotes are contiguous enum ranges
                if (action is >= GameAction.Skill1 and <= GameAction.Skill12)
                    HandleSkillSlotClicked((byte)(action - GameAction.Skill1 + 1));
                else if (action is >= GameAction.Spell1 and <= GameAction.Spell12)
                    HandleSpellSlotClicked((byte)(action - GameAction.Spell1 + 1));
                else if (action is >= GameAction.Item1 and <= GameAction.Item12)
                    Game.Connection.UseItem((byte)(action - GameAction.Item1 + 1));
                else if (action is >= GameAction.EmoteSmile and <= GameAction.EmoteConfused)
                    TrySendEmote(Keybindings.EmoteOrder[action - GameAction.EmoteSmile]);

                break;
        }

        e.Handled = true;
    }

    //emotes lock the body, so the player must be standing still; shared by keybinds and the emote menu
    private void TrySendEmote(BodyAnimation emote)
    {
        var player = WorldState.GetPlayerEntity();

        if ((player is null) || !player.IsAtRest)
            return;

        Game.Connection.SendEmote(emote);
    }

    private void MoveOrTurn(Direction direction, bool turnOnly)
    {
        //post-warp grace: a movement key still held from before the warp is ignored briefly so the player doesn't step
        //the instant the new map loads (e.g. straight back into the warp they just arrived from)
        if (KeyMoveSuppressMs > 0f)
            return;

        //manual movement is "walking away": it cancels the chase AND any pending after-action re-target
        Pathfinding.Clear();
        ResumeChaseTargetId = null;
        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        //dead players don't move at all, not even a turn in place
        if (IsPlayerDead)
            return;

        if (turnOnly)
        {
            //turn in place: only pivots while at rest, never steps
            QueuedWalkDirection = null;

            if (player.IsAtRest && (player.Direction != direction))
            {
                Game.Connection.Turn(direction);
                player.Direction = direction;
            }

            return;
        }

        if (player.IsAtRest)
        {
            //fresh input at idle invalidates any direction queued from a prior walk;
            //the queue must not override what the user just pressed
            QueuedWalkDirection = null;

            //Modern controls: walk immediately, unless the next tile is a wall and the player isn't already
            //facing that way, in which case turn first (retail "lean into a wall" feel without the two-press delay).
            //Classic controls: a press to a new direction only turns; a second press (now facing it) walks.
            if (ClientSettings.ModernControls)
            {
                (var dx, var dy) = direction.ToTileOffset();
                var nextX = player.TileX + dx;
                var nextY = player.TileY + dy;

                if (IsTileWallBlocked(nextX, nextY) || IsClosedDoorAt(nextX, nextY)
                    || WorldState.HasBlockingEntityAt(nextX, nextY, WorldState.PlayerEntityId))
                {
                    if (player.Direction != direction)
                    {
                        Game.Connection.Turn(direction);
                        player.Direction = direction;
                    }
                } else
                    PredictAndWalk(player, direction);
            } else if (player.Direction == direction)
            {
                (var cx, var cy) = direction.ToTileOffset();

                if (!IsClosedDoorAt(player.TileX + cx, player.TileY + cy))
                    PredictAndWalk(player, direction);
            }
            else
            {
                Game.Connection.Turn(direction);
                player.Direction = direction;
            }
        } else if (player.AnimState == EntityAnimState.Walking)
        {
            var totalDuration = Math.Max(1f, player.AnimFrameCount * player.AnimFrameIntervalMs);
            var progress = player.AnimElapsedMs / totalDuration;

            if (progress >= WALK_QUEUE_THRESHOLD)
                QueuedWalkDirection = direction;
        }
    }

    //the held-key movement poll runs outside the dispatcher's focus routing and must check these itself
    private bool GameplayInputAllowed()
        => !IsPlayerDead
           && !Keybindings.IsCapturing
           && !ActiveChatInput.IsFocused
           && (Game.Dispatcher.ExplicitFocus is not UITextBox)
           && (Game.Dispatcher.ControlStackCount == 0);

    //actions that act on the WORLD (move, fight, interact, use a slot, emote) - all inert while the player is dead.
    //Anything not listed is UI (panel toggles, options, chat focus, minimap zoom, log out) and still works when dead.
    //Assail..Item12 is the contiguous assail + skill/spell/item hotbar bank in the GameAction enum.
    private static bool IsWorldAction(GameAction action)
        => action is GameAction.MoveUp or GameAction.MoveDown or GameAction.MoveLeft or GameAction.MoveRight
                  or GameAction.PickUpItem
                  or GameAction.UnequipWeaponShield
           || (action is >= GameAction.Assail and <= GameAction.Item12)
           || (action is >= GameAction.EmoteSmile and <= GameAction.EmoteConfused);

    private static Direction MovementDirection(GameAction action)
        => action switch
        {
            GameAction.MoveUp   => Direction.Up,
            GameAction.MoveDown => Direction.Down,
            GameAction.MoveLeft => Direction.Left,
            _                   => Direction.Right
        };

    //rate-limited to SPACEBAR_INTERVAL_MS; only fires while the player is standing still
    private void TryAssail()
    {
        var player = WorldState.GetPlayerEntity();

        if (player is null || !player.IsAtRest)
            return;

        //warn once per auto-attack session: pressing Space while already following/attacking an enemy is redundant
        if (Pathfinding.TargetEntityId.HasValue && !AutoAttackSpaceWarned)
        {
            WorldState.Chat.AddMessage("Auto-attacking! No need to keep pressing the attack key.", new Color(220, 120, 120), ChatChannel.System);
            AutoAttackSpaceWarned = true;
        }

        var now = Environment.TickCount64;

        if ((now - LastSpacebarMs) < SPACEBAR_INTERVAL_MS)
            return;

        Game.Connection.Spacebar();
        LastSpacebarMs = now;
    }

    private void UnequipWeaponAndShield()
    {
        if (WorldState.Equipment.GetSlot(EquipmentSlot.Weapon) is not null)
            Game.Connection.Unequip(EquipmentSlot.Weapon);

        if (WorldState.Equipment.GetSlot(EquipmentSlot.Shield) is not null)
            Game.Connection.Unequip(EquipmentSlot.Shield);
    }

    //flash group member highlighting (gated while a request is pending or a flash is already active)
    private void FlashGroupMembers()
    {
        if (GroupHighlightRequested || (GroupHighlightedIds.Count > 0))
            return;

        GroupHighlightRequested = true;
        Game.Connection.RequestSelfProfile();
    }

    private void ToggleTownMap()
    {
        if (TownMapControl.IsOpen)
            TownMapControl.BeginClose();
        else
            ShowTownMap();
    }

    private void ToggleMinimap()
    {
        if (!CurrentMapFlags.HasFlag(MapFlags.NoTabMap))
            TabMapVisible = !TabMapVisible;
    }

    private void OpenGroupWindow()
    {
        Game.Connection.RequestSelfProfile();
        GroupPanel.ShowMembers();
    }

    //the Group key toggles: pressing it again while the group window is up closes it (like the other panel keys)
    private void ToggleGroupWindow()
    {
        if (GroupPanel.Visible)
            GroupPanel.Hide();
        else
            OpenGroupWindow();
    }

    //the Friends key/menu toggles the friends window (data is loaded on world entry via LoadPlayerFriendList)
    private void ToggleFriendsWindow()
    {
        if (FriendsList.Visible)
            FriendsList.Hide();
        else
            FriendsList.Show();
    }

    //The Equipment (U) and Legend (J) keys both drive the one Equipment book, on different tabs. Pressing a key when the
    //book is already showing THAT tab closes it; pressing it on a different tab just switches tab (no server round-trip);
    //pressing it while closed asks the server for the profile, which opens the book on that tab (HandleSelfProfile).
    private void ToggleStatusBook(StatusBookTab tab)
    {
        if (StatusBook.Visible)
        {
            if (StatusBook.ActiveTab == tab)
                StatusBook.Close();
            else
                StatusBook.SwitchTab(tab);

            return;
        }

        RequestStatusBook(tab);
    }

    //asks the server for the profile, which opens the book on the requested tab (see ServerHandlers)
    private void RequestStatusBook(StatusBookTab tab)
    {
        SelfProfileRequested = true;
        SelfProfileRequestedTab = tab;
        Game.Connection.RequestSelfProfile();
    }

    private void ToggleMainOptionsPanel()
    {
        ForceCloseOtherTogglePanels(Keys.Q);

        if (MainOptions.Visible)
        {
            SettingsDialog.Hide();
            MacrosList.Hide();
            FriendsList.Hide();
            MainOptions.SlideClose();
        } else
        {
            WorldHud.OptionButton?.IsSelected = true;

            MainOptions.Show();
        }
    }

    private void ToggleBoardPanel()
    {
        ForceCloseOtherTogglePanels(Keys.W);

        if (IsAnyBoardPanelVisible())
        {
            if (BoardList.Visible)
                BoardList.SlideClose();
            else
                WorldState.Board.CloseSession();
        } else
        {
            WorldHud.BulletinButton?.IsSelected = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.BoardList);
        }
    }

    //the world list is a free-floating window (like Friends/Group): closing hides it, opening asks the server for the
    //list and the response (OnWorldListChanged -> Show) makes the window visible + fills it.
    private void ToggleWorldListPanel()
    {
        if (WorldList.Visible)
            WorldList.Hide();
        else
            Game.Connection.RequestWorldList();
    }

    private void OnRootMouseScroll(MouseScrollEvent e)
    {
        if (WorldHud.ChatDisplay.Visible)
        {
            WorldHud.ChatDisplay.OnMouseScroll(e);

            return;
        }

        if (WorldHud.MessageHistory.Visible)
            WorldHud.MessageHistory.OnMouseScroll(e);
    }

    //right-click pathfinding fires on press rather than release for snappier response
    private void OnRootMouseDown(MouseDownEvent e)
    {
        //dead players can't walk or interact
        if (IsPlayerDead)
        {
            e.Handled = true;

            return;
        }

        //an NPC dialog or sign is open: clicks that land outside the dialog must not move or path the player.
        //keyed on the control's visibility (cleared by HideAll on close) rather than NpcInteraction.IsActive,
        //which lingers after a local close until the server replies and would leave the player frozen.
        if (NpcSession.Visible)
        {
            e.Handled = true;

            return;
        }

        //a sign/board text popup is open: the press is inert; dismiss runs on the click event so the handled
        //click can never re-click the sign's tile and instantly reopen the popup
        if (TextPopup.Visible)
        {
            e.Handled = true;

            return;
        }

        //the town map is a full-screen modal: any press that reaches the root while it is up is inert (its own
        //handlers manage warps/close; the world under it must never react)
        if (TownMapControl.Visible)
        {
            e.Handled = true;

            return;
        }

        var moveButton = ClientSettings.FlipWalkInteract ? MouseButton.Left : MouseButton.Right;

        if (e.Button != moveButton)
            return;

        //move-button while targeting: pathfind normally, targeting stays active

        if (e.Shift)
        {
            HandleShiftRightClick(e.ScreenX, e.ScreenY);
            e.Handled = true;

            return;
        }

        //cache the hovered entity for the upcoming doubleclick; pathfinding from this press will start
        //moving the player on the next update, shifting the camera so the second click's ScreenToTile
        //would resolve to a different world tile than the entity actually occupies
        var currentTick = Environment.TickCount;

        if ((currentTick - PendingDoubleClickTick) > DOUBLE_CLICK_CACHE_WINDOW_MS)
            PendingDoubleClickEntityId = null;

        //tile-first: cache the occupant of the clicked tile (else the sprite under the cursor) as the chase target
        var hoverEntity = ResolveCursorTarget(e.ScreenX, e.ScreenY);

        //exclude self: the player's own sprite has a hitbox, and a rapid right-click on the tile the
        //player is walking off of overlaps that hitbox, which would cache the player as a double-click
        //target and kick off a self-follow loop in OnRootDoubleClick
        if (hoverEntity?.Type is ClientEntityType.Aisling or ClientEntityType.Creature
            && (hoverEntity.Id != Game.Connection.AislingId))
        {
            PendingDoubleClickEntityId = hoverEntity.Id;
            PendingDoubleClickTick = currentTick;
        }

        //ctrl is a UI modifier (ctrl+left-click opens the aisling context menu); suppress single-press
        //pathfinding but prime the same-tile tracker so a right-doubleclick still resolves to follow
        if (e.Ctrl)
        {
            if (MapFile is not null)
            {
                (var tileX, var tileY) = ScreenToTile(e.ScreenX, e.ScreenY);
                tileX = Math.Clamp(tileX, 0, MapFile.Width - 1);
                tileY = Math.Clamp(tileY, 0, MapFile.Height - 1);
                RightClickTracker.Click(tileX, tileY);
            }
        } else
            HandleWorldRightClick(e.ScreenX, e.ScreenY);

        e.Handled = true;
    }

    private void OnRootClick(ClickEvent e)
    {
        //dead players can't interact with the world (same gate as OnRootMouseDown; clicks are synthesized separately)
        if (IsPlayerDead)
        {
            e.Handled = true;

            return;
        }

        var interactButton = ClientSettings.FlipWalkInteract ? MouseButton.Right : MouseButton.Left;

        if (e.Button != interactButton)
            return;

        //while an NPC dialog or town map is open, a world click must not move the player; keyed on the control's
        //visibility (HideAll clears it on close) rather than NpcInteraction.IsActive, which lingers after a local
        //close until the server replies and would leave the player unable to move
        if (NpcSession.Visible || TownMapControl.Visible)
        {
            e.Handled = true;

            return;
        }

        //a sign/board popup: any click dismisses it once the open delay has passed, handled either way so the
        //same click cannot also act on the world and instantly reopen the sign's popup
        if (TextPopup.Visible)
        {
            if (TextPopup.ReadyToDismiss)
                TextPopup.Hide();

            e.Handled = true;

            return;
        }

        //clicking the money label in the exchange opens the gold amount popup; the exchange sits in a magnifying
        //ScaleHost so the raw cursor must be mapped back into un-scaled space before the hit-test
        var (exMoneyX, exMoneyY) = MapToExchange(e.ScreenX, e.ScreenY);

        if (Exchange.Visible && Exchange.IsMyMoneyClicked(exMoneyX, exMoneyY))
        {
            GoldDrop.ShowForTarget(Exchange.OtherUserId, 0, 0);
            WorldHud.SetDescription($"Gold( {WorldState.Inventory.Gold} )");
            e.Handled = true;

            return;
        }

        //cast mode: target selection or cancel. A readied spell always casts on the auto-snapped closest target
        //regardless of cursor position; a click confirms the cast. Side mouse buttons force closest-enemy or
        //closest-friendly. Cancels when no valid target is in range.
        if (CastingSystem.IsTargeting)
        {
            if (FindClosestTarget() is { } target)
                CastingSystem.SelectTarget(target.Id, target.TileX, target.TileY, Game.Connection);
            else
                CastingSystem.CancelTargeting();

            e.Handled = true;

            return;
        }

        //ctrl+click opens the context menu on aisling entities
        if (e.Ctrl)
        {
            HandleCtrlClick(e.ScreenX, e.ScreenY);
            e.Handled = true;

            return;
        }

        //alt+click on self opens the self profile
        if (e.Alt)
        {
            var hoverEntity = ResolveCursorTarget(e.ScreenX, e.ScreenY);

            if (hoverEntity is not null && (hoverEntity.Id == Game.Connection.AislingId))
            {
                SelfProfileRequested = true;
                Game.Connection.RequestSelfProfile();
            } else
                HandleWorldClick(e.ScreenX, e.ScreenY);

            e.Handled = true;

            return;
        }

        HandleWorldClick(e.ScreenX, e.ScreenY);
        e.Handled = true;
    }

    private void OnRootDoubleClick(DoubleClickEvent e)
    {
        if (MapFile is null)
            return;

        //dead players can't interact with the world (classic attacks/follows on double-click)
        if (IsPlayerDead)
        {
            e.Handled = true;

            return;
        }

        //modern controls do everything on a single click, so double-click has no actions
        if (ClientSettings.ModernControls)
            return;

        var viewport = WorldInputBounds;

        if ((e.ScreenX < viewport.X)
            || (e.ScreenX >= (viewport.X + viewport.Width))
            || (e.ScreenY < viewport.Y)
            || (e.ScreenY >= (viewport.Y + viewport.Height)))
            return;

        (var tileX, var tileY) = ScreenToTile(e.ScreenX, e.ScreenY);

        var interactBtn = ClientSettings.FlipWalkInteract ? MouseButton.Right : MouseButton.Left;
        var moveBtn     = ClientSettings.FlipWalkInteract ? MouseButton.Left  : MouseButton.Right;

        if (e.Button == interactBtn)
        {
            var sameTile = LeftClickTracker.Click(tileX, tileY);

            if (!sameTile)
                return;

            //shift+doubleclick bypasses hitboxes and only picks up ground items
            if (e.Shift)
            {
                var groundItem = WorldState.GetGroundItemAt(tileX, tileY);

                if (groundItem is not null)
                {
                    var firstEmptySlot = WorldState.Inventory.GetFirstEmptySlot();
                    Game.Connection.PickupItem(groundItem.TileX, groundItem.TileY, firstEmptySlot);
                }
            } else
            {
                var entity = GetEntityAtScreen(e.ScreenX, e.ScreenY);

                if (entity is not null && !entity.IsHidden)
                {
                    if (entity.Type == ClientEntityType.GroundItem)
                    {
                        var firstEmptySlot = WorldState.Inventory.GetFirstEmptySlot();
                        Game.Connection.PickupItem(entity.TileX, entity.TileY, firstEmptySlot);
                    } else if ((entity.Type != ClientEntityType.Aisling) || ClientSettings.EnableProfileClick)
                        Game.Connection.ClickEntity(entity.Id);
                }
            }

            e.Handled = true;
        } else if (e.Button == moveBtn)
        {
            if (!MapPreloaded)
                return;

            var player = WorldState.GetPlayerEntity();

            if (player is null)
                return;

            tileX = Math.Clamp(tileX, 0, MapFile.Width - 1);
            tileY = Math.Clamp(tileY, 0, MapFile.Height - 1);

            //tracker still updates so anything relying on the last-clicked tile stays accurate
            var sameTile = RightClickTracker.Click(tileX, tileY);

            //prefer the entity captured on the first single right-click; pathfinding from that click will have
            //moved the player by now, shifting the camera so ScreenToTile would land on a different world tile
            WorldEntity? entity = null;

            if (PendingDoubleClickEntityId.HasValue
                && ((Environment.TickCount - PendingDoubleClickTick) <= DOUBLE_CLICK_CACHE_WINDOW_MS))
                entity = WorldState.GetEntity(PendingDoubleClickEntityId.Value);

            //fallback to the legacy tile-based lookup only when the cache miss AND the tiles line up
            if (entity is null)
            {
                if (!sameTile)
                {
                    PendingDoubleClickEntityId = null;

                    return;
                }

                entity = WorldState.GetEntityAt(tileX, tileY);
            }

            //reject self since following yourself produces a re-pathfinding loop that walks into walls or oscillates;
            //reject hidden aislings since they have a hitbox for spell targeting but should not be followable
            if (entity?.Type is ClientEntityType.Aisling or ClientEntityType.Creature
                && (entity.Id != Game.Connection.AislingId)
                && !entity.IsHidden)
            {
                Pathfinding.SetEntityTarget(entity.Id);
                PathfindToEntity(player, entity);
            }

            PendingDoubleClickEntityId = null;
            e.Handled = true;
        }
    }

    private void OnRootDragStart(DragStartEvent e)
    {
        if (!GameplayInputAllowed() || !ClientSettings.ModernControls)
            return;

        //only left-button drags initiate a ground item move
        if (e.Button != MouseButton.Left)
            return;

        var groundItem = GetGroundItemAtScreen(e.ScreenX, e.ScreenY);

        if (groundItem is null)
            return;

        e.Payload = new GroundItemDragPayload
        {
            SourceTileX = groundItem.TileX,
            SourceTileY = groundItem.TileY
        };

        GroundItemDragIcon = Game.ItemRenderer.GetSprite(groundItem.SpriteId, groundItem.ItemColor)?.Texture;
    }

    private WorldEntity? GetGroundItemAtScreen(int mouseX, int mouseY)
    {
        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        return WorldState.GetGroundItemAt(tileX, tileY);
    }

    private void OnRootDragMove(DragMoveEvent e)
    {
        var dragging = GetDraggingPanel();
        dragging?.UpdateDragPosition(e.ScreenX, e.ScreenY);

        if (e.Payload is EquipmentDragPayload equipPayload)
            EquipmentDragIcon = equipPayload.Icon;
    }

    private void OnRootDragDrop(DragDropEvent e)
    {
        //equipment drag released anywhere → unequip (item goes to first free inventory slot)
        if (e.Payload is EquipmentDragPayload equipPayload)
        {
            EquipmentDragIcon = null;
            Game.Connection.Unequip(equipPayload.Slot);
            e.Handled = true;

            return;
        }

        //ground item drag: pick up from source tile, drop at destination tile
        if (e.Payload is GroundItemDragPayload groundPayload)
        {
            GroundItemDragIcon = null;
            (var destX, var destY) = ScreenToTile(e.ScreenX, e.ScreenY);
            var firstEmpty = WorldState.Inventory.GetFirstEmptySlot();

            if (firstEmpty == 0)
            {
                //inventory full, cancel silently; item stays on the ground
                e.Handled = true;

                return;
            }

            //the server processes packets in arrival order, so pick-up fills the slot before the drop executes
            Game.Connection.PickupItem(groundPayload.SourceTileX, groundPayload.SourceTileY, firstEmpty);
            Game.Connection.DropItem(firstEmpty, destX, destY, 1);
            e.Handled = true;

            return;
        }

        if (e.Payload is not SlotDragPayload payload)
            return;

        if (payload.Source.Parent is not PanelBase { IsDragging: true } panel)
            return;

        panel.CompleteDragOutside(e.ScreenX, e.ScreenY);
        e.Handled = true;
    }

    #endregion

    #region Click Handling
    //the world target (WorldRenderWidth x WorldRenderHeight) is drawn into ChaosGame.WorldDrawRect, so a native
    //mouse coordinate maps back to world-render space by removing that rect's offset and inverting its scale
    private int ToWorldX(int nativeX)
        => (nativeX - ChaosGame.WorldDrawRect.X) * ChaosGame.WorldRenderWidth / Math.Max(1, ChaosGame.WorldDrawRect.Width);

    private int ToWorldY(int nativeY)
        => (nativeY - ChaosGame.WorldDrawRect.Y) * ChaosGame.WorldRenderHeight / Math.Max(1, ChaosGame.WorldDrawRect.Height);

    //single rule for what the cursor is targeting, used by hover, spell targeting, the right-click chase, and world drops.
    //the ground-cursor tile wins: whatever is on the tile under the cursor (creature/aisling, else ground item) is the
    //target no matter what. Only when that tile is empty does it fall back to the sprite under the cursor; sprites are
    //tall so a creature a tile or two away can hang "under" the cursor and must not beat the actual pointed-at tile.
    //This mirrors HandleModernLeftClick's tile-first resolution so clicking, hovering, and casting all agree.
    private WorldEntity? ResolveCursorTarget(int mouseX, int mouseY)
    {
        if (MapFile is null)
            return null;

        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        return WorldState.GetEntityAt(tileX, tileY) ?? GetEntityAtScreen(mouseX, mouseY);
    }

    private WorldEntity? GetEntityAtScreen(int mouseX, int mouseY)
    {
        if (MapFile is null)
            return null;

        //hitbox rects are in 640x480 viewport-relative coords (viewport origin is 0,0), so convert the
        //native mouse back into that space.
        var viewportMouseX = ToWorldX(mouseX);
        var viewportMouseY = ToWorldY(mouseY);

        //scan hitboxes back-to-front (last drawn = closest to camera = highest priority)
        for (var i = EntityHitBoxes.Count - 1; i >= 0; i--)
        {
            var hitbox = EntityHitBoxes[i];

            if (hitbox.ScreenRect.Contains(viewportMouseX, viewportMouseY))
                return WorldState.GetEntity(hitbox.EntityId);
        }

        //fallback: tile-based lookup for ground items
        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        return WorldState.GetGroundItemAt(tileX, tileY);
    }

    private (int TileX, int TileY) ScreenToTile(int mouseX, int mouseY)
    {
        var worldPos = Camera.ScreenToWorld(new Vector2(ToWorldX(mouseX), ToWorldY(mouseY)));
        var tile = Camera.WorldToTile(worldPos.X, worldPos.Y, MapFile!.Height);

        return (tile.X, tile.Y);
    }

    //the "Pick Up / Interact" action (default E / F). One key for everything, rate-limited so holding or mashing
    //cannot spam the server. Priority: pick up a ground item, talk to an NPC, click whatever is in front
    //(door / sign / reactor). Tiles are searched front, sides, behind.
    private void TryInteract()
    {
        var now = Environment.TickCount64;

        if ((now - LastInteractMs) < INTERACT_INTERVAL_MS)
            return;

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        var px = player.TileX;
        var py = player.TileY;
        (var fx, var fy) = player.Direction.ToTileOffset();

        //remember the tile in front as the interact point so the alternative camera focuses the NPC we are facing
        NoteInteractTile(px + fx, py + fy);

        //the tiles around the player, in interaction priority: in front, the two sides, then behind
        (int X, int Y)[] around =
        [
            (px + fx, py + fy), //front
            (px - fy, py + fx), //one side (perpendicular to facing)
            (px + fy, py - fx), //the other side
            (px - fx, py - fy)  //behind
        ];

        //1) pick up a ground item: the tile you're standing on first, then around you (front/sides/behind)
        var slot = WorldState.Inventory.GetFirstEmptySlot();

        if (slot != 0)
        {
            if (WorldState.HasGroundItemAt(px, py))
            {
                Game.Connection.PickupItem(px, py, slot);
                LastInteractMs = now;

                return;
            }

            foreach (var (x, y) in around)
                if (WorldState.HasGroundItemAt(x, y))
                {
                    Game.Connection.PickupItem(x, y, slot);
                    LastInteractMs = now;

                    return;
                }
        }

        //2) talk to the nearest NPC (merchant / dialog), front first
        foreach (var (x, y) in around)
            if (WorldState.GetEntityAt(x, y) is { Type: ClientEntityType.Creature, IsHidden: false, CreatureType: CreatureType.Merchant } npc)
            {
                Game.Connection.ClickEntity(npc.Id);
                LastInteractMs = now;

                return;
            }

        //3) interact with whatever is in front: a door, sign, or other reactor tile
        if (TileHasForeground(px + fx, py + fy))
        {
            Game.Connection.ClickTile(px + fx, py + fy);
            LastInteractMs = now;

            return;
        }

        //4) nothing right next to us: reach out to the CLOSEST ground item within INTERACT_REACH_TILES
        if (slot != 0)
        {
            WorldEntity? nearestItem = null;
            var bestItem = int.MaxValue;

            foreach (var e in WorldState.GetEntities())
            {
                if ((e.Type != ClientEntityType.GroundItem) || e.IsHidden)
                    continue;

                var dx = e.TileX - px;
                var dy = e.TileY - py;

                if ((Math.Abs(dx) > INTERACT_REACH_TILES) || (Math.Abs(dy) > INTERACT_REACH_TILES))
                    continue;

                var d = (dx * dx) + (dy * dy);

                if (d < bestItem)
                {
                    bestItem = d;
                    nearestItem = e;
                }
            }

            if (nearestItem is not null)
            {
                Game.Connection.PickupItem(nearestItem.TileX, nearestItem.TileY, slot);
                LastInteractMs = now;

                return;
            }
        }

        //5) still nothing: talk to the CLOSEST NPC on screen
        if (MapFile is not null)
        {
            var (vMinX, vMinY, vMaxX, vMaxY) = Camera.GetVisibleTileBounds(MapFile.Width, MapFile.Height);
            WorldEntity? nearestNpc = null;
            var bestNpc = int.MaxValue;

            foreach (var e in WorldState.GetEntities())
            {
                if (e is not { Type: ClientEntityType.Creature, IsHidden: false, CreatureType: CreatureType.Merchant })
                    continue;

                if ((e.TileX < vMinX) || (e.TileX > vMaxX) || (e.TileY < vMinY) || (e.TileY > vMaxY))
                    continue;

                var dx = e.TileX - px;
                var dy = e.TileY - py;
                var d = (dx * dx) + (dy * dy);

                if (d < bestNpc)
                {
                    bestNpc = d;
                    nearestNpc = e;
                }
            }

            if (nearestNpc is not null)
            {
                NoteInteractTile(nearestNpc.TileX, nearestNpc.TileY); //so the camera focuses the NPC we chose
                Game.Connection.ClickEntity(nearestNpc.Id);
                LastInteractMs = now;
            }
        }
    }

    private void HandleWorldClick(int mouseX, int mouseY)
    {
        if (MapFile is null)
            return;

        var viewport = WorldInputBounds;

        if ((mouseX < viewport.X)
            || (mouseX >= (viewport.X + viewport.Width))
            || (mouseY < viewport.Y)
            || (mouseY >= (viewport.Y + viewport.Height)))
            return;

        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        //track tile for same-tile guard used by onrootdoubleclick
        LeftClickTracker.Click(tileX, tileY);

        //remember where we interacted so the alternative camera can pick the right same-named NPC if a dialog opens
        NoteInteractTile(tileX, tileY);

        //check group box text overlays first since they sit above entity hitboxes;
        //rects are viewport-relative, so rebase mouse coords to match
        var groupBoxViewport = WorldInputBounds;
        var groupBoxHit = Overlays.GetGroupBoxAtScreen(mouseX - groupBoxViewport.X, mouseY - groupBoxViewport.Y);

        if (groupBoxHit.HasValue)
        {
            (_, var entityName) = groupBoxHit.Value;

            Game.Connection.SendGroupInvite(ClientGroupSwitch.ViewGroupBox, entityName);

            return;
        }

        //single click: check for entity at hitbox first, then tile interaction
        var entity = GetEntityAtScreen(mouseX, mouseY);

        //clicking yourself never opens your profile and counts as clicking the ground tile you stand on;
        //the "click character profile" option only affects clicking other players
        if (entity is not null && (entity.Id == Game.Connection.AislingId))
            entity = null;

        //modern scheme: a single left-click does everything (pick up / attack / talk / interact)
        if (ClientSettings.ModernControls)
        {
            HandleModernLeftClick(entity, tileX, tileY, mouseX, mouseY);

            return;
        }

        if (entity?.Type is ClientEntityType.Creature)
            Game.Connection.ClickEntity(entity.Id);
        else if (TileHasForeground(tileX, tileY))
            Game.Connection.ClickTile(tileX, tileY);
    }

    //modern single-left-click: pick up a ground item, attack an enemy, talk to an NPC/merchant, open another
    //player's profile, or interact with a door/sign. Classic does these on double-click instead.
    //Self was already nulled by the caller so this never acts on the player themselves.
    private void HandleModernLeftClick(WorldEntity? spriteEntity, int tileX, int tileY, int mouseX, int mouseY)
    {
        //tile first: whatever occupies the clicked tile wins (ground item, then enemy/NPC/player).
        //sprites are tall, so a creature a tile or two away can sit "under" the cursor and must not beat a click
        //on a closer tile; spriteEntity is the fallback only when the tile is empty.
        var entity = WorldState.GetGroundItemAt(tileX, tileY) ?? WorldState.GetEntityAt(tileX, tileY) ?? spriteEntity;

        switch (entity?.Type)
        {
            case ClientEntityType.GroundItem:
                Game.Connection.PickupItem(entity.TileX, entity.TileY, WorldState.Inventory.GetFirstEmptySlot());

                return;

            case ClientEntityType.Creature when !entity.IsHidden:
                //a plain creature is an enemy -> attack (follow + auto-assail); merchants / NPC kinds -> talk to it
                if (entity.CreatureType == CreatureType.Normal)
                {
                    //clicking the same enemy that is already being auto-attacked is redundant; warn after 3 clicks
                    if (Pathfinding.TargetEntityId == entity.Id)
                    {
                        if (RedundantClickTargetId != entity.Id)
                        {
                            RedundantClickTargetId = entity.Id;
                            RedundantClickCount = 0;
                        }

                        RedundantClickCount++;

                        if (RedundantClickCount >= 3)
                        {
                            WorldState.Chat.AddMessage("Already targeting this enemy! No need to keep clicking.", new Color(220, 120, 120), ChatChannel.System);
                            RedundantClickCount = 0;
                        }
                    } else
                    {
                        RedundantClickTargetId = null;
                        RedundantClickCount = 0;
                    }

                    var player = WorldState.GetPlayerEntity();

                    if ((player is not null) && MapPreloaded)
                    {
                        Pathfinding.SetEntityTarget(entity.Id);
                        PathfindToEntity(player, entity);
                    }
                } else
                    Game.Connection.ClickEntity(entity.Id);

                return;

            //another player: attack only when the server marked them hostile (NameTagStyle.Hostile, set via IsHostileTo
            //on PvP maps). Outside PvP a click opens the player context menu at the cursor.
            case ClientEntityType.Aisling when !entity.IsHidden && (entity.Id != Game.Connection.AislingId):
            {
                if (entity.NameTagStyle == NameTagStyle.Hostile)
                {
                    var player = WorldState.GetPlayerEntity();

                    if ((player is not null) && MapPreloaded)
                    {
                        Pathfinding.SetEntityTarget(entity.Id);
                        PathfindToEntity(player, entity);
                    }
                } else if (ClientSettings.EnableProfileClick)
                    ShowAislingContextMenu(entity, mouseX, mouseY);

                return;
            }
        }

        //tall sprites (signs, doors) extend above their base tile; check pixel-perfect
        //first so a click on the sprite always targets the sprite's tile
        var signDoorTile = FindSignDoorAtCursor(mouseX, mouseY);

        if (signDoorTile.HasValue)
        {
            Game.Connection.ClickTile(signDoorTile.Value.X, signDoorTile.Value.Y);

            return;
        }

        //empty ground, your own tile, or a door/sign -> tile interaction
        if (TileHasForeground(tileX, tileY))
            Game.Connection.ClickTile(tileX, tileY);
    }

    private void HandleCtrlClick(int mouseX, int mouseY)
    {
        if (MapFile is null)
            return;

        var viewport = WorldInputBounds;

        if ((mouseX < viewport.X)
            || (mouseX >= (viewport.X + viewport.Width))
            || (mouseY < viewport.Y)
            || (mouseY >= (viewport.Y + viewport.Height)))
            return;

        //tile-first: the aisling on the clicked tile wins, else the sprite under the cursor
        var entity = ResolveCursorTarget(mouseX, mouseY);

        if (entity is null)
            return;

        if ((entity.Type == ClientEntityType.Aisling) && (entity.Id != Game.Connection.AislingId))
            ShowAislingContextMenu(entity, mouseX, mouseY);
    }

    //opens the player context menu (Profile / Group Request / Whisper) at the cursor for another aisling. Shared by the
    //modern left-click path and the classic Ctrl+click path.
    private void ShowAislingContextMenu(WorldEntity entity, int mouseX, int mouseY)
    {
        var name = entity.Name;
        var id = entity.Id;

        AislingContext.Show(
            mouseX,
            mouseY,
            name,
            () => Game.Connection.ClickEntity(id),
            () => Game.Connection.SendGroupInvite(ClientGroupSwitch.TryInvite, name),
            () => ActiveChatInput.Focus($"-> {name}: ", TextColors.Whisper));
    }

    private void HandleWorldRightClick(int mouseX, int mouseY)
    {
        if (MapFile is null || !MapPreloaded)
            return;

        var viewport = WorldInputBounds;

        if ((mouseX < viewport.X)
            || (mouseX >= (viewport.X + viewport.Width))
            || (mouseY < viewport.Y)
            || (mouseY >= (viewport.Y + viewport.Height)))
            return;

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        //clamp to map bounds
        tileX = Math.Clamp(tileX, 0, MapFile.Width - 1);
        tileY = Math.Clamp(tileY, 0, MapFile.Height - 1);

        //track tile for same-tile guard used by onrootdoubleclick
        RightClickTracker.Click(tileX, tileY);

        //don't pathfind to current position
        if ((tileX == player.TileX) && (tileY == player.TileY))
        {
            Pathfinding.Clear();

            return;
        }

        //reject right-clicks onto walls so we don't auto-walk into them. Doors and warp arches are not walls.
        if (IsTileWallBlocked(tileX, tileY))
            return;

        //single right-click: pathfind to the ground tile
        Pathfinding.TargetEntityId = null;
        PathfindToTile(player, tileX, tileY);

        //seed the hold-to-walk baseline so the "half the path walked" re-aim works from this very first path, and the
        //target so an idle re-aim only fires once the cursor points somewhere new (a held move button keeps
        //re-pathing toward the cursor in WorldScreen.Update); harmless for a non-held single click.
        HeldWalkPathLength = Pathfinding.Path?.Count ?? 0;
        HeldWalkLastTarget = (tileX, tileY);
        HeldWalkRepathTimer = 0;
    }

    //the button that walks the player: right by default, left when walk/interact is flipped. Matches the move-button
    //selection in OnRootMouseDown so a held walk follows the same binding as the click that starts it.
    private static bool IsMoveButtonHeld()
        => ClientSettings.FlipWalkInteract ? InputBuffer.IsLeftButtonHeld : InputBuffer.IsRightButtonHeld;

    //the map tile under the cursor, clamped to bounds. Used by the hold-to-walk poll, which only runs with a map loaded.
    private (int X, int Y) HeldWalkCursorTile()
    {
        (var tileX, var tileY) = ScreenToTile(InputBuffer.MouseX, InputBuffer.MouseY);

        return (Math.Clamp(tileX, 0, MapFile!.Width - 1), Math.Clamp(tileY, 0, MapFile!.Height - 1));
    }

    //re-paths toward the cursor tile; only call when the player is at rest so the path starts from the true tile.
    //skips pathing when the cursor is on the player's own tile or a wall so a momentary hover neither stops the
    //player nor runs the pathfinder; records the target so idle re-aim doesn't retry the same tile
    private void HeldWalkRepathToCursor(WorldEntity player, int tileX, int tileY)
    {
        HeldWalkLastTarget = (tileX, tileY);

        var onOwnTile = (tileX == player.TileX) && (tileY == player.TileY);

        var blocked = IsTileWallBlocked(tileX, tileY);

        if (!onOwnTile && !blocked)
        {
            Pathfinding.TargetEntityId = null;
            PathfindToTile(player, tileX, tileY);
        }

        HeldWalkPathLength = Pathfinding.Path?.Count ?? 0;
    }

    private void HandleShiftRightClick(int mouseX, int mouseY)
    {
        //explicit cancel: drops the chase and any pending after-action re-target
        Pathfinding.Clear();
        ResumeChaseTargetId = null;

        if (MapFile is null)
            return;

        var player = WorldState.GetPlayerEntity();

        if (player is null || !player.IsAtRest)
            return;

        var viewport = WorldInputBounds;

        if ((mouseX < viewport.X)
            || (mouseX >= (viewport.X + viewport.Width))
            || (mouseY < viewport.Y)
            || (mouseY >= (viewport.Y + viewport.Height)))
            return;

        (var tileX, var tileY) = ScreenToTile(mouseX, mouseY);

        tileX = Math.Clamp(tileX, 0, MapFile.Width - 1);
        tileY = Math.Clamp(tileY, 0, MapFile.Height - 1);

        if ((tileX == player.TileX) && (tileY == player.TileY))
            return;

        var dx = tileX - player.TileX;
        var dy = tileY - player.TileY;

        var direction = Math.Abs(dx) >= Math.Abs(dy)
            ? dx > 0 ? Direction.Right : Direction.Left
            : dy > 0 ? Direction.Down : Direction.Up;

        if (player.Direction != direction)
        {
            Game.Connection.Turn(direction);
            player.Direction = direction;
        }
    }

    //re-routes a path whose next step became blocked or desynced without releasing the chase.
    //an entity chase re-paths and keeps the target even with no path right now; a tile path re-paths to the
    //original destination and only stops if that destination is now unreachable
    private void RecoverPath(WorldEntity player)
    {
        if (Pathfinding.TargetEntityId is { } targetId)
        {
            Pathfinding.Path = null;

            if (WorldState.GetEntity(targetId) is { } target)
                PathfindToEntity(player, target);
            else
                Pathfinding.Clear();

            return;
        }

        //tile path: the destination is the bottom of the remaining stack
        (int X, int Y)? dest = null;

        if (Pathfinding.Path is not null)
            foreach (var p in Pathfinding.Path) //Stack enumerates top->bottom, so the last seen is the goal
                dest = (p.X, p.Y);

        Pathfinding.Path = null;

        if (dest is { } d)
            PathfindToTile(player, d.X, d.Y);
    }

    private void PathfindToTile(WorldEntity player, int tileX, int tileY)
    {
        if (!MapPreloaded || MapFile is null)
            return;

        //click-to-walk obeys collision even for GMs; only arrow keys let a GM clip walls
        var raw = Pathfinder.FindPathToTile(
            player.TileX,
            player.TileY,
            tileX,
            tileY,
            MapFile.Width,
            MapFile.Height,
            GetPathfindingBlockedPoints(player),
            IsPlanWalkable,
            isTileWarp: (x, y) => WarpData.HasWarpAt(CurrentMapId, x, y));

        if (raw is null || raw.Count == 0)
        {
            Pathfinding.Path = raw;

            return;
        }

        //truncate at the first closed door so the walker stops at the tile right before it, facing the door;
        //the door tile itself is not included in the truncated path
        var points = raw.ToArray();

        for (var i = 0; i < points.Length; i++)
        {
            if (!IsClosedDoorAt(points[i].X, points[i].Y))
                continue;

            if (i == 0)
            {
                Pathfinding.Path = null;

                return;
            }

            var truncated = new Stack<IPoint>(i);

            for (var j = i - 1; j >= 0; j--)
                truncated.Push(points[j]);

            Pathfinding.Path = truncated;

            return;
        }

        Pathfinding.Path = raw;
    }

    private void PathfindToEntity(WorldEntity player, WorldEntity target)
    {
        if (!MapPreloaded || MapFile is null)
            return;

        //click-to-follow obeys collision even for GMs; arrow keys are the only wall-clip path
        var path = Pathfinder.FindPathToEntity(
            player.TileX,
            player.TileY,
            target.TileX,
            target.TileY,
            MapFile.Width,
            MapFile.Height,
            GetPathfindingBlockedPoints(player),
            IsPlanWalkable,
            out var alreadyAdjacent,
            isTileWarp: (x, y) => WarpData.HasWarpAt(CurrentMapId, x, y));

        //already adjacent: no path to walk, but keep TargetEntityId so the Update loop's auto-follow
        //branch turns and assails next tick. Calling Pathfinding.Clear() here would wipe the target entity
        //that OnRootDoubleClick just set, breaking double-right-click follow on neighbors.
        if (alreadyAdjacent)
            Pathfinding.Path = null;
        else if (path is null || path.Count == 0)
        {
            Pathfinding.Path = path;
        }
        else
        {
            var points = path.ToArray();

            for (var i = 0; i < points.Length; i++)
            {
                if (!IsClosedDoorAt(points[i].X, points[i].Y))
                    continue;

                if (i == 0)
                {
                    Pathfinding.Path = null;

                    return;
                }

                var truncated = new Stack<IPoint>(i);

                for (var j = i - 1; j >= 0; j--)
                    truncated.Push(points[j]);

                Pathfinding.Path = truncated;

                return;
            }

            Pathfinding.Path = path;
        }
    }
    #endregion
}