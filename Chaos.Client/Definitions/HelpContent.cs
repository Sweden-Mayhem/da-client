namespace Chaos.Client.Definitions;

/// <summary>
///     In-game help article text shown to players.
/// </summary>
public static class HelpContent
{
    public static readonly (string Title, string Body)[] Articles =
    [
        ("Controls",
            "Movement\n" +
            "WASD or arrow keys to walk. Hold the key to keep moving.\n" +
            "Shift + direction turns in place without walking.\n\n" +
            "Combat\n" +
            "Left-click a creature to move toward it and attack. Hold Space to swing in place (useful with WASD).\n" +
            "Your character keeps swinging as long as Space is held and you are still.\n\n" +
            "Interact\n" +
            "E or F: pick up items, talk to NPCs, open doors and signs.\n" +
            "Left-click also interacts with the nearest thing on the tile.\n" +
            "Right-click anywhere to walk there.\n\n" +
            "Hotbars\n" +
            "1-= use skill slots. Shift+1-= use spell slots. F1-F12 use item slots.\n\n" +
            "Other keys\n" +
            "I = Inventory, K = Skills, P = Spells, C = Stats, U = Equipment.\n" +
            "T = Town map, M = World map, Tab = Mini-map.\n" +
            "G = Group, O = Friends, R = Mail and Help (this screen).\n" +
            "Q = Emotes, Ctrl+Q = Log out.\n" +
            "Options (Shift+O) lets you rebind everything except Enter and Escape."
        ),

        ("Character Classes",
            "Warrior\n" +
            "High strength and endurance. Best melee damage and HP pool.\n" +
            "Recommended for beginners. Learn Assail early and focus STR and CON.\n\n" +
            "Rogue\n" +
            "High dexterity, quick strikes, and stealth abilities.\n" +
            "Good solo damage but lower HP. Focus DEX and STR.\n\n" +
            "Monk\n" +
            "Balanced fighter with unique unarmed and ki skills.\n" +
            "Requires patience to master. Focus STR and WIS.\n\n" +
            "Wizard\n" +
            "Highest magical damage. Learns powerful offensive spells.\n" +
            "Fragile, needs a group. Focus INT and WIS.\n\n" +
            "Priest\n" +
            "Healing and support spells. Essential in groups.\n" +
            "Can also deal decent damage. Focus WIS and INT.\n\n" +
            "You choose your class after reaching a certain experience level.\n" +
            "Talk to the class master NPCs in Mileth."
        ),

        ("Stats and Attributes",
            "Primary Stats\n" +
            "STR - Melee damage, carry weight. Main stat for Warriors and Monks.\n" +
            "INT - Spell power and mana. Main stat for Wizards.\n" +
            "WIS - Healing power and mana. Main stat for Priests.\n" +
            "CON - Maximum HP and resilience.\n" +
            "DEX - Hit rate, dodge, and attack speed. Main stat for Rogues.\n\n" +
            "Secondary\n" +
            "AC (Armor Class) - Lower is better. Reduces physical damage taken.\n" +
            "Hit - Increases chance to hit enemies.\n" +
            "Dmg - Flat bonus to physical damage.\n" +
            "MR (Magic Resistance) - Reduces magical damage taken.\n\n" +
            "Level Up\n" +
            "Each level grants unspent stat points shown by the arrow buttons next to\n" +
            "your stats (C key, or Menu > Stats). Click an arrow to raise that stat.\n" +
            "You can also raise stats from the Equipment book (U key, Intro tab)."
        ),

        ("Skills and Spells",
            "Learning\n" +
            "Skills are learned from skill masters in towns or from NPCs in the world.\n" +
            "You must meet the minimum stats shown in the skill tooltip to learn it.\n" +
            "Once learned, you can always use it regardless of your current stats.\n\n" +
            "Using\n" +
            "Skills go on the skill hotbar (K opens the book, drag slots to the bar).\n" +
            "Spells go on the spell hotbar (P opens the book).\n" +
            "Hotbar keys: 1-= for skills, Shift+1-= for spells.\n\n" +
            "Targeting\n" +
            "Skills that need a target show a crosshair cursor. Click the target tile.\n" +
            "Some spells cast instantly; others require selecting a target.\n\n" +
            "Hover a skill or spell slot to see its full description and requirements."
        ),

        ("Combat",
            "Basic\n" +
            "Move toward an enemy to engage. Left-click attacks automatically.\n" +
            "Hold Space to keep swinging while standing still.\n" +
            "Your character shows a red cursor ring on your current attack target.\n\n" +
            "Healing\n" +
            "Potions can be used from inventory slots (F1-F12 hotkeys).\n" +
            "Priests can cast healing spells on themselves and allies.\n" +
            "Resting at an inn or sitting restores HP and MP over time.\n\n" +
            "Death\n" +
            "If your HP reaches 0 you die. A Priest can resurrect you.\n" +
            "You can also revive at the nearest priest NPC.\n\n" +
            "Experience\n" +
            "Defeating enemies grants EXP shown in the chat log.\n" +
            "Level up when the EXP bar fills. Unspent stat points appear in the Stats window."
        ),

        ("Traveling the World",
            "Town Map (T)\n" +
            "Press T for a large overview of the current town area with warp destinations.\n" +
            "Click a location to jump there instantly.\n\n" +
            "World Map (M)\n" +
            "Press M for the full world map. Select a destination and press Travel.\n" +
            "You must be in a warp zone to travel between regions.\n\n" +
            "Mini-map (Tab)\n" +
            "Tab shows a small fog-of-war map of the current area.\n" +
            "PgUp / PgDn zooms it in and out.\n\n" +
            "Signs\n" +
            "Walk up to a sign and press E (or left-click it) to read it.\n" +
            "Signs appear as brown or grey boards on walls in towns.\n\n" +
            "Portals\n" +
            "Step on glowing floor tiles to warp to another area.\n" +
            "Most dungeons have portals at the entrance."
        ),

        ("Groups and Chat",
            "Groups\n" +
            "Press G to open the Group window. Use it to invite and manage party members.\n" +
            "Being in a group shares EXP and makes dungeons safer.\n" +
            "Press J to flash your group members on the screen.\n\n" +
            "Chat\n" +
            "Press Enter to open the chat bar. Type and press Enter again to send.\n" +
            "/say (default) - area chat\n" +
            "/shout - zone-wide shout\n" +
            "/whisper Name - private message to another player\n" +
            "Up/Down arrows recall previously sent messages.\n\n" +
            "Mail\n" +
            "Press R to open Mail and Help (this screen). Your inbox is on the server.\n" +
            "You can send mail to any player even when they are offline."
        ),

        ("Items and Equipment",
            "Inventory\n" +
            "Press I to open your inventory. Hover items to see their stats.\n" +
            "Drag items to hotbar slots (F1-F12) for quick use.\n\n" +
            "Equipment\n" +
            "Press U to open the Equipment book. Drag items from inventory onto slots\n" +
            "to equip them. Drag equipped items off the book to unequip.\n" +
            "You can also drop an item anywhere on the book to auto-equip it.\n\n" +
            "Buying and Selling\n" +
            "Talk to merchant NPCs in towns to buy and sell items.\n" +
            "Grey items in a merchant's buy list are things they want but you do not have.\n\n" +
            "Gold\n" +
            "Pick up gold stacks by pressing E near them or left-clicking.\n" +
            "Your gold total shows in the inventory window.\n\n" +
            "Weight\n" +
            "Items have weight. Your STR stat determines how much you can carry."
        ),
    ];
}
