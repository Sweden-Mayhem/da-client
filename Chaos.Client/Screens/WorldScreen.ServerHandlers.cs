#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Data;
using Chaos.Client.Data.Repositories;
using Chaos.Client.Data.Utilities;
using Chaos.Client.Extensions;
using Chaos.Client.Models;
using Chaos.Client.Networking;
using Chaos.Client.Networking.Definitions;
using Chaos.Client.Rendering.Models;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Extensions.Common;
using Chaos.Geometry.Abstractions.Definitions;
using Chaos.Networking.Entities.Server;
using DALib.Drawing;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    #region Server Event Handlers
    //--- entity display / removal ---

    private void HandleDisplayAisling(DisplayAislingArgs args)
    {
        //update player name in hud when the player's own aisling is displayed
        if (args.Id == Game.Connection.AislingId)
        {
            WorldState.PlayerName = args.Name;
            UpdateHuds(HudOps.SetPlayerName, args.Name);
            UpdateHuds(HudOps.SetServerName, Game.Connection.ServerName);
            DataContext.LocalPlayerSettings.Initialize(args.Name);
            LoadPlayerFamilyList();
            LoadPlayerFriendList();
            LoadPlayerMacros();
            WorldState.ReloadChants();
            PlayerPortrait = LoadPortraitFile(args.Name);
            StatusBook.SetProfileText(LoadProfileText());
        }

        //check for idle animation ("04") frames on this aisling's body
        var entity = WorldState.GetEntity(args.Id);

        if (entity?.Appearance is { } appearance)
        {
            entity.IdleAnimFrameCount = Game.AislingRenderer.GetIdleAnimFrameCount(in appearance);

            //start idle cycling if entity is currently idle
            if (entity.AnimState == EntityAnimState.Idle)
                AnimationSystem.ResetToIdle(entity);
        }
    }

    private void HandleRemoveEntity(uint id)
    {
        //capture creature sprite for death dissolve before removing from worldstate
        var entity = WorldState.GetEntity(id);

        if (entity is { Type: ClientEntityType.Creature })
            CreateDyingEffect(entity);

        //clean up aisling composited texture cache
        Game.AislingRenderer.RemoveCachedEntity(id);

        //clean up all overlay caches (name tag, chat bubble, health bar, chant)
        Overlays.RemoveEntity(id);

        //clean up cached debug label texture
        DebugRenderer.RemoveEntity(id);

        //remove entity from worldstate (chaosgame skips removal when worldscreen is active)
        WorldState.RemoveEntity(id);
    }

    private void CreateDyingEffect(WorldEntity entity)
    {
        var creatureRenderer = Game.CreatureRenderer;
        var animInfo = creatureRenderer.GetAnimInfo(entity.SpriteId);

        if (animInfo is null)
            return;

        var info = animInfo.Value;
        (var frameIndex, var flip) = AnimationSystem.GetCreatureFrame(entity, in info);

        var spriteFrame = creatureRenderer.GetFrame(entity.SpriteId, frameIndex);

        if (spriteFrame is null)
            return;

        var frame = spriteFrame.Value;

        var dyingEffect = new EntityRemovalAnimation(
            Device,
            frame.Texture,
            entity.TileX,
            entity.TileY,
            frame.CenterX,
            frame.CenterY,
            frame.Left,
            frame.Top,
            flip);

        WorldState.DyingEffects.Add(dyingEffect);
    }

    //--- movement ---

    private void PredictAndWalk(WorldEntity player, Direction direction)
    {
        //bounds check: don't walk off the map edge
        (var dx, var dy) = direction.ToTileOffset();
        var newX = player.TileX + dx;
        var newY = player.TileY + dy;

        if (MapFile is null || (newX < 0) || (newY < 0) || (newX >= MapFile.Width) || (newY >= MapFile.Height))
            return;

        //swimming gate. retail behavior, off by default, toggled via GlobalSettings.RequireSwimmingSkill
        if (GlobalSettings.RequireSwimmingSkill
            && player.IsOnSwimmingTile
            && !IsGameMaster
            && !WorldState.SkillBook.HasSkillByName("swimming"))
        {
            WorldState.Chat.AddMessage("You need to learn how to swim.", Color.White, ChatChannel.Public);

            return;
        }

        //collision check. IsTilePassable already returns true under NoClip, so a noclip GM still phases through;
        //a NORMAL gm is blocked exactly like the server blocks them. (Gating on !IsGameMaster instead made GMs
        //fire walk packets straight into walls - the server refused every one, which both rubber-banded the GM and
        //hammered the speed-walk detector, the root of "Admin gets stuck by the fence".)
        if (!IsTilePassable(newX, newY))
            return;

        //closed door safety net; keyboard movement should already be gated in MoveOrTurn, but
        //this catches the queued-walk and path-executor paths too
        if (IsClosedDoorAt(newX, newY))
            return;

        Game.Connection.Walk(direction);

        //remember where we predicted this step lands, so the matching server response can be reconciled in order
        PredictedWalkDests.Enqueue((newX, newY));

        //predict position locally
        player.TileX = newX;
        player.TileY = newY;
        WorldState.MarkSortDirty();

        var walkFrames = player.UsesCreatureWalkTiming ? Game.CreatureRenderer.GetWalkFrameCount(player.SpriteId) : null;

        AnimationSystem.StartWalk(
            player,
            direction,
            player.UsesCreatureWalkTiming,
            true,
            walkFrames);
        UpdateHuds(HudOps.SetCoords, player.TileX, player.TileY);
    }

    private void HandleClientWalkResponse(Direction direction, int oldX, int oldY)
    {
        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        //the server's authoritative tile for this walk = its source + the direction it moved
        (var dx, var dy) = direction.ToTileOffset();
        var serverX = oldX + dx;
        var serverY = oldY + dy;

        //this response confirms the OLDEST predicted walk (FIFO over the same stream)
        if (PredictedWalkDests.Count > 0)
        {
            var predicted = PredictedWalkDests.Dequeue();

            //in sync: the server landed exactly where we predicted this step would. our local state already
            //reflects it (and any further-ahead predictions stay queued for their own responses). Nothing to do.
            if ((predicted.X == serverX) && (predicted.Y == serverY))
                return;

            //DIVERGENCE: the server ended up somewhere our prediction did not (a refused step left a stale
            //prediction in the queue, or a server move slipped in). Our whole predicted chain is now wrong, so
            //drop the rest and fall through to a hard resync onto the server's tile. This is the self-heal that
            //stops a one-tile drift from rubber-banding every subsequent step forever.
            PredictedWalkDests.Clear();
        }

        //no prediction (genuine server-initiated walk: push tile, knockback, teleport) OR a divergence resync:
        //snap to the server's tile and animate the step.
        QueuedWalkDirection = null;

        player.TileX = serverX;
        player.TileY = serverY;
        WorldState.MarkSortDirty();

        var walkFrames = player.UsesCreatureWalkTiming ? Game.CreatureRenderer.GetWalkFrameCount(player.SpriteId) : null;

        AnimationSystem.StartWalk(
            player,
            direction,
            player.UsesCreatureWalkTiming,
            true,
            walkFrames);

        UpdateHuds(HudOps.SetCoords, serverX, serverY);

        //a server-initiated move (push, knockback) invalidates the PATH but must not drop the CHASE - the retarget
        //tick re-paths from wherever we landed
        var chase = Pathfinding.TargetEntityId;
        Pathfinding.Clear();

        if (chase is { } chaseId)
            Pathfinding.SetEntityTarget(chaseId);
    }

    //--- attributes ---

    private void HandleAttributes(AttributesArgs args)
        => IsGameMaster = args.StatUpdateType.HasFlag(StatUpdateType.GameMasterA)
                          || args.StatUpdateType.HasFlag(StatUpdateType.GameMasterB);

    //--- chat / messages ---

    private void HandleDisplayPublicMessage(DisplayPublicMessageArgs args)
    {
        var entityExists = WorldState.GetEntity(args.SourceId) is not null;

        if (args.PublicMessageType == PublicMessageType.Chant)
        {
            if (entityExists)
                Overlays.AddChantOverlay(args.SourceId, args.Message);

            return;
        }

        //custom-channel messages arrive as a public "shout" from no entity (source uint.MaxValue), formatted
        //"[!name] sender: msg". Route them to their own chat tab (keyed by the "!name") instead of the Public tab.
        if ((args.SourceId == uint.MaxValue) && TryParseChannelName(args.Message, out var chanName))
        {
            //honor the channel's set colour ("{=x" prefix) for the whole line; fall back to shout colour if unknown/invalid
            var chColor = ChannelMessageColor(args.Message) ?? TextColors.Shout;
            WorldState.Chat.AddMessage(args.Message, chColor, ChatChannel.Public, chanName);

            return;
        }

        var entity = WorldState.GetEntity(args.SourceId);
        var isNpc = entity is not null && entity.Type is not ClientEntityType.Aisling;

        var color = args.PublicMessageType switch
        {
            PublicMessageType.Shout => TextColors.Shout,
            _                       => LegendColors.White
        };

        if (!isNpc || ClientSettings.RecordNpcChat)
            WorldState.Chat.AddMessage(args.Message, color, ChatChannel.Public);

        if (entity is null)
            return;

        var isShout = args.PublicMessageType == PublicMessageType.Shout;

        //while an NPC dialog is open, suppress over-head bubbles entirely (no bubble, no chat-bubble sound) so the
        //conversation isn't cluttered/noisy; the message still goes to the chat window above.
        if (!NpcSession.Visible)
        {
            const int BUBBLE_MAX = 100;
            var bubbleText = args.Message.Length > BUBBLE_MAX ? args.Message[..BUBBLE_MAX] + "..." : args.Message;
            Overlays.AddChatBubble(args.SourceId, bubbleText, isShout);
        }
    }

    //pulls the sender out of a RECEIVED whisper "[Sender]: msg". Returns null for the sent echo "[Name]> msg", system
    //whisper notices, or any other shape, so we only ever reply to a real incoming whisper.
    private static string? ExtractWhisperSender(string message)
    {
        if (string.IsNullOrEmpty(message) || (message[0] != '['))
            return null;

        var close = message.IndexOf(']');

        if ((close <= 1) || (close + 1 >= message.Length) || (message[close + 1] != ':'))
            return null;

        var name = message[1..close];

        foreach (var c in name)
            if (!char.IsLetter(c))
                return null;

        return name;
    }

    private void HandleServerMessage(ServerMessageArgs args)
    {
        //group/guild chat reuses the channel system internally, so forming a group/guild sends a "You have joined channel
        //!group"/"!guild" (and a "left channel ..." on disband) notice using the channel override name. That is pure
        //plumbing - the player already gets "You form a group with X" - and naming an internal "!group" channel only
        //confuses (it reads like a user channel). Drop just those reserved notices; real channel joins still show.
        if (IsReservedChannelNotice(args.Message))
            return;

        switch (args.ServerMessageType)
        {
            case ServerMessageType.Whisper:
                WorldState.Chat.AddMessage(args.Message, TextColors.Whisper, ChatChannel.Whisper);
                WorldState.Chat.AddOrangeBarMessage(args.Message, TextColors.Whisper);

                if (ExtractWhisperSender(args.Message) is { } whisperSender)
                {
                    ActiveChatInput?.SeedWhisperTargetIfEmpty(whisperSender);

                    if (ClientSettings.WhisperSound)
                        Game.SoundSystem.PlaySound(158);
                }

                break;

            case ServerMessageType.GroupChat:
            {
                //the server formats group chat with the channel's internal override tag "[!group]"; show it as "[Group]"
                var groupMsg = args.Message.Replace("[!group]", "[Group]", StringComparison.OrdinalIgnoreCase);
                WorldState.Chat.AddMessage(groupMsg, TextColors.GroupChat, ChatChannel.Group);
                WorldState.Chat.AddOrangeBarMessage(groupMsg, TextColors.GroupChat);

                break;
            }

            case ServerMessageType.GuildChat:
            {
                var guildMsg = args.Message.Replace("[!guild]", "[Guild]", StringComparison.OrdinalIgnoreCase);
                WorldState.Chat.AddMessage(guildMsg, TextColors.GuildChat, ChatChannel.Guild);
                WorldState.Chat.AddOrangeBarMessage(guildMsg, TextColors.GuildChat);

                break;
            }

            case ServerMessageType.ActiveMessage:
                WorldState.Chat.AddMessage(args.Message, Color.Orange, ChatChannel.System);
                WorldState.Chat.AddOrangeBarMessage(args.Message);

                break;

            case ServerMessageType.OrangeBar1
                 or ServerMessageType.OrangeBar2
                 or ServerMessageType.OrangeBar3
                 or ServerMessageType.AdminMessage
                 or ServerMessageType.OrangeBar5:
                //the server ALSO re-broadcasts every custom-channel chat line here ("[!name] user: text"); it is already
                //shown in its own channel tab (via the DisplayPublicMessage path), so don't duplicate it into System/orange.
                if (TryParseChannelName(args.Message, out _))
                    break;

                WorldState.Chat.AddMessage(args.Message, Color.Orange, ChatChannel.System);
                WorldState.Chat.AddOrangeBarMessage(args.Message);

                break;

            case ServerMessageType.PersistentMessage:
                UpdateHuds(HudOps.ShowPersistentMessage, args.Message);

                break;

            //the text popups own the screen front-and-center: an open town map leaves first (and cannot reopen
            //under them - ShowTownMap gates on TextPopup.Visible)
            case ServerMessageType.ScrollWindow:
                TownMapControl.Hide();
                TextPopup.Show(args.Message);

                break;

            case ServerMessageType.NonScrollWindow:
                TownMapControl.Hide();
                TextPopup.Show(args.Message, PopupStyle.NonScroll);

                break;

            case ServerMessageType.WoodenBoard:
                TownMapControl.Hide();
                TextPopup.Show(args.Message, PopupStyle.Wooden);

                break;

            case ServerMessageType.UserOptions:
                ParseUserOptions(args.Message);

                break;

            case ServerMessageType.ClosePopup:
                TextPopup.Hide();

                break;

            default:
                WorldState.Chat.AddMessage(args.Message, Color.Orange, ChatChannel.System);
                WorldState.Chat.AddOrangeBarMessage(args.Message);

                break;
        }
    }

    private void ParseUserOptions(string message)
    {
        if (message.Length < 2)
            return;

        //single option toggle response: "{digit}{description,-25}:{on/off,-3}"
        if (message[0] != '0')
        {
            if (!char.IsDigit(message[0]))
                return;

            var optionIndex = message[0] - '1';

            if (optionIndex is < 0 or >= UserOptions.SETTING_COUNT)
                return;

            ParseOptionEntry(optionIndex, message[1..]);

            return;
        }

        //full request response: "0{opt1_desc}:{state}\t{opt2_desc}:{state}\t..."
        //leading '0' prefix, then 8 options in order with digits stripped
        var entries = message[1..]
            .Split('\t', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; (i < entries.Length) && (i < 8); i++)
            ParseOptionEntry(i, entries[i]);
    }

    private void ParseOptionEntry(int optionIndex, string entry)
    {
        if (!UserOptions.IsServerSetting(optionIndex))
            return;

        var colonIdx = entry.LastIndexOf(':');

        if (colonIdx < 1)
            return;

        var stateStr = entry[(colonIdx + 1)..]
            .Trim();
        var isOn = stateStr.StartsWithI("ON");

        //server settings: use the full formatted text as the display name (includes :on/:off)
        SettingsDialog.SetSettingName(optionIndex, entry.TrimEnd());
        WorldState.UserOptions.SetValue(optionIndex, isOn);
    }

    //--- npc dialog / menu ---

    private void HandleDialogChanged()
    {
        var dialog = WorldState.NpcInteraction.CurrentDialog;

        if (dialog is null || (dialog.DialogType == DialogType.CloseDialog))
        {
            NpcSession.HideAll();
            //keep CameraFocusEntityId so the spotlit speaker can fade out during the close; the pan eases back because the
            //pan target is gated on NpcSession.Visible (now false). It is overwritten on the next dialog open.

            return;
        }

        //play the dialog-open sound only when the session goes from hidden to shown - so paging through a conversation
        //(or a dialog->menu hop within the same open session) does not replay it. Covers NPC dialogs and read-only signs
        //(the server sends those as a one-page text dialog through this same path).
        var wasOpen = NpcSession.Visible;
        NpcSession.ShowDialog(dialog);

        //resolve the speaker BEFORE rendering the portrait so RenderNpcSessionPortrait can skip the avatar when found
        if (!wasOpen)
        {
            ResolveCameraFocus();

            //a dialog owns the screen: an open town map leaves immediately (and cannot reopen under it)
            TownMapControl.Hide();
        }

        RenderNpcSessionPortrait();

        if (!wasOpen)
            Game.SoundSystem.PlaySound(SoundSystem.SoundDialog);
    }

    private void HandleMenuChanged()
    {
        var menu = WorldState.NpcInteraction.CurrentMenu;

        if (menu is null)
            return;

        var wasOpen = NpcSession.Visible;
        NpcSession.ShowMenu(menu);

        //resolve the speaker BEFORE rendering the portrait so RenderNpcSessionPortrait can skip the avatar when found
        if (!wasOpen)
        {
            ResolveCameraFocus();

            //a dialog owns the screen: an open town map leaves immediately (and cannot reopen under it)
            TownMapControl.Hide();
        }

        RenderNpcSessionPortrait();

        if (!wasOpen)
            Game.SoundSystem.PlaySound(SoundSystem.SoundDialog);
    }

    private void RenderNpcSessionPortrait()
    {
        //when the speaker is shown in-world (the camera panned to it + it is spotlit), skip the dialog's own portrait/avatar
        //entirely - we are already showing the real NPC, so the little avatar tile is redundant
        if (SpeakerResolved)
        {
            NpcSession.SetPortrait(null, false);

            return;
        }

        //phase 1: try full-art illustration spf. The original DA client attempts this unconditionally for every
        //dialog/menu packet. The only gate is whether the NPC name matches an entry in the merged illustration
        //metadata (npci.tbl inside npcbase.dat + server-pushed NPCIllust metafile). IllustrationIndex picks which
        //filename variant to load when a name has multiple.
        if (!string.IsNullOrEmpty(NpcSession.NpcName))
        {
            var illustTexture = TryLoadNpcIllustration(NpcSession.NpcName, NpcSession.IllustrationIndex);

            if (illustTexture is not null)
            {
                NpcSession.SetPortrait(illustTexture, true);

                return;
            }
        }

        //phase 2: fall back to entity sprite portrait based on entitytype
        if (NpcSession.PortraitSpriteId == 0)
        {
            NpcSession.SetPortrait(null, false);

            return;
        }

        switch (NpcSession.SourceEntityType)
        {
            case EntityType.Creature:
            {
                var frame = RenderCreaturePortrait(NpcSession.PortraitSpriteId);

                if (frame is not null)
                    NpcSession.SetPortrait(frame.Value);
                else
                    NpcSession.SetPortrait(null, false);

                break;
            }

            case EntityType.Item:
            {
                var sprite = Game.ItemRenderer.GetSprite(NpcSession.PortraitSpriteId, (byte)NpcSession.PortraitColor);
                NpcSession.SetPortrait(sprite?.Texture, false);

                break;
            }

            default:
                NpcSession.SetPortrait(null, false);

                break;
        }
    }

    /// <summary>
    ///     Attempts to load a full-art NPC illustration SPF from <c>npcbase.dat</c>. Looks up <paramref name="npcName" />
    ///     in the merged illustration metadata (npci.tbl + server NPCIllust metafile) and picks the filename at
    ///     <paramref name="variant" />. Returns null if the NPC has no entries, the variant index is out of range,
    ///     or the SPF file is missing.
    /// </summary>
    private static Texture2D? TryLoadNpcIllustration(string npcName, byte variant)
    {
        var illustMeta = DataContext.MetaFiles.GetNpcIllustrationMetadata();

        if (!illustMeta.Illustrations.TryGetValue(npcName, out var filenames) || (filenames.Count == 0))
            return null;

        if (variant >= filenames.Count)
            return null;

        var spfFileName = filenames[variant];

        if (!DatArchives.Npcbase.TryGetValue(spfFileName, out var entry))
            return null;

        var spf = SpfFile.FromEntry(entry);

        if (spf.Count == 0)
            return null;

        using var image = SpfRenderer.RenderFrame(spf, 0);

        return TextureConverter.ToTexture2D(image);
    }

    

    private SpriteFrame? RenderCreaturePortrait(ushort spriteId)
    {
        var info = Game.CreatureRenderer.GetAnimInfo(spriteId);

        if (info is null)
            return null;

        (var frameIndex, _) = AnimationSystem.GetCreatureIdleFrame(info.Value, Direction.Down);

        return Game.CreatureRenderer.GetFrame(spriteId, frameIndex);
    }

    private void HandleRefreshResponse()
        =>

            //server acknowledged the refresh request; re-center camera
            FollowPlayerCamera();

    //--- exchange ---

    private void HandleExchangeAmountRequested(byte fromSlot)
    {
        //ItemAmount is hosted in its own ScaleHost now (scaled + centered each frame by AnchorPopups), so it no longer
        //positions itself relative to the exchange window.
        ItemAmount.ShowForSlot(fromSlot);

        //surface the slot's hover description (e.g. "Apple[ 10 ]") in the HUD bar while the popup
        //is open. matches retail behavior of pinning the operated-on item's tooltip text.
        WorldHud.SetDescription(WorldState.Inventory.GetSlot(fromSlot).Name);
    }

    private void HandleExchangeClosed(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            ExchangeResultPopup.Show(message);
    }

    //--- board / mail ---

    private void HandleBoardResponse(string message, bool success)
    {
        if (success)
            PendingBoardSuccessAction?.Invoke();

        PendingBoardSuccessAction = null;
        BoardResponsePopup.Show(message);
    }

    private void HandleRedirectReceived(RedirectInfo _) => RedirectInProgress = true;

    private void HandleBoardListReceived()
    {
        var boards = WorldState.Board.AvailableBoards;

        if (boards is null or { Count: 0 })
            return;

        WorldState.Board.OpenSession();

        var serverEntries = boards.Select(b => (b.BoardId, b.Name)).ToList();
        BoardList.ShowBoards(serverEntries);
    }

    private void HandleBoardPostListChanged()
    {
        var board = WorldState.Board;
        var posts = board.Posts.ToList();

        //ensure session is open. server can send board data directly (e.g. tile click) without going through BoardList
        if (!board.IsSessionOpen)
            board.OpenSession();

        if (board.IsPublicBoard)
        {
            if (LoadingMoreBoardPosts && ArticleList.Visible && (ArticleList.BoardId == board.BoardId))
                ArticleList.AppendEntries(posts);
            else
            {
                HideAllBoardControls();
                ArticleList.ShowArticles(board.BoardId, posts);
                ArticleList.SetHighlightEnabled(IsGameMaster);
            }
        } else
        {
            if (LoadingMoreBoardPosts && MailList.Visible && (MailList.BoardId == board.BoardId))
                MailList.AppendEntries(posts);
            else
            {
                HideAllBoardControls();
                MailList.ShowMailList(board.BoardId, posts);
            }
        }

        LoadingMoreBoardPosts = false;
    }

    private void HandleBoardPostViewed()
    {
        var post = WorldState.Board.CurrentPost;

        if (post is not { } p)
            return;

        var board = WorldState.Board;

        //ensure session is open. server can send a post directly without going through BoardList
        if (!board.IsSessionOpen)
            board.OpenSession();

        HideAllBoardControls();

        if (board.IsPublicBoard)
        {
            ArticleRead.BoardId = board.BoardId;

            ArticleRead.ShowArticle(
                p.PostId,
                p.Author,
                p.MonthOfYear,
                p.DayOfMonth,
                p.Subject,
                p.Message,
                board.EnablePrevButton);
        } else
        {
            MailRead.BoardId = board.BoardId;

            MailRead.ShowMail(
                p.PostId,
                p.Author,
                p.MonthOfYear,
                p.DayOfMonth,
                p.Subject,
                p.Message,
                board.EnablePrevButton);
        }
    }

    //--- group ---

    private void HandleGroupInviteReceived()
    {
        var invite = WorldState.GroupInvite.Current;

        if (invite is null)
            return;

        var sourceName = invite.SourceName;

        switch (invite.ServerGroupSwitch)
        {
            case ServerGroupSwitch.Invite:
            {
                WorldState.Chat.AddOrangeBarMessage($"{sourceName} invites you to join a group.");

                if (!ClientSettings.UseGroupWindow)
                {
                    Game.Connection.SendGroupInvite(ClientGroupSwitch.AcceptInvite, sourceName);

                    break;
                }

                ShowGroupInvitePopup($"{sourceName} invites you to join a group.", sourceName);

                break;
            }

            case ServerGroupSwitch.RequestToJoin:
            {
                // Retail behavior: the leader's client silently auto-forwards as TryInvite
                // with no UI prompt (verified). The orange-bar notice is a QoL addition retail omits.
                WorldState.Chat.AddOrangeBarMessage($"{sourceName} wants to join your group.");
                Game.Connection.SendGroupInvite(ClientGroupSwitch.TryInvite, sourceName);

                break;
            }

            case ServerGroupSwitch.ShowGroupBox:
            {
                if (invite.GroupBoxInfo is not { } groupBoxInfo)
                {
                    WorldState.Chat.AddOrangeBarMessage($"{sourceName} has an open group box.");

                    break;
                }

                if (sourceName.EqualsI(WorldState.PlayerName))
                {
                    WorldState.Group.MarkGroupBoxActive();
                    GroupPanel.ShowRecruitOwnerEdit(groupBoxInfo);
                } else
                    GroupBoxViewer.ShowAsViewer(sourceName, groupBoxInfo);

                break;
            }
        }
    }

    private void ShowGroupInvitePopup(string message, string sourceName)
    {
        if (Root is null)
            return;

        var popup = new OkPopupMessageControl(true)
        {
            Name = "GroupInvitePopup"
        };
        Root.AddChild(popup);

        popup.OnOk += () =>
        {
            Game.Connection.SendGroupInvite(ClientGroupSwitch.AcceptInvite, sourceName);
            popup.Hide();
            Root.RemoveChild(popup.Name);
        };

        popup.OnCancel += () =>
        {
            popup.Hide();
            Root.RemoveChild(popup.Name);
        };

        popup.Show(message);
    }

    //--- profiles ---

    private void HandleEditableProfileRequest() => Game.Connection.SendEditableProfile(PlayerPortrait, StatusBook.GetProfileText());

    private static byte[] LoadPortraitFile(string name)
    {
        if (!DataContext.LocalPlayerSettings.IsInitialized || string.IsNullOrEmpty(name))
            return [];

        var jpgPath = DataContext.LocalPlayerSettings.GetFilePath($"{name}.jpg");

        if (File.Exists(jpgPath))
            return File.ReadAllBytes(jpgPath);

        var noExtPath = DataContext.LocalPlayerSettings.GetFilePath(name);

        if (File.Exists(noExtPath))
            return File.ReadAllBytes(noExtPath);

        return [];
    }

    private static string LoadProfileText()
    {
        if (!DataContext.LocalPlayerSettings.IsInitialized)
            return string.Empty;

        var profilePath = DataContext.LocalPlayerSettings.GetFilePath("profile.txt");

        return File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;
    }

    private static void SaveProfileText(string text)
    {
        if (!DataContext.LocalPlayerSettings.IsInitialized)
            return;

        var profilePath = DataContext.LocalPlayerSettings.GetFilePath("profile.txt");
        File.WriteAllText(profilePath, text);
    }

    private void HandleSelfProfile(SelfProfileArgs args)
    {
        WorldState.IsMaster = args.EnableMasterQuestMetaData;

        //remember guild membership so the chat window knows whether to show a Guild tab (profile is fetched on demand)
        WorldState.GuildName = args.GuildName ?? string.Empty;

        //nation emblem and text
        StatusBook.SetNation((byte)args.Nation);

        //social status display
        var status = SocialStatusPicker.CurrentStatus;
        StatusBook.SetEmoticonState((byte)status, UiComponentRepository.GetSocialStatusName(status));

        //fill and show the status book
        StatusBook.SetPlayerInfo(
            WorldHud.PlayerName,
            args.DisplayClass,
            args.GuildName ?? string.Empty,
            args.GuildRank ?? string.Empty,
            args.Title ?? string.Empty);

        //legend marks
        var marks = args.LegendMarks
                        .Select(m => new LegendMarkEntry(
                            m.Text,
                            MapMarkColor(m.Color),
                            (byte)m.Icon,
                            m.Key))
                        .ToList();

        StatusBook.SetLegendMarks(marks);

        //ability metadata (skills/spells from sclass file). Cached so the hotbar + K/P book hover tooltips can look up a
        //skill/spell's detail by name without re-parsing the metafile each hover. The class is remembered so the
        //metadata-sync handler can re-parse: at login this can run BEFORE the sync lands the fresh SClass files.
        PlayerBaseClass = (byte)args.BaseClass;
        var abilityMetadata = DataContext.MetaFiles.GetAbilityMetadata((byte)args.BaseClass);
        PlayerAbilityMetadata = abilityMetadata;

        if (abilityMetadata is not null)
            StatusBook.SetAbilityMetadata(abilityMetadata);
        else
            StatusBook.ClearSkills();

        //event metadata (quests from sevent files)
        var eventMetadata = DataContext.MetaFiles.GetEventMetadata();

        if (eventMetadata.Count > 0)
        {
            //build a set of completed event ids from legend marks for o(1) lookup
            var completedEventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mark in args.LegendMarks)
                completedEventIds.Add(mark.Key);

            StatusBook.SetEvents(
                eventMetadata,
                completedEventIds,
                args.BaseClass,
                args.EnableMasterQuestMetaData);
        } else
            StatusBook.ClearEvents();

        //family info
        StatusBook.SetFamilyInfo(args.SpouseName ?? string.Empty);
        LoadPlayerFamilyList();

        //paperdoll: render the player's full aisling at south-facing idle
        var playerEntity = WorldState.GetPlayerEntity();

        if (playerEntity?.Appearance is { } appearance)
            StatusBook.SetPaperdoll(Game.AislingRenderer, in appearance);

        //group open state. server is source of truth, sync all ui
        StatusBook.SetGroupOpen(args.GroupOpen);
        WorldState.UserOptions.SetValue(12, args.GroupOpen);
        WorldHud.SetGroupOpen(args.GroupOpen);

        //group members: parse groupstring into state, ui subscribes via event
        if (!string.IsNullOrEmpty(args.GroupString))
        {
            if (args.GroupString.StartsWithI(GROUP_MEMBERS_PREFIX))
                WorldState.Group.ParseAndSet(args.GroupString);
            else if (args.GroupString.StartsWithI(SPOUSE_PREFIX))
            {
                var spouseName = args.GroupString[SPOUSE_PREFIX.Length..]
                                     .Trim();
                StatusBook.SetFamilyInfo(spouseName);
                WorldState.Group.Clear();
            } else
                WorldState.Group.Clear();
        } else
            WorldState.Group.Clear();

        if (GroupHighlightRequested)
        {
            GroupHighlightRequested = false;
            ApplyGroupHighlight();
        } else if (SelfProfileRequested)
        {
            SelfProfileRequested = false;
            ShowStatusBook(SelfProfileRequestedTab);
            SelfProfileRequestedTab = StatusBookTab.Equipment;
        }
    }

    private void ApplyGroupHighlight()
    {
        GroupHighlightedIds.Clear();
        Game.AislingRenderer.ClearGroupTintCache();
        Game.CreatureRenderer.ClearTintCaches();

        var members = WorldState.Group.Members;

        if (members.Count == 0)
            return;

        var memberSet = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);

        foreach (var entity in WorldState.GetSortedEntities())
        {
            if (entity.Type != ClientEntityType.Aisling)
                continue;

            if ((entity.Id != WorldState.PlayerEntityId) && !string.IsNullOrEmpty(entity.Name) && memberSet.Contains(entity.Name))
                GroupHighlightedIds.Add(entity.Id);
        }

        if (GroupHighlightedIds.Count > 0)
            GroupHighlightTimer = 1000f;
    }

    private void ShowStatusBook(StatusBookTab tab = StatusBookTab.Equipment)
    {
        StatusBook.RefreshEquipment();

        if (WorldState.Attributes.Current is { } attrs)
        {
            StatusBook.UpdateEquipmentStats(
                attrs.Str,
                attrs.Int,
                attrs.Wis,
                attrs.Con,
                attrs.Dex,
                attrs.Ac);

            StatusBook.SetUnspentStatPoints(attrs.UnspentPoints);
        }

        StatusBook.SwitchTab(tab);

        //magnify (dynamic WindowScale); center the host in the native window only the FIRST time it opens, then leave it
        //where the player dragged it (until restart). The book sits at the host's origin.
        if (StatusBookHost is not null)
        {
            StatusBookHost.Scale = ClientSettings.EffectiveWindowScale;

            if (!StatusBookCentered)
            {
                StatusBookHost.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
                StatusBookCentered = true;
            }
        }

        StatusBook.Show();
    }

    private void HandleOtherProfile(OtherProfileArgs args)
    {
        var marks = args.LegendMarks
                        .Select(m => new LegendMarkEntry(
                            m.Text,
                            MapMarkColor(m.Color),
                            (byte)m.Icon,
                            m.Key))
                        .ToList();

        OtherProfile.Show(args, marks, Game.AislingRenderer);
    }

    //--- animations / effects / sound ---

    private void HandleBodyAnimation(BodyAnimationArgs args)
    {
        var entity = WorldState.GetEntity(args.SourceId);

        if (entity is null)
            return;

        //emotes are body animations; ignore if any body anim or emote overlay is already playing
        if ((entity.AnimState == EntityAnimState.BodyAnim) || (entity.ActiveEmoteFrame >= 0))
            return;

        //creatures use their mpffile attack frame counts; aislings use epf suffix-based frame counts
        if (entity.Type == ClientEntityType.Creature)
        {
            var animInfo = Game.CreatureRenderer.GetAnimInfo(entity.SpriteId);

            if (animInfo is { } info)
                AnimationSystem.StartCreatureBodyAnimation(
                    entity,
                    args.BodyAnimation,
                    args.AnimationSpeed,
                    in info);
        } else
        {
            (_, var framesPerDir, _, _) = AnimationSystem.ResolveBodyAnimParams(args.BodyAnimation);

            if (framesPerDir > 0)
            {
                if (entity.Appearance.HasValue && !Game.AislingRenderer.HasArmorAnimation(entity.Appearance.Value, args.BodyAnimation))
                    return;

                AnimationSystem.StartBodyAnimation(entity, args.BodyAnimation, args.AnimationSpeed);
            } else if (DataUtilities.IsEmote(args.BodyAnimation))
            {
                //emote overlay: face/bubble icon composited into the aisling sprite
                (var startFrame, var frameCount, var durationMs) = AnimationSystem.ResolveEmoteFrames(args.BodyAnimation);

                if (startFrame >= 0)
                {
                    entity.EmoteStartFrame = startFrame;
                    entity.EmoteFrameCount = frameCount;
                    entity.ActiveEmoteFrame = startFrame;
                    entity.EmoteDurationMs = durationMs;
                    entity.EmoteElapsedMs = 0;
                    entity.EmoteRemainingMs = durationMs;
                }
            }
        }

        if (args.Sound.HasValue)
            Game.SoundSystem.PlaySound(args.Sound.Value);
    }

    //TargetAnimation values in [PROJECTILE_ANIMATION_BASE, PROJECTILE_ANIMATION_MAX_EXCLUSIVE) are MEFC projectiles;
    //the meffect id is recovered by subtracting the base.
    private const int PROJECTILE_ANIMATION_BASE = 10000;
    private const int PROJECTILE_ANIMATION_MAX_EXCLUSIVE = 12000;

    private void HandleAnimation(AnimationArgs args)
    {
        if (args is { SourceId: > 0, TargetId: > 0, TargetAnimation: >= PROJECTILE_ANIMATION_BASE and < PROJECTILE_ANIMATION_MAX_EXCLUSIVE })
        {
            var meffectId = args.TargetAnimation - PROJECTILE_ANIMATION_BASE;
            CreateProjectile(meffectId, args.SourceId.Value, args.TargetId.Value);

            if (args is { SourceAnimation: > 0 })
                CreateEffect(args.SourceAnimation, args.AnimationSpeed, args.SourceId.Value);

            return;
        }

        //ground-targeted effect
        if (args is { TargetPoint: not null, TargetAnimation: > 0 })
            CreateEffect(
                args.TargetAnimation,
                args.AnimationSpeed,
                targetTileX: args.TargetPoint.Value.X,
                targetTileY: args.TargetPoint.Value.Y);

        //entity-targeted effect on target
        if (args is { TargetId: > 0, TargetAnimation: > 0 })
            CreateEffect(args.TargetAnimation, args.AnimationSpeed, args.TargetId.Value);

        //source-side effect (caster visual)
        if (args is { SourceId: > 0, SourceAnimation: > 0 })
            CreateEffect(args.SourceAnimation, args.AnimationSpeed, args.SourceId.Value);
    }

    private void CreateProjectile(int meffectId, uint sourceEntityId, uint targetEntityId)
    {
        var record = DataContext.Effects.GetMeffectRecord(meffectId);

        if (record is null)
            return;

        var sourceEntity = WorldState.GetEntity(sourceEntityId);
        var targetEntity = WorldState.GetEntity(targetEntityId);

        if (sourceEntity is null || targetEntity is null)
            return;

        if (MapFile is null)
            return;

        var sourceWorld = Camera.TileToWorld(sourceEntity.TileX, sourceEntity.TileY, MapFile.Height);
        var targetWorld = Camera.TileToWorld(targetEntity.TileX, targetEntity.TileY, MapFile.Height);

        var srcX = sourceWorld.X + DaLibConstants.HALF_TILE_WIDTH;
        var srcY = sourceWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;
        var tgtX = targetWorld.X + DaLibConstants.HALF_TILE_WIDTH;
        var tgtY = targetWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

        var dx = tgtX - srcX;
        var dy = tgtY - srcY;
        var distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance < 1f)
            return;

        //direction matches server's DirectionalRelationTo (tile space): Up=0, Right=1, Down=2, Left=3
        var direction = GetProjectileDirection(
            targetEntity.TileX - sourceEntity.TileX,
            targetEntity.TileY - sourceEntity.TileY);

        WorldState.ActiveProjectiles.Add(
            new Projectile
            {
                TargetEntityId = targetEntityId,
                MeffectId = meffectId,
                CurrentX = srcX,
                CurrentY = srcY,
                LastKnownTargetX = tgtX,
                LastKnownTargetY = tgtY,
                Step = record.Step,
                StepDelayMs = record.StepDelay,
                InitialDistance = distance,
                ArcRatioV = record.ArcRatioV,
                ArcRatioH = record.ArcRatioH,
                FramesPerDirection = record.FramesPerDirection,
                Direction = direction
            });
    }

    private void CreateEffect(
        int effectId,
        ushort animationSpeed,
        uint? targetEntityId = null,
        int? targetTileX = null,
        int? targetTileY = null)
    {
        var info = Game.EffectRenderer.GetEffectInfo(effectId);

        if (info is null)
            return;

        (var frameCount, var fileIntervalMs, var isEfa, var blendMode) = info.Value;

        //efa effects use the interval from the file; epf effects use the packet's animation speed
        float frameIntervalMs = isEfa
            ? fileIntervalMs > 0 ? fileIntervalMs : 50
            : animationSpeed > 0
                ? animationSpeed
                : 50;

        //cancel any existing effect on the same entity; only one effect per entity at a time
        if (targetEntityId.HasValue)
            WorldState.ActiveEffects.RemoveAll(e => e.TargetEntityId == targetEntityId);

        //capture the target entity's current tile as a FALLBACK position. An entity-targeted effect normally follows
        //the live entity (DrawEntityEffects), but if that entity is removed before the effect finishes - e.g. a
        //killing-blow spell on a monster that dies the same instant - the effect would vanish. With the tile recorded,
        //it keeps playing at the death spot (see DrawGroundEffectsAtDepth). The entity still exists here because the
        //animation packet is processed before the remove-entity packet.
        if (targetEntityId.HasValue && (targetTileX is null) && WorldState.GetEntity(targetEntityId.Value) is { } target)
        {
            targetTileX = target.TileX;
            targetTileY = target.TileY;
        }

        WorldState.ActiveEffects.Add(
            new Animation
            {
                EffectId = effectId,
                TargetEntityId = targetEntityId,
                TileX = targetTileX,
                TileY = targetTileY,
                FrameCount = frameCount,
                FrameIntervalMs = frameIntervalMs,
                BlendMode = blendMode
            });
    }

    private void HandleSound(SoundArgs args)
    {
        if (args.IsMusic)
            Game.SoundSystem.PlayMusic(args.Sound);
        else
            Game.SoundSystem.PlaySound(args.Sound);
    }

    //--- world / map / doors ---

    private void HandleWorldMap(WorldMapArgs args) => WorldMap.Show(args, CurrentMapName, CurrentMapId);

    private void HandleDoor(DoorArgs args)
    {
        if (MapFile is null)
            return;

        foreach (var door in args.Doors)
        {
            if ((door.X < 0) || (door.X >= MapFile.Width) || (door.Y < 0) || (door.Y >= MapFile.Height))
                continue;

            var tile = MapFile.Tiles[door.X, door.Y];

            if (door.Closed)
            {
                //restore closed tile: find the open tile currently set and swap it back
                var closedLeft = DoorTable.GetClosedTileId(tile.LeftForeground);
                var closedRight = DoorTable.GetClosedTileId(tile.RightForeground);

                if (closedLeft.HasValue)
                    tile.LeftForeground = closedLeft.Value;

                if (closedRight.HasValue)
                    tile.RightForeground = closedRight.Value;
            } else
            {
                //open door: find the closed tile and swap to open
                var openLeft = DoorTable.GetOpenTileId(tile.LeftForeground);
                var openRight = DoorTable.GetOpenTileId(tile.RightForeground);

                if (openLeft.HasValue)
                    tile.LeftForeground = openLeft.Value;

                if (openRight.HasValue)
                    tile.RightForeground = openRight.Value;
            }
        }
    }

    private void HandleMapChangePending()
    {
        //MapPreloaded gates every path request: the water/door tile lists still belong to the OLD map's dimensions,
        //so nothing may pathfind against them before FinalizeMapLoad rebuilds them
        MapPreloaded = false;
        LastHealthPercent.Clear(); //entity ids don't carry across maps; drop stale baselines so the hit cue stays correct
        ResetCameraFx();           //don't carry a shake or red flash across a warp

        QueuedWalkDirection = null;
        Pathfinding.Clear();
        TownMapControl.Hide();

        //a true map transition is the one place we discard outstanding predicted walks: any that were in flight
        //on the old map will not be confirmed on the new one. (the same-map F5 refresh path, which also runs
        //ClearTransientState, must NOT clear this - its predictions are still awaiting their responses.)
        PredictedWalkDests.Clear();
    }

    //--- health / effects / light ---

    //a channel message is "[!name] sender: msg" (an optional "{=c" colour code may lead). Pulls out the "!name" channel
    //key when the leading bracket holds a channel-prefixed name. Returns false for ordinary broadcasts.
    private static bool TryParseChannelName(string message, out string channelName)
    {
        channelName = string.Empty;

        var open = message.IndexOf('[');

        if (open is < 0 or > 3) //allow a short leading colour code ("{=c") before the bracket, nothing more
            return false;

        var close = message.IndexOf(']', open + 1);

        if (close <= open + 1)
            return false;

        var content = message[(open + 1)..close];

        if (!content.StartsWith('!'))
            return false;

        channelName = content;

        return true;
    }

    //the line colour from a leading "{=x" channel-colour code, or null if absent/unknown (then the caller uses a default)
    private static Color? ChannelMessageColor(string message)
        => (message.Length >= 3) && (message[0] == '{') && (message[1] == '=') ? TextRenderer.GetColorCode(message[2]) : null;

    //true for the internal group/guild channel join/leave notices ("... channel !group" / "... channel !guild"), which
    //are plumbing the player should never see (group/guild chat has its own tabs and its own "you formed/joined" lines).
    private static bool IsReservedChannelNotice(string message)
        => message.Contains("channel !group", StringComparison.OrdinalIgnoreCase)
           || message.Contains("channel !guild", StringComparison.OrdinalIgnoreCase);

    private void HandleEffect(EffectArgs args) => BuffBar?.SetEffect(args.EffectIcon, args.EffectColor);

    //each entity's last-seen health %, so the hit cue fires only on an actual DROP. Heals also show a health bar
    //(server ApplyHealScript calls ShowHealth), so without this a heal would wrongly play the hit sound. Cleared on map change.
    private readonly Dictionary<uint, int> LastHealthPercent = [];

    private void HandleHealthBar(HealthBarArgs args)
    {
        Overlays.AddOrResetHealthBar(args.SourceId, args.HealthPercent);

        //hit cue on HP LOSS: play when this entity's health dropped vs what we last saw (or the first time we see its
        //bar, which is almost always a hit). Any source counts - melee, spell, a draining effect - matching "play it
        //whenever something loses HP". A heal (% went up) shows a bar too but must stay silent, hence the compare.
        int pct = args.HealthPercent;
        var lostHp = !LastHealthPercent.TryGetValue(args.SourceId, out var prev) || (pct < prev);
        LastHealthPercent[args.SourceId] = pct;

        if (lostHp)
        {
            Game.SoundSystem.PlaySound(SoundSystem.SoundHit);

            var hitEntity = WorldState.GetEntity(args.SourceId);

            if (hitEntity is not null)
                hitEntity.HitShakeMs = 264f; //200ms delay + 64ms active
        }

        if (args.Sound.HasValue)
            Game.SoundSystem.PlaySound(args.Sound.Value);
    }

    private void HandleLightLevel(LightLevelArgs args) => DarknessRenderer.OnLightLevel(args.LightLevel);

    private void HandleMetaDataSyncComplete()
    {
        DarknessRenderer.ReloadMetadata();
        DarknessRenderer.ReapplyLightLevel();
        DataContext.MetaFiles.BuildItemIndex();

        //if the server shipped a SwmWarps metafile it is now on disk and authoritative; swap it in over the baked table
        WarpData.ReloadFromServer();

        //likewise the server collision table (SwmSotp): use it over the ia.dat sotp so client walls match the server
        DataContext.Tiles.ReloadSotpOverride();

        //the SClass files may have just been replaced - drop the merged cross-class cache AND re-parse the player's
        //own set. At login the self-profile often arrives BEFORE this sync, so the player's set was parsed from the
        //PREVIOUS session's files (symptom: stale ability detail, e.g. "Cast lines: instant" on a 2-line spell).
        AllClassAbilityMetadata = null;

        if ((PlayerBaseClass >= 0) && DataContext.MetaFiles.GetAbilityMetadata((byte)PlayerBaseClass) is { } fresh)
        {
            PlayerAbilityMetadata = fresh;
            StatusBook.SetAbilityMetadata(fresh);
        }
    }

    //--- notepad ---

    private void HandleDisplayReadonlyNotepad(DisplayReadonlyNotepadArgs args)
    {
        ItemTooltip.Hide();

        Notepad.ShowReadonly(
            (byte)args.NotepadType,
            args.Width,
            args.Height,
            args.Message);
    }

    private void HandleDisplayEditableNotepad(DisplayEditableNotepadArgs args)
    {
        ItemTooltip.Hide();

        Notepad.ShowEditable(
            args.Slot,
            (byte)args.NotepadType,
            args.Width,
            args.Height,
            args.Message);
    }

    //--- exit / state ---

    private void HandleExitResponse(ExitResponseArgs args)
    {
        //server confirmed exit; send the actual logout (isrequest=false triggers server-side redirect to login)
        if (args.ExitConfirmed)
            Game.Connection.RequestExit(false);
    }

    private void HandleStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        //while a silent reconnect is running, ReconnectFlow owns every transition (it walks us back through
        //lobby → login → world). Ignore them here so we don't switch to the lobby on the intermediate Login state
        //or pop the disconnect box on the expected redirect disconnects.
        if (Reconnecting)
            return;

        //server redirected us back to login (e.g., after logout)
        //state transitions go world → connecting → login, so just check for login arrival
        if (newState == ConnectionState.Login)
        {
            RedirectInProgress = false;
            PendingLoginSwitch = true;

            return;
        }

        //unexpected disconnect (skip if this is part of a redirect sequence)
        if ((newState == ConnectionState.Disconnected) && !RedirectInProgress)
        {
            //force any open NPC dialog shut: the server can't round-trip the close, so it would otherwise hang over the
            //reconnect overlay / disconnect prompt until relog
            NpcSession.HideAll();
            NpcSessionHost?.SnapHidden();

            //try a silent reconnect first (freeze + darken + replay login), but ONLY for a GENUINE unexpected drop -
            //never for the expected disconnect of an in-flight redirect (logout / server move closes the world socket
            //on purpose). IsRedirecting is set BEFORE that disconnect, so it gates this correctly; RedirectInProgress
            //is only set just AFTER it (by OnRedirectReceived) and so can't. A reconnect needs this session's
            //credentials, always present once in the world; otherwise fall back to the disconnect popup.
            if (!Game.Connection.IsRedirecting && Game.Connection.CanReconnect)
            {
                BeginReconnect();

                return;
            }

            DisconnectPopup.Show("Connection lost.");
        }
    }

    //--- helpers ---

    private static Color MapMarkColor(MarkColor color)
    {
        if (color == MarkColor.Invisible)
            return Color.Transparent;

        return LegendColors.Get((int)color);
    }
    #endregion

    //Up=0, Right=1, Down=2, Left=3 (matches server DirectionalRelationTo in tile space)
    private static int GetProjectileDirection(int dtx, int dty)
    {
        var absDtx = Math.Abs(dtx);
        var absDty = Math.Abs(dty);

        if (absDtx > absDty)
            return dtx > 0 ? 1 : 3;

        if (absDty > 0)
            return dty < 0 ? 0 : 2;

        return 0;
    }
}