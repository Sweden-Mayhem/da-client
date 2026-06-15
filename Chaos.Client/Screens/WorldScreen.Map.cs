#region
using Chaos.Client.Collections;
using Chaos.Client.Data;
using Chaos.Client.Extensions;
using Chaos.Client.Models;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Geometry;
using Chaos.Geometry.Abstractions;
using Chaos.Networking.Entities.Server;
using DALib.Cryptography;
using DALib.Data;
using DALib.Extensions;
using TileFlags = DALib.Definitions.TileFlags;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    #region Map Assembly
    private void HandleUserId(uint id) => WorldState.PlayerEntityId = id;

    private void HandleMapInfo(MapInfoArgs args)
    {
        WorldMap.HideMap();
        CurrentMapName = args.Name ?? string.Empty;
        var zoneName = CurrentMapName;
        
        //same map (refresh), skip expensive teardown and just clear transient entity state
        //the checksum must also match, a server-side map edit reuses the same id but changes the bytes,
        //so a mismatch means the cached MapFile is stale and we take the full reload path
        if ((args.MapId == CurrentMapId) && (args.CheckSum == CurrentMapCheckSum) && MapFile is not null)
        {
            ClearTransientState();

            //re-evaluate darkness and weather only if the flag actually changed
            var newFlags = (MapFlags)args.Flags;

            if (newFlags != CurrentMapFlags)
            {
                CurrentMapFlags = newFlags;
                DarknessRenderer.OnMapChanged(args.MapId, CurrentMapFlags.HasFlag(MapFlags.Darkness));
                WeatherRenderer.OnMapChanged(CurrentMapFlags);
            }

            UpdateHuds(HudOps.SetZoneName, zoneName);

            return;
        }

        //a real map change (not a same-map refresh), start the quick fade to black, FinalizeMapLoad fades the new map
        //back in once it is preloaded. Skip on first world entry (HasEnteredWorld is set in FinalizeMapLoad) since there
        //is no prior world frame to freeze, it would fade out stale lobby pixels. The freeze is captured next Draw from
        //the world target's retained last frame (the old map), so it fades out even though it stops rendering now
        if (HasEnteredWorld)
            Game.MapTransition.BeginFadeOut();

        //new map, dispose old caches and load a fresh mapfile from local files
        TownMapControl.Hide();
        MapRenderer.Dispose();
        MapRenderer = new MapRenderer();

        MapFile = LoadMapFile(
            args.MapId,
            args.Width,
            args.Height,
            args.CheckSum);
        //MapPreloaded gates every path request, stale water and door tile lists belong to the old map's dimensions,
        //so a right-click arriving before FinalizeMapLoad must not pathfind against them
        MapPreloaded = false;
        AwaitingMapData = false;
        CurrentMapId = args.MapId;
        CurrentMapCheckSum = args.CheckSum;
        CurrentMapFlags = (MapFlags)args.Flags;

        //re-center on the current window size (it can be resized between map loads) so the bar stays on screen-center
        MapLoading.CenterOnUi();
        MapLoading.Show();

        //local file missing, corrupt or checksum mismatch, request from server
        if (MapFile is null)
        {
            MapFile = new MapFile(args.Width, args.Height);
            InitializeEmptyTiles(MapFile);
            AwaitingMapData = true;
            Game.Connection.RequestMapData();
        } else
        {
            //snapshot tab-map walls from pristine disk state before any DoorArgs can mutate the foreground tiles
            //FinalizeMapLoad's later rebuild of the pathfinder reflects door state on purpose, but the tab map
            //must stay tied to the raw mapfile so clicking a door does not change how the map looks on (Tab)
            TabMapRenderer.Generate(Device, MapFile);
        }

        //clear entity + renderer caches for the new map
        ClearTransientState();
        Game.CreatureRenderer.Clear();
        Game.AislingRenderer.ClearCache();
        Game.AislingRenderer.ClearTintedCache();
        Game.AislingRenderer.ClearLayerImageCache();
        Game.ItemRenderer.Clear();

        //reset darkness state and load the hea light map for the new map
        DarknessRenderer.OnMapChanged(args.MapId, CurrentMapFlags.HasFlag(MapFlags.Darkness));
        WeatherRenderer.OnMapChanged(CurrentMapFlags);

        UpdateHuds(HudOps.SetZoneName, zoneName);
        UpdateHuds(HudOps.ShowPersistentMessage, string.Empty);
    }

    private void ClearTransientState()
    {
        WorldState.Clear();
        Game.AislingRenderer.ClearCompositeCache();
        Overlays.Clear();
        DebugRenderer.Clear();
        NpcSession.HideAll();
        Pathfinding.Clear();
        ResumeChaseTargetId = null;
        GroupHighlightedIds.Clear();
        Game.AislingRenderer.ClearGroupTintCache();
        Game.CreatureRenderer.ClearTintCaches();
    }

    private void HandleMapData(MapDataArgs args)
    {
        if (MapFile is null)
            return;

        var y = args.CurrentYIndex;

        if (y >= MapFile.Height)
            return;

        //each tile is 6 bytes: bg(2 be), lfg(2 be), rfg(2 be)
        var data = args.MapData;
        var tileCount = Math.Min(data.Length / 6, MapFile.Width);

        for (var x = 0; x < tileCount; x++)
        {
            var offset = x * 6;
            var background = (short)((data[offset] << 8) | data[offset + 1]);
            var leftForeground = (short)((data[offset + 2] << 8) | data[offset + 3]);
            var rightForeground = (short)((data[offset + 4] << 8) | data[offset + 5]);

            MapFile.Tiles[x, y] = new MapTile
            {
                Background = background,
                LeftForeground = leftForeground,
                RightForeground = rightForeground
            };
        }

        //last row received, save to disk and finalize
        if (AwaitingMapData && (y >= (MapFile.Height - 1)))
        {
            AwaitingMapData = false;

            //snapshot tab-map walls from pristine server-delivered state before any DoorArgs can mutate tiles
            TabMapRenderer.Generate(Device, MapFile);
            SaveMapFile(CurrentMapId);
            FinalizeMapLoad();
        }
    }

    private void HandleMapLoadComplete()
    {
        //when awaiting server map data, ignore this, FinalizeMapLoad gets called from HandleMapData
        if (AwaitingMapData)
            return;

        FinalizeMapLoad();
    }

    private void FinalizeMapLoad()
    {
        if (MapFile is null)
            return;

        //first map load is login or world entry, item-received sounds arm a grace after this so the inventory the
        //server sends on login never triggers them (later maps are warps and never re-send the inventory)
        var firstEntry = !HasEnteredWorld;

        if (firstEntry)
        {
            HasEnteredWorld = true;
            WorldEntryTick = Environment.TickCount;

            //pull the self-profile once on entry. SelfProfileRequested is false here so HandleSelfProfile only fills
            //it in and does not open the book, this primes PlayerAbilityMetadata so skill and spell hover tooltips have
            //their detail immediately instead of being blank until the player opens the equipment menu the first time
            Game.Connection.RequestSelfProfile();
        }

        if (!MapPreloaded)
        {
            MapRenderer.PreloadMapTiles(
                Device,
                MapFile,
                MapLoading.SetProgress,
                static id => DoorTable.GetVariants((short)id).Select(static v => (int)v));

            MapWaterTiles = BuildPathfindingData(MapFile);
            MapPreloaded = true;
        }

        MapLoading.Hide();
        FollowPlayerCamera();

        //suppress hold-to-walk briefly, if the move button is still held from before the warp don't auto-path on the
        //fresh map (the cursor may be behind the player, which would walk them straight back out), reset the baseline
        //too. A deliberate new press still paths immediately
        HeldWalkSuppressMs = HELD_WALK_MAP_ENTRY_SUPPRESS_MS;
        HeldWalkRepathTimer = 0f;

        //same idea for a held arrow/movement key: ignore it for a moment so the player doesn't immediately step on arrival
        KeyMoveSuppressMs = KEY_MOVE_MAP_ENTRY_SUPPRESS_MS;
        HeldWalkPathLength = 0;
        HeldWalkLastTarget = null;

        if (firstEntry)
        {
            //login to world, the lobby faded the whole window to black and held it. Don't reveal immediately, hold the
            //black a beat (Update counts IntroHoldRemaining down) so the map's night or darkness LightLevel snaps in
            //while still fully black, then slow-fade the world in for a calm intro. The global fade owns this transition
            PendingIntroReveal = true;
            IntroHoldRemaining = INTRO_HOLD_SECONDS;
        } else
            //a normal in-world map change, fade the new map up from black (matches the fade-out begun on the change)
            Game.MapTransition.BeginFadeIn();

        Game.GcRequested = true;
    }

    /// <summary>
    ///     Builds the static pathfinder grid. Tiles whose foreground is a door (either side) are pulled out of the wall set
    ///     and returned in <c>doorTiles</c> so <see cref="GetPathfindingBlockedPoints" /> can re-evaluate each on every
    ///     <c>FindPath</c> call against the live foreground state, this keeps the grid immutable while still reflecting
    ///     runtime door toggles. A static decoration that happens to use a door-side id is harmlessly captured and will be
    ///     re-blocked each call because <see cref="IsTileWall" /> always returns true for it.
    /// </summary>
    //scans the map once for the swim-gate water tiles, walls and doors are checked live per path request
    //(IsTilePassable, IsClosedDoorAt) so they always reflect HandleDoor swaps
    private static List<IPoint> BuildPathfindingData(MapFile mapFile)
    {
        var gndAttrs = DataContext.Tiles.GroundAttributes;
        var waterTiles = new List<IPoint>();

        for (var y = 0; y < mapFile.Height; y++)
            for (var x = 0; x < mapFile.Width; x++)
            {
                var tile = mapFile.Tiles[x, y];

                if (DoorTable.IsDoorTileId(tile.LeftForeground) || DoorTable.IsDoorTileId(tile.RightForeground))
                    continue;

                if (!IsTileWall(tile.LeftForeground)
                    && !IsTileWall(tile.RightForeground)
                    && gndAttrs.TryGetValue(tile.Background, out var gndAttr)
                    && gndAttr.IsWalkBlocking)
                    waterTiles.Add(new Point(x, y));
            }

        return waterTiles;
    }

    /// <summary>
    ///     Returns the current set of blocked points for pathfinding: entity positions, currently-closed doors (evaluated
    ///     on the fly from the live tile state so recent HandleDoor swaps are reflected), plus water tiles when the swim
    ///     gate is active and the player can't swim. GMs bypass all blocking.
    /// </summary>
    private List<IPoint> GetPathfindingBlockedPoints(WorldEntity? player = null)
    {
        //GMs obey collision when click-pathfinding (entities, closed doors, walls), only manual arrow-key movement
        //lets them clip. The swim gate is the one exception that still ignores GMs, they can always cross water
        var blocked = WorldState.GetBlockedPoints();

        //entities are only hard blockers near the player, distant creatures will have wandered on before we arrive,
        //and planning around them caused huge detours and constant path flapping. If one is still in the way when we
        //get close, the blocked-step recovery re-routes on the spot
        if (player is not null)
            blocked.RemoveAll(p => (Math.Abs(p.X - player.TileX) + Math.Abs(p.Y - player.TileY)) > 3);

        if (!IsGameMaster && GlobalSettings.RequireSwimmingSkill && !WorldState.SkillBook.HasSkillByName("swimming"))
            blocked.AddRange(MapWaterTiles);

        //closed doors are NOT planning obstacles (a path may deliberately lead up to / into one); the walker simply
        //STOPS at a closed door - opening it is the player's act (see the path executor)

        return blocked;
    }

    private bool TileHasForeground(int tileX, int tileY)
    {
        if (MapFile is null)
            return false;

        if ((tileX < 0) || (tileY < 0) || (tileX >= MapFile.Width) || (tileY >= MapFile.Height))
            return false;

        var tile = MapFile.Tiles[tileX, tileY];

        return tile.LeftForeground.IsRenderedTileIndex() || tile.RightForeground.IsRenderedTileIndex();
    }

    /// <summary>
    ///     True when the foreground is a walk-blocking wall. Authoritative source is <c>sotp.dat</c>, indexed by the tile's
    ///     current fgIndex (after any door-swap from <see cref="HandleDoor" />). Door open/closed state is tracked entirely
    ///     by mutating the tile's fgIndex, the open-side id's SOTP byte carries the correct walkability, so no override
    ///     is needed. Jambs and frame pieces of multi-tile doors are not in <see cref="DoorTable" /> and correctly stay
    ///     walls in both states.
    /// </summary>
    private static bool IsTileWall(int fgIndex)
    {
        if (fgIndex <= 0)
            return false;

        var sotpIndex = fgIndex - 1;
        var sotpData = DataContext.Tiles.SotpData;

        if (sotpIndex >= sotpData.Length)
            return false;

        return (sotpData[sotpIndex] & TileFlags.Wall) != 0;
    }

    private bool IsTileWallBlocked(int tileX, int tileY)
    {
        if (NoClip)
            return false;

        if (MapFile is null)
            return false;

        if ((tileX < 0) || (tileY < 0) || (tileX >= MapFile.Width) || (tileY >= MapFile.Height))
            return false;

        var tile = MapFile.Tiles[tileX, tileY];

        if (!IsTileWall(tile.LeftForeground) && !IsTileWall(tile.RightForeground))
            return false;

        //a closed door is a wall until it is opened (walking into it auto-opens it, see MoveOrTurn and the path
        //executor), a non-door warp tile (arch etc.) is enterable despite its wall art, the server's collision
        //rule is "walls block unless a warp reactor overrides", and stepping onto it is what fires the warp
        if (DoorTable.IsDoorTileId(tile.LeftForeground) || DoorTable.IsDoorTileId(tile.RightForeground))
            return true;

        return !WarpData.HasWarpAt(CurrentMapId, tileX, tileY);
    }

    /// <summary>Planning walkability, everything passable now plus closed doors. A path may lead to or through
    ///     one, but the walker stops in front of it (opening doors is the player's act).</summary>
    private bool IsPlanWalkable(int tileX, int tileY) => IsTilePassable(tileX, tileY) || IsClosedDoorAt(tileX, tileY);


    /// <summary>True when the tile is a door whose current art is closed (reads as a wall). Such a tile can be
    ///     planned through, opening it on approach is the walker's job.</summary>
    private bool IsClosedDoorAt(int tileX, int tileY)
    {
        if (MapFile is null || (tileX < 0) || (tileY < 0) || (tileX >= MapFile.Width) || (tileY >= MapFile.Height))
            return false;

        var tile = MapFile.Tiles[tileX, tileY];

        if (!DoorTable.IsDoorTileId(tile.LeftForeground) && !DoorTable.IsDoorTileId(tile.RightForeground))
            return false;

        return IsTileWall(tile.LeftForeground) || IsTileWall(tile.RightForeground);
    }

    private bool IsTilePassable(int tileX, int tileY)
    {
        if (NoClip)
            return true;

        if (MapFile is null)
            return true;

        //check wall tiles (foreground sotp data). A closed door is a wall here, stepping requires opening it first
        //(the walker auto-opens on approach). A non-door warp tile (arch etc.) is enterable despite wall art, the
        //server's rule is "walls block unless a warp reactor overrides", and stepping onto it fires the warp
        var tile = MapFile.Tiles[tileX, tileY];

        if (IsTileWall(tile.LeftForeground) || IsTileWall(tile.RightForeground))
        {
            var isDoor = DoorTable.IsDoorTileId(tile.LeftForeground) || DoorTable.IsDoorTileId(tile.RightForeground);

            if (isDoor || !WarpData.HasWarpAt(CurrentMapId, tileX, tileY))
                return false;
        }

        //check gndattr walk-blocking (deep water tiles), only when the swim gate is active and the player can't swim
        if (GlobalSettings.RequireSwimmingSkill
            && !IsGameMaster
            && !WorldState.SkillBook.HasSkillByName("swimming")
            && DataContext.Tiles.GroundAttributes.TryGetValue(tile.Background, out var gndAttr)
            && gndAttr.IsWalkBlocking)
            return false;

        //check entities at the destination tile
        if (WorldState.HasBlockingEntityAt(tileX, tileY, WorldState.PlayerEntityId))
            return false;

        return true;
    }

    private static MapFile? LoadMapFile(
        int mapId,
        int width,
        int height,
        ushort serverCheckSum)
    {
        var path = Path.Combine(DataContext.DataPath, "maps", $"lod{mapId}.map");

        if (!File.Exists(path))
            return null;

        try
        {
            var fileBytes = File.ReadAllBytes(path);

            if (fileBytes.Length != (width * height * 6))
                return null;

            if (CRC16.Calculate(fileBytes) != serverCheckSum)
                return null;

            //parse in place, file format is le int16 x3 per tile, y-major x-minor
            var mapFile = new MapFile(width, height);
            var index = 0;

            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var background = (short)(fileBytes[index] | (fileBytes[index + 1] << 8));
                    var leftForeground = (short)(fileBytes[index + 2] | (fileBytes[index + 3] << 8));
                    var rightForeground = (short)(fileBytes[index + 4] | (fileBytes[index + 5] << 8));
                    index += 6;

                    mapFile.Tiles[x, y] = new MapTile
                    {
                        Background = background,
                        LeftForeground = leftForeground,
                        RightForeground = rightForeground
                    };
                }

            return mapFile;
        } catch
        {
            return null;
        }
    }

    private void SaveMapFile(int mapId)
    {
        var path = Path.Combine(DataContext.DataPath, "maps", $"lod{mapId}.map");

        //custom server maps are streamed and cached here, a retail data folder has no "maps" subdirectory,
        //so create it before saving or MapFile.Save throws DirectoryNotFoundException and FinalizeMapLoad
        //never runs (the client hangs forever on "Loading map...")
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        MapFile!.Save(path);
    }

    private void HandleLocationChanged(int x, int y)
    {
        UpdateHuds(HudOps.SetCoords, x, y);

        var player = WorldState.GetPlayerEntity();

        if (player is null)
            return;

        //if the server position matches, nothing to reconcile
        if ((player.TileX == x) && (player.TileY == y))
            return;

        //server-authoritative position correction, snap and reset visuals
        //PredictedWalkDests is not cleared on purpose, the responses for those predicted walks are still
        //coming, and HandleClientWalkResponse reconciles each one by tile (matching no-op, diverging resync),
        //so the queue drains and self-corrects on its own. Clearing it here would make those trailing responses
        //look like genuine server walks and snap the player to stale tiles
        QueuedWalkDirection = null;
        player.TileX = x;
        player.TileY = y;
        WorldState.MarkSortDirty();
        AnimationSystem.ResetToIdle(player);

        //a position correction throws the path away, never the chase, a monster cutting into the predicted step
        //rubber-bands the player, and dropping the target here is what randomly cancelled attacks. The retarget
        //tick re-paths from the corrected tile
        var chase = Pathfinding.TargetEntityId;
        Pathfinding.Clear();

        if (chase is { } chaseId)
            Pathfinding.SetEntityTarget(chaseId);

        FollowPlayerCamera();
    }

    /// <summary>
    ///     Sets Camera.Offset for the current HUD. Both HUD MAP rects share the same X origin and width, only the height
    ///     differs (large is ~116px taller, extending downward). The small HUD is calibrated to (-28, 24). The large HUD uses
    ///     (-28, -4) so the player appears ~30px lower than the small HUD on screen, not the naive ~58px that a fixed
    ///     viewport-relative offset would produce.
    /// </summary>
    private void UpdateCameraOffset(XnaRectangle viewport)
    {
        _ = viewport;
        Camera.Offset = WorldHud == SmallHud ? new XnaVector2(-28, 24) : new XnaVector2(-28, -4);
    }

    /// <summary>
    ///     Updates camera position to follow the player entity's visual position, including walk interpolation offset. In
    ///     rough scroll mode, only updates at fixed intervals for a choppier look.
    /// </summary>
    private void FollowPlayerCamera()
    {
        if (ComputeBaseFollow() is not { } target)
            return;

        //a snap (map load or server position correction), jump straight onto the player and mark settled-following so the
        //next frame tracks exactly (no leftover transition from a focus pan)
        CamPos = target;
        CamInitialized = true;
        PanSettled = true;
        LockedOnSpeaker = false;
        Camera.Position = target;
    }

    private static void InitializeEmptyTiles(MapFile mapFile)
    {
        for (var y = 0; y < mapFile.Height; y++)
            for (var x = 0; x < mapFile.Width; x++)
                mapFile.Tiles[x, y] = new MapTile();
    }
    #endregion
}