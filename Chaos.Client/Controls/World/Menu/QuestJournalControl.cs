#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Data.Models;
using Chaos.Client.Definitions;
using Chaos.Client.Networking;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     The Quest Journal - a modern MMO quest log, styled like the market window: a prominent tab bar
///     (Active / Suggested / Repeatable / Completed), a scrollable list on the left with foldout categories, and a
///     spacious detail pane on the right. Active quests come from the live tracker push (full description + live
///     objectives); the guide tabs come from the SEvent metadata + legend-mark completion the client already holds.
///     Suggested is grouped by circle (so it respects the player's level); Repeatable is grouped by cadence
///     (Daily / Weekly / ...), detected from the quest title suffix. Per-quest "untrack" is still TODO.
/// </summary>
public sealed class QuestJournalControl : DraggableWindow
{
    //sizes match the market window (Temuair Exchange) so the two windows feel identical
    private const int WIN_W = 900;
    private const int WIN_H = 580;
    private const int TABBAR_H = 34;
    private const int TAB_FONT = 16;
    private const int PAD = 8;
    private const int ROW_H = 36;
    private const int SIDEBAR_W = 304;
    private const int FONT = 14;
    private const int HEADER_FONT = 14;
    private const int NAME_FONT = 17;
    private const int BODY_FONT = 14;

    private static readonly Color TabIdle = new(34, 30, 24);
    private static readonly Color TabSelected = new(70, 60, 40);
    private static readonly Color TabHover = new(54, 48, 36);
    //selection/hover colours match the market window's tree exactly so the two feel identical
    private static readonly Color RowSelected = new(54, 45, 28);
    private static readonly Color RowHover = new(34, 29, 19);
    private static readonly Color TextSel = new(255, 224, 138); //selected row text (gold), like the market
    private static readonly Color Edge = new(88, 72, 46);
    private static readonly Color Divider = new(60, 50, 34);     //matches the market window's divider colour

    private static readonly Color HeaderColor = new(224, 192, 108); //gold foldout/section headers, matches the market
    private static readonly Color ActiveColor = new(255, 232, 150);
    private static readonly Color AvailableColor = new(214, 206, 186);
    private static readonly Color LockedColor = new(142, 136, 126);
    private static readonly Color CompletedColor = new(140, 172, 140);
    private static readonly Color BodyColor = new(214, 206, 186);
    private static readonly Color DimColor = new(172, 164, 150);
    private static readonly Color RewardColor = new(192, 212, 152);
    private static readonly Color ReqMetColor = new(120, 200, 110);   //requirement met (green)
    private static readonly Color ReqUnmetColor = new(208, 96, 88);   //requirement not met (red)

    private enum Tab { Active, Suggested, Repeatable, Completed }

    //title suffixes that mark a quest as repeatable + the category they map to (checked case-insensitively)
    private sealed record StepLine(string Label, string Detail, string Description, int State); //0 pending,1 current,2 done

    private sealed class Entry
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required Tab Tab { get; init; }
        public string Category { get; init; } = string.Empty; //foldout group ("" = ungrouped/flat)
        public int Sort { get; init; }                         //sort key within a category (e.g. circle)
        public required Color Color { get; init; }
        public string Description { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public Tab StatusKind { get; init; }
        public string StartHint { get; init; } = string.Empty; //"how to start" hint (Suggested/Repeatable)
        public bool ShowStart { get; init; }                    //whether to render the "How to start" block
        public bool Startable { get; init; }                    //a Start button may directly begin this quest
        public string QuestKey { get; init; } = string.Empty;   //the real quest key (active quests; for track/start)
        public string Counter { get; init; } = string.Empty;    //optional lifetime counter line ("Label: value")

        //structured reward outcomes from the catalog; the detail draws them via the shared QuestDetailView (so the
        //journal + the offer/turn-in window render identical reward previews from this one source)
        public IReadOnlyList<QuestRewardOutcome> Outcomes { get; init; } = [];
        public int StartMinLevel { get; init; }
        public int StartMaxLevel { get; init; }

        public List<StepLine> Steps { get; init; } = [];

        //the catalog's objective OUTLINE (the Suggested/Repeatable preview "Objectives" list). Distinct from Steps
        //above, which carry per-checkpoint STATE for an ACTIVE quest.
        public IReadOnlyList<QuestStepInfo> OutlineSteps { get; init; } = [];
    }

    //--- tabs ---
    private readonly Dictionary<Tab, TabButton> TabButtons = new();
    private Tab Current = Tab.Active;

    //--- panes ---
    private readonly UITextBox Search;
    private readonly UILabel SearchPlaceholder;
    private readonly MenuButton SearchClear;
    private readonly ScrollRegion Sidebar;
    private readonly ScrollRegion Detail;
    private string AppliedSearch = string.Empty;

    /// <summary>The reward item the cursor is over (its name), so the world tooltip resolver can show its details.</summary>
    public string? HoveredRewardName { get; private set; }

