#region
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Chaos.Client.Data;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Manages sound-effect and background-music playback via SDL2_mixer (with minimp3 compiled directly into the
///     mixer DLL). SFX are loaded from legend.dat as MP3 bytes, or for the custom feedback sounds from
///     the client sound folder, decoded to PCM once via <c>Mix_LoadWAV_RW</c> and cached as <c>Mix_Chunk</c>
///     pointers. Playback uses the mixer channel pool with per-channel volume. Music is streamed from disk via
///     <c>Mix_LoadMUS</c> (map tracks from the music folder, the custom login theme from the sound folder), with
///     transitions driven by the built-in <c>Mix_FadeOutMusic</c> and <c>Mix_FadeInMusic</c>. When the same sound id
///     is triggered while an earlier instance is still audible, the prior live instances are faded out via
///     <c>Mix_FadeOutChannel</c> (per-id voice-stealing), matching the retail client behaviour of restarting the single
///     cached sample handle on each trigger.
/// </summary>
public sealed class SoundSystem : IDisposable
{
    //custom non-retail sound ids, negative so they never collide with a retail legend.dat sound id
    //their audio is a plain MP3 file in the client sound folder, played through the same PlaySound pipeline
    public const int SoundItem = -1;        //inventory changed, item gained or lost
    public const int SoundMoney = -2;       //gold value changed
    public const int SoundUi = -3;          //UI button click
    public const int SoundDialog = -4;      //NPC dialog or read-only sign opened
    public const int SoundWindowOpen = -5;  //a window started to open
    public const int SoundWindowClose = -6; //a window started to close
    public const int SoundLevelUp = -7;     //the local player level went up
    public const int SoundSwing = -8;       //melee swing, replaces retail assail sound 1 via SoundRemap
    public const int SoundHit = -9;         //something lost HP, hit cue played on a drop

    //footstep cues, the local player walk plays a random pitch-varied one of these per step
    //at the independent Footsteps volume, files step1 through step4 in the sound folder
    public const int SoundStep1 = -10;
    public const int SoundStep2 = -11;
    public const int SoundStep3 = -12;
    public const int SoundStep4 = -13;

    //chat-bubble cue, plays pitch-varied whenever an over-head speech bubble appears at the independent Chat volume
    public const int SoundChat = -14;

    //world-map travel song, played once when the player confirms a world-map travel
    public const int SoundTravel = -15;

    //the retail death-knell heart thump, the sound the server plays once when an aisling dies
    //the death FX re-triggers it on a fixed beat timer while the local player is dead so the heart keeps beating
    public const int SoundHeartbeat = 6;

    //custom non-retail music ids, negative so they never collide with a server map-music id
    //each maps to a file in the sound folder, loaded by path the same way as a map track
    public const int MusicLobbyLogin = -1000;    //lobby and login-screen music
    public const int MusicLobbyCreation = -1001; //character-creation screen music

    //subfolder of the client Data folder holding all custom audio
    private const string CustomAudioDir = "sound";

    //the single live SoundSystem, so UI controls with no game reference can fire a click sound statically
    public static SoundSystem? Instance { get; private set; }
    //the original client opens its audio driver at 22050 Hz stereo, we match that rate and let the OS do
    //the single resample to the output device rate rather than stacking our own
    private const int MIX_FREQUENCY = 22050;
    private const int MIX_CHANNELS = 2;
    //sample chunk size fed to the audio callback in sample frames, 512 is about 23ms at 22050Hz
    //a newly-triggered chunk only begins at the next callback boundary, so a small chunk keeps sound onsets tight
    //do not raise this without retesting footstep cadence, a large chunk makes rhythmic sounds drop a beat
    private const int MIX_CHUNK_SIZE = 512;
    //32 channels so overlap-heavy situations like AOE effects or crowds of mobs do not run out of voices
    private const int CHANNEL_COUNT = 32;
    //fade duration for map-transition music swaps, matched to the feel of the original client ramp
    private const int MUSIC_FADE_MS = 500;
    private const int MAX_CACHED_SOUNDS = 64;
    private const int VOLUME_STEPS = 10;
    //volume scale multiplier mapping our 0 to 10 slider to the mixer 0 to 128 range
    private const int VOLUME_SCALE = SdlMixer.MIX_MAX_VOLUME / VOLUME_STEPS;
    //when the same sound id fires while a prior instance is still audible, fade the prior one out over this many ms
    //the mixer interpolates the fade sample-accurately so there is no step discontinuity in the output waveform
    private const int FADE_OUT_MS = 200;

    //a few feedback sounds get a randomized pitch per play so repeated presses do not sound identical
    //the mixer has no pitch control, so we pre-build copies resampled across a small range and pick a random one
    //PITCH_RANGE is the amount knob, 0.08 is about plus or minus 8%, raise for wilder and lower for subtler
    private const int PITCH_VARIANT_COUNT = 13;
    private const float PITCH_RANGE = 0.08f;
    //footsteps get a smaller pitch spread so back-to-back steps are not identical but never sound warbly
    private const float FOOTSTEP_PITCH_RANGE = 0.05f;
    //the retail assail melee swing sound, it reaches the client via the generic Sound packet
    //we replace it with the custom SoundSwing via SoundRemap, drop the SoundRemap entry below to revert
    private const int AssailSound = 1;
    //retail sound ids we substitute with a custom sound, applied at the top of PlaySound before dedup and tracking
    private static readonly Dictionary<int, int> SoundRemap = new() { [AssailSound] = SoundSwing };
    //sound ids that get the randomized-pitch treatment, the UI cues plus the swing, hit and footstep samples
    private static readonly int[] PitchedSoundIds =
    [
        SoundUi, SoundWindowOpen, SoundWindowClose, SoundDialog, SoundItem, SoundMoney, SoundSwing, SoundHit,
        SoundStep1, SoundStep2, SoundStep3, SoundStep4, SoundChat
    ];

    //the footstep samples one is picked at random from on each step
    private static readonly int[] FootstepSoundIds = [SoundStep1, SoundStep2, SoundStep3, SoundStep4];

    //callback instance kept as a field so the GC does not collect it since SDL holds a native pointer to it
    private readonly SdlMixer.ChannelFinishedCallback ChannelFinishedDelegate;
    //filled on the audio thread when a channel naturally finishes, drained in Update on the game thread
    private readonly ConcurrentQueue<int> FinishedChannels = new();
    //channel to sound id mapping for voice-steal lookup and finish cleanup, only touched on the game thread
    private readonly Dictionary<int, int> ChannelToSoundId = [];
    //inverse lookup, sound id to currently-playing channels, scanned on each PlaySound to fade out prior instances
    private readonly Dictionary<int, List<int>> SoundIdToChannels = [];
    //decoded Mix_Chunk pointers indexed by sound id, with a monotonic timestamp for LRU eviction
    private readonly Dictionary<int, (nint Chunk, long Timestamp)> SoundCache = [];
    //custom sound id to file name inside the sound folder, LoadChunk reads and decodes that file instead of legend.dat
    //the bytes are not held here, each decode reads the small file fresh like legend.dat
    private readonly Dictionary<int, string> CustomSoundFiles = [];
    //custom music id to file name inside the sound folder, the lobby login and character-creation tracks
    private readonly Dictionary<int, string> CustomMusicFiles = [];
    //pre-built pitch-shifted variant chunks per pitched sound id, built lazily on first play and freed on Dispose
    //kept out of SoundCache so the LRU never frees one mid-use, PlaySound picks a random variant
    private readonly Dictionary<int, nint[]> PitchVariants = [];
    //last variant index played per pitched sound id, so the next play can pick a different one
    private readonly Dictionary<int, int> LastPitchVariant = [];
    //same-frame dedup, for example AOE hitting multiple targets in one tick trying to play the same sound many times
    private readonly HashSet<int> PlayedThisFrame = [];

    private int CurrentMusicId = -1;
    private nint CurrentMusicPtr;
    private bool Initialized;
    private bool IsDisposed;
    private bool MusicFadingOut;
    private int MusicVolumeValue = SdlMixer.MIX_MAX_VOLUME;
    //music id queued to start once the current music fade-out completes, 0 means stop music
    private int PendingMusicId;
    private int SfxVolume = SdlMixer.MIX_MAX_VOLUME;
    //footstep, chat-bubble and whisper-cue loudness as a 0 to 100 percentage of the SFX volume, still scaled by the master slider
    private int FootstepPercent;
    private int ChatBubblePercent;
    private int WhisperPercent;
    private long SoundCacheTimestamp;

    public SoundSystem()
    {
        ChannelFinishedDelegate = OnChannelFinished;
        InitializeMixer();

        //map the custom feedback sound ids to their files in the sound folder, LoadChunk reads them on first play
        CustomSoundFiles[SoundItem] = "item.mp3";
        CustomSoundFiles[SoundMoney] = "money.mp3";
        CustomSoundFiles[SoundUi] = "ui.mp3";
        CustomSoundFiles[SoundDialog] = "dialog.mp3";
        CustomSoundFiles[SoundWindowOpen] = "window_open.mp3";
        CustomSoundFiles[SoundWindowClose] = "window_close.mp3";
        CustomSoundFiles[SoundLevelUp] = "level_up.mp3";
        CustomSoundFiles[SoundSwing] = "swing.mp3";
        CustomSoundFiles[SoundHit] = "hit.mp3";
        CustomSoundFiles[SoundStep1] = "step1.mp3";
        CustomSoundFiles[SoundStep2] = "step2.mp3";
        CustomSoundFiles[SoundStep3] = "step3.mp3";
        CustomSoundFiles[SoundStep4] = "step4.mp3";
        CustomSoundFiles[SoundChat] = "chat.mp3";
        CustomSoundFiles[SoundTravel] = "travel.mp3";

        //the custom lobby music tracks, streamed from the sound folder by StartMusic
        CustomMusicFiles[MusicLobbyLogin] = "login.mp3";
        CustomMusicFiles[MusicLobbyCreation] = "creation.mp3";

        Instance = this;
    }

    /// <summary>Fires the UI button-click sound. Static so any control can call it without a game reference.</summary>
    public static void PlayUiClick() => Instance?.PlaySound(SoundUi);

    /// <summary>Fires the window/menu open (fade-in) sound. Static so a window control can call it with no game reference.</summary>
    public static void PlayWindowOpen() => Instance?.PlaySound(SoundWindowOpen);

    /// <summary>Fires the window/menu close (fade-out) sound. Static so a window control can call it with no game reference.</summary>
    public static void PlayWindowClose() => Instance?.PlaySound(SoundWindowClose);

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;

        if (Instance == this)
            Instance = null;

        if (!Initialized)
            return;

        //clear the callback before halting so the audio thread cannot fire OnChannelFinished while we tear down
        SdlMixer.Mix_ChannelFinished(nint.Zero);
        SdlMixer.Mix_HaltChannel(SdlMixer.MIX_DEFAULT_CHANNEL);
        SdlMixer.Mix_HaltMusic();

        FreeCurrentMusic();

        foreach (var entry in SoundCache.Values)
            if (entry.Chunk != nint.Zero)
                SdlMixer.Mix_FreeChunk(entry.Chunk);

        SoundCache.Clear();

        //free the pre-built pitch variant chunks, these live outside SoundCache
        foreach (var variants in PitchVariants.Values)
            foreach (var chunk in variants)
                if (chunk != nint.Zero)
                    SdlMixer.Mix_FreeChunk(chunk);

        PitchVariants.Clear();

        SdlMixer.Mix_CloseAudio();
        SdlMixer.Mix_Quit();
        Sdl.SDL_QuitSubSystem(Sdl.SDL_INIT_AUDIO);

        Initialized = false;
    }

    /// <summary>
    ///     Plays background music by id. Triggers a fade-out of the current track (if any) and a fade-in of the new
    ///     track once the fade-out completes. musicId 0 stops playback with a fade-out and leaves no pending track.
    /// </summary>
    public void PlayMusic(int musicId)
    {
        if (IsDisposed || !Initialized)
            return;

        //already mid-fade-out, just update what plays next once the fade completes
        if (MusicFadingOut)
        {
            PendingMusicId = musicId;

            return;
        }

        //already playing the requested track, nothing to do
        if (musicId == CurrentMusicId)
            return;

        //stop request when nothing is currently playing, no fade needed
        if ((musicId == 0) && (CurrentMusicPtr == nint.Zero))
            return;

        //nothing currently playing, skip the fade-out phase and start the new track directly
        if (CurrentMusicPtr == nint.Zero)
        {
            StartMusic(musicId);

            return;
        }

        //kick off the async fade-out, Update picks up the completion and starts PendingMusicId
        SdlMixer.Mix_FadeOutMusic(MUSIC_FADE_MS);
        MusicFadingOut = true;
        PendingMusicId = musicId;
    }

    /// <summary>
    ///     Plays a sound effect by id. First-time plays decode the MP3 synchronously (minimp3 is fast on short
    ///     files), subsequent plays grab the cached <c>Mix_Chunk</c>. If a previous instance of the same id is
    ///     still playing it is faded out via <c>Mix_FadeOutChannel</c> so overlaps do not stack loudness. Ids in
    ///     <see cref="PitchedSoundIds" /> instead play a randomly-pitched variant each time so repeats do not match.
    /// </summary>
    public void PlaySound(int soundId)
    {
        //substitute any retail sound we have replaced with a custom one before everything else
        //so dedup, pitch selection and voice-steal tracking all key off the id that actually plays
        if (SoundRemap.TryGetValue(soundId, out var remapped))
            soundId = remapped;

        PlaySoundInternal(soundId, SfxVolume);
    }

    /// <summary>Fires the chat-bubble cue (pitch-varied). Static so the overlay manager can call it with no game reference.</summary>
    public static void PlayChatBubble() => Instance?.PlayChatBubbleSound();

    /// <summary>
    ///     Plays a footstep cue, a random one of the four step samples (pitch-varied), at the footstep volume scaled by
    ///     the SFX slider. Called by the world screen when the player starts walking or
    ///     reaches the midpoint of a tile. Pure fire-and-forget, no channel tracking, no dedup, no voice-steal.
    /// </summary>
    public void PlayFootstep()
    {
        if (IsDisposed || !Initialized)
            return;

        var volume = ScaledSfx(FootstepPercent);

        if (volume <= 0)
            return;

        //pick from the enabled step samples only, the per-step toggles in ClientSettings
        Span<int> pool = stackalloc int[FootstepSoundIds.Length];
        var count = 0;

        for (var i = 0; i < FootstepSoundIds.Length; i++)
            if (ClientSettings.FootstepStepsEnabled[i])
                pool[count++] = FootstepSoundIds[i];

        if (count == 0)
            return;

        var soundId = pool[Random.Shared.Next(count)];
        var chunk = GetPitchedChunk(soundId);

        if (chunk == nint.Zero)
            return;

        //fire-and-forget, find any free channel and play, no tracking, no dedup, no channel management
        for (var i = 0; i < CHANNEL_COUNT; i++)
            if (SdlMixer.Mix_Playing(i) == 0)
            {
                SdlMixer.Mix_Volume(i, volume);
                SdlMixer.Mix_PlayChannel(i, chunk, 0);

                return;
            }
    }

    /// <summary>
    ///     Plays the chat-bubble cue (pitch-varied) at the chat volume scaled by the SFX slider.
    ///     Called when an over-head speech bubble appears.
    /// </summary>
    public void PlayChatBubbleSound() => PlaySoundInternal(SoundChat, ScaledSfx(ChatBubblePercent));

    /// <summary>
    ///     Plays the whisper-received cue (sound 158) at the whisper volume scaled by the SFX slider. 0% is silent.
    /// </summary>
    public void PlayWhisperSound() => PlaySoundInternal(158, ScaledSfx(WhisperPercent));

    //effective channel volume for a sub-slider expressed as a 0 to 100 percentage of the master SFX volume
    private int ScaledSfx(int percent) => SfxVolume * Math.Clamp(percent, 0, 100) / 100;

    private int PlaySoundInternal(int soundId, int channelVolume, int preferChannel = -1)
    {
        if (IsDisposed || !Initialized || (channelVolume <= 0))
            return -1;

        //collapse same-frame duplicate triggers, for example AOE hitting multiple targets in a single tick
        if (!PlayedThisFrame.Add(soundId))
            return -1;

        nint chunk;

        //pitched sounds pick a randomly-pitched variant per play rather than the single shared chunk so they vary
        if (Array.IndexOf(PitchedSoundIds, soundId) >= 0)
        {
            chunk = GetPitchedChunk(soundId);

            if (chunk == nint.Zero)
                return -1;
        } else if (SoundCache.TryGetValue(soundId, out var cached))
        {
            chunk = cached.Chunk;
            SoundCache[soundId] = (chunk, SoundCacheTimestamp++);
        } else
        {
            chunk = LoadChunk(soundId);

            if (chunk == nint.Zero)
                return -1;

            SoundCache[soundId] = (chunk, SoundCacheTimestamp++);

            if (SoundCache.Count > MAX_CACHED_SOUNDS)
                EvictOldest();
        }

        //voice-steal any currently-playing instances of the same sound id so overlaps do not stack loudness
        //Mix_FadeOutChannel does a sample-accurate fade to zero then halt inside the mix callback
        //so the output waveform has no step discontinuity, unlike Mix_Volume which can click
        //skip channels the audio thread already finished but did not drain yet, a fade on an idle channel
        //would affect whatever play SDL assigns there next
        //this matches the retail per-id voice stealing, one live sample handle per sound id restarted on each trigger
        if (SoundIdToChannels.TryGetValue(soundId, out var existing))
            foreach (var ch in existing)
                if (SdlMixer.Mix_Playing(ch) != 0)
                    SdlMixer.Mix_FadeOutChannel(ch, FADE_OUT_MS);

        //preferred channel, when given use it directly even if it is still playing, Mix_PlayChannel replaces
        //whatever is running atomically in the audio callback so there is no gap
        //used by PlayFootstep to keep footsteps on one channel, each new step replaces the previous one instantly
        var channel = preferChannel;

        if ((channel < 0) || (channel >= CHANNEL_COUNT))
        {
            channel = -1;

            for (var i = 0; i < CHANNEL_COUNT; i++)
                if (SdlMixer.Mix_Playing(i) == 0)
                {
                    channel = i;

                    break;
                }

            if (channel < 0)
                return -1;
        }

        //set volume before play begins so the first audio callback after Mix_PlayChannel sees the correct level
        SdlMixer.Mix_Volume(channel, channelVolume);

        //if the channel we just claimed has stale tracking from its previous play, scrub it now
        //otherwise the drain would remove the new play tracking and leave the previous SoundIdToChannels entry stale
        //stale entries cause spurious voice-steals on unrelated sounds that land on the same channel number later
        if (ChannelToSoundId.Remove(channel, out var prevSoundId))
            if (SoundIdToChannels.TryGetValue(prevSoundId, out var prevList))
            {
                prevList.Remove(channel);

                if (prevList.Count == 0)
                    SoundIdToChannels.Remove(prevSoundId);
            }

        //Mix_PlayChannel with an explicit channel index still stops any sound currently on that channel
        //this is the key to footstep channel-reuse, the old sound is replaced without a separate halt
        //so the audio callback sees the new PCM from the very next sample with zero gap
        if (SdlMixer.Mix_PlayChannel(channel, chunk, 0) < 0)
            return -1;

        ChannelToSoundId[channel] = soundId;

        if (!SoundIdToChannels.TryGetValue(soundId, out var list))
        {
            list = [];
            SoundIdToChannels[soundId] = list;
        }

        if (!list.Contains(channel))
            list.Add(channel);

        return channel;
    }

    /// <summary>
    ///     Sets the music volume, range 0 (mute) to 10 (max). Applies immediately to the currently playing track.
    /// </summary>
    public void SetMusicVolume(int volume)
    {
        MusicVolumeValue = Math.Clamp(volume, 0, VOLUME_STEPS) * VOLUME_SCALE;

        if (Initialized)
            SdlMixer.Mix_VolumeMusic(MusicVolumeValue);
    }

    /// <summary>
    ///     Sets the sound effect volume, range 0 (mute) to 10 (max). Future plays use the new volume, sounds
    ///     already in flight keep their current channel volume.
    /// </summary>
    public void SetSoundVolume(int volume) => SfxVolume = Math.Clamp(volume, 0, VOLUME_STEPS) * VOLUME_SCALE;

    /// <summary>
    ///     Sets the footstep volume as a 0 to 100 percentage of the SFX volume, 0 is silent.
    /// </summary>
    public void SetFootstepVolume(int percent) => FootstepPercent = Math.Clamp(percent, 0, 100);

    /// <summary>
    ///     Sets the chat-bubble volume as a 0 to 100 percentage of the SFX volume, 0 is silent.
    /// </summary>
    public void SetChatVolume(int percent) => ChatBubblePercent = Math.Clamp(percent, 0, 100);

    /// <summary>
    ///     Sets the whisper-cue volume as a 0 to 100 percentage of the SFX volume, 0 is silent.
    /// </summary>
    public void SetWhisperVolume(int percent) => WhisperPercent = Math.Clamp(percent, 0, 100);

    /// <summary>
    ///     Pumps deferred audio-thread work back into the game state. Call once per frame from the game loop.
    /// </summary>
    public void Update()
    {
        if (IsDisposed || !Initialized)
            return;

        //reset same-frame dedup window, any PlaySound later this frame starts from a clean set
        PlayedThisFrame.Clear();

        //reap channels that finished on the audio thread so their tracking entries do not leak
        //reset per-channel volume to the current SFX slider here, the mixer preserves channel volume across plays
        //so leaving a faded-out channel at volume 0 would carry into whatever chunk SDL assigns there next
        while (FinishedChannels.TryDequeue(out var channel))
        {
            //skip stale events for channels that PlaySound already reassigned before this drain ran
            //the new play already set its own volume and tracking, touching either would corrupt the live sound
            if (SdlMixer.Mix_Playing(channel) != 0)
                continue;

            SdlMixer.Mix_Volume(channel, SfxVolume);

            if (!ChannelToSoundId.Remove(channel, out var soundId))
                continue;

            if (!SoundIdToChannels.TryGetValue(soundId, out var list))
                continue;

            list.Remove(channel);

            if (list.Count == 0)
                SoundIdToChannels.Remove(soundId);
        }

        //detect fade-out completion and start the queued track (if any)
        if (MusicFadingOut && (SdlMixer.Mix_PlayingMusic() == 0))
        {
            MusicFadingOut = false;

            FreeCurrentMusic();
            CurrentMusicId = -1;

            //start the queued track, the check is not above-zero since custom music ids are negative
            //0 alone means stop with nothing next, and StartMusic with 0 is a no-op anyway
            if (PendingMusicId != 0)
                StartMusic(PendingMusicId);

            PendingMusicId = 0;
        }
    }

    private void EvictOldest()
    {
        while (SoundCache.Count > MAX_CACHED_SOUNDS)
        {
            var oldestKey = -1;
            var oldestTime = long.MaxValue;

            foreach ((var key, var entry) in SoundCache)
            {
                //skip anything that is still audibly playing, Mix_FreeChunk on a live chunk corrupts the mixer
                if (SoundIdToChannels.ContainsKey(key))
                    continue;

                if (entry.Timestamp < oldestTime)
                {
                    oldestTime = entry.Timestamp;
                    oldestKey = key;
                }
            }

            //every cached sound is currently playing, defer eviction rather than risk crashing the mixer
            if (oldestKey < 0)
                break;

            var chunk = SoundCache[oldestKey].Chunk;
            SoundCache.Remove(oldestKey);

            if (chunk != nint.Zero)
                SdlMixer.Mix_FreeChunk(chunk);
        }
    }

    private void InitializeMixer()
    {
        if (Sdl.SDL_InitSubSystem(Sdl.SDL_INIT_AUDIO) != 0)
            return;

        //Mix_Init is effectively a no-op for minimp3 (statically linked since SDL_mixer 2.6) but is still part of
        //the official init sequence, safe to call unconditionally
        SdlMixer.Mix_Init(SdlMixer.MIX_INIT_MP3);

        if (SdlMixer.Mix_OpenAudio(MIX_FREQUENCY, SdlMixer.AUDIO_S16LSB, MIX_CHANNELS, MIX_CHUNK_SIZE) != 0)
        {
            SdlMixer.Mix_Quit();
            Sdl.SDL_QuitSubSystem(Sdl.SDL_INIT_AUDIO);

            return;
        }

        SdlMixer.Mix_AllocateChannels(CHANNEL_COUNT);
        SdlMixer.Mix_VolumeMusic(MusicVolumeValue);

        var cb = Marshal.GetFunctionPointerForDelegate(ChannelFinishedDelegate);
        SdlMixer.Mix_ChannelFinished(cb);

        Initialized = true;
    }

    private nint LoadChunk(int soundId)
    {
        //custom sounds decode from their MP3 file in the sound folder rather than legend.dat
        if (CustomSoundFiles.TryGetValue(soundId, out var fileName))
        {
            var soundPath = Path.Combine(DataContext.DataPath, CustomAudioDir, fileName);

            if (!File.Exists(soundPath))
                return nint.Zero;

            try
            {
                return LoadChunkFromBytes(File.ReadAllBytes(soundPath));
            } catch
            {
                return nint.Zero;
            }
        }

        if (!DatArchives.Legend.TryGetValue($"{soundId}.mp3", out var entry))
            return nint.Zero;

        byte[] bytes;

        try
        {
            using var archiveStream = entry.ToStreamSegment();
            using var ms = new MemoryStream();
            archiveStream.CopyTo(ms);
            bytes = ms.ToArray();
        } catch
        {
            return nint.Zero;
        }

        return LoadChunkFromBytes(bytes);
    }

    private static nint LoadChunkFromBytes(byte[] bytes)
    {
        //pin the managed byte array so SDL can read it during Mix_LoadWAV_RW, which decodes the whole file
        //to PCM synchronously before returning, after which the buffer can be unpinned and freed
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            var rw = Sdl.SDL_RWFromConstMem(handle.AddrOfPinnedObject(), bytes.Length);

            if (rw == nint.Zero)
                return nint.Zero;

            //freesrc 1 asks SDL to close the RWops for us after the load completes
            return SdlMixer.Mix_LoadWAV_RW(rw, 1);
        } finally
        {
            handle.Free();
        }
    }

    //returns a random pitch-shifted variant chunk for a pitched sound id, building the variant set on first use
    //picks a different index from the previous play so two presses in a row never sound identical
    private nint GetPitchedChunk(int soundId)
    {
        if (!PitchVariants.TryGetValue(soundId, out var variants))
        {
            variants = BuildPitchVariants(soundId) ?? [];
            PitchVariants[soundId] = variants;
        }

        if (variants.Length == 0)
            return nint.Zero;

        if (variants.Length == 1)
            return variants[0];

        var last = LastPitchVariant.GetValueOrDefault(soundId, -1);

        //pick uniformly among the other variants when we have a previous one so the pitch always changes
        var idx = last < 0
            ? Random.Shared.Next(variants.Length)
            : Random.Shared.Next(variants.Length - 1);

        if ((last >= 0) && (idx >= last))
            idx++;

        LastPitchVariant[soundId] = idx;

        return variants[idx];
    }

    //decodes the sound once then builds the pitch variant chunks resampled across the pitch range
    //returns null if the sound cannot be loaded, the caller caches the empty result so PlaySound just no-ops
    private nint[]? BuildPitchVariants(int soundId)
    {
        var baseChunk = LoadChunk(soundId);

        if (baseChunk == nint.Zero)
            return null;

        //read the decoded PCM out of the Mix_Chunk, it is in the mixer output format of MIX_FREQUENCY, S16, stereo
        var info = Marshal.PtrToStructure<MixChunk>(baseChunk);

        if ((info.Abuf == nint.Zero) || (info.Alen == 0))
        {
            SdlMixer.Mix_FreeChunk(baseChunk);

            return null;
        }

        var pcm = new byte[info.Alen];
        Marshal.Copy(info.Abuf, pcm, 0, (int)info.Alen);
        SdlMixer.Mix_FreeChunk(baseChunk);

        var variants = new nint[PITCH_VARIANT_COUNT];
        var made = 0;

        //footsteps spread wider than the subtle UI cues
        var range = Array.IndexOf(FootstepSoundIds, soundId) >= 0 ? FOOTSTEP_PITCH_RANGE : PITCH_RANGE;

        for (var i = 0; i < PITCH_VARIANT_COUNT; i++)
        {
            //spread the factor evenly across the range, the middle variant is about 1.0 and unshifted
            var t = PITCH_VARIANT_COUNT == 1 ? 0.5f : i / (float)(PITCH_VARIANT_COUNT - 1);
            var factor = (1f - range) + t * (2f * range);
            var chunk = LoadChunkFromBytes(WrapWavPcm(ResampleStereoS16(pcm, factor)));

            if (chunk != nint.Zero)
                variants[made++] = chunk;
        }

        if (made == 0)
            return null;

        if (made < variants.Length)
            Array.Resize(ref variants, made);

        return variants;
    }

    //linear-resamples interleaved 16-bit stereo PCM by factor, above 1 is higher pitch and shorter, below 1 lower and longer
    //this is the standard cheap SFX pitch shift, the tiny duration change on a short blip is imperceptible
    private static byte[] ResampleStereoS16(byte[] src, float factor)
    {
        var srcS = MemoryMarshal.Cast<byte, short>(src);
        var frames = srcS.Length / MIX_CHANNELS;

        if ((frames <= 1) || (Math.Abs(factor - 1f) < 0.0001f))
            return src;

        var outFrames = Math.Max(1, (int)MathF.Round(frames / factor));
        var dst = new byte[outFrames * MIX_CHANNELS * sizeof(short)];
        var dstS = MemoryMarshal.Cast<byte, short>(dst.AsSpan()); //.AsSpan() forces the writable Span overload

        for (var j = 0; j < outFrames; j++)
        {
            var pos = j * factor;
            var i0 = (int)pos;
            var frac = pos - i0;
            var i1 = Math.Min(i0 + 1, frames - 1);

            for (var c = 0; c < MIX_CHANNELS; c++)
            {
                var s0 = srcS[(i0 * MIX_CHANNELS) + c];
                var s1 = srcS[(i1 * MIX_CHANNELS) + c];
                var v = s0 + ((s1 - s0) * frac);
                dstS[(j * MIX_CHANNELS) + c] = (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
            }
        }

        return dst;
    }

    //wraps raw PCM in a 44-byte WAV header so LoadChunkFromBytes can decode it
    //into a chunk SDL owns and frees, avoiding any manual unmanaged buffer bookkeeping
    private static byte[] WrapWavPcm(byte[] pcm)
    {
        const int bits = 16;
        var blockAlign = MIX_CHANNELS * bits / 8;
        var byteRate = MIX_FREQUENCY * blockAlign;

        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                  //PCM fmt chunk size
        w.Write((short)1);            //audio format PCM
        w.Write((short)MIX_CHANNELS);
        w.Write(MIX_FREQUENCY);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();

        return ms.ToArray();
    }

    //SDL2_mixer Mix_Chunk layout, so we can read the decoded PCM of a loaded sound for resampling
    [StructLayout(LayoutKind.Sequential)]
    private struct MixChunk
    {
        public int Allocated;
        public nint Abuf;
        public uint Alen;
        public byte Volume;
    }

    private void OnChannelFinished(int channel)
        //called on the SDL audio thread, keep this to a lock-free enqueue so we never touch the tracking dicts
        //from anywhere except the game-loop Update
        => FinishedChannels.Enqueue(channel);

    private void StartMusic(int musicId)
    {
        if (musicId == 0)
            return;

        //a custom track is a file in the sound folder, a map track is in the music folder, both stream via Mix_LoadMUS
        var path = CustomMusicFiles.TryGetValue(musicId, out var customFile)
            ? Path.Combine(DataContext.DataPath, CustomAudioDir, customFile)
            : Path.Combine(DataContext.DataPath, "music", $"{musicId}.mus");

        if (!File.Exists(path))
            return;

        var handle = SdlMixer.Mix_LoadMUS(path);

        if (handle == nint.Zero)
            return;

        CurrentMusicPtr = handle;
        CurrentMusicId = musicId;

        //Mix_FadeInMusic ramps from silence up to the current Mix_VolumeMusic over ms milliseconds
        SdlMixer.Mix_FadeInMusic(handle, -1, MUSIC_FADE_MS);
    }

    //frees the current music handle. Safe to call when nothing is playing.
    private void FreeCurrentMusic()
    {
        if (CurrentMusicPtr == nint.Zero)
            return;

        SdlMixer.Mix_FreeMusic(CurrentMusicPtr);
        CurrentMusicPtr = nint.Zero;
    }
}