namespace Chaos.Client.Systems;

/// <summary>
///     Drives the quick transition shown when the player changes maps, so the screen no longer just snaps from one map to
///     the next. Two styles, chosen per-warp from <see cref="ClientSettings.AlternativeMapFade" />:
///     <list type="bullet">
///         <item>CROSS-FADE (default): the frozen old map dissolves straight into the new one with no black flash.</item>
///         <item>FADE-TO-BLACK (the "Alternative map fade" option): old map fades out to black, holds, new map fades in.</item>
///     </list>
///     ChaosGame owns one instance, advances it each frame, and draws the captured old-map frame + black overlay as the
///     these alpha values dictate. WorldScreen drives the phases from the map handlers (<c>BeginFadeOut</c> when a new map
///     starts loading, <c>BeginFadeIn</c> when it is ready). The fade-in is deferred (BeginFadeIn arms a flag rather than
///     switching phase) because a cached map loads in the same tick the fade-out begins, so the out/hold phase must run
///     first or the transition would collapse with nothing drawn. The captured old frame covers the (possibly
///     still-loading) new map until the reveal begins, so even a streamed map never flashes a blank/partial frame.
/// </summary>
public sealed class MapTransition
{
    public enum Phase
    {
        None,
        //fade-to-black style
        Out,  //old (frozen) map fading to black
        Hold, //fully black, waiting for the new map to be ready
        In,   //new map fading up from black
        //cross-fade style
        CrossWait,  //frozen old map held over the (loading) new map, waiting for ready
        CrossFade   //frozen old map dissolving away to reveal the new map
    }

    private const float OutSeconds = 0.16f;
    private const float InSeconds = 0.26f;
    private const float CrossSeconds = 0.32f;

    //safety net, reveal anyway if the new map never signals ready, rather than holding forever
    private const float HoldTimeoutSeconds = 3f;

    private float HoldElapsed;
    private float Progress; //0..1 within the current fading sub-phase (Out / In / CrossFade)
    private bool CaptureRequested;
    private bool ReadyToFadeIn;
    private bool BlackStyle; //snapshot of the option at BeginFadeOut so toggling mid-warp can't mix styles

    public Phase Current { get; private set; } = Phase.None;

    /// <summary>True when the captured old-map frame should REPLACE the live world as the base layer (fade-to-black
    ///     out/hold, where the live new map must not show yet).</summary>
    public bool ShowFrozenAsBase => Current is Phase.Out or Phase.Hold;

    /// <summary>Alpha to draw the captured old-map frame at, OVER the live new map (cross-fade). 1 = old fully covers new,
    ///     0 = fully revealed new. 0 outside the cross-fade phases.</summary>
    public float FrozenOverlayAlpha
        => Current switch
        {
            Phase.CrossWait => 1f,
            Phase.CrossFade => 1f - Progress,
            _               => 0f
        };

    /// <summary>Alpha of the full-screen black overlay (fade-to-black style only; 0 for cross-fade).</summary>
    public float BlackAlpha
        => Current switch
        {
            Phase.Out  => Progress,
            Phase.Hold => 1f,
            Phase.In   => 1f - Progress,
            _          => 0f
        };

    /// <summary>Starts a transition (a new map is loading). Requests a one-shot capture of the current world frame.</summary>
    public void BeginFadeOut()
    {
        //already transitioning, keep going, just re-arm the hold timeout
        if (Current != Phase.None)
        {
            HoldElapsed = 0f;

            return;
        }

        BlackStyle = ClientSettings.AlternativeMapFade;
        CaptureRequested = true;
        ReadyToFadeIn = false;
        HoldElapsed = 0f;
        Progress = 0f;
        Current = BlackStyle ? Phase.Out : Phase.CrossWait;
    }

    /// <summary>Signals the new map is ready. The out/wait phase still completes first, then the reveal begins. No-op when
    ///     no transition is in progress (e.g. first world entry).</summary>
    public void BeginFadeIn()
    {
        if (Current == Phase.None)
            return;

        ReadyToFadeIn = true;
    }

    /// <summary>Returns true once after <see cref="BeginFadeOut" />, so ChaosGame snapshots the world frame exactly once.</summary>
    public bool ConsumeCaptureRequest()
    {
        if (!CaptureRequested)
            return false;

        CaptureRequested = false;

        return true;
    }

    /// <summary>Advances the transition by the elapsed time. Called once per frame.</summary>
    public void Update(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return;

        switch (Current)
        {
            case Phase.Out:
                Progress = Math.Min(1f, Progress + deltaSeconds / OutSeconds);

                if (Progress >= 1f)
                {
                    Current = Phase.Hold;
                    HoldElapsed = 0f;
                }

                break;

            case Phase.Hold:
                HoldElapsed += deltaSeconds;

                if (ReadyToFadeIn || (HoldElapsed >= HoldTimeoutSeconds))
                {
                    Current = Phase.In;
                    Progress = 0f;
                }

                break;

            case Phase.In:
                Progress = Math.Min(1f, Progress + deltaSeconds / InSeconds);

                if (Progress >= 1f)
                    Finish();

                break;

            case Phase.CrossWait:
                HoldElapsed += deltaSeconds;

                if (ReadyToFadeIn || (HoldElapsed >= HoldTimeoutSeconds))
                {
                    Current = Phase.CrossFade;
                    Progress = 0f;
                }

                break;

            case Phase.CrossFade:
                Progress = Math.Min(1f, Progress + deltaSeconds / CrossSeconds);

                if (Progress >= 1f)
                    Finish();

                break;
        }
    }

    private void Finish()
    {
        Current = Phase.None;
        Progress = 0f;
        ReadyToFadeIn = false;
    }
}
