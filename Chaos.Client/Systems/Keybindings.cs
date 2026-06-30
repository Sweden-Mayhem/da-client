#region
using System.Text;
using Chaos.Client.Definitions;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Systems;

/// <summary>Every rebindable in-world action. Order here is the order rows appear in the Controls window.</summary>
public enum GameAction
{
    //movement
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,

    //panels / windows (always allowed, even while a popup is up, so a panel key can close its own panel)
    ToggleInventory,
    ToggleSkills,
    ToggleSpells,
    ToggleStats,
    ToggleEquipment,
    ToggleLegend,
    ToggleActions,
    ToggleGroup,
    ToggleMarket,
    ToggleTownMap,
    ToggleMinimap,
    ToggleTownMinimap,
    ToggleOptions,
    ToggleWorldList,
    ToggleSettings,
    ToggleBulletinBoard,
    ToggleFriends,
    ToggleSocialStatus,
    ToggleEmotes,
    ToggleQuestJournal,

    //combat / hotbar
    Assail,
    TargetFriendly, //while a spell is readied, cast it on the closest friendly (self / group member)
    TargetEnemy,    //while a spell is readied, cast it on the closest enemy
    Skill1, Skill2, Skill3, Skill4, Skill5, Skill6, Skill7, Skill8, Skill9, Skill10, Skill11, Skill12,
    Spell1, Spell2, Spell3, Spell4, Spell5, Spell6, Spell7, Spell8, Spell9, Spell10, Spell11, Spell12,
    Item1, Item2, Item3, Item4, Item5, Item6, Item7, Item8, Item9, Item10, Item11, Item12,

    //misc
    PickUpItem,
    UnequipWeaponShield,
    FocusWhisper,
    FlashGroup,
    MinimapZoomIn,
    MinimapZoomOut,
    LogOut,

    //system (handled globally by ChaosGame, not WorldScreen)
    ToggleFullscreen,
    Screenshot,
    ToggleDebugOverlay,

    //emotes: one GameAction per emote BodyAnimation. Order MUST match Keybindings.EmoteOrder (index arithmetic maps
    //the action back to its BodyAnimation). Defaults are the classic Ctrl / Ctrl+Alt / Alt + number-row banks.
    EmoteSmile, EmoteCry, EmoteFrown, EmoteWink, EmoteSurprise, EmoteTongue, EmotePleasant, EmoteSnore, EmoteMouth,
    EmoteBlowKiss, EmoteWave, EmoteRockOn, EmotePeace, EmoteStop, EmoteOuch, EmoteImpatient, EmoteShock, EmotePleasure,
    EmoteLove, EmoteSweatDrop, EmoteWhistle, EmoteIrritation, EmoteSilly, EmoteCute, EmoteYelling, EmoteMischievous,
    EmoteEvil, EmoteHorror, EmotePuppyDog, EmoteStoneFaced, EmoteTears, EmoteFiredUp, EmoteConfused
}

/// <summary>Grouping used by the Controls window and by the in-world dispatch rules.</summary>
public enum BindCategory
{
    Movement,
    Panels,
    Combat,
    Misc,
    System,
    Emotes
}

