#region
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Chaos.Client.Collections;
using DALib.Utility;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Networking;
using Chaos.Client.Networking.Definitions;
using Chaos.Client.Rendering.Utility;
using Chaos.Client.Screens;
using Chaos.Client.Systems;
using Chaos.Client.Utilities;
using Chaos.Cryptography;
using Chaos.DarkAges.Definitions;
using Chaos.Networking.Entities.Server;
using DALib.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SkiaSharp;
#endregion

namespace Chaos.Client;

public sealed class ChaosGame : Game
{
    public const int VIRTUAL_WIDTH = 640;
    public const int VIRTUAL_HEIGHT = 480;
    private const float ASPECT_RATIO = (float)VIRTUAL_WIDTH / VIRTUAL_HEIGHT;

    //native backbuffer size, refreshed each frame. The new UI anchors to these so it renders pixel-perfect
    //at the window's real resolution, while the world stays a 640x480 target stretched to fill the window.
    public static int UiWidth { get; private set; } = VIRTUAL_WIDTH * 2;
    public static int UiHeight { get; private set; } = VIRTUAL_HEIGHT * 2;

    //where the world target is drawn in the backbuffer. Native screens fill the whole window; legacy screens are
    //best-fit 4:3 (letterboxed, black bars). The UI overlays the whole window.
    public static Rectangle WorldDrawRect { get; private set; } = new(0, 0, VIRTUAL_WIDTH * 2, VIRTUAL_HEIGHT * 2);

    //the world render target's size. For native screens this EXPANDS to fill the window (extra map tiles render in
    //what would be the letterbox bars); legacy screens keep VIRTUAL_WIDTH x VIRTUAL_HEIGHT. Recomputed each frame.
    public static int WorldRenderWidth { get; private set; } = VIRTUAL_WIDTH;
    public static int WorldRenderHeight { get; private set; } = VIRTUAL_HEIGHT;

    private readonly GraphicsDeviceManager Graphics;
    private readonly string MetaFilePath = Path.Combine(GlobalSettings.DataPath, "metafile");
    private readonly Dictionary<string, uint> MetaPendingChecksums = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServerPacket> PacketBuffer = [];
    private int CursorOffsetX;
    private int CursorOffsetY;
    private Texture2D? CursorTexture;
    internal volatile bool GcRequested;
    internal bool ScreenshotRequested; //WorldScreen captures the frame mid-UI-pass (after bubbles, before the HUD)

    //dev --screenoutput: an accumulating timer used to dump the composited frame (world + UI) to a file every
    //LaunchArgs.ScreenOutputInterval seconds so an external watcher can poll one stable path.
    private double ScreenOutputTimer;
    private int HandCursorOffsetX;
    private int HandCursorOffsetY;
    private Texture2D? HandCursorTexture;

    //tiling stone texture drawn behind the 4:3 legacy screens (lobby / char create) so the letterbox is not black bars
    private Texture2D? LobbyBackgroundTexture;

    //the stone texture only tiles SIDEWAYS, so wrap horizontally (repeat) and clamp vertically (one tile covers height)
    private static readonly SamplerState TileHorizontalSampler = new()
    {
        AddressU = TextureAddressMode.Wrap,
        AddressV = TextureAddressMode.Clamp,
        Filter = TextureFilter.Linear
    };
    private bool MetaSyncStarted;
    private RenderTarget2D RenderTarget = null!;

    /// <summary>The world (low-res) render target, made available so the deferred lighting can multiply the light buffer
    /// into it mid-world-pass (it is PreserveContents, so the albedo survives the detour).</summary>
    public RenderTarget2D WorldTarget => RenderTarget;

    //intermediate target for sharp-bilinear world scaling: the low-res world target integer-upscaled to the smallest
    //whole multiple >= the window, then bilinear-downscaled to the window (kills the uneven-pixel shimmer at non-integer
    //scales without blurring the art). Only allocated when a non-integer scale needs it; sized to a multiple of RenderTarget.
    private RenderTarget2D? ScaledTarget;

    //death greyscale: the world blit draws through this desaturation effect whenever WorldSaturation drops below 1
    //(the world screen's death FX eases it toward 0 while the player is dead). The UI on top stays colored.
    //Pre-compiled sprite effect embedded as desaturate.mgfxo (source Assets/Desaturate.fx).
    public static float WorldSaturation = 1f;
    private Effect DesaturateEffect = null!;
    private EffectParameter DesaturateParam = null!;

    //the quick fade-through-black shown between map changes. WorldScreen drives the phases (BeginFadeOut on a map-change,
    //BeginFadeIn when the new map is ready); ChaosGame advances + draws it in Draw. FreezeTarget holds a one-shot snapshot
    //of the world frame captured when the fade-out begins, so the OLD map can fade out even though it stops rendering the
    //instant the change is pending (see the capture in Draw).
    public MapTransition MapTransition { get; } = new();
    private RenderTarget2D? FreezeTarget;

    private bool ResizingInProgress;
    private int WindowSizeMultiplier = 2; // default to 2x (1280x960); Alt+Enter cycles from here
    private SpriteBatchEx SpriteBatch = null!;

    /// <summary>
    ///     Input dispatcher that routes mouse and keyboard events to UI elements via hit-testing and focus routing.
    /// </summary>
    public InputDispatcher Dispatcher { get; private set; } = null!;

    /// <summary>
    ///     The screen manager that owns the active screen stack.
    /// </summary>
    public ScreenManager Screens { get; private set; } = null!;

    public bool UseHandCursor { get; set; }

    /// <summary>
    ///     Shared aisling renderer for compositing player/NPC equipment layers.
    /// </summary>
    public AislingRenderer AislingRenderer { get; } = new();

    /// <summary>
    ///     The connection manager that orchestrates lobby, login, and world connections.
    /// </summary>
    public ConnectionManager Connection { get; }

    /// <summary>
    ///     Shared creature sprite renderer with per-frame texture cache.
    /// </summary>
    public CreatureRenderer CreatureRenderer { get; } = new();

    /// <summary>
    ///     Shared spell/effect animation renderer with per-frame texture cache.
    /// </summary>
    public EffectRenderer EffectRenderer { get; } = new();

    /// <summary>
    ///     Shared item sprite renderer with frame offset metadata. Evicted on map change.
    /// </summary>
    public ItemRenderer ItemRenderer { get; } = new();

    /// <summary>
    ///     Manages sound effect and music playback.
    /// </summary>
    public SoundSystem SoundSystem { get; } = new();

    public static GraphicsDevice Device => TextureConverter.Device;

    /// <summary>
    ///     The game time as reported by the last Update().
    /// </summary>
    public static GameTime GameTime { get; private set; } = new GameTime();

