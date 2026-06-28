#region
using Chaos.Client.Collections;
using Chaos.Client.Rendering;
using Chaos.Client.Rendering.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Screens;

/// <summary>
///     Camera feel and damage feedback
///     A smooth pan toward the player or NPC speaker, a shake on HP loss, and a red screen-edge pulse on damage
/// </summary>
public sealed partial class WorldScreen
{
    //focus speaker, camera pan to the talking NPC
    //the camera targets the player's smooth-walk position or the speaker's, sitting exactly on it when settled
    //a change of target runs one fixed-duration eased transition so there is no slow decel crawl at low res
    private Vector2 CamPos;          //the camera world position
    private bool CamInitialized;     //seed CamPos on first use
    private bool LockedOnSpeaker;    //current target identity, a change starts a transition
    private bool PanSettled = true;  //true once a transition has arrived
    private Vector2 PanStart;        //CamPos captured at the start of the current transition
    private float PanT;              //transition progress 0..1
    private const float PAN_DURATION = 0.34f; //transition time, smoothstep-eased
    private uint? CameraFocusEntityId; //NPC to focus while a dialog is open, null follows the player
    private float EntityAlphaMul = 1f; //transient alpha multiplier for entity draws, used to fade the spotlit speaker

    //the tile the player last clicked or interacted with, used to tell same-named NPCs apart when resolving the speaker
    //falls back to the player's tile if nothing is recorded yet
    private Point LastInteractTile;
    private bool HasLastInteractTile;

    //records where the player last clicked so the speaker resolver can pick the right same-named NPC
    public void NoteInteractTile(int tileX, int tileY)
    {
        LastInteractTile = new Point(tileX, tileY);
        HasLastInteractTile = true;
    }

    //camera shake
    private float ShakeTimer;
    private float ShakeMagnitude;
    private float ShakePhase;
    private float ShakeOffsetX;
    private const float SHAKE_DURATION = 0.35f;
    private const float SHAKE_FREQ = 52f; //rad/sec oscillation rate

    //death FX while HP is at or below 0, a heart thump on a beat plus a dark edge vignette that pulses with it
    //the server plays the death knell once, the client re-triggers it on a fixed beat while dead
    private bool WasPlayerDead;     //edge detection, arms the beat timer when death starts
    private float HeartbeatTimer;   //counts down to the next heart thump while dead
    private float SecondsDead;      //how long the player has lain dead, drives the creeping reach of the darkness
    private float DeathFxStrength;  //0..1 eased, fades the dark vignette in and out during the dying phase
    private float SpiritFxStrength; //0..1 eased, the softer steady vignette while walking as the claimed spirit
    private float GreyFxStrength;   //0..1 eased, drives the world greyscale and holds through the spirit walk
    private static Texture2D? DeathVignetteTexture;
    private const float HEARTBEAT_PERIOD = 1.1f;        //seconds between thumps, about 55 bpm
    private const float DEATH_FADE_IN_SECONDS = 1.2f;   //how slowly the death darkness settles in
    private const float DEATH_FADE_OUT_SECONDS = 0.5f;  //how quickly it lifts again on revive
    private const float DEATH_VIGNETTE_MIN = 0.8f;      //edge darkness at the resting point between beats
    private const float DEATH_VIGNETTE_MAX = 1f;        //edge darkness at the moment of each thump
    private const float DEATH_PULSE_DECAY = 3.5f;       //how fast each beat's surge relaxes
    private const float SPIRIT_VIGNETTE_ALPHA = 0.45f;  //steady edge darkness while in spirit form

    //the darkness creeps inward as the death timer runs out
    //a clear iris around the player shrinks until almost everything else is swallowed, the heartbeat pulse rides on top
    private const float DEATH_CREEP_SECONDS = 20f; //matched to the server's death timer
    private const float DEATH_IRIS_START = 2.8f;   //iris scale at death, the clear circle starts bigger than the view
    private const float DEATH_IRIS_END = 0.55f;    //fully crept, only a small circle around the player stays clear
    private static Texture2D? DeathIrisTexture;

    //red damage pulse
    private float DamageFlashTimer;   //counts down from FLASH_DURATION on each HP loss
    private float LowHpPulsePhase;     //continuous phase for the sustained low-HP pulse
    private bool LowHpActive;          //true while at or below the low-HP threshold
    private static Texture2D? RedVignetteTexture;
    private const float FLASH_DURATION = 0.55f;
    private const float LOW_HP_PULSE_SPEED = 3.2f; //rad/sec
    //peak alphas come from DebugSettings.DamageFlashMax and LowHpPulseMax

