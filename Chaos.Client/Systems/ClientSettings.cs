namespace Chaos.Client.Systems;

public static class ClientSettings
{
    private const string FILE_NAME = "Darkages.cfg";
    public static bool UseGroupWindow { get; set; } = true;
    public static int ChattingMode { get; set; }
    public static bool DoGroundAnimation { get; set; } = true;
    //clicking another (non-hostile) player opens their profile. Default on. Persisted via the SWM key
    //"SwmProfileClick" (NOT the legacy "UserClickMode": the retail settings menu that wrote it is unreachable,
    //so existing configs carry the old default-off value, never a user choice. Reading it would force this back off).
    public static bool EnableProfileClick { get; set; } = true;

    //the inventory grid cell the gold bag sits in (it is draggable, unlike retail). -1 = default (last visible cell).
    public static int GoldSlotIndex { get; set; } = -1;

    //menu button: dragging flag, attachment side (0=below, 1=above, 2=left, 3=right, -1=detached/independent),
    //and the independent center-relative offset used when detached.
    public static bool AllowDragMenuButton { get; set; } = true;
    public static int MenuButtonAttachSide { get; set; } = 0; //default is attached below the minimap
    public static int MenuButtonOffsetX { get; set; } = int.MinValue; //only used when AttachSide == -1
    public static int MenuButtonOffsetY { get; set; } = int.MinValue;

    //corner minimap: dragging enabled + on/off + center-relative offset
    public static bool AllowDragMinimap { get; set; } = true;
    public static bool ShowMinimap { get; set; } = true;
    public static int MinimapOffsetX { get; set; } = int.MinValue;
    public static int MinimapOffsetY { get; set; } = int.MinValue;
    public static float MinimapScale { get; set; } = 1f; //scales the circle's SIZE only (same tiles shown)
    public static float EffectiveMinimapScale { get => Math.Clamp(EffectiveInterfaceScale * MinimapScale, 0.1f, 4f); }
    public static bool GroupOpen { get; set; }
    public static int MusicVolume { get; set; } = 3; //30% by default (0-10 scale), quieter than sound

    //show NPC dialog/ambient chatter in the chat window. Off filters it out so only player/system messages appear.
    //Default off. Persisted via the SWM key "SwmShowNpcChat" (NOT the legacy "MonsterSayRecordMode", whose old
    //default-on value in existing configs would otherwise force this back on).
    public static bool RecordNpcChat { get; set; }

    //show "[HH:mm] " timestamp before every chat line. Timestamps are always recorded so toggling this on
    //retroactively shows the time on all messages in the current session.
    public static bool ShowChatTimestamp { get; set; }

    //CHAT LINE fade: an individual chat line stays fully readable for this many seconds, then fades by age (recent lines
    //stay visible even while the window chrome has faded). 0 = lines never fade. Options "Chat line fade" slider.
    public static int ChatFadeDelaySeconds { get; set; } = 15;

    //CHAT WINDOW fade: once the cursor leaves the chat window (and you are not typing/pinned) it stays for this many
    //seconds, then the window chrome fades out and becomes click-through. 0 = never fade (always visible). The cursor
    //being inside resets this. Hovering does NOT show a hidden window. Only Enter, pinning, or 0 do. "Chat window fade".
    public static int ChatWindowFadeSeconds { get; set; } = 3;

    //chat window: center-relative X offset, anchor-relative Y offset, and saved size.
    //int.MinValue = not yet placed (first run uses the default beside the HP orb).
    public static int ChatWindowOffsetX { get; set; } = int.MinValue;
    public static int ChatWindowOffsetY { get; set; } = int.MinValue;
    public static int ChatWindowWidth { get; set; } = int.MinValue;
    public static int ChatWindowHeight { get; set; } = int.MinValue;

    //chat tabs whose "Highlight" was turned off (right-click a tab): they never pulse/flag on a new message. Keyed by tab
    //key (built-in channel name like "Public"/"Group", or a custom "!channel"). Persisted as a comma list (SwmMutedTabs).
    public static readonly HashSet<string> MutedChatTabs = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsChatTabMuted(string key) => MutedChatTabs.Contains(key);