/// <summary>A single key + modifier combo. <see cref="None" /> means "unbound".</summary>
public readonly record struct KeyBind(Keys Key, KeyModifiers Mods)
{
    public static readonly KeyBind None = new(Keys.None, KeyModifiers.None);

    public bool IsBound => Key != Keys.None;

    /// <summary>Friendly text for the UI, e.g. "Shift+1", "W", "Up", or "" when unbound.</summary>
    public string Display()
    {
        if (!IsBound)
            return string.Empty;

        var prefix = string.Empty;

        if (Mods.HasFlag(KeyModifiers.Ctrl))
            prefix += "Ctrl+";

        if (Mods.HasFlag(KeyModifiers.Alt))
            prefix += "Alt+";

        if (Mods.HasFlag(KeyModifiers.Shift))
            prefix += "Shift+";

        return prefix + Keybindings.KeyName(Key);
    }

    /// <summary>Compact text for tight spots like the hotbar slot labels. Modifier prefixes are single letters
    ///     (Ctrl="C+", Alt="A+", Shift="S+") followed by the key name, e.g. "S+1", "C+A+1", "F1". Empty string when unbound.</summary>
    public string DisplayShort()
    {
        if (!IsBound)
            return string.Empty;

        var prefix = string.Empty;

        if (Mods.HasFlag(KeyModifiers.Ctrl))
            prefix += "C+";

        if (Mods.HasFlag(KeyModifiers.Alt))
            prefix += "A+";

        if (Mods.HasFlag(KeyModifiers.Shift))
            prefix += "S+";

        return prefix + Keybindings.KeyName(Key);
    }

    /// <summary>Compact "&lt;modsInt&gt;.&lt;KeyEnumName&gt;" for the config file. Empty string when unbound.</summary>
    public string Serialize() => IsBound ? $"{(int)Mods}.{Key}" : string.Empty;

    public static KeyBind Parse(string token)
    {
        token = token.Trim();

        if (token.Length == 0)
            return None;

        var dot = token.IndexOf('.');

        if ((dot <= 0) || !int.TryParse(token[..dot], out var mods) || !Enum.TryParse<Keys>(token[(dot + 1)..], out var key))
            return None;

        return new KeyBind(key, (KeyModifiers)mods);
    }
}

public readonly record struct ActionInfo(GameAction Action, string Label, BindCategory Category, bool Bindable = true);

public static class Keybindings
{
    private const KeyModifiers MOD_MASK = KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt;

    //the number-row keys feeding skill/spell slots 1..12
    private static readonly Keys[] SlotKeys =
    [
        Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0,
        Keys.OemMinus, Keys.OemPlus
    ];

    private static readonly GameAction[] MovementActions = [GameAction.MoveUp, GameAction.MoveDown, GameAction.MoveLeft, GameAction.MoveRight];

    public static readonly BodyAnimation[] EmoteOrder =
    [
        BodyAnimation.Smile, BodyAnimation.Cry, BodyAnimation.Frown, BodyAnimation.Wink, BodyAnimation.Surprise,
        BodyAnimation.Tongue, BodyAnimation.Pleasant, BodyAnimation.Snore, BodyAnimation.Mouth, BodyAnimation.BlowKiss,
        BodyAnimation.Wave, BodyAnimation.RockOn, BodyAnimation.Peace, BodyAnimation.Stop, BodyAnimation.Ouch,
        BodyAnimation.Impatient, BodyAnimation.Shock, BodyAnimation.Pleasure, BodyAnimation.Love, BodyAnimation.SweatDrop,
        BodyAnimation.Whistle, BodyAnimation.Irritation, BodyAnimation.Silly, BodyAnimation.Cute, BodyAnimation.Yelling,
        BodyAnimation.Mischievous, BodyAnimation.Evil, BodyAnimation.Horror, BodyAnimation.PuppyDog, BodyAnimation.StoneFaced,
        BodyAnimation.Tears, BodyAnimation.FiredUp, BodyAnimation.Confused
    ];

    public static string EmoteLabel(BodyAnimation emote)
    {
        var name = emote.ToString();
        var sb = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if ((i > 0) && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');

            sb.Append(name[i]);
        }

        return sb.ToString();
    }

    /// <summary>All actions, in display order, with their labels and categories.</summary>
    public static readonly ActionInfo[] Actions = BuildActionInfo();

    private static readonly Dictionary<GameAction, BindCategory> Categories = Actions.ToDictionary(a => a.Action, a => a.Category);

    /// <summary>Current bindings. Value is (primary, secondary).</summary>
    public static readonly Dictionary<GameAction, (KeyBind Primary, KeyBind Secondary)> Binds = new();

    /// <summary>Raised whenever a binding changes (rebind, clear, or reset) so live UI can refresh (e.g. hotbar slot labels).</summary>
    public static event Action? Changed;

    /// <summary>Held modifier that makes the movement keys turn the character in place instead of walking. None = off.</summary>
    public static KeyModifiers TurnModifier { get; set; } = KeyModifiers.Shift;

    public static bool IsCapturing { get; set; }

