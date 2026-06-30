#region
using DALib.Data;
#endregion

namespace Chaos.Client.Data.Models;

/// <summary>One outline step of a quest (the journal's objective list when the quest is browsed).</summary>
public sealed record QuestStepInfo(string Label, string Description, bool Show);

/// <summary>
///     One reward OUTCOME of a quest: a labelled bundle of exp / gold / items / marks. A quest may grant several
///     (e.g. branching turn-ins), and ALL of them are shown together so the journal and the offer/turn-in window
///     never disagree about what a quest pays.
/// </summary>
public sealed record QuestRewardOutcome(string Label, int Exp, int Gold, IReadOnlyList<RewardItemInfo> Items, IReadOnlyList<RewardMarkInfo> Marks);

/// <summary>One item reward for the journal: inventory panel sprite + colour + count + name (for the hover tooltip).</summary>
public sealed record RewardItemInfo(ushort Sprite, byte Color, int Count, string Name = "");

/// <summary>One legend-mark reward for the journal: MarkIcon byte (legends.epf frame) + MarkColor byte + title.</summary>
public sealed record RewardMarkInfo(byte Icon, byte Color, string Title);

/// <summary>
///     A single parsed quest from the server's dedicated <c>SwmQuests</c> catalog metafile. Each entry carries the FULL
///     reward-outcome scan, so the quest journal and the NPC offer / turn-in window can render identical, complete
///     reward previews from this one source.
/// </summary>
public sealed record QuestMetadataEntry
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Quest kind: normal | ambient | event.</summary>
    public string Type { get; init; } = "normal";

    /// <summary>"How to start" hint for a not-started quest.</summary>
    public string StartHint { get; init; } = string.Empty;
    public string GiverNpc { get; init; } = string.Empty;
    public string EnderNpc { get; init; } = string.Empty;

    /// <summary>The journal may begin this quest directly via a Start button.</summary>
    public bool Startable { get; init; }

    /// <summary>The quest is listed when browsing (legacy single flag = VisibleWhenNotStarted).</summary>
    public bool Visible { get; init; }

    /// <summary>Listed in the browse / Suggested tab while NOT yet started (the spec's "seen in journal when not started").</summary>
    public bool VisibleWhenNotStarted { get; init; }

    /// <summary>Listed in the Completed tab after completion. Active (in-progress) always shows regardless, via the live tracker.</summary>
    public bool VisibleWhenComplete { get; init; }

    /// <summary>Repeat cooldown in MINUTES (= repeat-seconds / 60, 0 = a once-off quest), matching the journal's cadence bucketing.</summary>
    public int RepeatMinutes { get; init; }
    public int MinLevel { get; init; }
    public int MaxLevel { get; init; }

    /// <summary>Digit string of qualifying circle numbers (e.g. "123"); empty = not listed in the browse tabs.</summary>
    public string QualifyingCircles { get; init; } = string.Empty;

    /// <summary>Digit string of qualifying base-class numbers; empty = all classes.</summary>
    public string QualifyingClasses { get; init; } = string.Empty;
    public string PrerequisiteKey { get; init; } = string.Empty;

    /// <summary>May the player abandon this quest from the journal? Default true; the server emits <c>canabandon=0</c>
    ///     only for mandatory quests (choose-your-class, the tutorial events), so the journal hides the Abandon button.</summary>
    public bool CanAbandon { get; init; } = true;

    public IReadOnlyList<QuestStepInfo> Steps { get; init; } = [];
    public IReadOnlyList<QuestRewardOutcome> Outcomes { get; init; } = [];

    /// <summary>True when ANY outcome grants something (so a view knows to draw a reward section).</summary>
    public bool HasAnyReward => Outcomes.Any(static o => (o.Exp > 0) || (o.Gold > 0) || (o.Items.Count > 0) || (o.Marks.Count > 0));

    //control chars the server uses to pack the steps / outcomes blocks (kept private to the parser)
    private const char FS = ''; //field separator: between fields of one item / mark record
    private const char GS = ''; //group separator: between item / mark records
    private const char RS = ''; //record separator: between steps, and between outcomes
    private const char US = ''; //unit separator: between fields of one step / outcome record

    /// <summary>Parses every quest entry from one or more <c>SwmQuests</c> metafiles.</summary>
    public static IReadOnlyList<QuestMetadataEntry> ParseAll(IEnumerable<MetaFile> metaFiles)
    {
        var quests = new List<QuestMetadataEntry>();

        foreach (var metaFile in metaFiles)
            foreach (var entry in metaFile)
            {
                //each property is "name=value", split on the FIRST '='; unknown names are simply ignored
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var prop in entry.Properties)
                {
                    var eq = prop.IndexOf('=');

                    if (eq < 0)
                        continue;

                    props[prop[..eq]] = prop[(eq + 1)..];
                }

                quests.Add(
                    new QuestMetadataEntry
                    {
                        Key = entry.Key,
                        Title = Str(props, "title"),
                        Description = Str(props, "desc"),
                        Type = props.TryGetValue("type", out var type) && !string.IsNullOrEmpty(type) ? type : "normal",
                        StartHint = Str(props, "hint"),
                        GiverNpc = Str(props, "giver"),
                        EnderNpc = Str(props, "ender"),
                        Startable = Flag(props, "startable"),
                        Visible = Flag(props, "visible"),
                        VisibleWhenNotStarted = FlagOr(props, "visstart", "visible"),
                        VisibleWhenComplete = FlagOr(props, "viscomplete", "visible"),
                        RepeatMinutes = Int(props, "repeat") / 60,
                        MinLevel = Int(props, "minlvl"),
                        MaxLevel = Int(props, "maxlvl"),
                        QualifyingCircles = Str(props, "circles"),
                        QualifyingClasses = Str(props, "classes"),
                        PrerequisiteKey = Str(props, "prereq"),
                        CanAbandon = FlagDefaultTrue(props, "canabandon"),
                        Steps = ParseSteps(Str(props, "steps")),
                        Outcomes = ParseOutcomes(Str(props, "outcomes"))
                    });
            }

        return quests;
    }

    private static string Str(IReadOnlyDictionary<string, string> props, string name) => props.TryGetValue(name, out var v) ? v : string.Empty;

    private static bool Flag(IReadOnlyDictionary<string, string> props, string name) => props.TryGetValue(name, out var v) && (v == "1");

    //prefer `name`, falling back to `legacy` when the server hasn't emitted the newer prop
    private static bool FlagOr(IReadOnlyDictionary<string, string> props, string name, string legacy)
        => props.TryGetValue(name, out var v) ? v == "1" : Flag(props, legacy);

    //a flag that DEFAULTS TRUE: true unless the server explicitly emitted it as "0" (so an old/absent prop stays true)
    private static bool FlagDefaultTrue(IReadOnlyDictionary<string, string> props, string name)
        => !props.TryGetValue(name, out var v) || v != "0";

    private static int Int(IReadOnlyDictionary<string, string> props, string name)
        => props.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : 0;

    //steps = RS-joined records; each record US-joined: label, longdesc, show(1/0)
    private static IReadOnlyList<QuestStepInfo> ParseSteps(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var steps = new List<QuestStepInfo>();

        foreach (var record in raw.Split(RS))
        {
            if (record.Length == 0)
                continue;

            var f = record.Split(US);
            steps.Add(new QuestStepInfo(f.Length > 0 ? f[0] : string.Empty, f.Length > 1 ? f[1] : string.Empty, (f.Length > 2) && (f[2] == "1")));
        }

        return steps;
    }

    //outcomes = RS-joined records; each record US-joined: label, exp, gold, itemsBlock, marksBlock
    private static IReadOnlyList<QuestRewardOutcome> ParseOutcomes(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var outcomes = new List<QuestRewardOutcome>();

        foreach (var record in raw.Split(RS))
        {
            if (record.Length == 0)
                continue;

            var f = record.Split(US);
            var label = f.Length > 0 ? f[0] : string.Empty;
            var exp = (f.Length > 1) && int.TryParse(f[1], out var e) ? e : 0;
            var gold = (f.Length > 2) && int.TryParse(f[2], out var g) ? g : 0;
            var items = ParseItems(f.Length > 3 ? f[3] : string.Empty);
            var marks = ParseMarks(f.Length > 4 ? f[4] : string.Empty);
            outcomes.Add(new QuestRewardOutcome(label, exp, gold, items, marks));
        }

        return outcomes;
    }

    //itemsBlock = GS-joined records; each record FS-joined: sprite, color, count, name
    private static IReadOnlyList<RewardItemInfo> ParseItems(string block)
    {
        if (string.IsNullOrEmpty(block))
            return [];

        var items = new List<RewardItemInfo>();

        foreach (var record in block.Split(GS))
        {
            if (record.Length == 0)
                continue;

            var f = record.Split(FS);

            if ((f.Length >= 3) && ushort.TryParse(f[0], out var sprite) && byte.TryParse(f[1], out var color) && int.TryParse(f[2], out var count))
                items.Add(new RewardItemInfo(sprite, color, count, f.Length >= 4 ? f[3] : string.Empty));
        }

        return items;
    }

    //marksBlock = GS-joined records; each record FS-joined: icon, color, title
    private static IReadOnlyList<RewardMarkInfo> ParseMarks(string block)
    {
        if (string.IsNullOrEmpty(block))
            return [];

        var marks = new List<RewardMarkInfo>();

        foreach (var record in block.Split(GS))
        {
            if (record.Length == 0)
                continue;

            var f = record.Split(FS);

            if ((f.Length >= 3) && byte.TryParse(f[0], out var icon) && byte.TryParse(f[1], out var color))
                marks.Add(new RewardMarkInfo(icon, color, f[2]));
        }

        return marks;
    }
}