    public static void SetChatTabMuted(string key, bool muted)
    {
        if (muted)
            MutedChatTabs.Add(key);
        else
            MutedChatTabs.Remove(key);

        Save();
    }

    //smooth (bilinear) filtering for the town-map (T) overview instead of crisp point sampling. Options checkbox.
    public static bool SmoothMapView { get; set; } = true;

    //screen scroll smoothing: >0 = Smooth, 0 = Rough. Default Smooth. Read live in WorldScreen.Update.
    public static int ScrollLevel { get; set; } = 1;

    //smooth (pixel-interpolated) movement for OTHER entities (enemies, NPCs, other players) instead of the discrete
    //per-frame step. Default on. Options "Smooth creature movement" checkbox, read live each frame in WorldScreen.Update.
    public static bool SmoothCreatureMovement { get; set; } = true;

    //map-change transition style. False (default) = cross-dissolve old map into new. True = fade out to black, hold,
    //then fade in (the "Alternative map fade"). Read by MapTransition.BeginFadeOut at the start of each warp.
    public static bool AlternativeMapFade { get; set; }

    //focus speaker: while an NPC dialog is open, smoothly pan the (normal) camera over to the speaking NPC, then ease back
    //to the player when it closes. Default on. Options "Focus speaker" checkbox, read live in WorldScreen's camera follow.
    public static bool FocusSpeaker { get; set; } = true;

    //subtle horizontal camera shake when the local player loses HP (any source). 0 = off, 100 = full. Options slider.
    public static float CameraShake { get; set; } = 100f;

    //screen-edge red pulse when the local player loses HP, plus a faint sustained pulse at 10% HP or less. 0 = off, 100 = full.
    public static float CameraEffects { get; set; } = 100f;

    //whether the world-map travel window shows the destination info panel by default. Toggled by its Info button.
    public static bool WorldMapShowInfo { get; set; } = true;

    //per-footstep-sample enable flags (index 0..3 = step1..step4). DEBUG AID so you can hear which sample is which;
    //the Options checkboxes for these are hidden in the released build. All enabled by default.
    public static bool[] FootstepStepsEnabled { get; } = [true, true, true, true];

    //footstep loudness as a 0-100 percentage of the Sound volume (its own "Footsteps" slider, effective = this % of the
    //Sound slider). 0 = off. Default 15%. Pushed into SoundSystem.SetFootstepVolume at boot and when the slider changes.
    public static int FootstepVolume { get; set; } = 15;

    //which walk-animation frames a footstep fires on: true = EVEN frame indices (0/2), false = ODD (1/3). Phase-shifts the
    //chat-bubble cue loudness as a 0-100 percentage of the Sound volume (its own "Chat" slider, effective = this % of the
    //Sound slider). 0 = off. Default 50%. Pushed into SoundSystem.SetChatVolume at boot and when the slider changes.
    public static int ChatBubbleVolume { get; set; } = 50;

    //defaults match the original client
    public static int SoundVolume { get; set; } = 5;
    public static int Speed { get; set; } = 100;
    public static bool UseShiftKeyForAltPanels { get; set; } = true;

    //the master scale of all interface elements
    //a value less than 1.0 means automatic sizing (adjusts with window scale)
    //Note that all scales have a scale setting and an effective value. The scale setting is the setting stored, while the
    //effective scale is what is actually in effect (taking into account automatic window scale, and size clamping etc.)
    public static float InterfaceScale { get; set; } = 0f;
    public static float EffectiveInterfaceScale { get; set; } = 1f; //this is managed by ChaosGame window management
    public static float EffectiveInterfaceTextScale { get => MathF.Max(1f, EffectiveInterfaceScale * 0.7f); } //text slightly smaller. There is a delay before this starts upscaling

