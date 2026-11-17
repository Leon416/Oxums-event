using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using Random = UnityEngine.Random;

/*
 * Oxum Event
 *
 * An event at Oxum's Gas Station: patrol helicopters and/or Bradley APCs guard the monument until players
 * destroy them all or the time limit runs out. How many of each spawn is set per difficulty in the config.
 *
 * Keeping them in the region:
 *  - A Bradley spawned by a plugin has no patrol path, and without one the game leaves its route alone
 *    (BradleyAPC.UpdateMovement_Patrol / _Hunt return at once). The plugin feeds it waypoints along the road
 *    next to the gas station, the same way the game fills currentPath itself.
 *  - A helicopter gets the gas station as its interest zone. Every tick the plugin checks where it is and
 *    where it is heading and sends it back when either is outside the leash radius. Retiring and fleeing
 *    are blocked through hooks, and it may only target players inside the event zone.
 *
 * Only this file goes to the server (oxide/plugins or carbon/plugins).
 */

namespace Oxide.Plugins
{
    [Info("Oxum Event", "Leon S", "1.0.2")]
    [Description("Event at Oxum's Gas Station with patrol helicopters and Bradley APCs, configurable per difficulty.")]
    public class OxumEvent : RustPlugin
    {
        #region Constants

        private const string PermAdmin = "oxumevent.admin";

        private const string BradleyPrefab = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";
        private const string HeliPrefab = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
        private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string TimeMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        // Part of the monument's prefab name: .../roadside/gas_station_1.prefab
        private const string MonumentKey = "gas_station_1";

        private const float TickSeconds = 2f;
        private const float RouteSpacing = 5f;        // metres between Bradley waypoints
        private const float ArriveDistance = 8f;      // a Bradley this close to the end of its leg turns around
        private const float LegTimeoutSeconds = 120f; // a Bradley that needs longer than this is sent the other way
        private const float EndPauseMinSeconds = 6f;  // a Bradley waits this long at the end of its stretch...
        private const float EndPauseMaxSeconds = 18f; // ...up to this long, before driving back
        private const int MinSectionPoints = 4;      // fewer waypoints per Bradley than this: all share the whole route
        private const int SectionGapPoints = 2;       // waypoints left free between two Bradley sections
        private const float HeliSpawnDistance = 350f;
        private const float HeliSpawnHeight = 100f;
        private const float HeliHoverHeight = 35f;
        private const float HeliHoverSpread = 50f;
        private const float HeliFarOutMargin = 100f;  // this far beyond the leash radius a helicopter is always sent back
        private const float ProtectLingerSeconds = 120f; // napalm keeps burning and a shot-down heli still has to crash
        private const float AutoRetrySeconds = 300f;

        // Hooks that only matter while an event is running; unsubscribed the rest of the time
        private static readonly string[] EventHooks =
        {
            nameof(OnEntityDeath), nameof(OnEntityKill), nameof(OnPatrolHelicopterKill),
            nameof(OnHelicopterRetire), nameof(CanHelicopterTarget), nameof(OnNoGoZoneAdded),
            nameof(CanBradleyApcTarget), nameof(CanDeployScientists), nameof(OnScientistInitialized)
        };

        #endregion

        #region Configuration

        private Configuration config;

        private class SavedPosition
        {
            [JsonProperty("x")] public float X;
            [JsonProperty("y")] public float Y;
            [JsonProperty("z")] public float Z;
        }

        private class Difficulty
        {
            [JsonProperty("Name")] public string Name = "Easy";
            [JsonProperty("Bradleys")] public int Bradleys = 1;
            [JsonProperty("Helicopters")] public int Helicopters;
            [JsonProperty("Chance weight (0 = only with the admin command)")] public float Weight = 1f;
            [JsonProperty("Time limit in minutes")] public float TimeLimitMinutes = 30f;
            [JsonProperty("Loot crates per Bradley")] public int CratesPerBradley = 3;
            [JsonProperty("Loot crates per helicopter")] public int CratesPerHelicopter = 4;
        }

        private class Configuration
        {
            [JsonProperty("Automatic start enabled")]
            public bool AutoStart = true;

            [JsonProperty("Automatic start: minimum minutes between events")]
            public float AutoMinMinutes = 120f;

            [JsonProperty("Automatic start: maximum minutes between events")]
            public float AutoMaxMinutes = 180f;

            [JsonProperty("Automatic start: minimum online players")]
            public int AutoMinPlayers = 1;

            [JsonProperty("Announce an automatic event this many minutes before it starts (0 = start at once)")]
            public float AnnounceMinutes = 5f;

            [JsonProperty("Warn this many minutes before the time limit (0 = no warning)")]
            public float EndWarningMinutes = 5f;

            [JsonProperty("Event zone radius in metres (only players inside are attacked)")]
            public float ZoneRadius = 150f;

            [JsonProperty("Helicopter leash radius in metres (sent back when further away)")]
            public float HeliLeashRadius = 300f;

            [JsonProperty("Bradley road length in metres to each side of the gas station")]
            public float RoadLength = 60f;

            [JsonProperty("Maximum distance in metres between the gas station and a road")]
            public float RoadSearchDistance = 80f;

            [JsonProperty("Bradleys deploy scientists")]
            public bool BradleyScientists = true;

            // Off by default (owner's choice): living next to the gas station must not be a safe spot to fight from
            [JsonProperty("Block event damage to player bases")]
            public bool ProtectBases = false;

            [JsonProperty("Show a marker on the map")]
            public bool MapMarker = true;

            [JsonProperty("Map marker radius (map units, not metres)")]
            public float MapMarkerRadius = 0.5f;

            [JsonProperty("Show the time left on the map (shop icon in the middle of the circle)")]
            public bool MapMarkerTime = true;

            [JsonProperty("Chat prefix")]
            public string ChatPrefix = "<color=#ff6b35>[Oxum's Event]</color> ";

