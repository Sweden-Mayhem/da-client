#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Data.Models;
using Chaos.Client.Extensions;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     The WoW-style "Do you want to start this quest?" offer window, shown when an NPC offers a quest. Renders the
///     EXACT same detail the journal shows (large title / description / --- / Reward icons / --- / repeatable info)
///     via the shared <see cref="QuestDetailView" />, plus an Accept / Decline choice. Accepting raises
///     <see cref="OnAccept" /> with the quest key.
/// </summary>
public sealed class QuestOfferControl : DraggableWindow
{
    private const int W = 560;
    private const int H = 470;
    private const int PAD = 10;
    private const int BTN_H = 30;

    private static readonly Color GoldColor = new(224, 192, 108);
    private static readonly Color DimColor = new(172, 164, 150);

    private readonly ScrollRegion Body;
    private readonly MenuButton AcceptBtn;
    private readonly MenuButton DeclineBtn;

    private string QuestKey = string.Empty;

    /// <summary>The reward item the cursor is over (its name), so WorldScreen can show the item tooltip. Null = none.</summary>
    public string? HoveredRewardName { get; private set; }

    /// <summary>Raised when the player clicks Accept (the quest key). The owner sends the start request.</summary>
    public Action<string>? OnAccept;

    public QuestOfferControl()
        : base("Quest", W, H, useWoodFrame: true)
    {
        FadeOnOpen = true;

        var cw = Content.Width;
        var ch = Content.Height;
        var btnW = (cw - PAD * 3) / 2;

        Body = new ScrollRegion(cw - PAD * 2, ch - PAD * 2 - BTN_H - 6) { X = PAD, Y = PAD };
        Content.AddChild(Body);

        AcceptBtn = new MenuButton("Accept Quest", btnW, BTN_H, GoldColor) { X = PAD, Y = ch - PAD - BTN_H };
        AcceptBtn.Clicked = _ =>
        {
            var key = QuestKey;
            Close();
            OnAccept?.Invoke(key);
        };
        Content.AddChild(AcceptBtn);

        DeclineBtn = new MenuButton("Decline", btnW, BTN_H, DimColor) { X = PAD * 2 + btnW, Y = ch - PAD - BTN_H };
        DeclineBtn.Clicked = _ => Close();
        Content.AddChild(DeclineBtn);
    }

    //route the mousewheel to the scrollable body so the description + rewards scroll
    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (e.Handled)
            return;

        Body.OnMouseScroll(e);
    }

    /// <summary>Populates + centres + shows the offer for a catalog quest with the SAME visual detail as the journal.
    ///     The reward outcomes come ONLY from the catalog (never the offer packet), so the offer + journal never disagree.</summary>
    public void ShowOffer(QuestMetadataEntry quest)
    {
        QuestKey = quest.Key;
        HoveredRewardName = null; //the reward icons are recreated

        Body.Clear();
        var innerW = Body.InnerWidth - PAD;
        var y = 0;

        var model = new QuestDetailModel
        {
            Title = string.IsNullOrEmpty(quest.Title) ? "New Quest" : quest.Title,
            Description = quest.Description,
            Outcomes = QuestDetailView.ToOutcomeViews(quest.Outcomes),
            StatusLine = RepeatableLine(quest.RepeatMinutes)
        };

        QuestDetailView.Render(Body, innerW, model, n => HoveredRewardName = n, ref y);

        Body.SetContentHeight(y + PAD);
        this.CenterOnUi();
        Open();
    }

    //"Daily - repeatable (12h)" style line from the cooldown, or null for a one-off quest
    private static string? RepeatableLine(int minutes)
    {
        if (minutes <= 0)
            return null;

        var cadence = minutes switch
        {
            <= 60           => "Hourly",
            <= 24 * 60      => "Daily",
            <= 7 * 24 * 60  => "Weekly",
            <= 31 * 24 * 60 => "Monthly",
            _               => "Periodic"
        };

        var human = minutes >= 60 ? $"{minutes / 60}h" : $"{minutes}m";

        return $"{cadence} - repeatable ({human})";
    }
}