    //magnification of the on-screen hotbars (skills/spells/inventory). The collapsed 1-row art is already a good
    //size at 1.0, so that is the default. A future options slider will drive this (intended range ~1.0 to 4.0).
    //Read live each frame, so it is independent of the inventory window's own scale.
    public static float HotbarScale { get; set; } = 1f;
    public static float EffectiveHotbarScale { get => Math.Clamp(EffectiveInterfaceScale * HotbarScale, 1f, 4f); }

    //magnification of floating windows that host retail pixel-art panels (the profile/legend/equipment book, etc.).
    //Driven by the Options window's "Window size" slider (range 1.0 to 4.0).
    public static float WindowScale { get; set; } = 1f;
    public static float EffectiveWindowScale { get => Math.Clamp(EffectiveInterfaceScale * WindowScale, 1f, 4f); }

    public static event EffectiveWindowScaleChangedHandler? OnEffectiveWindowScaleChanged;
    public static void NotifyEffectiveWindowScaleChanged() => OnEffectiveWindowScaleChanged?.Invoke();

    //scale of the chat log + chat input text (multiplies the base 14px Cinzel size). Driven by the Options "Chat font
    //size" slider (range 1.0 to 4.0, 0.05 step); 1.0 = the current size. ChatWindow re-lays out live when it changes.
    public static float ChatFontScale { get; set; } = 1f;
    public static float EffectiveChatFontScale { get => Math.Clamp(EffectiveInterfaceTextScale * ChatFontScale, 1f, 4f); }

    //scale of the over-head chat bubble text (multiplies the bubble's base 13px Cinzel size). Options "Bubble font size"
    //slider (1.0 to 4.0, 0.05 step); 1.0 = the current size. Read when a bubble is created.
    public static float BubbleFontScale { get; set; } = 1f;
    public static float EffectiveBubbleFontScale { get => Math.Clamp(EffectiveInterfaceTextScale * BubbleFontScale, 1f, 4f); }

    //scale of the over-head entity NAME tags (multiplies the base 13px Cinzel size). Options "Names font size" slider
    //(1.0 to 4.0, 0.05 step); 1.0 = the current size. Read live each frame when name tags are drawn.
    public static float NameFontScale { get; set; } = 1f;
    public static float EffectiveNameFontScale { get => Math.Clamp(EffectiveInterfaceTextScale * NameFontScale, 1f, 4f); }

    //seconds an over-head chat bubble stays before fading out. Options "Bubble fade after" slider (0 to 30s, 1s step);
    //0 disables bubbles entirely. Default 4s.
    public static int BubbleFadeSeconds { get; set; } = 4;

    //run at the monitor refresh (no tearing). Driven by the Options "VSync" checkbox, applied via ChaosGame.SetVSync.
    public static bool VSync { get; set; } = true;

    //modern click scheme: right-click always moves, left-click always interacts (pick up / attack / talk to NPC).
    //Off = the classic scheme (right double-click follows+assails, left double-click picks up). Options checkbox.
    public static bool ModernControls { get; set; } = true;

    //when on, left-click moves and right-click interacts/attacks (flips the modern-controls buttons). Default off.
    public static bool FlipWalkInteract { get; set; } = false;

    //mouse target buttons: Mouse 3 = target self, Mouse 4 = target enemy. When on, the two are swapped. Default off.
    public static bool FlipMouseTargetButtons { get; set; } = false;

    //opacity of the item/map hover tooltip background (Options "Tooltip opacity" slider, 0.25 to 1.0). Read live.
    public static float TooltipAlpha { get; set; } = 0.85f;

    //seconds the cursor must rest on something before its tooltip appears (Options "Tooltip delay" slider, 0.0 to 1.0,
    //default 0.25 = 250ms). 0 = instant. Read live by WorldScreen's tooltip resolver.
    public static float TooltipDelaySeconds { get; set; } = 0.25f;

    //magnification of the tooltip font + layout (Options "Tooltip size" slider, 1.0 to 4.0). Read live in ItemTooltipControl.
    public static float TooltipScale { get; set; } = 1f;
    public static float EffectiveTooltipScale { get => Math.Clamp(EffectiveInterfaceTextScale * TooltipScale, 1f, 4f); }