    public ChaosGame()
    {
        //sdl by default is polling all possible input devices
        //some devices apparently don't like to always respond in a timely manner
        //when this occurs it causes the entire application to hang
        //to remedy this, we use this to disable polling of extraneous devices
        Sdl.SDL_QuitSubSystem(
            Sdl.SDL_INIT_JOYSTICK
            | Sdl.SDL_INIT_GAMECONTROLLER
            | Sdl.SDL_INIT_HAPTIC
            | Sdl.SDL_INIT_SENSOR);

        ClientSettings.Load();
        WarpData.Load();

        //apply persisted volumes (default 50%) so the Options sliders' saved values take effect from boot
        SoundSystem.SetSoundVolume(ClientSettings.SoundVolume);
        SoundSystem.SetMusicVolume(ClientSettings.MusicVolume);
        SoundSystem.SetFootstepVolume(ClientSettings.FootstepVolume);
        SoundSystem.SetChatVolume(ClientSettings.ChatBubbleVolume);
        SoundSystem.SetWhisperVolume(ClientSettings.WhisperVolume);

        //push the persisted behind-walls opacity into the renderer (the slider also sets it live)
        Rendering.SilhouetteRenderer.SilhouetteAlpha = ClientSettings.SilhouetteAlpha;

        Graphics = new GraphicsDeviceManager(this)
        {
            //wider-than-4:3 default so the chat window fits comfortably in the lower-left; the window is freely resizable
            PreferredBackBufferWidth = 1706,
            PreferredBackBufferHeight = 960,
            PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8,
            SynchronizeWithVerticalRetrace = ClientSettings.VSync //VSync: run at the monitor's refresh rate, no tearing
        };

        //a fractional-scale UI window/hotbar (ScaleHost.DrawSharpBilinear) renders to an offscreen target and rebinds
        //the backbuffer mid-frame; PreserveContents keeps the world + UI already drawn this frame when we come back
        //(the default DiscardContents would drop everything painted before the first scaled window). The frame is
        //cleared every Draw regardless, so this only adds the backbuffer-preserve, no ghosting.
        Graphics.PreparingDeviceSettings += (_, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;

        //run at the display refresh (driven by VSync) by default instead of a hard 60fps lock. Game logic is
        //time-based (AnimationTick from TotalGameTime, controls use ElapsedGameTime deltas), so the rate only
        //affects smoothness. The optional Max Framerate cap (Options) applies a fixed timestep; 0 = unlimited.
        SetMaxFramerate(ClientSettings.MaxFramerate);
        InactiveSleepTime = TimeSpan.Zero;

        Connection = new ConnectionManager();
        Directory.CreateDirectory(MetaFilePath);
        Connection.OnMetaData += HandleMetaData;
        Connection.OnWorldEntryComplete += () => Connection.SendMetaDataRequest(MetaDataRequestType.AllCheckSums);
        Connection.StateChanged += OnConnectionStateChanged;

        //wire state events to worldstate at startup so state is tracked
        //even during world entry (before worldscreen is created)
        WorldState.SubscribeTo(Connection);
        Connection.OnDisplayVisibleEntities += WorldState.AddOrUpdateVisibleEntities;
        Connection.OnDisplayAisling += WorldState.AddOrUpdateAisling;

        //removeentity wired in worldscreen, it needs to capture the creature sprite for
        //the death dissolve animation before removing the entity from worldstate.
        //fallback for non-world screens (e.g., during world entry before worldscreen exists).
        Connection.OnRemoveEntity += id =>
        {
            if (Screens.ActiveScreen is not WorldScreen)
                WorldState.RemoveEntity(id);
        };

        Connection.OnCreatureWalk += (
            id,
            oldX,
            oldY,
            dir) =>
        {
            var entity = WorldState.GetEntity(id);
            var walkFrames = entity is not null && (entity.SpriteId > 0) ? CreatureRenderer.GetWalkFrameCount(entity.SpriteId) : null;

            WorldState.HandleCreatureWalk(
                id,
                oldX,
                oldY,
                dir,
                walkFrames);
        };
        Connection.OnCreatureTurn += (id, dir) => WorldState.HandleCreatureTurn(id, dir);

        Window.Title = "Sweden Mayhem";
        Window.AllowUserResizing = true;
        IsMouseVisible = true;
    }

    protected override void Draw(GameTime gameTime)
    {
        var screen = Screens.ActiveScreen;
        var native = screen?.UsesNativeUi == true;

        //work out the world render size + where it lands in the window BEFORE pass 1 (the target must be the right
        //size before we render into it). Same best-fit scale as before, so tiles keep their exact on-screen size.
        var bb = GraphicsDevice.Viewport.Bounds;
        UiWidth = bb.Width;
        UiHeight = bb.Height;
        var fit = Math.Min((float)bb.Width / VIRTUAL_WIDTH, (float)bb.Height / VIRTUAL_HEIGHT);

        //scale the effective interface scale when set to automatic, or ensure it's fixed otherwise
        {
            var interfaceScale = ClientSettings.InterfaceScale;
    
            if (interfaceScale < 1)
            {
                interfaceScale = Math.Min(bb.Width/1280f, bb.Height/850f);
    
                //avoid scaling to 1.25. If too close round to the nearest half size instead, otherwise scale to the nearest quarter
                if (Math.Abs(interfaceScale - 1.25) <= 0.125)
                    interfaceScale = MathF.Round(interfaceScale / 0.5f) * 0.5f;
                else
                    interfaceScale = MathF.Round(interfaceScale / 0.25f) * 0.25f;
            } else
                interfaceScale = ClientSettings.InterfaceScale;
    
            if (ClientSettings.EffectiveInterfaceScale != interfaceScale)
            {
                ClientSettings.EffectiveInterfaceScale = interfaceScale;
                ClientSettings.NotifyEffectiveWindowScaleChanged();
            }
        }

        if (native)
        {
            //EXPAND the world target to fill the whole window at the same scale: the centered 640x480 region is
            //identical to the old letterboxed view, and what used to be black bars now renders extra map tiles
            //(entities still only appear where the server sent them, so the margins show ground/foreground only).
            //round to EVEN: the camera centre is ViewportWidth/2, so an ODD width lands the centre on a half-pixel,
            //which rounds adjacent map tiles to a 1px gap and shows as faint vertical seams across the world.
            var renderW = Math.Max(VIRTUAL_WIDTH, (int)Math.Round(bb.Width / fit));
            var renderH = Math.Max(VIRTUAL_HEIGHT, (int)Math.Round(bb.Height / fit));
            WorldRenderWidth = renderW + (renderW & 1);
            WorldRenderHeight = renderH + (renderH & 1);
            WorldDrawRect = new Rectangle(0, 0, bb.Width, bb.Height);
        } else
        {
            //legacy screens (lobby) keep the fixed 640x480 target, best-fit 4:3 with black bars
            WorldRenderWidth = VIRTUAL_WIDTH;
            WorldRenderHeight = VIRTUAL_HEIGHT;
            var fw = (int)(VIRTUAL_WIDTH * fit);
            var fh = (int)(VIRTUAL_HEIGHT * fit);
            WorldDrawRect = new Rectangle((bb.Width - fw) / 2, (bb.Height - fh) / 2, fw, fh);
        }

        //(re)create the world target only when its size actually changes (window resize / screen switch)
        if ((RenderTarget.Width != WorldRenderWidth) || (RenderTarget.Height != WorldRenderHeight))
        {
            RenderTarget.Dispose();

            //PreserveContents so the map-change fade can capture the last rendered (old-map) frame at the top of the
            //next Draw, before this target is cleared - it retains its pixels across frames (the per-frame Clear means
            //this adds no ghosting, only a reliable freeze source).
            RenderTarget = new RenderTarget2D(
                GraphicsDevice,
                WorldRenderWidth,
                WorldRenderHeight,
                false,
                SurfaceFormat.HalfVector4, //16-bit/channel - MUST match the LoadContent target, else the resize path silently drops the world back to 8-bit and the day/night fade bands again
                DepthFormat.Depth24Stencil8,
                0,
                RenderTargetUsage.PreserveContents);
        }

        var pixelScale = (float)bb.Width / (float)WorldRenderWidth;

        //scale the mouse cursor so it also matches the pixel scale (but bias down by .4 as larger resolutions expect
        //slightly smaller scale cursors)
        OsCursor.SetTexturedCursorScale((int)Math.Round(pixelScale - 0.4f));
        OsCursor.SetHand(UseHandCursor);

        //advance the map-change fade and, if a fade-out just began, snapshot the world frame BEFORE we clear the target
        //for this frame. RenderTarget still holds the last rendered (old) map here; the map-change handler has already
        //set MapPreloaded=false, so DrawWorld below would render nothing - capturing now is the only chance to freeze it.
        MapTransition.Update((float)gameTime.ElapsedGameTime.TotalSeconds);

        if (native && MapTransition.ConsumeCaptureRequest())
            CaptureWorldFreeze();

        //pass 1: the world (low-res) into the target. Cleared TRANSPARENT so the per-pixel ALPHA is the map-coverage mask
        //the night-shadow grade keys on: drawn floor + ALL foreground sprites (walls/arches rising above the floor diamond)
        //are alpha 1, the off-map void is alpha 0 (never tinted). The one gap is the transparent HOLES some floor sprites
        //have - those are filled by stamping the map's floor parallelogram into the alpha in ApplyNightShadowTint.
        GraphicsDevice.SetRenderTarget(RenderTarget);
        GraphicsDevice.Clear(Color.Transparent);
        screen?.DrawWorld(SpriteBatch, gameTime);

        if (DebugOverlay.IsActive)
            DebugOverlay.DrawStats(SpriteBatch);

        //the album screenshot is captured later, in WorldScreen's UI pass (after the world + chat bubbles are on the
        //backbuffer but BEFORE the HUD), so the shot is the world + bubbles with no interface on it. See CaptureWorldFrame.

        //sharp-bilinear world scaling (native only). A non-integer window scale makes nearest-neighbour stretch some
        //source pixels across 2 screen pixels and others across 3, so a few columns/rows look "squished" and shimmer as
        //the camera moves - it shows even near 2x because WorldRenderWidth is rounded to even (so e.g. 1706/854 = 1.9977x,
        //not exactly 2x). Fix: integer-upscale the crisp target to the smallest whole multiple >= the window (still
        //nearest, so pixel INTERIORS stay crisp), then bilinear-DOWNscale that to the window, which softens only the thin
        //pixel seams. At an exact integer scale this is skipped (the direct nearest blit is already perfect).
        var sharpScale = false;

        if (native)
        {
            var scaleX = (float)WorldDrawRect.Width / RenderTarget.Width;
            var scaleY = (float)WorldDrawRect.Height / RenderTarget.Height;

            if (!IsNearInteger(scaleX) || !IsNearInteger(scaleY))
            {
                var k = (int)Math.Ceiling(Math.Max(scaleX, scaleY));
                var sw = RenderTarget.Width * k;
                var sh = RenderTarget.Height * k;

                if ((ScaledTarget is null) || (ScaledTarget.Width != sw) || (ScaledTarget.Height != sh))
                {
                    ScaledTarget?.Dispose();
                    ScaledTarget = new RenderTarget2D(GraphicsDevice, sw, sh, false, SurfaceFormat.HalfVector4, DepthFormat.None);
                }

                //crisp integer upscale of the world target (nearest). Opaque copy: it covers the whole ScaledTarget, and
                //the world target's void is now transparent - an alpha blend onto the uncleared ScaledTarget would ghost
                //the void with last frame, so replace pixels outright.
                GraphicsDevice.SetRenderTarget(ScaledTarget);
                SpriteBatch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp);
                SpriteBatch.Draw(RenderTarget, new Rectangle(0, 0, sw, sh), Color.White);
                SpriteBatch.End();

                sharpScale = true;
            }
        }

        //draw the world target into the window: full window for native (no bars), letterboxed for legacy
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);

