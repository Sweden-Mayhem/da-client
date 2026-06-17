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
    Ability,
    OffenseElement,
    DefenseElement,
    MagicResistance,
    Damage,
    HitChance
}

/// <summary>
///     Hover help for the character stats, shown by the unified info tooltip on the Stats window and Equipment book.
/// </summary>
public static class StatInfo
{
    public static (string Title, string Body) Get(StatInfoKind kind)
        => kind switch
        {
            StatInfoKind.Strength => (
                "Strength",
                "Increases your maximum carry weight.\n"
                + "<green>Increases the damage of most skills.</green>"),

            StatInfoKind.Intelligence => (
                "Intelligence",
                "<green>Increases the damage of most spells.</green>"),

            StatInfoKind.Wisdom => (
                "Wisdom",
                "<green>Increases the potency of most heals.</green>\n"
                + "Increases mana gained when leveling up.\n"
                + "<green>Increases mana regeneration amount.</green>\n"
                + "<red>Does not increase mana regeneration speed.</red>"),

            StatInfoKind.Constitution => (
                "Constitution",
                "Increases the damage of most kick skills.\n"
                + "<green>Increases health gained when leveling up.</green>\n"
                + "<green>Increases health regeneration amount and speed.</green>"),

            StatInfoKind.Dexterity => (
                "Dexterity",
                "<green>Increases the damage of some skills.</green>\n"
                + "<green>Increases the chance for assails to do double damage.</green>\n"
                + "<red>Does not affect spells in any way.</red>"),

            StatInfoKind.ArmorClass => (
                "Armor Class",
                "Modifies the damage you will take from most sources by an equal percentage.\n"
                + "<green>Lower is better.</green>\n"
                + "<red>Kelb skills are unaffected.</red>"),

            StatInfoKind.Health => (
                "Health (HP)",
                "The damage you must take before dying.\n"
                + "<green>Increases the damage of crasher skills.</green>"),

            StatInfoKind.Mana => (
                "Mana (MP)",
                "Required to cast most spells.\n"
                + "<green>Increases the damage of strioch spells.</green>"),

            StatInfoKind.Experience => (
                "Experience",
                "How much experience you have gained.\n"
                + "<green>Can be used to buy health or mana.</green>\n"
                + "Required to learn some medenian skills and spells."),

            StatInfoKind.NextLevel => (
                "To next level",
                "The amount of experience you must gain to level up."),

            StatInfoKind.AbilityExperience => (
                "Ability experience",
                "How much ability experience you have gained.\n"
                + "Required to learn some medenian skills and spells."),

            StatInfoKind.NextAbility => (
                "To next ability",
                "How much ability experience you need to reach your next ability level.\n"
                + "<red>95 is the current cap.</red>"),

            StatInfoKind.Gold => (
                "Gold",
                "How much gold you are carrying.\n"
                + "The main currency of Dark Ages.\n"
                + "Used to learn some skills and spells.\n"
                + "<red>Drop it and others can pick it up.</red>"),

            StatInfoKind.GamePoints => (
                "Game points",
                "Does nothing."),

            StatInfoKind.Level => (
                "Level",
                "Your current level.\n"
                + "Some skills, spells, equipment, quests and areas may have level requirements.\n"
                + "<red>99 is the maximum level.</red>"),

            StatInfoKind.Ability => (
                "Ability",
                "Your current ability level.\n"
                + "Some skills, spells, equipment, quests and areas may have ability requirements."),

            StatInfoKind.OffenseElement => (
                "Offense element",
                "Modifies the element of your attack skills.\n"
                + "Also affects mor strioch, pian and gar spells."),

            StatInfoKind.DefenseElement => (
                "Defense element",
                "Modifies the element of your defense."),

            StatInfoKind.MagicResistance => (
                "Magic resistance",
                "<green>Increases the chance for spells to miss you.</green>\n"
                + "<red>70% is the cap.</red>"),

            StatInfoKind.Damage => (
                "Damage",
                "<green>Increases the damage your assail skills will do.</green>\n"
                + "<red>Does not affect spells in any way.</red>"),

            StatInfoKind.HitChance => (
                "Hit",
                "<green>Increases the chance for your unmaxed assails to hit.</green>\n"
                + "<red>Does not affect spells in any way.</red>"),

            _ => (string.Empty, string.Empty)
        };
}