    //how visible blocked entities (player/enemies/items/NPCs) are through walls (Options "Behind-walls opacity"
    //slider, 0 to 0.85; 0 = fully off). Pushed into SilhouetteRenderer.SilhouetteAlpha at boot and whenever the slider changes.
    public static float SilhouetteAlpha { get; set; } = 0.35f;

    //show friendly NPCs through walls in the behind-walls silhouette (Options checkbox, OFF by default). Read live.
    public static bool ShowNpcsBehindWalls { get; set; }

    //play a sound (158) when a whisper is received. ON by default.
    public static bool WhisperSound { get; set; } = true;

    //draw the bezier targeting line from the hotbar slot to the cursor/target while selecting a spell target. OFF by default.
    public static bool SpellTargetLine { get; set; }

    private static string FilePath => Path.Combine(GlobalSettings.DataPath, FILE_NAME);

    public static void Load()
    {
        //start from the built-in keybinding defaults; any SwmBind_/SwmTurnModifier lines below override them
        Keybindings.ResetDefaults();

        if (!File.Exists(FilePath))
            return;

        try
        {
            foreach (var line in File.ReadLines(FilePath))
            {
                var colonIndex = line.IndexOf(':');

                if (colonIndex < 0)
                    continue;

                var key = line[..colonIndex]
                    .Trim();

                var value = line[(colonIndex + 1)..]
                    .Trim();

                switch (key)
                {
                    case "Sound Volume":
                        if (int.TryParse(value, out var sv))
                            SoundVolume = Math.Clamp(sv, 0, 10);

                        break;

                    case "Music Volume":
                        if (int.TryParse(value, out var mv))
                            MusicVolume = Math.Clamp(mv, 0, 10);

                        break;

                    case "doGroundAnimation":
                        DoGroundAnimation = value == "1";

                        break;

                    case "SkillSpellSelectByToggle":
                        UseShiftKeyForAltPanels = value != "1";

                        break;

                    case "GroupAnswer":
                        GroupOpen = value == "1";

                        break;

                    case "ScrollLevel":
                        if (int.TryParse(value, out var sl))
                            ScrollLevel = sl;

                        break;

                    //"UserClickMode" (the legacy key for profile-click) is deliberately NOT read - see EnableProfileClick

                    case "GroupObjectOption":
                        UseGroupWindow = value == "1";

                        break;

                    case "Chatting Mode":
                        if (int.TryParse(value, out var cm))
                            ChattingMode = cm;

                        break;

                    case "Speed":
                        if (int.TryParse(value, out var spd))
                            Speed = spd;

                        break;

                    //Sweden Mayhem custom settings (not part of the original Darkages.cfg format)
                    case "SwmInterfaceScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ins))
                            InterfaceScale = Math.Clamp(ins, 0f, 4f);

                        break;

                    case "SwmHotbarScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var hbs))
                            HotbarScale = Math.Clamp(hbs, 0.25f, 4f);

                        break;

                    case "SwmWindowScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ws))
                            WindowScale = Math.Clamp(ws, 0.25f, 4f);

                        break;

                    case "SwmChatFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var cfs))
                            ChatFontScale = Math.Clamp(cfs, 0.25f, 4f);

                        break;

                    case "SwmNameFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var nfs))
                            NameFontScale = Math.Clamp(nfs, 0.25f, 4f);

                        break;

                    case "SwmBubbleFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var bfs))
                            BubbleFontScale = Math.Clamp(bfs, 0.25f, 4f);

                        break;

                    case "SwmBubbleFade":
                        if (int.TryParse(value, out var bfd))
                            BubbleFadeSeconds = Math.Clamp(bfd, 0, 30);

                        break;

                    case "SwmVSync":
                        VSync = value == "1";

                        break;

                    case "SwmModernControls":
                        ModernControls = value == "1";

                        break;

                    case "SwmFlipWalkInteract":
                        FlipWalkInteract = value == "1";

                        break;

                    case "SwmFlipMouseTargetButtons":
                        FlipMouseTargetButtons = value == "1";

                        break;

                    case "SwmTooltipAlpha":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var tta))
                            TooltipAlpha = Math.Clamp(tta, 0.25f, 1f);

                        break;

                    case "SwmTooltipDelay":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var tdl))
                            TooltipDelaySeconds = Math.Clamp(tdl, 0f, 1f);

