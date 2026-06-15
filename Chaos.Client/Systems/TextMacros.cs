#region
using System.Text;
using Chaos.Client.Collections;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Expands <c>{{...}}</c> placeholders in server-authored text (NPC dialog bodies, menu options) into live client
///     values: the player's CURRENT keybinding for an action (<c>{{kb_inventory}}</c>), their name
///     (<c>{{player_name}}</c>), level, class, gold, etc. Expansion runs at display time, so a rebind or a different
///     character is always reflected, the server never has to know the player's key layout. An unknown token is left
///     verbatim (so a typo shows as <c>{{kb_typo}}</c> rather than silently vanishing). This is the COMPANION to the
///     inline <c>&lt;color&gt;</c> markup (see <see cref="Controls.Components.RichText" />): placeholders run first, then
///     the result is color-parsed at render time.
/// </summary>
public static class TextMacros
{
    //the {{kb_<name>}} suffix maps to the action whose current PRIMARY bind to show, "chat" is special-cased (fixed Enter)
    private static readonly Dictionary<string, GameAction> KeyActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inventory"] = GameAction.ToggleInventory,
        ["skills"] = GameAction.ToggleSkills,
        ["spells"] = GameAction.ToggleSpells,
        ["stats"] = GameAction.ToggleStats,
        ["equipment"] = GameAction.ToggleEquipment,
        ["legend"] = GameAction.ToggleLegend,
        ["actions"] = GameAction.ToggleActions,
        ["group"] = GameAction.ToggleGroup,
        ["options"] = GameAction.ToggleOptions,
        ["emotes"] = GameAction.ToggleEmotes,
        ["friends"] = GameAction.ToggleFriends,
        ["mail"] = GameAction.ToggleBulletinBoard,
        ["townmap"] = GameAction.ToggleTownMap,
        ["minimap"] = GameAction.ToggleMinimap,
        ["worldlist"] = GameAction.ToggleWorldList,
        ["pickup"] = GameAction.PickUpItem,
        ["interact"] = GameAction.PickUpItem,
        ["assail"] = GameAction.Assail,
        ["whisper"] = GameAction.FocusWhisper,
        ["unequip"] = GameAction.UnequipWeaponShield,
        ["logout"] = GameAction.LogOut
    };

    /// <summary>
    ///     Replaces every <c>{{token}}</c> in <paramref name="text" /> with its live value. Returns the text unchanged
    ///     when it contains no placeholders. Whitespace inside the braces is ignored, e.g. <c>{{ player_name }}</c>.
    /// </summary>
    public static string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);

            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);

                break;
            }

            sb.Append(text, i, open - i);
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);

            if (close < 0)
            {
                //unterminated, write the rest verbatim and stop
                sb.Append(text, open, text.Length - open);

                break;
            }

            var token = text.Substring(open + 2, close - (open + 2)).Trim();

            if (Resolve(token) is { } value)
                sb.Append(value);
            else
                sb.Append(text, open, close + 2 - open); //unknown token, leave the {{...}} literally so the author sees it

            i = close + 2;
        }

        return sb.ToString();
    }

    private static string? Resolve(string token)
    {
        if (token.StartsWith("kb_", StringComparison.OrdinalIgnoreCase))
        {
            var name = token[3..];

            //chat is opened by the fixed Enter key (not a rebindable action)
            if (name.Equals("chat", StringComparison.OrdinalIgnoreCase))
                return "Enter";

            if (!KeyActions.TryGetValue(name, out var action))
                return null;

            //show BOTH binds the player has set, one bound shows just that key, two bound shows "<key1> or <key2>"
            var (primary, secondary) = Keybindings.Get(action);

            if (primary.IsBound && secondary.IsBound)
                return primary.Display() + " or " + secondary.Display();

            if (primary.IsBound)
                return primary.Display();

            if (secondary.IsBound)
                return secondary.Display();

            return "(unbound)";
        }

        return token.ToLowerInvariant() switch
        {
            "player_name" or "name"   => WorldState.PlayerName,
            "player_gold" or "gold"   => WorldState.Inventory.Gold.ToString("N0"),
            "player_level" or "level" => WorldState.Attributes.Current?.Level.ToString(),
            _                         => null
        };
    }
}