            [JsonProperty("Custom event position (set with /oxum setpos; replaces the gas station search)")]
            public SavedPosition CustomPosition;

            // Replace: without it Json.NET adds the saved list to the defaults and every difficulty doubles
            [JsonProperty("Difficulties", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Difficulty> Difficulties = DefaultDifficulties();
        }

        private static List<Difficulty> DefaultDifficulties() => new List<Difficulty>
        {
            new Difficulty { Name = "Easy", Bradleys = 1, Helicopters = 0, Weight = 40f, TimeLimitMinutes = 30f },
            new Difficulty { Name = "Medium", Bradleys = 1, Helicopters = 1, Weight = 30f, TimeLimitMinutes = 40f },
            new Difficulty { Name = "Hard", Bradleys = 2, Helicopters = 2, Weight = 20f, TimeLimitMinutes = 50f },
            new Difficulty { Name = "Extreme", Bradleys = 3, Helicopters = 3, Weight = 10f, TimeLimitMinutes = 60f }
        };

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
            }
            catch
            {
                PrintError("Configuration file is invalid; using default values.");
                LoadDefaultConfig();
            }

            NormalizeConfig();
            SaveConfig(); // writes settings added by a newer version into an existing file
        }

        private void NormalizeConfig()
        {
            config.AutoMinMinutes = Mathf.Max(1f, config.AutoMinMinutes);
            config.AutoMaxMinutes = Mathf.Max(config.AutoMinMinutes, config.AutoMaxMinutes);
            config.AutoMinPlayers = Mathf.Max(0, config.AutoMinPlayers);
            config.AnnounceMinutes = Mathf.Max(0f, config.AnnounceMinutes);
            config.EndWarningMinutes = Mathf.Max(0f, config.EndWarningMinutes);
            config.ZoneRadius = Mathf.Max(30f, config.ZoneRadius);
            config.HeliLeashRadius = Mathf.Max(config.ZoneRadius, config.HeliLeashRadius);
            config.RoadLength = Mathf.Max(10f, config.RoadLength);
            config.RoadSearchDistance = Mathf.Max(10f, config.RoadSearchDistance);
            config.MapMarkerRadius = Mathf.Max(0.05f, config.MapMarkerRadius);
            if (config.ChatPrefix == null) config.ChatPrefix = string.Empty;

            var list = new List<Difficulty>();
            if (config.Difficulties != null)
            {
                foreach (var difficulty in config.Difficulties)
                {
                    if (difficulty == null || string.IsNullOrWhiteSpace(difficulty.Name)) continue;
                    difficulty.Name = difficulty.Name.Trim();
                    difficulty.Bradleys = Mathf.Clamp(difficulty.Bradleys, 0, 10);
                    difficulty.Helicopters = Mathf.Clamp(difficulty.Helicopters, 0, 10);
                    difficulty.Weight = Mathf.Max(0f, difficulty.Weight);
                    difficulty.TimeLimitMinutes = Mathf.Max(1f, difficulty.TimeLimitMinutes);
                    difficulty.CratesPerBradley = Mathf.Clamp(difficulty.CratesPerBradley, 0, 12);
                    difficulty.CratesPerHelicopter = Mathf.Clamp(difficulty.CratesPerHelicopter, 0, 12);
                    list.Add(difficulty);
                }
            }
            if (list.Count == 0)
            {
                PrintWarning("The config has no difficulties; using the default four.");
                list = DefaultDifficulties();
            }
            config.Difficulties = list;
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region State

        private enum Phase { Idle, Announced, Running }
        private enum EndReason { Won, TimeUp, Stopped }

        private class BradleyUnit
        {
            public BradleyAPC Apc;
            public List<Vector3> Route; // null = no road found, the Bradley stays where it spawned
            public bool Forward = true;
            public bool HasLeg;
            public Vector3 LegEnd;
            public float LegStarted;
            public float WaitUntil;
        }

        private class HeliUnit
        {
            public PatrolHelicopter Heli;
            public PatrolHelicopterAI Ai;
        }

        private Phase phase = Phase.Idle;
        private Difficulty current;
        private Vector3 center;
        private float startAt; // when an announced event begins
        private float endTime;
        private bool endWarned;
        private float protectUntil;
        private float nextAutoAt;
        private bool cleaning; // true while the plugin removes its own entities, so OnEntityKill ignores them

        private readonly List<Vector3> monuments = new List<Vector3>();
        private readonly List<BradleyUnit> bradleys = new List<BradleyUnit>();
        private readonly List<HeliUnit> helis = new List<HeliUnit>();
        private readonly List<ScientistNPC> scientists = new List<ScientistNPC>();
        private MapMarkerGenericRadius marker;
        private VendingMachineMapMarker timeMarker; // shop icon; its "shop name" shows the time left
        private string timeMarkerText;

        private Timer autoTimer;
        private Timer startTimer;
        private Timer tickTimer;
        private Timer markerTimer;

        private static float Now => Time.realtimeSinceStartup;

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            foreach (var hook in EventHooks) Unsubscribe(hook);
            Unsubscribe(nameof(OnEntityTakeDamage));
        }

        private void OnServerInitialized()
        {
            FindMonuments();
            if (config.CustomPosition != null)
                Puts("Using the custom event position from the config.");
            else if (monuments.Count == 0)
                PrintWarning("No Oxum's Gas Station found on this map. The event cannot start until an admin sets a position with /oxum setpos.");
            else
                Puts("Found {0} Oxum's Gas Station(s) on this map.", monuments.Count);

            ScheduleAuto();
        }

        private void Unload()
        {
            if (phase != Phase.Idle) Broadcast("Event.Stopped");
            DestroyTimer(ref autoTimer);
            Cleanup();
            phase = Phase.Idle;
        }