                        break;

                    case "SwmTooltipScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var tsc))
                            TooltipScale = Math.Clamp(tsc, 0.25f, 4f);

                        break;

                    case "SwmSilhouetteAlpha":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var sla))
                            SilhouetteAlpha = Math.Clamp(sla, 0f, 0.85f);

                        break;

                    case "SwmShowNpcsBehindWalls":
                        ShowNpcsBehindWalls = value == "1";

                        break;
                    case "SwmWhisperSound":
                        WhisperSound = value == "1";

                        break;
                    case "SwmSpellTargetLine":
                        SpellTargetLine = value == "1";

                        break;

                    case "SwmChatOffX":
                        if (int.TryParse(value, out var cwox))
                            ChatWindowOffsetX = cwox;

                        break;

                    case "SwmChatOffY":
                        if (int.TryParse(value, out var cwoy))
                            ChatWindowOffsetY = cwoy;

                        break;

                    case "SwmChatW":
                        if (int.TryParse(value, out var cww))
                            ChatWindowWidth = cww;

                        break;

                    case "SwmChatH":
                        if (int.TryParse(value, out var cwh))
                            ChatWindowHeight = cwh;

                        break;

                    case "SwmChatFadeDelay":
                        if (int.TryParse(value, out var cfd))
                            ChatFadeDelaySeconds = Math.Clamp(cfd, 0, 60);

                        break;

                    case "SwmChatWindowFade":
                        if (int.TryParse(value, out var cwf))
                            ChatWindowFadeSeconds = Math.Clamp(cwf, 0, 30);

                        break;

                    case "SwmMutedTabs":
                        MutedChatTabs.Clear();

                        foreach (var t in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            MutedChatTabs.Add(t);

                        break;

                    case "SwmShowNpcChat":
                        RecordNpcChat = value == "1";

                        break;

                    case "SwmChatTimestamp":
                        ShowChatTimestamp = value == "1";

                        break;

                    case "SwmProfileClick":
                        EnableProfileClick = value == "1";

                        break;

                    case "SwmGoldSlot":
                        if (int.TryParse(value, out var gs))
                            GoldSlotIndex = gs;

                        break;

                    case "SwmDragMenuBtn":
                        AllowDragMenuButton = value == "1";

                        break;

                    case "SwmDragMinimap":
                        AllowDragMinimap = value == "1";

                        break;

                    case "SwmMenuAttachSide":
                        if (int.TryParse(value, out var mas))
                            MenuButtonAttachSide = mas;

                        break;

                    case "SwmMenuBtnOffX":
                        if (int.TryParse(value, out var mbOffX))
                            MenuButtonOffsetX = mbOffX;

                        break;

                    case "SwmMenuBtnOffY":
                        if (int.TryParse(value, out var mbOffY))
                            MenuButtonOffsetY = mbOffY;

                        break;

                    case "SwmMinimap":
                        ShowMinimap = value == "1";

                        break;

                    case "SwmMinimapOffX":
                        if (int.TryParse(value, out var mmOffX))
                            MinimapOffsetX = mmOffX;

                        break;

                    case "SwmMinimapOffY":
                        if (int.TryParse(value, out var mmOffY))
                            MinimapOffsetY = mmOffY;

                        break;

                    case "SwmMinimapScale":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mms))
                            MinimapScale = Math.Clamp(mms, 0.5f, 4f);

                        break;

                    case "SwmSmoothMapView":
                        SmoothMapView = value == "1";

                        break;

                    case "SwmSmoothCreatures":
                        SmoothCreatureMovement = value == "1";

                        break;

                    case "SwmAltMapFade":
                        AlternativeMapFade = value == "1";

                        break;

                    case "SwmFocusSpeaker":
                        FocusSpeaker = value == "1";

                        break;

                    case "SwmCameraShake":
                        //old saves wrote "0"/"1" as a boolean; new saves write the percentage (5, 50, 100, ...)
                        if (value == "0")
                            CameraShake = 0f;
                        else if (value == "1")
                            CameraShake = 100f;
                        else if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var csVal))
                            CameraShake = Math.Clamp(csVal, 0f, 100f);

                        break;

                    case "SwmCameraEffects":
                        if (value == "0")
                            CameraEffects = 0f;
                        else if (value == "1")
                            CameraEffects = 100f;
                        else if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ceVal))
                            CameraEffects = Math.Clamp(ceVal, 0f, 100f);

                        break;

                    case "SwmWorldMapInfo":
                        WorldMapShowInfo = value == "1";

                        break;

                    case "SwmStep1":
                        FootstepStepsEnabled[0] = value == "1";

                        break;

                    case "SwmStep2":
                        FootstepStepsEnabled[1] = value == "1";

                        break;

                    case "SwmStep3":
                        FootstepStepsEnabled[2] = value == "1";

                        break;

                    case "SwmStep4":
                        FootstepStepsEnabled[3] = value == "1";

                        break;

                    case "SwmFootstepVolume":
                        if (int.TryParse(value, out var fsv))
                            FootstepVolume = Math.Clamp(fsv, 0, 100);

                        break;

                    case "SwmChatVolume":
                        if (int.TryParse(value, out var cbv))
                            ChatBubbleVolume = Math.Clamp(cbv, 0, 100);

                        break;

                    //keybindings (SwmBind_<Action> and SwmTurnModifier) are owned by the Keybindings system
                    default:
                        Keybindings.ApplyConfigLine(key, value);

                        break;
                }
            }
        } catch
        {
            //corrupted file; fall back to whatever defaults or partial state was already set
        }
    }

    public static void Save()
    {
        try
        {
            using var writer = new StreamWriter(FilePath, false);
            writer.WriteLine("Version: 9728");
            writer.WriteLine("Port: 5");
            writer.WriteLine($"Speed: {Speed}");
            writer.WriteLine("KeyBoard: 0");
            writer.WriteLine("Tel: 1");
            writer.WriteLine("HanFont: 0");
            writer.WriteLine("EngFont: 0");
            writer.WriteLine("Tel1: \"Nexus\",\"1\"");
            writer.WriteLine("Tel2: \"Nexus\",\"2\"");
            writer.WriteLine("Tel3: \"Nexus\",\"3\"");
            writer.WriteLine("Tel4: \"Nexus\",\"4\"");
            writer.WriteLine($"Chatting Mode : {ChattingMode}");
            writer.WriteLine($"doGroundAnimation : {(DoGroundAnimation ? 1 : 0)}");
            writer.WriteLine($"Sound Volume : {SoundVolume}");
            writer.WriteLine($"Music Volume : {MusicVolume}");
            writer.WriteLine($"SkillSpellSelectByToggle : {(UseShiftKeyForAltPanels ? 0 : 1)}");
            writer.WriteLine($"GroupAnswer : {(GroupOpen ? 1 : 0)}");
            writer.WriteLine($"ScrollLevel : {ScrollLevel}");
            writer.WriteLine($"GroupObjectOption : {(UseGroupWindow ? 1 : 0)}");
            writer.WriteLine($"SwmInterfaceScale : {InterfaceScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmProfileClick : {(EnableProfileClick ? 1 : 0)}");
            writer.WriteLine($"SwmGoldSlot : {GoldSlotIndex}");
            writer.WriteLine($"SwmDragMenuBtn : {(AllowDragMenuButton ? 1 : 0)}");
            writer.WriteLine($"SwmDragMinimap : {(AllowDragMinimap ? 1 : 0)}");
            writer.WriteLine($"SwmMenuAttachSide : {MenuButtonAttachSide}");
            writer.WriteLine($"SwmMenuBtnOffX : {MenuButtonOffsetX}");
            writer.WriteLine($"SwmMenuBtnOffY : {MenuButtonOffsetY}");
            writer.WriteLine($"SwmMinimap : {(ShowMinimap ? 1 : 0)}");
            writer.WriteLine($"SwmMinimapOffX : {MinimapOffsetX}");
            writer.WriteLine($"SwmMinimapOffY : {MinimapOffsetY}");
            writer.WriteLine($"SwmMinimapScale : {MinimapScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmHotbarScale : {HotbarScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmWindowScale : {WindowScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmChatFontScale : {ChatFontScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmBubbleFontScale : {BubbleFontScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmNameFontScale : {NameFontScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmBubbleFade : {BubbleFadeSeconds}");
            writer.WriteLine($"SwmVSync : {(VSync ? 1 : 0)}");
            writer.WriteLine($"SwmModernControls : {(ModernControls ? 1 : 0)}");
            writer.WriteLine($"SwmFlipWalkInteract : {(FlipWalkInteract ? 1 : 0)}");
            writer.WriteLine($"SwmFlipMouseTargetButtons : {(FlipMouseTargetButtons ? 1 : 0)}");
            writer.WriteLine($"SwmTooltipAlpha : {TooltipAlpha.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmTooltipDelay : {TooltipDelaySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmTooltipScale : {TooltipScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmSilhouetteAlpha : {SilhouetteAlpha.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmShowNpcsBehindWalls : {(ShowNpcsBehindWalls ? 1 : 0)}");
            writer.WriteLine($"SwmWhisperSound : {(WhisperSound ? 1 : 0)}");
            writer.WriteLine($"SwmSpellTargetLine : {(SpellTargetLine ? 1 : 0)}");
            writer.WriteLine($"SwmChatOffX : {ChatWindowOffsetX}");
            writer.WriteLine($"SwmChatOffY : {ChatWindowOffsetY}");
            writer.WriteLine($"SwmChatW : {ChatWindowWidth}");
            writer.WriteLine($"SwmChatH : {ChatWindowHeight}");
            writer.WriteLine($"SwmChatFadeDelay : {ChatFadeDelaySeconds}");
            writer.WriteLine($"SwmChatWindowFade : {ChatWindowFadeSeconds}");
            writer.WriteLine($"SwmMutedTabs : {string.Join(",", MutedChatTabs)}");
            writer.WriteLine($"SwmShowNpcChat : {(RecordNpcChat ? 1 : 0)}");
            writer.WriteLine($"SwmChatTimestamp : {(ShowChatTimestamp ? 1 : 0)}");
            writer.WriteLine($"SwmSmoothMapView : {(SmoothMapView ? 1 : 0)}");
            writer.WriteLine($"SwmSmoothCreatures : {(SmoothCreatureMovement ? 1 : 0)}");
            writer.WriteLine($"SwmAltMapFade : {(AlternativeMapFade ? 1 : 0)}");
            writer.WriteLine($"SwmFocusSpeaker : {(FocusSpeaker ? 1 : 0)}");
            writer.WriteLine($"SwmCameraShake : {CameraShake.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmWorldMapInfo : {(WorldMapShowInfo ? 1 : 0)}");
            writer.WriteLine($"SwmCameraEffects : {CameraEffects.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            writer.WriteLine($"SwmStep1 : {(FootstepStepsEnabled[0] ? 1 : 0)}");
            writer.WriteLine($"SwmStep2 : {(FootstepStepsEnabled[1] ? 1 : 0)}");
            writer.WriteLine($"SwmStep3 : {(FootstepStepsEnabled[2] ? 1 : 0)}");
            writer.WriteLine($"SwmStep4 : {(FootstepStepsEnabled[3] ? 1 : 0)}");
            writer.WriteLine($"SwmFootstepVolume : {FootstepVolume}");
            writer.WriteLine($"SwmChatVolume : {ChatBubbleVolume}");
            Keybindings.WriteConfig(writer);
        } catch
        {
            //best effort, don't crash on save failure
        }
    }
}