    //--- data ---
    private List<QuestTrackerQuestInfo> ActiveQuests = [];
    private IReadOnlyList<QuestMetadataEntry> Guide = [];
    private HashSet<string> CompletedIds = new(StringComparer.OrdinalIgnoreCase);
    private BaseClass PlayerClass;
    private bool MasterQuests;

    private readonly List<Entry> Entries = [];
    private string? SelectedKey;

    /// <summary>Raised when the player toggles tracking on an active quest: (questKey, nowTracked). WorldScreen
    ///     re-applies the corner-tracker filter and announces a re-tracked quest.</summary>
    public Action<string, bool>? OnTrackToggled;

    /// <summary>Raised when the player clicks Start on a startable quest (questKey). WorldScreen sends the request.</summary>
    public Action<string>? OnStartQuest;

    /// <summary>Raised when the player clicks Abandon on an active quest (questKey). WorldScreen sends the request.</summary>
    public Action<string>? OnAbandonQuest;

    public QuestJournalControl()
        : base("Quest Journal", WIN_W, WIN_H, useWoodFrame: true)
    {
        var cw = Content.Width;
        var ch = Content.Height;

        //tab bar across the top
        var tabs = new[] { (Tab.Active, "Active"), (Tab.Suggested, "Suggested"), (Tab.Repeatable, "Repeatable"), (Tab.Completed, "Completed") };
        var tabW = cw / tabs.Length;
        var tx = 0;

        foreach (var (tab, label) in tabs)
        {
            var btn = new TabButton(label, tabW - 3, TABBAR_H) { X = tx, Y = 0 };
            var captured = tab;
            btn.Clicked = () => SwitchTab(captured);
            TabButtons[tab] = btn;
            Content.AddChild(btn);
            tx += tabW;
        }

        Content.AddChild(new UIPanel { X = 0, Y = TABBAR_H + 2, Width = cw, Height = 1, BackgroundColor = Divider, IsPassThrough = true });

        var bodyY = TABBAR_H + PAD;
        var bodyH = ch - bodyY - PAD;

        //search box above the list (filters the current tab by name) - mirrors the market: a placeholder hint when
        //empty + a clear "x" button, no permanent prefix
        const int searchH = 24;
        const int clearW = 24;
        var searchAreaH = searchH + 6;
        Search = new UITextBox
        {
            X = PAD, Y = bodyY, Width = SIDEBAR_W - clearW - 2, Height = searchH, MaxLength = 40, CustomFontSize = FONT,
            BackgroundColor = new Color(26, 23, 17), BorderColor = Edge, FocusedBackgroundColor = new Color(34, 29, 20)
        };
        Content.AddChild(Search);

        SearchPlaceholder = new UILabel
        {
            X = PAD + 6, Y = bodyY, Width = SIDEBAR_W - clearW - 12, Height = searchH, CustomFontSize = FONT,
            ForegroundColor = new Color(120, 112, 96), VerticalAlignment = VerticalAlignment.Center,
            Text = "Search quests...", IsHitTestVisible = false
        };
        Content.AddChild(SearchPlaceholder);

        SearchClear = new MenuButton("x", clearW, searchH) { X = PAD + SIDEBAR_W - clearW, Y = bodyY, SuppressClickSound = true, Visible = false };
        SearchClear.Clicked = _ => Search.Text = string.Empty;
        Content.AddChild(SearchClear);

        Sidebar = new ScrollRegion(SIDEBAR_W, bodyH - searchAreaH) { X = PAD, Y = bodyY + searchAreaH };
        Content.AddChild(Sidebar);

        Content.AddChild(new UIPanel { X = PAD + SIDEBAR_W + PAD, Y = bodyY, Width = 1, Height = bodyH, BackgroundColor = Divider, IsPassThrough = true });

        var detailX = PAD + SIDEBAR_W + PAD * 2 + 1;
        Detail = new ScrollRegion(cw - detailX - PAD, bodyH) { X = detailX, Y = bodyY };
        Content.AddChild(Detail);
    }

    public void SetActiveQuests(IReadOnlyList<QuestTrackerQuestInfo> quests)
    {
        ActiveQuests = [..quests];

        if (Visible)
            Rebuild();
    }

    //quests startable RIGHT NOW (a Start button) vs. on cooldown (a live countdown, seconds decremented locally).
    private HashSet<string> StartableNow = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> Cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private double CountdownRefresh;

    public void SetStartStatuses(IReadOnlyList<QuestStartStatusInfo> statuses)
    {
        var nowSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in statuses)
            if (s.CooldownSeconds <= 0)
                nowSet.Add(s.Key);
            else
                cds[s.Key] = s.CooldownSeconds;

        var changed = !nowSet.SetEquals(StartableNow) || (cds.Count != Cooldowns.Count) || cds.Keys.Any(k => !Cooldowns.ContainsKey(k));
        StartableNow = nowSet;
        Cooldowns = cds; //replace the remaining seconds with the authoritative server value