        // The radius marker does not send its colour and size to players who connect later
        private void OnPlayerConnected(BasePlayer player)
        {
            if (Alive(marker)) marker.SendUpdate();
        }

        private void OnEntityDeath(BradleyAPC apc, HitInfo info)
        {
            var unit = FindBradley(apc);
            if (unit == null) return;
            bradleys.Remove(unit);

            var killer = info != null ? info.InitiatorPlayer : null;
            BroadcastKill("Event.BradleyDestroyed", killer);
            NextTick(CheckFinished);
        }

        // Called on the hit that brings the helicopter down; it then spins and crashes on its own
        private object OnPatrolHelicopterKill(PatrolHelicopter heli, HitInfo info)
        {
            var unit = FindHeli(heli);
            if (unit == null) return null;
            helis.Remove(unit); // from here on the plugin leaves it alone, so it can crash and drop its crates

            var killer = info != null ? info.InitiatorPlayer : null;
            BroadcastKill("Event.HeliDestroyed", killer);
            NextTick(CheckFinished);
            return null;
        }

        // Safety net: an event vehicle removed in any other way (admin command, another plugin)
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (cleaning || entity == null) return;

            var apc = entity as BradleyAPC;
            if (apc != null)
            {
                var unit = FindBradley(apc);
                if (unit == null) return;
                bradleys.Remove(unit);
                NextTick(CheckFinished);
                return;
            }

            var heli = entity as PatrolHelicopter;
            if (heli != null)
            {
                var unit = FindHeli(heli);
                if (unit == null) return;
                helis.Remove(unit);
                NextTick(CheckFinished);
            }
        }

        // Normally a helicopter leaves the map after heli.lifetimeminutes; event helicopters stay
        private object OnHelicopterRetire(PatrolHelicopterAI ai)
        {
            return FindHeli(ai) != null ? (object)true : null;
        }

        // Event helicopters do not flee when damaged ('zone' is a PatrolHelicopterAI.DangerZone)
        private object OnNoGoZoneAdded(PatrolHelicopterAI ai, object zone)
        {
            return FindHeli(ai) != null ? (object)true : null;
        }

        // Only players inside the event zone can be targeted. Returning null keeps the game's own line-of-sight check.
        private object CanHelicopterTarget(PatrolHelicopterAI ai, BasePlayer player)
        {
            if (player == null || FindHeli(ai) == null) return null;
            return InZone(player.transform.position) ? null : (object)false;
        }

        private object CanBradleyApcTarget(BradleyAPC apc, BaseEntity target)
        {
            if (!(target is BasePlayer) || FindBradley(apc) == null) return null;
            return InZone(target.transform.position) ? null : (object)false;
        }

        private object CanDeployScientists(BradleyAPC apc, BaseEntity attacker, List<GameObjectRef> scientistPrefabs, List<Vector3> spawnPositions)
        {
            if (config.BradleyScientists || FindBradley(apc) == null) return null;
            return false;
        }

        // Remember the scientists of event Bradleys, so they can be removed when the event is stopped
        private void OnScientistInitialized(BradleyAPC apc, ScientistNPC scientist, Vector3 spawnPos)
        {
            if (scientist == null || FindBradley(apc) == null) return;
            scientists.Add(scientist);
        }