    //start the sustained low-HP pulse when the over-head health bar turns red so the two cues agree
    //shared with HealthBar so they can never drift apart
    private const float LOW_HP_FRACTION = Controls.World.ViewPort.HealthBar.RedThresholdPercent / 100f;

    /// <summary>
    ///     Per-frame camera update tracking the player normally, or the NPC speaker while a dialog is open
    ///     Sits exactly on the target when settled, a change of target runs one fast eased transition
    /// </summary>
    private void UpdateCameraFollow(float dt)
    {
        UpdateCameraShake(dt);

        if (ComputeBaseFollow() is not { } playerPos)
            return;

        //target the NPC speaker while a dialog is open, else the player
        var locked = false;
        var targetPos = playerPos;

        if (ClientSettings.FocusSpeaker
            && NpcSession.Visible
            && (CameraFocusEntityId is { } focusId)
            && (WorldState.GetEntity(focusId) is { } npc))
        {
            locked = true;
            targetPos = Camera.TileToWorld(npc.TileX, npc.TileY, MapFile!.Height) + npc.VisualOffset;
        }

        if (!CamInitialized)
        {
            CamPos = targetPos;
            CamInitialized = true;
            LockedOnSpeaker = locked;
            PanSettled = true;
        }

        //a change of target between player and speaker starts a fresh transition
        if (locked != LockedOnSpeaker)
        {
            LockedOnSpeaker = locked;
            PanStart = CamPos;
            PanT = 0f;
            PanSettled = false;
        }

        if (PanSettled)
            CamPos = targetPos; //exact tracking, crisp follow or locked on the NPC with no lag while the player walks
        else
        {
            //fixed-duration smoothstep transition with real accel and decel
            //the fixed duration bounds the slow ends to about one frame each so there is no sub-pixel crawl at low res
            PanT += dt / PAN_DURATION;

            if (PanT >= 1f)
            {
                CamPos = targetPos;
                PanSettled = true;
            } else
            {
                var eased = PanT * PanT * (3f - 2f * PanT); //smoothstep
                CamPos = Vector2.Lerp(PanStart, targetPos, eased);
            }
        }

        Camera.Position = CamPos + new Vector2(ShakeOffsetX, 0f);
    }

    //true once a dialog's speaker has resolved to a live on-screen entity
    //independent of the host fade so it is valid the instant the dialog opens
    private bool SpeakerResolved
        => ClientSettings.FocusSpeaker && (CameraFocusEntityId is { } id) && (WorldState.GetEntity(id) is not null);

    //true while the dialog speaker should be spotlit, re-drawn bright above the dim
    //gated on the host visibility so it keeps drawing and fading through the dialog's close fade-out
    private bool SpeakerSpotlightActive => SpeakerResolved && (NpcSessionHost is { Visible: true });

    //the camera's base follow position, the local player's smooth-walk position
    private Vector2? ComputeBaseFollow()
    {
        if (MapFile is null)
            return null;

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return null;

        return Camera.TileToWorld(player.TileX, player.TileY, MapFile.Height) + player.VisualOffset;
    }

    //picks the NPC to focus while a dialog is open, the creature whose name matches the speaker nearest the last click
    //a sign or reactor dialog has no matching entity name, so this stays null and the camera keeps following the player
    private void ResolveCameraFocus()
    {
        CameraFocusEntityId = null;

        if (MapFile is null)
            return;

        var speaker = NpcSession.NpcName;

        if (string.IsNullOrWhiteSpace(speaker))
            return;

        speaker = speaker.Trim();

        //reference point is the last click tile, else the player's current tile
        var refTile = LastInteractTile;

        if (!HasLastInteractTile && (WorldState.GetPlayerEntity() is { } self))
            refTile = new Point(self.TileX, self.TileY);

        var refWorld = Camera.TileToWorld(refTile.X, refTile.Y, MapFile.Height);
        var best = float.MaxValue;

        foreach (var entity in WorldState.GetEntities())
        {
            if ((entity.Type != ClientEntityType.Creature) || string.IsNullOrEmpty(entity.Name))
                continue;

            if (!entity.Name.Trim().Equals(speaker, StringComparison.OrdinalIgnoreCase))
                continue;

            var world = Camera.TileToWorld(entity.TileX, entity.TileY, MapFile.Height);
            var distSq = Vector2.DistanceSquared(world, refWorld);

            if (distSq < best)
            {
                best = distSq;
                CameraFocusEntityId = entity.Id;
            }
        }
    }