        //legacy screens are best-fit 4:3, so fill what would be the black bars with the stone texture. It only tiles
        //sideways, so cover the full window HEIGHT with one tile and repeat HORIZONTALLY: a source rect exactly one
        //tile tall, but wide enough (wrap-sampled in U) to span the window. X and Y land on the same scale, so the
        //stones are not stretched. Native screens fill the window and skip this.
        if (!native && (LobbyBackgroundTexture is not null))
        {
            var srcWidth = (int)Math.Ceiling(bb.Width * (double)LobbyBackgroundTexture.Height / bb.Height);
            SpriteBatch.Begin(samplerState: TileHorizontalSampler);
            SpriteBatch.Draw(
                LobbyBackgroundTexture,
                new Rectangle(0, 0, bb.Width, bb.Height),
                new Rectangle(0, 0, srcWidth, LobbyBackgroundTexture.Height),
                Color.White);
            SpriteBatch.End();
        }

        //base world layer. The fade-to-black out/hold phases REPLACE the live world with the frozen old-map snapshot
        //(the live new map is not ready / must not show yet); every other case draws the live world as normal.
        if (native && MapTransition.ShowFrozenAsBase && (FreezeTarget is not null))
        {
            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler, effect: WorldBlitEffect());
            SpriteBatch.Draw(FreezeTarget, WorldDrawRect, Color.White);
            SpriteBatch.End();
        } else if (sharpScale)
        {
            //bilinear downscale of the integer-upscaled target -> only the pixel seams blend, no overall blur
            SpriteBatch.Begin(samplerState: SamplerState.LinearClamp, effect: WorldBlitEffect());
            SpriteBatch.Draw(ScaledTarget!, WorldDrawRect, Color.White);
            SpriteBatch.End();
        } else
        {
            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler, effect: WorldBlitEffect());
            SpriteBatch.Draw(RenderTarget, WorldDrawRect, Color.White);
            SpriteBatch.End();
        }

        //cross-fade: the frozen OLD map drawn OVER the live new map, dissolving out (alpha 1 -> 0) - no black flash
        if (native && (MapTransition.FrozenOverlayAlpha > 0f) && (FreezeTarget is not null))
        {
            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler, effect: WorldBlitEffect());
            SpriteBatch.Draw(FreezeTarget, WorldDrawRect, Color.White * MapTransition.FrozenOverlayAlpha);
            SpriteBatch.End();
        }

        //fade-to-black style: a black overlay over the world (under the UI, so menus/chat stay visible during a warp)
        if (native && (MapTransition.BlackAlpha > 0f))
        {
            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            RenderHelper.DrawRect(SpriteBatch, WorldDrawRect, Color.Black * MapTransition.BlackAlpha);
            SpriteBatch.End();
        }

        //pass 2: the UI at native window resolution, on top of the world (no-op for legacy screens)
        screen?.DrawNativeUi(SpriteBatch, gameTime);

        //global screen fade-to-black, over EVERYTHING (world/lobby + UI + cursor): lobby transitions + login->world handoff.
        //The drawn alpha is smoothstep-eased (the timing logic in UpdateScreenFade stays linear - 0 and 1 map to themselves,
        //so the at-black action and the held-black endpoints are unaffected), which reads calmer than a linear ramp.
        if (FadeAlpha > 0f)
        {
            var eased = FadeAlpha * FadeAlpha * (3f - (2f * FadeAlpha));

            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            RenderHelper.DrawRect(SpriteBatch, new Rectangle(0, 0, bb.Width, bb.Height), Color.Black * eased);
            SpriteBatch.End();
        }

        //world-map travel cutscene: a full-screen splash drawn ABOVE everything (even the screen fade). Held opaque,
        //then faded out to reveal the player at the new location. The letterbox bars use the same horizontally-tiled
        //lobby/creation background; the splash itself is drawn NEAREST (crisp), like the login art.
        if ((TravelAlpha > 0f) && (TravelImage is not null))
        {
            var tint = Color.White * TravelAlpha;

            //backdrop: the lobby stone background tiled horizontally (same as the lobby/creation bars), else black
            if (LobbyBackgroundTexture is not null)
            {
                var srcWidth = (int)Math.Ceiling(bb.Width * (double)LobbyBackgroundTexture.Height / bb.Height);
                SpriteBatch.Begin(samplerState: TileHorizontalSampler);
                SpriteBatch.Draw(
                    LobbyBackgroundTexture,
                    new Rectangle(0, 0, bb.Width, bb.Height),
                    new Rectangle(0, 0, srcWidth, LobbyBackgroundTexture.Height),
                    tint);
                SpriteBatch.End();
            } else
            {
                SpriteBatch.Begin(samplerState: GlobalSettings.Sampler);
                RenderHelper.DrawRect(SpriteBatch, new Rectangle(0, 0, bb.Width, bb.Height), Color.Black * TravelAlpha);
                SpriteBatch.End();
            }

            //the splash, letterboxed, nearest-neighbour (matches the login screen; no bilinear blur)
            SpriteBatch.Begin(samplerState: GlobalSettings.Sampler);
            var scale = MathF.Min(bb.Width / (float)TravelImage.Width, bb.Height / (float)TravelImage.Height);
            var dw = (int)(TravelImage.Width * scale);
            var dh = (int)(TravelImage.Height * scale);
            SpriteBatch.Draw(TravelImage, new Rectangle((bb.Width - dw) / 2, (bb.Height - dh) / 2, dw, dh), tint);
            SpriteBatch.End();
        }

        base.Draw(gameTime);

        DebugOverlay.EndFrame();

        //dev --screenoutput: dump the composited frame to a file every N seconds (the backbuffer is fully drawn here,
        //before EndDraw presents it). GetBackBufferData stalls the GPU, so it is interval-gated, never per-frame.
        if (LaunchArgs.ScreenOutput)
        {
            ScreenOutputTimer += gameTime.ElapsedGameTime.TotalSeconds;

            if (ScreenOutputTimer >= LaunchArgs.ScreenOutputInterval)
            {
                ScreenOutputTimer = 0;
                CaptureScreenOutput();
            }
        }
    }

    //writes the current backbuffer (world + UI) to LaunchArgs.ScreenOutputPath as a plain PNG. Temp-file-then-move so a
    //watcher polling the path never reads a half-written file. Best-effort: swallows IO/driver hiccups.
    private void CaptureScreenOutput()
    {
        var path = LaunchArgs.ScreenOutputPath;

        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            using var image = ReadBackbuffer();

            if (image is null)
                return;

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var tmp = path + ".tmp";

            using (var fs = File.Create(tmp))
                data.SaveTo(fs);

            File.Move(tmp, path, true);
        } catch
        {
            //best-effort dump; ignore transient failures (file briefly locked by the reader, driver readback hiccup)
        }
    }

    //true when the scale is (essentially) a whole number, so nearest-neighbour stretches every source pixel evenly and
    //the sharp-bilinear pass is unnecessary
    private static bool IsNearInteger(float v) => Math.Abs(v - (float)Math.Round(v)) < 0.001f;

    //the world-blit effect carries ONLY the death greyscale now (the 16-bit world chain handles the day/night fade with
    //no dither). So skip it entirely on the normal path - a null effect is a plain blit. It only kicks in while the
    //player is dead and WorldSaturation has eased below 1.
    private Effect? WorldBlitEffect()
    {
        if (WorldSaturation >= 1f)
            return null;

        DesaturateParam.SetValue(Math.Clamp(WorldSaturation, 0f, 1f));

        return DesaturateEffect;
    }

    //snapshots the current world RenderTarget (the last rendered map frame) into FreezeTarget, so the map-change fade can
    //show the old map fading out even after DrawWorld stops rendering it. Called once at the start of a fade-out, before
    //the target is cleared for the new (loading) frame. If the freeze can't be captured the fade just starts from black.
    private void CaptureWorldFreeze()
    {
        var w = RenderTarget.Width;
        var h = RenderTarget.Height;

        if ((FreezeTarget is null) || (FreezeTarget.Width != w) || (FreezeTarget.Height != h))
        {
            FreezeTarget?.Dispose();
            FreezeTarget = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        }

        GraphicsDevice.SetRenderTarget(FreezeTarget);
        GraphicsDevice.Clear(Color.Black);
        SpriteBatch.Begin(samplerState: SamplerState.PointClamp);
        SpriteBatch.Draw(RenderTarget, new Rectangle(0, 0, w, h), Color.White);
        SpriteBatch.End();
        GraphicsDevice.SetRenderTarget(null);
    }

    private void DrawCursor(int x, int y, float scale)
    {
        var activeCursor = UseHandCursor && HandCursorTexture is not null ? HandCursorTexture : CursorTexture;
        var offsetX = UseHandCursor && HandCursorTexture is not null ? HandCursorOffsetX : CursorOffsetX;
        var offsetY = UseHandCursor && HandCursorTexture is not null ? HandCursorOffsetY : CursorOffsetY;

        SpriteBatch.Begin(samplerState: GlobalSettings.Sampler);
        SpriteBatch.Draw(
            activeCursor!,
            new Vector2(x - (offsetX * scale), y - (offsetY * scale)),
            null,
            Color.White,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            0f);
        SpriteBatch.End();
    }

    protected override void EndDraw()
    {
        base.EndDraw();

        if (GcRequested)
        {
            GcRequested = false;

            GC.Collect(
                2,
                GCCollectionMode.Aggressive,
                true,
                true);

            GC.WaitForPendingFinalizers();
        }
    }

    public void RequestScreenshot() => ScreenshotRequested = true;

    //a reused backbuffer-readback buffer, shared by the album screenshot and the --screenoutput dev dump (only one
    //captures at a time; the backbuffer is window-sized, so this grows once).
    private Color[]? ScreenshotBuffer;

    //reads the fully-composited backbuffer into an SKImage (RGBA8888). FromPixelCopy copies, so the buffer is free again
    //immediately. Null when the device has no size yet. GetBackBufferData stalls the GPU, so callers gate it (never per-frame).
    private SKImage? ReadBackbuffer()
    {
        var w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var h = GraphicsDevice.PresentationParameters.BackBufferHeight;

        if ((w <= 0) || (h <= 0))
            return null;

        if ((ScreenshotBuffer is null) || (ScreenshotBuffer.Length != w * h))
            ScreenshotBuffer = new Color[w * h];

        GraphicsDevice.GetBackBufferData(ScreenshotBuffer);

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

        return SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(ScreenshotBuffer.AsSpan()), w * 4);
    }

    //SWM: called from WorldScreen's UI pass once the world + chat bubbles are on the backbuffer but BEFORE the HUD, so
    //the shot is the world exactly as displayed (with bubbles, sharp-bilinear scaling) and no interface. The world fills
    //the whole backbuffer in native mode, so the backbuffer IS the world frame here. Must never crash (runs inside Draw).
    internal void CaptureWorldFrame()
    {
        if (!ScreenshotRequested)
            return;

        ScreenshotRequested = false;

        try
        {
            using var frame = ReadBackbuffer();

            if (frame is null)
                return;

            //push the album JPEG first (best-effort) so a local-save problem can never stop it
            TryUploadAlbumShot(frame);

            try
            {
                SaveLocalScreenshotPng(frame, frame.Width, frame.Height);
            }
            catch
            {
                //a local-save failure must never surface as a crash; the album upload above already ran
            }
        }
        catch
        {
            //never let a screenshot bring down the client
        }
    }

    //the original local-disk screenshot: a palettized PNG written to the data folder as lodNNN.png
    private static void SaveLocalScreenshotPng(SKImage sourceImage, int shotW, int shotH)
    {
        var dataPath = GlobalSettings.DataPath;
        var highestNumber = 0;

        foreach (var file in Directory.EnumerateFiles(dataPath, "lod*.*"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            if ((name.Length >= 4) && int.TryParse(name.AsSpan(3), out var num) && (num > highestNumber))
                highestNumber = num;
        }

        var nextNumber = highestNumber + 1;
        var fileName = Path.Combine(dataPath, $"lod{nextNumber:D3}.png");

        using var intermediary = ImageProcessor.PreserveNonTransparentBlacks(sourceImage);
        using var quantized = ImageProcessor.Quantize(QuantizerOptions.Default, intermediary);
        var palette = quantized.Palette;
        var indices = quantized.Entity.GetPalettizedPixelData(palette);

        var rgbPalette = new List<uint>(palette.Count);

        for (var i = 0; i < palette.Count; i++)
        {
            var c = palette[i];
            rgbPalette.Add(((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue);
        }

        WritePalettizedPng(fileName, shotW, shotH, indices, rgbPalette);
    }

    //SWM: scale the captured world frame to fit a 1280x720 box, JPEG-encode it, and upload it to the player's
    //server-side album. Wrapped so a failed / oversized / offline upload can never disturb the local screenshot.
    private void TryUploadAlbumShot(SKImage source)
    {
        try
        {
            //high quality (chunked, so no single-packet cap); only step quality down if a busy frame exceeds the budget.
            //The chunked upload + storage then carry whatever this comes out as, up to the 500000 cap.
            var bytes = ImageEncoding.ScaleToJpeg(source, 1280, 720, 460000, 94, 86, 78);

            if (bytes is { Length: > 0 and <= 500000 })
                Connection.UploadAlbumImage(bytes);
        }
        catch
        {
            //best-effort: an album upload must never break the local screenshot that was just saved
        }
    }

    private static void WritePalettizedPng(string fileName, int width, int height, byte[] indices, List<uint> palette)
    {
        using var file = File.Create(fileName);

        //PNG signature
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        //IHDR has width, height, 8-bit indexed color
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; //bit depth
        ihdr[9] = 3; //color type: indexed
        WritePngChunk(file, "IHDR"u8, ihdr);

        //PLTE has RGB triplets
        var plte = new byte[palette.Count * 3];

        for (var i = 0; i < palette.Count; i++)
        {
            var rgb = palette[i];
            plte[i * 3] = (byte)(rgb >> 16);
            plte[i * 3 + 1] = (byte)(rgb >> 8);
            plte[i * 3 + 2] = (byte)rgb;
        }

        WritePngChunk(file, "PLTE"u8, plte);

        //IDAT has zlib-compressed scanlines with no-filter bytes
        using var idatBuffer = new MemoryStream();

        using (var zlib = new ZLibStream(idatBuffer, CompressionLevel.Optimal, true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); //filter: none
                zlib.Write(indices, y * width, width);
            }

        WritePngChunk(file, "IDAT"u8, idatBuffer.ToArray());

        //IEND
        WritePngChunk(file, "IEND"u8, []);
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];

        //chunk length (big-endian)
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        stream.Write(buf);

        //chunk type
        stream.Write(type);

        //chunk data
        stream.Write(data);

        //CRC32 over type + data (PNG uses the standard CRC32 polynomial)
        var crc = 0xFFFFFFFFu;

        foreach (var b in type)
            crc = PngCrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        foreach (var b in data)
            crc = PngCrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        BinaryPrimitives.WriteUInt32BigEndian(buf, crc ^ 0xFFFFFFFF);
        stream.Write(buf);
    }

    private static readonly uint[] PngCrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }

    private static (int X, int Y) FindCursorHotspot(Texture2D texture)
    {
        var pixels = new Color[texture.Width * texture.Height];
        texture.GetData(pixels);

        var hotX = texture.Width;
        var hotY = texture.Height;

        for (var y = 0; y < texture.Height; y++)
            for (var x = 0; x < texture.Width; x++)
                if (pixels[y * texture.Width + x].A > 0)
                {
                    if (x < hotX)
                        hotX = x;

                    if (y < hotY)
                        hotY = y;
                }

        return (hotX, hotY);
    }

    protected override void Initialize()
    {
        base.Initialize();

        //never let the window be dragged smaller than the 640x480 virtual size. Done here (not in the ctor) because the
        //SDL window only exists after base.Initialize(); the OS then enforces the floor.
        Sdl.SDL_SetWindowMinimumSize(Window.Handle, VIRTUAL_WIDTH, VIRTUAL_HEIGHT);

        Window.ClientSizeChanged += OnClientSizeChanged;
    }

    protected override void LoadContent()
    {
        SpriteBatch = new SpriteBatchEx(GraphicsDevice);

        RenderTarget = new RenderTarget2D(
            GraphicsDevice,
            VIRTUAL_WIDTH,
            VIRTUAL_HEIGHT,
            false,
            SurfaceFormat.HalfVector4, //16-bit/channel: the day/night darkness math stays smooth; dithered to 8-bit at the final blit
            DepthFormat.Depth24Stencil8,
            0,
            RenderTargetUsage.PreserveContents);
        InputBuffer.Initialize();
        Dispatcher = new InputDispatcher();
        Screens = new ScreenManager(this);

        TextureConverter.Device = GraphicsDevice;
        FontAtlas.Initialize(GraphicsDevice);
        TtfTextRenderer.Initialize(LoadCustomFontBytes());
        TtfTextRenderer.InitializeFancy(LoadOptionalFontBytes("font_fancy.ttf"));
        TtfTextRenderer.InitializeSign(LoadOptionalFontBytes("font_sign.ttf"));
        UiRenderer.Instance = new UiRenderer(GraphicsDevice);

        //dynamic tile-light table (which foreground tiles glow); Data/tilelights.ini overrides the embedded default
        TileLights.Load(GlobalSettings.DataPath);

        LoadCustomCursor();

        using (var bgStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lobby_bg.png"))
            if (bgStream is not null)
                LobbyBackgroundTexture = Texture2D.FromStream(GraphicsDevice, bgStream);

        //the death-greyscale desaturation effect (a missing resource is a broken build - fail loudly)
        using (var fxStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("desaturate.mgfxo")
                              ?? throw new InvalidOperationException("Embedded resource 'desaturate.mgfxo' not found"))
        {
            using var fxBuffer = new MemoryStream();
            fxStream.CopyTo(fxBuffer);
            DesaturateEffect = new Effect(GraphicsDevice, fxBuffer.ToArray());
            DesaturateParam = DesaturateEffect.Parameters["Saturation"];
        }

        Screens.Switch(new LobbyLoginScreen());
    }

    //optional UI font: an external font.ttf in the Data folder wins (swap without a rebuild), else the embedded default
    private static byte[] LoadCustomFontBytes()
    {
        var external = Path.Combine(GlobalSettings.DataPath, "font.ttf");

        if (File.Exists(external))
        {
            Console.WriteLine($"[font] using external override: {external}");

            return File.ReadAllBytes(external);
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("font.ttf")
                           ?? throw new InvalidOperationException("Embedded resource 'font.ttf' not found");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    //the extra display faces: font_fancy.ttf (UnifrakturMaguntia blackletter - the website logo font - for the
    //town-map banner) and font_sign.ttf (IM Fell English - readable old-print serif - for sign/board body text).
    //An external copy in the Data folder wins, else the embedded default.
    private static byte[] LoadOptionalFontBytes(string fileName)
    {
        var external = Path.Combine(GlobalSettings.DataPath, fileName);

        if (File.Exists(external))
        {
            Console.WriteLine($"[font] using external override: {external}");

            return File.ReadAllBytes(external);
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(fileName)
                           ?? throw new InvalidOperationException($"Embedded resource '{fileName}' not found");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private void LoadCustomCursor()
    {
        CursorTexture = UiRenderer.Instance!.GetEpfTexture("mouse.epf", 0);

        //cursor visibility is decided per-frame in Draw (OS cursor by default, in-game cursor only on hover)
        if (CursorTexture is not null)
        {
            (CursorOffsetX, CursorOffsetY) = FindCursorHotspot(CursorTexture);
            OsCursor.SetArrowCursorTexture(CursorTexture, CursorOffsetX, CursorOffsetY);
        }

        HandCursorTexture = UiRenderer.Instance.GetEpfTexture("mouse.epf", 1);

        if (HandCursorTexture is not null)
        {
            (HandCursorOffsetX, HandCursorOffsetY) = FindCursorHotspot(HandCursorTexture);
            OsCursor.SetHandCursorTexture(HandCursorTexture, HandCursorOffsetX, HandCursorOffsetY);
        }
    }

    #region Window Sizing
    /// <summary>
    ///     Cycles the window through integer multipliers of the virtual resolution (640x480).
    ///     Advances to the next multiplier if it fits on the current monitor, otherwise wraps to 1x.
    /// </summary>
    internal void CycleWindowSize()
    {
        var displayIndex = Sdl.SDL_GetWindowDisplayIndex(Window.Handle);

        if ((displayIndex < 0) || (Sdl.SDL_GetDisplayBounds(displayIndex, out var bounds) < 0))
            return;

        var nextMultiplier = WindowSizeMultiplier + 1;
        var nextWidth = VIRTUAL_WIDTH * nextMultiplier;
        var nextHeight = VIRTUAL_HEIGHT * nextMultiplier;

        if ((nextWidth > bounds.W) || (nextHeight > bounds.H))
        {
            nextMultiplier = 1;
            nextWidth = VIRTUAL_WIDTH;
            nextHeight = VIRTUAL_HEIGHT;
        }

        WindowSizeMultiplier = nextMultiplier;

        ResizingInProgress = true;

        //leave maximized state so the backbuffer resize actually shrinks the OS window
        if ((Sdl.SDL_GetWindowFlags(Window.Handle) & Sdl.SDL_WINDOW_MAXIMIZED) != 0)
            Sdl.SDL_RestoreWindow(Window.Handle);

        Graphics.PreferredBackBufferWidth = nextWidth;
        Graphics.PreferredBackBufferHeight = nextHeight;
        Graphics.ApplyChanges();
        ResizingInProgress = false;
    }

    /// <summary>
    ///     Toggles fullscreen, bound to Alt+Enter. Uses MonoGame/SDL's native borderless-desktop fullscreen
    ///     (HardwareModeSwitch=false = no exclusive video-mode switch) and lets it save and restore the windowed
    ///     size/position on its own - no hand-rolled geometry. We deliberately do NOT touch PreferredBackBuffer:
    ///     it stays at the windowed size, which is exactly what SDL restores to on exit (the world render target
    ///     follows the real backbuffer in Draw). OnClientSizeChanged skips while IsFullScreen so it can't overwrite
    ///     that saved windowed size with the fullscreen size.
    /// </summary>
    public void ToggleFullscreen()
    {
        Graphics.HardwareModeSwitch = false;
        Graphics.ToggleFullScreen();
    }

    /// <summary>
    ///     Corrects the window size after a resize to enforce 4:3 aspect ratio.
    ///     Uses the larger dimension as the reference and adjusts the other.
    /// </summary>
/// <summary>
    ///     Returns the centered, integer-rounded rectangle of virtual 4:3 content inside a
    ///     backbuffer of the given size. Equal to the full backbuffer when it's already 4:3;
    ///     pillarboxes (wider windows) or letterboxes (taller windows) otherwise.
    /// </summary>
    

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        //skip while fullscreen: MonoGame resizes the window to the display on entry, which would otherwise make us
        //overwrite PreferredBackBuffer with the fullscreen size and lose the windowed size SDL restores to on exit.
        if (ResizingInProgress || Graphics.IsFullScreen)
            return;

        var width = Window.ClientBounds.Width;
        var height = Window.ClientBounds.Height;

        if ((width <= 0) || (height <= 0))
            return;

        //never render below the 640x480 virtual size (backstop to the OS minimum set on the window)
        width = Math.Max(width, VIRTUAL_WIDTH);
        height = Math.Max(height, VIRTUAL_HEIGHT);

        //the window may be any size and aspect. The backbuffer follows it 1:1; the Draw path letterboxes
        //the 640x480 world target inside it (4:3 preserved, black bars), while the UI fills the whole window.
        if ((Graphics.PreferredBackBufferWidth == width) && (Graphics.PreferredBackBufferHeight == height))
            return;

        ResizingInProgress = true;

        Graphics.PreferredBackBufferWidth = width;
        Graphics.PreferredBackBufferHeight = height;
        Graphics.ApplyChanges();

        ResizingInProgress = false;
    }

    /// <summary>
    ///     Toggles VSync at runtime (the Options "VSync" checkbox). Game logic is time-based, so this only changes how
    ///     often frames present, not the game speed.
    /// </summary>
    public void SetVSync(bool on)
    {
        Graphics.SynchronizeWithVerticalRetrace = on;
        Graphics.ApplyChanges();
    }

    /// <summary>
    ///     Caps the frame/update rate. fps &lt;= 0 = unlimited (variable timestep, render as fast as the display/VSync
    ///     allows - the old behaviour). A positive cap uses a fixed timestep at 1/fps so the game never produces frames
    ///     faster than that. Logic is time-based, so this only limits how often frames are produced, not game speed.
    /// </summary>
    public void SetMaxFramerate(int fps)
    {
        if (fps > 0)
        {
            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / fps);
        }
        else
            IsFixedTimeStep = false;
    }
    #endregion Window Sizing

    /// <summary>
    ///     Fired when all metadata files are up to date with the server.
    /// </summary>
    public event MetaDataSyncCompleteHandler? OnMetaDataSyncComplete;

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        if (newState == ConnectionState.World)
            LatencyMonitor.Start(Connection.Client);
        else if (oldState == ConnectionState.World)
            LatencyMonitor.Stop();
    }

    protected override void UnloadContent()
    {
        Window.ClientSizeChanged -= OnClientSizeChanged;
        CursorTexture?.Dispose();
        RenderTarget.Dispose();
        ScaledTarget?.Dispose();
        Screens.Dispose();
        Connection.Dispose();
        InputBuffer.Shutdown();
        CreatureRenderer.Dispose();
        AislingRenderer.Dispose();
        EffectRenderer.Dispose();
        ItemRenderer.Dispose();
        SoundSystem.Dispose();
        UiRenderer.Instance?.Dispose();
        UiRenderer.Instance = null;
        base.UnloadContent();
    }

    //── global screen fade-to-black ─────────────────────────────────────────────────────────────────────────────────
    //a full-window black overlay drawn over EVERYTHING at the end of Draw. It lives HERE (not on a screen) so it survives
    //a screen switch - used for the lobby's Create/Cancel transitions AND the login->world handoff (fade out on the
    //lobby, switch screens at black, then the world fades itself back in once its first map is loaded). FadeThroughBlack
    //= out -> run the action at full black -> fade back in. FadeToBlackHold = out -> action -> STAY black until something
    //calls FadeFromBlack. The action runs exactly once, on the frame black is reached.
    private float FadeAlpha;
    private float FadeTarget;
    private float FadeRate = 3f;
    private Action? FadeAtBlack;
    private bool FadeReturnAfterBlack;

    //── world-map travel cutscene ──────────────────────────────────────────────────────────────────────────────────
    //a full-screen splash (embedded da_travel_image.png) shown over EVERYTHING when the player confirms a world-map
    //travel, with its own song. Fades IN fast, holds fully opaque for TRAVEL_HOLD_SECONDS (covering the warp + new-map
    //load), then fades OUT over TRAVEL_FADE_OUT_SECONDS to reveal the player at the destination. Lives on ChaosGame so
    //it sits above all screens/menus.
    //the whole cutscene lasts ~3s: a very fast fade-in, a hold, and a quick fade-out (sums to ~3 seconds)
    private const float TRAVEL_FADE_IN_SECONDS = 0.12f;
    private const float TRAVEL_HOLD_SECONDS = 2.38f;
    private const float TRAVEL_FADE_OUT_SECONDS = 0.5f;
    private Texture2D? TravelImage;
    private float TravelElapsed;
    private bool TravelActive;

    private float TravelAlpha
    {
        get
        {
            if (!TravelActive)
                return 0f;

            if (TravelElapsed < TRAVEL_FADE_IN_SECONDS)
                return TravelElapsed / TRAVEL_FADE_IN_SECONDS;

            var afterHold = TravelElapsed - TRAVEL_FADE_IN_SECONDS - TRAVEL_HOLD_SECONDS;

            if (afterHold <= 0f)
                return 1f;

            return afterHold >= TRAVEL_FADE_OUT_SECONDS ? 0f : 1f - (afterHold / TRAVEL_FADE_OUT_SECONDS);
        }
    }

    /// <summary>Starts the world-map travel cutscene: fade the full-screen splash in fast + play its song, hold, fade out.</summary>
    public void PlayTravelCutscene()
    {
        TravelImage ??= LoadTravelImage();
        TravelElapsed = 0f;
        TravelActive = true;
        SoundSystem.PlaySound(SoundSystem.SoundTravel);
    }

    private void UpdateTravelCutscene(float dt)
    {
        if (!TravelActive)
            return;

        TravelElapsed += dt;

        if (TravelElapsed >= (TRAVEL_FADE_IN_SECONDS + TRAVEL_HOLD_SECONDS + TRAVEL_FADE_OUT_SECONDS))
            TravelActive = false;
    }

    private static Texture2D? LoadTravelImage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("da_travel_image.png");

        return stream is null ? null : Texture2D.FromStream(Device, stream);
    }

    /// <summary>True while a screen fade is showing or in progress (used to gate input during the transition).</summary>
    public bool IsFading => (FadeAlpha > 0f) || (FadeTarget > 0f);

    /// <summary>Fade to black, run <paramref name="atBlack" /> at full black, then fade back in (a fade THROUGH black).</summary>
    public void FadeThroughBlack(Action? atBlack, float seconds = 0.33f)
    {
        FadeAtBlack = atBlack;
        FadeReturnAfterBlack = true;
        FadeTarget = 1f;
        FadeRate = 1f / Math.Max(0.01f, seconds);
    }

    /// <summary>Fade to black, run <paramref name="atBlack" /> at full black, then HOLD black (call FadeFromBlack to reveal).</summary>
    public void FadeToBlackHold(Action? atBlack, float seconds = 0.4f)
    {
        FadeAtBlack = atBlack;
        FadeReturnAfterBlack = false;
        FadeTarget = 1f;
        FadeRate = 1f / Math.Max(0.01f, seconds);
    }

    /// <summary>Fade the black overlay back out (reveal the screen). No-op when already clear.</summary>
    public void FadeFromBlack(float seconds = 0.4f)
    {
        FadeAtBlack = null;
        FadeReturnAfterBlack = false;
        FadeTarget = 0f;
        FadeRate = 1f / Math.Max(0.01f, seconds);
    }

    /// <summary>
    ///     Snap instantly to full black and hold. Used at the lobby->world screen switch so the world never flashes
    ///     mid-load if the redirect/world-entry beat the smooth login fade to black. Reveal with FadeFromBlack.
    /// </summary>
    public void SnapToBlack()
    {
        FadeAtBlack = null;
        FadeReturnAfterBlack = false;
        FadeAlpha = 1f;
        FadeTarget = 1f;
    }

    public static Texture2D LoadTextureResource(String filename, bool premultiply = true)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(filename) ?? throw new InvalidOperationException($"Embedded resource '{filename}' not found");

        return Texture2D.FromStream(Device, stream, premultiply ? DefaultColorProcessors.PremultiplyAlpha : null);
    }

    public static Effect LoadEffectResource(String path)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException($"Embedded resource '{path}' not found");
        using var fxBuffer = new MemoryStream();
        stream.CopyTo(fxBuffer);

        return new Effect(Device, fxBuffer.ToArray());
    }

    private void UpdateScreenFade(float dt)
    {
        if (FadeAlpha < FadeTarget)
        {
            FadeAlpha = Math.Min(FadeTarget, FadeAlpha + (FadeRate * dt));

            //reached full black this frame: run the queued action once, then either auto-return or hold
            if (FadeAlpha >= 1f)
            {
                var act = FadeAtBlack;
                FadeAtBlack = null;
                act?.Invoke();

                if (FadeReturnAfterBlack)
                {
                    FadeReturnAfterBlack = false;
                    FadeTarget = 0f;
                }
            }
        } else if (FadeAlpha > FadeTarget)
            FadeAlpha = Math.Max(FadeTarget, FadeAlpha - (FadeRate * dt));
    }

    protected override void Update(GameTime gameTime)
    {
        ChaosGame.GameTime = gameTime;

        DebugOverlay.BeginFrame();

        //mouse coordinate transform. native-UI screens want the raw native pixel position (scale 1). Legacy screens
        //best-fit the 640x480 target into the LETTERBOX rect (WorldDrawRect), so map raw→640x480 through that rect -
        //its scale AND its bar offset - otherwise a non-4:3 window puts clicks off by the black-bar width.
        if (Screens.ActiveScreen?.UsesNativeUi == true)
            InputBuffer.SetVirtualScale(1f);
        else
        {
            var rect = WorldDrawRect;
            var scaleX = (float)rect.Width / VIRTUAL_WIDTH;
            var scaleY = (float)rect.Height / VIRTUAL_HEIGHT;
            InputBuffer.SetVirtualTransform(scaleX, scaleY, rect.X, rect.Y);
        }

        //freeze buffered input for this frame before anything reads it
        InputBuffer.Update(IsActive);

        //global, configurable keys read from the Keybindings system so they show up in (and obey) the Controls window.
        //skipped while the Controls window is capturing a key, so binding e.g. fullscreen doesn't also toggle it.
        if (!Keybindings.IsCapturing)
        {
            if (Keybindings.Triggered(GameAction.ToggleFullscreen))
                ToggleFullscreen();

            if (Keybindings.Triggered(GameAction.ToggleDebugOverlay))
                DebugOverlay.Toggle();

            if (Keybindings.Triggered(GameAction.Screenshot))
                RequestScreenshot();
        }

        DebugOverlay.Update(gameTime);

        //pump audio decodes and reset the same-frame dedup window before any handler can trigger sounds
        SoundSystem.Update();

        //drain and process network packets each frame
        PacketBuffer.Clear();
        Connection.ProcessPackets(PacketBuffer);

        Screens.Update(gameTime);

        //advance the global fade AFTER the screens update, so a screen that kicked off a fade this frame eases from here
        UpdateScreenFade((float)gameTime.ElapsedGameTime.TotalSeconds);
        UpdateTravelCutscene((float)gameTime.ElapsedGameTime.TotalSeconds);

        base.Update(gameTime);
    }

    #region Metadata Sync
    private uint ComputeLocalMetaCheckSum(string name)
    {
        var filePath = Path.Combine(MetaFilePath, name);

        if (!File.Exists(filePath))
            return 0;

        try
        {
            using var fileStream = File.OpenRead(filePath);
            using var zlibStream = new ZLibStream(fileStream, CompressionMode.Decompress);
            using var memoryStream = new MemoryStream();

            zlibStream.CopyTo(memoryStream);

            return Crc.Generate32(memoryStream.ToArray());
        } catch
        {
            return 0;
        }
    }

    private void HandleMetaData(MetaDataArgs args)
    {
        switch (args.MetaDataRequestType)
        {
            case MetaDataRequestType.AllCheckSums:
                HandleMetaDataCheckSums(args.MetaDataCollection);

                break;

            case MetaDataRequestType.DataByName:
                HandleMetaDataFileData(args.MetaDataInfo);

                break;
        }
    }

    private void HandleMetaDataCheckSums(ICollection<MetaDataInfo>? collection)
    {
        if (collection is null || (collection.Count == 0))
        {
            OnMetaDataSyncComplete?.Invoke();

            return;
        }

        MetaPendingChecksums.Clear();
        MetaSyncStarted = true;

        foreach (var info in collection)
        {
            var localCheckSum = ComputeLocalMetaCheckSum(info.Name);

            if (localCheckSum != info.CheckSum)
                MetaPendingChecksums[info.Name] = info.CheckSum;
        }

        foreach (var name in MetaPendingChecksums.Keys)
            Connection.SendMetaDataRequest(MetaDataRequestType.DataByName, name);

        if (MetaPendingChecksums.Count == 0)
            OnMetaDataSyncComplete?.Invoke();
    }

    private void HandleMetaDataFileData(MetaDataInfo? info)
    {
        if (info is null || string.IsNullOrEmpty(info.Name) || (info.Data.Length == 0))
            return;

        File.WriteAllBytes(Path.Combine(MetaFilePath, info.Name), info.Data);
        MetaPendingChecksums.Remove(info.Name);

        if (MetaSyncStarted && (MetaPendingChecksums.Count == 0))
            OnMetaDataSyncComplete?.Invoke();
    }
    #endregion
}