        // Only when "Block event damage to player bases" is on (off by default). During an event (and shortly after),
        // damage to player bases around the gas station that does not come from a player is then blocked:
        // helicopter rockets, napalm, Bradley shells. Decay is never blocked.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || !config.ProtectBases) return null;
            if (phase != Phase.Running && Now > protectUntil) return null;
            if (info.InitiatorPlayer != null) return null;
            if (entity is BasePlayer) return null;
            if (!(entity is BuildingBlock) && entity.OwnerID == 0) return null; // not built or placed by a player
            if (info.damageTypes != null && info.damageTypes.GetMajorityDamageType() == Rust.DamageType.Decay) return null;
            if (Distance2D(entity.transform.position, center) > config.HeliLeashRadius + 50f) return null;
            return true;
        }

        #endregion

        #region Location

        private void FindMonuments()
        {
            monuments.Clear();
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null) return;

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null || monument.name == null) continue;
                if (monument.name.IndexOf(MonumentKey, StringComparison.OrdinalIgnoreCase) < 0) continue;
                monuments.Add(monument.transform.position);
            }
        }

        private bool PickLocation(out Vector3 position)
        {
            if (config.CustomPosition != null)
            {
                position = new Vector3(config.CustomPosition.X, config.CustomPosition.Y, config.CustomPosition.Z);
                return true;
            }
            if (monuments.Count == 0)
            {
                position = Vector3.zero;
                return false;
            }
            position = monuments[Random.Range(0, monuments.Count)];
            return true;
        }

        private bool InZone(Vector3 position) => Distance2D(position, center) <= config.ZoneRadius;

        // The stretch of road next to the event position, as waypoints a few metres apart; null if there is no road
        private List<Vector3> BuildRoadRoute(Vector3 origin)
        {
            if (TerrainMeta.Path == null || TerrainMeta.Path.Roads == null) return null;

            Vector3[] points = null;
            var nearest = -1;
            var nearestDistance = config.RoadSearchDistance;
            foreach (var road in TerrainMeta.Path.Roads)
            {
                var roadPoints = road != null && road.Path != null ? road.Path.Points : null;
                if (roadPoints == null || roadPoints.Length < 2) continue;
                for (var i = 0; i < roadPoints.Length; i++)
                {
                    var distance = Distance2D(roadPoints[i], origin);
                    if (distance >= nearestDistance) continue;
                    nearestDistance = distance;
                    nearest = i;
                    points = roadPoints;
                }
            }
            if (points == null) return null;

            // Walk along the road in both directions, measured along the road itself
            var first = nearest;
            var length = 0f;
            while (first > 0 && length < config.RoadLength)
            {
                length += Vector3.Distance(points[first], points[first - 1]);
                first--;
            }
            var last = nearest;
            length = 0f;
            while (last < points.Length - 1 && length < config.RoadLength)
            {
                length += Vector3.Distance(points[last], points[last + 1]);
                last++;
            }

            // Road points can be far apart; fill in so the Bradley always has a waypoint close by
            var route = new List<Vector3>();
            for (var i = first; i < last; i++)
            {
                var from = points[i];
                var to = points[i + 1];
                var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / RouteSpacing));
                for (var step = 0; step < steps; step++)
                    route.Add(Vector3.Lerp(from, to, step / (float)steps));
            }
            route.Add(points[last]);
            return route.Count >= 2 ? route : null;
        }

        #endregion

        #region Event Flow

        private void ScheduleAuto()
        {
            DestroyTimer(ref autoTimer);
            nextAutoAt = 0f;
            if (!config.AutoStart) return;

            var seconds = Random.Range(config.AutoMinMinutes, config.AutoMaxMinutes) * 60f;
            nextAutoAt = Now + seconds;
            autoTimer = timer.Once(seconds, AutoStart);
        }

        private void AutoStart()
        {
            autoTimer = null;
            nextAutoAt = 0f;
            if (phase != Phase.Idle) return;

            if (BasePlayer.activePlayerList.Count < config.AutoMinPlayers)
            {
                nextAutoAt = Now + AutoRetrySeconds;
                autoTimer = timer.Once(AutoRetrySeconds, AutoStart);
                return;
            }

            var difficulty = PickDifficulty();
            Vector3 position;
            if (difficulty == null || !PickLocation(out position))
            {
                PrintWarning(difficulty == null
                    ? "Automatic event skipped: no difficulty has a chance weight above 0 and at least one vehicle."
                    : "Automatic event skipped: no Oxum's Gas Station on this map and no custom position set.");
                ScheduleAuto();
                return;
            }

            current = difficulty;
            center = position;
            if (config.AnnounceMinutes <= 0f)
            {
                Begin();
                return;
            }

            phase = Phase.Announced;
            startAt = Now + config.AnnounceMinutes * 60f;
            CreateMarker();
            var announced = difficulty;
            Broadcast("Event.Announce", id => new object[] { announced.Name, FormatTime(config.AnnounceMinutes * 60f, id), Composition(announced.Bradleys, announced.Helicopters, id) });
            startTimer = timer.Once(config.AnnounceMinutes * 60f, Begin);
        }

        private Difficulty PickDifficulty()
        {
            var total = 0f;
            foreach (var difficulty in config.Difficulties)
                if (IsUsable(difficulty)) total += difficulty.Weight;
            if (total <= 0f) return null;

            var roll = Random.Range(0f, total);
            Difficulty picked = null;
            foreach (var difficulty in config.Difficulties)
            {
                if (!IsUsable(difficulty)) continue;
                picked = difficulty;
                roll -= difficulty.Weight;
                if (roll <= 0f) break;
            }
            return picked;
        }

        private static bool IsUsable(Difficulty difficulty) =>
            difficulty.Weight > 0f && difficulty.Bradleys + difficulty.Helicopters > 0;

        private Difficulty FindDifficulty(string name)
        {
            foreach (var difficulty in config.Difficulties)
                if (string.Equals(difficulty.Name, name, StringComparison.OrdinalIgnoreCase)) return difficulty;
            return null;
        }

        private string DifficultyNames()
        {
            var sb = new StringBuilder();
            foreach (var difficulty in config.Difficulties)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(difficulty.Name);
            }
            return sb.ToString();
        }

        // Spawns everything; 'current' and 'center' are already set
        private void Begin()
        {
            DestroyTimer(ref startTimer);
            DestroyTimer(ref autoTimer);
            nextAutoAt = 0f;

            phase = Phase.Running;
            endTime = Now + current.TimeLimitMinutes * 60f;
            endWarned = false;

            foreach (var hook in EventHooks) Subscribe(hook);
            if (config.ProtectBases) Subscribe(nameof(OnEntityTakeDamage));

            if (!Alive(marker)) CreateMarker();
            UpdateTimeMarker(); // an announced event shows "starts in"; switch to the time left at once
            SpawnBradleys(current.Bradleys);
            for (var i = 0; i < current.Helicopters; i++) SpawnHeli();

            if (bradleys.Count == 0 && helis.Count == 0)
            {
                PrintError("The event could not spawn any vehicle and was cancelled.");
                Finish(EndReason.Stopped, false);
                return;
            }

            var started = current;
            var bradleyCount = bradleys.Count;
            var heliCount = helis.Count;
            Broadcast("Event.Started", id => new object[] { started.Name, Composition(bradleyCount, heliCount, id), FormatTime(started.TimeLimitMinutes * 60f, id) });
            Puts("Event started: {0} with {1} Bradley(s) and {2} helicopter(s).", started.Name, bradleyCount, heliCount);

            tickTimer = timer.Every(TickSeconds, Tick);
        }

        private void Tick()
        {
            if (phase != Phase.Running) return;
            var now = Now;

            for (var i = bradleys.Count - 1; i >= 0; i--)
            {
                if (!Alive(bradleys[i].Apc)) bradleys.RemoveAt(i);
                else DriveBradley(bradleys[i], now);
            }
            for (var i = helis.Count - 1; i >= 0; i--)
            {
                if (!Alive(helis[i].Heli) || helis[i].Ai == null) helis.RemoveAt(i);
                else LeashHeli(helis[i]);
            }
            for (var i = scientists.Count - 1; i >= 0; i--)
                if (!Alive(scientists[i])) scientists.RemoveAt(i);

            if (bradleys.Count == 0 && helis.Count == 0)
            {
                Finish(EndReason.Won, true);
                return;
            }
            if (now >= endTime)
            {
                Finish(EndReason.TimeUp, true);
                return;
            }

            var warnSeconds = config.EndWarningMinutes * 60f;
            if (!endWarned && warnSeconds > 0f && current.TimeLimitMinutes * 60f > warnSeconds && endTime - now <= warnSeconds)
            {
                endWarned = true;
                var left = endTime - now;
                var bradleyCount = bradleys.Count;
                var heliCount = helis.Count;
                Broadcast("Event.TimeWarning", id => new object[] { FormatTime(left, id), Composition(bradleyCount, heliCount, id) });
            }
        }

        private void CheckFinished()
        {
            if (phase == Phase.Running && bradleys.Count == 0 && helis.Count == 0) Finish(EndReason.Won, true);
        }

        private void Finish(EndReason reason, bool announce)
        {
            var wasActive = phase != Phase.Idle;
            if (phase == Phase.Running) protectUntil = Now + ProtectLingerSeconds;
            phase = Phase.Idle;

            Cleanup();
            foreach (var hook in EventHooks) Unsubscribe(hook);
            timer.Once(ProtectLingerSeconds + 1f, () =>
            {
                if (phase != Phase.Running && Now > protectUntil) Unsubscribe(nameof(OnEntityTakeDamage));
            });

            if (announce && wasActive)
            {
                if (reason == EndReason.Won) Broadcast("Event.Won");
                else if (reason == EndReason.TimeUp) Broadcast("Event.TimeUp");
                else Broadcast("Event.Stopped");
            }
            if (wasActive) Puts("Event ended: {0}.", reason);

            current = null;
            ScheduleAuto();
        }

        // Removes everything the plugin spawned that is still under its control
        private void Cleanup()
        {
            DestroyTimer(ref startTimer);
            DestroyTimer(ref tickTimer);
            DestroyTimer(ref markerTimer);

            cleaning = true;
            foreach (var scientist in scientists)
                if (Alive(scientist)) scientist.Kill();
            foreach (var unit in bradleys)
                if (Alive(unit.Apc)) unit.Apc.Kill();
            foreach (var unit in helis)
                if (Alive(unit.Heli)) unit.Heli.Kill();
            if (Alive(marker)) marker.Kill();
            if (Alive(timeMarker)) timeMarker.Kill();
            cleaning = false;

            scientists.Clear();
            bradleys.Clear();
            helis.Clear();
            marker = null;
            timeMarker = null;
            timeMarkerText = null;
        }

        #endregion

        #region Bradleys

        private void SpawnBradleys(int count)
        {
            if (count <= 0) return;

            var route = BuildRoadRoute(center);
            if (route == null)
                PrintWarning("No road found within {0} m of the event position; the Bradleys will not drive.", config.RoadSearchDistance.ToString("0", CultureInfo.InvariantCulture));

            // Each Bradley gets its own part of the road, so they never have to pass each other
            var sectionSize = route != null ? route.Count / count : 0;
            var ownSections = count > 1 && sectionSize >= MinSectionPoints;

            for (var i = 0; i < count; i++)
            {
                List<Vector3> section = null;
                Vector3 position;
                var facing = new Vector3(0f, 0f, 1f);

                if (route == null)
                {
                    position = center + new Vector3(i * 10f, 1f, 0f);
                }
                else
                {
                    int startIndex;
                    if (ownSections)
                    {
                        var start = i * sectionSize;
                        var end = i == count - 1 ? route.Count : start + sectionSize - SectionGapPoints;
                        section = route.GetRange(start, end - start);
                        startIndex = 0;
                    }
                    else
                    {
                        section = route;
                        startIndex = i * route.Count / count;
                    }

                    position = section[startIndex] + new Vector3(0f, 0.5f, 0f);
                    var next = startIndex + 1 < section.Count ? section[startIndex + 1] - section[startIndex] : section[startIndex] - section[startIndex - 1];
                    next.y = 0f;
                    if (next.sqrMagnitude > 0.01f) facing = next;
                }

                var apc = GameManager.server.CreateEntity(BradleyPrefab, position, Quaternion.LookRotation(facing)) as BradleyAPC;
                if (apc == null)
                {
                    PrintError("Could not create a Bradley APC (prefab not found?).");
                    return;
                }

                apc.enableSaving = false; // never written to the save file, so nothing is left behind after a restart
                apc.Spawn();
                apc.maxCratesToSpawn = current.CratesPerBradley;
                bradleys.Add(new BradleyUnit { Apc = apc, Route = section });
            }
        }

        // Gives the Bradley its next stretch of waypoints. The game drives it along currentPath by itself and
        // empties the list at the end; with patrolPath unset (the case here) the game never picks a route of its own.
        private void DriveBradley(BradleyUnit unit, float now)
        {
            var apc = unit.Apc;
            var route = unit.Route;
            if (route == null || route.Count < 2 || apc.currentPath == null) return;
            if (now < unit.WaitUntil) return; // standing guard at the end of its stretch

            var position = apc.transform.position;
            var nearest = NearestIndex(route, position);

            if (unit.HasLeg)
            {
                var endIndex = unit.Forward ? route.Count - 1 : 0;
                var arrived = Distance2D(position, unit.LegEnd) < ArriveDistance || (apc.currentPath.Count == 0 && nearest == endIndex);
                var timedOut = now - unit.LegStarted > LegTimeoutSeconds;
                if (!arrived && !timedOut && apc.currentPath.Count > 0) return; // still driving

                if (arrived || timedOut)
                {
                    unit.Forward = !unit.Forward; // turn around (a timeout also frees a Bradley that got stuck)
                    unit.HasLeg = false;
                    if (arrived)
                    {
                        // Wait a moment before driving back, so it does not shuttle up and down non-stop
                        apc.currentPath.Clear();
                        apc.currentPathIndex = -1;
                        unit.WaitUntil = now + Random.Range(EndPauseMinSeconds, EndPauseMaxSeconds);
                        return;
                    }
                }
                // Otherwise the game emptied the path halfway (it does while scientists are deployed): same direction again
            }

            if (!unit.HasLeg) unit.LegStarted = now;
            apc.currentPath.Clear();
            if (unit.Forward)
            {
                for (var i = Math.Min(nearest + 1, route.Count - 1); i < route.Count; i++) apc.currentPath.Add(route[i]);
                unit.LegEnd = route[route.Count - 1];
            }
            else
            {
                for (var i = Math.Max(nearest - 1, 0); i >= 0; i--) apc.currentPath.Add(route[i]);
                unit.LegEnd = route[0];
            }
            apc.currentPathIndex = -1;
            apc.pathLooping = false;
            apc.finalDestination = unit.LegEnd;
            unit.HasLeg = true;
        }

        private static int NearestIndex(List<Vector3> route, Vector3 position)
        {
            var best = 0;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < route.Count; i++)
            {
                var distance = Distance2D(route[i], position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }

        private BradleyUnit FindBradley(BradleyAPC apc)
        {
            if (apc == null) return null;
            foreach (var unit in bradleys)
                if (unit.Apc == apc) return unit;
            return null;
        }

        #endregion

        #region Helicopters

        private void SpawnHeli()
        {
            var angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            // Fly in from a distance, but from inside the area the leash allows
            var spawnDistance = Mathf.Min(HeliSpawnDistance, config.HeliLeashRadius + HeliFarOutMargin * 0.5f);
            var spawnPosition = center + new Vector3(Mathf.Cos(angle) * spawnDistance, HeliSpawnHeight, Mathf.Sin(angle) * spawnDistance);

            var heli = GameManager.server.CreateEntity(HeliPrefab, spawnPosition, Quaternion.identity) as PatrolHelicopter;
            if (heli == null)
            {
                PrintError("Could not create a patrol helicopter (prefab not found?).");
                return;
            }
            var ai = heli.GetComponent<PatrolHelicopterAI>();
            if (ai == null)
            {
                PrintError("The patrol helicopter has no AI component; it was not spawned.");
                return;
            }

            heli.enableSaving = false;
            // Makes the gas station its interest zone. This call also moves the helicopter to the edge of the map,
            // so the position is set again after spawning.
            ai.SetInitialDestination(HoverPoint(), 0.25f);
            heli.Spawn();
            heli.transform.position = spawnPosition;
            heli.maxCratesToSpawn = current.CratesPerHelicopter;
            helis.Add(new HeliUnit { Heli = heli, Ai = ai });
        }

        // Sends a helicopter back when it is, or is heading, outside the leash radius
        private void LeashHeli(HeliUnit unit)
        {
            var ai = unit.Ai;
            // A strafe run starts almost 200 m away from the target; interrupting it would stop every rocket attack
            if (ai._currentState == PatrolHelicopterAI.aiState.STRAFE) return;

            // Where it is heading matters most: that catches a helicopter the moment it decides to leave.
            // One that is outside but already on its way back is left alone, unless it is far outside.
            var leash = config.HeliLeashRadius;
            var headingOut = Distance2D(ai.destination, center) > leash;
            var farOut = Distance2D(unit.Heli.transform.position, center) > leash + HeliFarOutMargin;
            if (!headingOut && !farOut) return;

            ai.interestZoneOrigin = center;
            ai.ExitCurrentState();
            ai.State_Move_Enter(HoverPoint());
        }

        private Vector3 HoverPoint()
        {
            var angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            var distance = Random.Range(0f, HeliHoverSpread);
            return center + new Vector3(Mathf.Cos(angle) * distance, HeliHoverHeight, Mathf.Sin(angle) * distance);
        }

        private HeliUnit FindHeli(PatrolHelicopterAI ai)
        {
            if (ai == null) return null;
            foreach (var unit in helis)
                if (unit.Ai == ai) return unit;
            return null;
        }

        private HeliUnit FindHeli(PatrolHelicopter heli)
        {
            if (heli == null) return null;
            foreach (var unit in helis)
                if (unit.Heli == heli) return unit;
            return null;
        }

        #endregion

        #region Map Marker

        private void CreateMarker()
        {
            if (!config.MapMarker) return;
            if (config.MapMarkerTime && !Alive(timeMarker)) CreateTimeMarker();
            if (Alive(marker)) return;

            var entity = GameManager.server.CreateEntity(MarkerPrefab, center, Quaternion.identity) as MapMarkerGenericRadius;
            if (entity == null)
            {
                PrintWarning("Could not create the map marker; the event continues without it.");
                return;
            }
            entity.alpha = 0.6f;
            entity.color1 = Color.red;
            entity.color2 = Color.black;
            entity.radius = config.MapMarkerRadius;
            entity.enableSaving = false;
            entity.Spawn();
            entity.SendUpdate();
            marker = entity;
        }

        // The map marker of a vending machine without the machine: a shop icon players can hover over.
        // Its shop name is the only text a map marker can show, so the plugin puts the time left in it.
        private void CreateTimeMarker()
        {
            var entity = GameManager.server.CreateEntity(TimeMarkerPrefab, center, Quaternion.identity) as VendingMachineMapMarker;
            if (entity == null)
            {
                PrintWarning("Could not create the time marker; the event continues without it.");
                return;
            }
            timeMarkerText = TimeMarkerText();
            entity.enabled = false; // same as RaidableBases and DangerousTreasures do before spawning this marker
            entity.markerShopName = timeMarkerText;
            entity.enableSaving = false;
            entity.Spawn();
            timeMarker = entity;

            DestroyTimer(ref markerTimer);
            markerTimer = timer.Every(TickSeconds, UpdateTimeMarker);
        }

        // One text for everyone, so it uses the server's default language
        private string TimeMarkerText()
        {
            if (current == null) return string.Empty;
            return phase == Phase.Announced
                ? GetMsg("Marker.Announced", null, current.Name, FormatTime(startAt - Now, null))
                : GetMsg("Marker.Running", null, current.Name, FormatTime(endTime - Now, null));
        }

        // Runs every tick, but only sends something to players when the text changes (once a minute, or every
        // tick during the last minute)
        private void UpdateTimeMarker()
        {
            if (phase == Phase.Idle || !Alive(timeMarker)) return;
            var text = TimeMarkerText();
            if (text == timeMarkerText) return;
            timeMarkerText = text;
            timeMarker.markerShopName = text;
            timeMarker.SendNetworkUpdate();
        }

        #endregion

        #region Commands

        private bool IsAdmin(BasePlayer player) =>
            player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));

        [ChatCommand("oxum")]
        private void CmdOxum(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var id = player.UserIDString;
            var action = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";

            if (action == "status")
            {
                player.ChatMessage(StatusText(id, IsAdmin(player)));
                return;
            }
            if (!IsAdmin(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            switch (action)
            {
                case "start":
                    player.ChatMessage(config.ChatPrefix + AdminStart(args.Length > 1 ? args[1] : null, id));
                    break;
                case "stop":
                    player.ChatMessage(config.ChatPrefix + AdminStop(id));
                    break;
                case "setpos":
                    var position = player.transform.position;
                    config.CustomPosition = new SavedPosition { X = position.x, Y = position.y, Z = position.z };
                    SaveConfig();
                    Puts("{0} set the custom event position.", player.displayName);
                    Reply(player, "SetPos.Done");
                    break;
                case "clearpos":
                    config.CustomPosition = null;
                    SaveConfig();
                    Puts("{0} cleared the custom event position.", player.displayName);
                    Reply(player, "ClearPos.Done", monuments.Count);
                    break;
                default:
                    Reply(player, "Help", DifficultyNames());
                    break;
            }
        }

        private bool IsConsoleAdmin(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null) return true; // server console or RCON
            if (IsAdmin(arg.Player())) return true;
            arg.ReplyWith("You don't have permission to use this command.");
            return false;
        }

        private static string ConsoleUserId(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            return player != null ? player.UserIDString : null;
        }

        [ConsoleCommand("oxum.start")]
        private void CcStart(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            // Read arguments through the game's helpers; arg.Args is no longer a plain string array
            var name = arg.HasArgs(1) ? arg.GetString(0) : null;
            arg.ReplyWith(AdminStart(name, ConsoleUserId(arg)));
        }

        [ConsoleCommand("oxum.stop")]
        private void CcStop(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            arg.ReplyWith(AdminStop(ConsoleUserId(arg)));
        }

        [ConsoleCommand("oxum.status")]
        private void CcStatus(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            arg.ReplyWith(StatusText(ConsoleUserId(arg), true));
        }

        // Starts an event right away, without the announcement countdown. Returns the reply for the admin.
        private string AdminStart(string difficultyName, string userId)
        {
            if (phase == Phase.Running) return GetMsg("Admin.AlreadyRunning", userId);

            Difficulty difficulty;
            if (string.IsNullOrEmpty(difficultyName))
            {
                // An announced event that is started early keeps its difficulty and location
                difficulty = phase == Phase.Announced ? current : PickDifficulty();
                if (difficulty == null) return GetMsg("Admin.NoDifficulty", userId, DifficultyNames());
            }
            else
            {
                difficulty = FindDifficulty(difficultyName);
                if (difficulty == null) return GetMsg("Admin.UnknownDifficulty", userId, difficultyName, DifficultyNames());
                if (difficulty.Bradleys + difficulty.Helicopters <= 0) return GetMsg("Admin.EmptyDifficulty", userId, difficulty.Name);
            }

            if (phase != Phase.Announced)
            {
                Vector3 position;
                if (!PickLocation(out position)) return GetMsg("Admin.NoLocation", userId);
                center = position;
            }
            current = difficulty;
            Begin();
            return phase == Phase.Running
                ? GetMsg("Admin.Started", userId, difficulty.Name)
                : GetMsg("Admin.StartFailed", userId);
        }

        private string AdminStop(string userId)
        {
            if (phase == Phase.Idle) return GetMsg("Admin.NotRunning", userId);
            Finish(EndReason.Stopped, true);
            return GetMsg("Admin.Stopped", userId);
        }

        private string StatusText(string userId, bool admin)
        {
            var sb = new StringBuilder(config.ChatPrefix);
            if (phase == Phase.Running)
                sb.Append(GetMsg("Status.Running", userId, current.Name, Composition(bradleys.Count, helis.Count, userId), FormatTime(endTime - Now, userId)));
            else if (phase == Phase.Announced)
                sb.Append(GetMsg("Status.Announced", userId, current.Name));
            else
                sb.Append(GetMsg("Status.Idle", userId));

            if (!admin) return sb.ToString();

            sb.Append('\n');
            if (config.CustomPosition != null) sb.Append(GetMsg("Status.CustomPosition", userId));
            else sb.Append(GetMsg("Status.Monuments", userId, monuments.Count));
            if (phase == Phase.Idle)
            {
                sb.Append('\n');
                if (nextAutoAt > 0f) sb.Append(GetMsg("Status.NextAuto", userId, FormatTime(nextAutoAt - Now, userId)));
                else sb.Append(GetMsg("Status.AutoOff", userId));
            }
            sb.Append('\n').Append(GetMsg("Help", userId, DifficultyNames()));
            return sb.ToString();
        }

        #endregion

        #region Helpers & Localization

        // Unity treats a destroyed object as equal to null, so both checks are needed
        private static bool Alive(BaseNetworkable entity) => entity != null && !entity.IsDestroyed;

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            var x = a.x - b.x;
            var z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private static void DestroyTimer(ref Timer t)
        {
            if (t != null) t.Destroy();
            t = null;
        }

        private string GetMsg(string key, string userId, params object[] args)
        {
            var text = lang.GetMessage(key, this, userId);
            return args == null || args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }

        private void Reply(BasePlayer player, string key, params object[] args) =>
            player.ChatMessage(config.ChatPrefix + GetMsg(key, player.UserIDString, args));

        private void Broadcast(string key) => Broadcast(key, null);

        // 'args' builds the arguments per player, so each player gets the text in their own language
        private void Broadcast(string key, Func<string, object[]> args)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                var id = player.UserIDString;
                player.ChatMessage(config.ChatPrefix + GetMsg(key, id, args != null ? args(id) : null));
            }
        }

        private void BroadcastKill(string key, BasePlayer killer)
        {
            var name = killer != null && !killer.IsNpc ? killer.displayName : null;
            var bradleyCount = bradleys.Count;
            var heliCount = helis.Count;
            Broadcast(key, id => new object[] { name ?? GetMsg("Unknown", id), Composition(bradleyCount, heliCount, id) });
        }

        // "2 Bradleys and 1 patrol helicopter"
        private string Composition(int bradleyCount, int heliCount, string userId)
        {
            var bradleyText = bradleyCount > 0 ? GetMsg(bradleyCount == 1 ? "Unit.Bradley" : "Unit.Bradleys", userId, bradleyCount) : null;
            var heliText = heliCount > 0 ? GetMsg(heliCount == 1 ? "Unit.Heli" : "Unit.Helis", userId, heliCount) : null;
            if (bradleyText != null && heliText != null) return GetMsg("Unit.And", userId, bradleyText, heliText);
            return bradleyText ?? heliText ?? GetMsg("Unit.None", userId);
        }

        private string FormatTime(float seconds, string userId)
        {
            if (seconds < 0f) seconds = 0f;
            if (seconds < 60f) return GetMsg("Time.Seconds", userId, Mathf.CeilToInt(seconds));
            var minutes = Mathf.CeilToInt(seconds / 60f);
            return GetMsg(minutes == 1 ? "Time.Minute" : "Time.Minutes", userId, minutes);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Event.Announce"] = "A <color=#ffd479>{0}</color> event starts at Oxum's Gas Station in {1}. Expect {2}. Stay away if you are not ready!",
                ["Event.Started"] = "The <color=#ffd479>{0}</color> event at Oxum's Gas Station has started: {1}. You have {2} to destroy everything.",
                ["Event.BradleyDestroyed"] = "<color=#ffd479>{0}</color> destroyed a Bradley! Still left: {1}.",
                ["Event.HeliDestroyed"] = "<color=#ffd479>{0}</color> shot down a patrol helicopter! Still left: {1}.",
                ["Event.TimeWarning"] = "The event at Oxum's Gas Station ends in {0}. Still left: {1}.",
                ["Event.Won"] = "Event complete! Everything at Oxum's Gas Station has been destroyed.",
                ["Event.TimeUp"] = "Time is up! The remaining vehicles at Oxum's Gas Station have left without dropping loot.",
                ["Event.Stopped"] = "The event at Oxum's Gas Station has been cancelled.",

                ["Unit.Bradley"] = "{0} Bradley",
                ["Unit.Bradleys"] = "{0} Bradleys",
                ["Unit.Heli"] = "{0} patrol helicopter",
                ["Unit.Helis"] = "{0} patrol helicopters",
                ["Unit.And"] = "{0} and {1}",
                ["Unit.None"] = "nothing",
                ["Unknown"] = "Someone",

                ["Time.Seconds"] = "{0} seconds",
                ["Time.Minute"] = "{0} minute",
                ["Time.Minutes"] = "{0} minutes",

                ["Marker.Announced"] = "Oxum's Event ({0}) - starts in {1}",
                ["Marker.Running"] = "Oxum's Event ({0}) - {1} left",

                ["Status.Idle"] = "There is no event at Oxum's Gas Station right now.",
                ["Status.Announced"] = "A {0} event at Oxum's Gas Station is about to start.",
                ["Status.Running"] = "A {0} event is running at Oxum's Gas Station. Still left: {1}. Time left: {2}.",
                ["Status.Monuments"] = "Gas stations found on this map: {0}.",
                ["Status.CustomPosition"] = "Using the custom event position (remove it with /oxum clearpos).",
                ["Status.NextAuto"] = "Next automatic event in about {0}.",
                ["Status.AutoOff"] = "Automatic start is off or not scheduled.",

                ["NoPermission"] = "You don't have permission to use this command.",
                ["Help"] = "Commands: /oxum status, /oxum start [difficulty], /oxum stop, /oxum setpos, /oxum clearpos. Difficulties: {0}.",
                ["SetPos.Done"] = "Custom event position saved. Events now take place where you are standing.",
                ["ClearPos.Done"] = "Custom event position removed. Gas stations found on this map: {0}.",

                ["Admin.Started"] = "Started a {0} event.",
                ["Admin.StartFailed"] = "The event could not start; check the server console.",
                ["Admin.Stopped"] = "Event stopped; everything the plugin spawned has been removed.",
                ["Admin.AlreadyRunning"] = "An event is already running. Stop it first with /oxum stop.",
                ["Admin.NotRunning"] = "There is no event to stop.",
                ["Admin.UnknownDifficulty"] = "Unknown difficulty '{0}'. Choose from: {1}.",
                ["Admin.EmptyDifficulty"] = "Difficulty '{0}' has 0 Bradleys and 0 helicopters in the config.",
                ["Admin.NoDifficulty"] = "No difficulty can be picked at random (all have chance weight 0 or no vehicles). Name one: {0}.",
                ["Admin.NoLocation"] = "No Oxum's Gas Station on this map. Stand where the event should be and use /oxum setpos."
            }, this);
        }

        #endregion
    }
}
