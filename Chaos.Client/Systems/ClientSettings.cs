namespace Chaos.Client.Systems;

/// <summary>
///     Reads and writes the client settings file in the original DarkAges format. The file is a line-delimited
///     key-value format, "Key : Value" or "Key: Value", saved next to the executable.
/// </summary>
public static class ClientSettings
{
    private const string FILE_NAME = "Darkages.cfg";
    public static bool UseGroupWindow { get; set; } = true;
    public static int ChattingMode { get; set; }
    public static bool DoGroundAnimation { get; set; } = true;
    //clicking another non-hostile player opens their profile, on by default
    //the legacy UserClickMode key is deliberately not read so old configs do not force this off
    public static bool EnableProfileClick { get; set; } = true;

    //the inventory grid cell the gold bag sits in (it is draggable), -1 means the last visible cell
    public static int GoldSlotIndex { get; set; } = -1;

    //corner minimap on/off plus its dragged position (-1 means not placed yet, anchor to a default corner)
    public static bool ShowMinimap { get; set; } = true;
    public static int MinimapX { get; set; } = -1;
    public static int MinimapY { get; set; } = -1;
    public static float MinimapScale { get; set; } = 1f; //scales the circle size only, same tiles shown
    public static bool GroupOpen { get; set; }
    public static int MusicVolume { get; set; } = 3; //30% by default on a 0-10 scale, quieter than sound

    //show NPC dialog and ambient chatter in the chat window, off filters it out
    //the legacy MonsterSayRecordMode key is deliberately not read so old configs do not force this on
    public static bool RecordNpcChat { get; set; }

    //a chat line stays fully readable for this many seconds then fades by age
    //0 means lines never fade
    public static int ChatFadeDelaySeconds { get; set; } = 15;

    //once the cursor leaves the chat window (and you are not typing or pinned) it stays for this many seconds
    //then the window chrome fades out and becomes click-through, 0 means never fade
    public static int ChatWindowFadeSeconds { get; set; } = 3;

    //chat tabs whose highlight was turned off, they never pulse on a new message
    //keyed by tab key (built-in channel name or a custom channel)
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

    //smooth bilinear filtering for the town map overview instead of crisp point sampling
    public static bool SmoothMapView { get; set; } = true;

    //screen scroll smoothing, above 0 is smooth, 0 is rough
    public static int ScrollLevel { get; set; } = 1;

    //smooth pixel-interpolated movement for other entities instead of the discrete per-frame step
    public static bool SmoothCreatureMovement { get; set; } = true;

    //map change transition style, false cross-dissolves old map into new, true fades out to black then in
    public static bool AlternativeMapFade { get; set; }

    //while an NPC dialog is open, smoothly pan the camera to the speaking NPC then ease back when it closes
    public static bool FocusSpeaker { get; set; } = true;

    //subtle horizontal camera shake when the local player loses HP, 0 is off and 100 is full
    public static float CameraShake { get; set; } = 100f;

    //screen-edge red pulse when the local player loses HP, plus a faint pulse at 10% HP or less, 0 is off and 100 is full
    public static float CameraEffects { get; set; } = 100f;

    //whether the world map travel window shows the destination info panel by default
    public static bool WorldMapShowInfo { get; set; } = true;

    //per-footstep-sample enable flags, lets you hear which sample is which
    public static bool[] FootstepStepsEnabled { get; } = [true, true, true, true];

    //footstep loudness as a 0-100 percentage of the Sound volume, 0 is off
    public static int FootstepVolume { get; set; } = 15;

    //chat-bubble cue loudness as a 0-100 percentage of the Sound volume, 0 is off
    public static int ChatBubbleVolume { get; set; } = 50;

    //defaults match the original client
    public static int SoundVolume { get; set; } = 5;
    public static int Speed { get; set; } = 100;
    public static bool UseShiftKeyForAltPanels { get; set; } = true;

    //magnification of the on-screen hotbars, read live each frame so it is independent of the inventory window scale
    public static float HotbarScale { get; set; } = 1f;

    //magnification of floating windows that host pixel-art panels, driven by the Options window size slider
    public static float WindowScale { get; set; } = 1.5f;

    //scale of the chat log and input text, multiplies the base 14px size
    public static float ChatFontScale { get; set; } = 1f;

    //scale of the over-head chat bubble text, multiplies the bubble base 13px size
    public static float BubbleFontScale { get; set; } = 1f;

    //scale of the over-head entity name tags, multiplies the base 13px size
    public static float NameFontScale { get; set; } = 1f;

    //seconds an over-head chat bubble stays before fading out, 0 disables bubbles entirely
    public static int BubbleFadeSeconds { get; set; } = 4;

    //run at the monitor refresh with no tearing
    public static bool VSync { get; set; } = true;

    //modern click scheme, right-click always moves and left-click always interacts
    //off uses the classic scheme where right double-click follows and assails, left double-click picks up
    public static bool ModernControls { get; set; } = true;

