namespace Chaos.Client.Definitions;

/// <summary>Which character stat or resource an info tooltip describes. Shared by the Stats window and the Equipment book.</summary>
public enum StatInfoKind
{
    Strength,
    Intelligence,
    Wisdom,
    Constitution,
    Dexterity,
    ArmorClass,
    Health,
    Mana,
    Experience,
    NextLevel,
    AbilityExperience,
    NextAbility,
    Gold,
    GamePoints,
    Level,
    Ability
}

/// <summary>
///     Hover help for the character stats, shown by the info tooltip on the Stats window (s_Str, s_EXP, ...) and the
///     Equipment book (N_STR, ...). Each entry is a short title plus a body that uses the client's &lt;green&gt;/&lt;red&gt;
///     markup, so the upside reads green and the caveat reads red.
///     EDIT THE WORDING HERE - this is the single source for both panels. The class/attribute pairings below follow the
///     usual Dark Ages conventions; tune them to this server's actual formulas.
/// </summary>
public static class StatInfo
{
    public static (string Title, string Body) Get(StatInfoKind kind)
        => kind switch
        {
            StatInfoKind.Strength => (
                "Strength",
                "<green>Increases your melee attack power</green> and how much you can carry.\n"
                + "The main attribute for Warriors and Monks.\n"
                + "<red>Does little for spellcasters.</red>\n"
                + "Raise it by spending level-up points."),

            StatInfoKind.Intelligence => (
                "Intelligence",
                "<green>Powers Wizard spells</green> - more magic damage and a larger mana pool.\n"
                + "The main attribute for Wizards.\n"
                + "Raise it by spending level-up points."),

            StatInfoKind.Wisdom => (
                "Wisdom",
                "<green>Powers Priest spells and healing</green>, and raises your maximum mana.\n"
                + "The main attribute for Priests.\n"
                + "Raise it by spending level-up points."),

            StatInfoKind.Constitution => (
                "Constitution",
                "<green>Raises your maximum health</green>, so you can take more hits.\n"
                + "Valuable to every class.\n"
                + "Raise it by spending level-up points."),

            StatInfoKind.Dexterity => (
                "Dexterity",
                "<green>Improves your accuracy and attack speed.</green>\n"
                + "The main attribute for Rogues.\n"
                + "Raise it by spending level-up points."),

            StatInfoKind.ArmorClass => (
                "Armor Class",
                "Your defense against physical attacks.\n"
                + "<green>Lower is better</green> - it makes you harder to hit.\n"
                + "Lower it by wearing better armor."),

            StatInfoKind.Health => (
                "Health (HP)",
                "Your life force. <red>Reach 0 and you die.</red>\n"
                + "<green>Regenerates over time</green>, and from food, potions and rest.\n"
                + "Raise your maximum with Constitution and better gear."),

            StatInfoKind.Mana => (
                "Mana (MP)",
                "The energy your spells cost to cast.\n"
                + "<green>Regenerates over time</green>, and from potions and rest.\n"
                + "Raise your maximum with Wisdom, Intelligence and better gear."),

            StatInfoKind.Experience => (
                "Experience",
                "How much experience you have earned in total.\n"
                + "<green>Gained by defeating monsters and finishing quests.</green>\n"
                + "Earn enough to level up and gain stat points."),

            StatInfoKind.NextLevel => (
                "To next level",
                "Experience still needed to reach your next level.\n"
                + "<green>Defeat monsters and finish quests</green> to close the gap."),

            StatInfoKind.AbilityExperience => (
                "Ability experience",
                "Experience that advances your Ability rank.\n"
                + "<green>Earned alongside regular experience</green>, and keeps you growing once leveling slows."),

            StatInfoKind.NextAbility => (
                "To next ability",
                "Ability experience still needed for your next Ability rank."),

            StatInfoKind.Gold => (
                "Gold",
                "Coin for buying from shops and paying for services.\n"
                + "<green>Looted from monsters</green> and earned by selling items.\n"
                + "<red>Drop it and others can grab it.</red>"),

            StatInfoKind.GamePoints => (
                "Game points",
                "A special currency, kept separate from your gold.\n"
                + "Spent on premium goods and services where they are accepted."),

            StatInfoKind.Level => (
                "Level",
                "Your character level.\n"
                + "<green>Each level raises your stats and opens up new gear and abilities.</green>\n"
                + "Gained by earning experience."),

            StatInfoKind.Ability => (
                "Ability",
                "Your Ability rank.\n"
                + "<green>Continues your growth</green> as you earn ability experience."),

            _ => (string.Empty, string.Empty)
        };
}