        if (changed && Visible)
            BuildDetail();
    }

    //quest keys the player has completed at least once - drives the Completed tab classification (auto, no eventId).
    private HashSet<string> CompletedKeys = new(StringComparer.OrdinalIgnoreCase);

    public void SetCompletedKeys(IReadOnlyList<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

        if (set.SetEquals(CompletedKeys))
            return;

        CompletedKeys = set;
        Rebuild(); //completion changes which tab a quest lands in
    }

    //SWM: "Claim Reward" was removed - an event / no-npc quest finishes on its own, so nothing is ever
    //left to claim from the journal.

    public void SetGuide(IReadOnlyList<QuestMetadataEntry> guide, HashSet<string> completedIds, BaseClass playerClass, bool masterQuests)
    {
        Guide = guide;
        CompletedIds = completedIds;
        PlayerClass = playerClass;
        MasterQuests = masterQuests;

        if (Visible)
            Rebuild();
    }

    public void RebuildIfVisible()
    {
        if (Visible)
            Rebuild();
    }

    private float AppliedQuestScale = -1f;

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        //tick the cooldown countdowns locally + refresh the visible detail once a second so it counts down
        if (Cooldowns.Count > 0)
        {
            var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

            foreach (var k in Cooldowns.Keys.ToList())
                Cooldowns[k] = Math.Max(0, Cooldowns[k] - dt);

            if (Visible)
            {
                CountdownRefresh += dt;

                if (CountdownRefresh >= 1.0)
                {
                    CountdownRefresh = 0;
                    var sel = Entries.FirstOrDefault(e => e.Key == SelectedKey);

                    if ((sel is not null) && Cooldowns.ContainsKey(sel.QuestKey))
                        BuildDetail();
                }
            }
        }

        //live-apply the Options "Quests" text-size slider
        if (Visible && !AppliedQuestScale.Equals(ClientSettings.EffectiveQuestFontScale))
        {
            AppliedQuestScale = ClientSettings.EffectiveQuestFontScale;
            Rebuild();
        }

        //placeholder hint when the box is empty and not being typed in (like the market)
        SearchPlaceholder.Visible = string.IsNullOrEmpty(Search.Text) && !Search.IsFocused;

        //the clear "x" only shows once something is typed
        SearchClear.Visible = Search.Text.Length > 0;

        //re-filter the list as the player types in the search box
        if (Visible && (Search.Text != AppliedSearch))
        {
            AppliedSearch = Search.Text;
            BuildSidebar();
        }
    }

    //route the wheel to whichever scroll region is under the cursor. The regions are pass-through and the detail's
    //labels are non-hit-test, so an unhandled wheel bubbles up to this window instead of reaching them.
    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (e.Handled)
            return;

        var region = e.ScreenX >= Detail.ScreenX ? Detail : Sidebar;
        region.OnMouseScroll(e);
    }

    private void SwitchTab(Tab tab)
    {
        Current = tab;
        SelectedKey = null; //let the rebuild pick the first entry in the new tab
        Rebuild();
    }

    //--- classification ---

    private void Rebuild()
    {
        Entries.Clear();

        var activeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var quest in ActiveQuests)
        {
            activeTitles.Add(quest.Title);

            if (!string.IsNullOrEmpty(quest.QuestKey))
                activeKeys.Add(quest.QuestKey);

            var steps = new List<StepLine>(quest.Checkpoints.Count);

            for (var i = 0; i < quest.Checkpoints.Count; i++)
            {
                var cp = quest.Checkpoints[i];
                var state = (quest.CurrentIndex != 255) && (i == quest.CurrentIndex) ? 1
                    : (quest.CurrentIndex != 255) && (i < quest.CurrentIndex) ? 2 : 0;
                steps.Add(new StepLine(cp.Label, cp.Detail, cp.Description, state));
            }

            Entries.Add(
                new Entry
                {
                    Key = "q:" + quest.QuestKey,
                    QuestKey = quest.QuestKey,
                    Title = string.IsNullOrEmpty(quest.Title) ? quest.QuestKey : quest.Title,
                    Tab = Tab.Active,
                    Color = ActiveColor,
                    Description = quest.Description,
                    Counter = quest.Counter,
                    Status = "In progress",
                    StatusKind = Tab.Active,
                    Steps = steps
                });
        }

        foreach (var ev in Guide)
        {
            if (string.IsNullOrEmpty(ev.Title))
                continue;

            Classify(ev, activeTitles, activeKeys);
        }

        UpdateTabCounts();
        BuildSidebar();

        //select the first entry of the current tab if nothing valid is selected
        var inTab = Entries.Where(e => e.Tab == Current).ToList();

        if (SelectedKey is null || inTab.All(e => e.Key != SelectedKey))
            SelectedKey = inTab.Count > 0 ? FirstByOrder(inTab).Key : null;

        BuildDetail();
    }

    //adds 0..2 journal entries for one guide event (a repeatable quest that is also completed appears in both the
    //Repeatable tab and the Completed tab's "Repeatable" foldout)
    private void Classify(QuestMetadataEntry ev, HashSet<string> activeTitles, HashSet<string> activeKeys)
    {
        //completed if the server reports this quest key as done, or a held legend-mark matches the quest key
        var completed = !string.IsNullOrEmpty(ev.Key) && (CompletedKeys.Contains(ev.Key) || CompletedIds.Contains(ev.Key));
        var wrongClass = !string.IsNullOrEmpty(ev.QualifyingClasses) && !ev.QualifyingClasses.Contains((char)('0' + (int)PlayerClass));

        //a quest that is already active belongs in the Active tab, not Suggested/Repeatable (hides its Start button)
        var active = (!string.IsNullOrEmpty(ev.Key) && activeKeys.Contains(ev.Key)) || activeTitles.Contains(ev.Title);

        //"listed" = the quest browses in Suggested/Repeatable (circle 1-6). An UNLISTED quest (empty circles)
        //only ever appears once COMPLETED, so it still shows in the Completed tab but never clutters the browse tabs.
        var listed = !string.IsNullOrEmpty(ev.QualifyingCircles) && ev.VisibleWhenNotStarted;

        //repeatable quests (server cooldown) live in their own tab, grouped by cadence; level is NOT filtered (dailies
        //are available across levels). Class still gates them.
        if (ev.RepeatMinutes > 0)
        {
            if (wrongClass || active) //active -> it shows in the Active tab instead
                return;

            var cadence = Cadence(ev.RepeatMinutes);
            var title = StripTrailingParen(ev.Title);

            //a repeatable that's been done is ON COOLDOWN: it sits in Completed > Repeatable until it's ready again
            //(when the cooldown expires it's no longer here and returns to the Repeatable tab).
            var onCooldown = !string.IsNullOrEmpty(ev.Key) && Cooldowns.ContainsKey(ev.Key);

            if ((onCooldown || (completed && !listed)) && ev.VisibleWhenComplete)
                Entries.Add(
                    new Entry
                    {
                        Key = "done:" + Id(ev), Title = title, Tab = Tab.Completed, Category = "Repeatable", Sort = 0,
                        Color = CompletedColor, Description = ev.Description, Outcomes = ev.Outcomes,
                        StartMinLevel = ev.MinLevel, StartMaxLevel = ev.MaxLevel, QuestKey = ev.Key,
                        Status = $"Completed - repeats {Cadence(ev.RepeatMinutes).ToLowerInvariant()}", StatusKind = Tab.Completed
                    });
            else if (listed)
                Entries.Add(
                    new Entry
                    {
                        Key = "rep:" + Id(ev), Title = title, Tab = Tab.Repeatable, Category = cadence,
                        Color = AvailableColor, Description = ev.Description, Outcomes = ev.Outcomes, OutlineSteps = ev.Steps,
                        StartMinLevel = ev.MinLevel, StartMaxLevel = ev.MaxLevel,
                        Status = $"{cadence} - repeatable ({Cooldown(ev.RepeatMinutes)})", StatusKind = Tab.Repeatable,
                        StartHint = ev.StartHint, ShowStart = true, QuestKey = ev.Key, Startable = ev.Startable
                    });

            return;
        }

        //once-off completed -> Completed (flat, no foldout) - gated by the author's "visible when complete" flag
        if (completed && ev.VisibleWhenComplete)
        {
            Entries.Add(
                new Entry
                {
                    Key = "done:" + Id(ev), Title = ev.Title, Tab = Tab.Completed, Sort = PrimaryCircle(ev),
                    Color = CompletedColor, Description = ev.Description, Outcomes = ev.Outcomes,
                    StartMinLevel = ev.MinLevel, StartMaxLevel = ev.MaxLevel,
                    Status = "Completed", StatusKind = Tab.Completed
                });

            return;
        }

        //an UNLISTED quest (empty circles) that isn't completed never shows in Suggested - it only appears once done
        if (!listed)
            return;

        var level = WorldState.Attributes.Current?.Level ?? 1;
        var circle = MasterQuests ? 6 : level switch { >= 99 => 5, >= 71 => 4, >= 41 => 3, >= 11 => 2, _ => 1 };

        if (wrongClass)
            return;

        //wrong circle (level) -> hide; this is the level filter for Suggested
        if (!string.IsNullOrEmpty(ev.QualifyingCircles) && !ev.QualifyingCircles.Contains((char)('0' + circle)))
            return;

        //already active -> it shows in the Active tab instead (also hides its Start button)
        if (active)
            return;

        var prim = PrimaryCircle(ev);
        var locked = !string.IsNullOrEmpty(ev.PrerequisiteKey) && !CompletedKeys.Contains(ev.PrerequisiteKey) && !CompletedIds.Contains(ev.PrerequisiteKey);

        Entries.Add(
            new Entry
            {
                Key = "sug:" + Id(ev), Title = ev.Title, Tab = Tab.Suggested,
                Category = prim > 0 ? $"Circle {prim}" : "General", Sort = prim,
                Color = locked ? LockedColor : AvailableColor,
                Description = ev.Description, Outcomes = ev.Outcomes, OutlineSteps = ev.Steps,
                StartMinLevel = ev.MinLevel, StartMaxLevel = ev.MaxLevel,
                Status = locked ? "Locked - finish an earlier quest first" : "Available",
                StatusKind = locked ? Tab.Completed /*greyed*/ : Tab.Suggested,
                StartHint = ev.StartHint, ShowStart = true,
                QuestKey = ev.Key, Startable = ev.Startable && !locked //no direct start while prereq-locked
            });
    }

    private static string Id(QuestMetadataEntry ev) => string.IsNullOrEmpty(ev.Key) ? ev.Title : ev.Key;

    //a friendly cadence bucket from the server cooldown (minutes -> hours)
    private static string Cadence(int minutes)
        => minutes switch
        {
            <= 0           => "Repeatable",
            <= 60          => "Hourly",
            <= 24 * 60     => "Daily",
            <= 7 * 24 * 60 => "Weekly",
            <= 31 * 24 * 60 => "Monthly",
            _              => "Periodic"
        };

    //a short human cooldown ("8h", "12h", "1d") for the status line
    private static string Cooldown(int minutes)
    {
        if (minutes <= 0)
            return "anytime";

        if (minutes < 60)
            return $"{minutes}m";

        if (minutes % (24 * 60) == 0)
            return $"{minutes / (24 * 60)}d";

        if (minutes % 60 == 0)
            return $"{minutes / 60}h";

        return $"{minutes / 60}h {minutes % 60}m";
    }

    //strips a trailing "(...)" hint from a title (e.g. "Sigrid the Peddler (Daily)" -> "Sigrid the Peddler")
    private static string StripTrailingParen(string title)
    {
        var open = title.LastIndexOf('(');

        return (open > 0) && title.TrimEnd().EndsWith(')') ? title[..open].Trim() : title;
    }

    //lowest qualifying circle digit (the circle the quest is first meant for), or 0 when unspecified
    private static int PrimaryCircle(QuestMetadataEntry ev)
    {
        var best = 0;

        foreach (var c in ev.QualifyingCircles)
            if (c is >= '1' and <= '6')
                best = best == 0 ? c - '0' : Math.Min(best, c - '0');

        return best;
    }

    //--- sidebar (left) ---

    private void UpdateTabCounts()
    {
        foreach (var (tab, btn) in TabButtons)
        {
            var n = Entries.Count(e => e.Tab == tab);
            btn.Text = n > 0 ? $"{TabTitle(tab)} ({n})" : TabTitle(tab);
            btn.Selected = tab == Current;
        }
    }

    private void BuildSidebar()
    {
        Sidebar.Clear();

        var search = Search.Text.Trim();
        var inTab = Entries.Where(e => e.Tab == Current)
                           .Where(e => (search.Length == 0) || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
                           .ToList();
        var y = 0;

        if (inTab.Count == 0)
        {
            var hint = new UILabel
            {
                X = PAD, Y = PAD, Width = Sidebar.InnerWidth - PAD * 2, WordWrap = true,
                CustomFontSize = Scaled(FONT), ForegroundColor = DimColor, IsHitTestVisible = false,
                Text = search.Length > 0 ? $"No quests match \"{search}\"." : EmptyTabHint(Current)
            };

            //size to the WRAPPED content - a fixed 40px clipped the 2-3 line message ("...pick one from")
            hint.Height = Math.Max(Scaled(FONT) + 6, hint.ContentHeight + 4);
            Sidebar.Add(hint);
            Sidebar.SetContentHeight(hint.Height + PAD * 2);

            return;
        }

        //group by category. An empty Category renders FLAT (no header, indent 0); a named one gets a foldout header
        //+ indented rows. Empty-category groups come first, then named groups by their sort key.
        var groups = inTab.GroupBy(e => e.Category)
                          .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 0 : 1)
                          .ThenBy(g => g.Min(e => e.Sort))
                          .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        foreach (var group in groups)
        {
            var named = !string.IsNullOrEmpty(group.Key);

            if (named)
            {
                var key = Current + "|" + group.Key;
                var collapsed = ClientSettings.JournalCollapsed.Contains(key);

                var header = new FoldoutHeader(Sidebar.InnerWidth, $"{(collapsed ? "+" : "-")}  {group.Key}  ({group.Count()})") { Y = y };
                header.Clicked = () =>
                {
                    if (!ClientSettings.JournalCollapsed.Remove(key))
                        ClientSettings.JournalCollapsed.Add(key);

                    ClientSettings.Save();
                    BuildSidebar();
                };
                Sidebar.Add(header);
                y += Scaled(ROW_H);

                if (collapsed)
                    continue;
            }

            foreach (var entry in group.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase))
            {
                var row = new QuestRow(Sidebar.InnerWidth, entry.Title, entry.Color, named ? 1 : 0, entry.Key == SelectedKey) { Y = y };
                var captured = entry.Key;
                row.Clicked = () => Select(captured);
                Sidebar.Add(row);
                y += Scaled(ROW_H);
            }
        }

        Sidebar.SetContentHeight(y + PAD);
    }

    private void Select(string key)
    {
        SelectedKey = key;
        BuildSidebar();
        BuildDetail();
    }

    private Entry FirstByOrder(List<Entry> inTab)
        => inTab.OrderBy(e => e.Sort).ThenBy(e => e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).First();

    //--- detail (right) ---

    private void BuildDetail()
    {
        Detail.Clear();
        HoveredRewardName = null; //the reward icons are recreated; drop any stale hover

        var entry = Entries.FirstOrDefault(e => e.Key == SelectedKey);
        var innerW = Detail.InnerWidth - PAD * 2;
        var y = PAD;

        if (entry is null)
        {
            AddDetail("Select a quest to read about it.", BODY_FONT, DimColor, PAD, innerW, ref y, wrap: true);
            Detail.SetContentHeight(y + PAD);

            return;
        }

        if (entry.Tab == Tab.Active)
        {
            QuestDetailView.AddTitle(Detail, entry.Title, innerW, ref y); //large title (shared with the offer window)
            BuildActiveDetail(entry, innerW, ref y);
        } else if (entry.Tab == Tab.Completed)
        {
            QuestDetailView.AddTitle(Detail, entry.Title, innerW, ref y);
            BuildCompletedDetail(entry, innerW, ref y);
        } else
            BuildStartableDetail(entry, innerW, ref y); //the shared renderer draws the title itself

        Detail.SetContentHeight(y + PAD);
    }

    //a thin horizontal divider (shared with the offer window)
    private void AddSeparator(int width, ref int y) => QuestDetailView.AddSeparator(Detail, width, ref y);

    //the detail for a quest that can be started/browsed (Suggested + Repeatable) - rendered by the SHARED
    //QuestDetailView so it looks exactly like the NPC offer window, with the Start button appended below.
    private void BuildStartableDetail(Entry entry, int innerW, ref int y)
    {
        var startableNow = !string.IsNullOrEmpty(entry.QuestKey) && StartableNow.Contains(entry.QuestKey);
        double cd = 0;
        var onCooldown = !string.IsNullOrEmpty(entry.QuestKey) && Cooldowns.TryGetValue(entry.QuestKey, out cd);

        var status = onCooldown
            ? $"Available in {HumanizeCooldown(cd)}"
            : !string.IsNullOrEmpty(entry.Status) && (entry.Tab == Tab.Repeatable) ? entry.Status : null;

        var model = new QuestDetailModel
        {
            Title = entry.Title,
            Description = entry.Description,
            //"How to start" only when the player can't start it here (it must be begun in the world at an NPC)
            ShowHowToStart = !startableNow && !onCooldown,
            StartHint = entry.StartHint,
            Steps = entry.OutlineSteps,
            Outcomes = QuestDetailView.ToOutcomeViews(entry.Outcomes),
            StatusLine = status
        };

        QuestDetailView.Render(Detail, innerW, model, n => HoveredRewardName = n, ref y);

        //Start Quest only when the server says it can be started right now
        if (startableNow)
        {
            y += 12;
            var key = entry.QuestKey;
            AddActionButton("Start Quest", ActiveColor, ref y, () => OnStartQuest?.Invoke(key));
        }
    }

    //the detail for an in-progress quest: objectives + reward + tracker/abandon controls
    private void BuildActiveDetail(Entry entry, int innerW, ref int y)
    {
        y += 2;
        AddDetail("In progress", BODY_FONT, ActiveColor, PAD, innerW, ref y, wrap: false);

        if (!string.IsNullOrEmpty(entry.Counter))
        {
            y += 6;
            AddDetail(entry.Counter, BODY_FONT, RewardColor, PAD, innerW, ref y, wrap: false);
        }

        if (!string.IsNullOrEmpty(entry.Description))
        {
            y += 8;
            AddDetail(entry.Description, BODY_FONT, BodyColor, PAD, innerW, ref y, wrap: true);
        }

        if (entry.Steps.Count > 0)
        {
            AddSeparator(innerW, ref y);
            AddDetail("Objectives", HEADER_FONT, HeaderColor, PAD, innerW, ref y, wrap: false);
            y += 4;

            foreach (var step in entry.Steps)
            {
                var color = step.State == 1 ? ActiveColor : step.State == 2 ? CompletedColor : DimColor;
                var marker = step.State == 1 ? ">  " : step.State == 2 ? "-  " : "    ";
                var detail = (step.State == 1) && !string.IsNullOrEmpty(step.Detail) ? $"   {step.Detail}" : string.Empty;
                AddDetail($"{marker}{step.Label}{detail}", BODY_FONT, color, PAD, innerW, ref y, wrap: false);
                y += 2;
            }
        }

        if (AnyReward(entry))
        {
            AddSeparator(innerW, ref y);
            QuestDetailView.AddRewards(Detail, innerW, QuestDetailView.ToOutcomeViews(entry.Outcomes), n => HoveredRewardName = n, ref y);
        }

        if (!string.IsNullOrEmpty(entry.QuestKey))
        {
            var key = entry.QuestKey;
            AddSeparator(innerW, ref y);

            //SWM: events / no-npc quests FINISH ON THEIR OWN now - there is never a journal "Claim Reward".

            var tracked = !ClientSettings.UntrackedQuests.Contains(key);
            AddActionButton(tracked ? "Hide from tracker" : "Show on tracker", tracked ? DimColor : ActiveColor, ref y, () => ToggleTrack(key));

            //SWM: mandatory quests (choose-your-class, the tutorial events) carry canabandon=0 in the
            //SwmQuests metafile - hide the Abandon button for them so the player cannot drop them.
            if (CanAbandonQuest(key))
            {
                y += 8;
                AddActionButton("Abandon quest", LockedColor, ref y, () => OnAbandonQuest?.Invoke(key));
            }
        }
    }

    //SWM: may this active quest be abandoned? Looked up from the SwmQuests catalog (Guide) by key; absent/unknown
    //defaults to TRUE (abandonable), matching the server, which only emits canabandon=0 for mandatory quests.
    private bool CanAbandonQuest(string key)
        => Guide.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))?.CanAbandon ?? true;

    //the detail for a finished quest: what it was + what you earned. For a repeatable on cooldown, the status line
    //IS the live "available again in ..." countdown.
    private void BuildCompletedDetail(Entry entry, int innerW, ref int y)
    {
        y += 2;

        if (!string.IsNullOrEmpty(entry.QuestKey) && Cooldowns.TryGetValue(entry.QuestKey, out var cd))
            AddDetail($"Available again in {HumanizeCooldown(cd)}", BODY_FONT, CompletedColor, PAD, innerW, ref y, wrap: false);
        else
            AddDetail("Completed", BODY_FONT, CompletedColor, PAD, innerW, ref y, wrap: false);

        if (!string.IsNullOrEmpty(entry.Description))
        {
            y += 8;
            AddDetail(entry.Description, BODY_FONT, BodyColor, PAD, innerW, ref y, wrap: true);
        }

        if (AnyReward(entry))
        {
            AddSeparator(innerW, ref y);
            QuestDetailView.AddRewards(Detail, innerW, QuestDetailView.ToOutcomeViews(entry.Outcomes), n => HoveredRewardName = n, ref y);
        }
    }

    //scale a base font size by the player's quest-text size setting (Options "Quests" slider)
    private static int Scaled(int baseSize) => Math.Max(baseSize, (int)MathF.Round(baseSize * ClientSettings.EffectiveQuestFontScale));

    //a short remaining-cooldown string for the journal countdown ("7h 12m", "4m 30s", "9s")
    private static string HumanizeCooldown(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));

        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";

        if (t.TotalMinutes >= 1)
            return $"{t.Minutes}m {t.Seconds}s";

        return $"{t.Seconds}s";
    }

    private void AddDetail(string text, int fontSize, Color color, int x, int width, ref int y, bool wrap)
    {
        fontSize = Scaled(fontSize);

        var label = new UILabel
        {
            X = x, Y = y, Width = width,
            WordWrap = wrap,
            CustomFontSize = fontSize,
            ForegroundColor = color,
            Text = text,
            TruncateWithEllipsis = !wrap,
            IsHitTestVisible = false
        };

        var lineH = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(fontSize) : fontSize + 4;
        label.Height = wrap ? Math.Max(lineH, label.ContentHeight) + 2 : lineH;

        Detail.Add(label);
        y += label.Height + 2;
    }

    private void AddActionButton(string text, Color color, ref int y, Action onClick)
    {
        var btn = new ActionButton(220, Scaled(28), text, color) { X = PAD, Y = y, Clicked = onClick };
        Detail.Add(btn);
        y += Scaled(28) + 4;
    }

    //true when any of the quest's reward outcomes grants something (drives whether a detail pane shows a reward section)
    private static bool AnyReward(Entry entry) => entry.Outcomes.Any(o => (o.Exp > 0) || (o.Gold > 0) || (o.Items.Count > 0) || (o.Marks.Count > 0));

    private void ToggleTrack(string questKey)
    {
        bool nowTracked;

        if (ClientSettings.UntrackedQuests.Remove(questKey))
            nowTracked = true;
        else
        {
            ClientSettings.UntrackedQuests.Add(questKey);
            nowTracked = false;
        }

        ClientSettings.Save();
        OnTrackToggled?.Invoke(questKey, nowTracked);
        BuildDetail(); //refresh the button label
    }

    private static Color StatusColor(Tab kind)
        => kind switch
        {
            Tab.Active     => ActiveColor,
            Tab.Suggested  => RewardColor,
            Tab.Repeatable => RewardColor,
            _              => CompletedColor
        };

    private static string TabTitle(Tab tab)
        => tab switch
        {
            Tab.Active     => "Active",
            Tab.Suggested  => "Suggested",
            Tab.Repeatable => "Repeatable",
            _              => "Completed"
        };

    private static string EmptyTabHint(Tab tab)
        => tab switch
        {
            Tab.Active     => "You have no active quests. Find a quest in the world or pick one from the Suggested tab.",
            Tab.Suggested  => "Nothing suggested right now - check back as you level up.",
            Tab.Repeatable => "No repeatable quests available yet.",
            _              => "You have not completed any quests yet."
        };

    protected override void OnCloseClicked()
    {
        ClearSearch();
        Visible = false;
    }

    /// <summary>Clears the search filter (called on close + when the journal is opened, so it always starts fresh).</summary>
    public void ClearSearch()
    {
        if (Search.Text.Length == 0)
            return;

        Search.Text = string.Empty;
        AppliedSearch = string.Empty;
        BuildSidebar();
    }

    //--- nested controls ---

    //a prominent top tab with a persistent selected state
    private sealed class TabButton : UIPanel
    {
        private readonly UILabel Label;
        private bool IsSelected;

        public Action? Clicked;

        public TabButton(string text, int width, int height)
        {
            Width = width;
            Height = height;
            BackgroundColor = TabIdle;
            BorderColor = Edge;

            Label = new UILabel
            {
                X = 2, Y = 0, Width = width - 4, Height = height,
                Text = text, CustomFontSize = TAB_FONT, ForegroundColor = new Color(206, 184, 132),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            AddChild(Label);
        }

        public string Text { set => Label.Text = value; }

        public bool Selected
        {
            set
            {
                IsSelected = value;
                BackgroundColor = value ? TabSelected : TabIdle;
                Label.ForegroundColor = value ? new Color(248, 230, 178) : new Color(206, 184, 132);
            }
        }

        public override void OnMouseEnter()
        {
            if (!IsSelected)
                BackgroundColor = TabHover;
        }

        public override void OnMouseLeave()
        {
            if (!IsSelected)
                BackgroundColor = TabIdle;
        }

        public override void OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Left)
            {
                if (!IsSelected) //clicking the tab you are already on is silent (matches the market)
                    SoundSystem.PlayUiClick();

                Clicked?.Invoke();
            }

            e.Handled = true;
        }
    }

    //a foldout category header ("+ Daily (3)" / "- Daily (3)"). Gold text (kept by request); toggles silently like
    //the market's tree headers.
    private sealed class FoldoutHeader : UIPanel
    {
        public Action? Clicked;

        public FoldoutHeader(int width, string text)
        {
            Width = width;
            Height = Scaled(ROW_H);

            AddChild(
                new UILabel
                {
                    X = PAD, Y = 0, Width = width - PAD - 4, Height = Scaled(ROW_H),
                    Text = text, CustomFontSize = Scaled(HEADER_FONT), ForegroundColor = HeaderColor,
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
                });
        }

        public override void OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Left)
                Clicked?.Invoke();

            e.Handled = true;
        }

        public override void OnMouseEnter() => BackgroundColor = RowHover;
        public override void OnMouseLeave() => BackgroundColor = null;
    }

    //one selectable quest row (left-aligned name, indent, hover + selected highlight - gold text when selected, like
    //the market). Selecting a NEW row clicks; re-clicking the already-selected row is silent.
    private sealed class QuestRow : UIPanel
    {
        private readonly bool IsSelectedRow;

        public Action? Clicked;

        public QuestRow(int width, string text, Color color, int indent, bool selected)
        {
            Width = width;
            Height = Scaled(ROW_H);
            IsSelectedRow = selected;
            BackgroundColor = selected ? RowSelected : null;

            var x = PAD + indent * 16; //match the market tree's indent

            AddChild(
                new UILabel
                {
                    X = x, Y = 0, Width = width - x - 6, Height = Scaled(ROW_H),
                    Text = text, CustomFontSize = Scaled(FONT), ForegroundColor = selected ? TextSel : color,
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                    TruncateWithEllipsis = true
                });
        }

        public override void OnMouseDown(MouseDownEvent e)
        {
            //rows are silent (only the tabs click); selection feedback is purely visual
            if (e.Button == MouseButton.Left)
                Clicked?.Invoke();

            e.Handled = true;
        }

        public override void OnMouseEnter()
        {
            if (!IsSelectedRow)
                BackgroundColor = RowHover;
        }

        public override void OnMouseLeave()
        {
            if (!IsSelectedRow)
                BackgroundColor = null;
        }
    }

    //a small bordered action button (Start / Hide from tracker). Silent, like the rest of the journal rows.
    private sealed class ActionButton : UIPanel
    {
        private static readonly Color Idle = new(40, 34, 24);
        private static readonly Color Hover = new(58, 50, 34);

        public Action? Clicked;

        public ActionButton(int width, int height, string text, Color color)
        {
            Width = width;
            Height = height;
            BackgroundColor = Idle;
            BorderColor = Edge;

            AddChild(
                new UILabel
                {
                    X = 2, Y = 0, Width = width - 4, Height = height,
                    Text = text, CustomFontSize = Scaled(FONT), ForegroundColor = color,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                });
        }

        public override void OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Left)
                Clicked?.Invoke();

            e.Handled = true;
        }

        public override void OnMouseEnter() => BackgroundColor = Hover;
        public override void OnMouseLeave() => BackgroundColor = Idle;
    }
}