    //when on, left-click moves and right-click interacts or attacks (flips the modern-controls buttons)
    public static bool FlipWalkInteract { get; set; } = false;

    //mouse target buttons, normally Mouse 3 targets self and Mouse 4 targets enemy, on swaps the two
    public static bool FlipMouseTargetButtons { get; set; } = false;

    //opacity of the item and map hover tooltip background
    public static float TooltipAlpha { get; set; } = 0.85f;

    //seconds the cursor must rest on something before its tooltip appears, 0 is instant
    public static float TooltipDelaySeconds { get; set; } = 0.25f;

    //magnification of the tooltip font and layout
    public static float TooltipScale { get; set; } = 1.15f;

    //how visible blocked entities are through walls, 0 is fully off
    public static float SilhouetteAlpha { get; set; } = 0.35f;

    //show friendly NPCs through walls in the behind-walls silhouette
    public static bool ShowNpcsBehindWalls { get; set; }

    //draw the bezier targeting line from the hotbar slot to the cursor while selecting a spell target
    public static bool SpellTargetLine { get; set; }

    private static string FilePath => Path.Combine(GlobalSettings.DataPath, FILE_NAME);

    /// <summary>
    ///     Loads settings into static properties. Uses defaults if the file does not exist or is corrupt.
    /// </summary>
    public static void Load()
    {
        //start from the built-in keybinding defaults, the bind lines below override them
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

                    //the legacy UserClickMode key for profile-click is deliberately not read, see EnableProfileClick

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

                    //custom settings not part of the original config format
                    case "SwmHotbarScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var hbs))
                            HotbarScale = Math.Clamp(hbs, 1f, 4f);

                        break;

                    case "SwmWindowScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ws))
                            WindowScale = Math.Clamp(ws, 1f, 4f);

                        break;

                    case "SwmChatFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var cfs))
                            ChatFontScale = Math.Clamp(cfs, 1f, 4f);

                        break;

                    case "SwmNameFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var nfs))
                            NameFontScale = Math.Clamp(nfs, 1f, 4f);

                        break;

                    case "SwmBubbleFontScale":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var bfs))
                            BubbleFontScale = Math.Clamp(bfs, 1f, 4f);

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
                            TooltipScale = Math.Clamp(tsc, 1f, 4f);

                        break;

                    case "SwmSilhouetteAlpha":
                        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var sla))
                            SilhouetteAlpha = Math.Clamp(sla, 0f, 0.85f);

                        break;

                    case "SwmShowNpcsBehindWalls":
                        ShowNpcsBehindWalls = value == "1";

                        break;
                    case "SwmSpellTargetLine":
                        SpellTargetLine = value == "1";

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

                    case "SwmProfileClick":
                        EnableProfileClick = value == "1";

                        break;

                    case "SwmGoldSlot":
                        if (int.TryParse(value, out var gs))
                            GoldSlotIndex = gs;

                        break;

                    case "SwmMinimap":
                        ShowMinimap = value == "1";

                        break;

                    case "SwmMinimapX":
                        if (int.TryParse(value, out var mmx))
                            MinimapX = mmx;

                        break;

                    case "SwmMinimapY":
                        if (int.TryParse(value, out var mmy))
                            MinimapY = mmy;

                        break;

                    case "SwmMinimapScale":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mms))
                            MinimapScale = Math.Clamp(mms, 0.1f, 4f);

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
                        //old saves wrote 0 or 1 as a boolean, new saves write the percentage
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

                    //keybinding lines are owned by the Keybindings system
                    default:
                        Keybindings.ApplyConfigLine(key, value);

                        break;
                }
            }
        } catch
        {
            //corrupted file, use whatever defaults or partial state was already set
        }
    }

    /// <summary>
    ///     Saves the current settings in the original format.
    /// </summary>
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
            writer.WriteLine($"SwmProfileClick : {(EnableProfileClick ? 1 : 0)}");
            writer.WriteLine($"SwmGoldSlot : {GoldSlotIndex}");
            writer.WriteLine($"SwmMinimap : {(ShowMinimap ? 1 : 0)}");
            writer.WriteLine($"SwmMinimapX : {MinimapX}");
            writer.WriteLine($"SwmMinimapY : {MinimapY}");
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
            writer.WriteLine($"SwmSpellTargetLine : {(SpellTargetLine ? 1 : 0)}");
            writer.WriteLine($"SwmChatFadeDelay : {ChatFadeDelaySeconds}");
            writer.WriteLine($"SwmChatWindowFade : {ChatWindowFadeSeconds}");
            writer.WriteLine($"SwmMutedTabs : {string.Join(",", MutedChatTabs)}");
            writer.WriteLine($"SwmShowNpcChat : {(RecordNpcChat ? 1 : 0)}");
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
            //best effort, do not crash on save failure
        }
    }
}