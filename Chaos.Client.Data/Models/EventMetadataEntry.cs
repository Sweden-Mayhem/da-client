#region
using DALib.Data;
#endregion

namespace Chaos.Client.Data.Models;

/// <summary>
///     A single parsed event/quest from an SEvent metadata file.
/// </summary>
public sealed record EventMetadataEntry
{
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     The page this event belongs to (1-based, corresponds to SEvent file number / circle level).
    /// </summary>
    public int Page { get; init; } = 1;

    public string PreRequisiteId { get; init; } = string.Empty;

    /// <summary>
    ///     Digit string of qualifying circle numbers (e.g. "1234567"). Each char is a LevelCircle int value.
    /// </summary>
    public string QualifyingCircles { get; init; } = string.Empty;

    /// <summary>
    ///     Digit string of qualifying class numbers (e.g. "012345"). Each char is a BaseClass int value.
    /// </summary>
    public string QualifyingClasses { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;
    public string Reward { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;
    public required string Title { get; init; }

    /// <summary>
    ///     SWM extension: the quest's repeat cooldown in MINUTES (0 = a once-off quest). Parsed from the custom
    ///     "{page}_repeat" sub-node the server emits for repeatable quests; used to sort them into Daily/Weekly/...
    /// </summary>
    public int RepeatMinutes { get; init; }

    /// <summary>SWM extension: a "how to start" hint for a not-started quest (custom "{page}_starthint" sub-node).</summary>
    public string StartHint { get; init; } = string.Empty;

    /// <summary>SWM extension: whether the journal may start this quest directly (custom "{page}_startable" sub-node).</summary>
    public bool Startable { get; init; }

    /// <summary>SWM extension: the real quest key (custom "{page}_questkey" sub-node), used to name a "start quest" request.</summary>
    public string QuestKey { get; init; } = string.Empty;

    /// <summary>SWM extension: structured rewards for the journal's VISUAL display (custom "{page}_swmrwd" sub-node).</summary>
    public int RewardExp { get; init; }
    public int RewardGold { get; init; }
    public IReadOnlyList<RewardItemInfo> RewardItems { get; init; } = [];
    public IReadOnlyList<RewardMarkInfo> RewardMarks { get; init; } = [];

    /// <summary>True when this entry carries any structured reward (so the journal draws icons instead of text).</summary>
    public bool HasRewardData => (RewardExp > 0) || (RewardGold > 0) || (RewardItems.Count > 0) || (RewardMarks.Count > 0);

    /// <summary>SWM extension: the quest's start level bounds (custom "{page}_swmlvl" sub-node); 0 = no bound.</summary>
    public int StartMinLevel { get; init; }
    public int StartMaxLevel { get; init; }

    /// <summary>
    ///     Parses all event entries from one or more SEvent MetaFiles.
    /// </summary>
    /// <remarks>
    ///     Each event is encoded as 9 sequential sub-nodes: {page}_start, _title, _id, _qual, _sum, _result, _sub, _reward,
    ///     _end.
    /// </remarks>
    public static IReadOnlyList<EventMetadataEntry> ParseAll(IEnumerable<MetaFile> metaFiles)
    {
        var events = new List<EventMetadataEntry>();

        foreach (var metaFile in metaFiles)
        {
            var currentPage = 1;
            string? currentTitle = null;
            string? currentId = null;
            string? qualCircles = null;
            string? qualClasses = null;
            string? summary = null;
            string? result = null;
            string? preReqId = null;
            string? reward = null;
            var repeatMinutes = 0;
            string? startHint = null;
            var startable = false;
            string? questKey = null;
            var rewardExp = 0;
            var rewardGold = 0;
            List<RewardItemInfo> rewardItems = [];
            List<RewardMarkInfo> rewardMarks = [];
            var startMinLevel = 0;
            var startMaxLevel = 0;

            foreach (var entry in metaFile)
            {
                var key = entry.Key;

                if (key.EndsWith("_start", StringComparison.Ordinal))
                {
                    //extract page from key prefix (e.g. "01_start" → page 1)
                    var underscoreIndex = key.IndexOf('_');

                    if ((underscoreIndex > 0)
                        && int.TryParse(
                            key[..underscoreIndex]
                                .Trim(),
                            out var page))
                        currentPage = page;

                    currentTitle = null;
                    currentId = null;
                    qualCircles = null;
                    qualClasses = null;
                    summary = null;
                    result = null;
                    preReqId = null;
                    reward = null;
                    repeatMinutes = 0;
                    startHint = null;
                    startable = false;
                    questKey = null;
                    rewardExp = 0;
                    rewardGold = 0;
                    rewardItems = [];
                    rewardMarks = [];
                    startMinLevel = 0;
                    startMaxLevel = 0;
                } else if (key.EndsWith("_title", StringComparison.Ordinal))
                    currentTitle = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_id", StringComparison.Ordinal))
                    currentId = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_qual", StringComparison.Ordinal))
                {
                    qualCircles = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                    qualClasses = entry.Properties.Count > 1 ? entry.Properties[1] : string.Empty;
                } else if (key.EndsWith("_sum", StringComparison.Ordinal))
                    summary = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_result", StringComparison.Ordinal))
                    result = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_sub", StringComparison.Ordinal))
                    preReqId = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_reward", StringComparison.Ordinal))
                    reward = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_repeat", StringComparison.Ordinal))
                    repeatMinutes = (entry.Properties.Count > 0) && int.TryParse(entry.Properties[0], out var rm) ? rm : 0;
                else if (key.EndsWith("_starthint", StringComparison.Ordinal))
                    startHint = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_startable", StringComparison.Ordinal))
                    startable = (entry.Properties.Count > 0) && (entry.Properties[0] == "1");
                else if (key.EndsWith("_questkey", StringComparison.Ordinal))
                    questKey = entry.Properties.Count > 0 ? entry.Properties[0] : string.Empty;
                else if (key.EndsWith("_swmrwd", StringComparison.Ordinal))
                {
                    if (entry.Properties.Count > 0)
                        ParseRewardData(entry.Properties[0], out rewardExp, out rewardGold, rewardItems, rewardMarks);
                } else if (key.EndsWith("_swmlvl", StringComparison.Ordinal))
                {
                    if (entry.Properties.Count > 0)
                    {
                        var lv = entry.Properties[0].Split(',');

                        if (lv.Length > 0)
                            int.TryParse(lv[0], out startMinLevel);

                        if (lv.Length > 1)
                            int.TryParse(lv[1], out startMaxLevel);
                    }
                } else if (key.EndsWith("_end", StringComparison.Ordinal) && currentTitle is not null)
                    events.Add(
                        new EventMetadataEntry
                        {
                            Title = currentTitle,
                            Id = currentId ?? string.Empty,
                            Page = currentPage,
                            QualifyingCircles = qualCircles ?? string.Empty,
                            QualifyingClasses = qualClasses ?? string.Empty,
                            Summary = summary ?? string.Empty,
                            Result = result ?? string.Empty,
                            PreRequisiteId = preReqId ?? string.Empty,
                            Reward = reward ?? string.Empty,
                            RepeatMinutes = repeatMinutes,
                            StartHint = startHint ?? string.Empty,
                            Startable = startable,
                            QuestKey = questKey ?? string.Empty,
                            RewardExp = rewardExp,
                            RewardGold = rewardGold,
                            RewardItems = rewardItems,
                            RewardMarks = rewardMarks,
                            StartMinLevel = startMinLevel,
                            StartMaxLevel = startMaxLevel
                        });
            }
        }

        return events;
    }

    //parses the "{page}_swmrwd" delimited reward string: "exp|gold|sprite,color,count~...|icon,color,title~..."
    private static void ParseRewardData(string data, out int exp, out int gold, List<RewardItemInfo> items, List<RewardMarkInfo> marks)
    {
        exp = 0;
        gold = 0;
        items.Clear();
        marks.Clear();

        var parts = data.Split('|');

        if (parts.Length > 0)
            int.TryParse(parts[0], out exp);

        if (parts.Length > 1)
            int.TryParse(parts[1], out gold);

        if ((parts.Length > 2) && (parts[2].Length > 0))
            foreach (var raw in parts[2].Split('~'))
            {
                var f = raw.Split(',');

                if ((f.Length >= 3) && ushort.TryParse(f[0], out var sprite) && byte.TryParse(f[1], out var color) && int.TryParse(f[2], out var count))
                    items.Add(new RewardItemInfo(sprite, color, count, f.Length >= 4 ? f[3] : string.Empty));
            }

        if ((parts.Length > 3) && (parts[3].Length > 0))
            foreach (var raw in parts[3].Split('~'))
            {
                var f = raw.Split(',');

                if ((f.Length >= 3) && byte.TryParse(f[0], out var icon) && byte.TryParse(f[1], out var color))
                    marks.Add(new RewardMarkInfo(icon, color, f[2]));
            }
    }
}

/// <summary>One item reward for the journal: inventory panel sprite + colour + count + name (for the hover tooltip).</summary>
public sealed record RewardItemInfo(ushort Sprite, byte Color, int Count, string Name = "");

/// <summary>One legend-mark reward for the journal: MarkIcon byte (legends.epf frame) + MarkColor byte + title.</summary>
public sealed record RewardMarkInfo(byte Icon, byte Color, string Title);