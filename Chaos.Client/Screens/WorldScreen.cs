#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Controls.World.Hud;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Controls.World.Hud.Panel.Slots;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Controls.World.Popups;
using Chaos.Client.Controls.World.Popups.Boards;
using Chaos.Client.Controls.World.Popups.Dialog;
using Chaos.Client.Controls.World.Popups.Exchange;
using Chaos.Client.Controls.World.Popups.Options;
using Chaos.Client.Controls.World.Popups.Profile;
using Chaos.Client.Controls.World.Popups.WorldList;
using Chaos.Client.Controls.World.ViewPort;
using Chaos.Client.Data;
using Chaos.Client.Data.Models;
using Chaos.Client.Data.Repositories;
using Chaos.Client.Extensions;
using Chaos.Client.Models;
using Chaos.Client.Rendering.Models;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Geometry.Abstractions;
using Chaos.Geometry.Abstractions.Definitions;
using DALib.Data;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathfinder = Chaos.Pathfinding.Pathfinder;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen : IScreen
{
    //one walk can be queued once the walk animation is at least 75% done
    private const float WALK_QUEUE_THRESHOLD = 0.75f;

    //minimum gap between held-spacebar assail fires, since os key-repeat rate varies
    private const long SPACEBAR_INTERVAL_MS = 100;

    //minimum gap between Pick Up / Interact fires, so holding or mashing the key can't spam the server
    private const long INTERACT_INTERVAL_MS = 250;

    //when nothing is adjacent, Pick Up / Interact reaches out to the closest ground item within this many tiles
    private const int INTERACT_REACH_TILES = 4;

    //while the move button is held, re-path toward the cursor at least this often (ms)
    //so a cursor held far away keeps being re-aimed even before its current path runs out
    private const float HELD_WALK_REPATH_INTERVAL_MS = 1000f;

    //stripe-pass alpha for transparent (invisible) aislings
    //1/3 so the stripe draw plus the silhouette overdraw lands the local player at ~50% in the open and ~25% behind foregrounds
    private const float TRANSPARENT_ALPHA = 1f / 3f;

    //silhouette-RT alpha for transparent entities
    //0.5 makes the overlay contribute 0.25, matching the behind-foreground target
    private const float TRANSPARENT_SILHOUETTE_ALPHA = 0.5f;

    //true while the silhouette pre-render is drawing entities into the silhouette RT
    //DrawAisling uses it to route transparent players through the silhouette pass instead of the stripe pass
    private bool DrawingForSilhouette;

    //entity hitbox dimensions (screen pixels)
    private const int HITBOX_WIDTH = 28;
    private const int HITBOX_HEIGHT = 60;

    //double-click entity cache expiry, a bit longer than the dispatcher's 300ms window so the cache stays valid through it
    private const int DOUBLE_CLICK_CACHE_WINDOW_MS = 550;

    private const string SPOUSE_PREFIX = "Spouse: ";
    private const string GROUP_MEMBERS_PREFIX = "Group members";

    private readonly CastingSystem CastingSystem = new();

    private readonly WorldDebugRenderer DebugRenderer = new();

    //hitbox list rebuilt every frame during entity rendering, in draw order back-to-front
    private readonly List<EntityHitBox> EntityHitBoxes = new(256);

    //entity ids currently highlighted as group members, auto-expires after 1000ms
    private readonly HashSet<uint> GroupHighlightedIds = [];
    private readonly EntityOverlayManager Overlays = new();
    private readonly PathfindingState Pathfinding = new();
    //predicted-but-unconfirmed walk destinations, one per Walk packet in send order, each server response confirms the oldest
    //a match keeps us in sync, a divergence or a server-initiated walk hard-resyncs to the server's tile so a desync self-heals
    private readonly Queue<(int X, int Y)> PredictedWalkDests = new();

    private AbilityMetadataDetailsControl AbilityMetadataDetails = null!;
    private ScaleHost? AbilityDetailsHost;
    private ScaleHost? EventDetailsHost;
    private AislingContextMenu AislingContext = null!;

    private int AnimationTick;
    private ArticleListControl ArticleList = null!;
    private ScaleHost ArticleListHost = null!;
    private ArticleReadControl ArticleRead = null!;
    private ScaleHost ArticleReadHost = null!;
    private ArticleSendControl ArticleSend = null!;
    //board and mail list hosts, kept so their rows can be drawn as native-resolution TTF
    private ScaleHost BoardListHost = null!;
    private ScaleHost MailListHost = null!;

    //board and mail controls, one instance per prefab
    private bool AwaitingMapData;
    private BoardListControl BoardList = null!;
    private OkPopupMessageControl BoardResponsePopup = null!;
    private Camera Camera = null!;

    //the rectangle the game world renders into
    //the world fills the whole screen and the new UI overlays on top, this is the single source of truth
    private Rectangle WorldViewport => new(0, 0, ChaosGame.VIRTUAL_WIDTH, ChaosGame.VIRTUAL_HEIGHT);

    //the actual world render-target rect, expands to fill the window so tiles render where the letterbox bars used to be
    //WorldViewport stays 640x480 for the legacy popups, which keep their original centered layout
    private Rectangle WorldRenderRect => new(0, 0, ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);

    //the native region the world is drawn into (full window)
    //world clicks are gated against this in native mouse space, camera and tile math work in WorldRenderRect space
    private Rectangle WorldInputBounds => ChaosGame.WorldDrawRect;

    //real inventory grid hosted in a draggable window, toggled by Menu or A
    private DraggableWindow? InventoryWindow;

    //the chat window, hosts a working typing line since the old HUD's chat input is hidden
    private ChatWindow? ChatWin;
    private bool ChatDefaultSized; //one-time fit of the chat's default width into the lower-left gap beside the HP orb

    //the chat input the focus and typing shortcuts target, the window's visible input falling back to the HUD's
    private ChatInputControl ActiveChatInput => ChatWin?.ChatInput ?? WorldHud.ChatInput;

    //fixed on-screen hotbars, inventory top-center, skills and spells bottom-center
    private ScaleHost? InvBar;
    private ScaleHost? SkillBar;
    private ScaleHost? SpellBar;
    //eased alpha on all three hotbars, dims to HOTBAR_TARGET_ALPHA while targeting a spell
    private float TargetingHotbarAlpha = 1f;

    //hp and mp orbs flanking the bottom spell hotbar
    private OrbDisplayControl? HpOrb;
    private OrbDisplayControl? MpOrb;
    private OrbDisplayControl? HoveredOrb;

    //server-driven buff bar on the right edge
    //a fresh instance for the new UI since the old HUD's is hidden, magnified by BuffBarHost and anchored each frame
    private EffectBarControl? BuffBar;
    private ScaleHost? BuffBarHost;

    //the entity a readied spell will hit (closest to the ground cursor), cached once per frame so the whole blue
    //highlight references the same target, null when not casting
    private uint? CastTargetId;


    //inner panels of the hotbars, used for drag routing
    private InventoryPanel? InvBarPanel;
    private SkillBookPanel? SkillBarPanel;
    private SpellBookPanel? SpellBarPanel;

    //full Skills and Spells books hosted in windows, used for drag routing
    private SkillBookPanel? SkillWinPanel;
    private SpellBookPanel? SpellWinPanel;

    //character stats hosted in a window with level-up buttons and next-level EXP
    private StatsPanel? StatsWinPanel;

    private ChantEditControl ChantEdit = null!;

    //the chant editor magnified by the "Window size" setting, centered and raised on open
    private ScaleHost ChantEditHost = null!;
    private ushort CurrentMapCheckSum;
    private MapFlags CurrentMapFlags;
    private short CurrentMapId;
    private string CurrentMapName = string.Empty;

    private DarknessRenderer DarknessRenderer = null!;

    //deferred lighting, an additive light buffer multiplied in
    //DarknessRenderer still owns the ambient day/night level, LightingRenderer draws the glows
    private LightingRenderer LightingRenderer = null!;
    private WeatherRenderer WeatherRenderer = null!;

    //total elapsed seconds, refreshed each Update, drives the tile-light flame flicker
    private float LightAnimTime;
    private OkPopupMessageControl DeleteConfirm = null!;
    private OkPopupMessageControl ConfirmDialog = null!; //reusable OK/Cancel question
    private GraphicsDevice Device = null!;
    private OkPopupMessageControl DisconnectPopup = null!;

    //event detail popup from the events tab
    private EventMetadataDetailsControl EventMetadataDetails = null!;
    private ExchangeControl Exchange = null!;
    private OkPopupMessageControl ExchangeResultPopup = null!;
    private ItemAmountControl ItemAmount = null!;

    private FriendsListControl FriendsList = null!;

    private ChaosGame Game = null!;
    private GoldAmountControl GoldDrop = null!;
    private GroupRecruitPanel GroupBoxViewer = null!;

    //true when j was pressed, the next self-profile response highlights group members instead of opening the panel
    private bool GroupHighlightRequested;
    private float GroupHighlightTimer;
    private GroupTabControl GroupPanel = null!;
    private HotkeyHelpControl HotkeyHelp = null!;
    private PanelSlot? HoveredInventorySlot;
    private bool IsGameMaster;
    private bool NoClip;
    private ItemTooltipControl ItemTooltip = null!;

    //tooltip resolver state, the most recent hover target's identity and how long the cursor has rested before showing
    //the NPC shop and dialog report their hovered item name here, everything is shown through the one resolver in UpdateTooltips
    private object? TooltipKey;
    private object? TooltipClickSuppressedKey;
    private float TooltipDelayTimer;

    //after the world fades in on connect, tooltips wait until the cursor actually moves
    //so a tooltip for whatever the stationary cursor happens to rest on does not hang on screen during the reveal
    private bool TooltipSuppressedUntilMove;
    private int LastTooltipMouseX;
    private int LastTooltipMouseY;

    //set when the current tooltip's content changes every frame (a live cooldown countdown), so it rebuilds each frame
    private bool TooltipDynamic;

    //whether the hovered ability had an active cooldown last frame, so the tooltip rebuilds once more when it hits 0
    private bool HoverCooldownWasActive;

    private string? HoveredNpcItemName;

    //the skill or spell slot the cursor is over, plus the player's parsed ability metadata cached at login
    //used to build the hover tooltip's detail from the slot's ability name
    private AbilitySlotControl? HoveredAbilitySlot;
    private AbilityMetadata? PlayerAbilityMetadata;

    //the player's base class from the last self-profile, -1 until one arrives
    //lets the metadata-sync handler re-parse PlayerAbilityMetadata when fresh SClass files land
    private int PlayerBaseClass = -1;
    private LargeWorldHudControl LargeHud = null!;
    private TileClickTracker LeftClickTracker;
    private readonly LightingSystem Lighting = new();

    //true while awaiting a paginated board response, so it appends instead of replacing
    private bool LoadingMoreBoardPosts;
    private MacrosListControl MacrosList = null!;
    private MailListControl MailList = null!;
    private MailReadControl MailRead = null!;
    private ScaleHost MailReadHost = null!;
    private MailSendControl MailSend = null!;
    private MainOptionsControl MainOptions = null!;
    private MapFile? MapFile;
    private MapLoadingBar MapLoading = null!;
    private bool MapPreloaded;
    private List<IPoint> MapWaterTiles = [];
    private MapRenderer MapRenderer = null!;

    //overlay panels rendered on top of the hud
    private NotepadControl Notepad = null!;
    private NpcSessionControl NpcSession = null!;
    //scales and centers the NPC/sign dialog to best-fit the window (it is authored for 640x480)
    //anchored each frame and visibility-synced to NpcSession
    private ScaleHost? NpcSessionHost;
    private DialogDimmer? DialogDim;

    //magnify and center the gold/item amount prompts and the exchange window with the Window-size slider
    //anchored each frame and visibility-synced to the inner control
    private ScaleHost? GoldDropHost;
    private ScaleHost? ItemAmountHost;
    private ScaleHost? ExchangeHost;

    //true while the cursor is over any HUD/menu/window control this frame, so the world goes inert to the mouse
    //same as while an NPC dialog is open, computed in Update
    private bool PointerOverUi;
    private OtherProfileTabControl OtherProfile = null!;
    private Action? PendingBoardSuccessAction;
    private Action? PendingDeleteAction;

    //entity captured on first right-click so a follow-up double-click can still target it even if the camera shifted between clicks
    private uint? PendingDoubleClickEntityId;
    private int PendingDoubleClickTick;
    private bool PendingLoginSwitch;

    //calm login intro, on first world entry hold full black a beat so the map's night LightLevel snaps in while still
    //fully black, then run a slow eased fade-from-black, driven from FinalizeMapLoad with Update counting the hold down
    private bool PendingIntroReveal;
    private float IntroHoldRemaining;
    private const float INTRO_HOLD_SECONDS = 0.3f; //black hold after the first map loads, covering the night snap
    private const float INTRO_FADE_SECONDS = 1.3f; //slow calm reveal from black on login

    //silent reconnect after an unexpected world disconnect, the world freezes under a "Reconnecting..." overlay while we replay the handshake
    //on success we reload a clean world screen, on give-up we drop to the lobby, both switches happen at the top of Update
    private bool Reconnecting;
    private bool PendingReconnectReload;
    private bool PendingReconnectGiveUp;
    private string? ReconnectGiveUpMessage;
    private ReconnectFlow? Reconnect;
    private UIPanel ReconnectDim = null!;
    private UILabel ReconnectLabel = null!;
    private UILabel ReconnectLabelShadow = null!;

    private byte[] PlayerPortrait = [];
    private SelfProfileTextEditorControl SelfProfileTextEditor = null!;
    private ScaleHost ProfileEditorHost = null!;
    private Direction? QueuedWalkDirection;

    //hold-to-walk state, all reset whenever the move button is not held, holding ms since last re-path, the step count then
    //so we re-aim once half is walked, and the last cursor tile we re-pathed toward so an idle player only re-steps on a new tile
    private float HeldWalkRepathTimer;
    private int HeldWalkPathLength;
    private (int X, int Y)? HeldWalkLastTarget;

    //ms of hold-to-walk suppression after entering a new map, so a still-held move button does not instantly auto-path
    //the player back out the door they came in, a fresh button press still paths and only the held auto-repath waits
    private const float HELD_WALK_MAP_ENTRY_SUPPRESS_MS = 1000f;
    private float HeldWalkSuppressMs;

    //ms of keyboard movement suppression after entering a new map, so a still-held movement key does not immediately step
    //the player back into the warp they just arrived from, the held key resumes walking once this elapses
    private const float KEY_MOVE_MAP_ENTRY_SUPPRESS_MS = 400f;
    private float KeyMoveSuppressMs;
    private bool RedirectInProgress;
    private TileClickTracker RightClickTracker;
    private RasterizerState ScissorRasterizerState = null!;

    //true when the client explicitly asked for its own profile, so unsolicited self-profile packets don't open the panel
    private bool SelfProfileRequested;
    private StatusBookTab SelfProfileRequestedTab = StatusBookTab.Equipment;
    private SettingsControl SettingsDialog = null!;
    private SilhouetteRenderer SilhouetteRenderer = null!;
    private WorldHudControl SmallHud = null!;
    private SocialStatusControl SocialStatusPicker = null!;

    //magnifies the social status picker by the "Window size" scale, positioned and scaled each time it opens
    private ScaleHost? SocialStatusHost;
    private long LastSpacebarMs;
    private long LastInteractMs;

    //spam-hint state, warn once per auto-attack session when Space is pressed redundantly, and after 3 redundant
    //clicks on the already-targeted enemy, both reset when the auto-attack target is cleared
    private bool AutoAttackSpaceWarned;
    private uint? RedundantClickTargetId;
    private int RedundantClickCount;
    private SelfProfileTabControl StatusBook = null!;

    //item and money feedback sounds, the item sound plays on any inventory change and the money sound on any gold change
    //both stay silent during the login fill (armed a grace after world entry), and a pure swap cancels to no change so it is silent
    private const int ItemSoundArmDelayMs = 1500;
    private int LastInventoryItemCount;
    private long LastGold;
    private bool ItemSoundArmed;
    private bool HasEnteredWorld;
    private int WorldEntryTick;

    //the local player is dead while their HP is 0, figured out straight from the attributes so there is no flag to reset
    //matches the server, the dying phase keeps HP at 0 and the Sgrios realm spirit walks again because it is given 1 HP
    private static bool IsPlayerDead => WorldState.Attributes.Current is { CurrentHp: <= 0 };

    //set true at the top of UnloadContent, a click handled mid-Update can synchronously switch screens, which unloads
    //this screen and resets WorldState before Update returns, so the frame tail bails on this and never reads torn-down state
    private bool IsUnloaded;

    //wraps StatusBook to magnify it, centered in ShowStatusBook the first time only
    private ScaleHost? StatusBookHost;
    private bool StatusBookCentered;

    //wraps the Group window to magnify it, centered the first time the group panel becomes visible
    private ScaleHost? GroupHost;
    private bool GroupCentered;

    //the friends list and the whole board/mail family are draggable menu windows that follow the Window size slider
    //collected here so the Options slider can rescale them all live, each built by RegisterMenuWindow
    private readonly List<ScaleHost> MenuWindowHosts = [];

    //the always-built magnified menu windows and their hosts, kept as fields so the "Window size" slider can rescale them live
    //the book and group hosts above re-read WindowScale when they open
    private ScaleHost? InvHost;
    private DraggableWindow? StatsWin;
    private ScaleHost? StatsHost;
    private DraggableWindow? SkillWin;
    private ScaleHost? SkillHost;
    private DraggableWindow? SpellWin;
    private ScaleHost? SpellHost;
    private DraggableWindow? ActionsWin;
    private ScaleHost? ActionsHost;
    private OptionsWindow? OptionsWin;
    private ControlsWindow? ControlsWin;
    private EmoteWindow? EmoteWin;
    private DebugFxWindow? DebugFxWin;
    private TabMapEntity[] TabMapEntities = [];
    private TabMapRenderer TabMapRenderer = null!;
    private MapOverviewRenderer MapOverview = null!;
    private bool TabMapVisible;
    private TextPopupControl TextPopup = null!;
    private ScaleHost TextPopupHost = null!;

    //tile cursor, a white dashed ellipse tinted at draw time so one texture serves every state
    private Texture2D? TileCursorTexture;

    //equipment drag ghost, non-owning reference to the dragged slot's item texture, cleared on drop
    private Texture2D? EquipmentDragIcon;

    //ground item drag ghost, non-owning reference to the ground item's panel sprite texture, cleared on drop
    private Texture2D? GroundItemDragIcon;

    //offscreen target for all HUD and window children so they can be drawn at reduced alpha during a dialog
    private RenderTarget2D? HudRenderTarget;

    //tile-cursor tints, orange while hovering walkable ground, blue while dragging an item, red around the auto-attack target
    private static readonly Color TileCursorHoverColor = new(247, 142, 24);
    private static readonly Color TileCursorDragColor = new(100, 149, 237);
    private static readonly Color TileCursorAttackColor = new(220, 40, 40);
    private IWorldHud WorldHud = null!;
    private WorldListControl WorldList = null!;
    private TownMapControl TownMapControl = null!;
    private MinimapControl Minimap = null!;
    private Func<int, int, bool>? MinimapWallLookup; //cached collision lookup for the minimap's inked build
    private WorldMap WorldMap = null!;

    /// <inheritdoc />
    public UIPanel? Root { get; private set; }

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public void Initialize(ChaosGame game)
    {
        Game = game;
        WireServerEvents();
    }



    /// <inheritdoc />
    public void LoadContent(GraphicsDevice graphicsDevice)
    {
        Device = graphicsDevice;

        //create both hud layouts, the '/' key swaps between them
        //zindex -1 so hud frames render behind all popup panels
        SmallHud = new WorldHudControl
        {
            ZIndex = -1
        };

        LargeHud = new LargeWorldHudControl
        {
            Visible = false,
            ZIndex = -1
        };
        WorldHud = SmallHud;

        var viewport = WorldViewport;

        //the camera spans the full world render rect, resized each frame in DrawWorld
        Camera = new Camera(ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight)
        {
            Offset = new Vector2(-28, 24)
        };
        MapRenderer = new MapRenderer();
        TabMapRenderer = new TabMapRenderer();
        MapOverview = new MapOverviewRenderer();
        SilhouetteRenderer = new SilhouetteRenderer(graphicsDevice);
        DarknessRenderer = new DarknessRenderer(graphicsDevice);
        LightingRenderer = new LightingRenderer(graphicsDevice, LoadEmbeddedBytes("light.mgfxo"));
        WeatherRenderer = new WeatherRenderer();

        ScissorRasterizerState = new RasterizerState
        {
            ScissorTestEnable = true
        };

        TileCursorTexture = CreateTileCursorTexture(graphicsDevice, Color.White);

        //overlay panel zindex layers, -2 sub-panels, -1 slide panels, 0 standard, 1 popups, 2 context menu
        NpcSession = new NpcSessionControl();
        WireNpcSession();

        MainOptions = new MainOptionsControl
        {
            ZIndex = -2
        };
        MainOptions.SetViewportBounds(WorldViewport);
        WireOptionsDialog();

        //sub-panels slide out from mainoptions' left edge and render behind it
        var optionsAnchorX = WorldViewport.X + WorldViewport.Width - MainOptions.Width + 10;
        var optionsAnchorY = WorldViewport.Y;

        //seed the user options from the persisted client-local settings
        var userOptions = WorldState.UserOptions;
        userOptions.SetValue(6, ClientSettings.UseGroupWindow);
        userOptions.SetValue(8, ClientSettings.ScrollLevel > 0);
        userOptions.SetValue(9, ClientSettings.UseShiftKeyForAltPanels);
        userOptions.SetValue(10, ClientSettings.EnableProfileClick);
        userOptions.SetValue(11, ClientSettings.RecordNpcChat);
        userOptions.SetValue(12, ClientSettings.GroupOpen);

        //route user-initiated toggles to the server or to local persistence
        userOptions.SettingToggled += (index, value) =>
        {
            if (UserOptions.IsServerSetting(index))
            {
                var option = (UserOption)(index + 1);
                Game.Connection.SendOptionToggle(option);
            } else
            {
                switch (index)
                {
                    case 6:
                        ClientSettings.UseGroupWindow = value;

                        break;
                    case 8:
                        ClientSettings.ScrollLevel = value ? 1 : 0;

                        break;
                    case 9:
                        ClientSettings.UseShiftKeyForAltPanels = value;

                        break;
                    case 10:
                        ClientSettings.EnableProfileClick = value;

                        break;
                    case 11:
                        ClientSettings.RecordNpcChat = value;

                        break;
                    case 12:
                        //server-authoritative, send the toggle and the server responds with an updated profile
                        Game.Connection.ToggleGroup();

                        return;
                }

                ClientSettings.Save();
            }
        };

        SettingsDialog = new SettingsControl(userOptions)
        {
            ZIndex = -3
        };
        SettingsDialog.SetSlideAnchor(optionsAnchorX, optionsAnchorY);

        SettingsDialog.VisibilityChanged += visible =>
        {
            if (visible)
                Game.Connection.SendOptionToggle(UserOption.Request);
        };

        MacrosList = new MacrosListControl
        {
            ZIndex = -3
        };
        MacrosList.SetSlideAnchor(optionsAnchorX, optionsAnchorY);

        HotkeyHelp = new HotkeyHelpControl();

        GroupPanel = new GroupTabControl();

        GroupPanel.MembersPanel.OnKick += name =>
        {
            Game.Connection.SendGroupInvite(ClientGroupSwitch.TryInvite, name);
            //retail sends a SelfProfileRequest after a kick to refresh group state
            Game.Connection.RequestSelfProfile();
        };

        GroupPanel.RecruitPanel.OnCreateGroupBox += (
            name,
            note,
            minLvl,
            maxLvl,
            maxW,
            maxWiz,
            maxR,
            maxP,
            maxM) =>
        {
            Game.Connection.SendCreateGroupBox(
                WorldState.PlayerName,
                name,
                note,
                minLvl,
                maxLvl,
                maxW,
                maxWiz,
                maxR,
                maxP,
                maxM);
            WorldState.Group.MarkGroupBoxActive();
        };

        GroupPanel.RecruitPanel.OnRemoveGroupBox += () =>
        {
            //retail writes the owner's own name in the TargetName field for RemoveGroupBox
            //the server doesn't check it but protocol parity matters
            Game.Connection.SendGroupInvite(ClientGroupSwitch.RemoveGroupBox, WorldState.PlayerName);
            //retail sends a SelfProfileRequest after RemoveGroupBox so the profile response confirms the change
            //queue both packets before flipping the local flag
            Game.Connection.RequestSelfProfile();
            WorldState.Group.MarkGroupBoxInactive();

            //the server clears Aisling.GroupBox but does not broadcast a fresh DisplayAisling, so the overhead
            //banner text stays stale, clear our own banner manually
            if (WorldState.GetPlayerEntity() is { } player)
                player.GroupBoxText = null;
        };

        GroupPanel.RecruitPanel.OnRequestJoin += name => Game.Connection.SendGroupInvite(ClientGroupSwitch.RequestToJoin, name);

        //when the recruit tab opens, ask the server for our own box if one is active
        //the response fills OwnerEdit mode, otherwise RecruitPanel stays in its blank OwnerNew state
        GroupPanel.OnRecruitTabOpened += () =>
        {
            if (WorldState.Group.HasActiveGroupBox)
                Game.Connection.SendGroupInvite(ClientGroupSwitch.ViewGroupBox, WorldState.PlayerName);
            //otherwise nothing, ShowMembers already primed RecruitPanel once per open so tab toggles keep in-progress typing
        };

        GroupBoxViewer = new GroupRecruitPanel(true);

        GroupBoxViewer.OnRequestJoin += name => Game.Connection.SendGroupInvite(ClientGroupSwitch.RequestToJoin, name);

        WorldList = new WorldListControl();

        FriendsList = new FriendsListControl();
        FriendsList.OnOk += SavePlayerFriendList;

        Exchange = new ExchangeControl();

        GoldDrop = new GoldAmountControl
        {
            ZIndex = 2
        };

        GoldDrop.OnConfirm += amount =>
        {
            if (Exchange.Visible && (GoldDrop.TargetEntityId == Exchange.OtherUserId))
                Game.Connection.SendExchangeInteraction(ExchangeRequestType.SetGold, Exchange.OtherUserId, goldAmount: (int)amount);
            else if (GoldDrop.TargetEntityId.HasValue)
                Game.Connection.DropGoldOnCreature((int)amount, GoldDrop.TargetEntityId.Value);
            else
                Game.Connection.DropGold((int)amount, GoldDrop.TargetTileX, GoldDrop.TargetTileY);
        };

        //like retail, while the gold amount popup is open the HUD description bar shows what is being operated on
        //even though nothing is hovered, clear it when the popup closes
        GoldDrop.Closed += () => WorldHud.SetDescription(null);

        ItemAmount = new ItemAmountControl
        {
            ZIndex = 2
        };

        ItemAmount.OnConfirm += amount =>
        {
            Game.Connection.SendExchangeInteraction(
                ExchangeRequestType.AddStackableItem,
                Exchange.OtherUserId,
                ItemAmount.ItemSlot,
                (byte)Math.Min(amount, byte.MaxValue));
        };

        ItemAmount.Closed += () => WorldHud.SetDescription(null);

        BoardList = new BoardListControl();

        ArticleList = new ArticleListControl
        {
            ZIndex = -2
        };

        ArticleRead = new ArticleReadControl
        {
            ZIndex = -2
        };

        ArticleSend = new ArticleSendControl
        {
            ZIndex = -2
        };

        MailList = new MailListControl
        {
            ZIndex = -2
        };

        MailRead = new MailReadControl
        {
            ZIndex = -2
        };

        MailSend = new MailSendControl
        {
            ZIndex = -2
        };
        DeleteConfirm = new OkPopupMessageControl(true)
        {
            ZIndex = 250_000,
            Name = "DeleteConfirm"
        };
        BoardResponsePopup = new OkPopupMessageControl
        {
            ZIndex = 250_000,
            Name = "BoardResponsePopup"
        };

        BoardResponsePopup.OnOk += () => BoardResponsePopup.Hide();

        ExchangeResultPopup = new OkPopupMessageControl
        {
            ZIndex = 250_000,
            Name = "ExchangeResultPopup"
        };
        ExchangeResultPopup.OnOk += () => ExchangeResultPopup.Hide();

        DisconnectPopup = new OkPopupMessageControl(true)
        {
            ZIndex = 250_000,
            Name = "DisconnectPopup"
        };

        //reusable OK/Cancel confirm for one-off questions like "Move to <map>?" or "Travel to <X>?"
        //ZIndex above the world-map modal so the confirm shows on top of it
        ConfirmDialog = new OkPopupMessageControl(true)
        {
            ZIndex = 160_001,
            Name = "ConfirmDialog",

            //a question owns the pointer, the dispatcher swallows every press outside the dialog until it is answered
            //so nothing behind it can react
            IsModal = true
        };

        DisconnectPopup.OnOk += () =>
        {
            DisconnectPopup.Hide();
            Game.Screens.Switch(new LobbyLoginScreen());
        };
        DisconnectPopup.OnCancel += () => Game.Exit();

        //reconnect overlay, a full-window dark veil plus centered "Reconnecting to server..." text, above everything
        //sized and positioned each frame, purely visual since input is frozen by Update returning early while reconnecting
        ReconnectDim = new UIPanel
        {
            Name = "ReconnectDim",
            BackgroundColor = Color.Black * 0.6f,
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 300_000
        };

        ReconnectLabelShadow = new UILabel
        {
            Name = "ReconnectLabelShadow",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomFontSize = RECONNECT_FONT,
            ForegroundColor = new Color(8, 6, 4),
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 300_001
        };

        ReconnectLabel = new UILabel
        {
            Name = "ReconnectLabel",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomFontSize = RECONNECT_FONT,
            ForegroundColor = new Color(236, 224, 200),
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 300_002
        };

        WireExchange();
        WireMailControls();

        StatusBook = new SelfProfileTabControl
        {
            ZIndex = 2
        };

        StatusBook.OnUnequip += slot => Game.Connection.Unequip(slot);
        StatusBook.OnClose += SavePlayerFamilyList;

        StatusBook.OnGroupToggled += () => Game.Connection.ToggleGroup();

        //the equipment book's level-up arrows raise a stat, clicking the emoticon opens the Social status picker at the cursor
        StatusBook.OnRaiseStat += stat => Game.Connection.RaiseStat(stat);
        StatusBook.OnSocialStatusClicked += () => ShowSocialStatusPickerAt(InputBuffer.MouseX, InputBuffer.MouseY);

        //center book-spawned popups in the native ui rect, matching how the book host itself is centered
        StatusBook.OnProfileTextClicked += () =>
        {
            SelfProfileTextEditor.Show(StatusBook.GetProfileText());

            //the host owns placement and magnification, scale by "Window size", center, raise above the book that spawned it
            ProfileEditorHost.Scale = ClientSettings.WindowScale;
            ProfileEditorHost.CenterOnUi();
            ProfileEditorHost.ZIndex = WindowOrder.Next();
        };

        StatusBook.OnAbilityDetailRequested += entry =>
        {
            AbilityMetadataDetails.ShowEntry(entry);

            //the host owns placement and magnification, scale by "Window size", center, raise above the book
            if (AbilityDetailsHost is not null)
            {
                AbilityDetailsHost.Scale = ClientSettings.WindowScale;
                AbilityDetailsHost.CenterOnUi();
                AbilityDetailsHost.ZIndex = WindowOrder.Next();
            }
        };
        StatusBook.OnEventDetailRequested += (entry, state) =>
        {
            EventMetadataDetails.ShowEntry(entry, state);

            if (EventDetailsHost is not null)
            {
                EventDetailsHost.Scale = ClientSettings.WindowScale;
                EventDetailsHost.CenterOnUi();
                EventDetailsHost.ZIndex = WindowOrder.Next();
            }
        };

        //above the centered book host so detail and editor popups it spawns draw on top of it
        SelfProfileTextEditor = new SelfProfileTextEditorControl
        {
            ZIndex = 100010
        };

        SelfProfileTextEditor.OnSave += text =>
        {
            StatusBook.SetProfileText(text);
            SaveProfileText(text);
        };

        AbilityMetadataDetails = new AbilityMetadataDetailsControl
        {
            ZIndex = 100010
        };

        EventMetadataDetails = new EventMetadataDetailsControl
        {
            ZIndex = 100010
        };

        SocialStatusPicker = new SocialStatusControl();

        SocialStatusPicker.OnStatusSelected += status =>
        {
            Game.Connection.SendSocialStatus(status);
            StatusBook.SetEmoticonState((byte)status, UiComponentRepository.GetSocialStatusName(status));

            var emoteIcon = UiRenderer.Instance?.GetEpfTexture("emot000.epf", (int)status * 3);

            if (emoteIcon is not null)
                UpdateHuds(HudOps.SetEmoteIcon, emoteIcon);
        };

        TextPopup = new TextPopupControl();

        //the sign/board fades in while sliding up into place, repositioned every frame, reversed on close
        TextPopupHost = new ScaleHost(TextPopup, ClientSettings.WindowScale)
        {
            ZIndex = 160_000,
            Fades = true,
            FadeSeconds = 0.28f
        };

        Notepad = new NotepadControl
        {
            ZIndex = 2
        };
        Notepad.OnSave += (slot, text) => Game.Connection.SendSetNotepad(slot, text);

        OtherProfile = new OtherProfileTabControl
        {
            ZIndex = 2
        };
        OtherProfile.OnGroupInviteRequested += name => Game.Connection.SendGroupInvite(ClientGroupSwitch.TryInvite, name);

        ChantEdit = new ChantEditControl
        {
            ZIndex = 2
        };
        ChantEdit.OnChantSet += HandleChantSet;

        //ZIndex is set high inside the control since it is a full-screen modal travel window, do not override it here
        WorldMap = new WorldMap(Game.Connection);

        //travel confirm uses the shared in-game OK/Cancel dialog, on OK play the travel cutscene then warp underneath
        WorldMap.TravelRequested += (name, onOk, onCancel) => ConfirmDialog.Confirm(
            $"Travel to {name}?",
            () =>
            {
                Game.PlayTravelCutscene();
                onOk();
            },
            onCancel);

        TownMapControl = new TownMapControl();
        Minimap = new MinimapControl(Device) { ZIndex = 95_000 };

        //clicking the minimap walks the player to that map spot, like a right-click in the world
        Minimap.TargetPicked += (tx, ty) =>
        {
            var p = WorldState.GetPlayerEntity();

            if (p is null)
                return;

            Pathfinding.TargetEntityId = null;
            PathfindToTile(p, tx, ty);
        };

        //clicking a warp on the town map asks to travel, OK closes the map and auto-paths to the warp tile, Cancel keeps it
        TownMapControl.WarpSelected += cluster => ConfirmDialog.Confirm(
            $"Move to {cluster.DestName}?",
            () =>
            {
                TownMapControl.Hide();
                PathfindToWarp(cluster);
            });

        //the warp travel confirm is anchored to the map, when the map goes it goes too
        TownMapControl.Closed += () =>
        {
            if (ConfirmDialog.Visible)
                ConfirmDialog.Hide();
        };

        //while the confirm is up, a non-warp click on the map is inert
        TownMapControl.HasOpenConfirm = () => ConfirmDialog.Visible;

        MapLoading = new MapLoadingBar
        {
            ZIndex = 5
        };

        //center in the full window, re-centered on every Show in case the window was resized
        MapLoading.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));

        AislingContext = new AislingContextMenu
        {
            ZIndex = 3
        };

        ItemTooltip = new ItemTooltipControl
        {
            //above the draggable windows so it is never hidden behind one
            ZIndex = 1_000_000,
            //the dialog's native-resolution text draws on top of the whole Root pass, so skip the tooltip in Root.Draw
            //and let DrawNativeUi paint it last, so a hovered shop or item tooltip always sits on top of the wares text
            SuppressDraw = true
        };

        Root = new WorldRootPanel(this)
        {
            Name = "WorldRoot",
            Width = ChaosGame.UiWidth,
            Height = ChaosGame.UiHeight
        };
        Root.AddChild(SmallHud);
        Root.AddChild(LargeHud);
        //host the NPC/sign dialog in a magnifier so it scales and centers to best-fit the window, high ZIndex so it draws above the hud
        //not draggable, fades a touch slower than a window, PlayFadeSounds off so the window cue does not double the dialog's open sound
        NpcSessionHost = new ScaleHost(NpcSession, 1f) { ZIndex = 150_000, Fades = true, PlayFadeSounds = false, FadeSeconds = 0.18f };
        Root.AddChild(NpcSessionHost);
        //dim the rest of the game view behind the dialog, easing in lockstep with the dialog's fade
        DialogDim = new DialogDimmer(NpcSessionHost) { ZIndex = 149_999 };
        Root.AddChild(DialogDim);
        Root.AddChild(ItemTooltip);
        Root.AddChild(MainOptions);
        Root.AddChild(SettingsDialog);
        Root.AddChild(MacrosList);
        Root.AddChild(HotkeyHelp);

        //magnify and drag the group window, pass-through so empty space reaches the host to drag
        GroupHost = new ScaleHost(GroupPanel, ClientSettings.WindowScale)
        {
            Draggable = true,
            Fades = true,
            ZIndex = 100000
        };
        GroupPanel.IsPassThrough = true;
        GroupPanel.VisibilityChanged += visible =>
        {
            if (!visible || (GroupHost is null))
                return;

            GroupHost.Scale = ClientSettings.WindowScale;

            //center only the first time it opens, after that it stays where the player dragged it
            if (!GroupCentered)
            {
                GroupHost.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
                GroupCentered = true;
            }
        };
        Root.AddChild(GroupHost);

        Root.AddChild(GroupBoxViewer);

        //the friends list and who-is-online list are free-floating menu windows that follow Window size and center on open
        Root.AddChild(RegisterMenuWindow(WorldList));
        Root.AddChild(RegisterMenuWindow(FriendsList));

        //the exchange window is draggable like the equipment book, centered on each new exchange open
        //the gold and item prompts stay anchored each frame since they are quick non-draggable dialogs
        Exchange.IsPassThrough = true;
        ExchangeHost = new ScaleHost(Exchange, ClientSettings.WindowScale) { ZIndex = 140_000, Draggable = true, Fades = true };
        Exchange.VisibilityChanged += visible =>
        {
            if (!visible || (ExchangeHost is null))
                return;

            ExchangeHost.Scale = ClientSettings.WindowScale;
            ExchangeHost.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
        };
        Root.AddChild(ExchangeHost);
        GoldDropHost = new ScaleHost(GoldDrop, ClientSettings.WindowScale) { ZIndex = 140_000, Fades = true };
        Root.AddChild(GoldDropHost);
        ItemAmountHost = new ScaleHost(ItemAmount, ClientSettings.WindowScale) { ZIndex = 140_000, Fades = true };
        Root.AddChild(ItemAmountHost);

        //the whole board and mail family are free-floating menu windows too, centered, scaled, draggable
        //they share one position so navigating board list to a board to a post stays put instead of each popping up centered
        var boardGroup = new WindowGroup();
        Root.AddChild(BoardListHost = RegisterMenuWindow(BoardList, boardGroup, slideOnFade: true));
        Root.AddChild(ArticleListHost = RegisterMenuWindow(ArticleList, boardGroup, slideOnFade: true));
        Root.AddChild(ArticleReadHost = RegisterMenuWindow(ArticleRead, boardGroup, slideOnFade: true));
        Root.AddChild(RegisterMenuWindow(ArticleSend, boardGroup, slideOnFade: true));
        Root.AddChild(MailListHost = RegisterMenuWindow(MailList, boardGroup, slideOnFade: true));
        Root.AddChild(MailReadHost = RegisterMenuWindow(MailRead, boardGroup, slideOnFade: true));
        Root.AddChild(RegisterMenuWindow(MailSend, boardGroup, slideOnFade: true));
        Root.AddChild(WrapOkPopup(DeleteConfirm));
        Root.AddChild(WrapOkPopup(ConfirmDialog));
        Root.AddChild(WrapOkPopup(BoardResponsePopup));
        Root.AddChild(WrapOkPopup(ExchangeResultPopup));
        //host the profile/legend/equipment book in a magnifier, draggable by its background, high ZIndex so it draws above
        //the windows, the book is pass-through so empty-space clicks reach the host to drag
        StatusBookHost = new ScaleHost(StatusBook, ClientSettings.WindowScale)
        {
            Draggable = true,
            Fades = true,
            ZIndex = 100000
        };
        StatusBook.IsPassThrough = true;
        Root.AddChild(StatusBookHost);
        //the profile-text editor lives in a magnifier so it opens at the "Window size" scale like the book that spawns it
        //it visibility-syncs to the inner editor, shown and positioned in the OnProfileTextClicked handler
        ProfileEditorHost = new ScaleHost(SelfProfileTextEditor, ClientSettings.WindowScale) { ZIndex = 100010 };
        Root.AddChild(ProfileEditorHost);
        //the skill/spell and event detail popups live in a magnifier so they open at the "Window size" scale with crisp TTF
        //each host visibility-syncs to its inner control, shown and centered in the detail-requested handlers above
        AbilityDetailsHost = new ScaleHost(AbilityMetadataDetails, ClientSettings.WindowScale) { ZIndex = 100010 };
        EventDetailsHost = new ScaleHost(EventMetadataDetails, ClientSettings.WindowScale) { ZIndex = 100010 };
        Root.AddChild(AbilityDetailsHost);
        Root.AddChild(EventDetailsHost);
        //wrap the other-player profile in the same draggable magnified menu-window host the rest of the books use
        //so it scales with the "Window size" slider and drags by its background like the self-profile book
        Root.AddChild(RegisterMenuWindow(OtherProfile));
        Root.AddChild(TextPopupHost);
        Root.AddChild(Notepad);
        //the chant editor lives in a magnifier so it opens at the "Window size" scale, it visibility-syncs to the inner
        //editor, shown and positioned by OpenChantEdit and re-scaled and re-centered on each open there
        ChantEditHost = new ScaleHost(ChantEdit, ClientSettings.WindowScale);
        Root.AddChild(ChantEditHost);
        Root.AddChild(WorldMap);
        //magnify the social status picker like the other windows, it visibility-syncs to the picker
        SocialStatusHost = new ScaleHost(SocialStatusPicker, ClientSettings.WindowScale)
        {
            ZIndex = 200_000 //above the status book host, below the item tooltip
        };
        Root.AddChild(SocialStatusHost);
        Root.AddChild(AislingContext);

        Root.AddChild(TownMapControl);
        Root.AddChild(Minimap);
        Root.AddChild(MapLoading);
        Root.AddChild(WrapOkPopup(DisconnectPopup));

        Root.AddChild(ReconnectDim);
        Root.AddChild(ReconnectLabelShadow);
        Root.AddChild(ReconnectLabel);

        //new native-resolution UI, the old border HUD is retired
        var menuBar = new MenuBar();
        Root.AddChild(menuBar);

        //a menu-entry tooltip with a title line, a body explaining what the window is for, and the action's current hotkey
        //returns a live provider re-read each time it shows, so rebinding the key in Options updates the tooltip too
        static Func<string?> Tip(string title, string body, GameAction action)
            => () =>
            {
                var (primary, secondary) = Keybindings.Get(action);
                var bind = primary.IsBound ? primary : secondary;
                var hotkey = bind.IsBound ? $"\n\nHotkey: {bind.Display()}" : string.Empty;

                return $"{title}\n{body}{hotkey}";
            };

        //chat window, bottom-left, open by default, fades when the cursor is off it
        const int chatW = 620;
        const int chatH = 300;

        ChatWin = new ChatWindow(chatW, chatH)
        {
            X = 8,
            Y = ChaosGame.UiHeight - chatH - 8
        };
        Root.AddChild(ChatWin);
        ChatWin.Open(); //always present, shows on Enter, pin or fade-timer, no menu entry or close box

        //the normal-mode prefix is the local player's name, the screen owns it
        ChatWin.ChatInput.PlayerNameProvider = () => WorldHud.PlayerName;

        //wire the chat window's typing line to the same dispatch as the HUD input
        ChatWin.ChatInput.MessageSent += HandleChatMessage;
        ChatWin.ChatInput.ShoutSent += msg => Game.Connection.SendShout(msg);
        ChatWin.ChatInput.WhisperSent += (target, msg) => Game.Connection.SendWhisper(target, msg);
        ChatWin.ChatInput.IgnoreAdded += name => Game.Connection.SendAddIgnore(name);
        ChatWin.ChatInput.IgnoreRemoved += name => Game.Connection.SendRemoveIgnore(name);
        ChatWin.ChatInput.IgnoreListRequested += () => Game.Connection.SendIgnoreRequest();

        //host the real inventory grid in a window, detached from the retired HUD but keeping all its wiring
        //shown expanded as the full 5-row grid with the matching background
        SmallHud.DetachTab(HudTab.Inventory);
        var inv = SmallHud.Inventory;
        inv.SetExpanded(true);

        var invBg = UiRenderer.Instance?.GetSpfTexture("_ninv5.spf");

        if (invBg is not null)
        {
            inv.Background = invBg;
            inv.Width = invBg.Width;
            inv.Height = invBg.Height;
        }

        //magnify the small pixel-art inventory grid so it reads well at native resolution
        //flush so the host fills the window and the inventory art's border merges with the wood frame
        InvHost = new ScaleHost(inv, ClientSettings.WindowScale)
        {
            X = 0,
            Y = 0
        };

        InventoryWindow = new DraggableWindow("Inventory", InvHost.Width, InvHost.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true)
        {
            X = 60,
            Y = 60,
            CentersOnFirstShow = true,
            FadeOnOpen = true
        };
        InventoryWindow.Content.AddChild(InvHost);
        InventoryWindow.ContentHost = InvHost;
        Root.AddChild(InventoryWindow);
        menuBar.AddEntry("Inventory", InventoryWindow.Toggle, Tip("Inventory",
            "Everything you are carrying. Drag items to rearrange them, drag one onto the ground to drop it, or drag onto the hotbar to assign a quick-use slot. Hover an item to see its details.",
            GameAction.ToggleInventory));
        //starts closed like every other menu window, the player opens it from Menu > Inventory

        //character stats window, level-up arrows show when there are unspent points, next-level EXP in the labels
        var statusHb = DataContext.UserControls.Get("_nstatus")!;
        StatsWinPanel = new StatsPanel(statusHb)
        {
            Visible = true
        };
        StatsWinPanel.OnRaiseStat += stat => Game.Connection.RaiseStat(stat);
        StatsHost = new ScaleHost(StatsWinPanel, ClientSettings.WindowScale) { X = 0, Y = 0 };
        StatsWin = new DraggableWindow("Stats", StatsHost.Width, StatsHost.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 60, Y = 120, CentersOnFirstShow = true, FadeOnOpen = true };
        StatsWin.Content.AddChild(StatsHost);
        StatsWin.ContentHost = StatsHost;
        Root.AddChild(StatsWin);
        StatsMenuEntry = menuBar.AddEntry("Stats", StatsWin.Toggle, Tip("Stats",
            "Your character's attributes, health and mana, level and experience. When you have unspent stat points, the up-arrows let you raise Strength, Intelligence, Wisdom, Constitution or Dexterity.",
            GameAction.ToggleStats));
        WorldState.Attributes.Changed += PushStatsWindow;
        PushStatsWindow();

        //full Skills and Spells books in windows, auto-bound to WorldState like the inventory window
        //the 3-row grid uses the small HUD's inventory background and is magnified like the inventory window
        var bookHb = DataContext.UserControls.Get("_nbk_s")!;
        var bookBg = UiRenderer.Instance!.GetPrefabTexture("_nbk_s", "InventoryBackground", 0);

        SkillWinPanel = new SkillBookPanel(bookHb, background: bookBg, normalVisibleSlots: 36);
        SkillWinPanel.OnSlotClicked += HandleSkillSlotClicked;
        SkillWinPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SkillWinPanel.OnSlotHoverExit += HandleAbilityHoverExit;
        SkillWinPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        SkillHost = new ScaleHost(SkillWinPanel, ClientSettings.WindowScale) { X = 0, Y = 0 };
        SkillWin = new DraggableWindow("Skills", SkillHost.Width, SkillHost.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 100, Y = 80, CentersOnFirstShow = true, FadeOnOpen = true };
        SkillWin.Content.AddChild(SkillHost);
        SkillWin.ContentHost = SkillHost;
        Root.AddChild(SkillWin);
        menuBar.AddEntry("Skills", SkillWin.Toggle, Tip("Skills",
            "Your skill book: every skill you have learned. Drag a skill onto the hotbar for quick use, or right-click it to edit its chant line. Hover a skill to read what it does and its requirements.",
            GameAction.ToggleSkills));

        SpellWinPanel = new SpellBookPanel(bookHb, background: bookBg, normalVisibleSlots: 36);
        SpellWinPanel.OnSlotClicked += HandleSpellSlotClicked;
        SpellWinPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SpellWinPanel.OnSlotHoverExit += HandleAbilityHoverExit;
        SpellWinPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        SpellWinPanel.OnSlotDroppedOutside += HandleSpellSlotDropped;
        SpellHost = new ScaleHost(SpellWinPanel, ClientSettings.WindowScale) { X = 0, Y = 0 };
        SpellWin = new DraggableWindow("Spells", SpellHost.Width, SpellHost.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 140, Y = 100, CentersOnFirstShow = true, FadeOnOpen = true };
        SpellWin.Content.AddChild(SpellHost);
        SpellWin.ContentHost = SpellHost;
        Root.AddChild(SpellWin);
        menuBar.AddEntry("Spells", SpellWin.Toggle, Tip("Spells",
            "Your spell book: every spell you have learned. Drag a spell onto the hotbar for quick casting, or right-click it to edit its chant line. Hover a spell to read its effect, cast lines and cooldown.",
            GameAction.ToggleSpells));

        //Actions window, the split Skills and Spells abilities panel, detached from the retired HUD so it keeps its
        //wiring and stays registered for the drag ghost, magnified like the other books
        SmallHud.DetachTab(HudTab.Tools);
        var tools = SmallHud.Tools;
        //the Actions window's two halves are real skill and spell panels, wire their hover and right-click
        //like the K/P book windows and the hotbars so slots get tooltips and the chant editor
        tools.WorldSkills.OnSlotHoverEnter += HandleAbilityHoverEnter;
        tools.WorldSkills.OnSlotHoverExit += HandleAbilityHoverExit;
        tools.WorldSpells.OnSlotHoverEnter += HandleAbilityHoverEnter;
        tools.WorldSpells.OnSlotHoverExit += HandleAbilityHoverExit;
        WireAbilityRightClicks(tools.WorldSkills);
        WireAbilityRightClicks(tools.WorldSpells);
        ActionsHost = new ScaleHost(tools, ClientSettings.WindowScale) { X = 0, Y = 0 };
        ActionsWin = new DraggableWindow("Actions", ActionsHost.Width, ActionsHost.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 180, Y = 120, CentersOnFirstShow = true, FadeOnOpen = true };
        ActionsWin.Content.AddChild(ActionsHost);
        ActionsWin.ContentHost = ActionsHost;
        Root.AddChild(ActionsWin);
        menuBar.AddEntry("Actions", ActionsWin.Toggle, Tip("Actions",
            "Your skills and spells side by side in one window, so you can manage both at once. Drag onto the hotbar, right-click to edit a chant, or hover for details.",
            GameAction.ToggleActions));

        //the book-themed profile dialog, Equipment opens the Intro tab and Legend opens the Legend tab
        menuBar.AddEntry(
            "Equipment",
            () =>
            {
                SelfProfileRequested = true;
                SelfProfileRequestedTab = StatusBookTab.Equipment;
                Game.Connection.RequestSelfProfile();
            },
            Tip("Equipment",
                "Your character profile: the gear you are wearing, your appearance, and your profile text. Drag an item from your inventory onto a slot to equip it, or drag an equipped item out to remove it. Click your portrait text to edit what others read.",
                GameAction.ToggleEquipment));

        menuBar.AddEntry(
            "Legend",
            () =>
            {
                SelfProfileRequested = true;
                SelfProfileRequestedTab = StatusBookTab.Legend;
                Game.Connection.RequestSelfProfile();
            },
            Tip("Legend",
                "Your legend: the marks and milestones recorded about your character over its life - achievements, titles, and notable events. Other players can read these when they view your profile.",
                GameAction.ToggleLegend));

        //Group window members, centered and magnified
        menuBar.AddEntry(
            "Group",
            () =>
            {
                Game.Connection.RequestSelfProfile();
                GroupPanel.ShowMembers();
            },
            Tip("Group",
                "Your party. See who is grouped with you and their health, set up recruitment to find members, and (as leader) remove members. Group members share experience and can use group chat.",
                GameAction.ToggleGroup));

        //Friends list, toggles the free-floating window, data is loaded on world entry
        menuBar.AddEntry("Friends", ToggleFriendsWindow, Tip("Friends",
            "Your friends list. See which of your friends are online right now, and keep track of the players you want to stay in touch with.",
            GameAction.ToggleFriends));

        //Who is online, the world-list window, data is requested from the server when opened
        menuBar.AddEntry("Who is online", ToggleWorldListPanel, Tip("Who is online",
            "Everyone currently playing. Filter the list by class using the tabs, and click a name to view that player's profile.",
            GameAction.ToggleWorldList));

        //Mail, message boards and help topics, the R key opens the same panel
        MailMenuEntry = menuBar.AddEntry("Mail & Help", ToggleBoardPanel, Tip("Mail & Help",
            "Read and send personal mail, browse the message boards, and look up help topics. A marker appears here when you have unread mail.",
            GameAction.ToggleBulletinBoard));

        //emote picker, a grid of every emote shown as its real face or bubble graphic in the player's skin tone
        EmoteWin = new EmoteWindow(Game.AislingRenderer) { CentersOnFirstShow = true, FadeOnOpen = true };
        EmoteWin.EmoteChosen += TrySendEmote;
        Root.AddChild(EmoteWin);
        menuBar.AddEntry("Emotes", EmoteWin.Toggle, Tip("Emotes",
            "A grid of every emote your character can perform, each shown with its real animation. Click one to play it. Emotes can also be bound to keys in Options > Controls.",
            GameAction.ToggleEmotes));

        //custom settings window shared with the lobby, each row applies live and persists to config
        //ApplyWindowScale rescales the open hud windows live when the slider moves
        ControlsWin = new ControlsWindow { CentersOnFirstShow = true, FadeOnOpen = true };
        Root.AddChild(ControlsWin);
        OptionsWin = OptionsWindow.Create(Game, ControlsWin, ApplyWindowScale);
        OptionsWin.CentersOnFirstShow = true;
        OptionsWin.FadeOnOpen = true;
        Root.AddChild(OptionsWin);
        menuBar.AddEntry("Options", OptionsWin.Toggle, Tip("Options",
            "Game settings: sound and music volume, interface and minimap sizes, camera and movement feel, tooltips, and more. The Controls button inside opens the keybinding editor where every key can be reassigned.",
            GameAction.ToggleOptions));

        //live render-FX tuning panel, opened by "/debugOptions", a dev tool with no menu entry
        DebugFxWin = new DebugFxWindow { CentersOnFirstShow = true, FadeOnOpen = true, Visible = false };
        Root.AddChild(DebugFxWin);

        //log out back to the login screen, fade to black, the server confirms, we send the real logout,
        //and the redirect switches us to the lobby
        menuBar.AddEntry("Log out", BeginLogout, "Log out\nLeave the world and return to the login screen. Your character is saved automatically.");

        //fixed on-screen hotbars, single-row auto-bound views of the top rows, each self-subscribes to its WorldState view model
        //the large HUD layout is the fully-collapsed single-row look, one row of 12 plus gold, no skill or spell buttons
        var hb = DataContext.UserControls.Get("_nbk_l")!;
        var barBg = UiRenderer.Instance!.GetPrefabTexture(hb.Name, "InventoryBackground", 0);

        SkillBarPanel = new SkillBookPanel(hb, background: barBg, normalVisibleSlots: 12);
        SpellBarPanel = new SpellBookPanel(hb, background: barBg, normalVisibleSlots: 12);
        InvBarPanel = new InventoryPanel(hb, background: barBg, normalVisibleSlots: 12, showGold: false);

        //use or activate on double-click
        SkillBarPanel.OnSlotClicked += HandleSkillSlotClicked;
        SpellBarPanel.OnSlotClicked += HandleSpellSlotClicked;
        InvBarPanel.OnSlotClicked += slot => Game.Connection.UseItem(slot);
        //single-click an empty hotbar slot to open its window, double-click still activates an occupied slot
        SkillBarPanel.OnSlotSingleClicked += HandleSkillBarSlotClicked;
        SpellBarPanel.OnSlotSingleClicked += HandleSpellBarSlotClicked;
        InvBarPanel.OnSlotSingleClicked += HandleInvBarSlotClicked;
        InvBarPanel.OnSlotHoverEnter += HandleInventoryHoverEnter;
        InvBarPanel.OnSlotHoverExit += HandleInventoryHoverExit;
        SkillBarPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SkillBarPanel.OnSlotHoverExit += HandleAbilityHoverExit;
        SpellBarPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SpellBarPanel.OnSlotHoverExit += HandleAbilityHoverExit;

        //drag to rearrange within a bar via a server swap, and drag items or spells out of the bar
        SkillBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        SpellBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        SpellBarPanel.OnSlotDroppedOutside += HandleSpellSlotDropped;
        InvBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.Inventory, s, t);
        InvBarPanel.OnSlotDroppedOutside += HandleInventoryDropInViewport;

        //key labels reflect the player's actual bindings in short form like "1", "S+1", "F1"
        //they refresh whenever a binding changes in the Controls window, all three render through the same text path
        RefreshHotbarSlotLabels();
        Keybindings.Changed += RefreshHotbarSlotLabels;

        SkillBar = new ScaleHost(SkillBarPanel, ClientSettings.HotbarScale);
        SpellBar = new ScaleHost(SpellBarPanel, ClientSettings.HotbarScale);
        InvBar = new ScaleHost(InvBarPanel, ClientSettings.HotbarScale);
        Root.AddChild(InvBar);
        Root.AddChild(SkillBar);
        Root.AddChild(SpellBar);

        HpOrb = new OrbDisplayControl(OrbKind.Hp);
        HpOrb.HoverEntered += () => HoveredOrb = HpOrb;
        HpOrb.HoverExited += () => { if (HoveredOrb == HpOrb) HoveredOrb = null; };
        HpOrb.Clicked += ToggleStatsWindow;
        Root.AddChild(HpOrb);

        MpOrb = new OrbDisplayControl(OrbKind.Mp);
        MpOrb.HoverEntered += () => HoveredOrb = MpOrb;
        MpOrb.HoverExited += () => { if (HoveredOrb == MpOrb) HoveredOrb = null; };
        MpOrb.Clicked += ToggleStatsWindow;
        Root.AddChild(MpOrb);

        //status-effect bar, a fresh EffectBarControl wrapped in a magnifier so the small icons read at the chunky UI scale
        //fed by HandleEffect and anchored on the right in AnchorHotbars
        BuffBar = new EffectBarControl();
        BuffBarHost = new ScaleHost(BuffBar, ClientSettings.HotbarScale * 2f) { ZIndex = 90_000 };
        Root.AddChild(BuffBarHost);


        WireHudPanels(SmallHud);
        WireHudPanels(LargeHud);

        //right-click a skill or spell to edit its chant lines, in the new K/P book windows and the on-screen hotbars
        //the retired HUD panels are wired in WireHudPanels, same OpenChantEdit path as the HUD
        WireAbilityRightClicks(SkillWinPanel!);
        WireAbilityRightClicks(SpellWinPanel!);
        WireAbilityRightClicks(SkillBarPanel!);
        WireAbilityRightClicks(SpellBarPanel!);

        //build the ui atlas after all hud controls are constructed
        UiRenderer.Instance?.BuildAtlas();

        //load the local portrait and profile text from the character folder
        var playerName = Game.Connection.AislingName;
        PlayerPortrait = LoadPortraitFile(playerName);
        StatusBook.SetProfileText(LoadProfileText());

        //retire the old border HUD, keep the objects alive so the many references still resolve but stop drawing it
        //the world now fills the screen and the new menu, windows, orbs and chat overlay it
        SmallHud.Visible = false;
        LargeHud.Visible = false;
    }

    //builds a draggable window with placeholder content and wires it to a Menu dropdown entry
    private void AddMenuWindow(MenuBar menuBar, string name, int width, int height, int x, int y)
    {
        var window = new DraggableWindow(name, width, height)
        {
            X = x,
            Y = y
        };

        window.Content.AddChild(
            new UILabel
            {
                Text = $"{name} window.\nDrag the titlebar.\nClick X to close.",
                X = 8,
                Y = 8,
                Width = width - 16,
                Height = height - 16,
                WordWrap = true,
                ForegroundColor = new Color(200, 198, 190)
            });

        Root!.AddChild(window);
        menuBar.AddEntry(name, window.Toggle);
    }

    //renders the whole current level into a texture scaled to fit the screen, then shows the town-map overlay
    //with the player marker on their real tile, works for every map with no hand-drawn map
    private void ShowTownMap()
    {
        if (MapFile is null)
            return;

        //never over an NPC dialog or a sign/board popup, they own the screen
        if (NpcSession.Visible || TextPopup.Visible)
            return;

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        //renders the level at native scale once, cached per map, the overlay scales it to fit the window each frame
        //foreground into the main target as line work, background into its own flat tint so the floor stays clean
        MapOverview.Generate(
            Device,
            MapRenderer,
            MapFile,
            AnimationTick,
            includeBackground: false,
            separateBackground: true);
        TownMapControl.Show(
            Device,
            MapOverview,
            CurrentMapName,
            WarpData.GetClusters(CurrentMapId),
            player.TileX,
            player.TileY,

            //collision lookup for the floor-plan wall shading, a bounds-safe wrapper around the wall-only check
            (x, y) => MapFile is not null
                      && (x >= 0)
                      && (y >= 0)
                      && (x < MapFile.Width)
                      && (y < MapFile.Height)
                      && IsTileWallBlocked(x, y));
    }

    //auto-paths the player onto the warp's tile, stepping on it triggers the server warp
    private void PathfindToWarp(WarpData.WarpCluster cluster)
    {
        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        Pathfinding.TargetEntityId = null; //not following anything, just walk to the warp tile
        PathfindToTile(player, cluster.RepX, cluster.RepY);
    }

    //last seen player level, so a genuine level-up can play the fanfare exactly once
    //-1 until the first attributes packet, which never plays a sound, reset per session since each login builds a new screen
    private int LastKnownLevel = -1;

    //last seen player HP, so a drop can drive the camera shake and red damage pulse, -1 until the first attributes packet
    private long LastKnownHp = -1;

    //menu entries that flag for attention, Stats on unspent stat points and Mail on unread mail
    //driven from PushStatsWindow, the closed Menu button pulses while either is set
    private MenuItem? StatsMenuEntry;
    private MenuItem? MailMenuEntry;

    private void PushStatsWindow()
    {
        if (WorldState.Attributes.Current is not { } attrs)
            return;

        StatsWinPanel?.UpdateAttributes(attrs);

        //local HP changes drive the camera shake, red damage pulse and the sustained low-HP pulse
        OnPlayerHealthChanged(LastKnownHp, attrs.CurrentHp, attrs.MaximumHp);
        LastKnownHp = attrs.CurrentHp;

        //light up the menu markers from the same attributes the HUD reads
        if (StatsMenuEntry is not null)
            StatsMenuEntry.ShowMarker = attrs.UnspentPoints > 0;

        if (MailMenuEntry is not null)
            MailMenuEntry.ShowMarker = attrs.HasUnreadMail;

        //local level-up fanfare, the server sends only the burst animation so the leveling player hears the sound here
        //only on an actual increase, gated on ItemSoundArmed so the staged login attribute packets are never misread as a level-up
        if ((LastKnownLevel >= 0) && (attrs.Level > LastKnownLevel) && ItemSoundArmed)
            Game.SoundSystem.PlaySound(SoundSystem.SoundLevelUp);

        LastKnownLevel = attrs.Level;
    }

    //logout fades the world to black first, then asks the server to exit, the fade holds at black and once the redirect
    //lands Update switches to the lobby and reveals it with a fade-from-black
    private void BeginLogout()
    {
        Game.FadeToBlackHold(null);
        Game.Connection.RequestExit();
    }

    //silent reconnect
    private const int RECONNECT_FONT = 18;
    private const int RECONNECT_TEXT_H = 40; //label band height, the text is centered within it

    //start the silent reconnect, freeze and darken the world, show the overlay, and kick off the lobby to login to world
    //replay, triggered on an unexpected world disconnect when we have this session's credentials
    private void BeginReconnect()
    {
        Reconnecting = true;
        SetReconnectOverlay(true);
        UpdateReconnectOverlay();

        Reconnect = new ReconnectFlow(Game.Connection, DataContext.LobbyHost, DataContext.LobbyPort, DataContext.ClientVersion);
        Reconnect.Succeeded += OnReconnectSucceeded;
        Reconnect.GaveUp += OnReconnectGaveUp;
        Reconnect.Begin();
    }

    //reconnect reached the world again, reload a clean world screen via the login to world handoff on the next Update
    private void OnReconnectSucceeded()
    {
        Reconnecting = false;
        Reconnect = null;
        PendingReconnectReload = true;
    }

    //reconnect gave up on timeout, rejection or cancel, drop to the lobby on the next Update
    //a non-null message is a login rejection worth showing, a null message is a plain timeout the lobby already covers
    private void OnReconnectGaveUp(string? message)
    {
        Reconnecting = false;
        Reconnect = null;
        ReconnectGiveUpMessage = message;
        PendingReconnectGiveUp = true;
    }

    private void SetReconnectOverlay(bool visible)
    {
        ReconnectDim.Visible = visible;
        ReconnectLabel.Visible = visible;
        ReconnectLabelShadow.Visible = visible;
    }

    //size the veil to the window and center the text on screen, refreshed each frame while reconnecting
    private void UpdateReconnectOverlay()
    {
        var w = ChaosGame.UiWidth;
        var h = ChaosGame.UiHeight;

        ReconnectDim.Width = w;
        ReconnectDim.Height = h;

        const string text = "Reconnecting to server...";

        //centered on screen, Height must be set since a zero-height label has an empty ClipRect and clips its own text away
        //the text centers inside this band
        var y = (h - RECONNECT_TEXT_H) / 2;

        ReconnectLabel.Text = text;
        ReconnectLabel.Width = w;
        ReconnectLabel.Height = RECONNECT_TEXT_H;
        ReconnectLabel.X = 0;
        ReconnectLabel.Y = y;

        ReconnectLabelShadow.Text = text;
        ReconnectLabelShadow.Width = w;
        ReconnectLabelShadow.Height = RECONNECT_TEXT_H;
        ReconnectLabelShadow.X = 2;
        ReconnectLabelShadow.Y = y + 2;
    }

    //applies the Options "Window size" scale live, resizes the always-built menu windows in place and re-scales the
    //book and group hosts, which also re-read WindowScale on open
    private void ApplyWindowScale(float scale)
    {
        ClientSettings.WindowScale = scale; //persisted by the caller's Save, also what new book or group opens read

        RescaleWindow(InventoryWindow, InvHost, scale);
        RescaleWindow(StatsWin, StatsHost, scale);
        RescaleWindow(SkillWin, SkillHost, scale);
        RescaleWindow(SpellWin, SpellHost, scale);
        RescaleWindow(ActionsWin, ActionsHost, scale);

        //rescale in place, the player's dragged position is kept with no re-centering on a scale change
        if (StatusBookHost is not null)
            StatusBookHost.Scale = scale;

        if (GroupHost is not null)
            GroupHost.Scale = scale;

        foreach (var host in MenuWindowHosts)
            host.Scale = scale;
    }

    //shared on-screen position for a set of menu windows, once any of them is placed the rest open at the same spot
    //and write their dragged position back, so a navigation flow reads as one window
    private sealed class WindowGroup
    {
        public bool Positioned;
        public int X;
        public int Y;

        //every host in the group, so opening one can instantly retire whichever sibling is on screen with no cross-fade
        public readonly List<ScaleHost> Members = [];
    }

    //wraps an OkPopupMessageControl in a ScaleHost so it scales with WindowScale and stays centered on screen
    //the host inherits the popup's ZIndex
    private ScaleHost WrapOkPopup(OkPopupMessageControl popup)
    {
        var host = new ScaleHost(popup, ClientSettings.WindowScale)
        {
            ZIndex = popup.ZIndex
        };

        popup.ZIndex = 0;

        popup.VisibilityChanged += visible =>
        {
            if (!visible)
                return;

            popup.X = 0;
            popup.Y = 0;
            host.Scale = ClientSettings.WindowScale;
            host.X = (ChaosGame.UiWidth - host.Width) / 2;
            host.Y = (ChaosGame.UiHeight - host.Height) / 2;
        };

        return host;
    }

    //wraps a panel as a free-floating menu window dragged by its background, centered on first open then left where dragged
    //a group makes several windows share one position so navigating board to post feels like one window, not separate menus
    private ScaleHost RegisterMenuWindow(UIPanel panel, WindowGroup? group = null, bool slideOnFade = false)
    {
        var centered = false;

        var host = new ScaleHost(panel, ClientSettings.WindowScale)
        {
            Draggable = true,
            Fades = true,
            ZIndex = 100000
        };

        if (slideOnFade)
        {
            //the board/mail family slides up as it fades in, a longer ramp than the default so the slide is seen
            //a sub-menu switch snaps the fade so it never slides between pages, only a real open or close does
            host.SlideOnFade = true;
            host.FadeSeconds = 0.18f;
        }

        group?.Members.Add(host);

        panel.VisibilityChanged += visible =>
        {
            if (visible)
            {
                host.Scale = ClientSettings.WindowScale;

                if (group is not null)
                {
                    if (group.Positioned)
                    {
                        host.X = group.X;
                        host.Y = group.Y;
                    } else
                    {
                        host.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
                        group.X = host.X;
                        group.Y = host.Y;
                        group.Positioned = true;
                    }

                    //switch within the group, if a sibling is still on screen this is one window changing content,
                    //snap both so there is no cross-fade, a fresh open with no sibling showing still fades in normally
                    var switching = false;

                    foreach (var other in group.Members)
                        if ((other != host) && other.Visible)
                        {
                            other.SnapHidden();
                            switching = true;
                        }

                    if (switching)
                        host.SnapShown();
                } else if (!centered)
                {
                    host.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
                    centered = true;
                }
            } else if (group is not null)
            {
                //remember where this window ended up including any drag, so the next window in the group opens there
                group.X = host.X;
                group.Y = host.Y;
                group.Positioned = true;
            }
        };

        MenuWindowHosts.Add(host);

        return host;
    }

    private static void RescaleWindow(DraggableWindow? window, ScaleHost? host, float scale)
    {
        if ((window is null) || (host is null))
            return;

        host.Scale = scale;

        //flush windows have the content filling the whole window so no side padding and the bordered content merges with
        //the wood frame, non-flush windows keep the wood chrome plus the small breathing gap
        if (window.FlushContent)
            window.Resize(host.Width, host.Height + DraggableWindow.FRAME + DraggableWindow.TITLE_H);
        else
            window.Resize(host.Width + 2 * DraggableWindow.FRAME + 6, host.Height + 2 * DraggableWindow.FRAME + DraggableWindow.TITLE_H + 6);
    }

    //maps a screen point into the magnified profile book's native space, for the equip-on-drop hit check
    private (int X, int Y) MapToDialog(int x, int y)
    {
        if (NpcSessionHost is null)
            return (x, y);

        var scale = NpcSessionHost.Scale;

        if (scale == 1f)
            return (x, y);

        return (NpcSessionHost.ScreenX + (int)((x - NpcSessionHost.ScreenX) / scale),
            NpcSessionHost.ScreenY + (int)((y - NpcSessionHost.ScreenY) / scale));
    }

    private (int X, int Y) MapToBook(int x, int y)
    {
        if (StatusBookHost is null)
            return (x, y);

        var scale = StatusBookHost.Scale;

        if (scale == 1f)
            return (x, y);

        return (StatusBookHost.ScreenX + (int)((x - StatusBookHost.ScreenX) / scale),
            StatusBookHost.ScreenY + (int)((y - StatusBookHost.ScreenY) / scale));
    }

    //maps a screen point into the magnified Stats window's native space, for the stat-label hover tooltip
    private (int X, int Y) MapToStats(int x, int y)
    {
        if (StatsHost is null)
            return (x, y);

        var scale = StatsHost.Scale;

        if (scale == 1f)
            return (x, y);

        return (StatsHost.ScreenX + (int)((x - StatsHost.ScreenX) / scale),
            StatsHost.ScreenY + (int)((y - StatsHost.ScreenY) / scale));
    }

    //opens the Social status picker at the cursor, toggles closed if already open
    //raised above the status book host so it is never hidden behind the open book
    private void ShowSocialStatusPickerAt(int x, int y)
    {
        if (SocialStatusPicker.Visible)
        {
            SocialStatusPicker.Hide();

            return;
        }

        if (SocialStatusHost is not null)
        {
            SocialStatusHost.Scale = ClientSettings.WindowScale; //pick up the current "Window size" setting
            SocialStatusHost.X = Math.Clamp(x, 0, Math.Max(0, ChaosGame.UiWidth - SocialStatusHost.Width));
            SocialStatusHost.Y = Math.Clamp(y, 0, Math.Max(0, ChaosGame.UiHeight - SocialStatusHost.Height));
        }

        SocialStatusPicker.Show();
    }

    /// <inheritdoc />
    public void UnloadContent()
    {
        IsUnloaded = true;

        //never leave the death greyscale active outside the world, like dying then disconnecting to the lobby
        ChaosGame.WorldSaturation = 1f;

        //detach any in-flight reconnect flow so a torn-down screen can't keep reacting to connection events
        //the normal success and give-up paths already null this out, but a future screen switch could leave one live
        Reconnect?.Abort();
        Reconnect = null;

        Keybindings.Changed -= RefreshHotbarSlotLabels;
        Game.Connection.OnUserId -= HandleUserId;
        Game.Connection.OnMapInfo -= HandleMapInfo;
        Game.Connection.OnMapData -= HandleMapData;
        Game.Connection.OnMapLoadComplete -= HandleMapLoadComplete;
        Game.Connection.OnLocationChanged -= HandleLocationChanged;
        Game.Connection.OnDisplayAisling -= HandleDisplayAisling;
        Game.Connection.OnRemoveEntity -= HandleRemoveEntity;
        Game.Connection.OnClientWalkResponse -= HandleClientWalkResponse;
        Game.Connection.OnAttributes -= HandleAttributes;
        Game.Connection.OnDisplayPublicMessage -= HandleDisplayPublicMessage;
        Game.Connection.OnServerMessage -= HandleServerMessage;
        WorldState.NpcInteraction.DialogChanged -= HandleDialogChanged;
        WorldState.NpcInteraction.MenuChanged -= HandleMenuChanged;
        Game.Connection.OnRefreshResponse -= HandleRefreshResponse;
        WorldState.Exchange.AmountRequested -= HandleExchangeAmountRequested;
        WorldState.Exchange.Closed -= HandleExchangeClosed;
        WorldState.Board.PostListChanged -= HandleBoardPostListChanged;
        WorldState.Board.PostViewed -= HandleBoardPostViewed;
        WorldState.Board.BoardListReceived -= HandleBoardListReceived;
        WorldState.Board.SessionClosed -= HideAllBoardControls;
        WorldState.Board.ResponseReceived -= HandleBoardResponse;
        WorldState.Board.SessionClosed -= ResetBulletinButtonSelection;
        WorldState.Board.SessionClosed -= ResetMailButtonSelection;
        WorldState.GroupInvite.Received -= HandleGroupInviteReceived;
        Game.Connection.OnEditableProfileRequest -= HandleEditableProfileRequest;
        Game.Connection.OnSelfProfile -= HandleSelfProfile;
        Game.Connection.OnOtherProfile -= HandleOtherProfile;
        Game.Connection.OnBodyAnimation -= HandleBodyAnimation;
        Game.Connection.OnAnimation -= HandleAnimation;
        Game.Connection.OnSound -= HandleSound;
        Game.Connection.OnCancelCasting -= CastingSystem.CancelChant;
        Game.Connection.OnMapChangePending -= HandleMapChangePending;
        Game.Connection.OnExitResponse -= HandleExitResponse;
        Game.Connection.OnRedirectReceived -= HandleRedirectReceived;
        Game.Connection.StateChanged -= HandleStateChanged;
        Game.Connection.OnHealthBar -= HandleHealthBar;
        Game.Connection.OnEffect -= HandleEffect;
        Game.Connection.OnLightLevel -= HandleLightLevel;
        Game.OnMetaDataSyncComplete -= HandleMetaDataSyncComplete;
        Game.Connection.OnDisplayReadonlyNotepad -= HandleDisplayReadonlyNotepad;
        Game.Connection.OnDisplayEditableNotepad -= HandleDisplayEditableNotepad;
        Game.Connection.OnWorldMap -= HandleWorldMap;
        Game.Connection.OnDoor -= HandleDoor;
        WorldState.Attributes.Changed -= PushStatsWindow;

        //unwire the panel click-to-use events
        WorldHud.Inventory.OnSlotClicked -= HandleInventorySlotClicked;
        WorldHud.SkillBook.OnSlotClicked -= HandleSkillSlotClicked;
        WorldHud.SkillBookAlt.OnSlotClicked -= HandleSkillSlotClicked;
        WorldHud.SpellBook.OnSlotClicked -= HandleSpellSlotClicked;
        WorldHud.SpellBookAlt.OnSlotClicked -= HandleSpellSlotClicked;
        WorldHud.Tools.WorldSkills.OnSlotClicked -= HandleSkillSlotClicked;
        WorldHud.Tools.WorldSpells.OnSlotClicked -= HandleSpellSlotClicked;

        WorldState.ResetAll();

        HudRenderTarget?.Dispose();
        HudRenderTarget = null;
        BloomA?.Dispose();
        BloomA = null;
        BloomB?.Dispose();
        BloomB = null;
        MapRenderer.Dispose();
        TabMapRenderer.Dispose();
        MapOverview.Dispose();
        ScissorRasterizerState.Dispose();
        DarknessRenderer.Dispose();
        LightingRenderer.Dispose();
        ShadowBlob?.Dispose();
        WeatherRenderer.Dispose();
        SilhouetteRenderer.Dispose();
        Root?.Dispose();
        Game.AislingRenderer.ClearCompositeCache();
        Game.AislingRenderer.ClearGroupTintCache();
        Game.CreatureRenderer.ClearTintCaches();
        Game.ItemRenderer.Clear();
        Overlays.Clear();
        DebugRenderer.Clear();
    }
}