    private void UpdateCameraShake(float dt)
    {
        if (ShakeTimer <= 0f)
        {
            ShakeOffsetX = 0f;

            return;
        }

        ShakeTimer -= dt;
        ShakePhase += dt * SHAKE_FREQ;
        var decay = Math.Clamp(ShakeTimer / SHAKE_DURATION, 0f, 1f);
        ShakeOffsetX = MathF.Sin(ShakePhase) * ShakeMagnitude * decay;
    }

    //starts a subtle horizontal shake, no-op if the camera-shake option is off
    //magnitude is already in screen pixels
    private void TriggerCameraShake(float magnitude)
    {
        if (ClientSettings.CameraShake <= 0f)
            return;

        ShakeTimer = SHAKE_DURATION;
        ShakeMagnitude = Math.Clamp(magnitude * (ClientSettings.CameraShake / 100f), 0.1f, 6f);
    }

    /// <summary>
    ///     Fires the shake and red flash on an HP drop and tracks the low-HP pulse state
    ///     <paramref name="previous" /> is -1 on the first seeding packet
    /// </summary>
    private void OnPlayerHealthChanged(long previous, long current, long max)
    {
        if (max > 0)
            LowHpActive = current <= (long)MathF.Ceiling(max * LOW_HP_FRACTION);

        if ((previous < 0) || (current >= previous))
            return; //first packet, or HP did not drop

        var frac = max > 0 ? (float)(previous - current) / max : 0.15f;
        TriggerCameraShake(2f + (6f * frac)); //subtle, about 2px base scaling a little with the size of the hit

        if (ClientSettings.CameraEffects > 0f)
            DamageFlashTimer = FLASH_DURATION;
    }

    private void UpdateCameraEffects(float dt)
    {
        if (DamageFlashTimer > 0f)
            DamageFlashTimer = Math.Max(0f, DamageFlashTimer - dt);

        if (LowHpActive && ClientSettings.CameraEffects > 0f)
            LowHpPulsePhase += dt * LOW_HP_PULSE_SPEED;
    }

    /// <summary>
    ///     Eases the death darkness in and out and, while dead, re-triggers the heart thump on a fixed beat
    ///     The server's knell at the moment of death is beat zero, so the timer starts at a full period
    /// </summary>
    private void UpdateDeathFx(float dt)
    {
        var dead = IsPlayerDead;

        if (dead != WasPlayerDead)
        {
            WasPlayerDead = dead;

            if (dead)
                HeartbeatTimer = HEARTBEAT_PERIOD;
        }

        if (dead)
        {
            SecondsDead += dt;
            DeathFxStrength = Math.Min(1f, DeathFxStrength + (dt / DEATH_FADE_IN_SECONDS));
            HeartbeatTimer -= dt;

            if (HeartbeatTimer <= 0f)
            {
                HeartbeatTimer += HEARTBEAT_PERIOD;

                //only thump while the world is actually shown, not mid map-load, unloading, or reconnecting
                //the timer keeps its rhythm regardless so the beat resumes cleanly once the world is back
                if (MapPreloaded && !IsUnloaded && !Reconnecting)
                    Game.SoundSystem.PlaySound(SoundSystem.SoundHeartbeat);
            }
        } else
        {
            SecondsDead = 0f;
            DeathFxStrength = Math.Max(0f, DeathFxStrength - (dt / DEATH_FADE_OUT_SECONDS));
        }

        //the spirit walk gets its own steady vignette, softer than the dying darkness with no heartbeat pulse
        var spirit = !dead && IsPlayerSpirit;

        SpiritFxStrength = spirit
            ? Math.Min(1f, SpiritFxStrength + (dt / DEATH_FADE_IN_SECONDS))
            : Math.Max(0f, SpiritFxStrength - (dt / DEATH_FADE_OUT_SECONDS));

        //the greyscale outlasts the dying phase and holds while the player is the claimed spirit
        //the world regains color only once alive and restored to the body
        var grey = dead || IsPlayerSpirit;

        GreyFxStrength = grey
            ? Math.Min(1f, GreyFxStrength + (dt / DEATH_FADE_IN_SECONDS))
            : Math.Max(0f, GreyFxStrength - (dt / DEATH_FADE_OUT_SECONDS));

        //the world blit draws through the desaturation effect, reset to full color in UnloadContent so the lobby is never affected
        ChaosGame.WorldSaturation = 1f - GreyFxStrength;
    }

