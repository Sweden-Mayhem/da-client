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
    //walk queue: when walk animation is >= 75% complete, one walk can be queued
    private const float WALK_QUEUE_THRESHOLD = 0.75f;

    //minimum interval between spacebar assail fires when held (os key-repeat rate varies)
    private const long SPACEBAR_INTERVAL_MS = 100;

    //minimum interval between Pick Up / Interact fires, so holding or mashing the key can't spam the server
    private const long INTERACT_INTERVAL_MS = 250;

    //when nothing is adjacent, Pick Up / Interact reaches out to the closest ground item within this many tiles
    private const int INTERACT_REACH_TILES = 4;

    //hold-to-walk: while the move button is held, the player continuously re-paths toward the cursor at least this
    //often (ms), so a cursor held far away keeps being re-aimed every second even before its current path runs out
    private const float HELD_WALK_REPATH_INTERVAL_MS = 1000f;

    //stripe-pass alpha for transparent (invisible) aislings. 1/3 is chosen so that for the silhouetted
    //local player, the stripe draw compounds with the silhouette overdraw to produce the target visibility:
    //    TRANSPARENT_ALPHA + TRANSPARENT_SILHOUETTE_ALPHA * SILHOUETTE_ALPHA * (1 - TRANSPARENT_ALPHA) = 0.5
    //i.e., ~50% in the open and ~25% behind foregrounds (occlusion × transparency = 50% × 50%).
    private const float TRANSPARENT_ALPHA = 1f / 3f;

    //silhouette-RT alpha for transparent entities. 0.5 makes the overlay's effective contribution
    //TRANSPARENT_SILHOUETTE_ALPHA * SILHOUETTE_ALPHA = 0.25, matching the behind-foreground target.
    private const float TRANSPARENT_SILHOUETTE_ALPHA = 0.5f;

    //set true while the silhouette pre-render callback is drawing entities into the silhouette RT.
    //used by DrawAisling to route transparent players through the silhouette pass instead of the stripe pass.
    private bool DrawingForSilhouette;

    //entity hitbox dimensions (screen pixels)
    private const int HITBOX_WIDTH = 28;
    private const int HITBOX_HEIGHT = 60;

    //doubleclick entity cache expiry; slightly larger than the dispatcher's 300ms double-click window so the cache
    //remains valid through the full doubleclick detection window
    private const int DOUBLE_CLICK_CACHE_WINDOW_MS = 550;

    private const string SPOUSE_PREFIX = "Spouse: ";
    private const string GROUP_MEMBERS_PREFIX = "Group members";

    private readonly CastingSystem CastingSystem = new();

    private readonly WorldDebugRenderer DebugRenderer = new();

    //draw-pass hitbox list: rebuilt every frame during entity rendering, in draw order (back-to-front)
    private readonly List<EntityHitBox> EntityHitBoxes = new(256);

    //set of entity ids currently highlighted as group members (auto-expires after 1000ms)
    private readonly HashSet<uint> GroupHighlightedIds = [];
    private readonly EntityOverlayManager Overlays = new();
    private readonly PathfindingState Pathfinding = new();
    //FIFO of the destination tiles of walks we've predicted but the server hasn't confirmed yet (one entry per
    //Walk packet, in send order). The server sends one ClientWalkResponse per accepted walk in the same order, so
    //each response confirms the OLDEST predicted destination. We compare the server's resulting tile to that
    //prediction: if they match we stay in sync (no-op); if they DIVERGE - or there's no prediction (a genuine
    //server-initiated walk: push tile, knockback, teleport) - we hard-resync to the server's tile. This is what
    //makes a desync self-heal on the very next step instead of rubber-banding forever (the old bare counter could
    //never tell a confirm from a divergence, so once the two drifted apart it stayed stuck until relog).
    //A refused walk sends NO response (the server Refreshes -> Location), so its prediction lingers here until the
    //next response detects the mismatch and clears the queue.
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
    //the board + mail list hosts, captured so their rows can be drawn as native-resolution TTF (DrawRowsNative)
    private ScaleHost BoardListHost = null!;
    private ScaleHost MailListHost = null!;

    //board/mail controls (7 instances for 7 prefabs)
    private bool AwaitingMapData;
    private BoardListControl BoardList = null!;
    private OkPopupMessageControl BoardResponsePopup = null!;
    private Camera Camera = null!;

    //the rectangle the game world renders into. Now independent of the old HUD: the world fills the
    //whole screen and the new UI (menu, windows, HP/MP, chat) overlays on top. This is the single
    //source of truth, so the next step (its own low-res render target, native-res UI) changes only here.
    private Rectangle WorldViewport => new(0, 0, ChaosGame.VIRTUAL_WIDTH, ChaosGame.VIRTUAL_HEIGHT);

    //the actual world render-target rect: expands to fill the window so tiles render where the letterbox bars used
    //to be. Used for the camera + all tile/foreground/weather/darkness rendering. (WorldViewport stays 640x480 for
    //the legacy popups, which keep their original centered layout.)
    private Rectangle WorldRenderRect => new(0, 0, ChaosGame.WorldRenderWidth, ChaosGame.WorldRenderHeight);

    //the native region the world is drawn into (full window for the world screen). World clicks are gated against
    //this in native mouse space; the camera/tile math works in the WorldRenderRect render space.
    private Rectangle WorldInputBounds => ChaosGame.WorldDrawRect;

    //real inventory grid (detached from the old HUD) hosted in a draggable window; toggled by Menu / A.
    private DraggableWindow? InventoryWindow;

    //the new chat window (hosts a working typing line, since the old HUD's chat input is hidden)
    private ChatWindow? ChatWin;
    private bool ChatDefaultSized; //one-time: fit the chat's default width into the lower-left gap beside the HP orb

    //the chat input the focus/typing shortcuts target: the window's (visible) input, falling back to the HUD's
    private ChatInputControl ActiveChatInput => ChatWin?.ChatInput ?? WorldHud.ChatInput;

    //fixed on-screen hotbars (single-row, auto-bound views): inventory top-center, skills+spells bottom-center.
    private ScaleHost? InvBar;
    private ScaleHost? SkillBar;
    private ScaleHost? SpellBar;
    //eased 0..1 alpha applied to all three hotbars; dims to HOTBAR_TARGET_ALPHA while targeting a spell
    private float TargetingHotbarAlpha = 1f;

    //hp/mp orbs flanking the bottom (spell) hotbar
    private OrbDisplayControl? HpOrb;
    private OrbDisplayControl? MpOrb;
    private OrbDisplayControl? HoveredOrb;

    //status-effect / buff bar on the right edge (server-driven via OnEffect). The old HUD has one too but it's hidden
    //with the retired HUD, so this is a fresh instance shown in the new UI; magnified by BuffBarHost, anchored each frame.
    private EffectBarControl? BuffBar;
    private ScaleHost? BuffBarHost;

    //the entity a readied spell will hit (closest to the ground cursor), cached once per frame so the blue highlight
    //(entity tint + ground ring + bezier) all reference the same target. Null when not casting.
    private uint? CastTargetId;


    //inner panels of the hotbars (referenced for drag routing / GetDraggingPanel)
    private InventoryPanel? InvBarPanel;
    private SkillBookPanel? SkillBarPanel;
    private SpellBookPanel? SpellBarPanel;

    //full Skills/Spells books hosted in windows (Menu -> Skills/Spells), referenced for drag routing
    private SkillBookPanel? SkillWinPanel;
    private SpellBookPanel? SpellWinPanel;

    //character stats hosted in a window (Menu -> Stats): level-up buttons + next-level EXP
    private StatsPanel? StatsWinPanel;
    private ExtendedStatsPanel? ExtStatsWinPanel;

    private ChantEditControl ChantEdit = null!;

    //the chant editor magnified by the "Window size" setting (like the inventory/book windows); centered + raised on open
    private ScaleHost ChantEditHost = null!;
    private ushort CurrentMapCheckSum;
    private MapFlags CurrentMapFlags;
    private short CurrentMapId;
    private string CurrentMapName = string.Empty;

    private DarknessRenderer DarknessRenderer = null!;

    //deferred lighting (additive light buffer + multiply). DarknessRenderer still owns the ambient day/night LEVEL;
    //LightingRenderer draws the glows, replacing the old flat carve overlay.
    private LightingRenderer LightingRenderer = null!;
    private WeatherRenderer WeatherRenderer = null!;

    //total elapsed seconds, refreshed each Update, driving the tile-light flame flicker (LightingSystem.Gather)
    private float LightAnimTime;
    private OkPopupMessageControl DeleteConfirm = null!;
    private OkPopupMessageControl ConfirmDialog = null!; //reusable OK/Cancel question (warp-to, future prompts)
    private GraphicsDevice Device = null!;
    private OkPopupMessageControl DisconnectPopup = null!;

    //event detail popup (from events tab)
    private EventMetadataDetailsControl EventMetadataDetails = null!;
    private ExchangeControl Exchange = null!;
    private OkPopupMessageControl ExchangeResultPopup = null!;
    private ItemAmountControl ItemAmount = null!;

    private FriendsListControl FriendsList = null!;

    private ChaosGame Game = null!;
    private GoldAmountControl GoldDrop = null!;
    private GroupRecruitPanel GroupBoxViewer = null!;

    //true when j was pressed; the next selfprofile response triggers group highlighting instead of opening the panel
    private bool GroupHighlightRequested;
    private float GroupHighlightTimer;
    private GroupTabControl GroupPanel = null!;
    private HotkeyHelpControl HotkeyHelp = null!;
    private PanelSlot? HoveredInventorySlot;
    private bool IsGameMaster;
    private bool NoClip;
    private ItemTooltipControl ItemTooltip = null!;

    //tooltip resolver state: the most recent hover target's identity (slot ref / EquipmentSlot / StatInfoKind / NPC item
    //name), and how long the cursor has rested before the tooltip is shown (ClientSettings.TooltipDelaySeconds). The NPC
    //shop/dialog reports its hovered item name here; everything is shown through the one resolver in UpdateTooltips.
    private object? TooltipKey;
    private object? TooltipClickSuppressedKey;
    private float TooltipDelayTimer;

    //after the world fades in on connect, tooltips are suppressed until the cursor actually moves - so a tooltip for
    //whatever the (stationary) cursor happens to rest on (e.g. an orb) doesn't hang on screen during the reveal.
    private bool TooltipSuppressedUntilMove;
    private int LastTooltipMouseX;
    private int LastTooltipMouseY;

    //set by ResolveTooltip when the current tooltip's content changes every frame (a live cooldown countdown), so
    //UpdateTooltips rebuilds it each frame instead of only on a target change.
    private bool TooltipDynamic;

    //whether the hovered ability had an active cooldown last frame, so the tooltip rebuilds once more when it hits 0
    private bool HoverCooldownWasActive;

    private string? HoveredNpcItemName;

    //the skill/spell slot (hotbar or K/P book window) the cursor is over, and the player's parsed ability metadata
    //(cached at login) used to build the hover tooltip's detail from the slot's ability name
    private AbilitySlotControl? HoveredAbilitySlot;
    private AbilityMetadata? PlayerAbilityMetadata;

    //the player's base class from the last self-profile (-1 until one arrives); lets the metadata-sync handler
    //re-parse PlayerAbilityMetadata when fresh SClass files land after the profile already parsed the old ones
    private int PlayerBaseClass = -1;
    private LargeWorldHudControl LargeHud = null!;
    private TileClickTracker LeftClickTracker;
    private readonly LightingSystem Lighting = new();

    //true while awaiting a paginated board response (append instead of replace)
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

    //overlay panels (rendered on top of hud)
    private NotepadControl Notepad = null!;
    private NpcSessionControl NpcSession = null!;
    //scales + centers the NPC/sign dialog to best-fit the window (the dialog is authored for 640x480). Anchored each
    //frame in AnchorNpcDialog; visibility-syncs to NpcSession.
    private ScaleHost? NpcSessionHost;
    private DialogDimmer? DialogDim;

    //magnify + center the gold/item amount prompts and the exchange/trade window with the Window-size slider, like the
    //menu windows (anchored each frame in AnchorPopups; visibility-syncs to the inner control).
    private ScaleHost? GoldDropHost;
    private ScaleHost? ItemAmountHost;
    private ScaleHost? ExchangeHost;

    //true while the cursor is over any HUD/menu/window control this frame - the world goes inert to the mouse (no
    //ground marker, hand cursor, or entity-name hover), same as while an NPC dialog is open. Computed in Update.
    private bool PointerOverUi;
    private (int X, int Y)? HoveredFgTile;
    private OtherProfileTabControl OtherProfile = null!;
    private Action? PendingBoardSuccessAction;
    private Action? PendingDeleteAction;

    //entity captured on first right-click so a follow-up double-click can still target it even if pathfinding has shifted the camera between clicks
    private uint? PendingDoubleClickEntityId;
    private int PendingDoubleClickTick;
    private bool PendingLoginSwitch;

    //calm login intro: on first world entry, hold full black a beat (so the map's night/darkness LightLevel snaps in
    //while still fully black, instead of the world revealing bright and popping to night a few frames later), THEN run
    //a slow eased fade-from-black. Driven from FinalizeMapLoad (sets the flag) + Update (counts the hold down).
    private bool PendingIntroReveal;
    private float IntroHoldRemaining;
    private const float INTRO_HOLD_SECONDS = 0.3f; //black hold after the first map loads, covering the night snap
    private const float INTRO_FADE_SECONDS = 1.3f; //slow, calm reveal from black on login

    //silent reconnect after an unexpected world disconnect: the world freezes + darkens under a "Reconnecting..."
    //overlay while ReconnectFlow replays the lobby -> login -> world handshake. On success we reload a clean world
    //screen (PendingReconnectReload); on give-up we drop to the lobby (PendingReconnectGiveUp). Both switches happen
    //at the top of Update (never re-entrantly from inside the flow's events). Reconnecting gates HandleStateChanged
    //and freezes the world sim.
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

    //hold-to-walk state (all reset whenever the move button is not held): ms since the last re-path, the step count
    //of the path at that re-path (so we can re-aim once HALF of it has been walked), and the last cursor tile we
    //re-pathed toward (so an idle player only re-steps when the cursor points somewhere new - which it does every
    //arrival as the camera follows - and an unreachable / own tile doesn't spin A* every frame).
    private float HeldWalkRepathTimer;
    private int HeldWalkPathLength;
    private (int X, int Y)? HeldWalkLastTarget;

    //ms of hold-to-walk suppression after entering a new map. Set on map entry so that if the move button is STILL held
    //from before the warp, the player doesn't instantly auto-path (e.g. straight back out of the door they came in) -
    //the cursor is wherever it happened to be. A fresh button PRESS still paths; only the held auto-repath waits.
    private const float HELD_WALK_MAP_ENTRY_SUPPRESS_MS = 1000f;
    private float HeldWalkSuppressMs;

    //ms of KEYBOARD movement suppression after entering a new map: if an arrow / movement key is still held from before
    //the warp, MoveOrTurn ignores it briefly so the player doesn't immediately step (e.g. straight back into the warp
    //they just arrived from) before the new map is on screen. The held key resumes walking once this elapses.
    private const float KEY_MOVE_MAP_ENTRY_SUPPRESS_MS = 400f;
    private float KeyMoveSuppressMs;
    private bool RedirectInProgress;
    private TileClickTracker RightClickTracker;
    private RasterizerState ScissorRasterizerState = null!;

    //true when the client explicitly requested its own profile; prevents unsolicited selfprofile packets from opening the panel
    private bool SelfProfileRequested;
    private StatusBookTab SelfProfileRequestedTab = StatusBookTab.Equipment;
    private SettingsControl SettingsDialog = null!;
    private SilhouetteRenderer SilhouetteRenderer = null!;
    private WorldHudControl SmallHud = null!;
    private SocialStatusControl SocialStatusPicker = null!;

    //magnifies the social status picker by the "Window size" scale; positioned + scaled each time it opens
    private ScaleHost? SocialStatusHost;
    private long LastSpacebarMs;
    private long LastInteractMs;

    //spam-hint state: warn once per auto-attack session when Space is pressed redundantly, and after 3 redundant
    //clicks on the already-targeted enemy. Both reset when the auto-attack target is cleared.
    private bool AutoAttackSpaceWarned;
    private uint? RedundantClickTargetId;
    private int RedundantClickCount;
    private SelfProfileTabControl StatusBook = null!;

    //item + money feedback sounds (custom embedded SoundSystem.SoundItem / SoundMoney). The item sound plays on ANY
    //inventory change (item gained OR lost); the money sound on any gold change. Both stay silent during the login
    //inventory/gold fill (armed a grace after world entry) - and because the baselines are measured per frame, a pure
    //item swap (an add + a remove in the same packet drain) cancels to no change and is silent. See UpdateFeedbackSounds.
    private const int ItemSoundArmDelayMs = 1500;
    private int LastInventoryItemCount;
    private long LastGold;
    private bool ItemSoundArmed;
    private bool HasEnteredWorld;
    private int WorldEntryTick;

    //the local player is dead while their HP is 0 - read straight from the authoritative attributes, so there is no
    //tracked flag to set or reset. Matches the server exactly: the skull/dying phase keeps HP at 0 (the server's IsAlive
    //gates block everything), and the Sgrios realm spirit walks again because it is given 1 HP on arrival.
    private static bool IsPlayerDead => WorldState.Attributes.Current is { CurrentHp: <= 0 };

    //set true at the top of UnloadContent. A click handled mid-Update can synchronously Switch screens (e.g. the
    //disconnect popup's OK -> lobby), which unloads this screen and resets WorldState before Update returns. The frame
    //tail bails on this so it never reads torn-down state (which would, e.g., see gold reset to 0 and play the money cue).
    private bool IsUnloaded;

    //wraps StatusBook to magnify it (ClientSettings.WindowScale); centered in ShowStatusBook the first time only
    private ScaleHost? StatusBookHost;
    private bool StatusBookCentered;

    //wraps the Group window to magnify it; centered the first time the group panel becomes visible only
    private ScaleHost? GroupHost;
    private bool GroupCentered;

    //the friends list + the whole board/mail family are simple draggable "menu windows" (drag by background art, center
    //the first time they open, follow the Window size slider). Collected here so the Options slider can rescale them all
    //live: each is built by RegisterMenuWindow and rescaled in ApplyWindowScale.
    private readonly List<ScaleHost> MenuWindowHosts = [];

    //the always-built magnified menu windows + their hosts, kept as fields so the Options "Window size" slider can
    //rescale and resize them live (ApplyWindowScale). The book/group above re-read WindowScale when they open.
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

    //tile cursor: a WHITE dashed ellipse, tinted at draw time so one texture serves every state
    private Texture2D? TileCursorTexture;

    //equipment drag ghost: non-owning reference to the dragged slot's item texture, cleared on drop
    private Texture2D? EquipmentDragIcon;

    //ground item drag ghost: non-owning reference to the ground item's panel sprite texture, cleared on drop
    private Texture2D? GroundItemDragIcon;

    //offscreen target used to render all HUD/window children so they can be drawn at reduced alpha during a dialog
    private RenderTarget2D? HudRenderTarget;

    //tile-cursor tints (a "color set" - tweak these to recolor the cursor): orange while hovering walkable ground,
    //cornflower blue while dragging an item, red around the enemy we are auto-attacking
    private static readonly Color TileCursorHoverColor = new(247, 142, 24);
    private static readonly Color TileCursorDragColor = new(100, 149, 237);
    private static readonly Color TileCursorAttackColor = new(220, 40, 40);
    private IWorldHud WorldHud = null!;
    private WorldListControl WorldList = null!;
    private TownMapControl TownMapControl = null!;
    private MinimapControl Minimap = null!;
    private Func<int, int, bool>? MinimapWallLookup; //cached collision lambda for the minimap's inked build
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

        ClientSettings.OnEffectiveWindowScaleChanged += OnWindowScaleChanged;
    }



    /// <inheritdoc />
    public void LoadContent(GraphicsDevice graphicsDevice)
    {
        Device = graphicsDevice;

        //create both hud layouts ('/' key swaps between them)
        //zindex=-1 so hud frames render behind all popup panels
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

        //the camera spans the full world render rect (expands to fill the window); resized each frame in DrawWorld
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

        //overlay panels. zindex: -2 sub-panels, -1 slide panels, 0 standard (default), 1 popups, 2 context menu
        NpcSession = new NpcSessionControl();
        WireNpcSession();

        MainOptions = new MainOptionsControl
        {
            ZIndex = -2
        };
        MainOptions.SetViewportBounds(WorldViewport);
        WireOptionsDialog();

        //sub-panels slide out from mainoptions' left edge, render behind it
        var optionsAnchorX = WorldViewport.X + WorldViewport.Width - MainOptions.Width + 10;
        var optionsAnchorY = WorldViewport.Y;

        //initialize client-local settings into useroptions from persisted config
        var userOptions = WorldState.UserOptions;
        userOptions.SetValue(6, ClientSettings.UseGroupWindow);
        userOptions.SetValue(8, ClientSettings.ScrollLevel > 0);
        userOptions.SetValue(9, ClientSettings.UseShiftKeyForAltPanels);
        userOptions.SetValue(10, ClientSettings.EnableProfileClick);
        userOptions.SetValue(11, ClientSettings.RecordNpcChat);
        userOptions.SetValue(12, ClientSettings.GroupOpen);

        //route user-initiated toggles to server or local persistence
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
                        //server-authoritative; send toggle, server responds with updated profile
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
            // Retail sends a SelfProfileRequest (0x2D) after a kick to refresh group state.
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
            //RemoveGroupBox (0x2E/6) writes the owner's own name in the TargetName
            //field on the wire per the retail client. The server doesn't check the value but protocol parity matters.
            Game.Connection.SendGroupInvite(ClientGroupSwitch.RemoveGroupBox, WorldState.PlayerName);
            //Retail sends a SelfProfileRequest (0x2D) after RemoveGroupBox so the
            //server's profile response confirms the state transition. Queue both
            //packets on the wire before flipping the local flag.
            Game.Connection.RequestSelfProfile();
            WorldState.Group.MarkGroupBoxInactive();

            //Server's RemoveGroupBox handler sets Aisling.GroupBox = null but does
            //NOT broadcast Display(), so no fresh DisplayAisling (0x33) packet
            //arrives and WorldEntity.GroupBoxText stays stale. Clear our own
            //overhead banner manually.
            if (WorldState.GetPlayerEntity() is { } player)
                player.GroupBoxText = null;
        };

        GroupPanel.RecruitPanel.OnRequestJoin += name => Game.Connection.SendGroupInvite(ClientGroupSwitch.RequestToJoin, name);

        // When the user clicks TAB1, query the server for our own box if we have one active.
        // The server's ShowGroupBox(self) response routes to GroupPanel.ShowRecruitOwnerEdit
        // via HandleGroupInviteReceived, populating OwnerEdit mode. Otherwise RecruitPanel
        // stays in its default OwnerNew (blank) state.
        GroupPanel.OnRecruitTabOpened += () =>
        {
            if (WorldState.Group.HasActiveGroupBox)
                Game.Connection.SendGroupInvite(ClientGroupSwitch.ViewGroupBox, WorldState.PlayerName);
            //else: no action. GroupTabControl.ShowMembers already primed RecruitPanel to
            //OwnerNew mode with defaults once per panel-open, so tab toggles preserve any
            //in-progress typing in the recruit fields.
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

        //match retail: while the gold amount popup is open, the HUD description bar shows what's
        //being operated on even though nothing is hovered. clear it when the popup closes.
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

        //reusable OK/Cancel confirm for one-off questions (e.g. "Move to <map>?" from the town map, "Travel to <X>?"
        //from the world map). ZIndex is above the world-map modal (160000) so the confirm shows on top of it.
        ConfirmDialog = new OkPopupMessageControl(true)
        {
            ZIndex = 160_001,
            Name = "ConfirmDialog",

            //a question OWNS the pointer: the dispatcher swallows every press outside the dialog until it is
            //answered (OK/Cancel/Escape) - nothing behind it (the town map, the world) can react
            IsModal = true
        };

        DisconnectPopup.OnOk += () =>
        {
            DisconnectPopup.Hide();
            Game.Screens.Switch(new LobbyLoginScreen());
        };
        DisconnectPopup.OnCancel += () => Game.Exit();

        //reconnect overlay: a full-window dark veil + bottom-center "Reconnecting to server..." text, above EVERYTHING
        //(including the disconnect popup at 250000). Sized/positioned each frame in UpdateReconnectOverlay; purely visual
        //(non-hit-testable) - input is frozen by Update returning early while Reconnecting.
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

        //the equipment book's level-up arrows raise a stat; clicking the emoticon opens the Social status picker at the cursor
        StatusBook.OnRaiseStat += stat => Game.Connection.RaiseStat(stat);
        StatusBook.OnSocialStatusClicked += () => ShowSocialStatusPickerAt(InputBuffer.MouseX, InputBuffer.MouseY);

        //center book-spawned popups in the NATIVE ui rect (not the legacy 640x480 / world viewport, which lands them
        //upper-left), matching how the book host itself is centered
        StatusBook.OnProfileTextClicked += () =>
        {
            SelfProfileTextEditor.Show(StatusBook.GetProfileText());

            //the host owns placement + magnification (like the chant editor): scale by "Window size", center, raise above
            //the book that spawned it in the shared window stack.
            ProfileEditorHost.Scale = ClientSettings.EffectiveWindowScale;
            ProfileEditorHost.CenterOnUi();
            ProfileEditorHost.ZIndex = WindowOrder.Next();
        };

        StatusBook.OnAbilityDetailRequested += entry =>
        {
            AbilityMetadataDetails.ShowEntry(entry);

            //the host owns placement + magnification: scale by "Window size", center on the UI, raise above the book
            if (AbilityDetailsHost is not null)
            {
                AbilityDetailsHost.Scale = ClientSettings.EffectiveWindowScale;
                AbilityDetailsHost.CenterOnUi();
                AbilityDetailsHost.ZIndex = WindowOrder.Next();
            }
        };
        StatusBook.OnEventDetailRequested += (entry, state) =>
        {
            EventMetadataDetails.ShowEntry(entry, state);

            if (EventDetailsHost is not null)
            {
                EventDetailsHost.Scale = ClientSettings.EffectiveWindowScale;
                EventDetailsHost.CenterOnUi();
                EventDetailsHost.ZIndex = WindowOrder.Next();
            }
        };

        //above the centered book host (ZIndex 100000) so detail/editor popups it spawns draw ON TOP of it, not behind
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

        //the sign/board fades in while sliding up into place (AnchorTextPopup repositions it every frame off
        //OpenFraction); the same path reversed plays on close
        TextPopupHost = new ScaleHost(TextPopup, ClientSettings.EffectiveWindowScale)
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

        //ZIndex is set high inside the control (it is a full-screen modal travel window); do not override it here
        WorldMap = new WorldMap(Game.Connection);

        //travel confirm uses the shared in-game OK/Cancel dialog (raised above the world map's ZIndex below). On OK,
        //play the full-screen travel cutscene (song + splash) over everything, then perform the actual warp underneath.
        WorldMap.TravelRequested += (name, onOk, onCancel) => ConfirmDialog.Confirm(
            $"Travel to {name}?",
            () =>
            {
                Game.PlayTravelCutscene();
                onOk();
            },
            onCancel);

        TownMapControl = new TownMapControl();
        Minimap = new MinimapControl(Device) { ZIndex = 500 };

        //clicking the minimap walks the player to that map spot (like a right-click in the world)
        Minimap.TargetPicked += (tx, ty) =>
        {
            var p = WorldState.GetPlayerEntity();

            if (p is null)
                return;

            Pathfinding.TargetEntityId = null;
            PathfindToTile(p, tx, ty);
        };

        //clicking a warp on the town map asks to travel: OK closes the map and auto-paths to the warp tile, Cancel keeps it
        TownMapControl.WarpSelected += cluster => ConfirmDialog.Confirm(
            $"Move to {cluster.DestName}?",
            () =>
            {
                TownMapControl.Hide();
                PathfindToWarp(cluster);
            });

        //the warp travel confirm is anchored to the map: when the map goes (closed, map change), it goes too
        TownMapControl.Closed += () =>
        {
            if (ConfirmDialog.Visible)
                ConfirmDialog.Hide();
        };

        //while the confirm is up, a non-warp click on the map is inert (see TownMapControl.OnMouseUp)
        TownMapControl.HasOpenConfirm = () => ConfirmDialog.Visible;

        MapLoading = new MapLoadingBar
        {
            ZIndex = 5
        };

        //center in the FULL window, not the legacy 640x480 viewport (which would land it in the upper-left of a larger
        //window). Re-centered on every Show in case the window was resized. See WorldScreen.Map.cs.
        MapLoading.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));

        AislingContext = new AislingContextMenu
        {
            ZIndex = 3
        };

        ItemTooltip = new ItemTooltipControl
        {
            //above the draggable windows (which raise their ZIndex into the 500s+) so it is never hidden behind one
            ZIndex = 1_000_000,
            //the dialog's native-resolution text is drawn ON TOP of the whole Root pass, so the tooltip would land under
            //it. Skip it in Root.Draw and let DrawNativeUi paint it LAST (after the native text), so a hovered shop/item
            //tooltip always sits on top of the wares text. Still a Root child for layout/state.
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
        //host the NPC/sign dialog in a magnifier so it scales + centers to best-fit the larger window (it is authored for
        //640x480). High ZIndex so the conversation draws above the hud windows; not draggable (it stays centered). Fades in
        //on open / out on close (a touch slower than a window for a deliberate feel); PlayFadeSounds off so the generic
        //window cue doesn't double the dialog's own open sound.
        NpcSessionHost = new ScaleHost(NpcSession, 1f) { ZIndex = 150_000, Fades = true, PlayFadeSounds = false, FadeSeconds = 0.18f };
        Root.AddChild(NpcSessionHost);
        //dim the rest of the game view behind the dialog, easing in lockstep with the dialog's fade (just under its ZIndex)
        DialogDim = new DialogDimmer(NpcSessionHost) { ZIndex = 149_999 };
        Root.AddChild(DialogDim);
        Root.AddChild(ItemTooltip);
        Root.AddChild(MainOptions);
        Root.AddChild(SettingsDialog);
        Root.AddChild(MacrosList);
        Root.AddChild(HotkeyHelp);

        //magnify + drag the group window (centered when shown); pass-through so empty space reaches the host to drag
        GroupHost = new ScaleHost(GroupPanel, ClientSettings.EffectiveWindowScale)
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

            GroupHost.Scale = ClientSettings.EffectiveWindowScale;

            //center only the first time it opens; after that it stays where the player dragged it (until restart)
            if (!GroupCentered)
            {
                GroupHost.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
                GroupCentered = true;
            }
        };
        Root.AddChild(GroupHost);

        Root.AddChild(GroupBoxViewer);

        //the friends list and who-is-online list are free-floating menu windows (drag by background art, follow Window
        //size, centered on open)
        Root.AddChild(RegisterMenuWindow(WorldList));
        Root.AddChild(RegisterMenuWindow(FriendsList));

        //the exchange window is draggable (like the equipment book / group window). Centered on each new exchange open.
        //The gold/item prompts remain anchored each frame in AnchorPopups (they are quick non-draggable dialogs).
        Exchange.IsPassThrough = true;
        ExchangeHost = new ScaleHost(Exchange, ClientSettings.EffectiveWindowScale) { ZIndex = 140_000, Draggable = true, Fades = true };
        Exchange.VisibilityChanged += visible =>
        {
            if (!visible || (ExchangeHost is null))
                return;

            ExchangeHost.Scale = ClientSettings.EffectiveWindowScale;
            ExchangeHost.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
        };
        Root.AddChild(ExchangeHost);
        GoldDropHost = new ScaleHost(GoldDrop, ClientSettings.EffectiveWindowScale) { ZIndex = 140_000, Fades = true };
        Root.AddChild(GoldDropHost);
        ItemAmountHost = new ScaleHost(ItemAmount, ClientSettings.EffectiveWindowScale) { ZIndex = 140_000, Fades = true };
        Root.AddChild(ItemAmountHost);

        //the whole board/mail family are free-floating menu windows too - the board list, the post/mail lists, the readers
        //and the composers all behave like Group/Equipment now (centered, scaled, draggable) instead of pinned-right panels.
        //They SHARE one position (boardGroup) so navigating board list -> a board -> a post stays put instead of each one
        //popping up centered, which made the single flow look like separate menus.
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
        //host the profile/legend/equipment book in a magnifier; draggable by its background, high ZIndex so the
        //centered book draws above the windows. The book is pass-through so empty-space clicks reach the host to drag.
        StatusBookHost = new ScaleHost(StatusBook, ClientSettings.EffectiveWindowScale)
        {
            Draggable = true,
            Fades = true,
            ZIndex = 100000
        };
        StatusBook.IsPassThrough = true;
        Root.AddChild(StatusBookHost);
        //the profile-text editor lives inside a magnifier so it opens at the "Window size" scale like the book that spawns
        //it; it visibility-syncs to the inner editor, which is shown/positioned/raised in the OnProfileTextClicked handler.
        ProfileEditorHost = new ScaleHost(SelfProfileTextEditor, ClientSettings.EffectiveWindowScale) { ZIndex = 100010 };
        Root.AddChild(ProfileEditorHost);
        //the skill/spell + event detail popups live inside a magnifier so they open at the "Window size" scale (like the
        //book that spawns them) with crisp native TTF; each host visibility-syncs to its inner control, which is
        //shown/centered/raised in the OnAbilityDetailRequested / OnEventDetailRequested handlers above.
        AbilityDetailsHost = new ScaleHost(AbilityMetadataDetails, ClientSettings.EffectiveWindowScale) { ZIndex = 100010 };
        EventDetailsHost = new ScaleHost(EventMetadataDetails, ClientSettings.EffectiveWindowScale) { ZIndex = 100010 };
        Root.AddChild(AbilityDetailsHost);
        Root.AddChild(EventDetailsHost);
        //wrap the other-player profile in the same draggable, magnified menu-window host the rest of the books use, so
        //it scales with the "Window size" slider and drags by its background like the self-profile book
        Root.AddChild(RegisterMenuWindow(OtherProfile));
        Root.AddChild(TextPopupHost);
        Root.AddChild(Notepad);
        //the chant editor lives inside a magnifier so it opens at the "Window size" scale; it visibility-syncs to the
        //inner editor, which is shown/positioned/raised by OpenChantEdit. Re-scaled + re-centered on each open there.
        ChantEditHost = new ScaleHost(ChantEdit, ClientSettings.EffectiveWindowScale);
        Root.AddChild(ChantEditHost);
        Root.AddChild(WorldMap);
        //magnify the social status picker like the other windows (Window size); it visibility-syncs to the picker
        SocialStatusHost = new ScaleHost(SocialStatusPicker, ClientSettings.EffectiveWindowScale)
        {
            ZIndex = 200_000 //above the status book host (100000), below the item tooltip (1,000,000)
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

        //--- new native-resolution UI (the old border HUD is retired) ---
        var menuBar = new MenuBar();
        Root.AddChild(menuBar);

        //a menu-entry tooltip: a TITLE line (the menu label), a descriptive body explaining what the window is for, and
        //the action's current hotkey on its own line (so the menu teaches the keys). Returns a LIVE provider (re-read each
        //time it shows) so rebinding the key in Options > Controls updates the menu tooltip too, instead of showing the key
        //bound when the world screen was built. The generic resolver renders line 1 as the title and the rest as the body.
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
        ChatWin.Open(); //always present; shows on Enter / pin / fade-timer, no menu entry or close box

        //the normal-mode prefix is the local player's name; the screen owns it
        ChatWin.ChatInput.PlayerNameProvider = () => WorldHud.PlayerName;

        //wire the chat window's typing line to the same dispatch as the HUD input (local /commands + server send)
        ChatWin.ChatInput.MessageSent += HandleChatMessage;
        ChatWin.ChatInput.ShoutSent += msg => Game.Connection.SendShout(msg);
        ChatWin.ChatInput.WhisperSent += (target, msg) => Game.Connection.SendWhisper(target, msg);
        ChatWin.ChatInput.IgnoreAdded += name => Game.Connection.SendAddIgnore(name);
        ChatWin.ChatInput.IgnoreRemoved += name => Game.Connection.SendRemoveIgnore(name);
        ChatWin.ChatInput.IgnoreListRequested += () => Game.Connection.SendIgnoreRequest();

        //host the REAL inventory grid (detached from the retired HUD, keeps all its wiring) in a window,
        //shown expanded (full 5-row grid) with the matching background
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

        //magnify the small pixel-art inventory grid so it reads well at native resolution (Options "Window size" slider).
        //flush: the host fills the window so the inventory art's border merges with the wood frame (no double border).
        InvHost = new ScaleHost(inv, ClientSettings.EffectiveWindowScale)
        {
            X = 0,
            Y = 0
        };

        InventoryWindow = new DraggableWindow("Inventory", InvHost.Width, InvHost.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true)
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
        //starts closed like every other menu window; the player opens it from Menu > Inventory

        //character stats window (level-up arrows show when there are unspent points; next-level EXP in the labels)
        var statusHb = DataContext.UserControls.Get("_nstatus")!;
        StatsWinPanel = new StatsPanel(statusHb) { Visible = true };
        StatsWinPanel.OnRaiseStat += stat => Game.Connection.RaiseStat(stat);

        //extended stats (element, AC, DMG, HIT, magic resistance) on top; main stats merged 4px below it
        ExtStatsWinPanel = new ExtendedStatsPanel(statusHb) { Visible = true, X = 0, Y = 0 };
        StatsWinPanel.X = 0;
        StatsWinPanel.Y = ExtStatsWinPanel.Height - 4;

        var statsContainer = new UIPanel
        {
            IsPassThrough = true,
            Width = Math.Max(StatsWinPanel.Width, ExtStatsWinPanel.Width),
            Height = ExtStatsWinPanel.Height + StatsWinPanel.Height - 4
        };
        statsContainer.AddChild(ExtStatsWinPanel);
        statsContainer.AddChild(StatsWinPanel);

        StatsHost = new ScaleHost(statsContainer, ClientSettings.EffectiveWindowScale) { X = 0, Y = 0 };
        StatsWin = new DraggableWindow("Stats", StatsHost.Width, StatsHost.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 60, Y = 120, CentersOnFirstShow = true, FadeOnOpen = true };
        StatsWin.Content.AddChild(StatsHost);
        StatsWin.ContentHost = StatsHost;
        Root.AddChild(StatsWin);
        StatsMenuEntry = menuBar.AddEntry("Stats", StatsWin.Toggle, Tip("Stats",
            "Your character's attributes, health and mana, level and experience. When you have unspent stat points, the up-arrows let you raise Strength, Intelligence, Wisdom, Constitution or Dexterity.",
            GameAction.ToggleStats));
        WorldState.Attributes.Changed += PushStatsWindow;
        PushStatsWindow();

        //full Skills / Spells books in windows (auto-bound to WorldState, like the inventory window). The 3-row grid
        //uses the small HUD's (_nbk_s) inventory background; magnified 2x like the inventory window.
        var bookHb = DataContext.UserControls.Get("_nbk_s")!;
        var bookBg = UiRenderer.Instance!.GetPrefabTexture("_nbk_s", "InventoryBackground", 0);

        SkillWinPanel = new SkillBookPanel(bookHb, background: bookBg, normalVisibleSlots: 36);
        SkillWinPanel.OnSlotClicked += HandleSkillSlotClicked;
        SkillWinPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SkillWinPanel.OnSlotHoverExit += HandleAbilityHoverExit;
        SkillWinPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        SkillHost = new ScaleHost(SkillWinPanel, ClientSettings.EffectiveWindowScale) { X = 0, Y = 0 };
        SkillWin = new DraggableWindow("Skills", SkillHost.Width, SkillHost.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 100, Y = 80, CentersOnFirstShow = true, FadeOnOpen = true };
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
        SpellHost = new ScaleHost(SpellWinPanel, ClientSettings.EffectiveWindowScale) { X = 0, Y = 0 };
        SpellWin = new DraggableWindow("Spells", SpellHost.Width, SpellHost.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 140, Y = 100, CentersOnFirstShow = true, FadeOnOpen = true };
        SpellWin.Content.AddChild(SpellHost);
        SpellWin.ContentHost = SpellHost;
        Root.AddChild(SpellWin);
        menuBar.AddEntry("Spells", SpellWin.Toggle, Tip("Spells",
            "Your spell book: every spell you have learned. Drag a spell onto the hotbar for quick casting, or right-click it to edit its chant line. Hover a spell to read its effect, cast lines and cooldown.",
            GameAction.ToggleSpells));

        //Actions window: the split Skills (left) / Spells (right) abilities panel. Detached from the retired HUD so it
        //keeps all its wiring and stays registered for the drag ghost via WorldHud.Tools. Magnified like the other books.
        SmallHud.DetachTab(HudTab.Tools);
        var tools = SmallHud.Tools;
        //the Actions window's two halves are real skill/spell panels but weren't hover/right-click wired, so their
        //slots showed no tooltip and no chant editor. Wire them like the K/P book windows and the hotbars.
        tools.WorldSkills.OnSlotHoverEnter += HandleAbilityHoverEnter;
        tools.WorldSkills.OnSlotHoverExit += HandleAbilityHoverExit;
        tools.WorldSpells.OnSlotHoverEnter += HandleAbilityHoverEnter;
        tools.WorldSpells.OnSlotHoverExit += HandleAbilityHoverExit;
        WireAbilityRightClicks(tools.WorldSkills);
        WireAbilityRightClicks(tools.WorldSpells);
        ActionsHost = new ScaleHost(tools, ClientSettings.EffectiveWindowScale) { X = 0, Y = 0 };
        ActionsWin = new DraggableWindow("Actions", ActionsHost.Width, ActionsHost.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H, useWoodFrame: true, flushContent: true) { X = 180, Y = 120, CentersOnFirstShow = true, FadeOnOpen = true };
        ActionsWin.Content.AddChild(ActionsHost);
        ActionsWin.ContentHost = ActionsHost;
        Root.AddChild(ActionsWin);
        menuBar.AddEntry("Actions", ActionsWin.Toggle, Tip("Actions",
            "Your skills and spells side by side in one window, so you can manage both at once. Drag onto the hotbar, right-click to edit a chant, or hover for details.",
            GameAction.ToggleActions));

        //the book-themed profile dialog (centered): Equipment opens the Intro tab, Legend opens the Legend tab
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

        //Group window (members), centered + magnified
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

        //Friends list (toggle the free-floating window; data is loaded on world entry via LoadPlayerFriendList)
        menuBar.AddEntry("Friends", ToggleFriendsWindow, Tip("Friends",
            "Your friends list. See which of your friends are online right now, and keep track of the players you want to stay in touch with.",
            GameAction.ToggleFriends));

        //Who is online (the world-list window; data is requested from the server when opened)
        menuBar.AddEntry("Who is online", ToggleWorldListPanel, Tip("Who is online",
            "Everyone currently playing. Filter the list by class using the tabs, and click a name to view that player's profile.",
            GameAction.ToggleWorldList));

        //Mail / message boards / help topics; the R key opens the same panel
        MailMenuEntry = menuBar.AddEntry("Mail & Help", ToggleBoardPanel, Tip("Mail & Help",
            "Read and send personal mail, browse the message boards, and look up help topics. A marker appears here when you have unread mail.",
            GameAction.ToggleBulletinBoard));

        //emote picker: a grid of every emote, each shown as its real face/bubble graphic in the player's skin tone
        EmoteWin = new EmoteWindow(Game.AislingRenderer) { CentersOnFirstShow = true, FadeOnOpen = true };
        EmoteWin.EmoteChosen += TrySendEmote;
        Root.AddChild(EmoteWin);
        menuBar.AddEntry("Emotes", EmoteWin.Toggle, Tip("Emotes",
            "A grid of every emote your character can perform, each shown with its real animation. Click one to play it. Emotes can also be bound to keys in Options > Controls.",
            GameAction.ToggleEmotes));

        //custom settings window (shared with the lobby via OptionsWindow.Create): each row applies live and
        //persists to Darkages.cfg.
        ControlsWin = new ControlsWindow { CentersOnFirstShow = true, FadeOnOpen = true };
        Root.AddChild(ControlsWin);
        OptionsWin = OptionsWindow.Create(Game, ControlsWin, onChatRefresh: () => ChatWin?.RefreshChatTimestamps());
        OptionsWin.CentersOnFirstShow = true;
        OptionsWin.FadeOnOpen = true;
        Root.AddChild(OptionsWin);
        menuBar.AddEntry("Options", OptionsWin.Toggle, Tip("Options",
            "Game settings: sound and music volume, interface and minimap sizes, camera and movement feel, tooltips, and more. The Controls button inside opens the keybinding editor where every key can be reassigned.",
            GameAction.ToggleOptions));

        //live render-FX tuning panel, opened by "/debugOptions" (no menu entry - a dev tool, see HandleChatMessage)
        DebugFxWin = new DebugFxWindow { CentersOnFirstShow = true, FadeOnOpen = true, Visible = false };
        Root.AddChild(DebugFxWin);

        //log out back to the login screen: fade to black, the server confirms (OnExitResponse), we send the real logout,
        //and the redirect switches us to the lobby (see BeginLogout + HandleExitResponse + WorldScreen.Update's PendingLoginSwitch).
        menuBar.AddEntry("Log out", BeginLogout, "Log out\nLeave the world and return to the login screen. Your character is saved automatically.");

        //fixed on-screen hotbars: single-row, auto-bound views of the top rows
        //reuse the panel class (the collapsed-HUD look); each self-subscribes to its WorldState view model, so these
        //extra instances stay in sync with no wiring. Positioned/scaled each frame in Update (see AnchorHotbars).
        //the large HUD layout (_nbk_l) is the fully-collapsed single-row look: 1 row of 12 + gold, no skill/spell buttons
        var hb = DataContext.UserControls.Get("_nbk_l")!;
        var barBg = UiRenderer.Instance!.GetPrefabTexture(hb.Name, "InventoryBackground", 0);

        SkillBarPanel = new SkillBookPanel(hb, background: barBg, normalVisibleSlots: 12);
        SpellBarPanel = new SpellBookPanel(hb, background: barBg, normalVisibleSlots: 12);
        InvBarPanel = new InventoryPanel(hb, background: barBg, normalVisibleSlots: 12, showGold: false);

        //use/activate on double-click (original behavior)
        SkillBarPanel.OnSlotClicked += HandleSkillSlotClicked;
        SpellBarPanel.OnSlotClicked += HandleSpellSlotClicked;
        InvBarPanel.OnSlotClicked += slot => Game.Connection.UseItem(slot);
        //single-click an EMPTY hotbar slot to open its window (double-click still activates an occupied slot above)
        SkillBarPanel.OnSlotSingleClicked += HandleSkillBarSlotClicked;
        SpellBarPanel.OnSlotSingleClicked += HandleSpellBarSlotClicked;
        InvBarPanel.OnSlotSingleClicked += HandleInvBarSlotClicked;
        InvBarPanel.OnSlotHoverEnter += HandleInventoryHoverEnter;
        InvBarPanel.OnSlotHoverExit += HandleInventoryHoverExit;
        SkillBarPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SkillBarPanel.OnSlotHoverExit += HandleAbilityHoverExit;
        SpellBarPanel.OnSlotHoverEnter += HandleAbilityHoverEnter;
        SpellBarPanel.OnSlotHoverExit += HandleAbilityHoverExit;

        //drag to rearrange within a bar (server swap), and drag items/spells out of the bar
        SkillBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SkillBook, s, t);
        SpellBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.SpellBook, s, t);
        SpellBarPanel.OnSlotDroppedOutside += HandleSpellSlotDropped;
        InvBarPanel.OnSlotSwapped += (s, t) => Game.Connection.SwapSlot(PanelType.Inventory, s, t);
        InvBarPanel.OnSlotDroppedOutside += HandleInventoryDropInViewport;

        //key labels reflect the player's ACTUAL bindings (Skill/Spell/Item slot actions), short form (e.g. "1", "S+1",
        //"F1"); they refresh whenever a binding changes in the Controls window. All three render through the same
        //SlotLabels text path now (no more _ninvn number sprite on the skill bar).
        RefreshHotbarSlotLabels();
        Keybindings.Changed += RefreshHotbarSlotLabels;

        SkillBar = new ScaleHost(SkillBarPanel, ClientSettings.EffectiveHotbarScale);
        SpellBar = new ScaleHost(SpellBarPanel, ClientSettings.EffectiveHotbarScale);
        InvBar = new ScaleHost(InvBarPanel, ClientSettings.EffectiveHotbarScale);
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

        //status-effect bar: a fresh EffectBarControl (the HUD's is hidden with the retired HUD) wrapped in a magnifier so
        //the small half-size icons read at the chunky UI scale. Fed by HandleEffect; anchored on the right in AnchorHotbars.
        BuffBar = new EffectBarControl();
        BuffBarHost = new ScaleHost(BuffBar, ClientSettings.EffectiveHotbarScale * 2f) { ZIndex = 90_000 };
        Root.AddChild(BuffBarHost);


        WireHudPanels(SmallHud);
        WireHudPanels(LargeHud);

        //right-click a skill/spell to edit its chant lines - in the new K/P book windows AND the on-screen hotbars,
        //not just the retired HUD panels (those are wired in WireHudPanels). Same OpenChantEdit path as the HUD.
        WireAbilityRightClicks(SkillWinPanel!);
        WireAbilityRightClicks(SpellWinPanel!);
        WireAbilityRightClicks(SkillBarPanel!);
        WireAbilityRightClicks(SpellBarPanel!);

        //build ui atlas after all hud controls are constructed
        UiRenderer.Instance?.BuildAtlas();

        //load local portrait and profile text from character folder
        var playerName = Game.Connection.AislingName;
        PlayerPortrait = LoadPortraitFile(playerName);
        StatusBook.SetProfileText(LoadProfileText());

        //retire the old border HUD: keep the objects alive (so the many references still resolve) but
        //stop drawing it. The world now fills the screen and the new menu/windows/HP/MP/chat overlay.
        SmallHud.Visible = false;
        LargeHud.Visible = false;
    }

    //POC: builds a draggable window with placeholder content and wires it to a Menu dropdown entry.
    //Real menus will host the existing panel content here instead of a placeholder label.
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

    //renders the whole current level into a texture scaled to fit the screen, then shows the town-map overlay with the
    //player marker on their real tile (no hand-drawn map needed; works for every map)
    private void ShowTownMap()
    {
        if (MapFile is null)
            return;

        //never under/over an NPC dialog or a sign/board popup: they own the screen (the map also closes if one
        //opens onto it)
        if (NpcSession.Visible || TextPopup.Visible)
            return;

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        //renders the level at native scale once (cached per map); the overlay scales it to fit the window each frame.
        //foreground into the main target (sketched as line work) + the background into its own target (flat, low-detail
        //tint) so the floor doesn't get the busy black pencil treatment the structure gets
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

            //collision lookup for the floor-plan wall shading (bounds-safe wrapper around the wall-only check)
            (x, y) => MapFile is not null
                      && (x >= 0)
                      && (y >= 0)
                      && (x < MapFile.Width)
                      && (y < MapFile.Height)
                      && IsTileWallBlocked(x, y));
    }

    //auto-paths the player onto the warp's tile (a real, walkable warp tile); stepping on it triggers the server warp
    private void PathfindToWarp(WarpData.WarpCluster cluster)
    {
        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        Pathfinding.TargetEntityId = null; //not following anything; just walk to the warp tile
        PathfindToTile(player, cluster.RepX, cluster.RepY);
    }

    //pushes the current attributes into the standalone stats window (same source the HUD's stats panel uses)
    //last seen player level, so a genuine level-up (Level going UP) can play the fanfare exactly once. -1 until the first
    //attributes packet fills it (which never plays a sound). Reset per session since each login builds a new WorldScreen.
    private int LastKnownLevel = -1;

    //last seen player HP, so a DROP can drive the camera shake + red damage pulse. -1 until the first attributes packet.
    private long LastKnownHp = -1;

    //menu entries that flag for attention: Stats when the player has unspent stat points, Mail when there is unread mail.
    //Driven from PushStatsWindow (fires on Attributes.Changed); the closed Menu button pulses while either is set.
    private MenuItem? StatsMenuEntry;
    private MenuItem? MailMenuEntry;

    private void PushStatsWindow()
    {
        if (WorldState.Attributes.Current is not { } attrs)
            return;

        StatsWinPanel?.UpdateAttributes(attrs);
        ExtStatsWinPanel?.UpdateAttributes(attrs);

        //local HP changes drive the camera shake + red damage pulse (and the sustained low-HP pulse)
        OnPlayerHealthChanged(LastKnownHp, attrs.CurrentHp, attrs.MaximumHp);
        LastKnownHp = attrs.CurrentHp;

        //light up the menu markers from the same authoritative attributes the HUD reads
        if (StatsMenuEntry is not null)
            StatsMenuEntry.ShowMarker = attrs.UnspentPoints > 0;

        if (MailMenuEntry is not null)
            MailMenuEntry.ShowMarker = attrs.HasUnreadMail;

        //local level-up fanfare: the server no longer plays a sound on level-up (it sends only the burst animation), so
        //the leveling player hears the embedded level_up.mp3 here. Only on an actual increase; the first fill just
        //seeds. Also gated on ItemSoundArmed (the same grace past first world entry the item/gold cues use) so the staged
        //attribute packets that stream in during login can never be misread as a level-up.
        if ((LastKnownLevel >= 0) && (attrs.Level > LastKnownLevel) && ItemSoundArmed)
            Game.SoundSystem.PlaySound(SoundSystem.SoundLevelUp);

        LastKnownLevel = attrs.Level;
    }

    //logout: fade the world to black FIRST, then ask the server to exit. The fade is held at black; once the redirect
    //lands (state -> Login) WorldScreen.Update switches to the lobby and reveals it with a fade-from-black.
    private void BeginLogout()
    {
        Game.FadeToBlackHold(null);
        Game.Connection.RequestExit();
    }

    //silent reconnect
    private const int RECONNECT_FONT = 18;
    private const int RECONNECT_TEXT_H = 40; //label band height; the text is centered within it

    //start the silent reconnect: freeze + darken the world, show the overlay, and kick off the lobby -> login -> world
    //replay. Triggered from HandleStateChanged on an unexpected world disconnect when we have this session's credentials.
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

    //reconnect reached the world again: reload a clean world screen via the proven login -> world handoff (next Update)
    private void OnReconnectSucceeded()
    {
        Reconnecting = false;
        Reconnect = null;
        PendingReconnectReload = true;
    }

    //reconnect gave up (timeout, login rejected, or cancelled): drop to the lobby (next Update). A non-null message is
    //a login rejection worth showing; a null message is a plain timeout (the lobby's own retry line already covers it).
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

        //centered on screen. Height MUST be set: UIElement.Draw early-returns when its ClipRect (computed from
        //Width x Height) is empty, so a zero-height label clips its own text away. The text centers inside this band.
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

    // TODO: call this whenever automatic window resizing occurs

    //applies the Options "Window size" scale live: resizes the always-built menu windows in place and re-scales the
    //book/group hosts (which also re-read WindowScale on open), re-centering those two if they are currently open.
    private void OnWindowScaleChanged()
    {
        var scale = ClientSettings.EffectiveWindowScale;

        RescaleWindow(InventoryWindow, InvHost, scale);
        RescaleWindow(StatsWin, StatsHost, scale);
        RescaleWindow(SkillWin, SkillHost, scale);
        RescaleWindow(SpellWin, SpellHost, scale);
        RescaleWindow(ActionsWin, ActionsHost, scale);

        //rescale in place - the player's dragged position is kept (no re-centering on a scale change)
        if (StatusBookHost is not null)
            StatusBookHost.Scale = scale;

        if (GroupHost is not null)
            GroupHost.Scale = scale;

        foreach (var host in MenuWindowHosts)
            host.Scale = scale;
    }

    //shared on-screen position for a set of menu windows (see RegisterMenuWindow). Once any of them is placed, the rest
    //open at the same spot and write their dragged position back, so a navigation flow reads as one window.
    private sealed class WindowGroup
    {
        public bool Positioned;
        public int X;
        public int Y;

        //every host in the group, so opening one can instantly retire whichever sibling is on screen (no cross-fade)
        public readonly List<ScaleHost> Members = [];
    }

    //wraps a panel as a free-floating "menu window": a magnifier dragged by its background art, raised to the top of the
    //wraps an OkPopupMessageControl in a ScaleHost so it scales with WindowScale and stays centered on screen.
    //the inner popup's CenterOnUi() sets X/Y to native-centered values; VisibilityChanged resets them to (0,0) and
    //repositions the host instead. The host inherits the popup's ZIndex.
    private ScaleHost WrapOkPopup(OkPopupMessageControl popup)
    {
        var host = new ScaleHost(popup, ClientSettings.EffectiveWindowScale)
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
            host.Scale = ClientSettings.EffectiveWindowScale;
            host.X = (ChaosGame.UiWidth - host.Width) / 2;
            host.Y = (ChaosGame.UiHeight - host.Height) / 2;
        };

        return host;
    }

    //window stack and centered the first time it opens, then left where the player dragged it. Collected in
    //MenuWindowHosts so ApplyWindowScale can rescale them all when the Options "Window size" slider moves. The panel must
    //be pass-through so empty-art clicks reach the host to drag it.
    //
    //A `group` makes several windows SHARE one on-screen position (e.g. the board/mail family): the group centers once on
    //first open, each window opens at the group's position, and writes its (possibly dragged) position back when it
    //hides - so navigating board list -> a board -> a post feels like one window changing content, not separate menus.
    private ScaleHost RegisterMenuWindow(UIPanel panel, WindowGroup? group = null, bool slideOnFade = false)
    {
        var centered = false;

        var host = new ScaleHost(panel, ClientSettings.EffectiveWindowScale)
        {
            Draggable = true,
            Fades = true,
            ZIndex = 100000
        };

        if (slideOnFade)
        {
            //the board/mail family slides up as it fades in (like the sign popup); a slightly longer ramp than the default
            //0.08s so the slide is actually seen. A sub-menu SWITCH snaps the fade (SnapShown), so it never slides between
            //pages - only a real open/close from outside the group does.
            host.SlideOnFade = true;
            host.FadeSeconds = 0.18f;
        }

        group?.Members.Add(host);

        panel.VisibilityChanged += visible =>
        {
            if (visible)
            {
                host.Scale = ClientSettings.EffectiveWindowScale;

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

                    //SWITCH within the group: if a sibling is still on screen (visible or mid fade-out), this is one
                    //window changing content, not a new one - snap both so there is NO cross-fade between them. A fresh
                    //open (no sibling showing) still fades in normally.
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
                //remember where this window ended up (incl. any drag) so the next window in the group opens there
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

        //flush windows: the content fills the whole window (host == content), so no side padding; the bordered content
        //merges with the wood frame. Non-flush windows keep the wood chrome + the small breathing gap (+6).
        if (window.FlushContent)
            window.Resize(host.Width, host.Height + DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H);
        else
            window.Resize(host.Width + DraggableWindow.FRAME_LEFT + DraggableWindow.FRAME_RIGHT + 6, host.Height + 2 * DraggableWindow.FRAME_TOP + DraggableWindow.TITLE_H + 6);
    }

    //maps a screen point into the magnified profile book's native space (for the equip-on-drop hit check)
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

    //maps a screen point into the magnified Stats window's native space (for the stat-label hover tooltip)
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

    //opens the Social status picker at the cursor (clicked from the equipment book's emoticon); toggles closed if open.
    //Raised above the status book host so it is never hidden behind the open book.
    private void ShowSocialStatusPickerAt(int x, int y)
    {
        if (SocialStatusPicker.Visible)
        {
            SocialStatusPicker.Hide();

            return;
        }

        if (SocialStatusHost is not null)
        {
            SocialStatusHost.Scale = ClientSettings.EffectiveWindowScale; //pick up the current "Window size"
            SocialStatusHost.X = Math.Clamp(x, 0, Math.Max(0, ChaosGame.UiWidth - SocialStatusHost.Width));
            SocialStatusHost.Y = Math.Clamp(y, 0, Math.Max(0, ChaosGame.UiHeight - SocialStatusHost.Height));
        }

        SocialStatusPicker.Show();
    }

    /// <inheritdoc />
    public void UnloadContent()
    {
        IsUnloaded = true;

        //never leave the death greyscale active outside the world (e.g. dying then disconnecting to the lobby)
        ChaosGame.WorldSaturation = 1f;

        //detach any in-flight reconnect flow so a torn-down screen can't keep reacting to connection events (the
        //normal success/give-up paths already null this out, but a future screen switch could leave one live)
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

        //unwire panel click-to-use events
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