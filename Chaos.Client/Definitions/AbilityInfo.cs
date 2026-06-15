#region
using Chaos.Client.Collections;
using Chaos.Client.Data.Models;
using Chaos.Client.ViewModel;
using Chaos.Extensions.Common;
using Chaos.Networking.Entities.Server;
#endregion

namespace Chaos.Client.Definitions;

/// <summary>
///     Formats a skill/spell's metadata into a hover tooltip (title + category + body). Layout: name, a "Lv N Skill" /
///     "Lv N Spell" category line, the description, then a "To Learn:" block with the stat minimums (and any prerequisite
///     abilities). Those stat values are the ability's LEARNING requirements (the server template's
///     <c>learningRequirements</c> - what you needed to learn it, NOT to use it; a known ability is always usable), so
///     they read &lt;green&gt; when the player meets them and &lt;red&gt; when not. Used by the skill/spell hotbar and K/P
///     book hover tooltips.
/// </summary>
public static class AbilityInfo
{
    /// <param name="entry">
    ///     The metafile entry to format.
    /// </param>
    /// <param name="liveCastLines">
    ///     The cast lines from the player's actual spell slot when the hovered spell is LEARNED - authoritative over
    ///     the metafile value (custom grants can differ, and the metafile can be a sync behind).
    /// </param>
    /// <param name="activeCooldownSecs">
    ///     Seconds of cooldown CURRENTLY remaining on the player's slot (live), or 0/null when ready. When &gt; 0 a
    ///     prominent "On cooldown" line is shown so the player sees the wait at a glance.
    /// </param>
    public static (string Title, string Category, string Body) Build(AbilityMetadataEntry entry, byte? liveCastLines = null, float? activeCooldownSecs = null)
    {
        var attrs = WorldState.Attributes.Current;
        var typeWord = entry.IsSpell ? "Spell" : "Skill";

        //category line, item-tooltip style: the primary gate + the type, e.g. "Lv 1 Skill" / "Ability 50 Spell" / "Master Skill"
        var category = entry.RequiresMaster ? $"Master {typeWord}"
            : entry.AbilityLevel > 0 ? $"Ability {entry.AbilityLevel} {typeWord}"
            : entry.Level > 0 ? $"Lv {entry.Level} {typeWord}"
            : typeWord;

        //learn requirements: the stat minimums + any prerequisite abilities (the level/gate is already in the category)
        var reqLines = new List<string>();

        var stats = StatText(entry, attrs);

        if (stats.Length > 0)
            reqLines.Add(stats);

        AddPreReq(reqLines, entry.PreReq1Name, entry.PreReq1Level);
        AddPreReq(reqLines, entry.PreReq2Name, entry.PreReq2Level);

        //body: description first, then the cast/cooldown facts, then a "To Learn:" block, blank-line separated
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(entry.Description))
            sections.Add(entry.Description.Trim());

        var facts = new List<string>();

        if (entry.IsSpell)
        {
            var lines = liveCastLines ?? entry.CastLines;
            facts.Add($"<grey>Cast lines:</grey> {(lines > 0 ? lines.ToString() : "instant")}");
        }

        if (entry.CooldownSecs > 0)
            facts.Add($"<grey>Cooldown:</grey> {FormatCooldown(entry.CooldownSecs)}");

        //live remaining cooldown (counts down while hovered) - shown in orange so it stands out from the static facts
        if (activeCooldownSecs is > 0.001f)
            facts.Add($"<orange>On cooldown: {FormatCooldownRemaining(activeCooldownSecs.Value)}</orange>");

        if (facts.Count > 0)
            sections.Add(string.Join("\n", facts));

        if (reqLines.Count > 0)
            sections.Add("<grey>To Learn:</grey>\n" + string.Join("\n", reqLines));

        return (entry.Name, category, string.Join("\n\n", sections));
    }

    //live remaining cooldown: seconds (one decimal) at >= 1s, milliseconds as "0.xx" under 1s, then minutes for long ones
    public static string FormatCooldownRemaining(float seconds)
    {
        if (seconds <= 0f)
            return "ready";

        if (seconds < 1f)
            return $"{seconds:0.00}s";

        if (seconds < 60f)
            return $"{seconds:0.0}s";

        var mins = (int)(seconds / 60f);
        var rest = (int)(seconds % 60f);

        return rest > 0 ? $"{mins}m {rest}s" : $"{mins}m";
    }

    private static string FormatCooldown(int seconds)
    {
        if (seconds < 60)
            return $"{seconds}s";

        var minutes = seconds / 60;
        var rest = seconds % 60;

        return rest > 0 ? $"{minutes}m {rest}s" : $"{minutes}m";
    }

    private static string StatText(AbilityMetadataEntry entry, AttributesArgs? attrs)
    {
        var parts = new List<string>();

        Add(parts, "Str", entry.Str, attrs?.Str);
        Add(parts, "Int", entry.Int, attrs?.Int);
        Add(parts, "Wis", entry.Wis, attrs?.Wis);
        Add(parts, "Dex", entry.Dex, attrs?.Dex);
        Add(parts, "Con", entry.Con, attrs?.Con);

        return parts.Count > 0 ? string.Join(" ", parts) : string.Empty;

        static void Add(List<string> into, string label, byte required, int? current)
        {
            if (required > 0)
                into.Add(Col((current ?? 0) >= required, $"{label} {required}"));
        }
    }

    private static void AddPreReq(List<string> lines, string? name, byte level)
    {
        if (string.IsNullOrEmpty(name))
            return;

        lines.Add("Needs " + Col(HasPreRequisite(name, level), $"{name} {level}"));
    }

    //true once the player owns the named ability at the required level (mirrors the details popup's check)
    private static bool HasPreRequisite(string name, byte requiredLevel)
    {
        for (byte i = 1; i <= SpellBook.MAX_SLOTS; i++)
        {
            ref readonly var slot = ref WorldState.SpellBook.GetSlot(i);

            if (slot.IsOccupied && (slot.AbilityName?.EqualsI(name) == true) && (slot.CurrentLevel >= requiredLevel))
                return true;
        }

        for (byte i = 1; i <= SkillBook.MAX_SLOTS; i++)
        {
            ref readonly var slot = ref WorldState.SkillBook.GetSlot(i);

            if (slot.IsOccupied && (slot.AbilityName?.EqualsI(name) == true) && (slot.CurrentLevel >= requiredLevel))
                return true;
        }

        return false;
    }

    //wraps text in the color the rich tooltip understands: green when the requirement is met, red when it is not
    private static string Col(bool met, string text) => met ? $"<green>{text}</green>" : $"<red>{text}</red>";
}