    //resets the shake and flashes on a map change so nothing carries across a warp
    private void ResetCameraFx()
    {
        ShakeTimer = 0f;
        ShakeOffsetX = 0f;
        DamageFlashTimer = 0f;
    }

    /// <summary>
    ///     Draws the red screen-edge pulse over the world, a no-op when the option is off or nothing is active
    /// </summary>
    /// <summary>
    ///     Draws the death darkness, a heavy black edge vignette that surges with each heart thump
    ///     Not tied to the camera-effects option since being dead must be unmissable, lingers through the revive fade-out
    /// </summary>
    private void DrawDeathVignette(SpriteBatchEx spriteBatch)
    {
        var alpha = 0f;

        if (DeathFxStrength > 0.003f)
        {
            //how far into the current beat we are, 0 is the thump just hit and 1 is the next due, surge then relax
            var t = Math.Clamp((HEARTBEAT_PERIOD - HeartbeatTimer) / HEARTBEAT_PERIOD, 0f, 1f);
            var pulse = MathF.Exp(-DEATH_PULSE_DECAY * t);
            alpha = (DEATH_VIGNETTE_MIN + ((DEATH_VIGNETTE_MAX - DEATH_VIGNETTE_MIN) * pulse)) * DeathFxStrength;
        }

        //the spirit walk's steady vignette, max so the handoff blends instead of stacking
        if (SpiritFxStrength > 0.003f)
            alpha = Math.Max(alpha, SPIRIT_VIGNETTE_ALPHA * SpiritFxStrength);

        if (alpha <= 0.003f)
            return;

        //the base vignette, full screen and pulsing with the heartbeat
        spriteBatch.Draw(
            EnsureDeathVignette(spriteBatch.GraphicsDevice),
            new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight),
            Color.White * alpha);

        //the iris that creeps closed as the death timer runs out, a clear circle around the player that shrinks
        //it starts fully outside the view so the start is just the normal vignette, the spirit walk does not creep
        var creep = Math.Clamp(SecondsDead / DEATH_CREEP_SECONDS, 0f, 1f);
        creep = creep * creep * (3f - (2f * creep)); //smoothstep, eases in and lands softly at full closure

