#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Hud;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Controls.World.Popups.Options;
using Chaos.Client.Definitions;
using Chaos.Client.Extensions;
using Chaos.Client.Systems;
using Chaos.Client.Utilities;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    #region Server Event Wiring
    private void WireServerEvents()
    {
        //player identity
        Game.Connection.OnUserId += HandleUserId;

        //map assembly events
        Game.Connection.OnMapInfo += HandleMapInfo;
        Game.Connection.OnMapData += HandleMapData;
        Game.Connection.OnMapLoadComplete += HandleMapLoadComplete;
        Game.Connection.OnLocationChanged += HandleLocationChanged;

        //entity events
        //worldstate updates (entity add/remove/walk/turn) are wired in chaosgame so they
        //work during world entry before this screen exists. we subscribe here only for
        //screen-specific side effects (hud updates, cache cleanup).
        Game.Connection.OnDisplayAisling += HandleDisplayAisling;
        Game.Connection.OnRemoveEntity += HandleRemoveEntity;
        Game.Connection.OnClientWalkResponse += HandleClientWalkResponse;

        //hud data events
        Game.Connection.OnAttributes += HandleAttributes;

        //chat events
        Game.Connection.OnDisplayPublicMessage += HandleDisplayPublicMessage;
        Game.Connection.OnServerMessage += HandleServerMessage;

        //npc dialog/menu
        WorldState.NpcInteraction.DialogChanged += HandleDialogChanged;
        WorldState.NpcInteraction.MenuChanged += HandleMenuChanged;

        //refresh response
        Game.Connection.OnRefreshResponse += HandleRefreshResponse;

        WorldState.Exchange.AmountRequested += HandleExchangeAmountRequested;
        WorldState.Exchange.Closed += HandleExchangeClosed;

        //board - subscribe to state events
        WorldState.Board.PostListChanged += HandleBoardPostListChanged;
        WorldState.Board.PostViewed += HandleBoardPostViewed;
        WorldState.Board.BoardListReceived += HandleBoardListReceived;
        WorldState.Board.ResponseReceived += HandleBoardResponse;

        //group invite - subscribe to state event
        WorldState.GroupInvite.Received += HandleGroupInviteReceived;

        //profiles
        Game.Connection.OnEditableProfileRequest += HandleEditableProfileRequest;
        Game.Connection.OnSelfProfile += HandleSelfProfile;
        Game.Connection.OnOtherProfile += HandleOtherProfile;

        //animations / effects / sound
        Game.Connection.OnBodyAnimation += HandleBodyAnimation;
        Game.Connection.OnAnimation += HandleAnimation;
        Game.Connection.OnSound += HandleSound;
        Game.Connection.OnCancelCasting += CastingSystem.CancelChant;

        //map transitions
        Game.Connection.OnMapChangePending += HandleMapChangePending;

        //logout / disconnect
        Game.Connection.OnExitResponse += HandleExitResponse;
        Game.Connection.StateChanged += HandleStateChanged;
        Game.Connection.OnRedirectReceived += HandleRedirectReceived;

        //health bars
        Game.Connection.OnHealthBar += HandleHealthBar;

        //status effects
        Game.Connection.OnEffect += HandleEffect;

        //SWM quest tracker (corner HUD panel)
        Game.Connection.OnQuestTracker += HandleQuestTracker;
        Game.Connection.OnAlbum += HandleAlbum;
        Game.Connection.OnAlbumImage += HandleAlbumImage;
        Game.Connection.OnSelfPortrait += HandleSelfPortrait;

        //SWM quest completion + rewards (banner + visual reward panel)
        Game.Connection.OnQuestComplete += HandleQuestComplete;

        //SWM quest offer from a giver NPC (the Yes/No offer window)
        Game.Connection.OnQuestOffer += HandleQuestOffer;

        //light level
        Game.Connection.OnLightLevel += HandleLightLevel;

        //metadata sync - reload metadata readers after server handshake completes
        Game.OnMetaDataSyncComplete += HandleMetaDataSyncComplete;

        //notepad popups
        Game.Connection.OnDisplayReadonlyNotepad += HandleDisplayReadonlyNotepad;
        Game.Connection.OnDisplayEditableNotepad += HandleDisplayEditableNotepad;

        //world map
        Game.Connection.OnWorldMap += HandleWorldMap;

        //doors
        Game.Connection.OnDoor += HandleDoor;
    }
    #endregion

    #region Exchange Wiring
    // Exchange subscriptions are intentionally layered across ExchangeControl and WorldScreen:
    //   - ExchangeControl subscribes to Started/ItemAdded/GoldSet/OtherAccepted/Closed - updates its own UI
    //   - WorldScreen (WireServerEvents) subscribes to AmountRequested (spawn amount popup) + Closed (screen-level teardown)
    // Closed is intentionally double-subscribed: the control hides itself, the screen runs side effects.
    // Don't collapse them - they serve different layers.
    private void WireExchange()
    {
        Exchange.OnOk += () => Game.Connection.SendExchangeInteraction(ExchangeRequestType.Accept, Exchange.OtherUserId);

        Exchange.OnCancel += () =>
        {
            Game.Connection.SendExchangeInteraction(ExchangeRequestType.Cancel, Exchange.OtherUserId);
            WorldState.Exchange.Close();
        };
    }
    #endregion

    #region NPC Session Wiring
    private void WireNpcSession()
    {
        NpcSession.OnClose += () =>
        {
            //tell the server the dialog/menu closed so it clears the player's ActiveDialog. DialogId is 0 here
            //(= DialogResult.Close) for BOTH the dialog and the menu opcode. This must fire for MENU-opcode dialogs
            //too: closing a Menu (e.g. the tutorial-exit confirm) via the X used to send nothing, so the server's
            //ActiveDialog stayed set and the tutorial-exit reactor (which only re-fires when no dialog is active)
            //could never re-trigger - the player was trapped in the tutorial until relog (da-server-swm#23). The
            //server's OnDialogInteraction clears ActiveDialog on Close regardless of how the dialog was displayed.
            if (NpcSession.SourceId is { } sourceId)
                Game.Connection.SendDialogResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    NpcSession.DialogId);
        };

        NpcSession.OnTop += () =>
        {
            if (NpcSession.SourceId is { } sourceId)
                Game.Connection.ClickEntity(sourceId);
        };

        NpcSession.OnNext += () =>
        {
            if (NpcSession.SourceId is { } sourceId)
                Game.Connection.SendDialogResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    (ushort)(NpcSession.DialogId + 1));
        };

        NpcSession.OnPrevious += () =>
        {
            if (NpcSession.SourceId is { } sourceId)
                Game.Connection.SendDialogResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    (ushort)(NpcSession.DialogId - 1));
        };

        NpcSession.OnOptionSelected += optionIndex =>
        {
            if (NpcSession.SourceId is not { } sourceId)
                return;

            if (NpcSession.IsDialogOpcode)
                Game.Connection.SendDialogResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    (ushort)(NpcSession.DialogId + 1),
                    DialogArgsType.MenuResponse,
                    (byte)(optionIndex + 1));
            else
            {
                var pursuitId = NpcSession.GetOptionPursuitId(optionIndex);

                if (NpcSession.MenuArgs is not null)
                    Game.Connection.SendMenuResponse(
                        NpcSession.SourceEntityType,
                        sourceId,
                        pursuitId,
                        args: [NpcSession.MenuArgs]);
                else
                    Game.Connection.SendMenuResponse(NpcSession.SourceEntityType, sourceId, pursuitId);
            }
        };

        NpcSession.OnTextSubmit += text =>
        {
            if (NpcSession.SourceId is not { } sourceId)
                return;

            if (NpcSession.IsDialogOpcode)
            {
                //speak: broadcast the combined prompt + input + epilog as a public say first
                if (NpcSession.CurrentDialogType is DialogType.Speak)
                {
                    var sayParts = new[]
                    {
                        NpcSession.SpeakPrompt,
                        text,
                        NpcSession.SpeakEpilog
                    };

                    var sayText = string.Join(" ", sayParts.Where(s => !string.IsNullOrEmpty(s)));

                    Game.Connection.SendPublicMessage(sayText);
                }

                Game.Connection.SendDialogResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    (ushort)(NpcSession.DialogId + 1),
                    DialogArgsType.TextResponse,
                    args: [text]);
            } else
            {
                //include previous args for textentrywithargs
                var prevArgs = NpcSession.GetMenuTextPreviousArgs();

                if (prevArgs is not null)
                    Game.Connection.SendMenuResponse(
                        NpcSession.SourceEntityType,
                        sourceId,
                        NpcSession.PursuitId,
                        args:
                        [
                            prevArgs,
                            text
                        ]);
                else
                    Game.Connection.SendMenuResponse(
                        NpcSession.SourceEntityType,
                        sourceId,
                        NpcSession.PursuitId,
                        args: [text]);
            }
        };

        NpcSession.OnProtectedSubmit += (id, password) =>
        {
            if (NpcSession.SourceId is not { } sourceId)
                return;

            Game.Connection.SendDialogResponse(
                NpcSession.SourceEntityType,
                sourceId,
                NpcSession.PursuitId,
                (ushort)(NpcSession.DialogId + 1),
                DialogArgsType.TextResponse,
                args:
                [
                    id,
                    password
                ]);
        };

        //the NPC shop/dialog item hover flows through the shared tooltip resolver (UpdateTooltips), so it gets the same
        //configurable delay as every other tooltip and never fights the resolver for the one ItemTooltip control
        NpcSession.OnItemHoverEnter += name => HoveredNpcItemName = name;
        NpcSession.OnItemHoverExit += () => HoveredNpcItemName = null;

        NpcSession.OnMerchantItemSelected += selectedIndex =>
        {
            if (NpcSession.SourceId is not { } sourceId)
                return;

            var name = NpcSession.GetMerchantEntryName(selectedIndex);

            if (name is not null)
                Game.Connection.SendMenuResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    args: [name]);
        };

        //the player market (Temuair Exchange): a standalone floating window driven by its own packet pair. The server
        //pushes a screen (catalog/sellers/listings/bids/storage), the window renders it, and every action goes back as
        //a MarketRequest. No NPC dialog is involved.
        Game.Connection.OnMarketData += data => MarketWin!.Apply(data);
        MarketWin!.OnRequest += req => Game.Connection.SendMarketRequest(req);

        NpcSession.OnListItemSelected += selectedIndex =>
        {
            if (NpcSession.SourceId is not { } sourceId)
                return;

            var slot = NpcSession.GetListEntrySlot(selectedIndex);

            if (slot is null)
                return;

            if (NpcSession.CurrentMenuType is MenuType.ShowPlayerItems or MenuType.ShowPlayerSkills or MenuType.ShowPlayerSpells)
                Game.Connection.SendMenuResponse(
                    NpcSession.SourceEntityType,
                    sourceId,
                    NpcSession.PursuitId,
                    slot.Value);
            else
            {
                var name = NpcSession.GetListEntryName(selectedIndex);

                if (name is not null)
                    Game.Connection.SendMenuResponse(
                        NpcSession.SourceEntityType,
                        sourceId,
                        NpcSession.PursuitId,
                        args: [name]);
            }
        };
    }
    #endregion

    #region Board/Mail Wiring
    private void WireMailControls()
    {
        WireBoardListControl();
        WireArticleListControl();
        WireMailListControl();
        WireArticleReadControl();
        WireMailReadControl();
        WireArticleSendControl();
        WireMailSendControl();

        DeleteConfirm.OnOk += () =>
        {
            PendingDeleteAction?.Invoke();
            PendingDeleteAction = null;
            DeleteConfirm.Hide();
        };

        DeleteConfirm.OnCancel += () =>
        {
            PendingDeleteAction = null;
            DeleteConfirm.Hide();
        };

        WorldState.Board.SessionClosed += HideAllBoardControls;
    }

    private void ToggleSocialStatusPicker()
    {
        if (SocialStatusPicker.Visible)
        {
            SocialStatusPicker.Hide();

            WorldHud.EmoteButton?.IsSelected = false;

            return;
        }

        var emoteBtn = WorldHud.EmoteButton;

        if (SocialStatusHost is not null)
        {
            SocialStatusHost.Scale = ClientSettings.EffectiveWindowScale; //pick up the current "Window size" (host re-sizes to it)

            if (emoteBtn is not null)
            {
                SocialStatusHost.X = emoteBtn.ScreenX - (SocialStatusHost.Width / 2) + (emoteBtn.Width / 2);
                SocialStatusHost.Y = emoteBtn.ScreenY - SocialStatusHost.Height - 2 + 24;
            } else
            {
                //fallback positioning when no emote button exists
                SocialStatusHost.CenterHorizontallyIn(WorldViewport);
                SocialStatusHost.Y = WorldViewport.Y + WorldViewport.Height - SocialStatusHost.Height;
            }

            ClampSocialStatusHost();
        }

        emoteBtn?.IsSelected = true;

        SocialStatusPicker.Show();
    }

    //keeps the (scaled) social status picker fully on-screen
    private void ClampSocialStatusHost()
    {
        if (SocialStatusHost is null)
            return;

        SocialStatusHost.X = Math.Clamp(SocialStatusHost.X, 0, Math.Max(0, ChaosGame.UiWidth - SocialStatusHost.Width));
        SocialStatusHost.Y = Math.Clamp(SocialStatusHost.Y, 0, Math.Max(0, ChaosGame.UiHeight - SocialStatusHost.Height));
    }

    private bool IsAnyBoardPanelVisible() =>
        BoardList.Visible
        || ArticleList.Visible
        || ArticleRead.Visible
        || ArticleSend.Visible
        || MailList.Visible
        || MailRead.Visible
        || MailSend.Visible;

    /// <summary>
    ///     Closes all Q/W/E/R toggle panels except the one identified by <paramref name="except" />.
    ///     Slide panels animate out concurrently with the new panel sliding in. Button deselection
    ///     is handled by the OnClose/SessionClosed events that fire when slide-out completes.
    /// </summary>
    private void ForceCloseOtherTogglePanels(Keys except)
    {
        if ((except != Keys.Q) && MainOptions.Visible)
        {
            SettingsDialog.Hide();
            MacrosList.Hide();
            FriendsList.Hide();
            MainOptions.SlideClose();
        }

        if ((except != Keys.W) && IsAnyBoardPanelVisible())
        {
            if (BoardList.Visible)
                BoardList.SlideClose();
            else
                WorldState.Board.CloseSession();
        }

        if ((except != Keys.R) && SocialStatusPicker.Visible)
        {
            SocialStatusPicker.Hide();

            WorldHud.EmoteButton?.IsSelected = false;
        }
    }

    private void HideAllBoardControls()
    {
        BoardList.Hide();
        ArticleList.Hide();
        ArticleRead.Hide();
        ArticleSend.Hide();
        MailList.Hide();
        MailRead.Hide();
        MailSend.Hide();
    }

    /// <summary>
    ///     UIPanel subclass that forwards root-level input events back to WorldScreen.
    ///     Used as the Root panel so the dispatcher's bubble-up terminates with WorldScreen's handlers.
    /// </summary>
    private sealed class WorldRootPanel : UIPanel
    {
        private readonly WorldScreen Screen;

        public WorldRootPanel(WorldScreen screen) => Screen = screen;

        public override void OnKeyDown(KeyDownEvent e) => Screen.OnRootKeyDown(e);
        public override void OnMouseDown(MouseDownEvent e) => Screen.OnRootMouseDown(e);
        public override void OnClick(ClickEvent e) => Screen.OnRootClick(e);
        public override void OnMouseScroll(MouseScrollEvent e) => Screen.OnRootMouseScroll(e);
        public override void OnDoubleClick(DoubleClickEvent e) => Screen.OnRootDoubleClick(e);
        public override void OnDragStart(DragStartEvent e) => Screen.OnRootDragStart(e);
        public override void OnDragMove(DragMoveEvent e) => Screen.OnRootDragMove(e);
        public override void OnDragDrop(DragDropEvent e) => Screen.OnRootDragDrop(e);
    }

    private void WireBoardListControl()
    {
        BoardList.OnViewBoard += boardId =>
        {
            //the post list / reader / composer all hide every board window first (HideAllBoardControls in the server
            //handlers, explicit Hide() in the client-only transitions), so these windows are shown one at a time.
            WorldState.Board.IsBoardListPending = true;
            WorldState.Board.WasOpenedFromBoardList = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.ViewBoard, boardId, startPostId: short.MaxValue);
        };

        BoardList.OnClose += () => WorldState.Board.CloseSession();
    }

    private void WireArticleListControl()
    {
        ArticleList.OnViewPost += postId =>
        {
            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                ArticleList.BoardId,
                postId,
                controls: BoardControls.RequestPost);
        };

        ArticleList.OnNewPost += () =>
        {
            ArticleList.Hide();
            ArticleSend.BoardId = ArticleList.BoardId;
            ArticleSend.ShowCompose(WorldHud.PlayerName);
        };

        ArticleList.OnDeletePost += postId =>
        {
            PendingDeleteAction = () =>
            {
                PendingBoardSuccessAction = () => ArticleList.RemoveEntry(postId);
                Game.Connection.SendBoardInteraction(BoardRequestType.Delete, ArticleList.BoardId, postId);
            };

            DeleteConfirm.Show("Delete this post?");
        };

        ArticleList.OnHighlight += postId =>
        {
            PendingBoardSuccessAction = () => ArticleList.ToggleHighlight(postId);
            Game.Connection.SendBoardInteraction(BoardRequestType.Highlight, ArticleList.BoardId, postId);
        };

        ArticleList.OnLoadMorePosts += lastPostId =>
        {
            LoadingMoreBoardPosts = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.ViewBoard, ArticleList.BoardId, startPostId: lastPostId);
        };

        ArticleList.OnUp += () =>
        {
            ArticleList.Hide();

            if (WorldState.Board.WasOpenedFromBoardList && WorldState.Board.AvailableBoards is { Count: > 0 })
                BoardList.ShowBoards(
                    WorldState.Board
                              .AvailableBoards
                              .Select(b => (b.BoardId, b.Name))
                              .ToList());
            else
                WorldState.Board.CloseSession();
        };

        ArticleList.OnClose += () => WorldState.Board.CloseSession();
    }

    private void WireMailListControl()
    {
        MailList.OnViewPost += postId =>
        {
            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                MailList.BoardId,
                postId,
                controls: BoardControls.RequestPost);
        };

        MailList.OnNewMail += () =>
        {
            MailList.Hide();
            MailSend.BoardId = MailList.BoardId;
            MailSend.ShowCompose();
        };

        MailList.OnDeletePost += postId =>
        {
            PendingDeleteAction = () =>
            {
                PendingBoardSuccessAction = () => MailList.RemoveEntry(postId);
                Game.Connection.SendBoardInteraction(BoardRequestType.Delete, MailList.BoardId, postId);
            };

            DeleteConfirm.Show("Delete this post?");
        };

        MailList.OnReplyPost += _ =>
        {
            MailSend.BoardId = MailList.BoardId;
            MailList.Hide();
            MailSend.ShowCompose(MailList.CurrentAuthor);
        };

        MailList.OnLoadMorePosts += lastPostId =>
        {
            LoadingMoreBoardPosts = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.ViewBoard, MailList.BoardId, startPostId: lastPostId);
        };

        MailList.OnUp += () =>
        {
            MailList.Hide();

            if (WorldState.Board.WasOpenedFromBoardList && WorldState.Board.AvailableBoards is { Count: > 0 })
                BoardList.ShowBoards(
                    WorldState.Board
                              .AvailableBoards
                              .Select(b => (b.BoardId, b.Name))
                              .ToList());
            else
                WorldState.Board.CloseSession();
        };

        MailList.OnClose += () => WorldState.Board.CloseSession();
    }

    private void WireArticleReadControl()
    {
        ArticleRead.OnUp += () =>
        {
            ArticleRead.Hide();
            ArticleList.Show();
        };

        ArticleRead.OnClose += () => WorldState.Board.CloseSession();

        ArticleRead.OnPrev += () =>
        {
            var prevId = (short)Math.Min(ArticleRead.CurrentPostId + 1, short.MaxValue);

            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                ArticleRead.BoardId,
                prevId,
                controls: BoardControls.PreviousPage);
        };

        ArticleRead.OnNext += () =>
        {
            var nextId = (short)Math.Max(ArticleRead.CurrentPostId - 1, 1);

            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                ArticleRead.BoardId,
                nextId,
                controls: BoardControls.NextPage);
        };

        ArticleRead.OnDeletePost += postId =>
        {
            PendingDeleteAction = () =>
            {
                PendingBoardSuccessAction = () =>
                {
                    ArticleList.RemoveEntry(postId);
                    ArticleRead.Hide();
                    ArticleList.Show();
                };
                Game.Connection.SendBoardInteraction(BoardRequestType.Delete, ArticleRead.BoardId, postId);
            };

            DeleteConfirm.Show("Delete this post?");
        };

        ArticleRead.OnNewPost += () =>
        {
            ArticleRead.Hide();
            ArticleSend.BoardId = ArticleRead.BoardId;
            ArticleSend.ShowCompose(WorldHud.PlayerName);
        };
    }

    private void WireMailReadControl()
    {
        MailRead.OnUp += () =>
        {
            MailRead.Hide();
            MailList.Show();
        };

        MailRead.OnQuit += () => WorldState.Board.CloseSession();

        MailRead.OnPrev += () =>
        {
            var prevId = (short)Math.Min(MailRead.CurrentPostId + 1, short.MaxValue);

            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                MailRead.BoardId,
                prevId,
                controls: BoardControls.PreviousPage);
        };

        MailRead.OnNext += () =>
        {
            var nextId = (short)Math.Max(MailRead.CurrentPostId - 1, 1);

            Game.Connection.SendBoardInteraction(
                BoardRequestType.ViewPost,
                MailRead.BoardId,
                nextId,
                controls: BoardControls.NextPage);
        };

        MailRead.OnReplyPost += _ =>
        {
            MailRead.Hide();
            MailSend.BoardId = MailRead.BoardId;
            MailSend.ShowCompose(MailRead.CurrentAuthor);
        };

        MailRead.OnDeletePost += postId =>
        {
            PendingDeleteAction = () =>
            {
                PendingBoardSuccessAction = () =>
                {
                    MailList.RemoveEntry(postId);
                    MailRead.Hide();
                    MailList.Show();
                };
                Game.Connection.SendBoardInteraction(BoardRequestType.Delete, MailRead.BoardId, postId);
            };

            DeleteConfirm.Show("Delete this post?");
        };

        MailRead.OnNewMail += () =>
        {
            MailRead.Hide();
            MailSend.BoardId = MailRead.BoardId;
            MailSend.ShowCompose();
        };
    }

    private void WireArticleSendControl()
    {
        ArticleSend.OnSend += (subject, body) =>
        {
            Game.Connection.SendBoardInteraction(
                BoardRequestType.NewPost,
                ArticleSend.BoardId,
                subject: subject,
                message: body);

            //re-request post list - compose stays visible until server responds
            WorldState.Board.IsBoardListPending = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.ViewBoard, ArticleSend.BoardId, startPostId: short.MaxValue);
        };

        ArticleSend.OnCancel += () =>
        {
            ArticleSend.Hide();
            ArticleList.Show();
        };
    }

    private void WireMailSendControl()
    {
        MailSend.OnSend += (recipient, subject, body) =>
        {
            Game.Connection.SendBoardInteraction(
                BoardRequestType.SendMail,
                MailSend.BoardId,
                to: recipient,
                subject: subject,
                message: body);

            //re-request post list - compose stays visible until server responds
            WorldState.Board.IsBoardListPending = true;
            Game.Connection.SendBoardInteraction(BoardRequestType.ViewBoard, MailSend.BoardId, startPostId: short.MaxValue);
        };

        MailSend.OnCancel += () =>
        {
            MailSend.Hide();
            MailList.Show();
        };
    }
    #endregion

    #region HUD Panel Wiring
    private void WireHudPanels(IWorldHud hud)
    {
        //layout/expand
        if (hud.ChangeLayoutButton is not null)
            hud.ChangeLayoutButton.Clicked += SwapHudLayout;

        if (hud.ExpandButton is not null)
            hud.ExpandButton.Clicked += () => hud.ToggleExpand();

        //action buttons
        if (hud.OptionButton is not null)
        {
            hud.OptionButton.Clicked += () =>
            {
                hud.OptionButton!.IsSelected = true;
                MainOptions.Show();
            };

            MainOptions.OnClose += () => hud.OptionButton.IsSelected = false;
        }

        if (hud.HelpButton is not null)
            hud.HelpButton.Clicked += () => HotkeyHelp.Show();

        if (hud.SettingsButton is not null)
            hud.SettingsButton.Clicked += () =>
            {
                if (!MacrosList.Visible && !FriendsList.Visible)
                    SettingsDialog.Show();
            };

        if (hud.GroupButton is not null)
            hud.GroupButton.Clicked += () =>
            {
                GroupPanel.ShowMembers();
                Game.Connection.RequestSelfProfile();
            };

        if (hud.GroupIndicator is not null)
            hud.GroupIndicator.Clicked += () => Game.Connection.ToggleGroup();

        if (hud.UsersButton is not null)
        {
            hud.UsersButton.Clicked += () =>
            {
                if (WorldList.Visible)
                {
                    WorldList.Hide();

                    return;
                }

                hud.UsersButton!.IsSelected = true;
                Game.Connection.RequestWorldList();
            };

            WorldList.OnClose += () => hud.UsersButton.IsSelected = false;
        }

        WorldList.OnWhisperRequested += name => ActiveChatInput.Focus($"-> {name}: ", TextColors.Whisper);

        if (hud.BulletinButton is not null)
        {
            hud.BulletinButton.Clicked += () =>
            {
                hud.BulletinButton!.IsSelected = true;
                Game.Connection.SendBoardInteraction(BoardRequestType.BoardList);
            };

            WorldState.Board.SessionClosed += ResetBulletinButtonSelection;
        }

        hud.InventoryReactivated += () =>
        {
            SelfProfileRequested = true;
            SelfProfileRequestedTab = StatusBookTab.Equipment;
            Game.Connection.RequestSelfProfile();
        };

        if (hud.LegendButton is not null)
            hud.LegendButton.Clicked += () =>
            {
                SelfProfileRequested = true;
                SelfProfileRequestedTab = StatusBookTab.Legend;
                Game.Connection.RequestSelfProfile();
            };

        if (hud.TownMapButton is not null)
            hud.TownMapButton.Clicked += () =>
            {
                if (TownMapControl.IsOpen)
                    TownMapControl.BeginClose();
                else
                    ShowTownMap();
            };

        if (hud.EmoteButton is not null)
            hud.EmoteButton.Clicked += ToggleSocialStatusPicker;

        if (hud.EmoteButton is not null)
            SocialStatusPicker.OnClosed += () => hud.EmoteButton.IsSelected = false;

        if (hud.CharScreenshotButton is not null)
            hud.CharScreenshotButton.Clicked += () => Game.RequestScreenshot();

        if (hud.MailButton is not null)
        {
            hud.MailButton.Clicked += () =>
            {
                hud.MailButton!.IsSelected = true;
                Game.Connection.SendBoardInteraction(BoardRequestType.BoardList);
            };

            WorldState.Board.SessionClosed += ResetMailButtonSelection;
        }

        //chat input events
        hud.ChatInput.PlayerNameProvider = () => WorldHud.PlayerName;
        hud.ChatInput.MessageSent += HandleChatMessage;
        hud.ChatInput.ShoutSent += msg => Game.Connection.SendShout(msg);
        hud.ChatInput.WhisperSent += (target, msg) => Game.Connection.SendWhisper(target, msg);
        hud.ChatInput.IgnoreAdded += name => Game.Connection.SendAddIgnore(name);
        hud.ChatInput.IgnoreRemoved += name => Game.Connection.SendRemoveIgnore(name);
        hud.ChatInput.IgnoreListRequested += () => Game.Connection.SendIgnoreRequest();

        //slot events
        hud.Inventory.OnSlotClicked += HandleInventorySlotClicked;
        hud.Inventory.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.Inventory, s, t);
        hud.Inventory.OnSlotDroppedOutside += HandleInventoryDropInViewport;
        hud.SkillBook.OnSlotClicked += HandleSkillSlotClicked;
        hud.SkillBook.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        hud.SkillBookAlt.OnSlotClicked += HandleSkillSlotClicked;
        hud.SkillBookAlt.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        hud.SpellBook.OnSlotClicked += HandleSpellSlotClicked;
        hud.SpellBook.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        hud.SpellBook.OnSlotDroppedOutside += HandleSpellSlotDropped;
        hud.SpellBookAlt.OnSlotClicked += HandleSpellSlotClicked;
        hud.SpellBookAlt.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        hud.SpellBookAlt.OnSlotDroppedOutside += HandleSpellSlotDropped;

        //tools (h tab) - page 3 world abilities
        hud.Tools.WorldSkills.OnSlotClicked += HandleSkillSlotClicked;
        hud.Tools.WorldSkills.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        hud.Tools.WorldSpells.OnSlotClicked += HandleSpellSlotClicked;
        hud.Tools.WorldSpells.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        hud.Tools.WorldSpells.OnSlotDroppedOutside += HandleSpellSlotDropped;

        WireAbilityRightClicks(hud.SkillBook);
        WireAbilityRightClicks(hud.SkillBookAlt);
        WireAbilityRightClicks(hud.SpellBook);
        WireAbilityRightClicks(hud.SpellBookAlt);
        WireAbilityRightClicks(hud.Tools.WorldSkills);
        WireAbilityRightClicks(hud.Tools.WorldSpells);

        hud.StatsPanel.OnRaiseStat += stat => Game.Connection.RaiseStat(stat);

        hud.StatsPanel.OnHoverEnter += count => WorldHud.SetDescription($"Level Up Point: {count}");
        hud.StatsPanel.OnHoverExit += () => WorldHud.SetDescription(null);

        hud.Inventory.OnSlotHoverEnter += HandleInventoryHoverEnter;
        hud.Inventory.OnSlotHoverExit += HandleInventoryHoverExit;

        foreach (var panel in new PanelBase[]
                 {
                     hud.Inventory,
                     hud.SkillBook,
                     hud.SkillBookAlt,
                     hud.SpellBook,
                     hud.SpellBookAlt,
                     hud.Tools.WorldSkills,
                     hud.Tools.WorldSpells
                 })
        {
            panel.OnSlotHoverEnter += slot => WorldHud.SetDescription(slot.SlotName);
            panel.OnSlotHoverExit += () => WorldHud.SetDescription(null);
        }

        //large hud: show a tooltip popup (matching the equipment tab's style) when hovering skill/spell slots so the
        //full ability name + level details are visible above the slot
        if (hud is LargeWorldHudControl largeHud)
            foreach (var panel in new PanelBase[]
                     {
                         hud.SkillBook,
                         hud.SkillBookAlt,
                         hud.SpellBook,
                         hud.SpellBookAlt,
                         hud.Tools.WorldSkills,
                         hud.Tools.WorldSpells
                     })
            {
                panel.OnSlotHoverEnter += largeHud.ShowSlotTooltip;
                panel.OnSlotHoverExit += largeHud.HideSlotTooltip;
            }
    }

    private void ResetBulletinButtonSelection() => WorldHud.BulletinButton?.IsSelected = false;

    private void ResetMailButtonSelection() => WorldHud.MailButton?.IsSelected = false;

    private void SwapHudLayout()
    {
        WorldHud.Inventory.ForceHoverExit();

        var activeTab = WorldHud.ActiveTab;

        ((UIPanel)WorldHud).Visible = false;
        WorldHud = WorldHud == SmallHud ? LargeHud : SmallHud;
        ((UIPanel)WorldHud).Visible = true;
        WorldHud.ShowTab(activeTab);

        var viewport = WorldViewport;
        //the camera spans the full (window-filling) world render rect; the popups below stay in the 640x480 region
        Camera.Resize(ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);
        UpdateCameraOffset(viewport);

        FollowPlayerCamera();

        //rebuild darkness texture immediately so this frame's draw uses the new viewport size -
        //DarknessRenderer.Update runs earlier in the frame (before ProcessInput), so without this
        //the first frame after the swap would draw the old-sized texture over the new viewport
        //re-gather lights into the new viewport size; the light buffer rebuilds from them in the next Draw
        if (DarknessRenderer.IsActive)
            Lighting.Gather(MapFile, Camera, darknessActive: true, DarknessRenderer.DuskGlow, LightAnimTime, DarknessRenderer.IsAlwaysDark);

        //weather uses fresh viewport each frame via WorldViewport, but snow needs an
        //immediate respawn into the new bounds so particles don't clump at the old edges
        if (WeatherRenderer.IsActive)
            WeatherRenderer.Update(new GameTime(), viewport);
    }

    /// <summary>
    ///     Calls an action on all HUD instances so both stay in sync regardless of which is visible.
    /// </summary>
    private void UpdateHuds<T>(Action<IWorldHud, T> op, T arg)
    {
        op(SmallHud, arg);
        op(LargeHud, arg);
    }

    private void UpdateHuds<T1, T2>(Action<IWorldHud, T1, T2> op, T1 arg1, T2 arg2)
    {
        op(SmallHud, arg1, arg2);
        op(LargeHud, arg1, arg2);
    }

    private static class HudOps
    {
        public static readonly Action<IWorldHud, int, int> SetCoords =
            static (h, x, y) => h.SetCoords(x, y);

        public static readonly Action<IWorldHud, string> SetZoneName =
            static (h, name) => h.SetZoneName(name);

        public static readonly Action<IWorldHud, string> SetPlayerName =
            static (h, name) => h.SetPlayerName(name);

        public static readonly Action<IWorldHud, string> SetServerName =
            static (h, name) => h.SetServerName(name);

        public static readonly Action<IWorldHud, string> ShowPersistentMessage =
            static (h, msg) => h.ShowPersistentMessage(msg);

        public static readonly Action<IWorldHud, Texture2D> SetEmoteIcon = static (h, icon) =>
        {
            if (h.EmoteButton is null)
                return;

            h.EmoteButton.NormalTexture = icon;
            h.EmoteButton.SelectedTexture = icon;
        };
    }
    #endregion

    #region Options Dialog Wiring
    private void WireOptionsDialog()
    {
        MainOptions.OnMacro += () => ToggleSubPanel(MacrosList, SettingsDialog, FriendsList);
        MainOptions.OnSettings += () => ToggleSubPanel(SettingsDialog, MacrosList, FriendsList);
        MainOptions.OnFriends += () => ToggleSubPanel(FriendsList, MacrosList, SettingsDialog);

        MainOptions.OnExit += () => Game.Connection.RequestExit();

        MainOptions.OnSoundVolumeChanged += volume =>
        {
            Game.SoundSystem.SetSoundVolume(volume);
            ClientSettings.SoundVolume = volume;
            ClientSettings.Save();
        };

        MainOptions.OnMusicVolumeChanged += volume =>
        {
            Game.SoundSystem.SetMusicVolume(volume);
            ClientSettings.MusicVolume = volume;
            ClientSettings.Save();
        };

        //apply saved volume settings
        MainOptions.SetSoundVolume(ClientSettings.SoundVolume);
        MainOptions.SetMusicVolume(ClientSettings.MusicVolume);
        Game.SoundSystem.SetSoundVolume(ClientSettings.SoundVolume);
        Game.SoundSystem.SetMusicVolume(ClientSettings.MusicVolume);
    }

    private static void ToggleSubPanel(PrefabPanel panel, PrefabPanel sibling1, PrefabPanel sibling2)
    {
        if (panel.Visible)
            panel.Hide();
        else if (sibling1.Visible || sibling2.Visible)
            // ReSharper disable once RedundantJumpStatement
            return;
        else if (panel is MacrosListControl macro)
            macro.SlideIn();
        else if (panel is SettingsControl settings)
            settings.SlideIn();
        else if (panel is FriendsListControl friends)
            friends.Show();
    }

    /// <summary>
    ///     Chat-line dispatch shared by the HUD input and the chat window: local /commands are handled first, then the
    ///     line goes to the server as a public message. "/noclip" is BOTH (the server flips its own flag); "/edit" is
    ///     purely local - a GM jumps to the web editor opened at the current map (the editor's #map deep link).
    /// </summary>
    private void HandleChatMessage(string msg)
    {
        var trimmed = msg.TrimStart();

        if (trimmed.StartsWith("/noclip", StringComparison.OrdinalIgnoreCase))
        {
            if (IsGameMaster)
            {
                NoClip = !NoClip;
                WorldState.Chat.AddOrangeBarMessage(NoClip ? "NoClip enabled." : "NoClip disabled.");
            } else
                WorldState.Chat.AddOrangeBarMessage("Only game masters can use /noclip.");

            Game.Connection.SendPublicMessage(msg);

            return;
        }

        if (trimmed.StartsWith("/debugoptions", StringComparison.OrdinalIgnoreCase))
        {
            DebugFxWin?.Toggle();

            return; //local only, never sent to the server
        }

        if (trimmed.StartsWith("/edit", StringComparison.OrdinalIgnoreCase))
        {
            WorldState.Chat.AddOrangeBarMessage($"Opening the editor at map {CurrentMapId}...");
            Browser.Open($"https://darkages.swedenmayhem.se/editor/#map={CurrentMapId}");

            return; //never sent to the server
        }

        Game.Connection.SendPublicMessage(msg);
    }
    #endregion
}