    static Keybindings() => ResetDefaults();

    public static BindCategory CategoryOf(GameAction action) => Categories.GetValueOrDefault(action, BindCategory.Misc);

    public static void ResetDefaults()
    {
        TurnModifier = KeyModifiers.Shift;
        Binds.Clear();

        //seed EVERY action as unbound first, so an action with no default (e.g. TargetFriendly/TargetEnemy) still has an
        //entry. Get()/Resolve() index Binds directly and would otherwise throw KeyNotFound for it.
        foreach (var action in Enum.GetValues<GameAction>())
            Binds[action] = (KeyBind.None, KeyBind.None);

        foreach (var (action, p, s) in BuildDefaults())
            Binds[action] = (p, s);

        Changed?.Invoke();
    }

    public static GameAction? Resolve(Keys key, KeyModifiers mods)
    {
        mods &= MOD_MASK;

        //exact modifier match wins (so Shift+1 = spell while plain 1 = skill)
        foreach (var info in Actions)
        {
            var (p, s) = Binds[info.Action];

            if ((p.IsBound && (p.Key == key) && (p.Mods == mods)) || (s.IsBound && (s.Key == key) && (s.Mods == mods)))
                return info.Action;
        }

        //turn modifier held: movement keys still resolve (the dispatcher turns instead of walks). this lets the turn
        //modifier (default Shift) work for movement without breaking Shift+number = spell, which matched exactly above.
        if ((TurnModifier != KeyModifiers.None) && ((mods & TurnModifier) != 0))
        {
            var stripped = mods & ~TurnModifier;

            foreach (var action in MovementActions)
            {
                var (p, s) = Binds[action];

                if ((p.IsBound && (p.Key == key) && (p.Mods == stripped)) || (s.IsBound && (s.Key == key) && (s.Mods == stripped)))
                    return action;
            }
        }

        return null;
    }

    public static bool IsTurnHeld(KeyModifiers mods) => (TurnModifier != KeyModifiers.None) && ((mods & TurnModifier) != 0);

    public static GameAction? HeldMovement(out bool turnOnly)
    {
        var mods = InputBuffer.CurrentModifiers & MOD_MASK;
        var turnActive = (TurnModifier != KeyModifiers.None) && ((mods & TurnModifier) != 0);

        foreach (var action in MovementActions)
        {
            var (p, s) = Binds[action];

            if (Held(p) || Held(s))
            {
                turnOnly = turnActive;

                return action;
            }
        }

        turnOnly = false;

        return null;

        //exact-modifier match walks. If the turn modifier is also held, pivot in place instead.
        bool Held(KeyBind b)
            => b.IsBound && InputBuffer.IsKeyHeld(b.Key) && ((mods == b.Mods) || (turnActive && ((mods & ~TurnModifier) == b.Mods)));
    }

    public static bool IsActionHeld(GameAction action)
    {
        var (p, s) = Binds[action];
        var mods = InputBuffer.CurrentModifiers & MOD_MASK;

        return (p.IsBound && InputBuffer.IsKeyHeld(p.Key) && (mods == p.Mods))
               || (s.IsBound && InputBuffer.IsKeyHeld(s.Key) && (mods == s.Mods));
    }

    public static bool Triggered(GameAction action)
    {
        var (p, s) = Binds[action];

        return Matches(p) || Matches(s);
    }

    private static bool Matches(KeyBind b)
        => b.IsBound && InputBuffer.WasKeyPressed(b.Key) && ((InputBuffer.CurrentModifiers & MOD_MASK) == b.Mods);

    public static (KeyBind Primary, KeyBind Secondary) Get(GameAction action) => Binds[action];

    public static void Set(GameAction action, int slot, KeyBind bind)
    {
        if (bind.IsBound)
            foreach (var info in Actions)
            {
                var b = Binds[info.Action];
                var changed = false;

                if (b.Primary == bind)
                {
                    b.Primary = KeyBind.None;
                    changed = true;
                }

                if (b.Secondary == bind)
                {
                    b.Secondary = KeyBind.None;
                    changed = true;
                }

                if (changed)
                    Binds[info.Action] = b;
            }

        var cur = Binds[action];

        if (slot == 0)
            cur.Primary = bind;
        else
            cur.Secondary = bind;

        Binds[action] = cur;
        Changed?.Invoke();
    }