        if (creep > 0.001f)
        {
            var side = (int)(Math.Max(ChaosGame.UiWidth, ChaosGame.UiHeight) * (DEATH_IRIS_START + ((DEATH_IRIS_END - DEATH_IRIS_START) * creep)));
            var iris = new Rectangle((ChaosGame.UiWidth - side) / 2, (ChaosGame.UiHeight - side) / 2, side, side);
            var bandColor = Color.Black * alpha;

            //solid bands seal the screen outside the iris rect, its rim is fully opaque so they join clean
            if (iris.Top > 0)
                RenderHelper.DrawRect(spriteBatch, new Rectangle(0, 0, ChaosGame.UiWidth, iris.Top), bandColor);

            if (iris.Bottom < ChaosGame.UiHeight)
                RenderHelper.DrawRect(spriteBatch, new Rectangle(0, iris.Bottom, ChaosGame.UiWidth, ChaosGame.UiHeight - iris.Bottom), bandColor);

            if (iris.Left > 0)
                RenderHelper.DrawRect(spriteBatch, new Rectangle(0, iris.Top, iris.Left, iris.Height), bandColor);

            if (iris.Right < ChaosGame.UiWidth)
                RenderHelper.DrawRect(spriteBatch, new Rectangle(iris.Right, iris.Top, ChaosGame.UiWidth - iris.Right, iris.Height), bandColor);

            spriteBatch.Draw(EnsureDeathIris(spriteBatch.GraphicsDevice), iris, Color.White * alpha);
        }
    }

    //a radial iris, clear inside and feathering to fully opaque black before the rim so the solid bands join with no seam
    private static Texture2D EnsureDeathIris(GraphicsDevice device)
    {
        if (DeathIrisTexture is not null)
            return DeathIrisTexture;

        const int SIZE = 256;
        const float CLEAR_R = 0.5f;  //fraction of the half-size that stays clear
        const float SOLID_R = 0.92f; //fully black from here out
        var pixels = new Color[SIZE * SIZE];
        var centre = (SIZE - 1) / 2f;

        for (var y = 0; y < SIZE; y++)
            for (var x = 0; x < SIZE; x++)
            {
                var dx = x - centre;
                var dy = y - centre;
                var d = MathF.Sqrt((dx * dx) + (dy * dy)) / centre;
                var t = Math.Clamp((d - CLEAR_R) / (SOLID_R - CLEAR_R), 0f, 1f);
                t = t * t * (3f - 2f * t); //smoothstep feather
                pixels[y * SIZE + x] = new Color(0f, 0f, 0f, t);
            }

        DeathIrisTexture = new Texture2D(device, SIZE, SIZE);
        DeathIrisTexture.SetData(pixels);

        return DeathIrisTexture;
    }

    //a premultiplied radial black vignette built once, clear centre deepening to dark at the edges
    //the death darkness reaches much further in than the red damage pulse does
    private static Texture2D EnsureDeathVignette(GraphicsDevice device)
    {
        if (DeathVignetteTexture is not null)
            return DeathVignetteTexture;

        const int SIZE = 256;
        const float INNER = 0.22f; //fraction of the radius that stays clear before the darkness ramps in
        var pixels = new Color[SIZE * SIZE];
        var centre = (SIZE - 1) / 2f;
        var maxDist = MathF.Sqrt(2f) * centre;

        for (var y = 0; y < SIZE; y++)
            for (var x = 0; x < SIZE; x++)
            {
                var dx = x - centre;
                var dy = y - centre;
                var d = MathF.Sqrt((dx * dx) + (dy * dy)) / maxDist; //0 at centre, 1 at corner
                var t = Math.Clamp((d - INNER) / (1f - INNER), 0f, 1f);
                t = t * t * (3f - 2f * t); //smoothstep
                t = MathF.Pow(t, 0.8f);    //thicken the mid-ring so the darkness presses further toward the centre
                pixels[y * SIZE + x] = new Color(0f, 0f, 0f, t); //premultiplied black
            }

        DeathVignetteTexture = new Texture2D(device, SIZE, SIZE);
        DeathVignetteTexture.SetData(pixels);

        return DeathVignetteTexture;
    }

    //true while the local player's entity carries the dead flag, the claimed spirit walking
    private static bool IsPlayerSpirit => WorldState.GetPlayerEntity() is { IsDead: true };

    private void DrawCameraEffects(SpriteBatchEx spriteBatch)
    {
        //no red while dead or in spirit form, the death darkness and greyscale own the screen
        //the 1-HP spirit would otherwise sit in the sustained low-HP pulse for its whole walk
        if (IsPlayerDead || IsPlayerSpirit)
            return;

        if (ClientSettings.CameraEffects <= 0f)
            return;

        var scale = ClientSettings.CameraEffects / 100f;
        var flash = (DamageFlashTimer / FLASH_DURATION) * DebugSettings.DamageFlashMax * scale;
        var lowHp = LowHpActive ? ((0.5f + (0.5f * MathF.Sin(LowHpPulsePhase))) * DebugSettings.LowHpPulseMax * scale) : 0f;
        var intensity = Math.Max(flash, lowHp);

        if (intensity <= 0.003f)
            return;

        spriteBatch.Draw(
            EnsureRedVignette(spriteBatch.GraphicsDevice),
            new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight),
            Color.White * intensity);
    }

    //a premultiplied radial red vignette built once, clear centre deepening to red at the edges
    //stretched to the window each draw and tinted by the live pulse intensity
    private static Texture2D EnsureRedVignette(GraphicsDevice device)
    {
        if (RedVignetteTexture is not null)
            return RedVignetteTexture;

        const int SIZE = 256;
        const float INNER = 0.55f; //fraction of the radius that stays clear before the red ramps in
        var pixels = new Color[SIZE * SIZE];
        var centre = (SIZE - 1) / 2f;
        var maxDist = MathF.Sqrt(2f) * centre;

        for (var y = 0; y < SIZE; y++)
            for (var x = 0; x < SIZE; x++)
            {
                var dx = x - centre;
                var dy = y - centre;
                var d = MathF.Sqrt((dx * dx) + (dy * dy)) / maxDist; //0 at centre, 1 at corner
                var t = Math.Clamp((d - INNER) / (1f - INNER), 0f, 1f);
                t = t * t * (3f - 2f * t); //smoothstep
                pixels[y * SIZE + x] = new Color(t, 0f, 0f, t); //premultiplied red
            }

        RedVignetteTexture = new Texture2D(device, SIZE, SIZE);
        RedVignetteTexture.SetData(pixels);

        return RedVignetteTexture;
    }
}