    public static string[] SlotBarLabels(GameAction firstSlot)
    {
        var labels = new string[12];

        for (var i = 0; i < 12; i++)
        {
            var (primary, secondary) = Get((GameAction)((int)firstSlot + i));
            labels[i] = (primary.IsBound ? primary : secondary).DisplayShort();
        }

        return labels;
    }

    public static string KeyName(Keys key)
        => key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (int)(key - Keys.NumPad0),
            Keys.OemMinus    => "-",
            Keys.OemPlus     => "=",
            Keys.OemQuestion => "/",
            Keys.OemTilde    => "`",
            Keys.OemQuotes   => "'",
            Keys.OemComma    => ",",
            Keys.OemPeriod   => ".",
            Keys.OemSemicolon => ";",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemPipe     => "\\",
            Keys.OemBackslash => "\\",
            Keys.Up          => "Up",
            Keys.Down        => "Down",
            Keys.Left        => "Left",
            Keys.Right       => "Right",
            Keys.PageUp      => "PgUp",
            Keys.PageDown    => "PgDn",
            Keys.Escape      => "Esc",
            Keys.PrintScreen => "PrtSc",
            Keys.None        => string.Empty,
            _                => key.ToString()
        };

    //--- persistence (called by ClientSettings) ---

    public static void ApplyConfigLine(string key, string value)
    {
        if (key == "SwmTurnModifier")
        {
            if (int.TryParse(value, out var m))
                TurnModifier = (KeyModifiers)(m & (int)MOD_MASK);

            return;
        }

        const string prefix = "SwmBind_";

        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return;

        if (!Enum.TryParse<GameAction>(key[prefix.Length..], out var action))
            return;

        var parts = value.Split(',');
        var primary = parts.Length > 0 ? KeyBind.Parse(parts[0]) : KeyBind.None;
        var secondary = parts.Length > 1 ? KeyBind.Parse(parts[1]) : KeyBind.None;
        Binds[action] = (primary, secondary);
    }

    public static void WriteConfig(TextWriter writer)
    {
        writer.WriteLine($"SwmTurnModifier : {(int)TurnModifier}");

        foreach (var info in Actions)
        {
            var (p, s) = Binds[info.Action];
            writer.WriteLine($"SwmBind_{info.Action} : {p.Serialize()},{s.Serialize()}");
        }
    }

    //--- defaults table ---

    private static ActionInfo[] BuildActionInfo()
    {
        var list = new List<ActionInfo>
        {
            new(GameAction.MoveUp, "Walk up", BindCategory.Movement),
            new(GameAction.MoveDown, "Walk down", BindCategory.Movement),
            new(GameAction.MoveLeft, "Walk left", BindCategory.Movement),
            new(GameAction.MoveRight, "Walk right", BindCategory.Movement),

            new(GameAction.ToggleInventory, "Inventory", BindCategory.Panels),
            new(GameAction.ToggleSkills, "Skills", BindCategory.Panels),
            new(GameAction.ToggleSpells, "Spells", BindCategory.Panels),
            new(GameAction.ToggleStats, "Stats", BindCategory.Panels),
            new(GameAction.ToggleEquipment, "Equipment", BindCategory.Panels),
            new(GameAction.ToggleLegend, "Legend", BindCategory.Panels),
            new(GameAction.ToggleActions, "Actions", BindCategory.Panels),
            new(GameAction.ToggleGroup, "Group", BindCategory.Panels),
            new(GameAction.ToggleMarket, "Market", BindCategory.Panels),
            new(GameAction.ToggleTownMap, "Town map", BindCategory.Panels),
            new(GameAction.ToggleMinimap, "Minimap", BindCategory.Panels),
            new(GameAction.ToggleTownMinimap, "Town minimap (corner)", BindCategory.Panels),
            new(GameAction.ToggleOptions, "Options", BindCategory.Panels),
            new(GameAction.ToggleWorldList, "Who is online", BindCategory.Panels),
            //deprecated: the old Settings/Macros/Friends slide menu is replaced by the Options window + the Friends key,
            //so it is no longer bindable (hidden from Options > Controls). The dispatch case stays but is unreachable.
            new(GameAction.ToggleSettings, "Settings / macros / friends", BindCategory.Panels, Bindable: false),
            new(GameAction.ToggleBulletinBoard, "Mail and Help", BindCategory.Panels),
            new(GameAction.ToggleFriends, "Friends", BindCategory.Panels),
            //social status is opened by clicking the emoticon in the Equipment book, not a key, so it is not rebindable
            new(GameAction.ToggleSocialStatus, "Social status", BindCategory.Panels, Bindable: false),
            new(GameAction.ToggleEmotes, "Emotes", BindCategory.Panels),
            new(GameAction.ToggleQuestJournal, "Quest Journal", BindCategory.Panels),

            new(GameAction.Assail, "Assail", BindCategory.Combat),
            new(GameAction.TargetFriendly, "Cast on closest friendly", BindCategory.Combat),
            new(GameAction.TargetEnemy, "Cast on closest enemy", BindCategory.Combat)
        };

        for (var i = 0; i < 12; i++)
            list.Add(new ActionInfo((GameAction)((int)GameAction.Skill1 + i), $"Skill {i + 1}", BindCategory.Combat));

        for (var i = 0; i < 12; i++)
            list.Add(new ActionInfo((GameAction)((int)GameAction.Spell1 + i), $"Spell {i + 1}", BindCategory.Combat));

        for (var i = 0; i < 12; i++)
            list.Add(new ActionInfo((GameAction)((int)GameAction.Item1 + i), $"Item {i + 1}", BindCategory.Combat));

        list.AddRange(
        [
            new ActionInfo(GameAction.PickUpItem, "Pick Up / Interact", BindCategory.Misc),
            new ActionInfo(GameAction.UnequipWeaponShield, "Unequip weapon + shield", BindCategory.Misc),
            new ActionInfo(GameAction.FocusWhisper, "Whisper", BindCategory.Misc),
            new ActionInfo(GameAction.FlashGroup, "Flash group members", BindCategory.Misc),
            new ActionInfo(GameAction.LogOut, "Log out", BindCategory.Misc),

            new ActionInfo(GameAction.ToggleFullscreen, "Fullscreen", BindCategory.System),
            new ActionInfo(GameAction.Screenshot, "Screenshot", BindCategory.System),
            new ActionInfo(GameAction.ToggleDebugOverlay, "Debug overlay", BindCategory.System)
        ]);

        for (var i = 0; i < EmoteOrder.Length; i++)
            list.Add(new ActionInfo((GameAction)((int)GameAction.EmoteSmile + i), EmoteLabel(EmoteOrder[i]), BindCategory.Emotes));

        return list.ToArray();
    }

    private static IEnumerable<(GameAction Action, KeyBind Primary, KeyBind Secondary)> BuildDefaults()
    {
        KeyBind K(Keys k) => new(k, KeyModifiers.None);
        KeyBind Shifted(Keys k) => new(k, KeyModifiers.Shift);

        //movement: WASD + arrow keys
        yield return (GameAction.MoveUp, K(Keys.W), K(Keys.Up));
        yield return (GameAction.MoveDown, K(Keys.S), K(Keys.Down));
        yield return (GameAction.MoveLeft, K(Keys.A), K(Keys.Left));
        yield return (GameAction.MoveRight, K(Keys.D), K(Keys.Right));

        //panels: WoW/GW2-style letters
        yield return (GameAction.ToggleInventory, K(Keys.I), K(Keys.B));
        yield return (GameAction.ToggleSkills, K(Keys.K), KeyBind.None);
        yield return (GameAction.ToggleSpells, K(Keys.P), KeyBind.None);
        yield return (GameAction.ToggleStats, K(Keys.C), KeyBind.None);
        yield return (GameAction.ToggleEquipment, K(Keys.U), K(Keys.H));
        //J opens the Legend tab of the same Equipment book. Toggling it closes the book (see WorldScreen ToggleStatusBook)
        yield return (GameAction.ToggleLegend, K(Keys.J), KeyBind.None);
        yield return (GameAction.ToggleActions, K(Keys.N), KeyBind.None);
        yield return (GameAction.ToggleGroup, K(Keys.G), KeyBind.None);
        yield return (GameAction.ToggleMarket, K(Keys.T), KeyBind.None);
        yield return (GameAction.ToggleTownMap, K(Keys.M), KeyBind.None);
        yield return (GameAction.ToggleMinimap, K(Keys.Tab), KeyBind.None);
        yield return (GameAction.ToggleTownMinimap, Shifted(Keys.M), KeyBind.None);
        yield return (GameAction.ToggleOptions, Shifted(Keys.O), KeyBind.None); //Shift+O (plain O opens Who is online)
        yield return (GameAction.ToggleWorldList, K(Keys.O), KeyBind.None);
        //the old "Settings / macros / friends" slide menu is deprecated (Options window + Friends key replace it): unbound
        yield return (GameAction.ToggleSettings, KeyBind.None, KeyBind.None);
        yield return (GameAction.ToggleBulletinBoard, K(Keys.R), KeyBind.None);
        yield return (GameAction.ToggleFriends, K(Keys.L), K(Keys.Y));
        yield return (GameAction.ToggleSocialStatus, KeyBind.None, KeyBind.None);
        yield return (GameAction.ToggleEmotes, K(Keys.Q), KeyBind.None);
        yield return (GameAction.ToggleQuestJournal, K(Keys.V), KeyBind.None); //V free; rebindable in Options > Controls

        //combat / hotbar
        yield return (GameAction.Assail, K(Keys.Space), KeyBind.None);

        for (var i = 0; i < 12; i++)
            yield return ((GameAction)((int)GameAction.Skill1 + i), K(SlotKeys[i]), KeyBind.None);

        for (var i = 0; i < 12; i++)
            yield return ((GameAction)((int)GameAction.Spell1 + i), Shifted(SlotKeys[i]), KeyBind.None);

        for (var i = 0; i < 12; i++)
            yield return ((GameAction)((int)GameAction.Item1 + i), K(Keys.F1 + i), KeyBind.None);

        //misc
        yield return (GameAction.PickUpItem, K(Keys.E), K(Keys.F));
        yield return (GameAction.UnequipWeaponShield, K(Keys.OemTilde), KeyBind.None);
        yield return (GameAction.FocusWhisper, Shifted(Keys.OemQuotes), KeyBind.None);
        //J now opens the Legend book
        yield return (GameAction.FlashGroup, K(Keys.OemComma), K(Keys.OemPeriod));
        //log out back to the lobby: Ctrl+Q by default (Q alone opens the emote menu)
        yield return (GameAction.LogOut, new KeyBind(Keys.Q, KeyModifiers.Ctrl), KeyBind.None);

        //system
        yield return (GameAction.ToggleFullscreen, new KeyBind(Keys.Enter, KeyModifiers.Alt), KeyBind.None);
        //PrintScreen is often swallowed by the OS, so a reliable secondary (Ctrl+B) ships by default; both rebindable
        yield return (GameAction.Screenshot, K(Keys.PrintScreen), new KeyBind(Keys.B, KeyModifiers.Ctrl));
        yield return (GameAction.ToggleDebugOverlay, KeyBind.None, KeyBind.None);

        //emotes: the classic banks across the number row (keys 1-0 then "-"), Ctrl bank first, then Ctrl+Alt, then Alt
        for (var i = 0; i < EmoteOrder.Length; i++)
        {
            var action = (GameAction)((int)GameAction.EmoteSmile + i);

            var bind = i < 11 ? new KeyBind(SlotKeys[i], KeyModifiers.Ctrl)
                : i < 22 ? new KeyBind(SlotKeys[i - 11], KeyModifiers.Ctrl | KeyModifiers.Alt)
                : new KeyBind(SlotKeys[i - 22], KeyModifiers.Alt);

            yield return (action, bind, KeyBind.None);
        }
    }
}
