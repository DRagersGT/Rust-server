using Facepunch;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using Rust;
using Rust.Ai.Gen2;
using Rust.Ai.Gen2.Nav;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using static Oxide.Plugins.RaidableBasesExtensionMethods.ExtensionMethods;

namespace Oxide.Plugins
{
    [Info("Raidable Bases", "nivex", "3.2.0")]
    [Description("Create fully automated raidable bases with npcs.")]
    public class RaidableBases : RustPlugin
    {
        [PluginReference]
        Plugin
        AbandonedBases, DangerousTreasures, ZoneManager, BankSystem, IQEconomic, Economics, ServerRewards, GUIAnnouncements, AdvancedAlerts, Archery, Space, PocketDimensions, FauxAdmin, PreventLooting,
        IQDronePatrol, Friends, Clans, Kits, TruePVE, AegisPVE, RealPVE, SimplePVE, NightLantern, Wizardry, NextGenPVE, Imperium, Backpacks, BaseRepair, Notify, SkillTree, ShoppyStock, XPerience, XLevels;

        private new const string Name = "RaidableBases";
        private const int targetMask = 8454145;
        private const int visibleMask = 10551553;
        private const int targetMask2 = 10551313;
        private const int manualMask = 1084293393;
        private const int blockLayers = 2228480;
        private const int queueLayers = 2294528;
        private const int gridLayers = 327936;
        private const float M_RADIUS = 25f;
        private const float CELL_SIZE = 12.5f;
        private float OceanLevel;
        private bool wiped;
        private bool IsUnloading;
        private bool IsShuttingDown;
        private bool bypassRestarting;
        private bool DebugMode;
        private int despawnLimit = 10;
        private static ulong BotIdCounter = 534922525;
        private const ulong RB_SKIN_ID = 3710562502;

        private SkinSettingsImportedWorkshop ImportedWorkshopSkins = new();
        private ProtectionProperties _elevatorProtection;
        private ProtectionProperties _turretProtection;
        private AutomatedController Automated;
        private StoredData data = new();
        public BuildingTables Buildings = new();
        public QueueController Queues;
        private SkinsPlugin skinsPlugin = new();
        private Coroutine setupCopyPasteObstructionRadius;
        public List<string> DestroyedPrefabs = new();
        private List<Coroutine> loadCoroutines = new();
        public List<RaidableBase> Raids = new();
        public Dictionary<ulong, DelaySettings> PvpDelay = new();
        public Dictionary<string, SkinInfo> Skins = new();
        private Dictionary<string, PasteData> _pasteData = new();
        private Dictionary<ulong, HumanoidBrain> HumanoidBrains = new();
        private Dictionary<NetworkableId, RaidableBase> EntityToRaid = new();
        private Dictionary<NetworkableId, BMGELEVATOR> _elevators = new();
        private Dictionary<string, ItemDefinition> PaidDeployableItems = new();
        private Dictionary<string, ItemDefinition> DeployableItems = new();
        private Dictionary<ItemDefinition, string> ItemDefinitions = new();
        private Dictionary<ItemDefinition, ItemModConsume> _itemModConsume = new();
        private readonly Dictionary<string, string> TypeNameLookup = new();
        private readonly List<string> ExcludedMounts = new() { "beachchair", "boogieboard", "cardtable", "chair", "chippyarcademachine", "computerstation", "drumkit", "microphonestand", "piano", "secretlabchair", "slotmachine", "sofa", "xylophone" };
        private readonly List<string> Blocks = new() { "wall.frame.cell", "wall.doorway", "wall", "wall.frame", "wall.half", "wall.low", "wall.window", "foundation.triangle", "foundation", "wall.external.high.wood", "wall.external.high.stone", "wall.external.high.ice", "floor.triangle.frame", "floor.triangle", "floor.frame" };
        private readonly List<string> TrueDamage = new() { "spikes.floor", "barricade.metal", "barricade.woodwire", "barricade.wood", "wall.external.high.wood", "wall.external.high.stone", "wall.external.high.ice" };
        private readonly List<string> arguments = new() { "add", "remove", "list", "clean", "enable_dome_marker", "toggle", "stability", "inventories", "maintained", "scheduled", "noexplosivecosts" };
        private readonly List<uint> CupboardPrefabIDs = new() { 2476970476, 785685130, 3932172323 };
        private readonly IPlayer _consolePlayer = new Game.Rust.Libraries.Covalence.RustConsolePlayer();
        private readonly List<BaseEntity.Slot> _checkSlots = new() { BaseEntity.Slot.Lock, BaseEntity.Slot.UpperModifier, BaseEntity.Slot.MiddleModifier, BaseEntity.Slot.LowerModifier };

        public class RaidElevator
        {
            public BMGELEVATOR BMG;
            public Elevator Elevator;
            public RaidableBase raid;
            public Elevator Entity => BMG?._elevator ?? Elevator;

            public bool IsBMG() => BMG != null && raid != null;
            public bool IsVanilla() => raid != null && !Elevator.IsKilled();
            public bool CanUseElevator(BasePlayer player)
            {
                var elevator = Entity;
                if (elevator.IsKilled()) return false;
                if (raid.Options.Elevators.RequiresPower && !elevator.IsPowered()) return false;
                return raid.HasCardPermission(player) && raid.HasBuildingPermission(player);
            }
        }

        public class PasteData
        {
            public bool valid;
            public float radius;
            public List<Vector3> foundations;
            public List<string> invalid;
            public PasteData() { }
        }

        public static class RaidableMode
        {
            public const string Normal = "Normal", Random = "Random", Points = "Points", Disabled = "Disabled";
        }

        public float MaxTerrainY = 150f;

        public struct DamageMultiplier { public DamageType index; public float amount; }

        public enum DamageResult { None, Allowed, Blocked }

        public enum RaidableType { None, Manual, Scheduled, Maintained, Grid }

        public enum AlliedType { All, Clan, Friend, Team }

        public enum CacheType { Close, Delete, Generic, Generic2, Temporary, Privilege, Submerged }

        public enum ConstructionType { Barricade, Ladder, Any }

        public enum SkinType { Box, Deployable, Loot, Npc }

        public class StoredData
        {
            public class NavigationBuild
            {
                public Vector3 center;
                public Vector3 size;
                public NavigationBuild() { }
                public NavigationBuild(Bounds bounds)
                {
                    center = bounds.center;
                    size = bounds.size;
                }
                public Bounds ToBounds() => new(center, size);
            }
            public RotationCycle Cycle = new();
            public Dictionary<string, PlayerInfo> Players = new();
            public List<NavigationBuild> RustNavigationBuilds = new();
            public DateTime RaidTime = DateTime.MinValue;
            public int TotalEvents;
            public string WipeId;
            public int protocol = -1;
            public StoredData() { }
            public PlayerInfo GetPlayerInfo(string userid)
            {
                if (!Players.TryGetValue(userid, out var info))
                {
                    Players[userid] = info = new();
                }
                return info;
            }
        }

        public class RandomBase
        {
            public float heightAdj, typeDistance, protectionRadius, safeRadius, ignoreRadius, buildRadius, baseHeight;
            public bool autoHeight, stability, checkTerrain, Sorted, Save, IsPasting, inventories = true;
            public string BaseName, username, id;
            public int attempts, errors;
            public ulong userid;
            public Vector3 Position;
            public IPlayer user;
            public RaidableType type;
            public BasePlayer owner;
            public PasteData pasteData;
            public BaseProfile Profile;
            public RaidableSpawns spawns;
            public RaidableBases Instance;
            public RaidableBase raid;
            public HashSet<ulong> members = new();

            public BuildingOptions options => Profile.Options;
            public bool isCustomSpawn => spawns != null && spawns.IsCustomSpawn;
            public bool HasSpawns() => spawns.Spawns.Count > 0;
        }

        public class BackpackData : Pool.IPooled
        {
            public BackpackData() { }
            public void EnterPool() { Pool.FreeUnmanaged(ref containers); _player = null; userid = 0uL; }
            public void LeavePool() => containers = Pool.Get<List<DroppedItemContainer>>();
            public List<DroppedItemContainer> containers;
            public BasePlayer _player;
            public ulong userid;
            public bool IsEmpty => containers.Count == 0 || containers.All(x => x.IsKilled());
            public BasePlayer player { get { if (_player == null) { _player = RustCore.FindPlayerById(userid); } return _player; } }
        }

        public class DelaySettings
        {
            public RaidableBase raid;
            public string mode;
            public Timer Timer;
            public double time;
            public void Destroy()
            {
                if (Timer != null && !Timer.Destroyed)
                {
                    Timer.Callback();
                    Timer.Destroy();
                }
            }
        }

        public class SkinInfo
        {
            public List<ulong> skins = new(), workshopSkins = new(), importedWorkshopSkins = new(), allSkins = new();
        }

        public class RankedRecord
        {
            public string Permission = string.Empty;
            public string Group = string.Empty;
            public string Mode = string.Empty;
            internal bool IsValid => !string.IsNullOrWhiteSpace(Permission) && !string.IsNullOrWhiteSpace(Group) && !string.IsNullOrWhiteSpace(Mode);
            public RankedRecord(string permission, string group, string mode)
            {
                (Permission, Group, Mode) = (permission, group, mode);
            }
            public RankedRecord() { }
        }

        public class RaidableSpawnLocation : IEquatable<RaidableSpawnLocation>
        {
            public List<Vector3> Surroundings = new();
            public Vector3 Location;
            public MinMax LandLevel;
            public float WaterHeight;
            public float TerrainHeight;
            public float SpawnHeight;
            public float Radius;
            public float RailRadius;
            public bool AutoHeight;
            public int? biome;
            public RaidableSpawnLocation(Vector3 location)
            {
                Location = location;
            }
            public bool Equals(RaidableSpawnLocation other) => Location.Equals(other.Location);
            public override bool Equals(object obj) => obj is RaidableSpawnLocation other && Equals(other);
            public override int GetHashCode() => base.GetHashCode();
        }

        public class ZoneInfo
        {
            internal string ZoneId;
            internal Quaternion Rotation;
            internal Vector3 Position;
            internal Vector3 Size;
            internal Vector3 extents;
            internal float Distance;
            internal bool IsBlocked;

            public ZoneInfo(string id, Vector3 pos, Quaternion rot, float radius, Vector3 size, bool isBlocked, float dist)
            {
                (IsBlocked, ZoneId, Position, Rotation) = (isBlocked, id, pos, rot);

                dist = Mathf.Max(dist, 100f);



                Distance = radius + M_RADIUS + dist;


                if (size != Vector3.zero)
                {
                    Size = size + new Vector3(dist, Position.y + 100f, dist);
                    extents = Size * 0.5f;
                }
            }

            public bool IsPositionInZone(Vector3 a)
            {
                if (Size != Vector3.zero)
                {
                    Vector3 v = Quaternion.Inverse(Rotation) * (a - Position);

                    return v.x <= extents.x && v.x > -extents.x && v.y <= extents.y && v.y > -extents.y && v.z <= extents.z && v.z > -extents.z;
                }
                return InRange2D(Position, a, Distance);
            }
        }

        public class BaseProfile
        {
            public BuildingOptions Options = new();
            public string ProfileName;
            private Dictionary<string, BaseProfile> Clones = new();
            public RaidableBases Instance;

            public BaseProfile(RaidableBases instance)
            {
                Instance = instance;
                Options.AdditionalBases = new();
            }

            public BaseProfile(RaidableBases instance, BuildingOptions options, string name)
            {
                Instance = instance;
                Options = options;
                ProfileName = name;
            }

            public static BaseProfile Clone(BaseProfile profile, string name)
            {
                if (profile.Clones.TryGetValue(name, out var clone))
                {
                    return clone;
                }
                profile.Clones[name] = clone = new(profile.Instance)
                {
                    Options = profile.Options.Clone(),
                    ProfileName = name,
                };
                return clone;
            }
        }

        public class BuildingTables
        {
            public Dictionary<string, List<LootItem>> DifficultyLootLists = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<DayOfWeek, List<LootItem>> WeekdayLootLists = new();
            public Dictionary<string, BaseProfile> Profiles = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Removed = new();

            public bool IsConfigured(string baseName)
            {
                foreach (var m in Profiles)
                {
                    if (m.Key == baseName || m.Value.Options.AdditionalBases.ContainsKey(baseName))
                    {
                        return true;
                    }
                }
                return false;
            }

            public bool TryGetValue(string baseName, out BaseProfile profile)
            {
                profile = Profiles.FirstOrDefault(m => m.Key == baseName || m.Value.Options.AdditionalBases.ContainsKey(baseName)).Value;
                return profile != null;
            }

            public void Remove(string baseName)
            {
                if (Profiles.Remove(baseName) || Profiles.Values.Exists(m => m.Options.AdditionalBases.Remove(baseName)))
                {
                    Removed.Add(baseName);
                }
            }
        }

        public GridControllerManager GridController = new();

        public class GridControllerManager
        {
            internal RaidableBases Instance;
            internal Dictionary<RaidableType, RaidableSpawns> Spawns = new();
            internal Coroutine gridCoroutine;
            internal Coroutine fileCoroutine;
            internal double gridTime;

            public SpawnsControllerManager SpawnsController => Instance.SpawnsController;
            public StoredData data => Instance.data;
            public Configuration config => Instance.config;
            public double GetRaidTime() => data.RaidTime.Subtract(DateTime.Now).TotalSeconds;

            public void StartAutomation()
            {
                if (Instance.Automated.IsScheduledEnabled)
                {
                    if (data.RaidTime != DateTime.MinValue && GetRaidTime() > config.Settings.Schedule.IntervalMax)
                    {
                        data.RaidTime = DateTime.MinValue;
                    }

                    Instance.Automated.StartCoroutine(RaidableType.Scheduled);
                }

                if (Instance.Automated.IsMaintainedEnabled)
                {
                    Instance.Automated.StartCoroutine(RaidableType.Maintained);
                }
            }

            private IEnumerator LoadFiles()
            {
                Instance.Buildings = new();
                using var sb = DisposableBuilder.Get();
                yield return Instance.LoadProfiles(sb);
                yield return Instance.LoadTables(sb);
                if (Instance.Buildings.Profiles.Count == 0)
                {
                    CriticalError();
                    yield break;
                }
                yield return CoroutineEx.waitForSeconds(5f);
                Instance.IsSpawnerBusy = false;
                StartAutomation();
                if (!Instance.IsCopyPasteLoaded(out var error)) Puts(error);
            }

            public void SetupGrid()
            {
                if (Spawns.Count >= 5)
                {
                    fileCoroutine = ServerMgr.Instance.StartCoroutine(LoadFiles());
                    return;
                }

                StopCoroutine();
                gridCoroutine = ServerMgr.Instance.StartCoroutine(GenerateGrid());
            }

            public void StopCoroutine()
            {
                if (gridCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(gridCoroutine);
                    gridCoroutine = null;
                }
                if (fileCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(fileCoroutine);
                    fileCoroutine = null;
                }
            }

            private void CriticalError(string text = "No valid profiles exist!")
            {
                if (Instance.profileErrors.Count > 0)
                {
                    Puts("Json errors found in:");
                    Instance.profileErrors.ForEach(str => Puts(str));
                }
                Puts("ERROR: Grid has failed initialization. {0} {1}", text, Instance.Buildings.Profiles.Count);
                Interface.Oxide.NextTick(() => gridCoroutine = null);
            }

            public bool BadFrameRate;

            private IEnumerator GenerateGrid()
            {
                yield return CoroutineEx.waitForSeconds(0.1f);

                while (Performance.report.frameRate < 15 && ConVar.FPS.limit > 15)
                {
                    BadFrameRate = true;

                    yield return CoroutineEx.waitForSeconds(1f);
                }

                BadFrameRate = false;

                var gridStopwatch = System.Diagnostics.Stopwatch.StartNew();
                RaidableSpawns spawns = Spawns[RaidableType.Grid] = new(Instance);

                gridTime = Time.realtimeSinceStartupAsDouble;
                Instance.Buildings = new();

                using var sb = DisposableBuilder.Get();
                yield return Instance.LoadProfiles(sb);
                yield return Instance.LoadTables(sb);
                yield return SpawnsController.SetupMonuments();

                if (Instance.Buildings.Profiles.Count == 0)
                {
                    gridStopwatch.Stop();
                    CriticalError();
                    yield break;
                }

                var spawnOnSeabed = false;
                var minPos = (int)(World.Size / -2f) + 100;
                var maxPos = (int)(World.Size / 2f) - 100;
                var maxProtectionRadius = -10000f;
                var minProtectionRadius = 10000f;
                var maxAutoRadius = 0f;
                var maxWaterDepth = 0f;
                var landLevel = 0.5f;
                var checks = 0;

                foreach (var profile in Instance.Buildings.Profiles.Values)
                {
                    maxAutoRadius = Mathf.Min(profile.Options.ProtectionRadius(RaidableType.None), maxAutoRadius);

                    maxProtectionRadius = Mathf.Max(profile.Options.ProtectionRadii.Max(), maxProtectionRadius);

                    minProtectionRadius = Mathf.Min(profile.Options.ProtectionRadii.Min(), minProtectionRadius);

                    maxWaterDepth = Mathf.Max(maxWaterDepth, profile.Options.Water.WaterDepth);

                    landLevel = Mathf.Max(profile.Options.GetLandLevel, landLevel);
                }

                if (!config.Settings.Management.AllowOnBeach && !config.Settings.Management.AllowInland && !spawnOnSeabed)
                {
                    gridStopwatch.Stop();
                    CriticalError("Spawn options for beach, inland and seabed are disabled!");
                    yield break;
                }

                using var blockedPositions = config.Settings.Management.BlockedPositions.ToPooledList();
                using var blockedMapPrefabs = DisposableList<(Vector3, float)>();
                using var custom = DisposableList<(string, int)>();
                var zero = blockedPositions.Find(x => x.position == Vector3.zero);

                if (zero == null)
                {
                    blockedPositions.Add(zero = new(Vector3.zero, 200f));
                }

                if (zero.radius < 200f)
                {
                    zero.radius = 200f;
                }

                var wtObj = Interface.Oxide.CallHook("GetGridWaitTime");
                var waitTime = CoroutineEx.waitForSeconds(wtObj is float w ? w : 0.0035f);
                var threshold = Interface.Oxide.CallHook("GetGridWaitThreshold") is int th ? th : 25;
                var prefabs = config.Settings.Management.BlockedPrefabs.ToDictionary(pair => pair.Key, pair => pair.Value);

                prefabs.Remove("test_prefab");
                prefabs.Remove("test_prefab_2");

                foreach (var prefab in World.Serialization.world.prefabs)
                {
                    if (!StringPool.toString.TryGetValue(prefab.id, out var fullname))
                    {
                        continue;
                    }
                    Vector3 v = new(prefab.position.x, prefab.position.y, prefab.position.z);
                    if (prefabs.Count > 0 && prefabs.TryGetValue(GetFileNameWithoutExtension(fullname), out var dist))
                    {
                        blockedMapPrefabs.Add((v, dist));
                    }
                }

                float railRadius = Mathf.Max(M_RADIUS * 2f, maxAutoRadius);
                bool hasBlockedMapPrefabs = blockedMapPrefabs.Count > 0;
                bool hasBlockedPositions = blockedPositions.Count > 0;
                double totalPoints = Math.Pow((maxPos - minPos) / CELL_SIZE, 2);
                double step = totalPoints / 4.0;
                int stepCounter = 0;
                int progress = 0;

                for (float x = minPos; x < maxPos; x += CELL_SIZE) // Credits to Jake_Rich for helping me with this!
                {
                    for (float z = minPos; z < maxPos; z += CELL_SIZE)
                    {
                        progress++;
                        if (++stepCounter >= step)
                        {
                            Puts($"{Math.Round((progress / totalPoints) * 100.0)}% loaded ({spawns.Spawns.Count} potential points)");
                            stepCounter = 0;
                        }

                        var position = new Vector3(x, 0f, z);

                        if (hasBlockedPositions && blockedPositions.Exists(a => InRange2D(position, a.position, a.radius)))
                        {
                            continue;
                        }

                        position.y = SpawnsController.GetSpawnHeight(position);

                        if (hasBlockedMapPrefabs && SpawnsController.IsBlockedByMapPrefab(blockedMapPrefabs, position))
                        {
                            continue;
                        }

                        SpawnsController.ExtractLocation(spawns, position, landLevel, minProtectionRadius, maxProtectionRadius, railRadius, maxWaterDepth);

                        if (++checks >= threshold)
                        {
                            checks = 0;
                            yield return waitTime;
                        }
                    }
                }

                Instance.IsSpawnerBusy = false;
                Instance.GridController.StartAutomation();
                Instance.Queues.Messages.Clear();
                gridStopwatch.Stop();

                Puts(Instance.mx("Initialized Grid", null, Math.Floor(gridStopwatch.Elapsed.TotalSeconds), gridStopwatch.Elapsed.Milliseconds, World.Size, spawns.Spawns.Count));
                foreach (var (type, amount) in custom) Puts($"Loaded {amount} custom spawns from {type}");
                if (!Instance.IsCopyPasteLoaded(out var error)) Puts(error);
                gridCoroutine = null;
            }

            public void LoadSpawns()
            {
                Spawns = new();
                Spawns.Add(RaidableType.Grid, new(Instance));

                LoadSpawnsForType(RaidableType.Manual, config.Settings.Manual.SpawnsFile, "LoadedManual");
                LoadSpawnsForType(RaidableType.Scheduled, config.Settings.Schedule.SpawnsFile, "LoadedScheduled");
                LoadSpawnsForType(RaidableType.Maintained, config.Settings.Maintained.SpawnsFile, "LoadedMaintained");
            }

            public bool BlockAtSpawnsDatabase(Vector3 a)
            {
                foreach (var (type, rs) in Spawns)
                {
                    if (rs.IsCustomSpawn && rs.Spawns.Count > 0)
                    {
                        foreach (var rsl in rs.Spawns)
                        {
                            if (rsl.Location.Distance(a) <= rsl.Radius)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            private void LoadSpawnsForType(RaidableType type, string spawnsFile, string key)
            {
                if (SpawnsFileValid(spawnsFile))
                {
                    var spawns = GetSpawnsLocations(spawnsFile);

                    if (spawns.Count > 0)
                    {
                        Puts(Instance.mx(key, null, spawns.Count));
                        Spawns[type] = new(Instance, spawns);
                    }
                }
            }

            public bool SpawnsFileValid(string spawnsFile)
            {
                if (string.IsNullOrWhiteSpace(spawnsFile) || spawnsFile.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return Instance.DataFileExists(Path.Combine("SpawnsDatabase", spawnsFile));
            }

            public HashSet<RaidableSpawnLocation> GetSpawnsLocations(string spawnsFile)
            {
                try
                {
                    Spawnfile spawnfile = Interface.Oxide.DataFileSystem.ReadObject<Spawnfile>(Path.Combine("SpawnsDatabase", spawnsFile));

                    HashSet<RaidableSpawnLocation> locations = new();

                    foreach (var value in spawnfile.spawnPoints.Values)
                    {
                        locations.Add(new(value.ToString().ToVector3()));
                    }

                    Puts("Loaded {0} spawn locations from file: {1}", locations.Count, spawnsFile);

                    return locations;
                }
                catch
                {
                    Puts("Invalid spawns file: {0}", spawnsFile);

                    return new();
                }
            }
        }

        private class Spawnfile
        {
            public Dictionary<string, object> spawnPoints = new();
        }

        public class QueueController
        {
            internal YieldInstruction instruction0, instruction1;
            internal Queue<RandomBase> queue = new();
            internal DebugMessages Messages = new();
            internal Coroutine _coroutine;
            internal int spawnChecks;
            internal bool Paused;
            internal RaidableBases Instance;
            internal const float REMOVE_RADIUS = 15f;
            internal Configuration config => Instance.config;
            internal SpawnsControllerManager SpawnsController => Instance.SpawnsController;
            internal bool Any => queue.Count > 0;

            private void Message(BasePlayer player, string key, params object[] args) => Instance.Message(player, key, args);

            private string mx(string key, string id = null, params object[] args) => Instance.mx(key, id, args);

            public class DebugMessages
            {
                internal Dictionary<string, Info> _elements = new();
                internal RaidableBases _instance;
                internal bool _logToFile;
                internal IPlayer _user;

                public class Info
                {
                    public int Amount = 1;
                    public List<string> Values = new();
                    public override string ToString() => Values.Count > 0 ? $": {string.Join(", ", Values)}" : string.Empty;
                }

                public string Add(string element, object obj = null)
                {
                    if (string.IsNullOrWhiteSpace(element))
                    {
                        return null;
                    }
                    if (!_elements.TryGetValue(element, out var info))
                    {
                        if (_elements.Count >= 20)
                        {
                            _elements.Remove(_elements.ElementAt(0).Key);
                        }
                        _elements[element] = info = new();
                    }
                    else info.Amount++;
                    if (obj == null)
                    {
                        return element;
                    }
                    string value = obj.ToString().Replace("(", "").Replace(")", "").Replace(",", "");
                    if (!info.Values.Contains(value))
                    {
                        if (info.Values.Count >= 5)
                        {
                            info.Values.RemoveAt(0);
                        }
                        info.Values.Add(value);
                    }
                    return $"{element}: {value}";
                }
                public void Clear()
                {
                    _elements.Clear();
                }
                public bool Any()
                {
                    return _elements.Count > 0;
                }
                public void PrintAll(IPlayer user = null)
                {
                    if (_elements.Count > 0 && _instance.DebugMode)
                    {
                        foreach (var (key, info) in _elements)
                        {
                            PrintInternal(user, $"{info.Amount}x - {key}{info}");
                        }
                        Clear();
                    }
                }
                private bool PrintInternal(IPlayer user, string message)
                {
                    if (!string.IsNullOrWhiteSpace(message) && _instance.DebugMode)
                    {
                        if (_logToFile)
                        {
                            _instance.LogToFile("debug", message, _instance, true);
                        }
                        if (user == null || user.IsServer)
                        {
                            Puts("DEBUG: {0}", message);
                        }
                        else user.Reply($"DEBUG: {message}");
                        return true;
                    }
                    return false;
                }
                public void Log(string baseName, string message)
                {
                    _instance?.Buildings?.Remove(baseName);
                    _instance.IsSpawnerBusy = false;
                    Print(message);
                    Puts(message);
                }
                public bool Print(string message)
                {
                    Print(_user, message, null);
                    return false;
                }
                public void Print(string message, object obj)
                {
                    Print(_user, message, obj);
                }
                public void Print(IPlayer user, string message, object obj)
                {
                    if (!PrintInternal(user, obj == null ? message : $"{message}: {obj}"))
                    {
                        Add(message, obj);
                    }
                }
                public void PrintLast(string id = null)
                {
                    if (_elements.Count > 0 && _instance.DebugMode)
                    {
                        PrintInternal(_user, GetLast(id));
                    }
                }
                public string GetLast(string id = null)
                {
                    if (_elements.Count == 0)
                    {
                        return _instance.m("CannotFindPosition", id);
                    }
                    var (key, info) = _elements.ElementAt(_elements.Count - 1);
                    _elements.Remove(key);
                    return $"{info.Amount}x - {key}{info}";
                }
            }

            public QueueController(RaidableBases instance)
            {
                Messages._instance = Instance = instance;
                Messages._logToFile = instance.config.LogToFile;
                spawnChecks = Mathf.Clamp(instance.config.Settings.Management.SpawnChecks, 1, 500);
                instruction0 = CoroutineEx.waitForSeconds(0.1f);
                instruction1 = CoroutineEx.waitForSeconds(1f);
            }

            public void RestartCoroutine()
            {
                StopCoroutine();
                _coroutine = ServerMgr.Instance.StartCoroutine(FindEventPosition());
            }

            public void StopCoroutine()
            {
                if (_coroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_coroutine);
                    _coroutine = null;
                }

                queue.Clear();
            }

            public void Add(RandomBase rb)
            {
                if (!queue.Contains(rb))
                {
                    queue.Enqueue(rb);
                }
            }

            private void Spawn(RandomBase rb, Vector3 position)
            {
                if (!Instance.IsUnloading)
                {
                    rb.Position = position;

                    if (Instance.PasteBuilding(rb))
                    {
                        Instance.IsSpawnerBusy = true;
                        rb.IsPasting = true;
                    }
                }
            }

            private bool CanBypassPause(RandomBase rb)
            {
                if (rb.type == RaidableType.Manual)
                {
                    return rb.user != null;
                }
                return false;
            }

            private IEnumerator FindEventPosition()
            {
                int checks = 0;

                while (!Instance.IsUnloading)
                {
                    if (++checks >= spawnChecks)
                    {
                        yield return instruction0;
                        checks = 0;
                    }

                    if (!queue.TryPeek(out var spq))
                    {
                        yield return instruction1;
                        continue;
                    }

                    if (Instance.Buildings.Removed.Contains(spq.BaseName))
                    {
                        if (spq.type == RaidableType.Scheduled)
                        {
                            Instance.data.RaidTime = DateTime.Now.AddSeconds(1f);
                        }
                        queue.Dequeue();
                        continue;
                    }

                    if (spq.Position != Vector3.zero)
                    {
                        queue.Dequeue();
                        Spawn(spq, spq.Position);
                        yield return instruction1;
                        continue;
                    }

                    if (Instance.ZoneManager != null)
                    {
                        SpawnsController.SetupZones(false);
                    }

                    spq.spawns.Check();

                    while (spq.HasSpawns())
                    {
                        if (++checks >= spawnChecks)
                        {
                            checks = 0;
                            yield return instruction0;
                        }

                        if (Instance.IsSpawnerBusy || Paused && !CanBypassPause(spq))
                        {
                            yield return instruction1;
                            continue;
                        }

                        spq.attempts++;

                        var rsl = spq.spawns.GetRandom(spq.options.Water);

                        if (rsl == null)
                        {
                            Messages.Add("RSL is null");
                            break;
                        }

                        var v = rsl.Location;

                        if (!TopologyChecks(spq, rsl.biome, v, rsl.RailRadius))
                        {
                            continue;
                        }

                        v.y = GetAdjustedHeight(spq, v);

                        if (IsTooClose(spq, v))
                        {
                            continue;
                        }

                        if (IsAreaManuallyBlocked(spq, v))
                        {
                            continue;
                        }

                        if (CanSpawnCustom(spq, RaidableType.Maintained, v, config.Settings.Maintained.Ignore, config.Settings.Maintained.SafeRadius))
                        {
                            yield return instruction1;
                            break;
                        }

                        if (CanSpawnCustom(spq, RaidableType.Scheduled, v, config.Settings.Schedule.Ignore, config.Settings.Schedule.SafeRadius))
                        {
                            yield return instruction1;
                            break;
                        }

                        if (IsSubmerged(spq, rsl, v))
                        {
                            continue;
                        }

                        if (!IsAreaSafe(spq, rsl, v))
                        {
                            continue;
                        }

                        if (!spq.pasteData.valid)
                        {
                            yield return SetupCopyPasteRadius(spq);
                        }

                        if (spq.pasteData.foundations.IsNullOrEmpty())
                        {
                            Instance.Buildings.Remove(spq.BaseName);
                            break;
                        }

                        if (Instance.Buildings.Removed.Contains(spq.BaseName))
                        {
                            break;
                        }

                        if (IsObstructed(spq, v) || !spq.spawns.IsCustomSpawn && SpawnsController.IsZoneBlocked(v))
                        {
                            continue;
                        }

                        Spawn(spq, v);
                        yield return instruction1;
                        break;
                    }

                    if (Instance.IsGridLoading() && !Instance.IsSpawnerBusy)
                    {
                        yield return instruction1;
                        continue;
                    }

                    queue.Dequeue();
                    CheckSpawner(spq);
                }

                _coroutine = null;
            }

            private void CheckSpawner(RandomBase spq)
            {
                if (!spq.IsPasting)
                {
                    if (spq.type == RaidableType.Manual)
                    {
                        if (spq.user == null || spq.user.IsServer)
                        {
                            Puts(mx("CannotFindPosition"));
                        }
                        else
                        {
                            Message(spq.user.Player(), Instance.Queues.Messages.GetLast(spq.user.Id));
                        }
                    }

                    spq.spawns.TryAddRange();
                    Messages.PrintAll();
                    Instance.data.Cycle.Add(spq.type, spq.BaseName, spq.owner);
                }
            }

            internal bool IsObstructed(RandomBase spq, Vector3 v)
            {
                if (!spq.spawns.IsCustomSpawn && SpawnsController.IsObstructed(v, spq.pasteData.radius, spq.options.GetLandLevel, spq.options.Setup.ForcedHeight))
                {
                    Messages.Add("Area is obstructed", v);
                    spq.spawns.RemoveNear(v, REMOVE_RADIUS, CacheType.Temporary, spq.type);
                    return true;
                }
                return false;
            }

            private IEnumerator SetupCopyPasteRadius(RandomBase spq)
            {
                yield return Instance.SetupCopyPasteObstructionRadius(spq.BaseName, spq.options.ProtectionRadii.Obstruction == -1 ? 0f : GetObstructionRadius(spq.options.ProtectionRadii, RaidableType.None));
            }

            internal bool IsAreaSafe(RandomBase spq, RaidableSpawnLocation rsl, Vector3 v)
            {
                if (!SpawnsController.IsAreaSafe(rsl.Location, spq.ignoreRadius, spq.safeRadius, spq.buildRadius, spq.pasteData.radius, queueLayers, spq.spawns.IsCustomSpawn, out var cacheType, spq.type))
                {
                    if (cacheType == CacheType.Delete) spq.spawns.Remove(rsl, cacheType);
                    else if (cacheType == CacheType.Privilege) spq.spawns.RemoveNear(rsl.Location, REMOVE_RADIUS, cacheType, spq.type);
                    else spq.spawns.RemoveNear(rsl.Location, REMOVE_RADIUS, cacheType, spq.type);
                    return false;
                }
                return true;
            }

            internal bool IsSubmerged(RandomBase spq, RaidableSpawnLocation rsl, Vector3 v)
            {
                if (!spq.spawns.IsCustomSpawn && spq.options.Setup.ForcedHeight == -1 && SpawnsController.IsSubmerged(spq.options.Water, rsl))
                {
                    Messages.Add("Area is submerged", v);
                    return true;
                }
                return false;
            }

            private bool CanSpawnCustom(RandomBase spq, RaidableType type, Vector3 v, bool ignore, float radius)
            {
                if (spq.type == type && spq.spawns.IsCustomSpawn && (ignore || radius > 0f))
                {
                    if (radius <= 0f)
                    {
                        Messages.Add($"Ignored checks for {spq.type} event", v);
                        Spawn(spq, v);
                        return true;
                    }
                    else spq.ignoreRadius = radius;
                }
                return false;
            }

            private bool IsTooClose(RandomBase spq, Vector3 v)
            {
                if (spq.typeDistance > 0 && Instance.IsTooClose(v, spq.typeDistance))
                {
                    spq.spawns.RemoveNear(v, REMOVE_RADIUS, CacheType.Close, spq.type);
                    Messages.Add("Too close (Spawn Bases X Distance Apart)", v);
                    return true;
                }
                return false;
            }

            internal bool IsAreaManuallyBlocked(RandomBase spq, Vector3 v)
            {
                if (!spq.spawns.IsCustomSpawn && config.Settings.Management.BlockedPositions.Exists(x => InRange2D(v, x.position, x.radius)))
                {
                    spq.spawns.RemoveNear(v, REMOVE_RADIUS, CacheType.Close, spq.type);
                    Messages.Add("Block Spawns At Positions", v);
                    return true;
                }
                return false;
            }

            private float GetAdjustedHeight(RandomBase spq, Vector3 v)
            {
                if (spq.options.Setup.ForcedHeight != -1)
                {
                    return spq.options.Setup.PasteHeightAdjustment + spq.options.Setup.ForcedHeight;
                }
                return v.y + spq.options.Setup.PasteHeightAdjustment;
            }

            private bool TopologyChecks(RandomBase spq, int? t, Vector3 v, float railRadius)
            {
                if (!spq.spawns.IsCustomSpawn && !SpawnsController.TopologyChecks(spq.options.Biomes, t, v, spq.protectionRadius, railRadius, out var topology))
                {
                    spq.spawns.RemoveNear(v, REMOVE_RADIUS, CacheType.Delete, spq.type);
                    Messages.Add($"Blocked on {topology} topology", v);
                    return false;
                }
                return true;
            }
        }

        public class AutomatedController
        {
            internal YieldInstruction instruction0, instruction1, instruction5, instruction15;
            internal Coroutine _maintainedCoroutine, _scheduledCoroutine;
            internal bool IsMaintainedEnabled, IsScheduledEnabled;
            internal RaidableBases Instance;
            internal int _maxOnce;

            internal StoredData data => Instance.data;
            internal Configuration config => Instance.config;

            public AutomatedController(RaidableBases instance, bool a, bool b)
            {
                instruction0 = CoroutineEx.waitForSeconds(0.0025f);
                instruction1 = CoroutineEx.waitForSeconds(1f);
                instruction5 = CoroutineEx.waitForSeconds(5f);
                instruction15 = CoroutineEx.waitForSeconds(15f);
                Instance = instance;
                IsMaintainedEnabled = a;
                IsScheduledEnabled = b;
            }

            public void DestroyMe()
            {
                StopCoroutine(RaidableType.Scheduled);
                StopCoroutine(RaidableType.Maintained);
            }

            public void StopCoroutine(RaidableType type, IPlayer user = null)
            {
                if (type == RaidableType.Scheduled && _scheduledCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_scheduledCoroutine);
                    Instance.Message(user, "ReloadScheduleCo");
                    _scheduledCoroutine = null;
                }
                else if (type == RaidableType.Maintained && _maintainedCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_maintainedCoroutine);
                    Instance.Message(user, "ReloadMaintainCo");
                    _maintainedCoroutine = null;
                }
            }

            public void StartCoroutine(RaidableType type, IPlayer user = null)
            {
                StopCoroutine(type, user);

                if (type == RaidableType.Scheduled ? !IsScheduledEnabled || config.Settings.Schedule.Max <= 0 : !IsMaintainedEnabled || config.Settings.Maintained.Max <= 0)
                {
                    return;
                }

                if (Instance.IsGridLoading() || !Instance.CanContinueAutomation())
                {
                    Instance.timer.Once(1f, () => StartCoroutine(type));
                    return;
                }

                if (type == RaidableType.Scheduled && data.RaidTime == DateTime.MinValue)
                {
                    ScheduleNextAutomatedEvent();
                }

                if (type == RaidableType.Scheduled)
                {
                    Instance.timer.Once(0.2f, () => _scheduledCoroutine = ServerMgr.Instance.StartCoroutine(ScheduleCoroutine()));
                }
                else Instance.timer.Once(0.2f, () => _maintainedCoroutine = ServerMgr.Instance.StartCoroutine(MaintainCoroutine()));
            }

            private IEnumerator MaintainCoroutine()
            {
                float timeBetweenSpawns = Mathf.Max(1f, config.Settings.Maintained.Time);

                while (!Instance.IsUnloading)
                {
                    if (!CanSpawn(RaidableType.Maintained, config.Settings.Maintained.GetPlayerCount(), config.Settings.Maintained.PlayerLimitMin, config.Settings.Maintained.PlayerLimitMax, config.Settings.Maintained.Max, false))
                    {
                        yield return instruction5;
                    }
                    else if (!Instance.Queues.Any)
                    {
                        yield return ProcessEvent(RaidableType.Maintained, timeBetweenSpawns);
                    }
                    else if (Instance.Queues.Messages.Any())
                    {
                        Instance.Queues.Messages.PrintLast();
                    }

                    yield return instruction5;
                }

                _maintainedCoroutine = null;
            }

            private IEnumerator ScheduleCoroutine()
            {
                float timeBetweenSpawns = Mathf.Max(1f, config.Settings.Schedule.Time);

                while (!Instance.IsUnloading)
                {
                    if (CanSpawn(RaidableType.Scheduled, config.Settings.Schedule.GetPlayerCount(), config.Settings.Schedule.PlayerLimitMin, config.Settings.Schedule.PlayerLimitMax, config.Settings.Schedule.Max, true))
                    {
                        while (Instance.Get(RaidableType.Scheduled) < config.Settings.Schedule.Max && MaxOnce())
                        {
                            if (SaveRestore.IsSaving)
                            {
                                Instance.Queues.Messages.Print("Scheduled: Server saving");
                                yield return instruction15;
                            }
                            else if (!Instance.Queues.Any)
                            {
                                yield return ProcessEvent(RaidableType.Scheduled, timeBetweenSpawns);
                            }
                            else if (Instance.Queues.Messages.Any())
                            {
                                Instance.Queues.Messages.PrintLast();
                            }

                            yield return instruction1;
                        }

                        yield return CoroutineEx.waitForSeconds(ScheduleNextAutomatedEvent());
                    }

                    yield return instruction5;
                }

                _scheduledCoroutine = null;
            }

            private IEnumerator ProcessEvent(RaidableType type, float timeBetweenSpawns)
            {
                Instance.SpawnRandomBase(type);
                yield return instruction1;
                //yield return new WaitWhile(() => Instance.Queues.Any);

                if (!Instance.IsSpawnerBusy)
                {
                    yield break;
                }

                if (type == RaidableType.Scheduled)
                {
                    _maxOnce++;
                }

                Instance.Queues.Messages.Print($"{type}: Waiting for base to be setup", Instance.IsBusy(out var pastedLocation) ? pastedLocation : (object)null);
                yield return new WaitWhile(() => Instance.IsSpawnerBusy);

                Instance.Queues.Messages.Print($"{type}: Waiting {timeBetweenSpawns} seconds");
                yield return CoroutineEx.waitForSeconds(timeBetweenSpawns);
            }

            private float ScheduleNextAutomatedEvent()
            {
                var raidInterval = Core.Random.Range(config.Settings.Schedule.IntervalMin, config.Settings.Schedule.IntervalMax + 1);

                _maxOnce = 0;
                data.RaidTime = DateTime.Now.AddSeconds(raidInterval);
                Puts(Instance.mx("Next Automated Raid", null, Instance.FormatTime(raidInterval, null), data.RaidTime));
                Instance.Queues.Messages.Print("Scheduled next automated event");

                return (float)raidInterval;
            }

            private bool MaxOnce()
            {
                return config.Settings.Schedule.MaxOnce <= 0 || _maxOnce < config.Settings.Schedule.MaxOnce;
            }

            private bool CanSpawn(RaidableType type, int onlinePlayers, int playerLimit, int playerLimitMax, int maxEvents, bool checkRaidTime)
            {
                if (onlinePlayers < playerLimit)
                {
                    return Instance.Queues.Messages.Print($"{type}: Insufficient amount of players online {onlinePlayers}/{playerLimit}");
                }
                else if (onlinePlayers > playerLimitMax)
                {
                    return Instance.Queues.Messages.Print($"{type}: Too many players online {onlinePlayers}/{playerLimitMax}");
                }
                else if (Instance.IsSpawnerBusy || Instance.IsLoaderBusy)
                {
                    return Instance.Queues.Messages.Print($"{type}: Waiting for a base to finish its task");
                }
                else if (maxEvents > 0 && Instance.Get(type) >= maxEvents)
                {
                    return Instance.Queues.Messages.Print($"{type}: The max amount of events are spawned");
                }
                else if (checkRaidTime && Instance.GridController.GetRaidTime() > 0)
                {
                    return Instance.Queues.Messages.Print($"{type}: Waiting on timer for next event");
                }
                else if (SaveRestore.IsSaving)
                {
                    return Instance.Queues.Messages.Print($"{type}: Server saving");
                }
                else if (!Instance.IsCopyPasteLoaded(out var error))
                {
                    return Instance.Queues.Messages.Print(error);
                }

                return true;
            }
        }

        public class BMGELEVATOR : FacepunchBehaviour // credits: bmgjet
        {
            internal const string ElevatorPanelName = "RB_UI_Elevator";
            internal Elevator _elevator;
            internal RaycastHit hit;
            internal BaseEntity hitEntity;
            internal RaidableBase raid;
            internal BuildingOptionsElevators options;
            internal Dictionary<ulong, BasePlayer> _UI = new();
            internal bool HasButton;
            internal NetworkableId uid;
            internal int CurrentFloor;
            internal int returnDelay = 60;
            internal float Floors;
            internal const float _LiftSpeedPerMetre = 3f;
            internal RaidableBases env;

            private void Awake()
            {
                _elevator = GetComponent<Elevator>();
                _elevator.LiftSpeedPerMetre = _LiftSpeedPerMetre;
            }

            private void OnDestroy()
            {
                _elevator.SafelyKill();
                _UI.Values.ForEach(DestroyUi);
                try { CancelInvoke(); } catch { }
            }

            private Vector3 GetWorldSpaceFloorPosition(int targetFloor)
            {
                int num = _elevator.Floor - targetFloor;
                Vector3 b = Vector3.up * ((float)num * _elevator.FloorHeight);
                b.y -= 1f;
                return base.transform.position - b;
            }

            public void GoToFloor(Elevator.Direction Direction = Elevator.Direction.Down, bool FullTravel = false, int forcedFloor = -1)
            {
                if (!GetElevatorLift(_elevator, out var elevatorLift))
                {
                    return;
                }
                if (_elevator.HasFlag(BaseEntity.Flags.Busy))
                {
                    return;
                }

                var serverPosition = elevatorLift.transform.position;
                int maxFloors = (int)(Floors / 3f);
                if (forcedFloor != -1)
                {
                    int targetFloor = Mathf.RoundToInt((forcedFloor - serverPosition.y) / 3);
                    if (targetFloor == 0 && CurrentFloor == 0) { targetFloor = maxFloors; }
                    else if (targetFloor == 0 && CurrentFloor == maxFloors) { targetFloor = -maxFloors; }
                    CurrentFloor += targetFloor;

                    if (CurrentFloor > maxFloors) { CurrentFloor = maxFloors; }

                    if (CurrentFloor < 0) { CurrentFloor = 0; }
                }
                else
                {
                    if (Direction == Elevator.Direction.Up)
                    {
                        CurrentFloor++;
                        if (FullTravel) CurrentFloor = (int)(Floors / _elevator.FloorHeight);
                        if ((CurrentFloor * 3) > Floors) CurrentFloor = (int)(Floors / _elevator.FloorHeight);
                    }
                    else
                    {
                        if (GamePhysics.CheckSphere(serverPosition - new Vector3(0, 1f, 0), 0.5f, Layers.Mask.Construction | Layers.Server.Deployed, QueryTriggerInteraction.Ignore))
                        {
                            _elevator.Invoke(Retry, returnDelay);
                            return;
                        }

                        CurrentFloor--;
                        if (CurrentFloor < 0 || FullTravel) CurrentFloor = 0;
                    }
                }
                Vector3 worldSpaceFloorPosition = GetWorldSpaceFloorPosition(CurrentFloor);
                if (!GamePhysics.LineOfSight(serverPosition, worldSpaceFloorPosition, 2097152))
                {
                    if (Direction == Elevator.Direction.Up)
                    {
                        if (!Physics.Raycast(serverPosition, Vector3.up, out hit, 21f) || (hitEntity = hit.GetEntity()).IsNull())
                        {
                            return;
                        }
                        CurrentFloor = (int)(hitEntity.transform.position.Distance(_elevator.transform.position) / 3);
                        worldSpaceFloorPosition = GetWorldSpaceFloorPosition(CurrentFloor);
                    }
                    else
                    {
                        if (!Physics.Raycast(serverPosition - new Vector3(0, 2.9f, 0), Vector3.down, out hit, 21f) || (hitEntity = hit.GetEntity()).IsNull() || hitEntity.ShortPrefabName == "foundation" || hitEntity.ShortPrefabName == "elevator.static")
                        {
                            _elevator.Invoke(Retry, returnDelay);
                            return;
                        }
                        CurrentFloor = (int)(hitEntity.transform.position.Distance(_elevator.transform.position) / 3) + 1;
                        worldSpaceFloorPosition = GetWorldSpaceFloorPosition(CurrentFloor);
                    }
                }
                Vector3 v = transform.InverseTransformPoint(worldSpaceFloorPosition);
                float timeToTravel = _elevator.TimeToTravelDistance(Abs(elevatorLift.transform.localPosition.y - v.y));
                LeanTween.moveLocalY(elevatorLift.gameObject, v.y, timeToTravel);
                _elevator.SetFlagLocal(BaseEntity.Flags.Busy, true);
                elevatorLift.ToggleHurtTrigger(true);
                _elevator.Invoke(_elevator.ClearBusy, timeToTravel);
                _elevator.CancelInvoke(ElevatorToGround);
                _elevator.Invoke(ElevatorToGround, timeToTravel + returnDelay);
                _elevator.SendNetworkUpdate();
            }

            private void Retry()
            {
                GoToFloor(Elevator.Direction.Down, true);
            }

            private void ElevatorToGround()
            {
                if (CurrentFloor != 0)
                {
                    if (_elevator.HasFlag(BaseEntity.Flags.Busy))
                    {
                        _elevator.Invoke(ElevatorToGround, 5f);
                        return;
                    }
                    GoToFloor(Elevator.Direction.Down, true);
                }
            }

            public void Init(RaidableBase raid)
            {
                this.raid = raid;
                env = raid.Instance;
                options = raid.Options.Elevators;
                _elevator._maxHealth = options.ElevatorHealth;
                _elevator.InitializeHealth(options.ElevatorHealth, options.ElevatorHealth);

                if (options.Enabled)
                {
                    InvokeRepeating(ShowHealthUI, 10, 1);
                }

                if (HasButton)
                {
                    env.Subscribe(nameof(OnButtonPress));
                }
            }

            private void ShowHealthUI()
            {
                if (!GetElevatorLift(_elevator, out var elevatorLift))
                {
                    return;
                }
                var serverPosition = elevatorLift.transform.position;
                foreach (var x in raid.raiders.Values)
                {
                    var player = x.player;
                    if (!raid.intruders.Contains(x.userid) || player.IsKilled() || player.IsSleeping() || player.Distance(serverPosition) > 3f)  // || !GamePhysics.LineOfSight(ServerPosition, player.transform.position, 2097152))
                    {
                        if (_UI.Remove(x.userid))
                        {
                            DestroyUi(player);
                        }
                        continue;
                    }
                    var translated = env.mx("Elevator Health", player.UserIDString);
                    var color = UI.ConvertHexToRGBA(options.PanelColor, options.PanelAlpha.Value);
                    var elements = new CuiElementContainer();
                    UI.AddCuiPanel(elements, color, options.AnchorMin, options.AnchorMax, null, null, "Hud", ElevatorPanelName);
                    UI.AddCuiElement(elements, $"{translated} {_elevator._health:#.##}/{_elevator._maxHealth}", 16, TextAnchor.MiddleCenter, "1 1 1 1", "0 0", "1 1", null, null, ElevatorPanelName, "LBL2");
                    CuiHelper.AddUi(player, elements);
                    _UI[x.userid] = player;
                }
            }

            public static void DestroyUi(BasePlayer player) => CuiHelper.DestroyUi(player, ElevatorPanelName);

            private static void CleanElevatorKill(BaseEntity entity)
            {
                if (!entity.IsKilled())
                {
                    entity.transform.position = new(0, -100f, 0);
                    entity.DelayedSafeKill();
                }
            }

            public static PooledList<PooledList<BaseEntity>> SplitElevators(RaidableBase raid, List<BaseEntity> source)
            {
                var groups = DisposableList<PooledList<BaseEntity>>();
                using var positions = DisposableList<Vector2Int>();

                foreach (var entity in source)
                {
                    raid.Entities.Remove(entity);
                    if (entity.IsKilled()) continue;
                    var position = entity.transform.position;
                    var key = new Vector2Int(Mathf.RoundToInt(position.x * 2f), Mathf.RoundToInt(position.z * 2f));
                    int index = positions.IndexOf(key);
                    if (index >= 0)
                    {
                        groups[index].Add(entity);
                    }
                    else
                    {
                        positions.Add(key);
                        var group = DisposableList<BaseEntity>();
                        group.Add(entity);
                        groups.Add(group);
                    }
                }

                return groups;
            }

            public static PooledList<KeyValuePair<NetworkableId, BMGELEVATOR>> FixElevators(RaidableBase raid)
            {
                using var elevators = DisposableList<BaseEntity>();
                bool hasButton = false;
                var bmgs = DisposableList<KeyValuePair<NetworkableId, BMGELEVATOR>>();

                foreach (BaseEntity entity in raid.Entities)
                {
                    switch (entity)
                    {
                        case Elevator or ElevatorLift:
                            elevators.Add(entity);
                            break;
                        case PressButton _:
                            hasButton = true;
                            break;
                    }
                }

                foreach (var elevator in elevators)
                {
                    raid.Entities.Remove(elevator);
                }

                using var splitElevators = SplitElevators(raid, elevators);

                for (int i = splitElevators.Count - 1; i >= 0; i--)
                {
                    using var split = splitElevators[i];
                    var bmg = FixElevator(raid, split);
                    if (bmg != null)
                    {
                        elevators.Add(bmg._elevator);
                        bmg.HasButton = hasButton;
                        bmgs.Add(new(bmg._elevator.net.ID, bmg));
                        raid.SetupEntity(bmg._elevator);
                    }
                    splitElevators.RemoveAt(i);
                }

                return bmgs;
            }

            public static BMGELEVATOR FixElevator(RaidableBase raid, List<BaseEntity> elevators)
            {
                RaidableBases instance = raid.Instance;
                if (elevators.IsNullOrEmpty())
                {
                    return null;
                }
                if (elevators.Count == 1)
                {
                    CleanElevatorKill(elevators[0]);
                    return null;
                }
                Vector3 bottom = new(999f, 999f, 999f);
                Vector3 top = new(-999f, -999f, -999f);
                Quaternion rot = elevators[0].transform.rotation;
                foreach (BaseEntity entity in elevators)
                {
                    var position = entity.transform.position;
                    if (position.y < bottom.y) bottom = position;
                    if (position.y > top.y) top = position;
                    CleanElevatorKill(entity);
                }
                Elevator elevator = GameManager.server.CreateEntity("assets/prefabs/deployable/elevator/static/elevator.static.prefab", bottom, rot, true) as Elevator;
                if (rot != Quaternion.identity) elevator.transform.rotation = rot;
                elevator.transform.position = bottom;
                elevator.transform.localPosition += new Vector3(0f, 0.25f, 0f);
                var bmgELEVATOR = elevator.gameObject.AddComponent<BMGELEVATOR>();
                bmgELEVATOR.env = instance;
                bmgELEVATOR._elevator = elevator;
                bmgELEVATOR._elevator.LiftSpeedPerMetre = BMGELEVATOR._LiftSpeedPerMetre;
                elevator.enableSaving = false;
                elevator.Spawn();
                bmgELEVATOR.Floors = top.y - bottom.y;
                elevator.Invoke(() =>
                {
                    if (elevator.IsDestroyed) return;
                    elevator.baseProtection = instance.GetElevatorProtection();
                    if (GetElevatorLift(elevator, out var lift)) lift.baseProtection = instance.GetElevatorProtection();
                    RemoveImmortality(elevator.baseProtection, 0.9f, 0f, 0f, 0f, 0f, 0.95f, 0f, 0f, 0f, 0.99f, 0.99f, 0.99f, 0f, 1f, 1f, 0.99f, 0.5f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 0f);
                }, 0.0625f);
                using var update = elevator.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                update.Set(BaseEntity.Flags.Reserved1, true);
                update.Set(Elevator.Flag_HasPower, true);
                raid.Elevators[elevator.net.ID] = new() { BMG = bmgELEVATOR, raid = raid };
                if (raid.Elevators.Count == 1)
                {
                    if (raid.Options.Elevators.RequiresBuildingPermission || raid.Options.Elevators.RequiredAccessLevel > 0) instance.Subscribe(nameof(OnElevatorButtonPress));
                    instance.Subscribe(nameof(OnElevatorMove));
                    instance.Subscribe(nameof(OnElevatorCall));
                }
                return bmgELEVATOR;
            }

            internal static bool GetElevatorLift(Elevator elevator, out ElevatorLift lift)
            {
                if (!elevator.IsKilled() && elevator.liftEntity.IsValid(true))
                {
                    lift = elevator.liftEntity.Get(true);
                    return !lift.IsKilled();
                }
                lift = null;
                return false;
            }

            internal static void RemoveImmortality(ProtectionProperties baseProtection, params float[] obj)
            {
                DamageType[] damageTypes = (DamageType[])Enum.GetValues(typeof(DamageType));

                for (int i = 0; i < damageTypes.Length && i < obj.Length; i++)
                {
                    baseProtection.amounts[(int)damageTypes[i]] = obj[i];
                }
            }
        }

        public class RaidableSpawns
        {
            public HashSet<RaidableSpawnLocation> Spawns = new(), Garbage = new();
            public Dictionary<CacheType, HashSet<RaidableSpawnLocation>> Cached = new();
            private double lastTryTime;
            public bool IsCustomSpawn;
            public RaidableBases Instance;
            internal Configuration config => Instance.config;
            public SpawnsControllerManager SpawnsController => Instance.SpawnsController;
            public HashSet<RaidableSpawnLocation> Inactive(CacheType cacheType) => GetCache(cacheType);

            public RaidableSpawns(RaidableBases instance, HashSet<RaidableSpawnLocation> spawns)
            {
                Spawns = spawns;
                Instance = instance;
                IsCustomSpawn = true;
                if (spawns == null) return;
                foreach (var x in spawns)
                {
                    if (x.Location.y > Instance.MaxTerrainY)
                    {
                        Instance.MaxTerrainY = x.Location.y + 1f;
                    }
                }
            }

            public RaidableSpawns(RaidableBases instance)
            {
                Instance = instance;
            }

            public bool CanBuild(Vector3 buildPos, float radius)
            {
                if (IsCustomSpawn && Spawns.Count > 0)
                {
                    foreach (var rsl in Spawns)
                    {
                        if (InRange(rsl.Location, buildPos, radius))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            public bool Add(RaidableSpawnLocation rsl, CacheType cacheType, HashSet<RaidableSpawnLocation> cache, bool forced)
            {
                if (!forced)
                {
                    switch (cacheType)
                    {
                        case CacheType.Close when Instance.IsTooClose(rsl.Location, Instance.GetDistance(RaidableType.None)):
                        case CacheType.Generic when Instance.EventTerritory(rsl.Location):
                        case CacheType.Submerged when !SetOceanLevel(rsl):
                            return false;
                    }
                }

                return Spawns.Add(rsl);
            }

            public bool SetOceanLevel(RaidableSpawnLocation rsl)
            {
                rsl.WaterHeight = WaterSystem.OceanLevel;
                rsl.Surroundings.Clear();
                return true;
            }

            public void Check()
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now > lastTryTime)
                {
                    TryAddRange(CacheType.Temporary, true);
                    TryAddRange(CacheType.Privilege, true);
                    lastTryTime = now + 300d;
                }

                if (Spawns.Count == 0)
                {
                    TryAddRange();
                }
            }

            public void TryAddRange(CacheType cacheType = CacheType.Generic, bool forced = false)
            {
                HashSet<RaidableSpawnLocation> cache = GetCache(cacheType);

                foreach (var rsl in cache)
                {
                    if (Add(rsl, cacheType, cache, forced))
                    {
                        Garbage.Add(rsl);
                    }
                }

                cache.RemoveWhere(Garbage.Contains);

                Garbage.Clear();
            }

            public RaidableSpawnLocation GetRandom(BuildingWaterOptions options)
            {
                RaidableSpawnLocation rsl = Spawns.GetRandom();

                Remove(rsl, CacheType.Generic);

                return rsl;
            }

            public HashSet<RaidableSpawnLocation> GetCache(CacheType cacheType)
            {
                if (!Cached.TryGetValue(cacheType, out var cache))
                {
                    Cached[cacheType] = cache = new();
                }
                return cache;
            }

            public HashSet<RaidableSpawnLocation> GetLocations(CacheType cacheType)
            {
                return Spawns;
            }

            public void AddNear(Vector3 target, float radius, CacheType to, CacheType from, float delayTime)
            {
                if (delayTime > 0)
                {
                    Instance.timer.Once(delayTime, () => AddNear(target, radius, to, from, 0f));
                    return;
                }

                HashSet<RaidableSpawnLocation> cache = GetCache(from);
                HashSet<RaidableSpawnLocation> locations = GetLocations(to);

                foreach (var rsl in cache)
                {
                    if (rsl == null || InRange2D(target, rsl.Location, radius) && Spawns.Add(rsl))
                    {
                        Garbage.Add(rsl);
                    }
                }

                cache.RemoveWhere(Garbage.Contains);
                Garbage.Clear();
            }

            public void Remove(RaidableSpawnLocation a, CacheType cacheType)
            {
                if (a == null) return;
                GetCache(cacheType).Add(a);
            }

            public float RemoveNear(Vector3 target, float radius, CacheType cacheType, RaidableType type) =>
                RemoveNear(target, radius, cacheType, cacheType, type);

            public float RemoveNear(Vector3 target, float radius, CacheType from, CacheType to, RaidableType type)
            {
                if (from == CacheType.Generic)
                {
                    radius = Mathf.Max(Instance.GetDistance(type), radius);
                }

                HashSet<RaidableSpawnLocation> cacheFrom = GetCache(from);
                HashSet<RaidableSpawnLocation> cacheTo = GetCache(to);
                HashSet<RaidableSpawnLocation> locations = GetLocations(from);

                foreach (var rsl in locations)
                {
                    if (rsl == null || InRange2D(target, rsl.Location, radius) && (from == CacheType.Delete || cacheTo.Add(rsl)))
                    {
                        Garbage.Add(rsl);
                    }
                }

                foreach (var rsl in cacheFrom)
                {
                    if (rsl == null || InRange2D(target, rsl.Location, radius))
                    {
                        cacheTo.Add(rsl);
                    }
                }

                locations.RemoveWhere(Garbage.Contains);
                cacheFrom.RemoveWhere(cacheTo.Contains);
                Garbage.Clear();

                return radius;
            }
        }

        public class PlayerInfo
        {
            public string Name;
            public int Raids, TotalRaids;
            public DateTime ExpiredDate = DateTime.MinValue;
            public bool IsExpired(double value)
            {
                if (ExpiredDate == DateTime.MinValue)
                {
                    ResetExpiredDate(value);
                    return false;
                }

                return ExpiredDate < DateTime.Now;
            }

            public void ResetExpiredDate(double value) => ExpiredDate = value > 0 ? DateTime.Now.AddDays(value) : DateTime.MinValue;
            public void ResetWipe()
            {
                Raids = 0;
            }
            public void ResetLifetime()
            {
                TotalRaids = 0;
            }
            internal bool Any => Raids > 0;
        }

        public class RotationCycle
        {
            [JsonProperty(PropertyName = "Buildings")]
            public Dictionary<string, List<string>> _buildings = new();

            [JsonProperty(PropertyName = "Player Buildings")]
            public Dictionary<ulong, Dictionary<string, List<string>>> _playerBuildings = new();

            internal RaidableBases Instance;

            internal Configuration config => Instance.config;

            public void Add(RaidableType type, string key, BasePlayer player, string mode = RaidableMode.Normal)
            {
                if (type == RaidableType.Grid || type == RaidableType.Manual)
                {
                    return;
                }

                var buildings = GetBuildingsDictionary(type, player);

                if (buildings == null)
                {
                    return;
                }

                if (!buildings.TryGetValue(mode, out var keyList))
                {
                    buildings[mode] = keyList = new();
                }

                if (!keyList.Contains(key))
                {
                    keyList.Add(key);
                }
            }

            private Dictionary<string, List<string>> GetBuildingsDictionary(RaidableType type, BasePlayer player)
            {
                return config.Settings.Management.RequireAllSpawned ? _buildings : null;
            }

            public bool CanSpawn(RaidableType type, string mode, string key, BasePlayer player)
            {
                if (mode == RaidableMode.Disabled)
                {
                    return false;
                }

                if (mode == RaidableMode.Random || type == RaidableType.Grid || type == RaidableType.Manual)
                {
                    return true;
                }

                var buildings = GetBuildingsDictionary(type, player);

                if (buildings == null)
                {
                    return !config.Settings.Management.RequireAllSpawned;
                }

                return !buildings.TryGetValue(mode, out var files) || !files.Contains(key) || TryClear(type, files);
            }

            public bool TryClear(RaidableType type, List<string> files)
            {
                foreach (var profile in Instance.Buildings.Profiles)
                {
                    if (Instance.MustExclude(type, profile.Value.Options.AllowPVP))
                    {
                        continue;
                    }

                    if (!files.Contains(profile.Key) && Instance.FileExists(profile.Key))
                    {
                        return false;
                    }

                    if (profile.Value.Options.AdditionalBases.Exists(kvp => !files.Contains(kvp.Key) && Instance.FileExists(kvp.Key)))
                    {
                        return false;
                    }
                }

                files.Clear();
                return true;
            }
        }

        public class PlayerInputEx : FacepunchBehaviour
        {
            private BasePlayer player;
            private Action queuedAction;
            private RaidableBases Instance;
            private RaidableBase raid;
            private Raider raider;
            private Transform t;
            private RaycastHit hit;
            private float deltaTimeTaken;
            private double nextConsumeTime;
            private bool AllowLadders;
            private bool AllowBarricades;
            public bool isDestroyed;
            public Configuration config => Instance.config;
            public bool IsInvalid => t == null || player == null || !player.IsConnected || !player.IsAlive() || player.IsSleeping();

            public void Setup(RaidableBase raid, Raider ri)
            {
                player = GetComponent<BasePlayer>();
                t = player.transform;
                raider = ri;
                raider.Input = this;
                Instance = raid.Instance;
                this.raid = raid;
                AllowBarricades = raid.Options.AllowBarricades;
                AllowLadders = config.Settings.Management.AllowLadders;
            }

            public void Restart()
            {
                deltaTimeTaken = 0f;
            }

            private void Update()
            {
                deltaTimeTaken += Time.unscaledDeltaTime;
                if (deltaTimeTaken < 0.1f)
                {
                    return;
                }

                if (isDestroyed || IsInvalid)
                {
                    deltaTimeTaken = 0f;
                    return;
                }

                if ((t.position - raider.lastPosition).sqrMagnitude > 0.001f)
                {
                    raider.lastPosition = t.position;
                    raider.lastActiveTime = Time.realtimeSinceStartupAsDouble;
                    raider.participantTime += deltaTimeTaken;
                }

                if (AllowLadders)
                {
                    if (queuedAction != null)
                    {
                        queuedAction();
                        queuedAction = null;
                    }

                    TryPlace(ConstructionType.Any);
                }

                deltaTimeTaken = 0f;
            }

            public Quaternion GetRotation(string shortname)
            {
                if (shortname == "ladder.wooden.wall")
                {
                    return Quaternion.LookRotation(hit.normal);
                }
                return Quaternion.LookRotation((t.position - hit.point).WithY(0f).normalized, player.serverInput.AimAngle() * Vector3.up);
            }

            private bool IsFireButton => player.serverInput.IsDown(BUTTON.FIRE_PRIMARY) || player.serverInput.WasDown(BUTTON.FIRE_PRIMARY);

            private bool IsUseButton => player.serverInput.IsDown(BUTTON.USE) || player.serverInput.WasDown(BUTTON.USE);

            public bool TryPlace(ConstructionType constructionType)
            {
                if (isDestroyed || !player.svActiveItemID.IsValid)
                {
                    return false;
                }

                if (!IsFireButton)
                {
                    return false;
                }

                Item item = player.GetActiveItem();
                if (item == null || item.info == null)
                {
                    return false;
                }

                if (!IsConstructionType(item.info.shortname, ref constructionType) || item.info.shortname == "ladder.wooden.wall" && (!AllowLadders || Abs(hit.normal.y) > Mathf.Max(Abs(hit.normal.x), Abs(hit.normal.z))))
                {
                    UseHeal(item, false);
                    return false;
                }

                int amount = item.amount;
                string shortname = item.info.shortname;

                queuedAction = () =>
                {
                    if (raid == null || item == null || item.amount != amount || IsConstructionNear(constructionType, hit.point) || !Instance.ItemDefinitions.TryGetValue(item.info, out var prefab))
                    {
                        return;
                    }

                    if (GameManager.server.CreateEntity(prefab, hit.point, GetRotation(shortname), true) is BaseEntity e && e != null)
                    {
                        e.gameObject.SendMessage("SetDeployedBy", player, SendMessageOptions.DontRequireReceiver);
                        e.OwnerID = 0;
                        e.enableSaving = false;
                        e.Spawn();
                        item.UseItem(1);

                        if (constructionType == ConstructionType.Ladder && hit.GetEntity() is BaseEntity hitEntity && hitEntity != null)
                        {
                            e.SetParent(hitEntity, true, false);
                        }

                        raid.BuiltList.Add(e);
                        raid.AddEntity(e);
                    }
                };

                return true;
            }

            private void UseHeal(Item item, bool consume)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < nextConsumeTime) return;
                nextConsumeTime = now + 1d;
                if (!player.CanInteract() || !player.IsSwimming()) return;
                if (consume && Instance._itemModConsume.TryGetValue(item.info, out var con))
                {
                    player.metabolism.MarkConsumption();
                    con.DoAction(item, player);
                }
                if (!consume && item.GetHeldEntity() is MedicalTool tool && tool != null && !tool.HasAttackCooldown())
                {
                    player.ClientRPC(RpcTarget.Player("Reset", player));
                    player.metabolism.MarkConsumption();
                    nextConsumeTime = now + 3d;
                    tool.ServerUse();
                }
            }

            private bool IsConstructionType(string shortname, ref ConstructionType constructionType)
            {
                hit = default;

                if ((constructionType == ConstructionType.Any || constructionType == ConstructionType.Ladder) && shortname == "ladder.wooden.wall")
                {
                    constructionType = ConstructionType.Ladder;

                    if (raid.Options.RequiresCupboardAccessLadders && !raid.CanBuild(player))
                    {
                        raid.Message(player, "Ladders Require Building Privilege!");
                        return false;
                    }

                    if (config.Settings.Management.AllowLadders && Physics.Raycast(player.eyes.HeadRay(), out hit, 4f, Layers.Mask.Construction, QueryTriggerInteraction.Ignore) && hit.GetEntity() is BaseEntity entity && entity.OwnerID == 0)
                    {
                        foreach (var block in Instance.Blocks)
                        {
                            if (block == entity.ShortPrefabName)
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                if ((constructionType == ConstructionType.Any || constructionType == ConstructionType.Barricade) && shortname.StartsWith("barricade."))
                {
                    constructionType = ConstructionType.Barricade;

                    return AllowBarricades && Physics.Raycast(player.eyes.HeadRay(), out hit, 5f, targetMask, QueryTriggerInteraction.Ignore) && hit.GetEntity().IsNull();
                }

                return false;
            }

            private bool IsConstructionNear(ConstructionType constructionType, Vector3 target)
            {
                float radius = constructionType == ConstructionType.Barricade ? 1f : 0.3f;
                int layerMask = constructionType == ConstructionType.Barricade ? -1 : Layers.Mask.Deployed;
                using var tmp = FindEntitiesOfType<BaseEntity>(target, radius, layerMask);
                if (constructionType == ConstructionType.Barricade) return tmp.Count > 0;
                foreach (var e in tmp) { if (e is BaseLadder) return true; }
                return false;
            }
        }

        public class HumanoidNavigator : NPCPlayerNavigator
        {
            public List<Door> doors;
            public HumanoidBrain brain;
            private HumanoidNPC npc;

            public void SetupNavigator(BaseCombatEntity owner, float distance, float maxWaterDepth)
            {
                npc = brain.npc;
                CanUseNavMesh = !Rust.Ai.AiManager.nav_disable;
                CanUseBaseNav = false;
                MaxRoamDistanceFromHome = BestMovementPointMaxDistance = BestRoamPointMaxDistance = distance * 0.85f;
                DefaultArea = "Walkable";
                topologyPreference = ((TerrainTopology.Enum)TerrainTopology.EVERYTHING);
                Agent.agentTypeID = NavMesh.GetSettingsByIndex(1).agentTypeID; // 0:0, 1: -1372625422, 2: 1479372276, 3: -1923039037
                MaxWaterDepth = maxWaterDepth;
                Init(owner, Agent);
            }

            private bool TryPlaceOnNav()
            {
                if (!CanSamplePosition())
                {
                    return false;
                }

                Vector3 origin = brain.ServerPosition;
                if (!RustNavMeshHelpers.SamplePosition(origin, out var hit, 0.75f, defaultAreaMask) || Abs(hit.position.y - origin.y) > 0.6f || !InRange2D(origin, hit.position, 0.75f))
                {
                    return false;
                }

                if (!HasClearNavigationPath(origin, hit.position))
                {
                    return false;
                }

                Agent.enabled = true;

                if (!Agent.Warp(hit.position))
                {
                    Agent.enabled = false;
                    return false;
                }

                npc.ServerPosition = hit.position;
                StuckOffNavmesh = false;
                return true;
            }

            private static bool HasClearNavigationPath(Vector3 origin, Vector3 newPositionWS)
            {
                return !Physics.Linecast(origin + Vector3.up * 0.9f, newPositionWS + Vector3.up * 0.9f, visibleMask, QueryTriggerInteraction.Ignore);
            }

            private static bool HasSupportBelow(Vector3 origin)
            {
                return Physics.Raycast(origin + Vector3.up * 0.5f, Vector3.down, 1.5f, Layers.Mask.Construction | Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
            }

            public bool EnsureNavReady()
            {
                if (brain.isKilled || !CanUseNavMesh || npc.IsSleeping() || Rust.Ai.AiManager.nav_disable || !CanSamplePosition())
                {
                    return false;
                }

                return Agent.enabled && Agent.isOnNavMesh && !StuckOffNavmesh || TryPlaceOnNav();
            }

            public bool TryMoveNear(Vector3 origin, Vector3 target, float radius)
            {
                if (!EnsureNavReady())
                {
                    return false;
                }

                if (!ConVar.AI.useUnityNavmesh)
                {
                    using var points = DisposableList<NavVector3>();
                    if (!Agent.SampleConnectedPositions(Agent.WorldToNavSpace(origin), radius, 0f, 8, points))
                    {
                        return false;
                    }

                    while (points.Count > 0)
                    {
                        int best = 0;
                        float distance = float.MaxValue;
                        for (int i = 0; i < points.Count; i++)
                        {
                            float d = (Agent.NavToWorldSpace(points[i]) - target).sqrMagnitude;
                            if (d < distance)
                            {
                                distance = d;
                                best = i;
                            }
                        }

                        Vector3 point = Agent.NavToWorldSpace(points[best]);
                        points.RemoveAt(best);
                        if (!ReachedPosition(point) && brain.SetDestination(point))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool preserved = HasSupportBelow(origin);
                for (int i = 0; i < 8; i++)
                {
                    Vector2 offset = UnityEngine.Random.insideUnitCircle * radius;
                    Vector3 point = i == 0 ? target : new Vector3(origin.x + offset.x, origin.y, origin.z + offset.y);
                    if (!preserved) point.y = TerrainMeta.HeightMap.GetHeight(point);
                    if (!ReachedPosition(point) && brain.SetDestination(point))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool TrySampleDestination(Vector3 positionWS, out Vector3 destination)
            {
                if (!Agent.SamplePosition(positionWS, out var hit, 0.75f, false) || Abs(hit.position.y - positionWS.y) > 0.6f || !InRange2D(positionWS, hit.position, 0.75f))
                {
                    destination = default;
                    return false;
                }

                destination = hit.position;
                return true;
            }

            public bool TrySetDestination(Vector3 destination, NavigationSpeed speed)
            {
                if (!base.SetDestination(destination, speed))
                {
                    return false;
                }

                npc.finalDestination = destination;
                return true;
            }

            public override void UpdatePositionAndRotation(Vector3 moveToPosition, float delta)
            {
                if (brain == null || brain.isKilled)
                {
                    return;
                }

                if (CurrentNavigationType == NavigationType.NavMesh && NPCPlayerEntity != null && doors != null && doors.Count > 0)
                {
                    Vector3 from = base.transform.position;
                    Vector3 movement = moveToPosition - from;
                    float distance = movement.magnitude;

                    if (distance > 0.001f)
                    {
                        const float radius = 0.3f;
                        float halfHeight = Mathf.Max(0f, NPCPlayerEntity.GetHeight() - 0.35f) * 0.5f;
                        Vector3 clearance = new(radius, halfHeight, radius);
                        Vector3 bodyFrom = from + Vector3.up * (halfHeight + 0.35f);
                        Vector3 bodyTo = bodyFrom + movement;
                        float checkRange = distance + clearance.magnitude;

                        for (int i = doors.Count - 1; i >= 0; i--)
                        {
                            Door door = doors[i];
                            if (door.IsKilled())
                            {
                                doors.RemoveAt(i);
                                continue;
                            }

                            if (door.IsOpen())
                            {
                                continue;
                            }

                            Transform t = door.transform;
                            Bounds bounds = door.bounds;
                            float range = Vector3.Scale(bounds.extents, t.lossyScale).magnitude + checkRange;
                            if ((t.TransformPoint(bounds.center) - bodyFrom).sqrMagnitude > range * range)
                            {
                                continue;
                            }

                            if (!IsDoorBlockingStep(t, bounds, bodyFrom, bodyTo, clearance))
                            {
                                continue;
                            }

                            bool aligned = Agent.Warp(from);
                            Stop();
                            if (!aligned) StuckOffNavmesh = true;
                            ApplyFacingDirectionOverride();
                            brain.Forget(door);
                            return;
                        }
                    }
                }

                base.UpdatePositionAndRotation(moveToPosition, delta);
            }

            private static bool IsDoorBlockingStep(Transform doorTransform, Bounds bounds, Vector3 from, Vector3 to, Vector3 clearance)
            {
                Matrix4x4 inverse = doorTransform.worldToLocalMatrix;
                Vector3 start = inverse.MultiplyPoint3x4(from), movement = inverse.MultiplyVector(to - from);

                bounds.Expand(new Vector3(
                    x: Abs(inverse.m00) * clearance.x + Abs(inverse.m01) * clearance.y + Abs(inverse.m02) * clearance.z,
                    y: Abs(inverse.m10) * clearance.x + Abs(inverse.m11) * clearance.y + Abs(inverse.m12) * clearance.z,
                    z: Abs(inverse.m20) * clearance.x + Abs(inverse.m21) * clearance.y + Abs(inverse.m22) * clearance.z) * 2f);

                if (bounds.Contains(start))
                {
                    Vector3 offset = start - bounds.center, depth = bounds.extents - new Vector3(Abs(offset.x), Abs(offset.y), Abs(offset.z));
                    int axis = depth.x <= depth.y && depth.x <= depth.z ? 0 : depth.y <= depth.z ? 1 : 2;
                    return offset[axis] * movement[axis] < -0.000001f;
                }

                if (Vector3.Dot(bounds.ClosestPoint(start) - start, movement) <= 0f) return false;

                float distance = movement.magnitude;
                return bounds.IntersectRay(new(start, movement / distance), out float hitDistance) && hitDistance <= distance;
            }
        }

        public class HumanoidNPC : ScientistNPC
        {
            internal Dictionary<ulong, HumanoidBrain> HumanoidBrains;

            public new HumanoidBrain Brain;

            public RaidableBases Instance;

            public string DisplayNameOverride;

            public RaidableBase raid;

            public new Translate.Phrase LootPanelTitle => DisplayNameOverride;

            public override string Categorize() => "Humanoid";

            public override bool ShouldDropActiveItem() => false;

            public override string displayName => DisplayNameOverride;

            public override void AttackerInfo(ProtoBuf.PlayerLifeStory.DeathInfo info)
            {
                info.attackerName = DisplayNameOverride;
                info.attackerSteamID = userID;
                info.inflictorName = inventory?.containerBelt?.GetSlot(0)?.info?.shortname;
                if (Brain != null) info.attackerDistance = Vector3.Distance(Brain.ServerPosition, Brain.AttackPosition);
            }

            public override void OnDied(HitInfo info)
            {
                Brain?.DisableShouldThink();
                base.OnDied(info);
            }

            private void TryRespawnNpc()
            {
                if (raid == null || raid.IsDespawning)
                {
                    return;
                }

                if (raid.npcs != null)
                {
                    raid.npcs.RemoveAll(npc => npc.IsKilled() || npc.userID == userID);
                }

                if (raid.Options.RespawnRateMax > 0f && Brain != null)
                {
                    raid.TryRespawnNpc(Brain.isMurderer);
                }
            }

            public override BaseCorpse CreateCorpse(PlayerFlags flagsOnDeath, Vector3 posOnDeath, Quaternion rotOnDeath, List<TriggerBase> triggersOnDeath, bool forceServerSide = false)
            {
                TryRespawnNpc();
                BasePlayer.bots.Remove(this);
                HumanoidBrains.Remove(userID);

                if (inventory == null || Brain == null || !Brain.HasCorpseLoot())
                {
                    if (Interface.Oxide.CallHook("OnRaidableNpcStrip", GetEntity(), 2) == null)
                    {
                        inventory.SafelyStrip();
                    }
                    return null;
                }

                if (Brain.keepInventory)
                {
                    inventory.containerWear.SafelyRemove("gloweyes");
                }
                else if (Interface.Oxide.CallHook("OnRaidableNpcStrip", GetEntity(), 1) == null)
                {
                    inventory.SafelyStrip();
                }

                if (raid.Options.DespawnGreyNpcBags)
                {
                    Brain.Instance.NpcCorpse.Add(userID);
                }

                List<LootItem> drops = Brain.isMurderer ? Brain.Settings.MurdererDrops : Brain.Settings.ScientistDrops;
                if (!RemoveOwnershipPass() && drops.Count == 0 && LootSpawnSlots.Length == 0)
                {
                    return null;
                }

                PlayerCorpse corpse = DropCorpse("assets/prefabs/player/player_corpse.prefab") as PlayerCorpse;
                if (corpse == null)
                {
                    return null;
                }

                corpse.transform.position = corpse.transform.position + Vector3.down * NavAgent.baseOffset;
                corpse.TakeFrom(this, inventory.containerMain, inventory.containerWear, inventory.containerBelt);
                corpse.playerName = displayName;
                corpse.playerSteamID = userID;
                corpse.skinID = RB_SKIN_ID;
                corpse.Spawn();
                if (corpse.IsKilled())
                {
                    return null;
                }

                corpse.TakeChildren(this);
                bool canPopulateLoot = !Brain.Settings.AlternateScientistLoot.CallHook || Interface.CallHook("OnCorpsePopulate", this, corpse) == null;
                if (canPopulateLoot && LootSpawnSlots.Length != 0)
                {
                    foreach (var lootSpawnSlot in LootSpawnSlots)
                    {
                        for (int k = 0; k < lootSpawnSlot.numberToSpawn; k++)
                        {
                            if (UnityEngine.Random.Range(0f, 1f) <= lootSpawnSlot.probability)
                            {
                                lootSpawnSlot.definition.SpawnIntoContainer(corpse.containers[0]);
                            }
                        }
                    }
                }

                raid.SpawnDrops(corpse.containers, drops);
                CheckCorpse(corpse);
                return corpse;
            }

            private void CheckCorpse(PlayerCorpse corpse)
            {
                raid.npcs.RemoveAll(npc => npc.IsKilled() || npc.userID == userID);

                if (!Brain.keepInventory)
                {
                    corpse.Invoke(corpse.SafelyKill, 30f);
                }

                if (!Instance.AnyNpcs())
                {
                    Instance.Unsubscribe(nameof(OnNpcDestinationSet));
                }

                if (raid.Options.DespawnGreyNpcBags)
                {
                    raid.SetupEntity(corpse);
                }

                corpse.playerName = displayName;
                Brain.DisableShouldThink();
                UnityEngine.Object.Destroy(Brain);
            }

            private bool RemoveOwnershipPass()
            {
                if (!Instance.config.BlockPaidContent) return true;

                using var itemList = Facepunch.Pool.Get<PooledList<Item>>();
                inventory.GetAllItems(itemList);
                for (int i = itemList.Count - 1; i >= 0; i--)
                {
                    Item item = itemList[i];
                    if (Brain.Instance.RequiresOwnership(item.info, item.skin))
                    {
                        item.GetHeldEntity().SafelyKill();
                        item.RemoveFromContainer();
                        item.Remove(0f);
                    }
                }

                return itemList.Count > 0;
            }
        }

        public class HumanoidBrain : ScientistBrain
        {
            public void DisableShouldThink()
            {
                if (isKilled)
                {
                    return;
                }
                isKilled = true;
                if (!Rust.Application.isQuitting)
                {
                    BaseEntity.Query.Server.RemoveBrain(GetBaseEntity());
                    LeaveGroup();
                }
                if (thinker != null)
                {
                    AIThinkManager._processQueue.Remove(thinker);
                }
                lastWarpTime = float.MaxValue;
                sleeping = true;
                SetEnabled(false);
            }

            internal enum AttackType { BaseProjectile, Explosive, FlameThrower, Melee, Water, None }
            internal string displayName, AttackName = string.Empty;
            internal Transform NpcTransform;
            internal IThinker thinker;
            internal ulong userid;
            internal HumanoidNPC npc;
            internal HumanoidNavigator navigator;
            internal AttackEntity _attackEntity;
            internal FlameThrower flameThrower;
            internal BaseProjectile launcher;
            internal LiquidWeapon liquidWeapon;
            internal BaseMelee baseMelee;
            internal BaseProjectile baseProjectile;
            internal BasePlayer AttackTarget;
            internal BasePlayer ignoredDoorTarget;
            internal Door blockingDoor;
            internal Transform AttackTransform;
            internal RaidableBases Instance;
            internal RaidableBase raid;
            internal NpcSettings Settings;
            internal List<Vector3> RandomRoamPositions;
            internal List<Vector3> RandomNearPositions;
            internal Vector3 DestinationOverride;
            internal bool keepInventory, isKilled, isMurderer, movementStarted;
            internal float ScientistChaseRange, attackRange, attackCooldown, equipWeaponTime, equipToolTime;
            internal double lastWarpTime, lastAttackTime, nextAttackTime;
            internal AttackType attackType = AttackType.None;
            internal BaseNavigator.NavigationSpeed CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;
            internal Configuration config => Instance.config;
            internal Vector3 AttackPosition => AttackTransform == null ? default : AttackTransform.position;
            internal Vector3 ServerPosition => NpcTransform == null ? default : NpcTransform.position;

            public double SecondsSinceLastAttack => Time.realtimeSinceStartupAsDouble - lastAttackTime;
            internal List<AttackEntity> AttackWeapons = new();
            internal List<Item> MedicalTools = new();
            internal Dictionary<ulong, HumanoidBrain> HumanoidBrains;

            internal AttackEntity AttackEntity
            {
                get
                {
                    if (_attackEntity.IsNull())
                    {
                        IdentifyWeapon();
                    }

                    return _attackEntity;
                }
            }

            private void Update()
            {
                if (isKilled)
                {
                    return;
                }
                float delta = Time.unscaledDeltaTime;
                equipToolTime += delta;
                if (equipToolTime >= 5f)
                {
                    equipToolTime = float.MinValue;
                    EquipMedicalTool();
                }
                equipWeaponTime += delta;
                if (equipWeaponTime >= 5f)
                {
                    equipWeaponTime = float.MinValue;
                    EquipWeapon();
                    equipWeaponTime = 0f;
                }
            }

            private void EquipWeapon()
            {
                if (RemoveLostWeapons())
                {
                    IdentifyWeapon();
                }
                if (AttackWeapons.Count == 0 || npc.IsWounded() || AttackWeapons.Count == 1 && npc.GetHeldEntity() == AttackWeapons[0])
                {
                    return;
                }
                Shuffle(AttackWeapons);
                foreach (var weapon in AttackWeapons)
                {
                    if (AttackTransform != null)
                    {
                        if (weapon is BaseMelee && !IsInAttackRange(5f))
                        {
                            continue;
                        }
                    }
                    if (UpdateWeapon(weapon, weapon.ownerItemUID))
                    {
                        break;
                    }
                }
                IdentifyWeapon();
            }

            private bool RemoveLostWeapons()
            {
                bool removed = false;
                for (int i = AttackWeapons.Count - 1; i >= 0; i--)
                {
                    AttackEntity weapon = AttackWeapons[i];
                    if (weapon.IsKilled() || weapon.GetParentEntity() != npc)
                    {
                        AttackWeapons.RemoveAt(i);
                        removed = true;
                    }
                }

                return removed;
            }

            public bool HasCorpseLoot()
            {
                if (isMurderer ? Settings.MurdererDrops.Count > 0 : Settings.ScientistDrops.Count > 0) return true;
                return keepInventory || npc != null && npc.LootSpawnSlots.Length != 0;
            }

            public void EnableMedicalTools()
            {
                equipToolTime = MedicalTools.Count == 0 ? float.MinValue : 4f;
            }

            private void EquipMedicalTool()
            {
                if (npc.IsWounded() || npc.health > npc.startHealth * HealBelowHealthFraction)
                {
                    equipToolTime = 0f;
                    return;
                }
                if (AttackTransform != null)
                {
                    if (!isMurderer && Senses.Memory.IsLOS(AttackTarget))
                    {
                        equipToolTime = 0f;
                        return;
                    }
                    if (isMurderer && IsInReachableRange())
                    {
                        equipToolTime = 0f;
                        return;
                    }
                }
                for (int i = MedicalTools.Count - 1; i >= 0; i--)
                {
                    if (!TryGetMedicalTool(MedicalTools[i], out _))
                    {
                        MedicalTools.RemoveAt(i);
                    }
                }
                if (MedicalTools.Count == 0)
                {
                    return;
                }
                Item tool = MedicalTools[0];
                equipWeaponTime = 0f;
                StartCoroutine(Heal(tool));
            }

            private bool TryGetMedicalTool(Item item, out MedicalTool tool)
            {
                ItemContainer belt = npc.inventory?.containerBelt;
                if (belt == null || item == null || !item.IsValid() || item.parent != belt)
                {
                    tool = null;
                    return false;
                }
                tool = item.GetHeldEntity() as MedicalTool;
                return tool != null && tool.GetParentEntity() == npc;
            }

            private IEnumerator Heal(Item item)
            {
                npc.UpdateActiveItem(item.uid);
                yield return CoroutineEx.waitForSeconds(1f);
                if (isKilled)
                {
                    yield break;
                }
                if (!TryGetMedicalTool(item, out MedicalTool tool) || npc.GetActiveItem() != item)
                {
                    equipToolTime = 0f;
                    yield break;
                }
                tool.ServerUse();
                if (!isKilled)
                {
                    npc.Heal(npc.MaxHealth());
                    equipToolTime = 0f;
                }
            }

            public bool UpdateWeapon(AttackEntity attackEntity, ItemId uid)
            {
                ItemContainer belt = npc?.inventory?.containerBelt;

                if (belt == null || attackEntity == null)
                {
                    return false;
                }

                Item item = belt.FindItemByUID(uid);

                if (item == null || item.parent != belt || item.GetHeldEntity() != attackEntity)
                {
                    return false;
                }

                npc.UpdateActiveItem(uid);

                if (attackEntity.IsKilled() || item.parent != belt || npc.GetActiveItem() != item)
                {
                    return false;
                }

                if (attackEntity is Chainsaw cs)
                {
                    cs.ServerNPCStart();
                }

                npc.damageScale = 1f;

                attackEntity.TopUpAmmo();
                attackEntity.SetHeld(true);
                return true;
            }

            internal void IdentifyWeapon()
            {
                _attackEntity = GetEntity().GetAttackEntity();

                attackRange = 0f;
                attackCooldown = 99999f;
                attackType = AttackType.None;
                baseMelee = null;
                flameThrower = null;
                launcher = null;
                liquidWeapon = null;
                AttackName = string.Empty;

                if (_attackEntity.IsNull())
                {
                    return;
                }

                ((AttackName = _attackEntity.ShortPrefabName) switch
                {
                    "double_shotgun.entity" or "shotgun_pump.entity" or "shotgun_waterpipe.entity" or "spas12.entity" or "nailgun.entity" or "t1_smg.entity" or "snowballgun.entity" or "blunderbuss.entity" or "pistol_eoka.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 30f, 0f, 30f);
                    }),
                    "pistol_revolver.entity" or "pistol_semiauto.entity" or "smg.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 50f, 0f, 50f);
                    }),
                    "m4_shotgun.entity" or "glock.entity" or "python.entity" or "thompson.entity" or "m92.entity" or "mp5.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 100f, 0f, 75f);
                    }),
                    "ak47u.entity" or "ak47u_ice.entity" or "ak47u_diver.entity" or "ak47u_med.entity" or "m249.entity" or "minigun.entity" or "sks.entity" or "m39.entity" or "semi_auto_rifle.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 300f, 0f, 190f);
                    }),
                    "hc_revolver.entity" or "lr300.entity" or "hmlmg.entity" or "l96.entity" or "bolt_rifle.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 400f, 0f, 380f);
                    }),
                    "chainsaw.entity" or "jackhammer.entity" => (Action)(() =>
                    {
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                    }),
                    "axe_salvaged.entity" or "bone_club.entity" or "butcherknife.entity" or "candy_cane.entity" or "hammer_salvaged.entity" or "hatchet.entity" or "icepick_salvaged.entity" or "knife.combat.entity" or "knife_bone.entity" or "longsword.entity" or "mace.baseballbat" or "mace.entity" or "machete.weapon" or "pickaxe.entity" or "pitchfork.entity" or "salvaged_cleaver.entity" or "salvaged_sword.entity" or "sickle.entity" or "spear_stone.entity" or "spear_wooden.entity" or "cny_spear.entity" or "stone_pickaxe.entity" or "stonehatchet.entity" or "vampirestake.entity" or "skinningknife.entity" or "pitchfork.entity" => (Action)(() =>
                    {
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 1.5f);
                    }),
                    "explosive.satchel.entity" or "explosive.timed.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.Explosive, 17.5f, 10f);
                    }),
                    "grenade.beancan.entity" or "grenade.f1.entity" or "grenade.molotov.entity" or "grenade.flashbang.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.Explosive, 17.5f, 5f);
                    }),
                    "mgl.entity" => (Action)(() =>
                    {
                        launcher = _attackEntity as BaseProjectile;
                        SetAttackRestrictions(AttackType.Explosive, 100f, 2f, 50f);
                    }),
                    "rocket_launcher.entity" => (Action)(() =>
                    {
                        launcher = _attackEntity as BaseProjectile;
                        SetAttackRestrictions(AttackType.Explosive, 300f, 6f, 150f);
                    }),
                    "flamethrower.entity" or "militaryflamethrower.entity" => (Action)(() =>
                    {
                        flameThrower = _attackEntity as FlameThrower;
                        SetAttackRestrictions(AttackType.FlameThrower, 10f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                    }),
                    "compound_bow.entity" or "crossbow.entity" or "bow_hunting.entity" or "legacybow.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 200f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 1.25f, 150f);
                    }),
                    "mini_crossbow.entity" or "speargun.entity" or "blowpipe.entity" or "boomerang.entity" => (Action)(() =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 50f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 1.25f, 20f);
                    }),
                    "watergun.entity" or "waterpistol.entity" => (Action)(() =>
                    {
                        if ((liquidWeapon = _attackEntity as LiquidWeapon) != null)
                        {
                            liquidWeapon.AutoPump = true;
                            SetAttackRestrictions(AttackType.Water, 10f, 2f);
                        }
                    }),
                    _ => (Action)(() => _attackEntity = null)
                })();
            }

            private void SetAttackRestrictions(AttackType attackType, float attackRange, float attackCooldown, float effectiveRange = 0f)
            {
                if (attackType == AttackType.BaseProjectile && _attackEntity is BaseProjectile projectile)
                {
                    baseProjectile = projectile;

                    if (!baseProjectile.MuzzlePoint)
                    {
                        baseProjectile.MuzzlePoint = baseProjectile.transform;
                    }
                }

                if (effectiveRange != 0f)
                {
                    _attackEntity.effectiveRange = effectiveRange;
                }

                (this.attackType, this.attackRange, this.attackCooldown) = (attackType, attackRange, attackCooldown);
            }

            public bool ValidTarget => AttackTransform != null && !AttackTarget.IsKilled() && !ShouldForgetTarget(AttackTarget);

            public override void OnDestroy()
            {
                HumanoidBrains.Remove(userid);
                if (!Rust.Application.isQuitting && !isKilled)
                {
                    BaseEntity.Query.Server.RemoveBrain(GetEntity());
                    LeaveGroup();
                    if (IsInvoking(TickMovement))
                    {
                        CancelInvoke(TickMovement);
                    }
                }
                Count--;
            }

            public override void InitializeAI()
            {
                base.InitializeAI();
                base.ForceSetAge(0f);

                Pet = false;
                sleeping = false;
                UseAIDesign = true;
                AllowedToSleep = false;
                HostileTargetsOnly = false;
                AttackRangeMultiplier = 2f;
                MaxGroupSize = 0;

                Senses.Init(
                    owner: GetEntity(),
                    brain: this,
                    memoryDuration: 5f,
                    range: 50f,
                    targetLostRange: 75f,
                    visionCone: -1f,
                    checkVision: false,
                    checkLOS: true,
                    ignoreNonVisionSneakers: true,
                    listenRange: 15f,
                    hostileTargetsOnly: false,
                    senseFriendlies: false,
                    ignoreSafeZonePlayers: false,
                    senseTypes: config.Settings.Management.TargetNpcs ? EntityType.Player | EntityType.BasePlayerNPC : EntityType.Player,
                    refreshKnownLOS: true
                );

                CanUseHealingItems = true;
            }

            public void SetSleeping(bool state)
            {
                SetEnabled(!state);
                sleeping = state;
                AllowedToSleep = state;
                npc.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, state);
            }

            public override void AddStates()
            {
                base.AddStates();

                states[AIState.Attack] = new AttackState(this);
            }

            public class AttackState : BaseAttackState
            {
                private new HumanoidBrain brain;
                private global::HumanNPC npc;
                private Transform NpcTransform;

                private new IAIAttack attack => brain.Senses.ownerAttack;

                public AttackState(HumanoidBrain humanoidBrain)
                {
                    base.brain = brain = humanoidBrain;
                    base.AgrresiveState = true;
                    npc = brain.GetBrainBaseEntity() as global::HumanNPC;
                    NpcTransform = npc.transform;
                }

                public override void StateEnter(BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (_brain != null && NpcTransform != null && brain.ValidTarget && InAttackRange())
                    {
                        StartAttacking();
                    }
                }

                public override void StateLeave(BaseAIBrain _brain, BaseEntity _entity)
                {

                }

                private new void StopAttacking()
                {
                    if (attack != null)
                    {
                        attack.StopAttacking();
                        brain.TryReturnHome();
                        brain.AttackTarget = null;
                        brain.AttackTransform = null;
                        brain.Navigator.ClearFacingDirectionOverride();
                    }
                }

                public override StateStatus StateThink(float delta, BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (_brain == null || NpcTransform == null || attack == null)
                    {
                        return StateStatus.Error;
                    }
                    if (brain.isKilled || !brain.ValidTarget)
                    {
                        StopAttacking();

                        return StateStatus.Finished;
                    }
                    if (brain.Senses.ignoreSafeZonePlayers && brain.AttackTarget.InSafeZone())
                    {
                        return StateStatus.Error;
                    }
                    if (brain.CanShoot())
                    {
                        if (InAttackRange())
                        {
                            StartAttacking();
                        }

                        return StateStatus.Running;
                    }
                    else
                    {
                        StopAttacking();

                        return StateStatus.Finished;
                    }
                }

                private bool InAttackRange()
                {
                    if (brain.AttackTransform == null)
                    {
                        return false;
                    }
                    float range = brain.attackRange;
                    if (brain.raid.IsMounted(brain.AttackTarget))
                    {
                        range += 3f;
                    }
                    return brain.IsInAttackRange(range) && brain.CanSeeTarget(brain.AttackTarget);
                }

                private void StartAttacking()
                {
                    if (brain.AttackTarget == null || brain.AttackTransform == null)
                    {
                        return;
                    }

                    brain.SetAimDirection();

                    if (!brain.CanShoot() || brain.IsAttackOnCooldown() || brain.TryThrowWeapon())
                    {
                        return;
                    }
                    if (brain.attackType == AttackType.Explosive && !brain.launcher.IsNull())
                    {
                        brain.EmulatedFire();
                    }
                    else if (brain.attackType == AttackType.BaseProjectile)
                    {
                        RealisticShotTest();
                    }
                    else if (brain.attackType == AttackType.FlameThrower)
                    {
                        brain.UseFlameThrower();
                    }
                    else if (brain.attackType == AttackType.Water)
                    {
                        brain.UseWaterGun();
                    }
                    else brain.MeleeAttack();
                    brain.lastAttackTime = Time.realtimeSinceStartupAsDouble;
                }

                private void RealisticShotTest()
                {
                    if (brain.AttackTarget.IsNpc)
                    {
                        var faction = brain.AttackTarget.faction;
                        brain.AttackTarget.faction = BaseCombatEntity.Faction.Horror;
                        npc.ShotTest(brain.AttackPosition.Distance(brain.ServerPosition));
                        if (brain.AttackTarget != null) brain.AttackTarget.faction = faction;
                    }
                    else npc.ShotTest(brain.AttackPosition.Distance(brain.ServerPosition));
                }
            }

            private bool init;

            public void Init()
            {
                if (init) return;
                init = true;
                lastWarpTime = Time.realtimeSinceStartupAsDouble;
                npc.spawnPos = raid.Location;
                npc.AdditionalLosBlockingLayer = (int)Layer.Deployed;
                navigator.SetupNavigator(GetEntity(), raid.ProtectionRadius, config.Settings.Management.WaterDepth);
            }

            private void Converge()
            {
                foreach (var brain in HumanoidBrains.Values)
                {
                    if (brain != null && brain.NpcTransform != null && brain != this && brain.CanConverge(npc))
                    {
                        brain.SetTarget(AttackTarget, false);
                    }
                }
            }

            private bool IsIgnoredDoorTarget(BasePlayer player)
            {
                if (player != ignoredDoorTarget)
                {
                    return false;
                }
                if (!blockingDoor.IsKilled() && !blockingDoor.IsOpen() && !Senses.Memory.IsLOS(player))
                {
                    return true;
                }
                ignoredDoorTarget = null;
                blockingDoor = null;
                return false;
            }

            public void Forget(Door door)
            {
                if (AttackTarget != null)
                {
                    ignoredDoorTarget = AttackTarget;
                    blockingDoor = door;
                }
                Forget();
            }

            public void Forget()
            {
                Senses.Players.Clear();
                Senses.Memory.LOS.Clear();
                Senses.Memory.All.Clear();
                Senses.Memory.Threats.Clear();
                Senses.Memory.Targets.Clear();
                Senses.Memory.Players.Clear();
                Events?.Memory?.Clear();
                Navigator.ClearFacingDirectionOverride();

                AttackTarget = null;
                AttackTransform = null;
                DestinationOverride = GetRandomRoamPosition();
            }

            public void SetRange(float range)
            {
                SenseRange = ListenRange = range;
                if (range < raid.ProtectionRadius)
                {
                    range = raid.ProtectionRadius;
                }
                ScientistChaseRange = range * 1.25f;
                Senses.targetLostRange = TargetLostRange = range * 1.5f;
            }

            private void RandomMove(float radius) => RandomMove(AttackPosition, radius);

            private void RandomMove(Vector3 v, float radius)
            {
                if (ConVar.AI.useUnityNavmesh)
                {
                    Vector3 destination = v + UnityEngine.Random.onUnitSphere * radius;
                    destination.y = TerrainMeta.HeightMap.GetHeight(destination);
                    SetUnityDestination(destination);
                    return;
                }

                Vector2 offset = UnityEngine.Random.insideUnitCircle * radius;
                navigator.TryMoveNear(ServerPosition, new Vector3(v.x + offset.x, v.y, v.z + offset.y), radius);
            }

            public void RandomMove(float radius, float margin, float maxAngle = 100f)
            {
                Vector3 direction = ServerPosition - AttackPosition;
                if (SecondsSinceLastAttack > 2f || direction.sqrMagnitude > radius * radius)
                {
                    RandomMove(radius);
                    return;
                }

                direction.y = 0f;
                direction.Normalize();

                float halfAngleRadians = maxAngle * 0.5f * Mathf.Deg2Rad;
                float finalAngleRadians = Mathf.Atan2(direction.z, direction.x) + UnityEngine.Random.Range(-halfAngleRadians, halfAngleRadians);
                float marginalDistance = UnityEngine.Random.Range(Mathf.Max(0f, radius - margin), radius + margin);
                Vector3 tentativePosition = AttackPosition + new Vector3(Mathf.Cos(finalAngleRadians), 0f, Mathf.Sin(finalAngleRadians)) * marginalDistance;

                if (ConVar.AI.useUnityNavmesh)
                {
                    tentativePosition.y = TerrainMeta.HeightMap.GetHeight(tentativePosition);
                    SetUnityDestination(tentativePosition);
                }
                else
                {
                    navigator.TryMoveNear(ServerPosition, tentativePosition, radius + margin);
                }
            }

            public Vector3 GetAimDirection()
            {
                if (Navigator.IsOverridingFacingDirection)
                {
                    return Navigator.FacingDirectionOverride;
                }
                if (InRange2D(AttackPosition, ServerPosition, 1f))
                {
                    return npc.eyes.BodyForward();
                }
                return (AttackPosition - ServerPosition).normalized;
            }

            private void SetAimDirection()
            {
                Navigator.SetFacingDirectionEntity(AttackTarget);
                npc.SetAimDirection(GetAimDirection());
            }

            private void MoveToTarget()
            {
                Vector3 target = AttackPosition;
                if (!SetDestination(target) && !ConVar.AI.useUnityNavmesh)
                {
                    navigator.TryMoveNear(ServerPosition, target, 8f);
                }
            }

            private void MovementUpdate()
            {
                if (Abs(AttackPosition.y - ServerPosition.y) > 1f)
                {
                    MoveToTarget();
                    return;
                }

                if (isMurderer)
                {
                    if (AttackTarget.IsOnGround())
                    {
                        MoveToTarget();
                    }
                    else RandomMove(10f);
                }
                else
                {
                    float sqrDistance = (ServerPosition - AttackPosition).sqrMagnitude;
                    if (sqrDistance < 100f)
                    {
                        float radius = Mathf.Sqrt(100f - sqrDistance);
                        RandomMove(radius, 1f);
                        return;
                    }
                    MoveToTarget();
                }
            }

            private void SetDestination()
            {
                if (ConVar.AI.useUnityNavmesh)
                {
                    SetUnityDestination(GetRandomRoamPosition(), true);
                }
                else if (raid.NearFoundation(ServerPosition, 8f))
                {
                    RandomMove(ServerPosition, 8f);
                }
                else
                {
                    SetDestination(GetRandomRoamPosition());
                }
            }

            internal bool SetDestination(Vector3 destination)
            {
                return ConVar.AI.useUnityNavmesh ? SetUnityDestination(destination) : SetRustDestination(destination);
            }

            private bool SetUnityDestination(Vector3 destination, bool preserveHeight = false)
            {
                if (!IsInChaseRange(destination))
                {
                    if (isMurderer)
                    {
                        float range = Settings.CanLeave ? TargetLostRange : raid.ProtectionRadius * 0.9f;
                        float distance = UnityEngine.Random.Range(range - 5f, range - 1f);
                        Vector2 u = UnityEngine.Random.insideUnitCircle.normalized;
                        destination = raid.Location + new Vector3(u.x, 0f, u.y) * distance;
                    }
                    else
                    {
                        float range = Settings.CanLeave ? ScientistChaseRange : raid.ProtectionRadius * 0.9f;
                        Vector3 offset = (destination - raid.Location).normalized * range;
                        destination = raid.Location + offset;
                    }
                }

                if (destination != DestinationOverride)
                {
                    if (!preserveHeight)
                    {
                        destination.y = TerrainMeta.HeightMap.GetHeight(destination);

                        if (destination.y < -1f)
                        {
                            destination = GetRandomRoamPosition();
                        }
                    }

                    DestinationOverride = destination;
                }

                Navigator.SetCurrentSpeed(CurrentSpeed);

                if (Navigator.CurrentNavigationType == BaseNavigator.NavigationType.None && !Rust.Ai.AiManager.ai_dormant && !Rust.Ai.AiManager.nav_disable)
                {
                    Navigator.SetCurrentNavigationType(BaseNavigator.NavigationType.NavMesh);
                }

                if (Navigator.CanUseNavMesh && !Navigator.StuckOffNavmesh)
                {
                    if (!Navigator.SetDestination(destination, CurrentSpeed))
                    {
                        Navigator.Destination = destination;
                        npc.finalDestination = destination;
                    }

                    return true;
                }

                return false;
            }

            internal bool SetRustDestination(Vector3 destination)
            {
                if (!navigator.EnsureNavReady())
                {
                    return false;
                }

                if (!IsInChaseRange(destination))
                {
                    float height = destination.y;
                    if (isMurderer)
                    {
                        float range = Settings.CanLeave ? TargetLostRange : raid.ProtectionRadius * 0.9f;
                        float distance = UnityEngine.Random.Range(range - 5f, range - 1f);
                        Vector2 u = UnityEngine.Random.insideUnitCircle.normalized;
                        destination = raid.Location + new Vector3(u.x, 0f, u.y) * distance;
                    }
                    else
                    {
                        float range = Settings.CanLeave ? ScientistChaseRange : raid.ProtectionRadius * 0.9f;
                        Vector3 offset = (destination - raid.Location).normalized * range;
                        destination = raid.Location + offset;
                    }
                    destination.y = height;
                }

                if (!navigator.TrySampleDestination(destination, out var sampled))
                {
                    return false;
                }

                destination = sampled;
                if (!IsInChaseRange(destination) || !raid.IsAcceptableWaterDepth(destination))
                {
                    return false;
                }

                Vector3 previous = DestinationOverride;
                DestinationOverride = destination;
                if (navigator.TrySetDestination(destination, CurrentSpeed))
                {
                    return true;
                }

                DestinationOverride = previous;
                return false;
            }

            public bool SetTarget(BasePlayer player, bool converge = true)
            {
                if (isKilled || npc == null || npc.IsWounded())
                {
                    return false;
                }

                if (NpcTransform == null)
                {
                    DisableShouldThink();
                    Destroy(this);
                    return false;
                }

                if (player.IsKilled() || player.limitNetworking || !converge && IsIgnoredDoorTarget(player))
                {
                    return false;
                }

                if (AttackTarget == player)
                {
                    return true;
                }

                if (player == ignoredDoorTarget)
                {
                    ignoredDoorTarget = null;
                    blockingDoor = null;
                }

                TrySetKnown(player);
                npc.lastAttacker = player;
                AttackTarget = player;
                AttackTransform = player.transform;

                if (converge)
                {
                    Converge();
                }

                return true;
            }

            private bool TryReturnHome()
            {
                if (IsInEventRange(ServerPosition))
                {
                    return true;
                }

                if (!Settings.CanShoot && !IsInEventRange(AttackPosition) && !IsInEventRange(ServerPosition) || !IsInTargetRange(AttackPosition))
                {
                    CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

                    if (Warp())
                    {
                        npc.Heal(npc.MaxHealth());
                    }
                    else
                    {
                        DestinationOverride = GetRandomRoamPosition();
                    }

                    return true;
                }

                return false;
            }

            private void TryToAttack()
            {
                if (isKilled || npc == null || npc.IsWounded() || npc.IsSleeping())
                {
                    return;
                }

                BasePlayer attacker = GetBestTarget();

                if (attacker.IsNull())
                {
                    if (!TryReturnHome())
                    {
                        RandomMove(ServerPosition, 15f);
                    }

                    return;
                }

                if (ShouldForgetTarget(attacker))
                {
                    Forget();

                    return;
                }

                if (!SetTarget(attacker))
                {
                    return;
                }

                if (!CanAnySeeTarget(attacker))
                {
                    CurrentSpeed = BaseNavigator.NavigationSpeed.Fast;
                    MoveToTarget();
                    return;
                }

                if (attackType == AttackType.BaseProjectile)
                {
                    TryScientistActions();
                }
                else
                {
                    TryMurdererActions();
                }

                SwitchToState(AIState.Attack, -1);
            }

            private void TryMurdererActions()
            {
                if (ValidTarget)
                {
                    CurrentSpeed = BaseNavigator.NavigationSpeed.Fast;

                    if (attackType == AttackType.Explosive)
                    {
                        if (IsInAttackRange(20f))
                        {
                            RandomMove(15f);
                        }
                        else MovementUpdate();
                    }
                    else if (!IsInReachableRange())
                    {
                        MoveToTarget();
                    }
                    else if (!IsInAttackRange())
                    {
                        if (attackType == AttackType.FlameThrower)
                        {
                            RandomMove(attackRange);
                        }
                        else
                        {
                            MovementUpdate();
                        }
                    }
                    else MovementUpdate();
                }
                else
                {
                    TryReturnHome();
                    SetDestination();
                }
            }

            private void TryScientistActions()
            {
                if (ValidTarget)
                {
                    CurrentSpeed = BaseNavigator.NavigationSpeed.Fast;

                    if (Abs(AttackPosition.y - ServerPosition.y) > 1f || !CanSeeTarget(AttackTarget))
                    {
                        MovementUpdate();
                    }
                    else
                    {
                        RandomMove(15f, 1f);
                    }
                }
                else
                {
                    TryReturnHome();
                    SetDestination();
                }
            }

            public void SetupMovement()
            {
                if (npc == null || npc.IsDestroyed)
                {
                    DisableShouldThink();
                    Destroy(this);
                    return;
                }

                if (isKilled || movementStarted)
                {
                    return;
                }

                movementStarted = true;
                npc.InvokeRepeating(TryToRoam, 0f, UnityEngine.Random.Range(6f, 7f));
                npc.InvokeRepeating(TryToAttack, 1f, 1f);
            }

            private void TryToRoam()
            {
                if (isKilled || npc == null || npc.IsDestroyed || npc.IsWounded() || npc.IsSleeping())
                {
                    return;
                }

                if (Settings.KillUnderwater && npc.playerCollider != null && npc.IsSwimming())
                {
                    DisableShouldThink();
                    SafelyKillNpc(npc);
                    Destroy(this);
                    return;
                }

                if (ValidTarget)
                {
                    return;
                }

                CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

                SetDestination();
            }

            public bool Warp()
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (isKilled || now < lastWarpTime)
                {
                    return false;
                }

                DestinationOverride = RandomNearPositions.GetRandom();

                Forget();

                if (!npc.IsWounded() && Navigator.Warp(DestinationOverride))
                {
                    lastWarpTime = now + 15d;
                    return true;
                }

                lastWarpTime = now + 1d;
                return false;
            }

            private void UseFlameThrower()
            {
                if (flameThrower == null || npc == null)
                {
                    return;
                }
                if (flameThrower.ammo < flameThrower.maxAmmo * 0.25)
                {
                    flameThrower.SetFlameState(false);
                    flameThrower.ServerReload();
                    return;
                }
                npc.triggerEndTime = Time.time + attackCooldown;
                flameThrower.SetFlameState(true);
                flameThrower.Invoke(() => flameThrower.SetFlameState(false), 2f);
            }

            private void UseWaterGun()
            {
                if (Physics.Raycast(npc.eyes.BodyRay(), out var hit, 10f, 1218652417))
                {
                    WaterBall.DoSplash(hit.point, 2f, ItemManager.FindItemDefinition("water"), 10);
                    DamageUtil.RadiusDamage(npc, liquidWeapon.LookupPrefab(), hit.point, 0.15f, 0.15f, new(), 131072, true);
                }
            }

            private void UseChainsaw()
            {
                AttackEntity.TopUpAmmo();
                AttackEntity.ServerUse();
                AttackTarget.Hurt(10f * AttackEntity.npcDamageScale, DamageType.Bleeding, npc);
            }

            private void EmulatedFire()
            {
                if (launcher.HasAttackCooldown()) return;
                float dist;
                string prefab;
                switch (launcher.ShortPrefabName)
                {
                    case "rocket_launcher.entity":
                        prefab = "assets/prefabs/ammo/rocket/rocket_basic.prefab";
                        dist = ServerPosition.Distance(AttackPosition);
                        launcher.repeatDelay = 6f;
                        break;
                    case "mgl.entity":
                        prefab = "assets/prefabs/ammo/40mmgrenade/40mm_grenade_he.prefab";
                        launcher.repeatDelay = 4f;
                        dist = ServerPosition.Distance(AttackPosition) + 5f;
                        break;
                    default: return;
                }
                Vector3 euler = launcher.MuzzlePoint.transform.forward + Vector3.up;
                Vector3 position = launcher.MuzzlePoint.transform.position + (Vector3.up * 1.6f);
                BaseEntity entity = GameManager.server.CreateEntity(prefab, position, GetEntity().eyes.GetLookRotation());
                if (entity == null) return;
                entity.creatorEntity = GetEntity();
                if (entity.TryGetComponent(out ServerProjectile serverProjectile))
                {
                    serverProjectile.InitializeVelocity(Quaternion.Euler(euler) * entity.transform.forward * dist);
                }
                if (entity is TimedExplosive explosive)
                {
                    explosive.timerAmountMin = 1;
                    explosive.timerAmountMax = 15;
                }
                entity.Spawn();
                launcher.StartAttackCooldown(launcher.repeatDelay);
            }

            private void MeleeAttack()
            {
                if (baseMelee.IsNull())
                {
                    return;
                }

                if (AttackEntity is Chainsaw)
                {
                    UseChainsaw();
                    return;
                }

                Vector3 position = AttackPosition;
                AttackEntity.StartAttackCooldown(AttackEntity.repeatDelay * 2f);
                npc.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty, null);
                if (baseMelee.swingEffect.isValid)
                {
                    Effect.server.Run(baseMelee.swingEffect.resourcePath, position, Vector3.forward, npc.Connection, false);
                }
                HitInfo info = new()
                {
                    damageTypes = new(),
                    DidHit = true,
                    Initiator = npc,
                    HitEntity = AttackTarget,
                    HitPositionWorld = position,
                    HitPositionLocal = AttackTransform.InverseTransformPoint(position),
                    HitNormalWorld = npc.eyes.BodyForward(),
                    HitMaterial = StringPool.Get("Flesh"),
                    PointStart = ServerPosition,
                    PointEnd = position,
                    Weapon = AttackEntity,
                    WeaponPrefab = AttackEntity
                };

                info.damageTypes.Set(DamageType.Slash, baseMelee.TotalDamage() * AttackEntity.npcDamageScale);
                Effect.server.ImpactEffect(info);
                AttackTarget.OnAttacked(info);
            }

            public bool TryThrowWeapon()
            {
                if (!IsInThrowRange() || !(AttackEntity is ThrownWeapon thrownWeapon))
                {
                    return false;
                }

                npc.SetAiming(true);
                SetAimDirection();

                npc.Invoke(() =>
                {
                    if (isKilled || thrownWeapon.IsKilled())
                    {
                        return;
                    }

                    Item item = npc.GetActiveItem();
                    if (item == null || item.parent != npc.inventory.containerBelt || item.GetHeldEntity() != thrownWeapon || thrownWeapon.GetParentEntity() != npc)
                    {
                        npc.SetAiming(false);
                        return;
                    }

                    if (!ValidTarget)
                    {
                        CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

                        Forget();
                        SetDestination();
                        npc.SetAiming(false);

                        return;
                    }

                    if (IsInThrowRange() && CanSeeTarget(AttackTarget))
                    {
                        item.amount++;
                        thrownWeapon.ServerThrow(AttackPosition);
                    }
                    else nextAttackTime = Time.realtimeSinceStartupAsDouble + 1f;

                    npc.SetAiming(false);
                    RandomMove(15f);
                }, 1f);

                return true;
            }

            private bool CanConverge(HumanoidNPC other)
            {
                if (ValidTarget || other.IsKilled() || other.IsDead()) return false;
                return IsInTargetRange(other.transform.position);
            }

            private bool CanAnySeeTarget(BasePlayer target)
            {
                foreach (var npc in raid.npcs)
                {
                    if (npc != null && !npc.IsDestroyed && npc != this.npc && npc.Brain.AttackTarget == target && npc.Brain.SecondsSinceLastAttack < 2)
                    {
                        return true;
                    }
                }
                return CanSeeTarget(target);
            }

            private bool CanSeeTarget(BasePlayer target)
            {
                if (Senses.Memory.IsLOS(target))
                {
                    return true;
                }

                nextAttackTime = Time.realtimeSinceStartupAsDouble + 1f;

                return false;
            }

            public bool CanRoam(Vector3 destination) => destination == DestinationOverride && IsInSenseRange(destination);

            private bool CanShoot()
            {
                if (attackType == AttackType.None)
                {
                    return false;
                }

                return Settings.CanShoot || attackType != AttackType.BaseProjectile && attackType != AttackType.Explosive || IsInEventRange(AttackPosition);
            }

            private void TrySetKnown(BasePlayer player)
            {
                if (Senses.ownerAttack != null && !Senses.Memory.IsPlayerKnown(player) && !Senses.Memory.Targets.Contains(player))
                {
                    Senses.Memory.SetKnown(player, npc, Senses);
                }
            }

            public BasePlayer GetBestTarget()
            {
                if (npc.IsWounded())
                {
                    return null;
                }
                if (AttackTarget != null)
                {
                    return AttackTarget;
                }
                float sqrSenseRange = SenseRange * SenseRange;
                float delta = -1f;
                BasePlayer target = null;
                foreach (var entity in Senses.Memory.Players)
                {
                    if (!(entity is BasePlayer player) || ShouldForgetTarget(player) || !IsInSenseRange(player.transform.position) || IsIgnoredDoorTarget(player)) continue;
                    if (!config.Settings.Management.TargetNpcs && !player.IsHuman()) continue;
                    float sqrDist = (player.transform.position - npc.transform.position).sqrMagnitude;
                    float rangeDelta = 1f - Mathf.InverseLerp(1f, sqrSenseRange, sqrDist);
                    rangeDelta += (CanSeeTarget(player) ? 2f : 0f);
                    if (rangeDelta <= delta) continue;
                    target = player;
                    delta = rangeDelta;
                }
                if (delta <= 0)
                    return null;
                return target;
            }

            private bool IsAttackOnCooldown()
            {
                if (attackType == AttackType.None || Time.realtimeSinceStartupAsDouble < nextAttackTime)
                {
                    return true;
                }

                if (attackCooldown > 0f)
                {
                    nextAttackTime = Time.realtimeSinceStartupAsDouble + attackCooldown;
                }

                return false;
            }

            private Vector3 GetRandomRoamPosition() => RandomRoamPositions.GetRandom();

            private bool IsInAttackRange(float range = 0f) => InRange(ServerPosition, AttackPosition, range == 0f ? attackRange : range);

            private bool IsInEventRange(Vector3 destination) => InRange(raid.Location, destination, Mathf.Min(raid.ProtectionRadius, TargetLostRange));

            private bool IsInReachableRange() => AttackPosition.y - ServerPosition.y <= attackRange && (attackType != AttackType.Melee || InRange(AttackPosition, ServerPosition, 15f));

            private bool IsInSenseRange(Vector3 destination) => InRange2D(raid.Location, destination, SenseRange);

            private bool IsInTargetRange(Vector3 destination) => InRange2D(raid.Location, destination, !Settings.CanShoot ? Mathf.Min(raid.ProtectionRadius, TargetLostRange) : TargetLostRange);

            private bool IsInChaseRange(Vector3 destination) => InRange(raid.Location, destination, !Settings.CanShoot || !Settings.CanLeave ? raid.ProtectionRadius : isMurderer ? TargetLostRange : ScientistChaseRange);

            private bool IsInThrowRange() => InRange(ServerPosition, AttackPosition, attackRange);

            private bool ShouldForgetTarget(BasePlayer target) => target.IsKilled() || target.health <= 0f || target.limitNetworking || target.IsDead() || target is HumanoidNPC || !IsInTargetRange(target.transform.position);
        }

        public class Raider
        {
            public bool HasDestroyed, IsAlly, IsAllowed, IsParticipant, PreEnter = true, eligible = true, rewards = true;
            public double lastActiveTime;
            public float TotalDamage, participantTime;
            public string id, displayName;
            public ulong userid;
            public PlayerInputEx Input;
            private BasePlayer _player;
            public Vector3 lastPosition;
            public BasePlayer player { get { if (_player == null) { _player = RustCore.FindPlayerById(userid); } return _player; } }
            public Raider(ulong userid, string username)
            {
                this.userid = userid;
                id = userid.ToString();
                displayName = username;
            }
            public Raider(BasePlayer target)
            {
                _player = target;
                userid = target.userID;
                id = target.UserIDString;
                displayName = target.displayName;
            }
            public void DestroyInput()
            {
                if (Input != null && !Input.isDestroyed)
                {
                    Input.isDestroyed = true;
                    UnityEngine.Object.Destroy(Input);
                }
            }
            public void CheckInput(BasePlayer player, RaidableBase raid)
            {
                if (Input == null && player.IsOnline())
                {
                    _player = player;

                    Input = VLB.Utils.GetOrAddComponent<PlayerInputEx>(player.gameObject);

                    Input.Setup(raid, this);

                    UI.UpdateStatusUI(raid.Instance, player);
                }
            }
        }

        public class RaidableBase : FacepunchBehaviour
        {
            public HashSet<ulong> alliance = Pool.Get<HashSet<ulong>>();
            public HashSet<ulong> cooldowns = Pool.Get<HashSet<ulong>>();
            public HashSet<ulong> intruders = new();
            public Dictionary<ItemId, float> conditions = Pool.Get<Dictionary<ItemId, float>>();
            internal List<Fridge> fridges = Pool.Get<List<Fridge>>();
            internal HashSet<StorageContainer> _containers = new();
            internal HashSet<StorageContainer> _allcontainers = new();
            public List<HumanoidNPC> npcs = Pool.Get<List<HumanoidNPC>>();
            public List<PressButton> buttons = Pool.Get<List<PressButton>>();
            public List<WeaponRack> weaponRacks = Pool.Get<List<WeaponRack>>();
            public List<BackpackData> backpacks = Pool.Get<List<BackpackData>>();
            public List<Vector3> compound = new();
            public List<Vector3> foundations = new();
            public List<Vector3> floors = new();
            public List<BaseEntity> locks = Pool.Get<List<BaseEntity>>();
            private List<BuildingBlock> blocks = Pool.Get<List<BuildingBlock>>();
            private List<Vector3> _inside = Pool.Get<List<Vector3>>();
            private List<SphereEntity> spheres = Pool.Get<List<SphereEntity>>();
            private List<IOEntity> lights = Pool.Get<List<IOEntity>>();
            private List<BaseOven> ovens = Pool.Get<List<BaseOven>>();
            public Dictionary<ulong, TurretInfo> turrets = Pool.Get<Dictionary<ulong, TurretInfo>>();
            private List<Door> navigationDoors = Pool.Get<List<Door>>();
            private List<Door> doors = Pool.Get<List<Door>>();
            public List<string> ids = Pool.Get<List<string>>();
            private List<CustomDoorManipulator> doorControllers = Pool.Get<List<CustomDoorManipulator>>();
            private List<Locker> lockers = Pool.Get<List<Locker>>();
            private Dictionary<string, Dictionary<SkinType, ulong>> _shortnameToSkin = Pool.Get<Dictionary<string, Dictionary<SkinType, ulong>>>();
            private Dictionary<uint, ulong> _prefabToSkin = Pool.Get<Dictionary<uint, ulong>>();
            private Dictionary<int, ulong> _itemIdToSkin = Pool.Get<Dictionary<int, ulong>>();
            internal Dictionary<TriggerBase, BaseEntity> triggers = Pool.Get<Dictionary<TriggerBase, BaseEntity>>();
            private List<SleepingBag> _beds = Pool.Get<List<SleepingBag>>();
            private Dictionary<SleepingBag, ulong> _bags = Pool.Get<Dictionary<SleepingBag, ulong>>();
            public List<SamSite> samsites = Pool.Get<List<SamSite>>();
            public List<VendingMachine> vms = Pool.Get<List<VendingMachine>>();
            public List<DamageMultiplier> PlayerDamageMultiplier = new();
            public List<ulong> HintCooldowns = Pool.Get<List<ulong>>();
            private List<BuildingPrivlidge> privs = Pool.Get<List<BuildingPrivlidge>>();
            public Dictionary<ulong, Raider> raiders = new();
            public BuildingPrivlidge priv;
            public List<ulong> TeleportExceptions = new();
            private List<string> murdererKits = new();
            private List<string> scientistKits = new();
            private MapMarkerExplosion explosionMarker;
            private MapMarkerGenericRadius genericMarker;
            private VendingMachineMapMarker vendingMarker;
            public Coroutine setupRoutine = null;
            public Coroutine turretsCoroutine = null;
            public GameObject go;
            private bool IsPrivDestroyed;
            public bool IsDespawning;
            public Vector3 Location;
            public Vector3 LocationXZ3D;
            public string ProfileName;
            public float BaseHeight;
            public string BaseName;
            public Color NoneColor;
            public bool ownerFlag;
            public string ID = "0";
            public ulong ownerId;
            public string ownerName;
            public float loadTime;
            public DateTime spawnDateTime;
            public DateTime despawnDateTime = DateTime.MaxValue;
            public float AddNearTime;
            public bool AllowPVP;
            public BuildingOptions Options;
            public bool IsAuthed;
            public bool IsOpened = true;
            public bool IsResetting;
            public int npcMaxAmountMurderers;
            public int npcMaxAmountScientists;
            public RaidableType Type;
            public bool IsLoading;
            public bool InitiateTurretOnSpawn;
            private bool markerCreated;
            private int itemAmountSpawned;
            public bool privSpawned;
            public bool privHadLoot;
            public string markerName;
            public string NoMode;
            public bool isAuthorized;
            public bool IsEngaged;
            public int _undoLimit;
            internal Dictionary<ulong, HumanoidBrain> HumanoidBrains;
            public Dictionary<NetworkableId, RaidElevator> Elevators = Pool.Get<Dictionary<NetworkableId, RaidElevator>>();
            public HashSet<BaseEntity> Entities = new();
            public HashSet<BaseEntity> DespawnExceptions = new();
            public HashSet<BaseEntity> BuiltList = new();
            public RaidableSpawns spawns;
            public RandomBase rb = new();
            public float RemoveNearDistance;
            public bool IsAnyLooted;
            public bool IsDamaged;
            public bool IsEligible = true;
            public bool IsCompleted;
            public float ProtectionRadius = 50f;
            public float SqrProtectionRadius = 2500f;
            public RaidableBases Instance;
            public bool stability;
            private int numLootRequired;
            public List<ulong> NotifiedNearby = new();
            public BasePlayer cached_attacker;
            public ulong cached_attacker_id;
            public double cached_attack_time;

            public float ProtectionRadiusSqr(float tolerance) => (ProtectionRadius + tolerance) * (ProtectionRadius + tolerance);
            public bool EjectBackpacksPVE => !AllowPVP && Options.EjectBackpacksPVE;
            public bool PlayersLootable => AllowPVP ? config.Settings.Management.PlayersLootableInPVP : config.Settings.Management.PlayersLootableInPVE;
            public List<string> BlacklistedCommands => AllowPVP ? Options.BlacklistedPVPCommands : Options.BlacklistedPVECommands;
            public SpawnsControllerManager SpawnsController => Instance.SpawnsController;
            public StoredData data => Instance.data;
            public Configuration config => Instance.config;
            public bool IsUnloading => Instance.IsUnloading;
            public bool IsShuttingDown => Instance.IsShuttingDown;

            private double nextHookTime;
            private object[] _hookObjects;
            public object[] hookObjects
            {
                get
                {
                    double time = Time.realtimeSinceStartupAsDouble;
                    if (time > nextHookTime)
                    {
                        nextHookTime = time + 0.1d;
                        _hookObjects = new object[17] { Location, 512, AllowPVP, ID, 0f, 0f, loadTime, ownerId, GetOwner(), GetRaiders(), GetIntruders(), Entities.ToList(), BaseName, spawnDateTime, despawnDateTime, ProtectionRadius, GetLootAmountRemaining() };
                    }
                    return _hookObjects;
                }
            }

            public int DespawnMinutes => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.DespawnMinutes : config.Settings.Management.DespawnMinutes;

            public bool DespawnMinutesReset => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.DespawnMinutesReset : config.Settings.Management.DespawnMinutesReset;

            public int DespawnMinutesInactive => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.DespawnMinutesInactive : config.Settings.Management.DespawnMinutesInactive;

            public bool DespawnMinutesInactiveReset => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.DespawnMinutesInactiveReset : config.Settings.Management.DespawnMinutesInactiveReset;

            public bool EngageOnBaseDamage => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.Engaged : config.Settings.Management.Engaged;

            public bool EngageOnNpcDeath => Options.DespawnOptions.OverrideConfig ? Options.DespawnOptions.EngagedNpc : config.Settings.Management.EngagedNpc;

            public string GetPercentCompleteMessage() => IsDespawning ? "DESPAWNING" : IsLoading ? "LOADING" : string.Join(", ", GetRaiders().Select(x => x.displayName)) is string str && !string.IsNullOrEmpty(str) ? str : "INACTIVE";

            public double GetPercentComplete() => IsDespawning ? 100.0 : IsLoading ? 0.0 : Math.Max(0.0, Math.Round((((double)numLootRequired - (double)GetLootAmountRemaining()) / (double)numLootRequired) * 100.0, 2));

            public int GetLootAmountRemaining()
            {
                int num = _containers.Sum(x => IsContainerKilled(x) ? 0 : x.inventory.itemList.Count);

                if (num > numLootRequired)
                {
                    numLootRequired = num;
                }

                return num;
            }

            public bool IsPlayerTurret(BaseEntity entity) => entity is AutoTurret && entity.OwnerID.IsSteamId() && BuiltList.Contains(entity);

            public bool Owns(AutoTurret turret)
            {
                if (turret == null || turret.enableSaving) return false;
                if (turret.skinID == RB_SKIN_ID) return true;
                foreach (TurretInfo info in turrets.Values)
                {
                    if (info.turret != null && info.turret == turret)
                    {
                        return true;
                    }
                }
                return false;
            }

            public bool Owns(BaseEntity entity) => entity != null && (entity is HumanoidNPC || (entity.OwnerID == 0uL && !entity.enableSaving && Entities.Contains(entity)));

            public bool Has(BaseEntity entity) => Entities.Contains(entity);

            public bool IsBox(BaseEntity entity, bool inherit) => Instance.IsBox(entity, inherit);

            public string FormatGridReference(BasePlayer player, Vector3 v) => Instance.FormatGridReference(player, v);

            public bool IsRaider(BasePlayer target) => intruders.Contains(target.userID) || raiders.ContainsKey(target.userID);

            private void OnDestroy()
            {
                Despawn();
            }

            public bool CanDropRustBackpack(ulong userid)
            {
                if (AllowPVP ? config.Settings.Management.RustBackpacksPVP : config.Settings.Management.RustBackpacksPVE)
                {
                    return !userid.HasPermission("raidablebases.keepbackpackrust") && raiders.TryGetValue(userid, out var ri);
                }
                return false;
            }

            public bool CanDropBackpack(ulong userid)
            {
                if (AllowPVP ? config.Settings.Management.BackpacksPVP : config.Settings.Management.BackpacksPVE)
                {
                    return !userid.HasPermission("raidablebases.keepbackpackplugin") && raiders.TryGetValue(userid, out var ri);
                }
                return false;
            }

            public Raider GetRaider(BasePlayer player)
            {
                if (!raiders.TryGetValue(player.userID, out var ri))
                {
                    raiders[player.userID] = ri = new(player);
                }
                return ri;
            }

            public bool CanHurtBox(BaseEntity entity)
            {
                if (Options.InvulnerableUntilCupboardIsDestroyed && IsBox(entity, false) && !IsContainerKilled(priv)) return false;
                if (Options.Invulnerable && IsBox(entity, false)) return false;
                return true;
            }

            public void DestroyGroundCheck(BaseEntity entity)
            {
                if (entity.GetParentEntity() is Tugboat) return;
                if (entity.TryGetComponent<GroundWatch>(out var obj1)) Destroy(obj1);
                if (entity.TryGetComponent<DestroyOnGroundMissing>(out var obj2)) Destroy(obj2);
            }

            public void SetupEntity(BaseEntity entity, bool skipCheck = true)
            {
                if (entity == null) return;
                if (entity.net == null) entity.net = Net.sv.CreateNetworkable();
                if (skipCheck) AddEntity(entity);
                else if (entity is Door door && navigationDoors != null && !navigationDoors.Contains(door)) navigationDoors.Add(door);
            }

            internal HashSet<NetworkableId> EntityIds = Pool.Get<HashSet<NetworkableId>>();
            public void AddEntity(BaseEntity entity)
            {
                if (EntityIds != null && TryGetNetworkId(entity, out NetworkableId id))
                {
                    if (entity is Door door && navigationDoors != null && !navigationDoors.Contains(door))
                    {
                        navigationDoors.Add(door);
                    }
                    EntityIds.Add(id);
                    Entities.Add(entity);
                    Instance.EntityToRaid[id] = this;
                }
            }

            public void FreeToPool()
            {
                Interface.CallHook("OnRaidableBaseEnded", hookObjects);

                if (EntityIds != null)
                {
                    foreach (var id in EntityIds)
                    {
                        Instance.EntityToRaid.Remove(id);
                    }

                    Pool.FreeUnmanaged(ref EntityIds);
                }

                ResetToPool(ref ids);
                ResetToPool(ref vms);
                ResetToPool(ref npcs);
                ResetToPool(ref _bags);
                ResetToPool(ref _beds);
                ResetToPool(ref doors);
                ResetToPool(ref locks);
                ResetToPool(ref privs);
                ResetToPool(ref ovens);
                ResetToPool(ref blocks);
                ResetToPool(ref lights);
                ResetToPool(ref lockers);
                ResetToPool(ref spheres);
                ResetToPool(ref turrets);
                ResetToPool(ref fridges);
                ResetToPool(ref _inside);
                ResetToPool(ref buttons);
                ResetToPool(ref alliance);
                ResetToPool(ref samsites);
                ResetToPool(ref triggers);
                ResetToPool(ref Elevators);
                ResetToPool(ref cooldowns);
                ResetToPool(ref conditions);
                ResetToPool(ref weaponRacks);
                ResetToPool(ref HintCooldowns);
                ResetToPool(ref _prefabToSkin);
                ResetToPool(ref _itemIdToSkin);
                ResetToPool(ref navigationDoors);
                ResetToPool(ref doorControllers);
                ResetToPool(ref _shortnameToSkin);
                if (backpacks != null)
                {
                    for (var i = 0; i < backpacks.Count; i++)
                    {
                        var backpack = backpacks[i];
                        ResetToPool(ref backpack);
                    }
                }
            }

            public void Message(string key, params object[] args)
            {
                foreach (var raider in raiders.Values)
                {
                    Message(raider.player, key, args);
                }
            }

            public void Message(BasePlayer player, string key, params object[] args)
            {
                Instance.Message(player, key, args);
            }

            public void TryMessage(BasePlayer player, string key, params object[] args)
            {
                Instance.TryMessage(player, key, args);
            }

            public void QueueNotification(BasePlayer player, string key, params object[] args)
            {
                if (!Options.Smart)
                {
                    Instance.Message(player, key, args);
                }
            }

            public string mx(string key, string id = null, params object[] args) => Instance.mx(key, id, args);

            public void SetupCollider()
            {
                go.transform.position = Location;
                go.layer = (int)Layer.Reserved1;

                if (!go.TryGetComponent<SphereCollider>(out var collider))
                {
                    collider = go.AddComponent<SphereCollider>();
                }

                if (collider != null)
                {
                    collider.radius = ProtectionRadius;
                    collider.isTrigger = true;
                    collider.center = Vector3.zero;
                }

                if (!go.TryGetComponent<Rigidbody>(out var rigidbody))
                {
                    rigidbody = go.AddComponent<Rigidbody>();
                }

                if (rigidbody != null)
                {
                    rigidbody.isKinematic = true;
                    rigidbody.useGravity = false;
                    rigidbody.detectCollisions = true;
                    rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                }
            }

            public HashSet<BaseEntity> enteredEntities = new();

            private void OnTriggerEnter(Collider collider)
            {
                if (collider == null || collider.ObjectName() == "ZoneManager")
                    return;

                var entity = collider.ToBaseEntity();
                if (entity == null || entity.IsDestroyed)
                    return;

                if (IsUnderground(entity.transform.position))
                    return;

                switch (entity)
                {
                    case BasePlayer player when player.IsHuman() && !(player.GetMounted() is ZiplineMountable):
                        HandlePlayerEntering(player);
                        break;

                    case BaseMountable mount when !(mount is ZiplineMountable) && !(mount is BaseChair && !mount.OwnerID.IsSteamId()):
                        HandleMountableEntering(mount);
                        break;

                    case HotAirBalloon hab:
                        HandleHotAirBalloonEntering(hab);
                        break;

                    default:
                        HandleDefaultEntity(entity, Options.Mounts.Other);
                        break;
                }
            }

            private void HandleDefaultEntity(BaseEntity entity, bool enabled)
            {
                if (enabled && IsCustomEntity(entity))
                {
                    Eject(entity, Location, ProtectionRadius + 15f, false);
                }
                else if (entity is Drone && !(entity is DeliveryDrone))
                {
                    if (Options.Mounts.Drones) Eject(entity, Location, ProtectionRadius + 15, false);
                    else BuiltList.Add(entity);
                }
                else if (Options.Mounts.RFExplosivesAboveDome && entity is RFTimedExplosive te && NearFoundation(entity.transform.position, 15f))
                {
                    HandleEntityToItem(te);
                }
            }

            private void OnTriggerExit(Collider collider)
            {
                if (collider == null || collider.ObjectName() == "ZoneManager")
                    return;

                var entity = collider.ToBaseEntity();
                if (entity == null)
                    return;

                if (!enteredEntities.Remove(entity))
                    return;

                switch (entity)
                {
                    case BasePlayer player:
                        HandlePlayerExiting(player);
                        break;

                    case BaseMountable mount:
                        HandleMountableExiting(mount);
                        break;

                    case HotAirBalloon hab:
                        HandleHotAirBalloonExiting(hab);
                        break;
                }
            }

            public void HandleEntityToItem(RFTimedExplosive te)
            {
                if (InRange(te.transform.position, Location, ProtectionRadius - 3f))
                {
                    return;
                }
                ItemDefinition itemToGive = te.pickupDefinition;
                if (itemToGive == null)
                {
                    Eject(te, Location, ProtectionRadius + 15f, false);
                    return;
                }
                Item item = ItemManager.Create(itemToGive, 1, te.skinID);
                if (te.ItemOwnership.IsValid())
                {
                    item.SetItemOwnership(te.ItemOwnership);
                }
                item.Drop(te.transform.position, te.GetDropVelocity());
                te.Invoke(te.SafelyKill, 0.01f);
            }

            public void HandlePlayerEntering(BasePlayer player)
            {
                if (enteredEntities.Add(player))
                {
                    OnPreEnterRaid(player);
                }
            }

            public void HandlePlayerExiting(BasePlayer player)
            {
                OnPlayerExit(player, player.IsDead());
                intruders.Remove(player.userID);
                enteredEntities.Remove(player);
            }

            private void HandleMountableEntering(BaseMountable m)
            {
                if (enteredEntities.Add(m))
                {
                    using var players = GetMountedPlayers(m);

                    if (TryRemoveMountable(m, players))
                    {
                        players.ForEach(HandlePlayerExiting);
                    }
                    else
                    {
                        //players.ForEach(OnPreEnterRaid);
                    }
                }
            }

            private void HandleMountableExiting(BaseMountable m)
            {
                using var players = GetMountedPlayers(m);
                players.ForEach(HandlePlayerExiting);
                if (players.Count > 0)
                {
                    RemoveMountedEntity(m);
                }
            }

            private void HandleHotAirBalloonEntering(HotAirBalloon hab)
            {
                if (enteredEntities.Add(hab))
                {
                    using var players = GetMountedPlayers(hab);

                    if (TryRemoveMountable(hab, players))
                    {
                        players.ForEach(HandlePlayerExiting);
                    }
                    else
                    {
                        //players.ForEach(OnPreEnterRaid);
                    }
                }
            }

            private void HandleHotAirBalloonExiting(HotAirBalloon hab)
            {
                using var players = GetMountedPlayers(hab);

                players.ForEach(HandlePlayerExiting);

                if (players.Count > 0)
                {
                    RemoveMountedEntity(hab);
                }
            }

            public void RemoveMountedEntity(BaseEntity entity)
            {
                if (!config.Settings.Management.DespawnMounts && entity != null)
                {
                    if (entity.skinID == RB_SKIN_ID)
                    {
                        entity.skinID = 0;
                    }
                    DespawnExceptions.Add(entity);
                    BuiltList.Add(entity);
                    if (entity.children != null)
                    {
                        foreach (var child in entity.children)
                        {
                            if (child.skinID == RB_SKIN_ID) child.skinID = 0;
                            BuiltList.Add(child);
                            DespawnExceptions.Add(child);
                        }
                    }
                }
            }

            public bool IsUnderground(Vector3 a) => !isEventUnderground && Location.y - a.y > 15f && EnvironmentManager.Check(a, EnvironmentType.TrainTunnels | EnvironmentType.Underground);

            public bool CanRespawnAt(BasePlayer target) => config.Settings.Management.AllowRespawn && target.lifeStory != null && target.lifeStory.secondsAlive <= 1.5f;

            public bool WasConnected(BasePlayer target) => raiders.TryGetValue(target.userID, out var raider) && raider.IsParticipant && InRange(raider.lastPosition, Location, ProtectionRadius);

            public bool IsParticipant(BasePlayer target) => raiders.TryGetValue(target.userID, out var raider) && raider.IsParticipant;

            public void HandleTurretSight(BasePlayer target)
            {
                if (turrets.Count > 0)
                {
                    using var keys = DisposableList<ulong>();

                    foreach (var pair in turrets)
                    {
                        TurretInfo info = pair.Value;

                        if (IsContainerKilled(info))
                        {
                            keys.Add(pair.Key);
                            continue;
                        }

                        AutoTurret turret = info.turret;

                        if (turret.sightRange > Options.AutoTurret.SightRange)
                        {
                            SetupSightRange(turret, Options.AutoTurret.SightRange);
                        }
                        if (turret.target != null && turret.target == target)
                        {
                            turret.SetNoTarget();
                        }
                    }

                    foreach (ulong key in keys)
                    {
                        turrets.Remove(key);
                    }
                }
            }

            public DamageResult OnTurretTarget(AutoTurret turret, BasePlayer victim, bool isRaidTurret)
            {
                if (isRaidTurret)
                {
                    return DamageResult.Allowed;
                }
                if (IsEventDrone(turret))
                {
                    return DamageResult.None;
                }
                if (Options.BlockOutsideTurrets && !InRange(turret.transform.position, Location, ProtectionRadius - 0.5f))
                {
                    if (turret.OwnerID.IsSteamId())
                    {
                        turret.SetNoTarget();
                        return DamageResult.Blocked;
                    }
                    return DamageResult.None;
                }
                if (victim != null && !victim.IsHuman())
                {
                    return DamageResult.Allowed;
                }
                return AllowPVP ? DamageResult.Allowed : (turret.OwnerID.IsSteamId() ? DamageResult.Blocked : DamageResult.None);
            }

            private void OnPreEnterRaid(BasePlayer target)
            {
                if (target.IsNull() || !target.IsHuman())
                {
                    return;
                }

                if (target.IsDead())
                {
                    intruders.Remove(target.userID);
                    enteredEntities.Remove(target);
                    return;
                }

                if (intruders.Contains(target.userID) && raiders.ContainsKey(target.userID))
                {
                    GetRaider(target).CheckInput(target, this);
                    return;
                }

                if (IsLoading && Type != RaidableType.None && !CanBypass(target))
                {
                    RemovePlayer(target, Location, ProtectionRadius, Type);
                    return;
                }

                if (RemoveFauxAdmin(target) || IsScavenging(target))
                {
                    return;
                }

                if (!TeleportExceptions.Contains(target.userID) && CanRespawnAt(target))
                {
                    TeleportExceptions.Add(target.userID);
                }

                OnEnterRaid(target, false);
            }

            public void OnEnterRaid(BasePlayer target, bool checkUnderground = true)
            {
                if (checkUnderground && IsUnderground(target.transform.position))
                {
                    intruders.Remove(target.userID);
                    enteredEntities.Remove(target);
                    return;
                }

                if (Type != RaidableType.None && CannotEnter(target, true))
                {
                    return;
                }

                Raider ri = GetRaider(target);

                ri.CheckInput(target, this);

                if (!intruders.Add(target.userID) && raiders.ContainsKey(target.userID))
                {
                    return;
                }

                Protector();

                if (!intruders.Contains(target.userID))
                {
                    return;
                }

                UI.UpdateStatusUI(Instance, target);

                StopUsingWeapon(target);

                if (config.EventMessages.AnnounceEnterExit)
                {
                    QueueNotification(target, AllowPVP ? "OnPlayerEntered" : "OnPlayerEnteredPVE");
                    if (Options.BlocksImmune && config.EventMessages.BlocksImmune) QueueNotification(target, "Blocks Immune");
                }

                ri.PreEnter = false;

                HolsterWeapon(target);

                SwitchToState();

                if (mapNote != null && target.userID == ownerId)
                {
                    DestroyMapNote(target);
                }

                Interface.CallHook("OnPlayerEnteredRaidableBase", new object[] { target, Location, AllowPVP, 512, ID, 0f, 0f, loadTime, ownerId, BaseName, spawnDateTime, despawnDateTime, ProtectionRadius, GetLootAmountRemaining() });
            }

            private void SwitchToState()
            {
                foreach (var brain in HumanoidBrains.Values)
                {
                    if (!brain.states.IsNullOrEmpty() && InRange2D(brain.DestinationOverride, Location, brain.SenseRange))
                    {
                        brain.SwitchToState(AIState.Attack, -1);
                    }
                }
            }

            public void HolsterWeapon(BasePlayer player)
            {
                if (!AllowPVP || !Options.Holster || !player.svActiveItemID.IsValid || Instance.HasPVPDelay(player.userID))
                {
                    return;
                }
                player.equippingBlocked = true;
                player.UpdateActiveItem(default);
                player.Invoke(() =>
                {
                    player.equippingBlocked = false;
                }, 0.2f);
            }

            public void OnPlayerExit(BasePlayer target, bool skipDelay = true)
            {
                if (IsUnloading || target == null || !target.IsHuman())
                {
                    return;
                }

                Raider ri = GetRaider(target);

                ri.DestroyInput();
                UI.DestroyStatusUI(target);

                intruders.Remove(target.userID);

                if (ri.PreEnter)
                {
                    return;
                }

                ri.PreEnter = true;

                OnPlayerExited(target);

                TrySetPVPDelay(target, false, skipDelay);

                if (config.EventMessages.AnnounceEnterExit)
                {
                    QueueNotification(target, AllowPVP ? "OnPlayerExit" : "OnPlayerExitPVE");
                }
            }

            public void OnPlayerExited(BasePlayer target)
            {
                Interface.CallHook("OnPlayerExitedRaidableBase", new object[] { target, Location, AllowPVP, 512, ID, 0f, 0f, loadTime, ownerId, BaseName, spawnDateTime, despawnDateTime, ProtectionRadius, GetLootAmountRemaining() });
            }

            public void AddHintCooldown(BasePlayer target, float cooldown)
            {
                ulong userid = target.userID;
                HintCooldowns.Add(userid);
                Invoke(() =>
                {
                    if (IsDespawning) return;
                    HintCooldowns.Remove(userid);
                }, cooldown);
            }

            public bool CanSetPVPDelay(BasePlayer target)
            {
                return AllowPVP && config.Settings.Management.PVPDelayTrigger && target.userID.IsSteamId() && !InRange(target.transform.position, Location, ProtectionRadius);
            }

            public void TrySetPVPDelay(BasePlayer target, bool isFireDamage, bool skipDelay = true, string key = "DoomAndGloom")
            {
                if (config.Settings.Management.PVPDelay <= 0f || skipDelay || !Instance.IsPVE() || !AllowPVP || target.IsFlying || target.limitNetworking)
                {
                    return;
                }

                if (config.EventMessages.AnnounceEnterExit)
                {
                    string arg = mx(GetAllowKey(), target.UserIDString).Replace("[", string.Empty).Replace("] ", string.Empty);
                    QueueNotification(target, key, arg, config.Settings.Management.PVPDelay);
                }

                SetPVPDelay(target, isFireDamage);
            }

            public void ExpireAllDelays()
            {
                if (!config.Settings.Management.PVPDelayPersists && Instance.PvpDelay.Count > 0)
                {
                    using var tmp = Instance.PvpDelay.ToPooledList();
                    foreach (var (userid, ds) in tmp)
                    {
                        if (ds == null || ds.raid == null || ds.raid == this)
                        {
                            Instance.RemovePVPDelay(userid, ds);
                        }
                    }
                }
            }

            private object[] GetDelayHookObjects(BasePlayer target) => new object[] { target, 512, Location, AllowPVP, ID, 0f, 0f, loadTime, ownerId, BaseName, spawnDateTime, despawnDateTime, GetLootAmountRemaining() };

            public void SetPVPDelay(BasePlayer target, bool isFireDamage)
            {
                if (IsDespawning)
                {
                    return;
                }

                ulong userid = target.userID;
                if (Instance.GetPVPDelay(userid, false, out DelaySettings ds))
                {
                    float currentDealtDamageTime = Time.time;
                    if (!isFireDamage || Time.time - target.lastDealtDamageTime >= 0.1f)
                    {
                        Interface.CallHook("OnPlayerPvpDelayReset", GetDelayHookObjects(target));
                        target.lastDealtDamageTime = currentDealtDamageTime;
                    }

                    ds.Timer.Reset();
                }
                else
                {
                    Instance.PvpDelay[userid] = ds = new();
                    ds.Timer = Instance.timer.Once(config.Settings.Management.PVPDelay, () =>
                    {
                        if (this == null || !config.UI.Delay.Enabled)
                        {
                            Instance.RemovePVPDelay(userid, ds);
                        }
                        Interface.CallHook("OnPlayerPvpDelayExpired", GetDelayHookObjects(target));
                    });
                    Interface.CallHook("OnPlayerPvpDelayStart", GetDelayHookObjects(target));
                }

                ds.raid = this;
                ds.time = Time.realtimeSinceStartupAsDouble + config.Settings.Management.PVPDelay;

                UI.UpdateDelayUI(Instance, target);
            }

            public string GetAllowKey()
            {
                return AllowPVP ? "PVPFlag" : "PVEFlag";
            }

            private bool IsScavenging(BasePlayer player)
            {
                if (IsOpened || !config.Settings.Management.EjectScavengers || !ownerId.IsSteamId() || CanBypass(player))
                {
                    return false;
                }

                return !Any(player.userID) && !IsAlly(player) && RemovePlayer(player, Location, ProtectionRadius, Type);
            }

            private bool RemoveFauxAdmin(BasePlayer player)
            {
                if (Instance.FauxAdmin != null && player.IsNetworked() && player.IsDeveloper && player.HasPermission("fauxadmin.allowed") && player.HasPermission("raidablebases.block.fauxadmin") && player.IsCheating())
                {
                    RemovePlayer(player, Location, ProtectionRadius, Type);
                    Message(player, "NoFauxAdmin");
                    return true;
                }

                return false;
            }

            private bool IsBanned(BasePlayer player)
            {
                if (player.HasPermission("raidablebases.banned"))
                {
                    Message(player, player.IsAdmin ? "BannedAdmin" : "Banned");
                    return true;
                }

                return false;
            }

            private bool Teleported(BasePlayer player)
            {
                if (!config.Settings.Management.AllowTeleport && !TeleportExceptions.Contains(player.userID) && player.IsConnected && !CanBypass(player) && NearFoundation(player.transform.position) && !IsMounted(player) && Interface.CallHook("OnBlockRaidableBasesTeleport", player, Location) == null)
                {
                    Message(player, "CannotTeleport");
                    return true;
                }

                return false;
            }

            public bool IsMounted(BasePlayer player, bool ignoreSiege = false)
            {
                BaseEntity m = player.GetMounted();
                if (m != null)
                {
                    return !ignoreSiege || !(m is BatteringRamSeat or BallistaGun or BaseSiegeWeapon);
                }
                BaseEntity parent = player.GetParentEntity();
                if (parent == null) return false;
                return parent is BaseMountable || IsCustomEntity(parent);
            }

            public bool IsMountable(BaseEntity entity)
            {
                if (entity is BaseMountable) return true;

                BaseEntity parent = entity.GetParentEntity();
                if (parent == null) return IsCustomEntity(entity);
                return parent is BaseMountable || IsCustomEntity(parent);
            }

            public bool BypassUseOwners()
            {
                if (Type == RaidableType.Manual)
                {
                    return AllowPVP ? config.Settings.Manual.BypassUseOwnersForPVP : config.Settings.Manual.BypassUseOwnersForPVE;
                }
                return AllowPVP ? config.Settings.Management.BypassUseOwnersForPVP : config.Settings.Management.BypassUseOwnersForPVE;
            }

            public bool IsHogging(BasePlayer player)
            {
                if (!player.IsNetworked() || CanBypass(player) || player.HasPermission("raidablebases.hoggingbypass"))
                {
                    return false;
                }

                foreach (var raid in Instance.Raids)
                {
                    if (raid.BypassUseOwners() || !config.Settings.Management.PreventHogging)
                    {
                        continue;
                    }
                    if (raid.IsOpened && raid.Location != Location && raid.Any(player.userID, false))
                    {
                        TryMessage(player, "HoggingFinishYourRaid", FormatGridReference(player, raid.Location));
                        return true;
                    }
                }

                if (!config.Settings.Management.IsBlocking() || player.HasPermission("raidablebases.blockbypass"))
                {
                    return false;
                }

                return IsAllyHogging(player);
            }

            public bool IsAllyHogging(BasePlayer player)
            {
                foreach (var raid in Instance.Raids)
                {
                    if (!raid.IsOpened || raid.Type == RaidableType.None || raid.BypassUseOwners() || raid.Location.Distance(Location) < 0.1f)
                    {
                        continue;
                    }
                    if (config.Settings.Management.PreventHogging && IsAllyHogging(player, raid))
                    {
                        TryMessage(player, "HoggingFinishYourRaid", FormatGridReference(player, raid.Location));
                        return true;
                    }
                }

                return false;
            }

            private bool IsAllyHogging(BasePlayer player, RaidableBase raid)
            {
                if (raid.BypassUseOwners() || CanBypass(player))
                {
                    return false;
                }

                foreach (var target in raid.GetIntruders().Where(x => x != player && !CanBypass(x)))
                {
                    if (config.Settings.Management.BlockTeams && raid.IsAlly(player.userID, target.userID, AlliedType.Team))
                    {
                        TryMessage(player, "HoggingFinishYourRaidTeam", target.displayName, FormatGridReference(player, raid.Location));
                        return true;
                    }

                    if (config.Settings.Management.BlockFriends && raid.IsAlly(player.userID, target.userID, AlliedType.Friend))
                    {
                        TryMessage(player, "HoggingFinishYourRaidFriend", target.displayName, FormatGridReference(player, raid.Location));
                        return true;
                    }

                    if (config.Settings.Management.BlockClans && raid.IsAlly(player.userID, target.userID, AlliedType.Clan, "IsClanMember"))
                    {
                        TryMessage(player, "HoggingFinishYourRaidClan", target.displayName, FormatGridReference(player, raid.Location));
                        return true;
                    }
                }

                return false;
            }

            private void CheckBackpacks(bool bypass = false)
            {
                for (int i = backpacks.Count - 1; i >= 0; i--)
                {
                    var backpack = backpacks[i];

                    EjectBackpack(backpack, bypass);

                    if (backpack.IsEmpty)
                    {
                        backpacks.Remove(backpack);
                        ResetToPool(ref backpack);
                    }
                }
            }

            private float RadiationProtection(BasePlayer player)
            {
                float protection = Mathf.Ceil(player.RadiationProtection());

                if (player.modifiers == null)
                {
                    return protection;
                }

                return protection + (protection * Mathf.Clamp01(player.modifiers.GetValue(Modifier.ModifierType.Radiation_Exposure_Resistance)));
            }

            private bool IsNullOrVoid(BaseEntity entity) => entity.IsNull();

            public bool InRangeTolerance(Vector3 v, float t = 1f) => (v.XZ2D() - Location.XZ2D()).sqrMagnitude <= ProtectionRadiusSqr(t);
            public bool InRangeTolerance(Raider ri, float t = 20f) => InRangeTolerance(ri.player.transform.position, t);

            private bool requiredLootPercentageMet;

            private void Protector()
            {
                if (IsDespawning)
                {
                    return;
                }

                if (!requiredLootPercentageMet && IsCompleted && IsEligible && RequiredLootPercentageMet(Options.RequiredLootPercentage, out _))
                {
                    requiredLootPercentageMet = true;
                    HandleAwards();
                }

                if (DateTime.Now >= despawnDateTime)
                {
                    Despawn();
                    return;
                }

                if (despawnTimeUpdated) OnRaidableDespawnUpdate();
                if (enteredEntities.Count > 0) enteredEntities.RemoveWhere(IsNullOrVoid);
                if (backpacks.Count > 0) CheckBackpacks(!AllowPVP && Options.EjectBackpacksPVE);
                if (Options.RespawnRateMax > 0.1f) CheckNpcRespawns();
                if (raidWindowPrivs && privs.Count > 0) RefreshRaidWindowPrivileges();

                if (Type == RaidableType.None || intruders.Count == 0)
                {
                    return;
                }

                using var tmp = raiders.Values.ToPooledList();

                foreach (var ri in tmp)
                {
                    if (!intruders.Contains(ri.userid))
                    {
                        continue;
                    }

                    if (!ri.player.IsOnline())
                    {
                        intruders.Remove(ri.userid);
                        continue;
                    }

                    if (!InRangeTolerance(ri))
                    {
                        HandlePlayerExiting(ri.player);
                        continue;
                    }

                    if (RemoveFauxAdmin(ri.player))
                    {
                        continue;
                    }

                    if (IsBanned(ri.player))
                    {
                        RejectPlayer(ri);
                        continue;
                    }

                    if (Options.Mounts.Jetpacks && IsWearingJetpack(ri.player))
                    {
                        RemovePlayer(ri.player, Location, ProtectionRadius, Type, true);
                        continue;
                    }

                    if (ri.IsAllowed || ri.userid == ownerId || CanBypass(ri.player))
                    {
                        ri.IsAllowed = true;
                        continue;
                    }

                    if (CanEject(ri.player))
                    {
                        RejectPlayer(ri);
                        continue;
                    }

                    if (config.Settings.Management.LockToRaidOnEnter && !ri.IsParticipant)
                    {
                        QueueNotification(ri.player, "OnLockedToRaid");

                        ri.IsParticipant = true;
                    }

                    ri.IsAllowed = true;
                }
            }

            private void RejectPlayer(Raider ri)
            {
                ri.DestroyInput();
                raiders.Remove(ri.userid);
                intruders.Remove(ri.userid);
                UI.DestroyStatusUI(ri.player);
                RemovePlayer(ri.player, Location, ProtectionRadius, Type);
            }

            public void AddMember(ulong userid)
            {
                alliance.Add(userid);
            }

            public void FinalizeUi()
            {
                if (!raiders.IsNullOrEmpty())
                {
                    raiders.Values.ForEach(ri =>
                    {
                        if (ri.player.IsOnline())
                        {
                            if (intruders.Contains(ri.userid))
                            {
                                UI.DestroyStatusUI(ri.player);
                            }
                        }
                    });
                }
            }

            public void StopSetupCoroutine()
            {
                if (setupRoutine != null)
                {
                    StopCoroutine(setupRoutine);
                    setupRoutine = null;
                }
                if (turretsCoroutine != null)
                {
                    StopCoroutine(turretsCoroutine);
                    turretsCoroutine = null;
                }
            }

            public void Despawn()
            {
                if (!IsDespawning)
                {
                    IsDespawning = true;
                    IsOpened = false;
                    TryInvokeMethod(SetNoDrops);
                    TryInvokeMethod(RemoveAllFromEvent);
                    TryInvokeMethod(StopSetupCoroutine);
                    TryInvokeMethod(FinalizeUi);
                    TryInvokeMethod(DestroyLocks);
                    TryInvokeMethod(DestroyNpcs);
                    TryInvokeMethod(DestroyInputs);
                    TryInvokeMethod(DestroySpheres);
                    TryInvokeMethod(DestroyMapMarkers);
                    TryInvokeMethod(ResetSleepingBags);
                    TryInvokeMethod(ExpireAllDelays);
                    TryInvokeMethod(DestroyEntities);
                    TryInvokeMethod(DestroyElevators);
                    TryInvokeMethod(CheckSubscribe);
                    TryInvokeMethod(RespawnEntities);
                    TryInvokeMethod(FreeToPool);
                    Destroy(go);
                    LogEvent();
                    CancellDrone(rb);
                }
            }

            public void LogEvent()
            {
                TryInvokeMethod(() => Instance.LogToFile("despawn", $"{BaseName} {ownerName ?? "N/A"} ({ownerId}) @ approx. {Instance.PositionToGrid(Location)} {Location} {Type}", Instance, true, true));
            }

            public static void TryInvokeMethod(Action action)
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    Puts("{0} ERROR: {1}", action.Method.Name, ex);
                }
            }

            public void RemoveAllFromEvent()
            {
                Interface.CallHook("OnRaidableBaseDespawn", hookObjects);

                GetIntruders().ForEach(HandlePlayerExiting);
            }

            public void SendDronePatrol(RandomBase rb)
            {
                if (Options.DronePatrols.UseDronePatrol && Instance.IQDronePatrol != null && rb != null && rb.Position != default)
                {
                    Instance.IQDronePatrol?.Call("SendPatrolPoint", JsonConvert.SerializeObject(new CustomPatrol()
                    {
                        pluginName = Name,
                        position = rb.Position,
                        settingDrone = new()
                        {
                            droneCountSpawned = Options.DronePatrols.droneCountSpawned,
                            droneAttackedCount = Options.DronePatrols.droneAttackedCount,
                            keyDrones = Options.DronePatrols.keyDrones,
                        },
                        settingPosition = new()
                        {
                            countSpawnPoint = 200,
                            radiusFindedPoints = 50
                        },
                    }), false);
                }
            }

            private void CancellDrone(RandomBase rb)
            {
                if (Instance.IQDronePatrol != null && Options.DronePatrols.UseDronePatrol && rb != null && rb.Position != default)
                    Instance.IQDronePatrol.Call("CancellPatrol", rb.Position);
            }

            public void CheckSubscribe()
            {
                Instance.Raids.Remove(this);

                if (Instance.Raids.Count == 0)
                {
                    if (IsUnloading)
                    {
                        Instance.UnsetStatics();
                    }
                    else
                    {
                        Instance.UnsubscribeHooks();
                        if (Instance.IsScheduledReload && Instance.Queues.Paused)
                        {
                            Puts("Scheduled reload completed");
                            Interface.Oxide.NextTick(() => Interface.Oxide.ReloadPlugin(Name));
                            return;
                        }
                    }
                }

                if (!IsUnloading)
                {
                    if (!IsShuttingDown && Entities.Count > 0)
                    {
                        float estimate = (Entities.Count / (float)_undoLimit * 0.1f) + 15f;
                        if (AddNearTime < estimate)
                        {
                            AddNearTime = estimate;
                        }
                    }
                    spawns?.AddNear(Location, RemoveNearDistance, CacheType.Generic, CacheType.Generic2, AddNearTime);
                }
            }

            public void DestroyElevators()
            {
                if (Elevators.Count == 0)
                {
                    return;
                }
                TryInvokeMethod(RemoveParentFromEntitiesOnElevators);
                foreach (var (key, ele) in Elevators)
                {
                    if (ele.IsBMG())
                    {
                        ele.BMG._elevator.SafelyKill();
                    }
                }
                Elevators.Clear();
                if (IsUnloading || Instance == null || Instance.Manager == null)
                {
                    return;
                }
                foreach (var raid in Instance.Raids)
                {
                    if (raid.Elevators?.Count > 0)
                    {
                        return;
                    }
                }
                Instance.Unsubscribe(nameof(OnElevatorMove));
                Instance.Unsubscribe(nameof(OnElevatorCall));
                Instance.Unsubscribe(nameof(OnButtonPress));
                Instance.Unsubscribe(nameof(OnElevatorButtonPress));
            }

            public void DestroyEntities()
            {
                if (!IsShuttingDown && !IsUnloading)
                {
                    if (Entities.Count > 0)
                    {
                        Entities.RemoveWhere(DespawnExceptions.Contains);

                        List<BaseEntity> filtered = new(Entities);
                        Instance.PrepareUndoEntities(filtered);
                        List<Bounds> bounds = HasRustNavigationBuild ? new() { RustNavigationBounds } : new();
                        Instance.UndoLoop(filtered, bounds, _undoLimit, hookObjects);
                    }

                    SetPreventLooting();
                }
            }

            public void SetPreventLooting()
            {
                if (Options.PreventLooting <= 0f) return;
                ulong userid = ownerId;
                if (userid == 0uL)
                {
                    var owner = GetOwner();
                    if (owner == null) return;
                    userid = owner.userID;
                }
                foreach (var e in DespawnExceptions)
                {
                    if (e.IsKilled() || e.OwnerID != 0uL) continue;
                    if (e.ShortPrefabName != "item_drop") continue;
                    e.Invoke(() =>
                    {
                        e.OwnerID = 0uL;
                        e.skinID = 0uL;
                    }, Options.PreventLooting);
                    e.skinID = RB_SKIN_ID;
                    e.OwnerID = userid;
                }
            }

            public void OnBuildingPrivilegeDestroyed()
            {
                Interface.CallHook("OnRaidableBasePrivilegeDestroyed", hookObjects);
                IsPrivDestroyed = true;
                TryToEnd();
            }

            public bool IsOwnerConnected() => ownerId.IsSteamId() && RustCore.FindPlayerById(ownerId).IsOnline();

            public BasePlayer GetOwner()
            {
                if (ownerId.IsSteamId() && RustCore.FindPlayerById(ownerId) is BasePlayer player)
                {
                    return player;
                }
                BasePlayer owner = null;
                foreach (var x in raiders.Values)
                {
                    if (x.player.IsNull()) continue;
                    if (x.player.userID == ownerId) return x.player;
                    if (x.IsParticipant) owner = x.player;
                }
                return owner;
            }

            private List<BasePlayer> _intruders = new();

            public List<BasePlayer> GetIntruders()
            {
                _intruders.Clear();
                foreach (var raider in raiders.Values)
                {
                    if (intruders.Contains(raider.userid) && raider.player != null)
                    {
                        _intruders.Add(raider.player);
                    }
                }
                return _intruders;
            }

            private List<BasePlayer> _raiders = new();

            public List<BasePlayer> GetRaiders(bool participantOnly = true)
            {
                _raiders.Clear();
                foreach (var raider in raiders.Values)
                {
                    if (raider.player != null && (!participantOnly || raider.IsParticipant))
                    {
                        _raiders.Add(raider.player);
                    }
                }
                return _raiders;
            }

            public bool AddLooter(BasePlayer looter, HitInfo info = null)
            {
                if (!looter.IsHuman())
                {
                    return false;
                }

                if (looter.IsFlying || looter.limitNetworking)
                {
                    return false;
                }

                if (!IsAlly(looter))
                {
                    if (info != null)
                    {
                        NullifyDamage(info);
                        if (!info.damageTypes.Has(DamageType.Heat))
                        {
                            TryMessage(looter, "NoDamageToEnemyBase");
                        }
                    }
                    else
                    {
                        Message(looter, "OwnerLocked");
                    }
                    return false;
                }

                if (IsHogging(looter))
                {
                    return NullifyDamage(info);
                }

                GetRaider(looter).IsParticipant = true;

                return true;
            }

            public bool IsDamageBlocked(BaseEntity entity)
            {
                return false;
            }

            public bool IsPickupAllowed(string name)
            {
                foreach (var value in Options.WhitelistedPickupItems)
                {
                    if (!string.IsNullOrWhiteSpace(value) && name.Contains(value, CompareOptions.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            public bool IsPickupBlacklisted(string name)
            {
                foreach (var value in Options.BlacklistedPickupItems)
                {
                    if (!string.IsNullOrWhiteSpace(value) && name.Contains(value, CompareOptions.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            private void FillAmmoTurret(TurretInfo info)
            {
                if (isAuthorized || IsUnloading || IsDespawning || Type == RaidableType.None || IsContainerKilled(info) || info.infiniteAmmoDisabled)
                {
                    return;
                }

                foreach (ulong userid in info.turret.authorizedPlayers)
                {
                    if (userid.IsSteamId() && !CanBypassAuthorized(userid))
                    {
                        info.infiniteAmmoDisabled = true;
                        isAuthorized = true;
                        return;
                    }
                }

                DisableInterference(info);

                BaseProjectile attachedWeapon = info.turret.GetAttachedWeapon();
                if (attachedWeapon == null)
                {
                    if (++info.refillAttempts <= 20)
                    {
                        QueueTurretRefill(info, 0.2f);
                    }
                    return;
                }

                ItemDefinition ammoType = attachedWeapon.primaryMagazine.ammoType;
                if (ammoType == null)
                {
                    return;
                }

                int amount = Math.Max(config.Weapons.Ammo.AutoTurret, attachedWeapon.primaryMagazine.capacity);
                Item ammo = ItemManager.Create(ammoType, amount, 0uL);
                if (ammo == null)
                {
                    return;
                }

                if (!ammo.MoveToContainer(info.turret.inventory, -1, true, true, null, true))
                {
                    ammo.Remove();
                    return;
                }

                info.refillAttempts = 0;
                attachedWeapon.primaryMagazine.contents = attachedWeapon.primaryMagazine.capacity;
                attachedWeapon.SendNetworkUpdateImmediate();
                info.turret.Invoke(() => FinalizeTurretAmmo(info), 0.25f);
            }

            private void QueueTurretRefill(TurretInfo info, float delay)
            {
                if (IsContainerKilled(info) || info.refillPending || info.infiniteAmmoDisabled)
                {
                    return;
                }

                info.refillPending = true;
                info.turret.Invoke(() =>
                {
                    info.refillPending = false;
                    FillAmmoTurret(info);
                }, delay);
            }

            private void FinalizeTurretAmmo(TurretInfo info)
            {
                if (IsUnloading || IsDespawning || IsContainerKilled(info))
                {
                    return;
                }

                info.turret.UpdateTotalAmmo();
            }

            private static void DisableInterference(TurretInfo info)
            {
                if (!IsContainerKilled(info) && info.turret.HasFlag(BaseEntity.Flags.OnFire))
                {
                    info.turret.SetFlagLocal(BaseEntity.Flags.OnFire, false);
                    info.turret.nearbyTurrets.Clear();
                    info.turret.interferringTurrets.Clear();
                    info.turret.SendNetworkUpdate();
                }
            }

            private void FillAmmoGunTrap(GunTrap gt)
            {
                if (IsUnloading || isAuthorized || IsContainerKilled(gt))
                {
                    return;
                }

                gt.ammoType ??= ItemManager.FindItemDefinition("ammo.handmade.shell");

                var ammo = gt.inventory.GetSlot(0);

                if (ammo == null)
                {
                    gt.inventory.AddItem(gt.ammoType, config.Weapons.Ammo.GunTrap);
                }
                else ammo.amount = config.Weapons.Ammo.GunTrap;
            }

            private ItemDefinition lowgradefuel;

            private void FillAmmoFogMachine(FogMachine fog)
            {
                if (IsUnloading || isAuthorized || IsContainerKilled(fog))
                {
                    return;
                }

                lowgradefuel ??= ItemManager.FindItemDefinition("lowgradefuel");

                Item slot = fog.inventory.GetSlot(0);
                if (slot == null)
                {
                    fog.inventory.AddItem(lowgradefuel, config.Weapons.Ammo.FogMachine);
                }
                else slot.amount = config.Weapons.Ammo.FogMachine;
            }

            private void FillAmmoFlameTurret(FlameTurret ft)
            {
                if (IsUnloading || isAuthorized || IsContainerKilled(ft))
                {
                    return;
                }

                lowgradefuel ??= ItemManager.FindItemDefinition("lowgradefuel");

                Item slot = ft.inventory.GetSlot(0);
                if (slot == null)
                {
                    ft.inventory.AddItem(lowgradefuel, config.Weapons.Ammo.FlameTurret);
                }
                else slot.amount = config.Weapons.Ammo.FlameTurret;
            }

            private void FillAmmoSamSite(SamSite ss)
            {
                if (IsUnloading || isAuthorized || IsContainerKilled(ss))
                {
                    return;
                }

                if (ss.ammoItem == null || !ss.HasAmmo())
                {
                    Item item = ItemManager.Create(ss.ammoType, config.Weapons.Ammo.SamSite);

                    if (!item.MoveToContainer(ss.inventory))
                    {
                        item.Remove();
                    }
                    else ss.ammoItem = item;
                }
                else if (ss.ammoItem.amount < config.Weapons.Ammo.SamSite)
                {
                    ss.ammoItem.amount = config.Weapons.Ammo.SamSite;
                }
            }

            private bool IsAuthorized()
            {
                foreach (var id in priv.authorizedPlayers)
                {
                    if (id.IsSteamId() && !CanBypassAuthorized(id))
                    {
                        return true;
                    }
                }
                return false;
            }

            private void OnWeaponItemPreRemove(Item item)
            {
                if (isAuthorized || IsUnloading || IsDespawning)
                {
                    return;
                }
                else if (!IsContainerKilled(priv) && IsAuthorized())
                {
                    isAuthorized = true;
                    return;
                }
                else if (privSpawned && IsContainerKilled(priv))
                {
                    isAuthorized = true;
                    return;
                }

                var weapon = item.parent?.entityOwner;

                if (weapon is AutoTurret turret)
                {
                    if (turret.net != null && turrets.TryGetValue(turret.net.ID.Value, out var info))
                    {
                        QueueTurretRefill(info, 0.1f);
                    }
                }
                else if (weapon is GunTrap gt)
                {
                    weapon.Invoke(() => FillAmmoGunTrap(gt), 0.1f);
                }
                else if (weapon is SamSite ss)
                {
                    weapon.Invoke(() => FillAmmoSamSite(ss), 0.1f);
                }
            }

            public void TryToEnd()
            {
                if (IsOpened && !IsLoading && !IsCompleted && CanUndo())
                {
                    if (Options.DropPrivilegeLoot && privHadLoot && !IsContainerKilled(priv))
                    {
                        Instance.DropOrRemoveItems(priv, this, true, true);
                    }
                    UnlockEverything();
                    AwardRaiders();
                    Undo();
                }
            }

            private void UnlockEverything()
            {
                if (Options.UnlockEverything)
                {
                    DestroyLocks();
                }
            }

            public bool GetInitiatorPlayer(HitInfo info, DamageType damageType, BaseCombatEntity entity, out BasePlayer target)
            {
                if (info == null)
                {
                    target = entity.lastAttacker as BasePlayer;
                    return target != null;
                }

                var weapon = info.Initiator ?? info.WeaponPrefab ?? info.Weapon;

                target = weapon switch
                {
                    BasePlayer player => player,
                    { creatorEntity: BasePlayer player } => player,
                    { parentEntity: EntityRef parentEntity } when parentEntity.Get(true) is BasePlayer player => player,
                    _ => damageType == DamageType.Heat ? entity.lastAttacker as BasePlayer ?? GetArsonist() : null
                };

                return target != null;
            }

            private List<string> fireAmmoTypes = new() { "arrow.fire", "ammo.pistol.fire", "ammo.rifle.explosive", "ammo.rifle.incendiary", "ammo.shotgun.fire" };

            public BasePlayer GetArsonist()
            {
                foreach (var raider in raiders.Values)
                {
                    if (raider.player == null || !raider.IsParticipant)
                    {
                        continue;
                    }
                    if (!raider.player.svActiveItemID.IsValid || !(raider.player.GetActiveItem() is Item item) || !(item.GetHeldEntity() is BaseEntity e))
                    {
                        continue;
                    }
                    if (e is FlameThrower || (e is BaseProjectile projectile && projectile.primaryMagazine.ammoType != null && fireAmmoTypes.Contains(projectile.primaryMagazine.ammoType.shortname)))
                    {
                        return raider.player;
                    }
                }
                return null;
            }

            public void SetAllowPVP(RandomBase rb)
            {
                Type = rb.type;

                AllowPVP = Type switch
                {
                    RaidableType.Maintained when config.Settings.Maintained.Chance > 0 => Convert.ToDecimal(UnityEngine.Random.Range(0f, 100f)) <= config.Settings.Maintained.Chance,
                    RaidableType.Scheduled when config.Settings.Schedule.Chance > 0 => Convert.ToDecimal(UnityEngine.Random.Range(0f, 100f)) <= config.Settings.Schedule.Chance,
                    RaidableType.Maintained when config.Settings.Maintained.ConvertPVP => false,
                    RaidableType.Maintained when config.Settings.Maintained.ConvertPVE => true,
                    RaidableType.Scheduled when config.Settings.Schedule.ConvertPVP => false,
                    RaidableType.Scheduled when config.Settings.Schedule.ConvertPVE => true,
                    RaidableType.Manual when config.Settings.Manual.ConvertPVP => false,
                    RaidableType.Manual when config.Settings.Manual.ConvertPVE => true,
                    _ => rb.options.AllowPVP
                };
            }

            private bool CancelOnServerRestart()
            {
                return config.Settings.Management.Restart && IsShuttingDown;
            }

            private List<ulong> _timeRequirement = new();
            public List<ulong> GetRaidersByTimeRequirement(float threshold = 0.25f)
            {
                _timeRequirement.Clear();
                float maxParticipantTime = 0f;
                foreach (var raider in raiders.Values)
                {
                    if (raider.IsParticipant && raider.participantTime > maxParticipantTime)
                    {
                        maxParticipantTime = raider.participantTime;
                    }
                }
                if (maxParticipantTime > 0f)
                {
                    float requiredDuration = maxParticipantTime * threshold;
                    foreach (var raider in raiders.Values)
                    {
                        if (raider.IsParticipant && raider.participantTime >= requiredDuration)
                        {
                            _timeRequirement.Add(raider.userid);
                        }
                    }
                }
                return _timeRequirement;
            }

            public void AwardRaiders()
            {
                if (raiders.TryGetValue(ownerId, out Raider ownerRi) && ownerRi.IsParticipant && ownerRi.eligible && ownerRi.rewards && !CancelOnServerRestart())
                {
                    Interface.CallHook("OnRaidableAwardOwner", hookObjects);
                    Interface.CallHook("OnRaidableAwardOwner", Location, 512, AllowPVP, ownerId, GetRaidersByTimeRequirement());
                }

                var sb = new StringBuilder();

                foreach (var ri in raiders.Values)
                {
                    if (CancelOnServerRestart() || !IsEligible)
                    {
                        ri.eligible = false;
                        continue;
                    }

                    if (ri.player != null && ri.player.IsFlying)
                    {
                        if (config.EventMessages.Rewards.Flying) Message(ri.player, "No Reward: Flying");
                        ri.eligible = false;
                        continue;
                    }

                    if (ri.player != null && ri.player._limitedNetworking)
                    {
                        if (config.EventMessages.Rewards.Vanished) Message(ri.player, "No Reward: Vanished");
                        ri.eligible = false;
                        continue;
                    }

                    if (!IsPlayerActive(ri.userid))
                    {
                        if (config.EventMessages.Rewards.Inactive) Message(ri.player, "No Reward: Inactive");
                        ri.eligible = false;
                        continue;
                    }

                    if (config.Settings.Management.OnlyAwardOwner && ri.userid != ownerId && ownerId.IsSteamId())
                    {
                        if (config.EventMessages.Rewards.NotOwner) Message(ri.player, "No Reward: Not Owner");
                        ri.rewards = false;
                    }

                    if (!ri.IsParticipant || Options.RequiredDestroyEntity && !ri.HasDestroyed)
                    {
                        if (config.EventMessages.Rewards.NotParticipant) Message(ri.player, "No Reward: Not A Participant");
                        ri.rewards = false;
                        continue;
                    }

                    if (config.Settings.Management.OnlyAwardAllies && ownerId.IsSteamId() && ri.userid != ownerId && !IsAlly(ri.userid, ownerId))
                    {
                        if (config.EventMessages.Rewards.NotAlly) Message(ri.player, "No Reward: Not Ally");
                        ri.rewards = false;
                    }

                    if (config.Settings.RemoveAdminRaiders && ri.player != null && ri.player.IsAdmin && Type != RaidableType.None)
                    {
                        if (config.EventMessages.Rewards.RemoveAdmin) Message(ri.player, "No Reward: Admin");
                        ri.rewards = false;
                        continue;
                    }

                    sb.Append(ri.displayName).Append(", ");
                }

                if (IsEligible)
                {
                    if (!CancelOnServerRestart())
                    {
                        Interface.CallHook("OnRaidableBaseCompleted", hookObjects);
                    }

                    if (!IsUnloading && Options.Levels.Level2 && npcMaxAmountMurderers + npcMaxAmountScientists > 0)
                    {
                        SpawnNpcs();
                    }

                    if (!requiredLootPercentageMet && IsCompleted && RequiredLootPercentageMet(Options.RequiredLootPercentage, out _))
                    {
                        requiredLootPercentageMet = true;
                        HandleAwards();
                    }
                }

                if (sb.Length == 0)
                {
                    return;
                }

                sb.Length -= 2;
                string thieves = sb.ToString();
                string con = mx(IsEligible ? "Thieves" : "ThievesDespawn", null, $"{LangMode()} ({BaseName})", Instance.PositionToGrid(Location), thieves);

                Puts(con);

                if (config.EventMessages.AnnounceThief && IsEligible)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        if (!IsRaider(target) && target.HasPermission("raidablebases.limitedannouncements")) continue;
                        QueueNotification(target, "Thieves", LangMode(target.UserIDString), FormatGridReference(target, Location), thieves);
                    }
                }

                if (config.EventMessages.LogThieves)
                {
                    Instance.LogToFile("treasurehunters", $"{DateTime.Now} : {con}", Instance, false);
                }
            }

            public bool RequiredLootPercentageMet(double requiredLootPercentage, out double percentageMet)
            {
                percentageMet = 0;
                if (requiredLootPercentage > 0 && numLootRequired > 0)
                {
                    int lootAmountRemaining = GetLootAmountRemaining();
                    if (lootAmountRemaining > 0)
                    {
                        double numLooted = numLootRequired - lootAmountRemaining;
                        percentageMet = (numLooted / numLootRequired) * 100.0;
                        if (percentageMet <= requiredLootPercentage)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            private void HandleAwards()
            {
                foreach (var ri in raiders.Values)
                {
                    if (!ri.IsParticipant || !ri.eligible)
                    {
                        continue;
                    }

                    if (config.RankedLadder.Enabled)
                    {
                        PlayerInfo info = data.GetPlayerInfo(ri.id);

                        info.ResetExpiredDate(config.RankedLadder.Days);

                        info.Name = ri.displayName.ToFriendlyJson();
                        info.TotalRaids++;
                        info.Raids++;

                        Interface.CallHook("OnRaidableAwardGiven", ri.displayName, ri.id, JsonConvert.SerializeObject(info));
                    }

                    if (!ri.rewards)
                    {
                        continue;
                    }

                    int total = raiders.Values.ToList().Sum(x => x.IsParticipant ? 1 : 0);

                    if (total == 0)
                        continue;

                    if (Options.Rewards.Money > 0 && Instance.Economics.CanCall())
                    {
                        double money = config.Settings.Management.DivideRewards ? Options.Rewards.Money / (double)total : Options.Rewards.Money;
                        if (Options.Rewards.IsDoubledAtNighttime()) money *= 2;
                        Instance.Economics?.Call("Deposit", ri.userid, money);
                        QueueNotification(ri.player, "EconomicsDeposit", money);
                    }

                    if (Options.Rewards.Money > 0 && Instance.BankSystem.CanCall())
                    {
                        int money = Convert.ToInt32(config.Settings.Management.DivideRewards ? Options.Rewards.Money / total : Options.Rewards.Money);
                        if (Options.Rewards.IsDoubledAtNighttime()) money *= 2;
                        Instance.BankSystem?.Call("Deposit", ri.id, money);
                        QueueNotification(ri.player, "EconomicsDeposit", money);
                    }

                    if (Options.Rewards.Money > 0 && Instance.IQEconomic.CanCall())
                    {
                        int money = Convert.ToInt32(config.Settings.Management.DivideRewards ? Options.Rewards.Money / total : Options.Rewards.Money);
                        if (Options.Rewards.IsDoubledAtNighttime()) money *= 2;
                        Instance.IQEconomic?.Call("API_SET_BALANCE", ri.userid, money);
                        QueueNotification(ri.player, "EconomicsDeposit", money);
                    }

                    if (Options.Rewards.Points > 0 && Instance.ServerRewards.CanCall())
                    {
                        int points = config.Settings.Management.DivideRewards ? Options.Rewards.Points / total : Options.Rewards.Points;
                        if (Options.Rewards.IsDoubledAtNighttime()) points *= 2;
                        Instance.ServerRewards?.Call("AddPoints", ri.userid, points);
                        QueueNotification(ri.player, "ServerRewardPoints", points);
                    }

                    if (Options.Rewards.SkillTree > 0 && Instance.SkillTree.CanCall())
                    {
                        double xp = config.Settings.Management.DivideRewards ? Options.Rewards.SkillTree / (double)total : Options.Rewards.SkillTree;
                        if (Options.Rewards.IsDoubledAtNighttime()) xp *= 2;
                        QueueNotification(ri.player, "SkillTreeXP", xp);
                        if (ri.player != null) Instance.SkillTree?.Call("AwardXP", ri.player, xp, Name);
                        else Instance.SkillTree?.Call("AwardXP", ri.userid, xp, Name);
                    }

                    if (Options.Rewards.XPerience > 0 && Instance.XPerience.CanCall())
                    {
                        double xp = config.Settings.Management.DivideRewards ? Options.Rewards.XPerience / (double)total : Options.Rewards.XPerience;
                        if (Options.Rewards.IsDoubledAtNighttime()) xp *= 2;
                        QueueNotification(ri.player, "SkillTreeXP", xp);
                        Instance.XPerience?.Call("GiveXPID", ri.userid, xp);
                    }

                    if (Options.Rewards.XLevels > 0 && ri.player != null && Instance.XLevels.CanCall())
                    {
                        double xp = config.Settings.Management.DivideRewards ? Options.Rewards.XLevels / (double)total : Options.Rewards.XLevels;
                        if (Options.Rewards.IsDoubledAtNighttime()) xp *= 2;
                        QueueNotification(ri.player, "SkillTreeXP", xp);
                        Instance.XLevels?.Call("API_GiveXP", ri.player, (float)xp);
                    }
                }
            }

            private void AddGroupedPermission(string userid, string group, string perm)
            {
                if (userid.HasPermission("raidablebases.notitle"))
                {
                    return;
                }

                if (!userid.HasPermission(perm))
                {
                    Instance.permission.GrantUserPermission(userid, perm, Instance);
                }

                if (!Instance.permission.UserHasGroup(userid, group))
                {
                    Instance.permission.AddUserGroup(userid, group);
                }
            }

            private bool CanAssignTo(ulong userid, ulong owner, bool only)
            {
                return only == false || owner == 0uL || userid == owner;
            }

            public bool CanBypass(BasePlayer player)
            {
                return !player.IsHuman() || player.IsFlying || player.limitNetworking || player.HasPermission("raidablebases.canbypass");
            }

            private bool Exceeds(BasePlayer player)
            {
                if (player.userID == ownerId || CanBypass(player) || config.Settings.Management.Players.BypassPVP && AllowPVP)
                {
                    return false;
                }

                int amount = config.Settings.Management.Players.Get(Type);

                if (amount == -1 || amount > 0 && GetParticipantsAmount() > amount)
                {
                    Message(player, "Event is full");
                    return true;
                }

                return false;
            }

            public int GetParticipantsAmount()
            {
                return raiders.Values.Count(x => x.player != null && !CanBypass(x.player));
            }

            public string LangMode(string userid = null, bool strip = false)
            {
                string text = mx("Normal", userid);
                return strip ? rf(text) : text;
            }

            public string Mode(string userid = null, bool forceShowName = false)
            {
                var difficultyMode = mx("Normal");

                if (difficultyMode == "normal")
                {
                    difficultyMode = "Normal";
                }

                if (ownerId.IsSteamId())
                {
                    return string.Format("{0} {1}", (config.Settings.Markers.ShowOwnersName || forceShowName) ? ownerName : mx("Claimed"), difficultyMode);
                }

                return difficultyMode;
            }

            private void SetOwnerInternal(string username, ulong userid)
            {
                if (config.Settings.Management.LockTime > 0f)
                {
                    if (IsInvoking(ResetPublicOwner))
                    {
                        CancelInvoke(ResetPublicOwner);
                    }
                    Invoke(ResetPublicOwner, config.Settings.Management.LockTime * 60f);
                }
                ownerId = userid;
                ownerName = username;
                UpdateMarker();
            }

            public void SetOwner(BasePlayer owner)
            {
                SetOwnerInternal(owner?.displayName, owner?.userID ?? 0);
                ResetRaiderRelations();
                Protector();
            }

            private double PlayerActivityTimeLeft(ulong userid)
            {
                if (config.Settings.Management.LockTime <= 0f)
                {
                    return double.PositiveInfinity;
                }

                if (!raiders.TryGetValue(userid, out var raider))
                {
                    return double.PositiveInfinity;
                }

                return (config.Settings.Management.LockTime * 60d) - (Time.realtimeSinceStartupAsDouble - raider.lastActiveTime);
            }

            public bool IsPlayerActive(ulong userid)
            {
                return PlayerActivityTimeLeft(userid) > 0f;
            }

            public void TrySetOwner(BasePlayer attacker, BaseEntity entity, HitInfo info)
            {
                if (!config.Settings.Management.UseOwners)
                {
                    return;
                }

                if (!IsOpened || BypassUseOwners() || ownerId.IsSteamId() || config.Settings.Management.PreventHogging && Instance.IsEventOwner(attacker, false))
                {
                    return;
                }

                if (IsHogging(attacker))
                {
                    NullifyDamage(info);
                    return;
                }

                if (entity is HumanoidNPC)
                {
                    SetOwner(attacker);
                    return;
                }

                if (!(entity is BuildingBlock or Door or SimpleBuildingBlock))
                {
                    return;
                }

                if (InRange2D(attacker.transform.position, Location, ProtectionRadius) || IsLootingWeapon(info))
                {
                    SetOwner(attacker);
                }
            }

            public void ResetRaiderRelations()
            {
                foreach (var ri in raiders.Values)
                {
                    if (ri.userid == ownerId)
                    {
                        continue;
                    }

                    ri.IsAllowed = false;
                    ri.IsAlly = false;
                }
            }

            public void ClearEnemies()
            {
                raiders.RemoveAll((uid, ri) => !IsAlly(ownerId, ri.userid));
            }

            public void CheckDespawn()
            {
                if (!IsOpened)
                {
                    if (DespawnMinutesReset)
                    {
                        UpdateDespawnDateTime(DespawnMinutes);
                    }
                    return;
                }

                if (IsDespawning || DespawnMinutesInactive <= 0f || !IsEngaged && EngageOnBaseDamage)
                {
                    return;
                }

                if (DespawnMinutesInactiveReset || despawnDateTime == DateTime.MaxValue)
                {
                    UpdateDespawnDateTime(DespawnMinutesInactive);
                }
            }

            private bool despawnTimeUpdated;
            public void UpdateDespawnDateTime(float time)
            {
                if (time > 0f)
                {
                    despawnDateTime = DateTime.Now.AddSeconds(time * 60f);
                }
                else
                {
                    despawnDateTime = DateTime.Now;
                }
                despawnTimeUpdated = true;
            }

            private void OnRaidableDespawnUpdate()
            {
                despawnTimeUpdated = false;
                Interface.CallHook("OnRaidableDespawnUpdate", new object[8] { Location, 512, AllowPVP, ownerId, BaseName, ProtectionRadius, GetLootAmountRemaining(), despawnDateTime });
            }

            public bool EndWhenCupboardIsDestroyed()
            {
                if (config.Settings.Management.EndWhenCupboardIsDestroyed && privSpawned)
                {
                    return IsCompleted = IsPrivDestroyed || IsContainerKilled(priv) || privHadLoot && priv.inventory.IsEmpty();
                }

                return false;
            }

            public bool CanUndo()
            {
                if (EndWhenCupboardIsDestroyed())
                {
                    return IsCompleted = true;
                }

                if (config.Settings.Management.RequireCupboardLooted && privHadLoot && !IsPrivDestroyed)
                {
                    if (!IsContainerKilled(priv) && !priv.inventory.IsEmpty())
                    {
                        return false;
                    }
                }

                foreach (var container in _containers)
                {
                    if (!IsContainerKilled(container) && !container.inventory.IsEmpty() && IsBox(container, true))
                    {
                        return false;
                    }
                }

                foreach (string value in config.Settings.Management.Inherit)
                {
                    foreach (var container in _allcontainers)
                    {
                        if (IsContainerKilled(container) || !container.ShortPrefabName.Contains(value, CompareOptions.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!container.inventory.IsEmpty())
                        {
                            return false;
                        }
                    }
                }

                return IsCompleted = true;
            }

            private bool CanPlayerBeLooted(ulong looter, ulong target)
            {
                return PlayersLootable || IsAlly(looter, target);
            }

            private bool CanBeLooted(BasePlayer player, BaseEntity e)
            {
                if (IsLoading)
                {
                    return CanBypassAuthorized(player.userID);
                }

                if (IsProtectedWeapon(e, true))
                {
                    if (config.Settings.Management.LootableTraps)
                    {
                        if (!CanBypassAuthorized(player.userID)) isAuthorized = true;

                        return true;
                    }

                    return false;
                }

                if (e is NPCPlayerCorpse)
                {
                    return true;
                }

                if (e is LootableCorpse corpse)
                {
                    if (CanBypass(player) || !corpse.playerSteamID.IsSteamId() || corpse.playerSteamID == player.userID || corpse.playerName == player.displayName)
                    {
                        return true;
                    }

                    return CanPlayerBeLooted(player.userID, corpse.playerSteamID);
                }
                else if (e is DroppedItemContainer container)
                {
                    if (CanBypass(player) || !container.playerSteamID.IsSteamId() || container.playerSteamID == player.userID || container.playerName == player.displayName)
                    {
                        return true;
                    }

                    return CanPlayerBeLooted(player.userID, container.playerSteamID);
                }

                return true;
            }

            public bool IsProtectedWeapon(BaseEntity e, bool checkBuiltList = false)
            {
                if (e.IsNull() || checkBuiltList && BuiltList.Contains(e))
                {
                    return false;
                }

                return IsWeapon(e);
            }

            public bool IsWeapon(BaseEntity e) => e is GunTrap || e is FlameTurret || e is FogMachine || e is SamSite || e is AutoTurret || e is TeslaCoil;

            public bool IsFoundation(BaseEntity e) => e.ShortPrefabName == "foundation.triangle" || e.ShortPrefabName == "foundation" || e.skinID == 1337424001 && e is CollectibleEntity;

            public bool IsCompound(BaseEntity e) => IsFoundation(e) || e.ShortPrefabName.Contains("floor") || e.ShortPrefabName.Contains("wall");

            public object CanLootEntityInternal(BasePlayer player, BaseEntity entity)
            {
                if (player == null || entity.OwnerID == player.userID || !entity.OwnerID.IsSteamId() && !Has(entity))
                {
                    return null;
                }

                //if (!player.limitNetworking && IsPickupBlacklisted(entity.ShortPrefabName))
                //{
                //    return true;
                //}

                if (entity.ShortPrefabName == "coffinstorage" && Mathf.Approximately(entity.transform.position.Distance(new(0f, -50f, 0f)), 0f))
                {
                    return null;
                }

                if (IsMountable(entity))
                {
                    return null;
                }

                if (!player.limitNetworking && !CanBeLooted(player, entity))
                {
                    return true;
                }

                if (entity is LootableCorpse || entity is DroppedItemContainer)
                {
                    return null;
                }

                if (player.GetMounted() != null)
                {
                    Message(player, "CannotBeMounted");
                    return true;
                }

                if (Options.RequiresCupboardAccess && !CanBuild(player))
                {
                    Message(player, "MustBeAuthorized");
                    return true;
                }

                if (Type != RaidableType.None)
                {
                    foreach (var ri in raiders.Values)
                    {
                        if (ri.IsParticipant)
                        {
                            CheckDespawn();
                            break;
                        }
                    }
                }

                if (player.IsFlying || player.limitNetworking || entity.OwnerID != 0)
                {
                    return null;
                }

                if (!AddLooter(player))
                {
                    return true;
                }

                AddMember(player.userID);

                return null;
            }

            public bool CanBuild(BasePlayer player)
            {
                if (privSpawned)
                {
                    return priv.IsKilled() || priv.IsAuthed(player);
                }
                return true;
            }

            public static void ClearInventory(ItemContainer container)
            {
                if (container == null || container.itemList == null)
                {
                    return;
                }
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    item.GetHeldEntity().SafelyKill();
                    item.RemoveFromContainer();
                    item.Remove(0f);
                }
            }

            public void SetNoDrops()
            {
                foreach (var container in _allcontainers)
                {
                    if (IsContainerKilled(container))
                    {
                        continue;
                    }
                    if (!IsShuttingDown && IsCompleted && Options != null && Options.DropPrivilegeLoot && container is BuildingPrivlidge)
                    {
                        Instance.DropOrRemoveItems(container, this, true, true);
                    }
                    else
                    {
                        container.dropsLoot = false;
                        ClearInventory(container.inventory);
                    }
                }

                if (Type != RaidableType.None)
                {
                    foreach (var info in turrets.Values)
                    {
                        if (!IsContainerKilled(info))
                        {
                            AutoTurret turret = info.turret;
                            ClearInventory(turret.inventory);
                            try { if (turret.IsInvoking(turret.UpdateAttachedWeapon)) turret.CancelInvoke(turret.UpdateAttachedWeapon); } catch { }
                        }
                    }
                }

                ItemManager.DoRemoves();
            }

            public void DestroyInputs()
            {
                raiders.Values.ForEach(ri => ri.DestroyInput());
            }

            public void Init(RandomBase rb, List<BaseEntity> entities = null)
            {
                rb.raid = this;
                this.rb = rb;
                spawns = rb.spawns;
                RemoveNearDistance = rb.spawns == null ? rb.options.ProtectionRadius(rb.type) : rb.spawns.RemoveNear(rb.Position, rb.options.ProtectionRadius(rb.type), CacheType.Generic, CacheType.Generic2, rb.type);

                data.Cycle.Add(rb.type, rb.BaseName, rb.owner);

                alliance.UnionWith(rb.members);

                if (!Options.Setup.BlockedPrefabs.IsNullOrEmpty())
                {
                    setupBlockedPrefabs.AddRange(Options.Setup.BlockedPrefabs);
                }

                Plugin CopyPaste = Instance.CopyPaste;
                if (Options.Elevators.BMGOnly || CopyPaste == null || CopyPaste.Version <= new VersionNumber(4, 2, 7))
                {
                    TryInvokeMethod(() =>
                    {
                        using var bmgs = BMGELEVATOR.FixElevators(this);
                        foreach (var bmg in bmgs)
                        {
                            Elevators[bmg.Key] = new() { BMG = bmg.Value, raid = this };
                        }
                    });
                }

                TryInvokeMethod(() => AddEntities(entities));

                Interface.Oxide.NextTick(() =>
                {
                    if (IsUnloading) return;

                    TryInvokeMethod(SetCenterFromMultiplePoints);
                    TryInvokeMethod(SetupElevators);

                    setupRoutine = ServerMgr.Instance.StartCoroutine(EntitySetup());
                });
            }

            private void SetupElevators()
            {
                foreach (var ele in Elevators.Values)
                {
                    if (ele.IsBMG())
                    {
                        ele.BMG.Init(this);
                    }
                }
            }

            private HashSet<ulong> _granted = new();

            public bool HasCardPermission(BasePlayer player)
            {
                var options = Options.Elevators;
                if (options.RequiredAccessLevel == 0 || _granted.Contains(player.userID) || player.HasPermission("raidablebases.elevators.bypass.card"))
                {
                    return true;
                }

                string shortname = options.RequiredAccessLevel == 1 ? "keycard_green" : options.RequiredAccessLevel == 2 ? "keycard_blue" : "keycard_red";
                Item item = player.inventory.FindItemByItemName(shortname) ?? player.GetActiveItem();

                if (item == null || item.skin != options.SkinID)
                {
                    Message(player, options.RequiredAccessLevel == 1 ? "Elevator Green Card" : options.RequiredAccessLevel == 2 ? "Elevator Blue Card" : options.RequiredAccessLevel == 3 ? "Elevator Red Card" : "Elevator Special Card");
                    return false;
                }

                if (item.GetHeldEntity() is Keycard keycard && keycard != null && keycard.accessLevel == options.RequiredAccessLevel)
                {
                    if (options.RequiredAccessLevelOnce)
                    {
                        _granted.Add(player.userID);
                    }

                    return true;
                }

                Message(player, options.RequiredAccessLevel == 1 ? "Elevator Green Card" : options.RequiredAccessLevel == 2 ? "Elevator Blue Card" : options.RequiredAccessLevel == 3 ? "Elevator Red Card" : "Elevator Special Card");
                return false;
            }

            public bool HasBuildingPermission(BasePlayer player)
            {
                if (!Options.Elevators.RequiresBuildingPermission || player.HasPermission("raidablebases.elevators.bypass.building") || priv.IsKilled() || priv.IsAuthed(player))
                {
                    return true;
                }

                Message(player, "Elevator Privileges");
                return false;
            }


            private List<string> setupBlockedPrefabs = new();

            private void AddEntities(List<BaseEntity> entities)
            {
                if (entities.IsNullOrEmpty())
                {
                    return;
                }
                foreach (var e in entities)
                {
                    if (e.IsKilled())
                    {
                        continue;
                    }
                    if (setupBlockedPrefabs.Exists(e.ShortPrefabName.Contains))
                    {
                        e.DelayedSafeKill();
                        continue;
                    }
                    Vector3 position = e.transform.position;
                    if (IsFoundation(e))
                    {
                        foundations.Add(position);
                    }
                    if (e.ShortPrefabName.StartsWith("floor"))
                    {
                        floors.Add(position);
                    }
                    if (IsCompound(e))
                    {
                        compound.Add(position);
                    }
                    e.OwnerID = 0;
                    AddEntity(e);
                }
            }

            private bool centerSetFromMultiplePoints, isEventUnderground;

            public void SetCenterFromMultiplePoints()
            {
                Vector3 vector = Location;

                if (compound.Count > 1)
                {
                    var bounds = new Bounds(compound[0], Vector3.zero);

                    for (int i = 1; i < compound.Count; i++)
                    {
                        bounds.Encapsulate(compound[i]);
                    }

                    vector = bounds.center;
                    vector.y = 0f;
                }

                if (Options.Setup.ForcedHeight == -1)
                {
                    vector.y = rb.isCustomSpawn ? Location.y : SpawnsController.GetSpawnHeight(vector);
                }
                else vector.y = Options.Setup.ForcedHeight;

                vector.y += BaseHeight + Options.Setup.PasteHeightAdjustment;

                Location = vector;
                LocationXZ3D = vector.XZ3D();

                go.transform.position = Location;

                centerSetFromMultiplePoints = true;
                isEventUnderground = EnvironmentManager.Check(Location, EnvironmentType.TrainTunnels | EnvironmentType.Underground);
            }

            private void CreateSpheres()
            {
                if (Options.SphereAmount <= 0 || Options.Silent)
                {
                    return;
                }

                for (int i = 0; i < Options.SphereAmount; i++)
                {
                    var sphere = GameManager.server.CreateEntity(StringPool.Get(3211242734), Location) as SphereEntity;

                    if (sphere.IsNull())
                    {
                        break;
                    }

                    sphere.currentRadius = 1f;
                    sphere.Spawn();
                    sphere.LerpRadiusTo(ProtectionRadius * 2f, ProtectionRadius * 0.75f);
                    spheres.Add(sphere);
                }
            }

            private void SpawnSphere(string prefab)
            {
                if (StringPool.toNumber.ContainsKey(prefab) && GameManager.server.CreateEntity(prefab, Location) is SphereEntity sphere)
                {
                    sphere.currentRadius = ProtectionRadius * 2f;
                    sphere.lerpRadius = sphere.currentRadius;
                    sphere.enableSaving = false;
                    sphere.skinID = RB_SKIN_ID;
                    sphere.OwnerID = RB_SKIN_ID;
                    sphere.Spawn();
                    spheres.Add(sphere);
                }
            }

            private void CreateZoneWalls()
            {
                if (!Options.ArenaWalls.Enabled)
                {
                    return;
                }

                const float yOverlap = 6f;
                float minHeight = float.MaxValue;
                float maxHeight = float.MinValue;
                var maxDistance = 48f;
                var stacks = Options.ArenaWalls.Stacks;
                var center = new Vector3(Location.x, Location.y, Location.z);
                var gap = Options.ArenaWalls.Stone || Options.ArenaWalls.Ice ? 0.3f : 0.5f;
                var next1 = Mathf.CeilToInt(360 / Options.ArenaWalls.Radius * 0.1375f);
                var next2 = 360 / Options.ArenaWalls.Radius - gap;
                var adjusted = false;
                var prefab = GetWallPrefabName(center);

                if (Options.ArenaWalls.IgnoreForcedHeight && Options.Setup.ForcedHeight >= 0 && center.y >= Options.Setup.ForcedHeight)
                {
                    center.y = TerrainMeta.HeightMap.GetHeight(center);
                    adjusted = true;
                }

                using var vectors1 = SpawnsController.GetCircumferencePositions(center, Options.ArenaWalls.Radius, next1, false, false, 1f);
                foreach (var position in vectors1)
                {
                    float y = SpawnsController.GetSpawnHeight(position, false, false, targetMask | Layers.Mask.Construction);
                    maxHeight = Mathf.Max(y, maxHeight, TerrainMeta.WaterMap.GetHeight(position));
                    minHeight = Mathf.Min(y, minHeight);
                    center.y = minHeight;
                }

                if (spawns != null && spawns.IsCustomSpawn)
                {
                    center.y = Location.y - (yOverlap * 0.5f);
                    maxDistance += Location.y;
                }

                if (Options.Setup.ForcedHeight >= 0)
                {
                    maxDistance += Options.Setup.ForcedHeight + Options.Setup.PasteHeightAdjustment;

                    if (Options.ArenaWalls.LeastAmount && adjusted)
                    {
                        stacks += Mathf.FloorToInt((maxHeight - minHeight) / yOverlap);
                    }
                    else
                    {
                        stacks = Mathf.FloorToInt((Options.Setup.ForcedHeight + Options.Setup.PasteHeightAdjustment) / yOverlap);
                    }
                }
                else if (Options.ArenaWalls.IgnoreWhenClippingTerrain)
                {
                    stacks += Mathf.FloorToInt((maxHeight - minHeight) / yOverlap);
                }

                using var vectors2 = SpawnsController.GetCircumferencePositions(center, Options.ArenaWalls.Radius, next2, false, false, center.y);
                for (int i = 0; i < stacks; i++)
                {
                    float currentY = center.y + (i * yOverlap);

                    if (currentY - Location.y > maxDistance)
                    {
                        break;
                    }

                    if (Options.ArenaWalls.LeastAmount && !Options.ArenaWalls.IgnoreForcedHeight && Options.Setup.ForcedHeight != -1 && i + 1 < stacks * 0.75)
                    {
                        continue;
                    }

                    foreach (var v in vectors2)
                    {
                        Vector3 position = new(v.x, currentY, v.z);
                        float terrainHeight = TerrainMeta.HeightMap.GetHeight(position);

                        if (terrainHeight - currentY > yOverlap)
                        {
                            continue;
                        }

                        if (Options.ArenaWalls.LeastAmount)
                        {
                            float h = SpawnsController.GetSpawnHeight(position, true, false, targetMask | Layers.Mask.Construction);
                            float j = stacks * yOverlap + yOverlap;

                            if (position.y - terrainHeight > j && position.y < h)
                            {
                                continue;
                            }
                        }

                        var e = GameManager.server.CreateEntity(prefab, position, Quaternion.identity) as SimpleBuildingBlock;

                        if (e == null)
                        {
                            continue;
                        }

                        e.transform.LookAt(center.WithY(position.y), Vector3.up);

                        if (Options.ArenaWalls.UseUFOWalls)
                        {
                            e.transform.Rotate(-66.6f, 0f, 0f);
                        }
                        else
                        {
                            e.transform.Rotate(0f, 180f, 0f);
                        }

                        e.enableSaving = false;
                        e.Spawn();

                        if (e == null)
                            continue;

                        SetupEntity(e);
                        Encapsulate(e);

                        e.debrisPrefab.guid = null;
                        e.canBeDemolished = false;
                        e.StopBeingDemolishable();

                        float fractionUnder = Mathf.Clamp01((terrainHeight - currentY) / yOverlap);

                        if (fractionUnder > 0.2f)
                        {
                            FixNav(e);
                        }

                        if (Options.ArenaWalls.IgnoreWhenClippingTerrain && i == stacks - 1 && fractionUnder >= 0.6f)
                        {
                            stacks++;
                            continue;
                        }

                        if (Options.ArenaWalls.IgnoreWhenClippingTerrain && stacks == i - 1 && Physics.Raycast(new(v.x, v.y + 6.5f, v.z), Vector3.down, out var hit, 13f, targetMask))
                        {
                            if (hit.collider.ObjectName().Contains("rock") || hit.collider.ObjectName().Contains("formation", CompareOptions.OrdinalIgnoreCase))
                            {
                                stacks++;
                            }
                        }
                    }
                }
            }

            private static void FixNav(SimpleBuildingBlock e)
            {
                MeshCollider mesh = e.GetComponentInChildren<MeshCollider>();
                if (mesh == null || !e.TryGetComponent(out NavMeshObstacle nav))
                {
                    return;
                }
                nav.size = nav.size.WithY(mesh.bounds.size.y);
                nav.center = e.transform.InverseTransformPoint(mesh.bounds.center);
            }

            private string GetWallPrefabName(Vector3 center)
            {
                return (Options.ArenaWalls.Ice, Options.ArenaWalls.Stone) switch
                {
                    (true, true) =>
                        (TerrainBiome.Enum)(TerrainMeta.BiomeMap?.GetBiomeMaxType(center) ?? -1) switch
                        {
                            TerrainBiome.Enum.Arctic or TerrainBiome.Enum.Tundra => "assets/prefabs/misc/xmas/icewalls/wall.external.high.ice.prefab",
                            _ => "assets/prefabs/building/wall.external.high.stone/wall.external.high.stone.prefab",
                        },
                    (true, false) => "assets/prefabs/misc/xmas/icewalls/wall.external.high.ice.prefab",
                    (false, true) => "assets/prefabs/building/wall.external.high.stone/wall.external.high.stone.prefab",
                    _ => "assets/prefabs/building/wall.external.high.wood/wall.external.high.wood.prefab"
                };
            }

            private List<RespawnInfo> respawns = new();
            private List<NaturalBeehive> hives = new();

            public class RespawnInfo
            {
                public Vector3 pos;
                public Quaternion rot;
                public string addition;
                public string prefab;
                public string guid;
                public float chance;
                public BaseEntity ent;
                public RespawnInfo(BaseEntity entity)
                {
                    if (entity is VineSwingingTree vine)
                    {
                        if (vine.StumpPrefab.isValid)
                        {
                            guid = vine.StumpPrefab.guid;
                            vine.StumpPrefab.guid = string.Empty;
                        }
                    }
                    else if (entity is TreeEntity tree)
                    {
                        if (tree.spawnTreeAddition && tree.treeAdditionPrefab.isValid)
                        {
                            chance = 1f;
                            addition = tree.treeAdditionPrefab.guid;
                        }
                    }
                    pos = entity.transform.position;
                    rot = entity.transform.rotation;
                    prefab = entity.PrefabName;
                    ent = entity;
                }
                public void Respawn()
                {
                    if (!ent.IsKilled())
                    {
                        if (ent is VineSwingingTree vine && !vine.StumpPrefab.isValid)
                        {
                            vine.StumpPrefab.guid = guid;
                        }
                        ent.transform.position = pos;
                    }
                    else
                    {
                        BaseEntity entity = GameManager.server.CreateEntity(prefab, pos, rot);
                        if (entity is TreeEntity tree)
                        {
                            if (chance != 0)
                            {
                                tree.spawnTreeAddition = true;
                                tree.treeAdditionSpawnChance = chance;
                                tree.treeAdditionPrefab.guid = addition;
                            }
                            else tree.spawnTreeAddition = false;
                        }
                        entity.Spawn();
                    }
                }
            }

            private void RemoveClutter()
            {
                using var tmp = FindEntitiesOfType<BaseEntity>(Location, ProtectionRadius);
                using var players = DisposableList<BasePlayer>();
                tmp.Sort(Instance.TreeComparer);
                foreach (var e in tmp)
                {
                    if (e is NaturalBeehive hive)
                    {
                        hives.Add(hive);
                    }
                    else if (e is TreeEntity t)
                    {
                        if (!Entities.Contains(e))
                        {
                            if (Options.DeleteRadius > 0f && e.Distance(Location) <= Options.DeleteRadius) ScheduledRespawn(e);
                            else if (Options.TreeRadius > 0f) { Eject(t, Location, Options.TreeRadius, true); HandleHiveAt(t, false); }
                            else if (Options.DeleteRadius <= 0f) ScheduledRespawn(e);
                        }
                    }
                    else if ((e is ResourceEntity || e is CollectibleEntity) && NearFoundation(e.transform.position))
                    {
                        Eject(e, Location, ProtectionRadius, true);
                    }
                    else if (e.GetParentEntity() is Tugboat)
                    {
                        continue;
                    }
                    else if (e is HotAirBalloon && NearFoundation(e.transform.position, 10f) && !Entities.Contains(e))
                    {
                        TryEjectMountable(e);
                    }
                    else if (e is BaseSiegeWeapon || e is ConstructableEntity)
                    {
                        Eject(e, Location, ProtectionRadius, true);
                    }
                    else if (e is BaseMountable m && CanEjectMountable(m, players))
                    {
                        TryEjectMountable(e);
                    }
                    else if (e is ScientistNPC && NearFoundation(e.transform.position, 15f))
                    {
                        e.SafelyKill();
                    }
                    else if (Instance.DeployableItems.ContainsKey(e.PrefabName) && !Entities.Contains(e))
                    {
                        DeployableItemHandler(e);
                    }
                    else if (e is DroppedItemContainer container && e.ShortPrefabName == "item_drop_backpack" && e.IsValid())
                    {
                        EjectContainer(container, container.playerSteamID);
                    }
                    else if (e is LootableCorpse corpse)
                    {
                        EjectContainer(corpse, corpse.playerSteamID);
                    }
                    else HandleDefaultEntity(e, config.Settings.Management.EjectMountables);
                }
            }

            private void ScheduledRespawn(BaseEntity ent)
            {
                if (!Options.RespawnTrees)
                {
                    if (ent is VineSwingingTree vine) // this should never be optional or the jungle will become baron over time.
                    {
                        if (!respawns.Exists(x => InRange(x.pos, ent.transform.position, 0.01f)))
                        {
                            respawns.Add(new(ent));
                        }
                        vine.StumpPrefab.guid = string.Empty;
                    }
                    else if (ent is TreeEntity tree) HandleHiveAt(tree, true);
                    ent.SafelyKill();
                    return;
                }
                if (!respawns.Exists(x => InRange(x.pos, ent.transform.position, 0.01f)))
                {
                    if (ent is TreeEntity tree) HandleHiveAt(tree, true);
                    respawns.Add(new(ent));
                }
                ent.SafelyKill();
            }

            private void RespawnEntities()
            {
                foreach (var ent in respawns)
                {
                    ent.Respawn();
                }
                respawns.Clear();
            }

            private void HandleHiveAt(TreeEntity tree, bool kill)
            {
                if (hives.Count == 0)
                {
                    return;
                }
                foreach (var hive in hives)
                {
                    if (!hive.IsKilled() && tree.treeAdditionRef == hive)
                    {
                        hives.Remove(hive);
                        if (kill)
                        {
                            ClearInventory(hive.inventory);
                            hive.transform.position = Vector3.zero;
                            hive.SendNetworkUpdateImmediate();
                            hive.DelayedSafeKill();
                        }
                        else
                        {
                            hive.transform.position = tree.transform.position;
                            hive.SendNetworkUpdate();
                        }
                        return;
                    }
                }
            }

            private bool CanEjectMountable(BaseEntity m, PooledList<BasePlayer> players)
            {
                if (m is BaseChair && !m.OwnerID.IsSteamId()) return false;
                if (m is TrainCar or ZiplineMountable) return false;
                if (Entities.Contains(m)) return false;
                if (m.HasParent())
                {
                    var parent = GetParentEntity(m);
                    if (parent is TrainCar or ZiplineMountable) return false;
                    if (Entities.Contains(parent)) return false;
                }
                if (NearFoundation(m.transform.position, 10f)) return true;
                if (config.Settings.Management.EjectMountables) return true;
                return TryRemoveMountable(m, players);
            }

            private void DeployableItemHandler(BaseEntity e)
            {
                if (e is SleepingBag bag)
                {
                    if (spawns.IsCustomSpawn)
                    {
                        bag.SafelyKill();
                        return;
                    }
                    _bags[bag] = bag.deployerUserID;
                    bag.deployerUserID = 0uL;
                    SleepingBag.OnBagChangedOwnership(bag, _bags[bag]);
                    bag.unlockTime = UnityEngine.Time.realtimeSinceStartup + 99999f;
                }
                if (config.Settings.Management.KillDeployables && e.OwnerID.IsSteamId())
                {
                    e.DelayedSafeKill();
                }
                else if (config.Settings.Management.EjectDeployables && e.OwnerID.IsSteamId())
                {
                    Eject(e, Location, ProtectionRadius + 10f, true);
                }
            }

            public void ResetSleepingBags()
            {
                foreach (var (bag, userid) in _bags)
                {
                    if (bag.IsNull()) continue;
                    ulong oldID = bag.deployerUserID;
                    bag.deployerUserID = userid;
                    SleepingBag.OnBagChangedOwnership(bag, oldID);
                    bag.unlockTime = UnityEngine.Time.realtimeSinceStartup;
                }
            }

            private IEnumerator EntitySetup()
            {
                if (Type != RaidableType.None)
                {
                    TryInvokeMethod(RemoveClutter);
                }

                int checks = 0;
                float invokeTime = 0f;
                int limit = Mathf.Clamp(Options.Setup.SpawnLimit, 1, 500);
                using var tmp = Entities.ToPooledList();

                foreach (var e in tmp)
                {
                    TryInvokeMethod(() => TrySetupEntity(e, ref invokeTime));

                    if (++checks >= limit)
                    {
                        checks = 0;
                        yield return CoroutineEx.waitForSeconds(0.0375f);
                    }
                }

                yield return CoroutineEx.waitForSeconds(2f);

                if (SetupLoot())
                {
                    TryInvokeMethod(Subscribe);
                    TryInvokeMethod(SetupTurrets);
                    TryInvokeMethod(CreateGenericMarker);
                    TryInvokeMethod(UpdateMarker);
                    TryInvokeMethod(EjectSleepers);
                    TryInvokeMethod(CreateZoneWalls);
                    TryInvokeMethod(CreateSpheres);
                    TryInvokeMethod(SetupLights);
                    TryInvokeMethod(SetupDoorControllers);
                    TryInvokeMethod(SetupDoors);
                    TryInvokeMethod(CheckDespawn);
                    TryInvokeMethod(SetupContainers);
                    TryInvokeMethod(MakeAnnouncements);
                    InvokeRepeating(Protector, 1f, 1f);
                    Interface.CallHook("OnRaidableBaseStarted", hookObjects);
                    Interface.CallHook("OnRaidableBaseStarted", rb);
                }
                else
                {
                    IsResetting = true;
                    Despawn();
                }

                loadTime = Time.realtimeSinceStartup - loadTime;
                IsLoading = false;
                Instance.IsSpawnerBusy = false;
                setupRoutine = null;
            }

            private void TrySetupEntity(BaseEntity e, ref float invokeTime)
            {
                if (!CanSetupEntity(e))
                {
                    return;
                }

                SetupEntity(e);

                e.OwnerID = 0;

                Encapsulate(e);

                if (e.skinID == 1337424001 && e is CollectibleEntity ce)
                {
                    ce.itemList = null; // WaterBases compatibility
                }

                if (!Options.AllowPickup && e is BaseCombatEntity bce && !IsPickupAllowed(bce.ShortPrefabName))
                {
                    SetupPickup(bce);
                }

                if (config.Weapons.Burn.Exists(e.ShortPrefabName.Contains))
                {
                    SetupBurnVisuals(e);
                }

                if (e is IOEntity io)
                {
                    SetupLight(io);

                    if (io is ContainerIOEntity cio)
                    {
                        SetupIO(cio);
                    }
                    else if (io is ElectricBattery eb)
                    {
                        SetupBattery(eb);
                    }
                    if (io is AutoTurret turret)
                    {
                        SetupTurret(turret);
                    }
                    else if (io is Igniter igniter)
                    {
                        SetupIgniter(igniter);
                    }
                    else if (io is SamSite ss)
                    {
                        SetupSamSite(ss);
                    }
                    else if (io is TeslaCoil tc)
                    {
                        SetupTeslaCoil(tc);
                    }
                    else if (io is CustomDoorManipulator cdm)
                    {
                        doorControllers.Add(cdm);
                    }
                    else if (io is HBHFSensor sensor)
                    {
                        SetupHBHFSensor(sensor);
                    }
                    else if (io is ElectricGenerator generator)
                    {
                        SetupGenerator(generator);
                    }
                    else if (io is PressButton button)
                    {
                        SetupButton(button);
                    }
                    else if (io is FogMachine fog)
                    {
                        SetupFogMachine(fog);
                    }
                    else if (io is Sprinkler sprinkler)
                    {
                        SetupSprinkler(sprinkler);
                    }
                    else if (io is Fridge fridge)
                    {
                        SetupFridge(fridge);
                    }
                    else if (io is VendingMachine vm)
                    {
                        SetupVendingMachine(vm);
                    }
                    else if (io is IIndustrialStorage st)
                    {
                        TryEmptyIndustrialStorage(io, st);
                    }
                }
                else if (e is StorageContainer c)
                {
                    SetupContainer(c);

                    if (c is BaseOven oven)
                    {
                        SetupOven(oven);
                    }
                    else if (c is FlameTurret ft)
                    {
                        SetupFlameTurret(ft);
                    }
                    else if (c is BuildingPrivlidge priv)
                    {
                        SetupBuildingPriviledge(priv);
                    }
                    else if (c is Locker locker)
                    {
                        SetupLocker(locker);
                    }
                    else if (c is GunTrap gt)
                    {
                        SetupGunTrap(gt);
                    }
                    else if (c is WeaponRack wr)
                    {
                        SetupWeaponRack(wr);
                    }
                }
                else if (e is BuildingBlock block)
                {
                    SetupBuildingBlock(block);
                }
                else if (e is BaseLock)
                {
                    SetupLock(e);
                }
                else if (e is SleepingBag bag)
                {
                    SetupSleepingBag(bag);
                }
                else if (e is CollectibleEntity ce2)
                {
                    SetupCollectible(ce2);
                }
                else if (e is SpookySpeaker speaker)
                {
                    SetupSpookySpeaker(speaker);
                }
                else if (e is ComputerStation cs)
                {
                    SetupComputerStation(cs);
                }

                if (e is DecayEntity de)
                {
                    SetupDecayEntity(de);
                }

                if (e is Door door)
                {
                    SetupDoor(door);
                }
                else SetupSkin(e);
            }

            public bool HasRustNavigationBuild;
            public Bounds RustNavigationBounds = default;
            private void Encapsulate(BaseEntity entity)
            {
                if (!ConVar.AI.useUnityNavmesh && entity is DecayEntity)
                {
                    Bounds bounds = entity.WorldSpaceBounds().ToBounds();

                    if (HasRustNavigationBuild)
                    {
                        RustNavigationBounds.Encapsulate(bounds);
                    }
                    else
                    {
                        RustNavigationBounds = bounds;
                        HasRustNavigationBuild = true;
                    }
                }
            }

            private void SetupLights()
            {
                if (Instance.NightLantern.CanCall())
                {
                    return;
                }

                if (config.Settings.Management.Lights || config.Settings.Management.AlwaysLights)
                {
                    ToggleLights();
                }
            }

            public bool IsPasted;
            private double pasteDeadline;

            public void CheckPaste()
            {
                if (IsDespawning || IsPasted || !IsLoading)
                {
                    return;
                }

                if (!Instance.IsCopyPasteLoaded(out _, out CopyPasteError type) && type == CopyPasteError.Steam)
                {
                    pasteDeadline = 0d;
                }
                else if (pasteDeadline == 0d)
                {
                    pasteDeadline = Time.realtimeSinceStartupAsDouble + 900d;
                }
                else if (Time.realtimeSinceStartupAsDouble > pasteDeadline)
                {
                    Puts("{0} @ {1} timed out after 15 minutes of no response from CopyPaste; despawning...", BaseName, Instance.PositionToGrid(Location));
                    IsLoading = false;
                    Despawn();
                    return;
                }

                Invoke(CheckPaste, 1f);
            }

            private void SetupContainers()
            {
                foreach (var container in _containers)
                {
                    if (!IsContainerKilled(container))
                    {
                        container.SendNetworkUpdate();
                    }
                }
            }

            private void SetupWeaponRack(WeaponRack rack)
            {
                if (!config.Settings.Management.Racks || rack.inventory == null)
                {
                    return;
                }
                if (Options.IgnoreContainedLoot && !rack.inventory.IsEmpty())
                {
                    return;
                }
                weaponRacks.Add(rack);
            }

            public void SetupElevator(Elevator elevator)
            {
                if (!elevator.IsValid() || Elevators.ContainsKey(elevator.net.ID))
                {
                    return;
                }

                Elevators[elevator.net.ID] = new() { Elevator = elevator, raid = this };
                elevator._maxHealth = Options.Elevators.ElevatorHealth;
                elevator.InitializeHealth(Options.Elevators.ElevatorHealth, Options.Elevators.ElevatorHealth);

                if (!Options.Elevators.RequiresPower && !elevator.IsPowered())
                {
                    if (elevator.previousPowerAmount?.Length >= 3) elevator.previousPowerAmount[2] = elevator.DesiredPower();
                    elevator.SetFlagLocal(IOEntity.Flag_HasPower, true);
                }

                if (Options.Elevators.RequiresBuildingPermission || Options.Elevators.RequiredAccessLevel > 0)
                {
                    Instance.Subscribe(nameof(OnElevatorButtonPress));
                    Instance.Subscribe(nameof(OnButtonPress));
                }

                elevator.SendNetworkUpdate();
            }

            private void SetupPickup(BaseCombatEntity e)
            {
                e.pickup.enabled = false;
            }

            private int _initialContainerCount;
            private void AddContainer(StorageContainer container)
            {
                _containers.Add(container);
                _initialContainerCount = _containers.Count;
            }

            private void TryAddContainer(StorageContainer container)
            {
                if (IsBox(container, true) || container is BuildingPrivlidge)
                {
                    AddContainer(container);
                }

                _allcontainers.Add(container);

                AddEntity(container);
            }

            private void RemoveContainer(StorageContainer container)
            {
                _allcontainers.Remove(container);
                _containers.Remove(container);
                Entities.Remove(container);
                container.skinID = 102201;
                container.dropsLoot = false;
                container.DelayedSafeKill();
            }

            public void TryEmptyContainer(StorageContainer container)
            {
                if (ShouldEmptyAll(container))
                {
                    ClearInventory(container.inventory);
                    ItemManager.DoRemoves();
                }
                container.dropsLoot = false;
                container.dropFloats = false;
            }

            public void TryEmptyIndustrialStorage(IOEntity io, IIndustrialStorage storage)
            {
                if (ShouldEmptyAll(io) && storage != null)
                {

                    try { ClearInventory(storage.Container); } catch { }
                }
            }

            public void TryEmptyContainer(ContainerIOEntity container)
            {
                if (ShouldEmptyAll(container))
                {
                    ClearInventory(container.inventory);
                    ItemManager.DoRemoves();
                }
                if (!(container is Fridge))
                {
                    container.dropsLoot = false;
                    container.dropFloats = false;
                }
            }

            private bool ShouldEmptyAll(BaseEntity container)
            {
                return Options.EmptyAll && Type != RaidableType.None && !Options.EmptyExemptions.Exists(container.ShortPrefabName.Contains);
            }

            private void SetupContainer(StorageContainer container)
            {
                if (!container.HasParent()) TryAddContainer(container);

                if (container.inventory == null)
                {
                    container.CreateInventory(true);
                }
                else TryEmptyContainer(container);

                SetupBoxSkin(container);

                if (Type == RaidableType.None && container.inventory.itemList.Count > 0)
                {
                    return;
                }

                container.dropsLoot = false;
                container.dropFloats = false;

                if (container is BuildingPrivlidge)
                {
                    container.dropsLoot = config.Settings.Management.AllowCupboardLoot;
                }
                else if (!IsProtectedWeapon(container))
                {
                    container.dropsLoot = true;
                }

                if (IsBox(container, false) || container is BuildingPrivlidge || config.Settings.Management.Racks && container is WeaponRack)
                {
                    container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, Options.NoItemInput);
                }

                if (IsBox(container, false))
                {
                    CreateLock(container, Options.KeyLockBoxes, Options.CodeLockBoxes);
                }

                if (container is Locker)
                {
                    CreateLock(container, Options.KeyLockLockers, Options.CodeLockLockers);
                }
            }

            private void SetupIO(ContainerIOEntity io)
            {
                io.dropFloats = false;
                io.dropsLoot = !IsProtectedWeapon(io);
                io.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }

            private void SetupIO(IOEntity io)
            {
                using var update = io.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags);
                update.Set(IOEntity.Flag_HasPower, true);
            }

            private void SetupLock(BaseEntity e, bool justCreated = false)
            {
                AddEntity(e);
                locks.Add(e);

                if (Type == RaidableType.None)
                {
                    return;
                }

                if (e is CodeLock codeLock)
                {
                    if (config.Settings.Management.RandomCodes || justCreated)
                    {
                        codeLock.code = UnityEngine.Random.Range(1000, 9999).ToString();
                        codeLock.hasCode = true;
                    }

                    codeLock.OwnerID = 0;
                    codeLock.guestCode = string.Empty;
                    codeLock.hasGuestCode = false;
                    codeLock.guestPlayers.Clear();
                    codeLock.whitelistPlayers.Clear();
                    codeLock.SetFlagLocal(BaseEntity.Flags.Locked, true);
                    codeLock.SendNetworkUpdate();
                }
                else if (e is KeyLock keyLock)
                {
                    if (config.Settings.Management.RandomCodes)
                    {
                        keyLock.keyCode = UnityEngine.Random.Range(1, 100000);
                    }

                    keyLock.OwnerID = 0;
                    keyLock.firstKeyCreated = true;
                    keyLock.SetFlagLocal(BaseEntity.Flags.Locked, true);
                    keyLock.SendNetworkUpdate();
                }
            }

            private void SetupVendingMachine(VendingMachine vm)
            {
                vms.Add(vm);
                TryEmptyContainer(vm);
                vm.dropsLoot = false;
                vm.SetFlagLocal(BaseEntity.Flags.Reserved4, config.Settings.Management.AllowBroadcasting);
                vm.FullUpdate();
            }

            private void SetupLight(IOEntity light)
            {
                if (light == null || light is XORSwitch or BaseDetector)
                {
                    return;
                }

                bool lightsEnabled = config.Settings.Management.Lights || config.Settings.Management.AlwaysLights;
                bool isIgnored = config.Settings.Management.IgnoredLights.Exists(light.ShortPrefabName.Contains);
                bool isSupportedLight = light is SimpleLight or CeilingLight or SearchLight or SirenLight or FlasherLight or Chandelier or StringLights or ElectricalHeater or AudioVisualisationEntity or NeonSign or StrobeLight
                    || light is ContainerIOEntity && light.ShortPrefabName.Contains("wallcabinet") || light.PrefabName.Contains("light");

                if (!lightsEnabled || isIgnored || !isSupportedLight)
                {
                    return;
                }

                if (!lights.Contains(light))
                {
                    lights.Add(light);
                }
            }

            private void SetupHBHFSensor(HBHFSensor sensor)
            {
                if (!sensor.HasConnections())
                {
                    return;
                }
                triggers[sensor.myTrigger] = sensor;
                using var update = sensor.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags);
                update.Set(IOEntity.Flag_HasPower, true);
                update.Set(HBHFSensor.Flag_IncludeAuthed, true);
                update.Set(HBHFSensor.Flag_IncludeOthers, true);
            }

            private void SetupBattery(ElectricBattery eb)
            {
                eb.rustWattSeconds = eb.maxCapactiySeconds - 1f;
            }

            private void SetupGenerator(ElectricGenerator generator)
            {
                generator.electricAmount = config.Weapons.TestGeneratorPower;
            }

            private void SetupButton(PressButton button)
            {
                if (!Options.Elevators.RequiresPower && !button.IsPowered())
                {
                    SetupIO(button);
                }
                buttons.Add(button);
                button._maxHealth = Options.Elevators.ButtonHealth;
                button.InitializeHealth(Options.Elevators.ButtonHealth, Options.Elevators.ButtonHealth);
            }

            private void SetupBuildingBlock(BuildingBlock block)
            {
                if (block.IsKilled())
                {
                    return;
                }

                if (blockPrefabs.Contains(block.ShortPrefabName))
                {
                    blocks.Add(block);
                }

                if (!IsUnloading)
                {
                    ChangeTier(block);
                    block.StopBeingDemolishable();
                    block.StopBeingRotatable();
                }
            }

            private List<string> blockPrefabs = new() { "foundation.triangle", "foundation", "floor.triangle", "floor", "roof", "roof.triangle" };

            public bool HasSkin(BuildingBlock block, BuildingGrade.Enum grade, ulong skin)
            {
                ConstructionGrade constructionGrade = block.blockDefinition.GetGrade(grade, skin);
                if (constructionGrade == null || !constructionGrade.skinObject.isValid) return false;
                if (constructionGrade.gradeBase.type != grade || constructionGrade.gradeBase.skin != skin) return false;
                return GameManager.server.FindPrefab(constructionGrade.skinObject.resourcePath).HasComponent<ConstructionSkin>();
            }

            private void ChangeTier(BuildingBlock block)
            {
                if (block.grade == BuildingGrade.Enum.Twigs)
                {
                    return;
                }
                BuildingGrade.Enum grade = Options.Blocks switch
                {
                    { HQM: true } => BuildingGrade.Enum.TopTier,
                    { Metal: true } => BuildingGrade.Enum.Metal,
                    { Stone: true } => BuildingGrade.Enum.Stone,
                    { Wooden: true } => BuildingGrade.Enum.Wood,
                    _ => block.grade
                };
                ulong skinID = block.skinID;
                if (block.grade != grade || !HasSkin(block, block.grade, skinID))
                {
                    skinID = 0uL;
                }
                if (block.grade != grade || block.skinID != skinID)
                {
                    block.ChangeGradeAndSkin(grade, skinID, false, true);
                }
                block.SetHealthToMax();
                block.SendNetworkUpdate();
            }

            private Dictionary<BuildingGrade.Enum, ulong> skinWhole = new();
            private Dictionary<BuildingGrade.Enum, uint> skinColors = new();

            private void SetupTeslaCoil(TeslaCoil tc)
            {
                tc.InitializeHealth(config.Weapons.TeslaCoil.Health, config.Weapons.TeslaCoil.Health);
                tc.maxDischargeSelfDamageSeconds = Mathf.Clamp(config.Weapons.TeslaCoil.MaxDischargeSelfDamageSeconds, 0f, 9999f);
                tc.maxDamageOutput = Mathf.Clamp(config.Weapons.TeslaCoil.MaxDamageOutput, 0f, 9999f);

                if (!config.Weapons.TeslaCoil.RequiresPower)
                {
                    tc.UpdateFromInput(Mathf.Max(tc.ConsumptionAmount(), 35), 0);
                }
            }

            private void SetupIgniter(Igniter igniter)
            {
                igniter.SelfDamagePerIgnite = 0f;
            }

            public void PreSetupTurret(AutoTurret turret)
            {
                if (Options.AutoTurret.TurretHours > 0)
                {
                    double elapsedHours = (DateTime.UtcNow - SaveRestore.SaveCreatedTime).TotalHours;
                    if (elapsedHours < Options.AutoTurret.TurretHours)
                    {
                        //Puts("Remove turret, elapsed {0}h, required {1}h", elapsedHours, Options.AutoTurret.TurretHours);
                        RemoveTurret(turret);
                        return;
                    }
                }
                turret.skinID = RB_SKIN_ID;
                turret.dropsLoot = false;
                if (turret.targetTrigger != null)
                {
                    triggers[turret.targetTrigger] = turret;
                }
            }

            public class TurretInfo
            {
                public AutoTurret turret;
                public bool refillPending;
                public bool infiniteAmmoDisabled;
                public int refillAttempts;
            }

            private void SetupTurret(AutoTurret turret)
            {
                triggers[turret.targetTrigger] = turret;

                if (IsUnloading || Type == RaidableType.None)
                {
                    return;
                }

                if (config.Settings.Management.ClippedTurrets && turret.RCEyes != null)
                {
                    var position = turret.RCEyes.position;

                    if (IsRockFaceUpwards(position))
                    {
                        RemoveTurret(turret);
                        return;
                    }
                }

                if (turret is NPCAutoTurret)
                {
                    turret.baseProtection = Instance.GetTurretProtection();
                    BMGELEVATOR.RemoveImmortality(turret.baseProtection, 1f, 1f, 1f, 1f, 1f, 0.8f, 1f, 1f, 1f, 0.9f, 0.5f, 0.5f, 1f, 1f, 0f, 0.5f, 0f, 1f, 1f, 0f, 1f, 0.9f, 0f, 1f, 0f);
                }

                SetupIO(turret as IOEntity);

                if (Type != RaidableType.None)
                {
                    turret.authorizedPlayers.Clear();
                }

                turret.InitializeHealth(Options.AutoTurret.Health, Options.AutoTurret.Health);
                SetupSightRange(turret, Options.AutoTurret.SightRange);
                turret.aimCone = Options.AutoTurret.AimCone;
                turrets[turret.net.ID.Value] = new()
                {
                    turret = turret,
                };

                if (turret.AttachedWeapon != null)
                {
                    turret.AttachedWeapon.EnableSaving(true);
                }

                if (Options.AutoTurret.RemoveWeapon)
                {
                    turret.AttachedWeapon = null;
                    Item slot = turret.inventory.GetSlot(0);
                    if (slot != null && (slot.info.category == ItemCategory.Weapon || slot.info.category == ItemCategory.Fun))
                    {
                        slot.RemoveFromContainer();
                        slot.Remove();
                    }
                }

                if (Options.AutoTurret.Hostile)
                {
                    turret.SetPeacekeepermode(false);
                }

                if (config.Weapons.InfiniteAmmo.AutoTurret)
                {
                    turret.inventory.onPreItemRemove += new Action<Item>(OnWeaponItemPreRemove);
                }
            }

            public bool TryRemoveOrConfirmTurret(AutoTurret turret) => turret.net != null && turrets.Remove(turret.net.ID.Value) || !turret.enableSaving && turret.skinID == RB_SKIN_ID;

            private void RemoveTurret(AutoTurret turret)
            {
                turret.skinID = 0uL;
                Entities.Remove(turret);
                TryRemoveOrConfirmTurret(turret);
                turret.dropsLoot = false;
                turret.DelayedSafeKill();
                if (turret.targetTrigger != null)
                {
                    triggers.Remove(turret.targetTrigger);
                }
            }

            private readonly Dictionary<NetworkableId, SphereCollider> _turretColliders = new();

            public void SetupSightRange(AutoTurret turret, float sightRange, int multi = 1)
            {
                if (turret.net != null && turret.targetTrigger != null)
                {
                    if (!_turretColliders.TryGetValue(turret.net.ID, out var collider) && turret.targetTrigger.TryGetComponent<SphereCollider>(out var val))
                    {
                        _turretColliders[turret.net.ID] = collider = val;
                    }
                    if (collider != null)
                    {
                        if (multi > 1)
                        {
                            turret.Invoke(() =>
                            {
                                if (collider != null) collider.radius = sightRange;
                                if (turret != null) turret.sightRange = sightRange;
                            }, 15f);
                        }
                        collider.radius = sightRange * multi;
                    }
                }
                turret.sightRange = sightRange * multi;
            }

            private void SetupTurrets()
            {
                if (Type != RaidableType.None && turrets.Count > 0)
                {
                    turretsCoroutine = ServerMgr.Instance.StartCoroutine(TurretsCoroutine());
                }
                else SetupNpcKits();
            }

            private IEnumerator TurretsCoroutine()
            {
                if (InitiateTurretOnSpawn)
                {
                    while (IsLoading)
                    {
                        yield return CoroutineEx.waitForSeconds(0.1f);
                    }
                }

                bool f = Options.AutoTurret.Shortnames.Count > 0;
                Options.AutoTurret.Shortnames.Remove("rocket.launcher");
                Options.AutoTurret.Shortnames.Remove("fun.trumpet");
                Options.AutoTurret.Shortnames.Remove("snowballgun");
                Options.AutoTurret.Shortnames.Remove("flamethrower");
                Options.AutoTurret.Shortnames.Remove("homingmissile.launcher");

                using var tmp = turrets.Values.ToPooledList();

                foreach (var info in tmp)
                {
                    yield return CoroutineEx.waitForSeconds(0.025f);

                    if (f) EquipTurretWeapon(info, Options.AutoTurret.Shortnames);

                    yield return CoroutineEx.waitForSeconds(0.025f);

                    UpdateAttachedWeapon(info);

                    yield return CoroutineEx.waitForSeconds(0.025f);

                    InitiateStartup(info);

                    yield return CoroutineEx.waitForSeconds(0.025f);

                    FillAmmoTurret(info);

                    DisableInterference(info);
                }

                SetupNpcKits();

                List<AutoTurret> obj = new();
                foreach (var info in turrets.Values)
                {
                    if (!IsContainerKilled(info))
                    {
                        obj.Add(info.turret);
                    }
                }

                Interface.CallHook("OnRaidableTurretsInitialized", new object[] { obj, Location, ProtectionRadius, 512, AllowPVP, ownerId });

                turretsCoroutine = null;
            }

            public bool UsableByTurret;

            private void EquipTurretWeapon(TurretInfo info, List<string> shortnames)
            {
                if (IsContainerKilled(info) || !info.turret.AttachedWeapon.IsNull()) return;

                AutoTurret turret = info.turret;
                using var weapons = DisposableList<(ItemDefinition, List<ulong>)>();
                foreach (var shortname in shortnames)
                {
                    if (string.IsNullOrWhiteSpace(shortname))
                    {
                        Puts("Invalid shortname in profile for turret weapon: {0}", shortname ?? "null");
                        continue;
                    }
                    ItemDefinition itemToCreate = ItemManager.FindItemDefinition(shortname);
                    if (itemToCreate == null)
                    {
                        Puts("Invalid shortname in profile for turret weapon: {0}", shortname);
                        continue;
                    }
                    if (!IsValidWeapon(itemToCreate))
                    {
                        continue;
                    }
                    weapons.Add((itemToCreate, new() { 0 }));
                }

                if (weapons.Count == 0)
                {
                    var fallback = ItemManager.FindItemDefinition("pistol.python");
                    if (fallback != null) weapons.Add(new(fallback, new() { 0 }));
                }

                var (def, skins) = weapons.GetRandom();
                ulong skin = GetItemSkin(def, SkinType.Loot, 0, config.Skins.Loot.Stackable, config.Skins.Loot.NonStackable, config.Skins.Loot.Random, config.Skins.Loot.Workshop, config.Skins.Loot.Workshop, config.Skins.Loot.ApprovedOnly, 1);
                if (!config.Skins.Deployables.Everything && !config.Skins.Deployables.Names.Exists(turret.ShortPrefabName.Contains)) skin = 0;
                else if (config.Skins.Deployables.Unique && _itemIdToSkin.TryGetValue(def.itemid, out var s)) skin = s;
                if (skin != 0 && Instance.RequiresOwnership(def, skin)) skin = 0;
                if (skin != 0 && !skins.Contains(skin)) skins.Add(skin);
                if (skin != 0) _itemIdToSkin.TryAdd(def.itemid, skin);
                if (skins.Count > 0 && config.BlockPaidContent) skins.RemoveAll(x => Instance.RequiresOwnership(def, x));

                Item item = ItemManager.Create(def, 1, skins.Count == 0 ? 0 : skins.GetRandom());
                BaseProjectile baseProjectile = item.GetHeldEntity() as BaseProjectile;
                if (baseProjectile != null)
                {
                    if (baseProjectile.MuzzlePoint == null)
                    {
                        baseProjectile.MuzzlePoint = baseProjectile.transform;
                    }
                    bool modified = false;
                    if (!baseProjectile.usableByTurret)
                    {
                        baseProjectile.usableByTurret = true;
                        UsableByTurret = true;
                        modified = true;
                    }
                    if (modified && def.shortname != "pistol.python")
                    {
                        turret.inventory.canAcceptItem -= turret.CanAcceptItem;
                        turret.inventory.canAcceptItem += CanAcceptItem;
                    }
                }

                if (!item.MoveToContainer(turret.inventory, 0, false))
                {
                    item.Remove();
                }
                else item.SwitchOnOff(true);
            }

            public bool IsValidWeapon(ItemDefinition itemDef)
            {
                ItemModEntity component = itemDef.GetComponent<ItemModEntity>();
                if (component == null)
                {
                    return false;
                }
                GameObjectRef objRef = component.entityPrefab;
                if (objRef == null)
                {
                    if (!Instance.DestroyedPrefabs.Contains(itemDef.shortname))
                    {
                        Puts("The game object reference for '{0}' has been broken by another plugin.", itemDef.shortname);
                        Instance.DestroyedPrefabs.Add(itemDef.shortname);
                    }
                    return false;
                }
                GameObject obj = objRef.Get();
                if (obj == null)
                {
                    if (!Instance.DestroyedPrefabs.Contains(itemDef.shortname))
                    {
                        Puts("The game object for '{0}' has been broken by another plugin.", itemDef.shortname);
                        Instance.DestroyedPrefabs.Add(itemDef.shortname);
                    }
                    return false;
                }
                HeldEntity component2 = obj.GetComponent<HeldEntity>();
                if (component2 == null)
                {
                    return false;
                }
                if (!component2.IsUsableByTurret)
                {
                    return false;
                }
                return true;
            }

            private bool CanAcceptItem(Item item, int targetPos)
            {
                if (targetPos == 0)
                {
                    return item.info.category == ItemCategory.Weapon;
                }
                return item.info.category == ItemCategory.Ammunition;
            }

            private bool CanAcceptItem(BasePlayer player, Item item, int targetPos)
            {
                if (targetPos == 0)
                {
                    return item.info.category == ItemCategory.Weapon;
                }
                return item.info.category == ItemCategory.Ammunition;
            }

            private void UpdateAttachedWeapon(TurretInfo info)
            {
                if (!IsUnloading && !IsDespawning && !IsContainerKilled(info))
                {
                    try { info.turret.UpdateAttachedWeapon(); } catch { }
                }
            }

            private void InitiateStartup(TurretInfo info)
            {
                if (!Options.AutoTurret.RequiresPower && !IsContainerKilled(info))
                {
                    info.turret.InitiateStartup();
                }
            }

            private void Authorize(ulong userid)
            {
                foreach (var info in turrets.Values)
                {
                    if (!IsContainerKilled(info))
                    {
                        info.turret.authorizedPlayers.Add(userid);
                    }
                }
                if (privSpawned && !priv.IsKilled())
                {
                    priv.authorizedPlayers.Add(userid);
                }
            }

            private bool CanBypassAuthorized(ulong userid) => userid.BelongsToGroup("admin") || userid.HasPermission("raidablebases.canbypass");

            private void SetupGunTrap(GunTrap gt)
            {
                if (config.Weapons.Ammo.GunTrap > 0)
                {
                    FillAmmoGunTrap(gt);
                }

                if (config.Weapons.InfiniteAmmo.GunTrap)
                {
                    gt.inventory.onPreItemRemove += new Action<Item>(OnWeaponItemPreRemove);
                }

                triggers[gt.trigger] = gt;
            }

            private void SetupFogMachine(FogMachine fog)
            {
                if (config.Weapons.Ammo.FogMachine > 0)
                {
                    FillAmmoFogMachine(fog);
                }

                if (config.Weapons.InfiniteAmmo.FogMachine)
                {
                    fog.fuelPerSec = 0f;
                }

                if (config.Weapons.FogMotion || !config.Weapons.FogRequiresPower)
                {
                    using var update = fog.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);

                    if (config.Weapons.FogMotion)
                    {
                        update.Set(BaseEntity.Flags.Reserved9, true);
                    }

                    if (!config.Weapons.FogRequiresPower)
                    {
                        fog.CancelInvoke(fog.CheckTrigger);
                        update.Set(BaseEntity.Flags.Reserved5, true);
                        update.Set(BaseEntity.Flags.Reserved6, true);
                        update.Set(BaseEntity.Flags.Reserved10, true);
                        update.Set(BaseEntity.Flags.On, true);
                    }
                }
            }

            private void SetupSprinkler(Sprinkler sprinkler)
            {
                if (!config.Weapons.SprinklerRequiresPower)
                {
                    sprinkler.SetFlagLocal(BaseEntity.Flags.On, false);
                    try { sprinkler.SetFuelType(WaterTypes.WaterItemDef, null); } catch { }
                    sprinkler.UpdateFromInput(sprinkler.ConsumptionAmount(), 0);
                }
            }

            private void SetupFridge(Fridge fridge)
            {
                TryEmptyContainer(fridge);
                if (config.Settings.Management.Food)
                {
                    fridges.Add(fridge);
                }
                SetupIO((IOEntity)fridge);
            }

            private void SetupBurnVisuals(BaseEntity entity)
            {
                if (entity is BaseOven oven && !oven.IsOn())
                {
                    using var update = oven.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags);
                    update.Set(BaseEntity.Flags.On, true);
                }

                if (entity is IOEntity io)
                {
                    SetupIO(io);
                }
            }

            private void SetupOven(BaseOven oven)
            {
                ovens.Add(oven);
            }

            private void SetupFlameTurret(FlameTurret ft)
            {
                triggers[ft.trigger] = ft;
                ft.InitializeHealth(Options.FlameTurretHealth, Options.FlameTurretHealth);

                if (config.Weapons.Ammo.FlameTurret > 0)
                {
                    FillAmmoFlameTurret(ft);
                }

                if (config.Weapons.InfiniteAmmo.FlameTurret)
                {
                    ft.fuelPerSec = 0f;
                }
            }

            private void SetupSamSite(SamSite ss)
            {
                samsites.Add(ss);

                ss.vehicleScanRadius = ss.missileScanRadius = config.Weapons.SamSiteRange;

                if (config.Weapons.SamSiteRepair > 0f)
                {
                    ss.staticRespawn = true;
                    ss.InvokeRepeating(ss.SelfHeal, config.Weapons.SamSiteRepair * 60f, config.Weapons.SamSiteRepair * 60f);
                }
                else
                {
                    ss.SetFlagLocal(BaseEntity.Flags.Reserved1, false);
                    ss.CancelInvoke(ss.SelfHeal);
                    ss.staticRespawn = false;
                }

                if (!config.Weapons.SamSiteRequiresPower)
                {
                    SetupIO(ss as IOEntity);
                }

                if (config.Weapons.Ammo.SamSite > 0)
                {
                    FillAmmoSamSite(ss);
                }

                if (config.Weapons.InfiniteAmmo.SamSite)
                {
                    ss.inventory.onPreItemRemove += new Action<Item>(OnWeaponItemPreRemove);
                }

                ss.SendNetworkUpdate();
            }

            private bool ChangeTier(Door door)
            {
                uint prefabID = door.ShortPrefabName switch
                {
                    "door.hinged.toptier" => Options.Doors.Metal ? 202293038u : Options.Doors.Wooden ? 1343928398u : 0u,
                    "door.hinged.metal" or "door.hinged.industrial.a" or "door.hinged.industrial.d" => Options.Doors.HQM ? 170207918u : Options.Doors.Wooden ? 1343928398u : 0u,
                    "door.hinged.wood" => Options.Doors.HQM ? 170207918u : Options.Doors.Metal ? 202293038u : 0u,
                    "door.double.hinged.toptier" => Options.Doors.Metal ? 1418678061u : Options.Doors.Wooden ? 43442943u : 0u,
                    "wall.frame.garagedoor" when !Options.Doors.GarageDoor => 0u,
                    "wall.frame.garagedoor" => Options.Doors.HQM ? 201071098u : Options.Doors.Wooden ? 43442943u : 0u,
                    "door.double.hinged.metal" => Options.Doors.HQM ? 201071098u : Options.Doors.Wooden ? 43442943u : 0u,
                    "door.double.hinged.wood" => Options.Doors.HQM ? 201071098u : Options.Doors.Metal ? 1418678061u : 0u,
                    _ => 0u,
                };

                return prefabID != 0u && StringPool.toString.TryGetValue(prefabID, out var prefab) && SetDoorType(door, prefab);
            }

            private bool SetDoorType(Door door, string prefab)
            {
                Door other = GameManager.server.CreateEntity(prefab, door.transform.position, door.transform.rotation) as Door;
                if (other != null)
                {
                    var parent = door.HasParent() ? door.GetParentEntity() : null;
                    if (parent != null)
                    {
                        other.gameObject.Identity();

                        if (door.parentBone != 0) other.SetParent(parent, StringPool.Get(door.parentBone));
                        else other.SetParent(parent);
                    }

                    var building = door.GetBuilding();
                    if (building != null)
                    {
                        other.AttachToBuilding(building.ID);
                    }
                    else if (priv != null)
                    {
                        other.AttachToBuilding(priv.buildingID);
                    }

                    other.enableSaving = false;
                    other.Spawn();

                    if (other != null)
                    {
                        door.SafelyKill();
                        SetupEntity(other);
                        SetupDoor(other, true);
                        other.RefreshEntityLinks();
                        other.SendNetworkUpdate();
                        return true;
                    }
                }

                return false;
            }

            private void SetupDoor(Door door)
            {
                if (door.canTakeLock && !door.isSecurityDoor)
                {
                    doors.Add(door);
                }
            }

            private void SetupDoor(Door door, bool changed)
            {
                CreateLock(door, Options.KeyLockDoors, Options.CodeLockDoors);

                if (!changed && Options.Doors.Any())
                {
                    try
                    {
                        if (ChangeTier(door))
                        {
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Puts(ex);
                        if (door.IsKilled())
                        {
                            return;
                        }
                    }
                }

                SetupSkin(door);

                if (Options.CloseOpenDoors)
                {
                    CloseDoor(door);
                }
            }

            private void CloseDoor(Door door)
            {
                if (door.IsKilled() || !door.IsOpen())
                {
                    return;
                }

                if (door.IsBusy())
                {
                    bool reverseOpen = door.HasFlag(Door.ReverseOpen);
                    door.ReverseDoorAnimation(true, reverseOpen);
                    door.SetOpen(false, true);
                    door.StopCheckingForBlockages();
                    door.ClientRPC(RpcTarget.NetworkGroup("OnDoorInterrupted"), true, reverseOpen);
                    return;
                }

                door.SetOpen(false, true);
            }

            private void SetupDoors()
            {
                doors.RemoveAll(IsKilled);

                foreach (var door in doors)
                {
                    SetupDoor(door, false);
                }
            }

            private void SetupDoorControllers()
            {
                doorControllers.RemoveAll(IsKilled);

                foreach (var cdm in doorControllers)
                {
                    if (!Options.DoorControllersRequiresPower)
                    {
                        SetupIO(cdm);
                    }

                    Door door = cdm.targetDoor;

                    if (door != null)
                    {
                        SetupPairedDoor(door);
                        continue;
                    }

                    try { door = cdm.FindDoor(true); } catch { continue; }

                    if (door.IsNetworked())
                    {
                        SetupPairedDoor(door);
                        cdm.SetTargetDoor(door);
                    }
                }

                doorControllers.Clear();
            }

            private void SetupPairedDoor(Door door)
            {
                if (door.canTakeLock && !door.isSecurityDoor)
                {
                    CreateLock(door, Options.KeyLockDoors, Options.CodeLockDoors);
                }
                SetupSkin(door);
                doors.Remove(door);
            }

            private void CreateLock(BaseEntity entity, bool createKeyLock, bool createCodeLock)
            {
                if (Type == RaidableType.None || !createKeyLock && !createCodeLock || entity.IsKilled())
                {
                    return;
                }

                var slot = entity.GetSlot(BaseEntity.Slot.Lock);

                if (slot.IsNull())
                {
                    if (createKeyLock)
                    {
                        CreateKeyLock(entity);
                    }
                    else if (createCodeLock)
                    {
                        CreateCodeLock(entity);
                    }
                    return;
                }

                if (createKeyLock)
                {
                    if (slot is CodeLock codeLock)
                    {
                        codeLock.SetParent(null);
                        codeLock.SafelyKill();
                    }

                    if (!(slot is KeyLock keyLock))
                    {
                        CreateKeyLock(entity);
                    }
                    else SetupLock(keyLock);
                }
                else if (createCodeLock)
                {
                    if (slot is KeyLock keyLock)
                    {
                        keyLock.SetParent(null);
                        keyLock.SafelyKill();
                    }

                    if (!(slot is CodeLock codeLock))
                    {
                        CreateCodeLock(entity);
                    }
                    else SetupLock(codeLock, true);
                }
            }

            private void CreateKeyLock(BaseEntity entity)
            {
                if (GameManager.server.CreateEntity(StringPool.Get(2106860026)) is KeyLock keyLock)
                {
                    keyLock.gameObject.Identity();
                    keyLock.SetParent(entity, entity.GetSlotAnchorName(BaseEntity.Slot.Lock));
                    keyLock.Spawn();
                    entity.SetSlot(BaseEntity.Slot.Lock, keyLock);
                    SetupLock(keyLock, true);
                }
            }

            private void CreateCodeLock(BaseEntity entity)
            {
                if (GameManager.server.CreateEntity(StringPool.Get(3518824735)) is CodeLock codeLock)
                {
                    codeLock.gameObject.Identity();
                    codeLock.SetParent(entity, entity.GetSlotAnchorName(BaseEntity.Slot.Lock));
                    codeLock.Spawn();
                    entity.SetSlot(BaseEntity.Slot.Lock, codeLock);
                    SetupLock(codeLock, true);
                }
            }

            private void SetupBuildingPriviledge(BuildingPrivlidge priv)
            {
                if (Type != RaidableType.None)
                {
                    priv.authorizedPlayers.Clear();
                    priv.SendNetworkUpdate();
                }

                CreateLock(priv, Options.KeyLockPrivilege, Options.CodeLockPrivilege);

                if (IsContainerKilled(this.priv) || priv.Distance(Location) < this.priv.Distance(Location))
                {
                    this.priv = priv;
                    privSpawned = true;
                }

                if (privSpawned && !privHadLoot)
                {
                    privHadLoot = priv != null && priv.inventory != null && !priv.inventory.IsEmpty();
                }

                privs.Add(priv);

                if (ConVar.Softcore.raidwindow_enabled && BaseGameMode.GetActiveGameMode(serverside: true) is GameModeSoftcore)
                {
                    raidWindowPrivs = true;
                    priv.UpdateRaidableFlag();
                }
            }

            private bool raidWindowPrivs;
            private void RefreshRaidWindowPrivileges()
            {
                if (ConVar.Softcore.raidwindow_fresh_tc_seconds <= 0f)
                {
                    return;
                }
                float now = Time.time;
                for (int i = privs.Count - 1; i >= 0; i--)
                {
                    BuildingPrivlidge priv = privs[i];
                    if (IsContainerKilled(priv)) privs.RemoveAt(i);
                    else priv.timePlaced = now;
                }
            }

            private void SetupLocker(Locker locker)
            {
                if (config.Settings.Management.Lockers)
                {
                    lockers.Add(locker);
                }
            }

            private void SetupSleepingBag(SleepingBag bag)
            {
                if (Type != RaidableType.None)
                {
                    ulong oldID = bag.deployerUserID;
                    bag.deployerUserID = 0uL;
                    SleepingBag.OnBagChangedOwnership(bag, oldID);
                }
            }

            private void SetupCollectible(CollectibleEntity ce)
            {
                if (IsPickupBlacklisted(ce.ShortPrefabName))
                {
                    ce.itemList = null;
                }
            }

            private void SetupSpookySpeaker(SpookySpeaker ss)
            {
                if (!config.Weapons.SpookySpeakersRequiresPower)
                {
                    ss.UpdateHasPower(25, 0);
                }
            }

            private void SetupComputerStation(ComputerStation cs)
            {
                if (cs.spawnedIo.IsValid(true))
                {
                    IOEntity io = cs.spawnedIo.Get(true);
                    if (io != null)
                    {
                        SetupIO(io);
                    }
                }
            }

            private void SetupDecayEntity(DecayEntity e)
            {
                e.decay = null;
                e.upkeepTimer = float.MinValue;

                Vector3 position = e.transform.position;
                switch (e)
                {
                    case Signage or BaseTrap or Barricade when !NearFoundation(position, 1.75f) && !Physics.Raycast(e.transform.position + new Vector3(0f, 0.15f, 0f), Vector3.down, 50f, Layers.Mask.Construction):
                        float spawnHeight = SpawnsController.GetSpawnHeight(position, false) + (e is Barricade ? 0f : 0.02f);
                        if (position.y - spawnHeight <= 3f)
                        {
                            position.y = spawnHeight;
                            e.transform.position = position;
                        }
                        break;
                }
            }

            private void SetupBoxSkin(StorageContainer container)
            {
                if (!IsBox(container, false) || config.Skins.Boxes.IgnoreSkinned && container.skinID != 0uL)
                {
                    return;
                }

                if (!Instance.DeployableItems.TryGetValue(container.gameObject.name, out var def))
                {
                    return;
                }

                if (config.Skins.Boxes.Unique && _prefabToSkin.TryGetValue(container.prefabID, out var skin))
                {
                    container.skinID = skin;
                    return;
                }

                var si = GetItemSkins(def, config.Skins.Boxes.ApprovedOnly);

                if (config.Skins.Boxes.Skins.Count > 0 && SetItemSkin(config.Skins.Boxes.Skins.ToList(), si, container, config.Skins.Boxes.Unique))
                {
                    return;
                }

                var skins = GetItemSkins(si, config.Skins.Boxes.Random, config.Skins.Boxes.Workshop, config.Skins.Boxes.Imported);

                if (!_prefabToSkin.TryGetValue(container.prefabID, out ulong value))
                {
                    _prefabToSkin[container.prefabID] = value = skins.Count == 0 ? container.skinID : skins.GetRandom();
                }

                if (config.Skins.Boxes.Unique)
                {
                    container.skinID = value;
                }
                else if (skins.Count > 0)
                {
                    container.skinID = skins.GetRandom();
                }
            }

            private void SetupSkin(BaseEntity entity)
            {
                if (IsUnloading || IsBox(entity, false) || config.Skins.Deployables.IgnoreSkinned && entity.skinID != 0uL)
                {
                    return;
                }

                if (config.Skins.Deployables.Unique && _prefabToSkin.TryGetValue(entity.prefabID, out var skin))
                {
                    entity.skinID = skin;
                    return;
                }

                if (!Instance.DeployableItems.TryGetValue(entity.gameObject.name, out var def) || def == null)
                {
                    return;
                }

                var si = GetItemSkins(def, config.Skins.Deployables.ApprovedOnly);

                if (config.Skins.Deployables.Doors.Count > 0 && entity is Door && SetItemSkin(config.Skins.Deployables.Doors.ToList(), si, entity, config.Skins.Deployables.Unique))
                {
                    return;
                }

                if (!config.Skins.Deployables.Everything && !config.Skins.Deployables.Names.Exists(entity.ShortPrefabName.Contains))
                {
                    return;
                }

                var skins = GetItemSkins(si, config.Skins.Deployables.Random, config.Skins.Deployables.Workshop, config.Skins.Deployables.ImportedWorkshop);

                if (!_prefabToSkin.TryGetValue(entity.prefabID, out ulong value))
                {
                    _prefabToSkin[entity.prefabID] = value = skins.Count == 0 ? entity.skinID : skins.GetRandom();
                }

                if (config.Skins.Deployables.Unique && entity is Door)
                {
                    entity.skinID = value;
                    entity.SendNetworkUpdate();
                }
                else if (skins.Count > 0)
                {
                    entity.skinID = skins.GetRandom();
                    entity.SendNetworkUpdate();
                }
            }

            private void Subscribe()
            {
                if (IsUnloading)
                {
                    return;
                }

                if (Instance.BaseRepair.CanCall())
                {
                    Subscribe(nameof(OnBaseRepair));
                }

                if (config.Settings.Management.AllyExploit)
                {
                    if (config.Settings.Management.BlockClans) Subscribe(nameof(OnClanMemberJoined));
                    if (config.Settings.Management.BlockTeams) Subscribe(nameof(OnTeamAcceptInvite));
                }

                if (Options.EnforceConditionLoss && !Instance.permission.GroupHasPermission("default", "raidablebases.durabilitybypass"))
                {
                    Subscribe(nameof(OnLoseCondition));
                    Subscribe(nameof(OnNeverWear));
                }

                if ((Options.NPC.SpawnAmountMurderers > 0 || Options.NPC.SpawnAmountScientists > 0) && Options.NPC.Enabled)
                {
                    npcMaxAmountMurderers = Options.NPC.SpawnRandomAmountMurderers && Options.NPC.SpawnAmountMurderers > 1 ? UnityEngine.Random.Range(Options.NPC.SpawnMinAmountMurderers, Options.NPC.SpawnAmountMurderers + 1) : Options.NPC.SpawnAmountMurderers;
                    npcMaxAmountScientists = Options.NPC.SpawnRandomAmountScientists && Options.NPC.SpawnAmountScientists > 1 ? UnityEngine.Random.Range(Options.NPC.SpawnMinAmountScientists, Options.NPC.SpawnAmountScientists + 1) : Options.NPC.SpawnAmountScientists;

                    if (npcMaxAmountMurderers > 0 || npcMaxAmountScientists > 0)
                    {
                        if (config.Settings.Management.BlockAlphaLoot)
                        {
                            Subscribe(nameof(CanPopulateLoot));
                        }

                        if (config.Settings.Management.BlockBetterLoot)
                        {
                            Subscribe(nameof(ShouldBLPopulate_NPC));
                        }

                        if (config.Settings.Management.BlockNpcKits)
                        {
                            Subscribe(nameof(OnNpcKits));
                        }

                        if (config.Settings.Management.BlockCustomLootNPC)
                        {
                            Subscribe(nameof(OnCustomLootNPC));
                        }

                        Subscribe(nameof(OnNpcDuck));
                        Subscribe(nameof(OnNpcDestinationSet));
                    }
                }

                if (config.Settings.Management.PreventFallDamage)
                {
                    Subscribe(nameof(OnPlayerLand));
                }

                if (!config.Settings.Management.AllowTeleport)
                {
                    Subscribe(nameof(CanTeleport));
                    Subscribe(nameof(canTeleport));
                }

                if (AllowPVP ? config.Settings.Management.BlockRevivePVP : config.Settings.Management.BlockRevivePVE)
                {
                    Subscribe(nameof(CanRevivePlayer));
                }

                if (AllowPVP ? config.Settings.Management.BlockRestorePVP : config.Settings.Management.BlockRestorePVE)
                {
                    Subscribe(nameof(OnRestoreUponDeath));
                }

                if (config.Settings.Management.NoLifeSupport)
                {
                    Subscribe(nameof(OnLifeSupportSavingLife));
                }

                if (config.Settings.Management.NoDoubleJump)
                {
                    Subscribe(nameof(CanDoubleJump));
                }

                if (!config.Settings.Management.BackpacksOpenPVP || !config.Settings.Management.BackpacksOpenPVE)
                {
                    Subscribe(nameof(CanOpenBackpack));
                }

                if (config.Settings.Management.PreventFireFromSpreading)
                {
                    Subscribe(nameof(OnFireBallSpread));
                }

                if (privSpawned)
                {
                    Subscribe(nameof(OnCupboardProtectionCalculated));
                }

                if (Options.BuildingRestrictions.Any() || !config.Settings.Management.AllowUpgrade)
                {
                    Subscribe(nameof(OnStructureUpgrade));
                }

                if (BlacklistedCommands.Exists(x => x.Equals("remove", StringComparison.OrdinalIgnoreCase)))
                {
                    Subscribe(nameof(canRemove));
                }

                if (Options.Invulnerable || Options.InvulnerableUntilCupboardIsDestroyed)
                {
                    Subscribe(nameof(OnEntityGroundMissing));
                }

                if (!config.Settings.Management.RustBackpacksPVP || !config.Settings.Management.RustBackpacksPVE)
                {
                    Subscribe(nameof(OnBackpackDrop));
                }

                if (Options.RequiresCupboardAccess)
                {
                    Subscribe(nameof(OnCupboardAuthorize));
                }

                Subscribe(nameof(OnLootEntityEnd));
                Subscribe(nameof(OnFireBallDamage));
                Subscribe(nameof(CanPickupEntity));
                Subscribe(nameof(OnPlayerDropActiveItem));
                Subscribe(nameof(OnPlayerDeath));
                Subscribe(nameof(OnEntityDeath));
                Subscribe(nameof(OnEntityKill));
                Subscribe(nameof(CanBGrade));
                Subscribe(nameof(CanBePenalized));
                Subscribe(nameof(CanLootEntity));
                Subscribe(nameof(OnEntityBuilt));
                //Subscribe(nameof(CanGainXp));
            }

            private void Subscribe(string hook) => Instance.Subscribe(hook);

            private void MakeAnnouncements()
            {
                if (Type == RaidableType.None)
                {
                    _allcontainers.RemoveWhere(IsContainerKilled);

                    itemAmountSpawned = _allcontainers.Sum(x => x.inventory.itemList.Count);
                }

                Puts("{0} @ {1} : {2} items", BaseName, Instance.PositionToGrid(Location), itemAmountSpawned);

                if (Options.Silent || Options.Smart)
                {
                    return;
                }

                if (!(config.EventMessages.OpenedPVE && !AllowPVP || config.EventMessages.OpenedPVP && AllowPVP))
                {
                    return;
                }

                foreach (var target in BasePlayer.activePlayerList)
                {
                    if (target.HasPermission("raidablebases.limitedannouncements")) continue;
                    float distance = Mathf.Floor(target.transform.position.Distance(Location));
                    string mode = LangMode(target.UserIDString);
                    string flag = mx(GetAllowKey(), target.UserIDString).Replace("[", string.Empty).Replace("] ", string.Empty);
                    string posStr = FormatGridReference(target, Location);
                    string text = posStr != Location.ToString() ? mx("RaidOpenMessage", target.UserIDString, mode, posStr, distance, flag) : mx("RaidOpenNoMapMessage", target.UserIDString, mode, distance, flag);
                    if (Type == RaidableType.None) text = text.Replace(mode, NoMode);
                    string message = ownerId.IsSteamId() ? mx("RaidOpenAppendedFormat", target.UserIDString, text, mx("Owner", target.UserIDString), ownerName) : text;
                    if (config.GUIAnnouncement.Enabled && config.GUIAnnouncement.Distance > 0 && Instance.GUIAnnouncements != null)
                    {
                        if (distance <= config.GUIAnnouncement.Distance)
                        {
                            QueueNotification(target, message);
                        }
                    }
                    else QueueNotification(target, message);
                }
            }

            public void ResetPublicOwner()
            {
                double remainingTime = ownerId.IsSteamId() ? PlayerActivityTimeLeft(ownerId) : 0d;
                if (!IsOpened || remainingTime > 0f)
                {
                    Invoke(ResetPublicOwner, (remainingTime > 0d && !double.IsPositiveInfinity(remainingTime)) ? (float)remainingTime : config.Settings.Management.LockTime * 60f);
                    return;
                }

                if (Interface.CallHook("OnRaidableResetPublicOwner", ownerId, Location, ProtectionRadius, GetRaiders(), Entities.ToList(), 512) != null)
                {
                    return;
                }

                ResetEventLock();
                CheckBackpacks(true);
            }

            public void ResetEventLock()
            {
                if (IsInvoking(ResetPublicOwner))
                {
                    CancelInvoke(ResetPublicOwner);
                }
                Interface.CallHook("OnRaidableBaseUnlocked", new object[] { ownerId.ToString(), Location, Instance.PositionToGrid(Location), 512, AllowPVP, BaseName, spawnDateTime, despawnDateTime });
                raiders.Remove(ownerId);
                IsEngaged = true;
                ownerId = 0uL;
                ownerName = string.Empty;
                UpdateMarker();
            }

            public void SpawnDrops(ItemContainer[] containers, List<LootItem> lootList)
            {
                if (containers == null || containers.Length == 0)
                {
                    return;
                }

                lootList.ForEach(ti =>
                {
                    if (!string.IsNullOrWhiteSpace(ti.shortname) && ti.HasProbability())
                    {
                        if (ti.definition == null)
                        {
                            Puts("Invalid shortname in profile for npc: {0}", ti.shortname);
                            return;
                        }

                        Item item = CreateItem(ti, ti.amountMin < ti.amount ? Core.Random.Range(ti.amountMin, ti.amount + 1) : ti.amount);

                        if (item == null || Array.Exists(containers, container => item.MoveToContainer(container)))
                        {
                            return;
                        }

                        item.Remove();
                    }
                });
            }

            private bool SetupLoot()
            {
                foreach (StorageContainer container in _containers)
                {
                    if (container.IsKilled() || container.inventory != null)
                    {
                        continue;
                    }

                    container.CreateInventory(true);

                    if (IsBox(container, false) || container is BuildingPrivlidge || config.Settings.Management.Racks && container is WeaponRack)
                    {
                        container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, Options.NoItemInput);
                    }
                }

                int removed = _containers.RemoveWhere(IsContainerKilled);

                int amount = Options.GetLootAmount(Type);

                if (Options.SkipTreasureLoot || amount <= 0)
                {
                    return true;
                }

                using var containers = DisposableList<StorageContainer>();

                if (!SetupLootContainers(containers, removed))
                {
                    return false;
                }

                LootProfile loot = new()
                {
                    Unique = Instance.config.Loot,
                    BaseName = BaseName,
                    Amount = amount,
                    Instance = Instance,
                    Options = Options,
                    UserID = ownerId,
                    AllowPVP = AllowPVP
                };

                TakeLootFromLootTables(loot);

                if (loot.Tables.Count == 0)
                {
                    Puts(mx("NoConfiguredLoot"));
                    return true;
                }

                DivideLoot(loot.Tables, loot.Amount, containers);

                SetupSellOrders();

                numLootRequired = GetLootAmountRemaining();

                return true;
            }

            private bool SetupLootContainers(List<StorageContainer> containers, int removed)
            {
                if (_containers.Count == 0)
                {
                    Puts(mx(Entities.Exists() ? "NoContainersFound" : "NoEntitiesFound", null, BaseName, Instance.PositionToGrid(Location)));
                    if (removed > 0) Puts("{0}/{1} containers were destroyed or not initialized.", removed, _initialContainerCount);
                    return false;
                }

                TryInvokeMethod(CheckExpansionSettings);

                using var tmp = _containers.ToPooledList();

                foreach (var container in tmp)
                {
                    if (!IsBox(container, true) || Options.IgnoreContainedLoot && !container.inventory.IsEmpty())
                    {
                        continue;
                    }

                    if (config.Settings.Management.ClippedBoxes && IsRockFaceUpwards(container.transform.position + new Vector3(0f, container.bounds.extents.y)))
                    {
                        RemoveContainer(container);
                        continue;
                    }

                    if (Options.DivideLoot)
                    {
                        containers.Add(container);
                        continue;
                    }
                    else if (container.inventory.IsEmpty())
                    {
                        containers.Add(container);
                        break;
                    }
                }

                if (Options.IgnoreContainedLoot)
                {
                    lockers.RemoveAll(x => IsContainerKilled(x) || !x.inventory.IsEmpty());
                }

                if (containers.Count == 0)
                {
                    Puts(mx("NoBoxesFound", null, BaseName, Instance.PositionToGrid(Location)));
                    return false;
                }

                return true;
            }

            public class LootProfile
            {
                public List<LootItem> Base = new();
                public List<LootItem> Difficulty = new();
                public List<LootItem> Default = new();
                public List<LootItem> Tables = new();
                public TreasureSettings Unique;
                public BuildingOptions Options;
                public RaidableBases Instance;
                public string BaseName;
                public bool AllowPVP;
                public ulong UserID;
                public int Amount;
                public int Count => Base.Count + Difficulty.Count + Default.Count;
            }

            private bool IsItemBlockedInto(LootItem lootItem, StorageContainer container)
            {
                return IsContainerKilled(container) || container is BaseOven && !IsCookable(lootItem.definition) || container is Locker && !IsLockerItem(lootItem.definition);
            }

            private LootItem GetLootItem(List<LootItem> lootList)
            {
                Shuffle(lootList);

                foreach (LootItem lootItem in lootList)
                {
                    if (lootItem.hasPriority)
                    {
                        lootItem.hasPriority = false;

                        return lootItem;
                    }
                }

                return lootList.GetRandom();
            }

            private void DivideLoot(List<LootItem> lootList, int amount, List<StorageContainer> containers)
            {
                while (lootList.Count > 0 && containers.Count > 0 && itemAmountSpawned < amount)
                {
                    LootItem lootItem = GetLootItem(lootList);

                    lootList.Remove(lootItem);

                    Item item = CreateItem(lootItem, lootItem.amount);

                    if (item == null)
                    {
                        continue;
                    }

                    if (MoveToCupboard(item) || MoveToBBQ(item) || MoveToOven(item) || MoveFood(item) || MoveToRack(item) || MoveToLocker(item))
                    {
                        itemAmountSpawned++;
                        continue;
                    }

                    bool itemMovedToContainer = false;

                    foreach (var container in containers)
                    {
                        if (container is WeaponRack || IsItemBlockedInto(lootItem, container))
                        {
                            continue;
                        }
                        if (item.MoveToContainer(container.inventory, -1, false))
                        {
                            if (item.info.category == ItemCategory.Weapon)
                            {
                                weaponsInBox++;
                            }
                            containers.Remove(container);

                            if (!container.inventory.IsFull())
                            {
                                containers.Add(container);
                            }

                            itemMovedToContainer = true;
                            itemAmountSpawned++;
                            break;
                        }
                    }

                    if (!itemMovedToContainer)
                    {
                        item.Remove();
                    }
                }

                if (itemAmountSpawned == 0)
                {
                    Puts(mx("NoLootSpawned"));
                }
            }

            private static void TakeLootFromBaseLoot(LootProfile loot)
            {
                foreach (var (key, profile) in loot.Instance.Buildings.Profiles)
                {
                    if (key != loot.BaseName && !profile.Options.AdditionalBases.ContainsKey(loot.BaseName))
                    {
                        continue;
                    }
                    if (loot.Options.AllowPVP != profile.Options.AllowPVP)
                    {
                        continue;
                    }
                    TakeLootFrom(loot.Instance, loot.Instance.BaseLootList, loot.Base, loot.Options, loot.UserID, loot.AllowPVP);
                    break;
                }

                if (loot.Options.AlwaysSpawnBaseLoot)
                {
                    using var tmp = loot.Base.ToPooledList();

                    foreach (var ti in tmp)
                    {
                        if (ti.HasProbability())
                        {
                            if (!loot.Options.AllowDuplicates)
                            {
                                loot.Base.Remove(ti);
                            }

                            ti.hasPriority = true;

                            AddToLoot(loot, ti);
                        }

                        if (loot.Options.EnforceProbability && ti.probability < 1f)
                        {
                            loot.Base.Remove(ti);
                        }
                    }

                    if (loot.Unique.Base)
                    {
                        loot.Base.Clear();
                    }
                }
            }

            private static void TakeLootFromDifficultyLoot(LootProfile loot)
            {
                if (loot.Instance.Buildings.DifficultyLootLists.TryGetValue("Normal", out var lootList))
                {
                    TakeLootFrom(loot.Instance, lootList, loot.Difficulty, loot.Options, loot.UserID, loot.AllowPVP);
                }
            }

            private static void TakeLootFromWeekdayLoot(LootProfile loot)
            {
                if (loot.Instance.WeekdayLoot.Count > 0)
                {
                    TakeLootFrom(loot.Instance, loot.Instance.WeekdayLoot, loot.Default, loot.Options, loot.UserID, loot.AllowPVP);
                }
            }

            private static void TakeLootFromDefaultLoot(LootProfile loot)
            {
                if (loot.Count < loot.Amount)
                {
                    TakeLootFrom(loot.Instance, loot.Instance.TreasureLoot, loot.Default, loot.Options, loot.UserID, loot.AllowPVP);
                }
            }

            private static void TakeLootFrom(RaidableBases env, List<LootItem> lootList, List<LootItem> to, BuildingOptions Options, ulong UserID, bool AllowPVP)
            {
                if (lootList.Count == 0)
                {
                    return;
                }

                foreach (var ti in lootList.Where(ti => ti != null && ti.amount > 0 && ti.probability > 0f))
                {
                    LootItem clone = ti.Clone();

                    if (env.config.BlockPaidContent)
                    {
                        if (env.RequiresOwnership(ti.definition, 0)) continue;
                        if (env.RequiresOwnership(ti.definition, ti.skin)) clone.skin = 0;
                    }

                    to.Add(clone);
                }

                if (Options.Multiplier != 1f)
                {
                    var m = Mathf.Clamp(Options.Multiplier, 0f, 999f);

                    foreach (var ti in to)
                    {
                        if (ti.amount > 1)
                        {
                            ti.amount = Mathf.CeilToInt(ti.amount * m);
                            ti.amountMin = Mathf.CeilToInt(ti.amountMin * m);
                        }
                    }
                }
            }

            private static void TakeLootFromLootTables(LootProfile loot)
            {
                TakeLootFromBaseLoot(loot);
                TakeLootFromDifficultyLoot(loot);
                TakeLootFromWeekdayLoot(loot);
                TakeLootFromDefaultLoot(loot);

                int iterations = 0;

                List<LootItem> source = new();

                Action<LootItem> remove = (LootItem ti) =>
                {
                    loot.Base.Remove(ti);
                    loot.Difficulty.Remove(ti);
                    loot.Default.Remove(ti);
                };

                Action refill = () =>
                {
                    source.AddRange(loot.Base);
                    source.AddRange(loot.Difficulty);
                    source.AddRange(loot.Default);
                };

                refill();

                if (loot.Unique.Base)
                {
                    loot.Base.Clear();
                }

                if (loot.Unique.Difficulty)
                {
                    loot.Difficulty.Clear();
                }

                if (loot.Unique.Default)
                {
                    loot.Default.Clear();
                }

                while (loot.Tables.Count < loot.Amount && source.Count > 0)
                {
                    LootItem ti = source.GetRandom();

                    source.Remove(ti);

                    if (ti.HasProbability())
                    {
                        if (!loot.Options.AllowDuplicates)
                        {
                            remove(ti);
                        }

                        AddToLoot(loot, ti);
                    }

                    if (loot.Options.EnforceProbability && ti.probability < 1f)
                    {
                        remove(ti);
                    }

                    if (source.Count == 0 && ++iterations < loot.Tables.Count)
                    {
                        refill();
                    }
                }
            }

            private static bool AddToLoot(LootProfile loot, LootItem lootItem)
            {
                if (lootItem.definition == null)
                {
                    Puts("Invalid shortname in loot table: {0} for {1}", lootItem.shortname, loot.BaseName);
                    return false;
                }

                LootItem ti = lootItem.Clone();

                int amount = ti.amountMin < ti.amount ? Core.Random.Range(ti.amountMin, ti.amount + 1) : ti.amount;

                if (amount <= 0)
                {
                    return false;
                }

                int[] stacks = loot.Unique.Stacks ? GetStacks(amount, ti.stacksize > 0 ? ti.stacksize : ti.definition.stackable) : (ti.stacksize > 0 ? GetStacks(amount, ti.stacksize) : new int[1] { amount });

                if (stacks.Length == 0)
                {
                    return false;
                }

                if (loot.Options.Dynamic && stacks.Length > 1)
                {
                    loot.Amount += stacks.Length - 1;
                }

                foreach (int stack in stacks)
                {
                    loot.Tables.Add(new(ti.shortname, stack, stack, ti.skin, ti.isBlueprint, ti.probability, ti.stacksize, ti.name, ti.text, ti.isModified, ti.hasPriority, ti.slots) { isSplit = stacks.Length > 1 });
                }

                return true;
            }

            private static int[] GetStacks(int amount, int maxStack)
            {
                if (amount <= 0) return Array.Empty<int>();
                if (maxStack <= 0) return new int[1] { amount };
                int size = (amount + maxStack - 1) / maxStack;
                int[] stacks = new int[size];
                for (int i = 0; i < size; i++)
                {
                    stacks[i] = Math.Min(amount, maxStack);
                    amount -= stacks[i];
                }
                return stacks;
            }


            private List<string> BuildingMaterials = new()
            {
                "hq.metal.ore", "metal.refined", "metal.fragments", "metal.ore", "stones", "sulfur.ore", "sulfur", "wood"
            };

            private Item CreateItem(LootItem ti, int amount)
            {
                if (amount <= 0 || ti.definition == null)
                {
                    return null;
                }

                Item item;
                if (ti.isBlueprint && ti.definition.Blueprint != null)
                {
                    item = ItemManager.Create(Workbench.GetBlueprintTemplate());
                    item.blueprintTarget = ti.definition.itemid;
                    item.amount = amount;
                }
                else
                {
                    item = ItemManager.Create(ti.definition, amount, 0uL);
                    item.skin = GetItemSkin(ti.definition, SkinType.Loot, ti.skin, config.Skins.Loot.Stackable, config.Skins.Loot.NonStackable, config.Skins.Loot.Random, config.Skins.Loot.Workshop, config.Skins.Loot.Imported, config.Skins.Loot.ApprovedOnly, item.MaxStackable());
                }

                if (!string.IsNullOrWhiteSpace(ti.name))
                {
                    item.name = ti.name;
                }

                if (!string.IsNullOrWhiteSpace(ti.text) && !BuildingMaterials.Contains(ti.shortname))
                {
                    item.text = ti.text;
                }

                var e = item.GetHeldEntity();

                if (e.IsNetworked())
                {
                    e.skinID = item.skin;
                    e.SendNetworkUpdate();
                }

                if (ti.slots != null)
                {
                    ti.slots.TryAdd(item);
                }

                item.MarkDirty();

                return item;
            }

            private void SetupSellOrders()
            {
                if (!config.Settings.Management.Inherit.Exists("vendingmachine".Contains))
                {
                    return;
                }

                vms.RemoveAll(IsContainerKilled);

                foreach (var vm in vms)
                {
                    vm.InstallDefaultSellOrders();
                    foreach (Item item in vm.inventory.itemList)
                    {
                        if (vm.sellOrders.sellOrders.Count < 8)
                        {
                            ItemDefinition itemToSellDef = ItemManager.FindItemDefinition(item.info.itemid);
                            ItemDefinition currencyDef = ItemManager.FindItemDefinition(-932201673);

                            if (!(itemToSellDef == null) && !(currencyDef == null))
                            {
                                int itemToSellAmount = Mathf.Clamp(item.amount, 1, itemToSellDef.stackable);

                                vm.sellOrders.sellOrders.Add(new()
                                {
                                    ShouldPool = false,
                                    itemToSellID = item.info.itemid,
                                    itemToSellAmount = itemToSellAmount,
                                    currencyID = -932201673,
                                    currencyAmountPerItem = 999999,
                                    currencyIsBP = true,
                                    itemToSellIsBP = item.IsBlueprint()
                                });

                                vm.RefreshSellOrderStockLevel(itemToSellDef);
                            }
                        }
                    }

                    vm.SetFlagLocal(BaseEntity.Flags.Reserved4, config.Settings.Management.AllowBroadcasting);
                    vm.FullUpdate();
                }
            }

            private bool MoveFood(Item item)
            {
                if (!config.Settings.Management.Food || fridges.Count == 0 || item.info.category != ItemCategory.Food || config.Settings.Management.Foods.Exists(item.info.shortname.Contains))
                {
                    return false;
                }

                fridges.RemoveAll(IsContainerKilled);

                if (fridges.Count > 1)
                {
                    Shuffle(fridges);
                }

                return fridges.Exists(x => item.MoveToContainer(x.inventory, -1, true));
            }

            private int weaponsInBox, weaponsOnRack;
            private bool MoveToRack(Item item)
            {
#if OXIDE_PUBLICIZED || CARBON
                if (config.Settings.Management.DivideRackLoot && weaponsOnRack >= weaponsInBox || item.info.category != ItemCategory.Weapon || weaponRacks.Count - weaponRacks.RemoveAll(IsKilled) <= 0)
                {
                    return false;
                }
                if (weaponRacks.Count > 1)
                {
                    weaponRacks.Sort((a, b) => a.inventory.itemList.Count.CompareTo(b.inventory.itemList.Count));
                }
                WeaponRack rack = weaponRacks[0];
                WorldModelRackMountConfig conf = WorldModelRackMountConfig.GetForItemDef(item.info);
                if (conf == null || !rack.CanAcceptWeaponType(conf))
                {
                    return false;
                }
                BasePlayer target = BasePlayer.bots.FirstOrDefault(bot => bot != null);
                if (target == null)
                {
                    return false;
                }
                for (int y = 0; y < rack.GridCellCountY; y++)
                {
                    for (int x = 0; x < rack.GridCellCountX; x++)
                    {
                        Vector2Int position = new(x, y);
                        int gridCellIndex = rack.GetBestPlacementCellIndex(position, conf, rotation: 0, ignoreSlot: null);
                        if (gridCellIndex != -1 && rack.GetWeaponAtIndex(gridCellIndex) == null && rack.MountWeapon(item, target, gridCellIndex, 0, true))
                        {
                            weaponsOnRack++;
                            return true;
                        }
                    }
                }
#endif
                return false;
            }

            private bool MoveToBBQ(Item item)
            {
                if (!config.Settings.Management.Food || ovens.Count == 0 || item.info.category != ItemCategory.Food || !IsCookable(item.info) || config.Settings.Management.Foods.Exists(item.info.shortname.Contains))
                {
                    return false;
                }

                ovens.RemoveAll(IsContainerKilled);

                if (ovens.Count > 1)
                {
                    Shuffle(ovens);
                }

                return ovens.Exists(oven => oven.ShortPrefabName.Contains("bbq") && item.MoveToContainer(oven.inventory, -1, true));
            }

            private bool MoveToCupboard(Item item)
            {
                if (!config.Settings.Management.Cupboard || !privSpawned || item.info.category != ItemCategory.Resources || config.Loot.ExcludeFromCupboard.Contains(item.info.shortname))
                {
                    return false;
                }

                if (config.Settings.Management.Cook && ovens.Count > 0 && item.info.shortname.Equals("crude.oil") && SplitIntoFurnaces(ovens, item))
                {
                    return true;
                }

                if (config.Settings.Management.Cook && item.info.shortname.EndsWith(".ore") && MoveToOven(item))
                {
                    return true;
                }

                if (!IsContainerKilled(priv) && item.MoveToContainer(priv.inventory, -1, true))
                {
                    privHadLoot = true;
                    return true;
                }

                return false;
            }

            private bool IsCookable(ItemDefinition def)
            {
                if (def.shortname.EndsWith(".cooked") || def.shortname.EndsWith(".burned") || def.shortname.EndsWith(".spoiled") || def.shortname == "lowgradefuel")
                {
                    return false;
                }

                return def.shortname == "wood" || def.shortname == "crude.oil" || def.HasComponent<ItemModCookable>();
            }

            private bool MoveToOven(Item item)
            {
                if (!config.Settings.Management.Cook || ovens.Count == 0 || !IsCookable(item.info))
                {
                    return false;
                }

                ovens.RemoveAll(IsContainerKilled);

                if (ovens.Count > 1)
                {
                    Shuffle(ovens);
                }

                if ((item.info.shortname.EndsWith(".ore") || item.info.shortname.Equals("crude.oil")) && item.skin == 0 && SplitIntoFurnaces(ovens, item))
                {
                    return true;
                }

                foreach (var oven in ovens)
                {
                    if (oven.ShortPrefabName.Contains("bbq") ||
                        (item.info.shortname == "crude.oil" && !oven.IsMaterialInput(item)) ||
                        (item.info.shortname.EndsWith(".ore") && !oven.IsMaterialInput(item)) ||
                        (item.info.shortname == "lowgradefuel" && !oven.IsBurnableItem(item))) continue;

                    if (item.MoveToContainer(oven.inventory, -1, true))
                    {
                        if (!oven.IsOn() && oven.FindBurnable() != null)
                        {
                            using var update = oven.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                            update.Set(BaseEntity.Flags.On, true);
                        }

                        if (oven.IsOn() && !item.HasFlag(global::Item.Flag.OnFire))
                        {
                            item.SetFlag(global::Item.Flag.OnFire, true);
                            item.MarkDirty();
                        }

                        return true;
                    }
                }

                return false;
            }

            private bool SplitIntoFurnaces(List<BaseOven> ovens, Item item)
            {
                List<(BaseOven, int)> furnaces = new();
                foreach (var oven in ovens)
                {
                    int position = -1;

                    try { position = oven.GetIdealSlot(null, null, item); } catch { }

                    if (position != -1)
                    {
                        furnaces.Add(new(oven, position));
                    }
                }
                if (item.amount <= 0 || furnaces.Count == 0)
                {
                    return false;
                }
                int size = item.amount / furnaces.Count;
                foreach (var (furnace, position) in furnaces)
                {
                    if (size > 0 && size < item.amount && item.SplitItem(size) is Item split)
                    {
                        if (!split.MoveToContainer(furnace.inventory, position, true, true))
                        {
                            item.amount += split.amount;
                            item.MarkDirty();
                            split.Remove();
                            return false;
                        }
                    }
                    else if (!item.MoveToContainer(furnace.inventory, position, true, true))
                    {
                        return false;
                    }
                    if (furnace is ElectricOven eo && eo.spawnedIo.Get(true) is IOEntity io && !io.IsPowered())
                    {
                        io.Invoke(() =>
                        {
                            io.UpdateHasPower(25, 0);
                            eo.StartCooking();
                        }, 0.1f);
                    }
                    if (config.Weapons.Furnace > 0 && furnace.fuelType != null && !(furnace is ElectricOven) && furnace.inventory.GetSlot(0) == null)
                    {
                        ItemManager.Create(furnace.fuelType, config.Weapons.Furnace).MoveToContainer(furnace.inventory, 0);

                        if (!BaseOven.cookQueue.Contains(furnace))
                        {
                            furnace.Invoke(furnace.StartCooking, 0.2f);
                        }
                    }
                }
                return true;
            }

            private bool IsLockerItem(ItemDefinition def)
            {
                if (def.shortname.Contains("explosive") || def.shortname.Contains("rocket"))
                {
                    return false;
                }
                if (config.Settings.Management.Food && def.category == ItemCategory.Food && !config.Settings.Management.Foods.Exists(def.shortname.Contains))
                {
                    return fridges.Count == 0;
                }
                return def.category == ItemCategory.Attire || def.category == ItemCategory.Ammunition || def.category == ItemCategory.Medical || def.category == ItemCategory.Weapon;
            }

            private bool MoveToLocker(Item item)
            {
                if (!config.Settings.Management.Lockers || lockers.Count == 0 || !IsLockerItem(item.info))
                {
                    return false;
                }

                lockers.RemoveAll(IsContainerKilled);

                if (config.Settings.Management.DivideLockerLoot)
                {
                    if (itemAmountSpawned % _containers.Count != 0)
                    {
                        return false;
                    }

                    lockers.Sort((a, b) => a.inventory.itemList.Count.CompareTo(b.inventory.itemList.Count));
                }

                return lockers.Exists(locker => MoveToLocker(item, locker));
            }

            private bool MoveToLocker(Item item, Locker locker)
            {
                try
                {
                    int position = locker.GetIdealSlot(null, null, item);

                    if (position != int.MinValue)
                    {
                        return item.MoveToContainer(locker.inventory, position, true);
                    }
                }
                catch { }

                return false;
            }

            private void CheckExpansionSettings()
            {
                if (!config.Settings.ExpansionMode || !Instance.DangerousTreasures.CanCall())
                {
                    return;
                }
                var containers = _containers.Where(x => x.ShortPrefabName == "box.wooden.large");
                if (containers.Count > 0)
                {
                    Instance.DangerousTreasures?.Call("API_SetContainer", containers.GetRandom(), M_RADIUS, !Options.NPC.Enabled || Options.NPC.UseExpansionNpcs, 512);
                }
            }

            private bool ToggleNpcMinerHat(HumanoidNPC npc, bool state)
            {
                if (npc.IsKilled() || npc.inventory == null || npc.IsDead())
                {
                    return false;
                }

                var slot = npc.inventory.FindItemByItemName("hat.miner");

                if (slot == null)
                {
                    return false;
                }

                if (state && slot.contents != null)
                {
                    slot.contents.AddItem(ItemManager.FindItemDefinition("lowgradefuel"), 50);
                }

                slot.SwitchOnOff(state);
                npc.inventory.ServerUpdate(0f);
                return true;
            }

            private bool HasConnectedInput(IOEntity io)
            {
                if (io == null || io.inputs == null)
                {
                    return false;
                }

                foreach (var input in io.inputs)
                {
                    var e = input?.connectedTo?.Get(true);

                    if (e.IsValid())
                    {
                        return true;
                    }
                }

                return false;
            }

            public void ToggleLights()
            {
                bool state = config.Settings.Management.AlwaysLights || TOD_Sky.Instance?.IsDay == false;

                if (lights?.Count > 0)
                {
                    foreach (var io in lights)
                    {
                        if (io.IsKilled() || io.HasConnections()) continue;

                        int inputAmount = state ? Math.Max(35, io.ConsumptionAmount()) : 0;
                        bool updatedInput = false;
                        var inputs = io.inputs;

                        if (io is StrobeLight strobeLight && state)
                        {
                            strobeLight.lifeTimeSeconds = float.MaxValue;
                        }

                        if (inputs != null)
                        {
                            for (int inputSlot = 0; inputSlot < inputs.Length; inputSlot++)
                            {
                                var input = inputs[inputSlot];
                                if (input == null || !input.mainPowerSlot) continue;
                                io.UpdateFromInput(inputAmount, inputSlot);
                                updatedInput = true;
                            }

                            if (!updatedInput)
                            {
                                for (int inputSlot = 0; inputSlot < inputs.Length; inputSlot++)
                                {
                                    if (inputs[inputSlot] == null) continue;
                                    io.UpdateFromInput(inputAmount, inputSlot);
                                    updatedInput = true;
                                    break;
                                }
                            }
                        }

                        if (!updatedInput)
                        {
                            io.currentEnergy = inputAmount;
                            io.UpdateHasPower(inputAmount, 0);
                            io.IOStateChanged(inputAmount, 0);
                        }

                        using var update = io.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                        update.Set(BaseEntity.Flags.On, state);
                    }
                }

                if (ovens?.Count > 0)
                {
                    foreach (var oven in ovens)
                    {
                        if (IsContainerKilled(oven)) continue;
                        if (state && (oven.ShortPrefabName.Contains("furnace") && oven.inventory.IsEmpty())) continue;
                        if (!state && (oven.ShortPrefabName.Contains("furnace") && BaseOven.cookQueue.Contains(oven))) continue;
                        if (config.Settings.Management.IgnoredLights.Count > 0 && config.Settings.Management.IgnoredLights.Exists(oven.ShortPrefabName.Contains)) continue;
                        using var update = oven.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                        update.Set(BaseEntity.Flags.On, state);
                    }
                }

                if (npcs?.Count > 0)
                {
                    foreach (var npc in npcs)
                    {
                        if (npc.IsKilled()) continue;
                        ToggleNpcMinerHat(npc, state);
                    }
                }
            }

            public void Undo()
            {
                if (IsOpened)
                {
                    IsOpened = false;

                    if (DespawnMinutes > 0f)
                    {
                        UpdateDespawnDateTime(DespawnMinutes);

                        if (config.EventMessages.ShowWarning)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                if (!IsRaider(target) && target.HasPermission("raidablebases.limitedannouncements")) continue;
                                QueueNotification(target, "DestroyingBaseAt", FormatGridReference(target, Location), DespawnMinutes);
                            }
                        }
                    }
                    else
                    {
                        Despawn();
                    }
                }
            }

            public bool Any(ulong userid, bool checkAllies = true)
            {
                if (ownerId != 0 && ownerId == userid) return true;
                if (!raiders.TryGetValue(userid, out var ri)) return false;
                return ri.IsParticipant || checkAllies && ri.IsAlly;
            }

            public ulong GetItemSkin(ItemDefinition def, SkinType skinType, ulong defaultSkin, bool stackable, bool nonstackable, bool random, bool workshop, bool importedworkshop, bool approved, int stacksize)
            {
                ulong skin = defaultSkin;

                if (def.shortname != "explosive.satchel" && def.shortname != "grenade.f1" && skin == 0uL)
                {
                    if (stackable && stacksize > 1 && _shortnameToSkin.TryGetValue(def.shortname, out var dict) && dict.TryGetValue(skinType, out var skin2))
                    {
                        return skin2;
                    }

                    if (nonstackable && stacksize == 1 && _shortnameToSkin.TryGetValue(def.shortname, out var dict2) && dict2.TryGetValue(skinType, out var skin3))
                    {
                        return skin3;
                    }

                    var si = GetItemSkins(def, approved);
                    var skins = GetItemSkins(si, random, workshop, importedworkshop);

                    if (skins.Count != 0)
                    {
                        if (!_shortnameToSkin.TryGetValue(def.shortname, out dict))
                        {
                            _shortnameToSkin[def.shortname] = dict = new();
                        }
                        dict[skinType] = skin = skins.GetRandom();
                    }
                }

                return skin;
            }

            public SkinInfo GetItemSkins(ItemDefinition def, bool approvedOnly)
            {
                if (!Instance.Skins.TryGetValue(def.shortname, out var si))
                {
                    Instance.Skins[def.shortname] = si = new();

                    if (!config.BlockPaidContent && !def.skins.IsNullOrEmpty())
                    {
                        foreach (var skin in def.skins)
                        {
                            if (IsBlacklistedSkin(def, skin.id))
                            {
                                continue;
                            }
                            var id = Convert.ToUInt64(skin.id);
                            si.skins.Add(id);
                            si.allSkins.Add(id);
                        }
                    }

                    if (Instance.ImportedWorkshopSkins.SkinList.TryGetValue(def.shortname, out var value) && !value.IsNullOrEmpty())
                    {
                        foreach (var skin in value)
                        {
                            if (IsBlacklistedSkin(def, (int)skin))
                            {
                                continue;
                            }
                            if (approvedOnly && !IsApproved(def, skin))
                            {
                                continue;
                            }
                            si.importedWorkshopSkins.Add(skin);
                            si.allSkins.Add(skin);
                        }
                    }

                    var sp = Instance.skinsPlugin.Skins.FindAll(x => x.Shortname == def.shortname);
                    if (sp != null && sp.Count != 0)
                    {
                        foreach (var item in sp)
                        {
                            foreach (var skin in item.Skins)
                            {
                                if (IsBlacklistedSkin(def, (int)skin))
                                {
                                    continue;
                                }
                                if (approvedOnly && !IsApproved(def, skin))
                                {
                                    continue;
                                }
                                si.importedWorkshopSkins.Add(skin);
                                si.allSkins.Add(skin);
                            }
                        }
                    }

                    if (!config.BlockPaidContent && !def.skins2.IsNullOrEmpty())
                    {
                        foreach (var skin in def.skins2)
                        {
                            if (skin == null || IsBlacklistedSkin(def, (int)skin.WorkshopId))
                            {
                                continue;
                            }
                            if (!si.workshopSkins.Contains(skin.WorkshopId))
                            {
                                si.workshopSkins.Add(skin.WorkshopId);
                                si.allSkins.Add(skin.WorkshopId);
                            }
                        }
                    }
                }

                return si;
            }

            private bool IsBlacklistedSkin(ItemDefinition def, int num)
            {
                var skinId = ItemDefinition.FindSkin(def.isRedirectOf?.itemid ?? def.itemid, num);
                var dirSkin = def.isRedirectOf == null ? def.skins.FirstOrDefault(x => (ulong)x.id == skinId) : def.isRedirectOf.skins.FirstOrDefault(x => (ulong)x.id == skinId);
                var itemSkin = (dirSkin.id == 0) ? null : (dirSkin.invItem as ItemSkin);
                return itemSkin?.Redirect != null || def.isRedirectOf != null;
            }

            private bool IsApproved(ItemDefinition def, ulong skin)
            {
                if (def.skins != null && Array.Exists(def.skins, x => (ulong)x.id == skin)) return true;
                if (def.skins2 != null && Array.Exists(def.skins2, x => x.WorkshopId == skin)) return true;
                return false;
            }

            private List<ulong> GetItemSkins(SkinInfo si, bool random, bool workshop, bool importedworkshop)
            {
                List<ulong> skins = new();

                if (random && si.skins.Count > 0)
                {
                    skins.Add(si.skins.GetRandom());
                }

                if (workshop && si.workshopSkins.Count > 0)
                {
                    skins.Add(si.workshopSkins.GetRandom());
                }

                if (importedworkshop && si.importedWorkshopSkins.Count > 0)
                {
                    skins.Add(si.importedWorkshopSkins.GetRandom());
                }

                return skins;
            }

            private bool SetItemSkin(List<ulong> skins, SkinInfo si, BaseEntity entity, bool unique)
            {
                Shuffle(skins);
                foreach (ulong skin in skins)
                {
                    if (!si.allSkins.Contains(skin))
                    {
                        continue;
                    }
                    if (unique)
                    {
                        _prefabToSkin[entity.prefabID] = skin;
                    }
                    entity.skinID = skin;
                    entity.SendNetworkUpdate();
                    return true;
                }
                return false;
            }

            public bool IsAlly(ulong playerId, ulong targetId, AlliedType type = AlliedType.All, string arg = "IsMemberOrAlly") => type switch
            {
                AlliedType.All or AlliedType.Team when RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out var team) && team.members.Contains(targetId) => true,
                AlliedType.All or AlliedType.Clan when Instance.Clans != null && Convert.ToBoolean(Instance.Clans?.Call(arg, playerId.ToString(), targetId.ToString())) => true,
                AlliedType.All or AlliedType.Friend when Instance.Friends != null && Convert.ToBoolean(Instance.Friends?.Call("AreFriends", playerId.ToString(), targetId.ToString())) => true,
                _ => false
            };

            public bool IsAlly(BasePlayer player)
            {
                if (ownerId.IsSteamId() && player.userID != ownerId && !CanBypass(player))
                {
                    Raider ri = GetRaider(player);

                    return ri.IsAlly || (ri.IsAlly = IsAlly(player.userID, ownerId));
                }

                return true;
            }

            public void StopUsingWeapon(BasePlayer player)
            {
                if (!player.svActiveItemID.IsValid)
                {
                    return;
                }

                if (config.Settings.BlockedWeapons.Count > 0)
                {
                    config.Settings.BlockedWeapons.ForEach(weapon =>
                    {
                        if (!string.IsNullOrWhiteSpace(weapon))
                        {
                            StopUsingWeapon(player, weapon);
                        }
                    });
                }

                if (Options.Siege.Only)
                {
                    Item item = player.GetActiveItem();

                    if (item != null && !item.info.IsAllowedInEra(EraRestriction.Default, Era.Primitive))
                    {
                        StopUsingWeapon(player, item);
                        return;
                    }
                }

                if (config.Settings.NoWizardry && Instance.Wizardry.CanCall())
                {
                    StopUsingWeapon(player, "knife.bone");
                }

                if (config.Settings.NoArchery && Instance.Archery.CanCall())
                {
                    StopUsingWeapon(player, "bow.compound", "bow.hunting", "crossbow");
                }
            }

            public void StopUsingWeapon(BasePlayer player, params string[] weapons)
            {
                Item item = player.GetActiveItem();

                if (item == null || !weapons.Contains(item.info.shortname))
                {
                    return;
                }

                StopUsingWeapon(player, item);
            }

            private void StopUsingWeapon(BasePlayer player, Item item)
            {
                if (!item.MoveToContainer(player.inventory.containerMain))
                {
                    item.DropAndTossUpwards(player.GetDropPosition() + player.transform.forward, 2f);
                    Message(player, "TooPowerfulDrop");
                }
                else Message(player, "TooPowerful");
            }

            public BackpackData AddBackpack(DroppedItemContainer container, ulong playerSteamID, BasePlayer player)
            {
                int index = backpacks.FindIndex(x => x.userid == playerSteamID);
                BackpackData backpack;

                if (index == -1)
                {
                    backpack = Pool.Get<BackpackData>();
                    if (player != null)
                    {
                        backpack._player = player;
                        backpack.userid = player.userID;
                    }
                    else backpack.userid = playerSteamID;
                    backpacks.Add(backpack);
                }
                else backpack = backpacks[index];

                if (!backpack.containers.Contains(container))
                {
                    backpack.containers.Add(container);
                }

                return backpack;
            }

            private void RemoveParentFromEntitiesOnElevators()
            {
                using var tmp = FindEntitiesOfType<BaseEntity>(Location, ProtectionRadius);
                foreach (var e in tmp)
                {
                    if ((e is PlayerCorpse || e is DroppedItemContainer) && e.HasParent())
                    {
                        e.SetParent(null, false, true);
                    }
                }
            }

            public bool EjectBackpack(BackpackData backpack, bool bypass)
            {
                if (backpack.IsEmpty)
                {
                    return true;
                }

                if (!bypass && (!ownerId.IsSteamId() || Any(backpack.userid) || backpack.player.IsNetworked() && IsAlly(backpack.player)))
                {
                    return false;
                }

                backpack.containers.RemoveAll(container =>
                {
                    if (!container.IsKilled())
                    {
                        EjectContainer(container, backpack.userid);
                    }

                    return true;
                });

                return true;
            }

            private void EjectBackpackNotice(BasePlayer player, Vector3 position)
            {
                if (!player.IsOnline())
                {
                    return;
                }
                if (player.IsDead() || player.IsSleeping())
                {
                    player.Invoke(() => EjectBackpackNotice(player, position), 1f);
                    return;
                }
                QueueNotification(player, "EjectedYourCorpse");
                if (config.Settings.Management.DrawTime > 0)
                {
                    AdminCommand(player, () => DrawText(player, config.Settings.Management.DrawTime, Color.red, position, mx("YourCorpse", player.UserIDString)));
                }
            }

            private void EjectSleepers()
            {
                if (!config.Settings.Management.EjectSleepers || Type == RaidableType.None)
                {
                    return;
                }
                using var tmp = FindEntitiesOfType<BasePlayer>(Location, ProtectionRadius, Layers.Mask.Player_Server);
                foreach (var player in tmp)
                {
                    if (player.IsSleeping() && !player.IsBuildingAuthed())
                    {
                        RemovePlayer(player, Location, ProtectionRadius, Type);
                    }
                }
            }

            public Vector3 GetEjectLocation(Vector3 a, float distance, Vector3 target, float radius, bool towardsZero, bool setHeight)
            {
                Vector3 direction = towardsZero ? Vector3.zero.XZ3D() - target.XZ3D() : a.XZ3D() - target.XZ3D();

                if (direction.sqrMagnitude <= 0.001f)
                {
                    direction = a.XZ3D() - target.XZ3D();

                    if (direction.sqrMagnitude <= 0.001f)
                    {
                        direction = Vector3.forward;
                    }
                }

                direction.Normalize();

                Vector3 position = target + direction * (radius + distance);

                if (towardsZero)
                {
                    float step = Mathf.Max(distance, M_RADIUS);

                    for (int i = 0; i < 4 && WaterLevel.GetOverallWaterDepth(position, waves: false, volumes: false) > 0.5f; i++)
                    {
                        position += direction * step;
                    }
                }

                if (setHeight)
                {
                    Vector3 origin = position;
                    origin.y = Instance.MaxTerrainY + 48f;

                    if (Physics.Raycast(origin, Vector3.down, out var hit, Mathf.Infinity, targetMask2, QueryTriggerInteraction.Ignore))
                    {
                        position.y = Mathf.Max(hit.point.y, TerrainMeta.WaterMap.GetHeight(hit.point), WaterSystem.OceanLevel) + 0.75f;
                    }
                    else
                    {
                        position.y = Mathf.Max(TerrainMeta.HeightMap.GetHeight(position), TerrainMeta.WaterMap.GetHeight(position), WaterSystem.OceanLevel) + 0.75f;
                    }
                }

                return position;
            }

            public bool RemovePlayer(BasePlayer player, Vector3 a, float radius, RaidableType type, bool special = false, bool towardsZero = false, float distance = 10f)
            {
                if (player.IsNull() || !player.IsHuman() || type == RaidableType.None && !player.IsSleeping())
                {
                    return false;
                }

                bool jetpack = IsWearingJetpack(player);

                if (special || jetpack)
                {
                    if (player.GetMounted() is BaseMountable b)
                    {
                        b.DismountPlayer(player, true);
                    }
                    else player.DismountObject();
                }

                if (player.GetMounted() is BaseMountable m)
                {
                    using var players = GetMountedPlayers(m);
                    return EjectMountable(m, players, a, radius, jetpack);
                }

                var parent = player.GetParentEntity();
                if (parent != null && IsCustomEntity(parent))
                {
                    return Eject(parent, Location, ProtectionRadius + 15f, false);
                }

                var position = GetEjectLocation(player.transform.position, distance, a, radius, towardsZero, true);

                if (player.IsFlying)
                {
                    position.y = player.transform.position.y;
                }

                player.Teleport(position);
                player.SendNetworkUpdateImmediate();

                return true;
            }

            public void Teleport(BasePlayer player)
            {
                var position = GetEjectLocation(player.transform.position, 10f, Location, ProtectionRadius, false, true);
                TeleportExceptions.Add(player.userID);
                player.Teleport(position);
                player.SendNetworkUpdateImmediate();
            }

            public void DismountAllPlayers(BaseMountable m)
            {
                using var targets = GetMountedPlayers(m);
                foreach (var target in targets)
                {
                    if (target.IsNull()) continue;

                    m.DismountPlayer(target, true);

                    target.EnsureDismounted();
                }
            }

            public static PooledList<BasePlayer> GetMountedPlayers(HotAirBalloon m)
            {
                var players = FindEntitiesOfType<BasePlayer>(m.CenterPoint(), 1.75f, Layers.Mask.Player_Server);
                players.RemoveAll(player => !player.IsHuman() || player.GetParentEntity() != m);
                return players;
            }

            public static PooledList<BasePlayer> GetMountedPlayers(BaseMountable m)
            {
                BaseVehicle vehicle = m.HasParent() ? m.VehicleParent() : m as BaseVehicle;
                PooledList<BasePlayer> players = DisposableList<BasePlayer>();

                if (vehicle == null)
                {
                    BasePlayer player = m.GetMounted();
                    if (player != null)
                    {
                        players.Add(player);
                    }
                }
                else vehicle.GetMountedPlayers(players);

                players.RemoveAll(x => !x.IsHuman());
                return players;
            }

            public static bool AnyMounted(BaseMountable m)
            {
                BaseVehicle vehicle = m.HasParent() ? m.VehicleParent() : m as BaseVehicle;

                if (vehicle == null)
                {
                    return m.GetMounted() != null;
                }

                return vehicle.AnyMounted();
            }

            private bool CanEject(PooledList<BasePlayer> players)
            {
                foreach (var player in players)
                {
                    if (!intruders.Contains(player.userID) && CanEject(player))
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool CanEject(BasePlayer target)
            {
                if (target.IsNull() || target.userID == ownerId)
                {
                    return false;
                }

                if (CannotEnter(target, false))
                {
                    return true;
                }

                if (CanEjectEnemy() && !GetRaider(target).IsAlly && !IsAlly(target))
                {
                    Message(target, "OnPlayerEntryRejected");
                    return true;
                }

                return false;
            }

            public bool CanEjectEnemy()
            {
                if (ownerId.IsSteamId()) return AllowPVP ? Options.EjectLockedPVP : Options.EjectLockedPVE;
                return false;
            }

            private bool CannotEnter(BasePlayer target, bool justEntered)
            {
                bool special = false;

                if (GetRaider(target).IsAllowed)
                {
                    if (IsBanned(target))
                    {
                        return RemovePlayer(target, Location, ProtectionRadius, Type);
                    }
                }
                else if (Exceeds(target) || IsBanned(target) || IsHogging(target) || (special = justEntered && Teleported(target)))
                {
                    return RemovePlayer(target, Location, ProtectionRadius, Type, special);
                }

                return false;
            }

            public bool IsControlledMount(BaseEntity m)
            {
                if (Options.Mounts.ControlledMounts)
                {
                    return false;
                }

                if (m is BaseChair chair)
                {
                    bool legacy = chair.legacyDismount;
                    chair.legacyDismount = true;
                    DismountAllPlayers(chair);
                    chair.legacyDismount = legacy;
                    return true;
                }

                if (!(m.GetParentEntity() is BaseEntity parent) || parent is HitchTrough.IHitchable)
                {
                    return false;
                }

                if (parent.GetType().Name.Contains("Controller"))
                {
                    DismountAllPlayers(m as BaseMountable);

                    return true;
                }

                return false;
            }

            private bool IsBlockingCampers(ModularCar car)
            {
                if (!Options.Mounts.Campers || car.AttachedModuleEntities == null)
                {
                    return false;
                }

                foreach (var module in car.AttachedModuleEntities)
                {
                    if (module is VehicleModuleCamper)
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool TryRemoveMountable(BaseEntity m, PooledList<BasePlayer> players)
            {
                if (m.IsNull() || Type == RaidableType.None || m.GetParentEntity() is TrainCar || IsControlledMount(m) || Entities.Contains(m))
                {
                    return false;
                }

                if (m is HotAirBalloon && (Options.Mounts.HotAirBalloon || CanEject(players)))
                {
                    return Eject(m, Location, ProtectionRadius, false);
                }

                if (Options.Mounts.Siege && !Options.Siege.Only)
                {
                    if (m is BaseSiegeWeapon or ConstructableEntity)
                    {
                        return Eject(m, Location, ProtectionRadius, true);
                    }
                }

                if (m is BaseMountable m2)
                {
                    bool jetpack = IsJetpack(m2);
                    bool carpet = !jetpack && m.ObjectName() == "FlyingCarpet";

                    if (ShouldEject(Options.Mounts, m, jetpack, carpet) || CanEject(players))
                    {
                        return EjectMountable(m2, players, Location, ProtectionRadius, jetpack || carpet);
                    }
                }

                return false;
            }

            private bool ShouldEject(ManagementMountableSettings ms, BaseEntity m, bool jetpack, bool carpet) => m switch
            {
                _ when IsInvisibleChair(m) => ms.Invisible,
                _ when jetpack => ms.Jetpacks,
                _ when carpet => ms.FlyingCarpet,
                global::Parachute _ => ms.Parachutes,
                BaseSiegeWeapon or ConstructableEntity => ms.Siege && !Options.Siege.Only,
                Tugboat => ms.Tugboats,
                Bike => ms.Bikes,
                BaseBoat => ms.Boats,
                BasicCar => ms.BasicCars,
                ModularCar car => ms.ModularCars || IsBlockingCampers(car),
                CH47Helicopter => ms.CH47,
                HitchTrough.IHitchable => ms.Hitchable,
                ScrapTransportHelicopter => ms.Scrap,
                AttackHelicopter => ms.AttackHelicopters,
                Minicopter => ms.MiniCopters,
                Snowmobile => ms.Snowmobile,
                StaticInstrument => ms.Pianos,
                _ => ms.Other
            };

            public static bool IsWearingJetpack(BasePlayer player) => !player.IsNull() && player.GetMounted() is BaseMountable m && IsJetpack(m);

            public static bool IsJetpack(BaseMountable m) => (m.ShortPrefabName == "testseat" || m.ShortPrefabName == "standingdriver") && m.GetParentEntity() is DroppedItem;

            public static bool IsInvisibleChair(BaseEntity m) => m.skinID == 1169930802;

            private static bool IsAirborne(BaseEntity m, float tolerance = 0.25f)
            {
                float waterY = TerrainMeta.WaterMap.GetHeight(m.transform.position);
                if (m.transform.position.y <= waterY + tolerance) return false;
                Vector3 start = new(m.bounds.center.x, m.bounds.min.y + 0.05f, m.bounds.center.z);
                float length = tolerance + 0.05f;
                return !Physics.Raycast(start, Vector3.down, length, Layers.Terrain | Layers.Construction | Layers.Solid, QueryTriggerInteraction.Ignore);
            }

            private static bool IsFlying(BasePlayer player)
            {
                return player != null && player.modelState != null && !player.modelState.onground && TerrainMeta.HeightMap.GetHeight(player.transform.position) < player.transform.position.y - 1f;
            }

            public bool EjectMountable(BaseEntity m, PooledList<BasePlayer> players, Vector3 position, float radius, bool special)
            {
                m = GetParentEntity(m);
                if (m is TrainCar { OwnerID: 0uL })
                {
                    return false;
                }

                var j = TerrainMeta.HeightMap.GetHeight(m.transform.position) - m.transform.position.y;
                var distance = m switch { HitchTrough.IHitchable => j > 5f ? j + 5f : 5f, _ => j > 5f ? j + 10f : 10f };
                var target = GetEjectLocation(m.transform.position, distance, position, radius, false, false);

                if (m is BaseHelicopter || players.Exists(IsFlying))
                {
                    target.y = Mathf.Max(m.transform.position.y, SpawnsController.GetSpawnHeight(target)) + 5f;
                }
                else if (m is Drone)
                {
                    target.y = Mathf.Max(target.y + 15f, position.y + radius);
                }
                else target.y = SpawnsController.GetSpawnHeight(target) + m switch { ModularCarSeat or ModularCar => 1f, _ => 0.5f };

                if (special)
                {
                    target.y += 15f;
                }

                BaseVehicle vehicle = m is BaseMountable b && b.HasParent() ? b.VehicleParent() : m as BaseVehicle;
                if (vehicle.IsNull() || m is HitchTrough.IHitchable || m is not (BaseSiegeWeapon or BaseHelicopter or global::Parachute) && InRange(m.transform.position, position, radius + 1f))
                {
                    m.transform.position = target;
                }
                else
                {
                    TryPushMountable(vehicle, target);
                }

                return true;
            }

            public void TryPushMountable(BaseVehicle vehicle, Vector3 target)
            {
                Rigidbody body = vehicle.rigidBody ?? vehicle.GetComponent<Rigidbody>();

                float forceMultiplier = vehicle switch
                {
                    BaseHelicopter => 2.5f,
                    GroundVehicle or BasicCar => 1f,
                    BaseSubmarine => 1.25f,
                    HitchTrough.IHitchable => 3f,
                    _ => 15f
                };

                switch (vehicle)
                {
                    case BaseSiegeWeapon:
                    case BaseBoat:
                        ApplyMassForce(vehicle, body, target);
                        break;

                    case ModularCar or _ when vehicle.PrefabName.Contains("modularcar"):
                        ApplyModularCarForce(vehicle, body, target, forceMultiplier: 150f);
                        break;

                    case BaseHelicopter:
                    case Parachute:
                        ApplyHelicopterOrParachuteForce(vehicle, body, target, forceMultiplier);
                        break;

                    default:
                        SetPositionAndRotation(vehicle, body);
                        break;
                }
            }

            private static void ApplyMassForce(BaseVehicle vehicle, Rigidbody body, Vector3 target)
            {
                Vector3 normalized = Vector3.ProjectOnPlane(vehicle.transform.position - target, vehicle.transform.up).normalized;
                float massAdjustedForce = vehicle is BaseSiegeWeapon ? body.mass * 25f : vehicle is PlayerBoat ? body.mass * 100f : body.mass * 50f;
                Vector3 forceDir = vehicle is Tugboat ? normalized : -normalized;
                body.AddForce(forceDir * massAdjustedForce + Vector3.up * 15f, ForceMode.Impulse);
            }

            private static void ApplyMassForce(BaseVehicle vehicle, Rigidbody body, Vector3 target, float maxSpeed = 30f)
            {
                Vector3 normalized = Vector3.ProjectOnPlane(vehicle.transform.position - target, Vector3.up).normalized;
                if (normalized.sqrMagnitude < 0.0001f)
                {
                    body.AddForce(Vector3.up * body.mass * 0.0003f, ForceMode.Impulse);
                    return;
                }
                Vector3 flatVel = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
                Vector3 forceDir = vehicle is Tugboat ? normalized : -normalized;
                float currentSpeed = Vector3.Dot(flatVel, forceDir);
                float deltaV = Mathf.Clamp(maxSpeed - currentSpeed, 0f, maxSpeed);
                body.AddForce(forceDir * body.mass * deltaV + Vector3.up * 15f, ForceMode.Impulse);
            }

            private static void ApplyModularCarForce(BaseVehicle vehicle, Rigidbody body, Vector3 target, float forceMultiplier)
            {
                if (body != null)
                {
                    Vector3 direction = Vector3.ProjectOnPlane(vehicle.transform.position - target, Vector3.up).normalized;
                    Vector3 horizontalForce = -direction * 140f * forceMultiplier;
                    Vector3 upwardForce = Vector3.up * 25f * forceMultiplier;
                    Vector3 totalForce = horizontalForce + upwardForce;

                    if (!body.isKinematic)
                    {
                        body.AddForce(totalForce, ForceMode.Impulse);
                    }
                    else
                    {
                        vehicle.transform.position += totalForce * 0.1f;
                        Quaternion rotationChange = Quaternion.LookRotation(direction);
                        vehicle.transform.rotation = Quaternion.Slerp(vehicle.transform.rotation, rotationChange, 0.1f);
                    }
                }
            }

            private static void ApplyHelicopterOrParachuteForce(BaseVehicle vehicle, Rigidbody body, Vector3 target, float forceMultiplier, bool b = false)
            {
                if (body != null)
                {
                    float baseForceMultiplier = 4f;
                    Vector3 direction = Vector3.ProjectOnPlane(vehicle.transform.position - target, Vector3.up).normalized;
                    Vector3 horizontalForce = -direction * baseForceMultiplier * forceMultiplier;
                    Vector3 multiForce = b ? direction * body.mass * forceMultiplier : Vector3.up * (vehicle is Parachute ? 100f : 25f) * forceMultiplier;
                    Vector3 totalForce = horizontalForce + multiForce;

                    if (!body.isKinematic)
                    {
                        body.AddForce(totalForce, ForceMode.VelocityChange);
                    }
                    else
                    {
                        vehicle.transform.position += totalForce * 0.1f;
                        Quaternion rotationChange = Quaternion.LookRotation(direction);
                        vehicle.transform.rotation = Quaternion.Slerp(vehicle.transform.rotation, rotationChange, 0.1f);
                    }
                }
            }

            private bool SetPositionAndRotation(BaseEntity m, Rigidbody rb)
            {
                m = GetParentEntity(m);
                if (m is ZiplineMountable or TrainCar { OwnerID: 0uL }) return false;

                float j = TerrainMeta.HeightMap.GetHeight(m.transform.position) - m.transform.position.y;
                float distance = 10f + m.bounds.size.Max() + (j > 5f ? j : 0f);
                Vector3 position = (m.transform.position.XZ3D() - LocationXZ3D).normalized * (ProtectionRadius + distance) + Location;
                Vector3 fwd = m.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.001f) fwd = m.transform.right;
                Quaternion yawOnly = Quaternion.LookRotation(-fwd.normalized, Vector3.up);
                float pitchDeg = Mathf.Asin(Mathf.Clamp(m.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                float newPitch = Mathf.Max(Abs(pitchDeg), 10f);
                Quaternion rotation = yawOnly * Quaternion.AngleAxis(newPitch, Vector3.left);
                position.y = Instance.GetSpawnHeight(position) + 1f;
                if (IsAirborne(m)) position.y = Mathf.Max(position.y, m.transform.position.y + 5f) + m.bounds.extents.y + 0.25f;

                if (rb != null)
                {
                    if (!rb.isKinematic)
                    {
                        Vector3 v = yawOnly * new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                        rb.linearVelocity = new Vector3(v.x, rb.linearVelocity.y, v.z);
                        rb.angularVelocity = Vector3.zero;
                    }
                    rb.MovePosition(position);
                    rb.MoveRotation(rotation);
                }
                else m.transform.SetPositionAndRotation(position, rotation);

                return true;
            }

            private void TryEjectMountable(BaseEntity e)
            {
                if (e is BaseMountable m)
                {
                    using var players = GetMountedPlayers(m);
                    if (players.Count == 0)
                    {
                        Eject(m, Location, ProtectionRadius, true);
                    }
                }
                else if (e is HotAirBalloon hab)
                {
                    using var players = GetMountedPlayers(hab);
                    if (players.Count == 0)
                    {
                        Eject(hab, Location, ProtectionRadius, false);
                    }
                }
            }

            private void EjectContainer(BaseEntity container, ulong playerSteamID, bool notice = true)
            {
                var position = GetEjectLocation(container.transform.position, 5f, Location, ProtectionRadius, true, true);
                position.y = Mathf.Max(position.y, TerrainMeta.WaterMap.GetHeight(position) + 0.1f, WaterSystem.OceanLevel, 0.1f);

                container.transform.position = position;
                container.TransformChanged();

                if (notice)
                {
                    BasePlayer player = BasePlayer.FindByID(playerSteamID);

                    EjectBackpackNotice(player, position);

                    Interface.CallHook("OnRaidableBaseBackpackEjected", new object[] { player, playerSteamID, container, Location, AllowPVP, 512, GetOwner(), GetRaiders(), BaseName });
                }
            }

            private float habdist = 15f;

            public bool Eject(BaseEntity m, Vector3 position, float radius, bool groundLevel)
            {
                if (m is HotAirBalloon)
                {
                    habdist += 15f;
                    radius += habdist;
                }

                m = GetParentEntity(m);
                var target = GetEjectLocation(m.transform.position, 10f, position, radius, false, false);
                var spawnHeight = SpawnsController.GetSpawnHeight(target);

                if (groundLevel)
                {
                    target.y = spawnHeight;
                }
                else if (m is Drone)
                {
                    target.y = Mathf.Max(target.y + 15f, position.y + radius);
                }
                else
                {
                    target.y = Mathf.Min(spawnHeight + radius, Mathf.Max(m.transform.position.y, SpawnsController.GetSpawnHeight(target))) + 5f;
                }

                if (m is PlayerCorpse)
                {
                    m.limitNetworking = true;
                }

                m.transform.position = target;
                m.TransformChanged();
                m.SendNetworkUpdate();

                if (m is PlayerCorpse)
                {
                    m.limitNetworking = false;
                }

                return true;
            }

            internal static BaseEntity GetParentEntity(BaseEntity m)
            {
                int n = 0;
                while (m != null && m.HasParent() && ++n < 30)
                {
                    if (!(m.GetParentEntity() is BaseEntity parent)) break;
                    m = parent;
                }

                return m;
            }

            public bool CanSetupEntity(BaseEntity e)
            {
                if (e.IsKilled() || setupBlockedPrefabs.Exists(e.ShortPrefabName.Contains))
                {
                    e.DelayedSafeKill();
                    Entities.Remove(e);
                    return false;
                }

                return true;
            }

            public bool ExtendHookSubscription;
            private readonly List<RespawnEntry> _respawns = new(32);


            private struct RespawnEntry
            {
                public double Target;
                public byte Type;

                public RespawnEntry(double target, bool isMurderer)
                {
                    Target = target;
                    Type = (byte)(isMurderer ? 1 : 0);
                }

                public bool IsMurderer => Type != 0;
            }

            public void TryRespawnNpc(bool IsMurderer)
            {
                if (!IsOpened && !Options.Levels.Level2)
                    return;

                float min = Mathf.Min(Options.RespawnRateMin, Options.RespawnRateMax);
                float max = Mathf.Max(Options.RespawnRateMin, Options.RespawnRateMax);
                float delay = min < max ? UnityEngine.Random.Range(min, max) : max;

                if (delay > 0.5f)
                {
                    ExtendHookSubscription = true;
                    double now = Time.realtimeSinceStartupAsDouble;
                    double target = now + delay;
                    if (Instance.DebugMode) Puts($"[Queue Respawn][{(IsMurderer ? "Murderer" : "Scientist")}] delay={delay:0.###}s now={now:0.###}s target={target:0.###}s range=[{min:0.###},{max:0.###}]s frame={Time.frameCount}");
                    _respawns.Add(new(target, IsMurderer));
                }
            }

            private void CheckNpcRespawns()
            {
                if (_respawns.Count == 0 || !CanSamplePosition())
                {
                    return;
                }
                double time = Time.realtimeSinceStartupAsDouble;
                for (int i = _respawns.Count - 1; i >= 0; i--)
                {
                    var e = _respawns[i];
                    if (time >= e.Target)
                    {
                        if (Instance.DebugMode) Puts($"[Fire Respawn][{Location}][{(e.IsMurderer ? "Murderer" : "Scientist")}] time={time:0.###}s scheduled={e.Target:0.###}s lateBy={(time - e.Target):0.###}s frame={Time.frameCount}");
                        int last = _respawns.Count - 1;
                        if (i != last) _respawns[i] = _respawns[last];
                        _respawns.RemoveAt(last);
                        RespawnNpcNow(e.IsMurderer);
                    }
                }
            }

            private void RespawnNpcNow(bool isMurderer)
            {
                if (IsUnloading || IsDespawning || (!IsOpened && !Options.Levels.Level2))
                {
                    return;
                }

                int current = 0;
                int max = isMurderer ? npcMaxAmountMurderers : npcMaxAmountScientists;

                foreach (var x in npcs)
                {
                    if (x != null && x.Brain != null && x.Brain.isMurderer == isMurderer)
                    {
                        current += 1;
                    }
                }

                if (current < max)
                {
                    SpawnNpc(isMurderer);
                }

                ExtendHookSubscription = _respawns.Count > 0;
            }

            public void SpawnNpcs()
            {
                if (!Options.NPC.Enabled || (Options.NPC.UseExpansionNpcs && config.Settings.ExpansionMode && Instance.DangerousTreasures.CanCall()))
                {
                    return;
                }

                if (!CanSamplePosition())
                {
                    if (IsInvoking(SpawnNpcs)) CancelInvoke(SpawnNpcs);
                    if (!IsDespawning) Invoke(SpawnNpcs, 1f);
                    return;
                }

                if (npcMaxAmountMurderers > 0)
                {
                    for (int i = 0; i < npcMaxAmountMurderers; i++)
                    {
                        SpawnNpc(true);
                    }
                }

                if (npcMaxAmountScientists > 0)
                {
                    for (int i = 0; i < npcMaxAmountScientists; i++)
                    {
                        SpawnNpc(false);
                    }
                }
            }

            public bool NearFoundation(Vector3 from, float range = 5f)
            {
                return foundations.Exists(to => InRange2D(from, to, range));
            }

            public bool FindPointOnNavmesh(Vector3 a, float radius, out Vector3 v)
            {
                if (RustNavMeshHelpers.SamplePosition(a, out var _navHit, radius, 25) && !NearFoundation(_navHit.position) && !IsNpcNearSpot(_navHit.position) && IsAcceptableWaterDepth(_navHit.position) && !TestInsideObject(_navHit.position))
                {
                    v = _navHit.position;
                    return true;
                }
                v = default;
                return false;
            }

            public bool IsAcceptableWaterDepth(Vector3 point) => WaterLevel.GetOverallWaterDepth(point, true, true, null) <= config.Settings.Management.WaterDepth;

            private bool TestInsideObject(Vector3 point) => GamePhysics.CheckSphere(point, 0.5f, Layers.Mask.Player_Server | Layers.Server.Deployed, QueryTriggerInteraction.Ignore) || IsPointInsideRock(point) || IsRockFaceUpwards(point) || IsRockFaceDownwards(point);

            private bool IsRockFaceDownwards(Vector3 point) => Array.Exists(Physics.RaycastAll(point + new Vector3(0f, 30f, 0f), Vector3.down, 31f, Layers.World), hit => hit.collider != null && IsRock(hit.collider.ObjectName()));

            private bool IsRockFaceUpwards(Vector3 point) => Array.Exists(Physics.RaycastAll(point + new Vector3(0f, 30f, 0f), Vector3.down, 31f, Layers.World | Layers.Terrain), hit => hit.collider != null && hit.point.y - point.y > 0.01f && (hit.collider.IsOnLayer(Layer.Terrain) || IsRock(hit.collider.ObjectName())));

            private bool IsPointInsideRock(Vector3 point) => Array.Exists(Physics.OverlapSphere(point, 0.01f, Layers.World), collider => collider != null && IsRock(collider.ObjectName()));

            private readonly List<string> _prefabs = new() { "rock", "formation", "cliff" };

            private bool IsRock(string name) => _prefabs.Exists(value => name.Contains(value, CompareOptions.OrdinalIgnoreCase));

            private bool InstantiateEntity(List<Vector3> wander, Vector3 position, out HumanoidBrain brain, out HumanoidNPC npc)
            {
                static void CopySerializableFields<T>(T src, T dst)
                {
                    var srcFields = typeof(T).GetFields();
                    foreach (var field in srcFields)
                    {
                        if (field.IsStatic) continue;
                        object value = field.GetValue(src);
                        field.SetValue(dst, value);
                    }
                }

                //"assets/prefabs/player/player.prefab"
                var prefabName = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
                var prefab = GameManager.server.FindPrefab(prefabName);
                var go = Facepunch.Instantiate.GameObject(prefab, position, Quaternion.identity);

                go.SetActive(false);

                go.name = prefabName;

                ScientistBrain scientistBrain = go.GetComponent<ScientistBrain>();
                ScientistNPC scientistNpc = go.GetComponent<ScientistNPC>();

                NPCPlayerNavigator originalNavigator = go.GetComponent<NPCPlayerNavigator>();
                HumanoidNavigator navigator = go.AddComponent<HumanoidNavigator>();
                CopySerializableFields(originalNavigator, navigator);
                DestroyImmediate(originalNavigator, true);

                npc = go.AddComponent<HumanoidNPC>();
                npc.HumanoidBrains = Instance.HumanoidBrains;
                npc.raid = this;
                npc.Instance = Instance;

                brain = go.AddComponent<HumanoidBrain>();
                navigator.doors = navigationDoors;
                navigator.brain = brain;
                brain.navigator = navigator;
                navigator.Agent = go.GetComponent<RustNavMeshAgent>();
                brain.HumanoidBrains = Instance.HumanoidBrains;
                brain.raid = this;
                brain.Instance = Instance;
                brain.SetRange(Options.NPC.AggressionRange);
                brain.RandomRoamPositions = wander;
                brain.DestinationOverride = position;
                brain.CheckLOS = brain.RefreshKnownLOS = true;
                brain.Settings = Options.NPC;
                brain.UseAIDesign = false;
                brain._baseEntity = npc;
                brain.npc = npc;
                brain.thinker = npc;
                brain.NpcTransform = npc.transform;
                brain.states ??= new();
                npc.Brain = brain;
                brain.RandomNearPositions = GetPositionsNearestTo(wander, Location, SqrProtectionRadius / 2f);

                CopySerializableFields(scientistNpc, npc);
                DestroyImmediate(scientistBrain, true);
                DestroyImmediate(scientistNpc, true);

                SceneManager.MoveGameObjectToScene(go, Rust.Server.EntityScene);

                go.SetActive(true);

                return npc != null;
            }

            private List<Vector3> GetPositionsNearestTo(List<Vector3> wander, Vector3 a, float sqrSenseRange)
            {
                List<Vector3> near = new();
                for (int i = 0; i < wander.Count; i++)
                {
                    Vector3 b = wander[i];
                    if ((a - b).sqrMagnitude < sqrSenseRange)
                    {
                        near.Add(b);
                    }
                }
                if (near.Count == 0)
                {
                    near.AddRange(wander);
                }
                return near;
            }

            private List<Vector3> GetWanderPositions(float radius)
            {
                List<Vector3> m = new();

                for (int i = 0; i < 11; i++)
                {
                    var target = Location + UnityEngine.Random.onUnitSphere * radius;

                    target.y = spawns != null && spawns.IsCustomSpawn ? Location.y : Instance.GetSpawnHeight(target);

                    if (FindPointOnNavmesh(target, radius, out var v))
                    {
                        m.Add(v);
                    }
                }

                return m;
            }

            private float GetRoamRadius() => Mathf.Clamp(Options.ArenaWalls.Radius, CELL_SIZE, Mathf.Min(Options.NPC.AggressionRange, ProtectionRadius * 0.9f));

            private float GetSpawnRadius() => Mathf.Clamp(Options.ArenaWalls.Radius, CELL_SIZE, ProtectionRadius * 0.9f);

            private HumanoidNPC SpawnNpc(bool isMurderer)
            {
                var positions = GetWanderPositions(GetRoamRadius());

                if (positions.Count == 0)
                    positions = GetWanderPositions(GetSpawnRadius());

                if (positions.Count == 0)
                    return null;

                var position = positions.GetRandom();

                if (position == Vector3.zero || !InstantiateEntity(positions, position, out var brain, out var npc))
                    return null;

                ulong userid = BotIdCounter++;
                npc.skinID = RB_SKIN_ID;
                npc.userID = userid;
                npc.UserIDString = userid.ToString();
                if (Options.NPC.UseRandomNames)
                {
                    List<string> RandomNames = isMurderer ? Options.NPC.RandomNpcNames : Options.NPC.RandomNpcNames;
                    brain.displayName = RandomNames.Count > 0 ? RandomNames.GetRandom() : RandomUsernames.Get(userid);
                    if (Options.NPC.Capitalize) brain.displayName = brain.displayName.TitleCase();
                    npc.displayName = npc.DisplayNameOverride = brain.displayName;
                }
                brain.userid = userid;
                brain.isMurderer = isMurderer;
                HumanoidBrains[userid] = brain;

                Authorize(userid);

                npcs.Add(npc);

                npc.loadouts = Array.Empty<PlayerInventoryProperties>();

                npc.EnableSaving(false);

                npc.Spawn();

                npc.CancelInvoke(npc.EquipTest);

                BasePlayer.bots.Remove(npc);

                SetupNpc(npc, brain, positions);

                return npc;
            }

            public class Loadout
            {
                public List<PlayerInventoryProperties.ItemAmountSkinned> belt = new();
                public List<PlayerInventoryProperties.ItemAmountSkinned> main = new();
                public List<PlayerInventoryProperties.ItemAmountSkinned> wear = new();
            }

            private PlayerInventoryProperties GetLoadout(HumanoidNPC npc, HumanoidBrain brain)
            {
                var loadout = CreateLoadout(npc, brain);
                var pip = ScriptableObject.CreateInstance<PlayerInventoryProperties>();

                if (pip.DeathIconPrefab == null)
                {
                    pip.DeathIconPrefab = new();
                    pip.DeathIconPrefab.guid = "6ff1ff9ea7408824ab5c8f6f3d9ab259";
                }

                pip.belt = loadout.belt;
                pip.main = loadout.main;
                pip.wear = loadout.wear;

                return pip;
            }

            private Loadout CreateLoadout(HumanoidNPC npc, HumanoidBrain brain)
            {
                var loadout = new Loadout();
                var items = brain.isMurderer ? Options.NPC.MurdererLoadout : Options.NPC.ScientistLoadout;

                if (items == null)
                    return loadout;

                AddItemAmountSkinned(loadout.wear, items.Boots, brain.keepInventory);
                AddItemAmountSkinned(loadout.wear, items.Gloves, brain.keepInventory);
                AddItemAmountSkinned(loadout.wear, items.Helm, brain.keepInventory);
                AddItemAmountSkinned(loadout.wear, items.Pants, brain.keepInventory);
                AddItemAmountSkinned(loadout.wear, items.Shirt, brain.keepInventory);
                AddItemAmountSkinned(loadout.wear, items.Torso, brain.keepInventory);
                if (!items.Torso.Exists(v => v.Contains("suit")))
                {
                    AddItemAmountSkinned(loadout.wear, items.Kilts, brain.keepInventory);
                }
                AddItemAmountSkinned(loadout.belt, items.Weapon, brain.keepInventory);

                return loadout;
            }

            private void AddItemAmountSkinned(List<PlayerInventoryProperties.ItemAmountSkinned> source, List<string> shortnames, bool keepInventory)
            {
                if (shortnames.IsNullOrEmpty())
                {
                    return;
                }

                string shortname = shortnames.GetRandom();

                if (string.IsNullOrWhiteSpace(shortname))
                {
                    return;
                }

                ItemDefinition def = ItemManager.FindItemDefinition(shortname);

                if (def == null)
                {
                    Puts("Invalid shortname {0} in profile {1}", shortname, ProfileName);
                    return;
                }

                if (def.TryGetComponent(out ItemModEntity mod) && mod != null && mod.entityPrefab != null && mod.entityPrefab.Get() is GameObject prefab && prefab != null && prefab.HasComponent<ThrownWeapon>())
                {
                    return;
                }

                ulong skin = GetItemSkin(def, SkinType.Npc, 0uL, config.Skins.Npc.Unique, config.Skins.Npc.Unique, config.Skins.Npc.Random, config.Skins.Npc.Workshop, config.Skins.Npc.Imported, config.Skins.Npc.ApprovedOnly, def.stackable);

                source.Add(new()
                {
                    amount = 1,
                    itemDef = def,
                    skinOverride = skin,
                    startAmount = 1
                });
            }

            private readonly List<string> _murdererPrefabNames = new() { "scarecrow", "scarecrow_dungeon", "scarecrow_dungeonnoroam" };

            private void SetupNpc(HumanoidNPC npc, HumanoidBrain brain, List<Vector3> positions)
            {
                if (!Options.NPC.AlternateScientistLoot.None)
                {
                    SetupAlternateLoot(npc, brain);
                }
                else npc.LootSpawnSlots = Array.Empty<LootContainer.LootSpawnSlot>();

                npc.CancelInvoke(npc.PlayRadioChatter);
                npc.DeathEffects = Array.Empty<GameObjectRef>();
                npc.RadioChatterEffects = Array.Empty<GameObjectRef>();
                npc.radioChatterType = ScientistNPC.RadioChatterType.NONE;
                npc.startHealth = brain.isMurderer ? Options.NPC.MurdererHealth : Options.NPC.ScientistHealth;
                npc.InitializeHealth(npc.startHealth, npc.startHealth);
                npc.Invoke(() => GiveKit(npc, brain, positions, brain.isMurderer), 0.2f);
            }

            private void SetupAlternateLoot(HumanoidNPC npc, HumanoidBrain brain)
            {
                var loot = Options.NPC.AlternateScientistLoot;

                if (loot.Enabled && loot.IDs.Count > 0)
                {
                    using var ids = loot.IDs.ToPooledList();
                    if (!brain.isMurderer)
                    {
                        ids.RemoveAll(x => _murdererPrefabNames.Contains(x));
                    }
                    else if (ids.Exists(_murdererPrefabNames.Contains))
                    {
                        ids.RemoveAll(x => !_murdererPrefabNames.Contains(x));
                    }
                    if (ids.Count > 0 && StringPool.toString.TryGetValue(loot.GetRandom(ids), out var prefab))
                    {
                        GameObject go = GameManager.server.FindPrefab(prefab);
                        if (go != null && go.TryGetComponent(out ScarecrowNPC obj2))
                        {
                            npc.LootSpawnSlots = obj2.LootSpawnSlots;
                        }
                        else if (go != null && go.TryGetComponent(out ScientistNPC obj1))
                        {
                            npc.LootSpawnSlots = obj1.LootSpawnSlots;
                        }
                    }
                }
            }

            private bool isKitted;

            private void GiveKit(HumanoidNPC npc, HumanoidBrain brain, List<Vector3> positions, bool isMurderer)
            {
                if (npc.IsDestroyed)
                    return;

                try
                {
                    GiveKit(npc, isMurderer);
                }
                catch (Exception ex)
                {
                    Puts("Kits plugin has thrown an error: {0}", ex);
                }

                using var itemList = npc.GetAllItems();

                bool isInventoryEmpty = itemList.Count == 0;

                if (isInventoryEmpty)
                {
                    var loadout = GetLoadout(npc, brain);

                    if (loadout.belt.Count > 0 || loadout.main.Count > 0 || loadout.wear.Count > 0)
                    {
                        npc.loadouts = new PlayerInventoryProperties[1];
                        npc.loadouts[0] = loadout;
                        npc.EquipLoadout(npc.loadouts);
                        isInventoryEmpty = false;
                    }
                }

                if (isInventoryEmpty)
                {
                    npc.inventory.GiveItem(ItemManager.CreateByName(isMurderer ? "pants" : "hazmatsuit", 1, 0uL), npc.inventory.containerWear);
                    npc.inventory.GiveItem(ItemManager.CreateByName(isMurderer ? "machete" : "pistol.python", 1, 0), npc.inventory.containerBelt);
                }

                npc.Invoke(() => UpdateItemsAndMovement(npc, brain, positions, brain.isMurderer), 0.2f);
            }

            private void GiveKit(HumanoidNPC npc, bool isMurderer)
            {
                List<string> kits = isMurderer ? murdererKits : scientistKits;

                if (kits.Count > 0)
                {
                    string kit = kits.GetRandom();

                    if (Instance.Kits?.Call("GiveKit", npc, kit) is string val)
                    {
                        if (val.Contains("Couldn't find the player"))
                        {
                            val = "Npcs cannot use the CopyPasteFile field in Kits";
                        }
                        Puts("Invalid kit '{0}' ({1})", kit, val);
                    }
                    else
                    {
                        isKitted = true;
                    }
                }
            }

            private int failedUpdates;
            private void UpdateItemsAndMovement(HumanoidNPC npc, HumanoidBrain brain, List<Vector3> positions, bool isMurderer)
            {
                if (npc.IsDestroyed)
                {
                    return;
                }

                try
                {
                    brain.Init();
                }
                catch
                {
                    SafelyKillNpc(npc);
                    if (++failedUpdates < npcMaxAmountMurderers + npcMaxAmountScientists)
                    {
                        Invoke(() => RespawnNpcNow(isMurderer), 1f);
                    }
                    return;
                }

                EquipWeapon(npc, brain);

                if (!ToggleNpcMinerHat(npc, TOD_Sky.Instance?.IsNight == true))
                {
                    npc.inventory.ServerUpdate(0f);
                }

                npc.Invoke(brain.SetupMovement, 0.1f);
            }

            public void EquipWeapon(HumanoidNPC npc, HumanoidBrain brain)
            {
                bool isHoldingProjectileWeapon = false;

                using var itemList = npc.GetAllItems();

                foreach (Item item in itemList)
                {
                    if (item == null || item.info == null) continue;
                    if (isKitted && config.Skins.Npc.CanSkinKit(item.skin, brain.isMurderer))
                    {
                        item.skin = GetItemSkin(item.info, SkinType.Npc, 0uL, config.Skins.Npc.Unique, config.Skins.Npc.Unique, config.Skins.Npc.Random, config.Skins.Npc.Workshop, config.Skins.Npc.Imported, config.Skins.Npc.ApprovedOnly, item.info.stackable);
                    }

                    if (item.GetHeldEntity() is HeldEntity e && e.IsValid())
                    {
                        if (item.skin != 0)
                        {
                            e.skinID = item.skin;
                            e.SendNetworkUpdate();
                        }

                        if (e is Shield && (item.position != 7 || item.parent != npc.inventory.containerWear))
                        {
                            if (!item.MoveToContainer(npc.inventory.containerWear, 7, false))
                            {
                                item.Remove();
                            }
                            continue;
                        }

                        if (!(e is AttackEntity attackEntity))
                        {
                            continue;
                        }

                        if (!isHoldingProjectileWeapon && attackEntity.hostileScore >= 2f && item.GetRootContainer() == npc.inventory.containerBelt && brain._attackEntity.IsNull())
                        {
                            isHoldingProjectileWeapon = brain.UpdateWeapon(attackEntity, item.uid) && e is BaseProjectile;
                        }

                        if (attackEntity.Is(out MedicalTool tool))
                        {
                            Item med = tool.GetItem() ?? tool.GetCachedItem();
                            if (med != null) brain.MedicalTools.Add(med);
                        }
                        else if (attackEntity.hostileScore >= 2f)
                        {
                            brain.AttackWeapons.Add(attackEntity);
                        }
                    }

                    item.MarkDirty();
                }

                brain.EnableMedicalTools();

                brain.IdentifyWeapon();
            }

            private bool IsNpcNearSpot(Vector3 position)
            {
                return npcs.Exists(npc => !npc.IsKilled() && InRange(npc.transform.position, position, 0.5f));
            }

            private void SetupNpcKits()
            {
                if (npcMaxAmountScientists > 0 || npcMaxAmountMurderers > 0)
                {
                    scientistKits.AddRange(Options.NPC.ScientistKits.Where(kit => Convert.ToBoolean(Instance.Kits?.Call("isKit", kit))));
                    murdererKits.AddRange(Options.NPC.MurdererKits.Where(kit => Convert.ToBoolean(Instance.Kits?.Call("isKit", kit))));
                    SpawnNpcs();
                }
            }

            public string DespawnString => despawnDateTime == DateTime.MaxValue ? string.Empty : $"[{DespawnTime}m]";

            public double DespawnTime => despawnDateTime != DateTime.MaxValue && DespawnMinutesInactive > 0 && despawnDateTime.Subtract(DateTime.Now).TotalSeconds > 0 ? Math.Ceiling(despawnDateTime.Subtract(DateTime.Now).TotalMinutes) : 0;

            public string MarkerName => config.Settings.Markers.MarkerName;

            public void ForceUpdateMarker()
            {
                markerCreated = false;
                DestroyMapMarkers();
                CreateGenericMarker();
                UpdateMarker();
                DestroySpheres();
                CreateSpheres();
            }

            public void UpdateMarker()
            {
                if (IsDespawning)
                {
                    return;
                }

                if (IsLoading)
                {
                    Invoke(UpdateMarker, 1f);
                    return;
                }

                if (!genericMarker.IsKilled())
                {
                    genericMarker.SendUpdate();
                }

                if (!explosionMarker.IsKilled())
                {
                    explosionMarker.transform.position = Location;
                    explosionMarker.SendNetworkUpdate();
                }

                if (!vendingMarker.IsKilled())
                {
                    bool showDespawnTime = AllowPVP ? !Options.HideDespawnTimePVP : !Options.HideDespawnTimePVE;
                    string despawnText = showDespawnTime && DespawnTime > 0 ? string.Format(" [{0}]", mx("UIFormatLockoutMinutes", null, DespawnTime)) : null;
                    vendingMarker.transform.position = Location;
                    vendingMarker.markerShopName = (markerName == MarkerName ? mx("MapMarkerOrderWithMode", null, mx(GetAllowKey()), Mode(), markerName, despawnText) : string.Format("{0} {1}", mx(GetAllowKey()), markerName)).Replace("{basename}", BaseName).Trim();
                    vendingMarker.SendNetworkUpdate();
                }

                if (markerCreated || !IsMarkerAllowed())
                {
                    return;
                }

                if (config.Settings.Markers.UseVendingMarker)
                {
                    vendingMarker = GameManager.server.CreateEntity(StringPool.Get(3459945130), Location) as VendingMachineMapMarker;

                    if (!vendingMarker.IsNull())
                    {
                        string flag = mx(GetAllowKey());
                        string despawnText = DespawnMinutesInactive > 0 ? string.Format(" [{0}m]", DespawnMinutesInactive.ToString()) : null;

                        if (markerName == MarkerName)
                        {
                            vendingMarker.markerShopName = mx("MapMarkerOrderWithMode", null, flag, Mode(), markerName, despawnText).Replace("{basename}", BaseName);
                        }
                        else vendingMarker.markerShopName = mx("MapMarkerOrderWithoutMode", null, flag, markerName, despawnText).Replace("{basename}", BaseName);

                        vendingMarker.enableSaving = false;
                        vendingMarker.enabled = false;
                        vendingMarker.Spawn();
                    }
                }
                else if (config.Settings.Markers.UseExplosionMarker)
                {
                    explosionMarker = GameManager.server.CreateEntity(StringPool.Get(4060989661), Location) as MapMarkerExplosion;

                    if (!explosionMarker.IsNull())
                    {
                        explosionMarker.Spawn();
                        explosionMarker.Invoke(() => explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy), 1f);
                    }
                }

                markerCreated = true;
                UpdateMarker();
            }

            private void CreateGenericMarker()
            {
                if (IsMarkerAllowed() && (config.Settings.Markers.UseExplosionMarker || config.Settings.Markers.UseVendingMarker))
                {
                    float radius = Mathf.Min(2.5f, World.Size <= 3600 ? config.Settings.Markers.SubRadius : config.Settings.Markers.Radius);
                    if (radius > 0f)
                    {
                        genericMarker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", Location) as MapMarkerGenericRadius;

                        if (!genericMarker.IsNull())
                        {
                            genericMarker.alpha = 0.75f;
                            genericMarker.color1 = GetMarkerColor1();
                            genericMarker.color2 = GetMarkerColor2();
                            genericMarker.radius = radius;
                            genericMarker.enableSaving = false;
                            genericMarker.Spawn();
                            genericMarker.SendUpdate();
                        }
                    }
                }
            }

            private void DestroyMapNote(BasePlayer owner)
            {
                if (mapNote == null)
                {
                    return;
                }
                if (owner.State?.pointsOfInterest?.Remove(mapNote) == true)
                {
                    owner.DirtyPlayerState();
                    owner.SendMarkersToClient();
                    owner.TeamUpdate();
                }
                if (mapNote.ShouldPool)
                {
                    mapNote.Dispose();
                }
                mapNote = null;
            }

            private ProtoBuf.MapNote mapNote;

            private bool TryParseHtmlString(string value, out Color color) => ColorUtility.TryParseHtmlString(value.StartsWith('#') ? value : $"#{value}", out color);

            private Color GetMarkerColor1() => Type == RaidableType.None ? Color.clear : TryParseHtmlString(config.Settings.Management.Colors1.Get(), out var colorDefault) ? colorDefault : Color.cyan;

            private Color GetMarkerColor2() => Type == RaidableType.None ? NoneColor : TryParseHtmlString(config.Settings.Management.Colors2.Get(), out var color) ? color : Color.cyan;

            private bool IsMarkerAllowed() => !Options.Silent && Type switch
            {
                RaidableType.Maintained => config.Settings.Markers.Maintained,
                RaidableType.Scheduled => config.Settings.Markers.Scheduled,
                _ => config.Settings.Markers.Manual
            };

            public void DestroyLocks()
            {
                locks.ForEach(SafelyKill);
            }

            public void DestroyNpcs()
            {
                npcs.ForEach(npc =>
                {
                    if (!npc.IsRealNull() && HumanoidBrains.TryGetValue(npc.userID, out var brain))
                    {
                        brain.DisableShouldThink();
                        if (!brain.AttackEntity.IsKilled())
                        {
                            brain.AttackEntity.SetHeld(false);
                        }
                    }
                    SafelyKillNpc(npc);
                });
            }

            public void DestroySpheres()
            {
                spheres.ForEach(SafelyKill);
                spheres.Clear();
            }

            public void DestroyMapMarkers()
            {
                if (!explosionMarker.IsKilled())
                {
                    explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy);
                    explosionMarker.Kill();
                }

                genericMarker.SafelyKill();
                vendingMarker?.server_vendingMachine.SafelyKill();
                vendingMarker.SafelyKill();
            }
        }

        public SpawnsControllerManager SpawnsController = new();

        public class SpawnsControllerManager
        {
            internal YieldInstruction instruction0;
            internal Dictionary<string, ZoneInfo> ManagedZones;
            internal List<string> assets;
            internal List<string> AdditionalBlockedColliders;
            internal List<string> _materialNames;
            internal List<MonumentInfoEx> Monuments = new();
            public RaidableBases Instance;
            internal Configuration config => Instance.config;

            public class MonumentInfoEx
            {
                public float radius;
                public string text;
                public Vector3 position;
                public MonumentInfoEx(string text, Vector3 position, float radius)
                {
                    this.text = text;
                    this.position = position;
                    this.radius = radius;
                }
            }

            public void Initialize()
            {
                ManagedZones = new();
                assets = new() { "perimeter_wall", "/props/", "/structures/", "/building/", "train_", "powerline_", "dune", "candy-cane", "assets/content/nature/", "assets/content/vehicles/", "walkway", "invisible_collider", "module_", "junkpile", "low_arc" };
                _materialNames = new() { "Generic (Instance)", "Concrete (Instance)", "Rock (Instance)", "Metal (Instance)", "Snow (Instance)", "Generic", "Concrete", "Rock", "Snow" }; // Fixed CreateSphere placement by removing "Metal"
                AdditionalBlockedColliders = new() { "powerline", "invisible", "TopCol", "swamp_", "floating_", "sentry", "walkway", "junkpile", "ore_node" };
                AdditionalBlockedColliders.AddRange(config.Settings.Management.AdditionalBlockedColliders);
            }

            private bool IsMonumentMarkerBlocked(string category) => config.Settings.Management.BlockedMonumentMarkers.Exists(m => m == "*" || m.Equals(category, StringComparison.OrdinalIgnoreCase));

            public IEnumerator SetupMonuments()
            {
                int attempts = 0;
                while (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null || TerrainMeta.Path.Monuments.Count == 0)
                {
                    if (++attempts >= 30)
                    {
                        break;
                    }
                    yield return CoroutineEx.waitForSeconds(1f);
                }
                Monuments = new();
                config.Settings.Management.BlockedMonumentMarkers.RemoveAll(string.IsNullOrWhiteSpace);
                foreach (var prefab in World.Serialization.world.prefabs)
                {
                    if (prefab != null && !string.IsNullOrEmpty(prefab.category) && prefab.id == 1724395471 && !IsMonumentMarkerBlocked(prefab.category))
                    {
                        yield return CalculateMonumentSize(new(prefab.position.x, prefab.position.y, prefab.position.z), prefab.category);
                    }
                }
                if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null || TerrainMeta.Path.Monuments.Count == 0)
                {
                    yield break;
                }
                foreach (var monument in TerrainMeta.Path.Monuments)
                {
                    if (monument == null || monument.transform == null || monument.name == null || monument.name.Contains("monument_marker"))
                    {
                        continue;
                    }
                    string monumentName = monument.displayPhrase?.english == null ? "BROKEN MONUMENT" : monument.displayPhrase.english.Trim();
                    if (monumentName.Contains("Lake") || monumentName.Contains("Canyon") || monumentName.Contains("Oasis"))
                    {
                        continue;
                    }
                    float max = monument.Bounds.size.Max();
                    if (max <= 0f) max = 150f;
                    if (monumentName.Equals("Substation")) max = 50f;
                    if (max > 0f)
                    {
                        if (monumentName.Contains("Excavator") || monumentName.Contains("Airfield")) max /= 1.5f;
                        if (monumentName.Equals("Abandoned Cabins")) max /= 2f;
                        Monuments.Add(new(monumentName, monument.transform.position, max));
                        continue;
                    }
                    yield return CalculateMonumentSize(monument.transform.position, string.IsNullOrEmpty(monument.displayPhrase.english.Trim()) ? monument.name.Contains("cave") ? "Cave" : monument.name : monument.displayPhrase.english.Trim());
                }
            }

            public IEnumerator CalculateMonumentSize(Vector3 from, string text)
            {
                int checks = 0;
                float radius = 15f;
                while (radius < World.Size / 2f)
                {
                    int pointsOfTopology = 0;
                    using var vectors = GetCircumferencePositions(from, radius, 30f, false, false, 0f);
                    foreach (var to in vectors)
                    {
                        if (ContainsTopology(TerrainTopology.Enum.Building | TerrainTopology.Enum.Monument, to, 5f))
                        {
                            pointsOfTopology++;
                        }
                        if (++checks >= 25)
                        {
                            yield return instruction0;
                            checks = 0;
                        }
                    }
                    if (pointsOfTopology < 4)
                    {
                        break;
                    }
                    radius += 15f;
                }
                if (radius <= 15f)
                {
                    radius = 100f;
                }
                Monuments.Add(new(text, from, radius));
            }

            public PooledList<Vector3> GetCircumferencePositions(Vector3 center, float radius, float next, bool spawnHeight = true, bool shouldSkipSmallRock = false, float y = 0f)
            {
                float degree = 0f;
                float angleInRadians = 2f * Mathf.PI;
                var positions = DisposableList<Vector3>();

                while (degree < 360)
                {
                    float radian = (angleInRadians / 360) * degree;
                    float x = center.x + radius * Mathf.Cos(radian);
                    float z = center.z + radius * Mathf.Sin(radian);
                    Vector3 a = new(x, y, z);

                    positions.Add(y == 0f ? a.WithY(spawnHeight ? GetSpawnHeight(a, true, shouldSkipSmallRock) : TerrainMeta.HeightMap.GetHeight(a)) : a);

                    degree += next;
                }

                return positions;
            }

            private bool IsValidMaterial(string materialName) => materialName.Contains("rock_") || _materialNames.Contains(materialName);

            private bool ShouldSkipSmallRock(RaycastHit hit, string colName)
            {
                return (colName.Contains("rock_") || colName.Contains("formation_", CompareOptions.OrdinalIgnoreCase)) && hit.collider.bounds.size.y <= 2f;
            }

            private RaycastHit[] hitBuffer = new RaycastHit[32768];
            public float GetSpawnHeight(Vector3 v, bool max = true, bool skip = false, int mask = targetMask)
            {
                float y = TerrainMeta.HeightMap.GetHeight(v);
                if (y > Instance.MaxTerrainY) Instance.MaxTerrainY = y;
                Vector3 origin = v;
                origin.y = (v.y > Instance.MaxTerrainY ? v.y : Instance.MaxTerrainY) + 48f;
                int num = Physics.RaycastNonAlloc(origin, Vector3.down, hitBuffer, Mathf.Infinity, mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < num; i++)
                {
                    RaycastHit hit = hitBuffer[i];
                    string colName = hit.collider.ObjectName();
                    if (string.IsNullOrEmpty(colName))
                    {
                        v.y = y;
                        return WaterLevel.GetWaterOrTerrainSurface(hit.point, waves: false, volumes: false);
                    }
                    if (skip && i != num - 1 && ShouldSkipSmallRock(hit, colName))
                    {
                        continue;
                    }
                    if (AdditionalBlockedColliders.Exists(colName.Contains))
                    {
                        continue;
                    }
                    string matName = hit.collider.MaterialName();
                    if (!string.IsNullOrEmpty(matName) && !IsValidMaterial(matName))
                    {
                        continue;
                    }
                    y = Mathf.Max(y, hit.point.y);
                    break;
                }
                y = max ? Mathf.Max(0f, y, WaterSystem.OceanLevel, TerrainMeta.WaterMap.GetHeight(v)) : y;
                return y;
            }

            public bool ContainsTopology(TerrainTopology.Enum mask, Vector3 position, float radius)
            {
                return (TerrainMeta.TopologyMap.GetTopology(position, radius) & (int)mask) != 0;
            }

            public bool ContainsTopology(TerrainTopology.Enum mask, Vector3 position, float radius, int topology)
            {
                return (topology & (int)mask) != 0;
            }

            public bool IsLocationBlocked(Vector3 v)
            {
                if (Instance.GridController.BlockAtSpawnsDatabase(v)) return true;
                if (TerrainMeta.Path?.OceanPatrolClose?.Count > 0 && TerrainMeta.Path.OceanPatrolClose.Exists(b => InRange2D(v, b, 100f))) return true;
                if (TerrainMeta.Path?.OceanPatrolFar?.Count > 0 && TerrainMeta.Path.OceanPatrolFar.Exists(b => InRange2D(v, b, 100f))) return true;
                string grid = MapHelper.PositionToString(v);
                return config.Settings.Management.BlockedGrids.Exists(blockedGrid => grid.Equals(blockedGrid, StringComparison.OrdinalIgnoreCase)) || IsZoneBlocked(v);
            }

            public bool IsZoneBlocked(Vector3 v)
            {
                if (ManagedZones.Count == 0)
                {
                    return false;
                }
                foreach (var zone in ManagedZones.Values)
                {
                    if (zone.IsPositionInZone(v))
                    {
                        return zone.IsBlocked;
                    }
                }
                return config.Settings.UseZoneManagerOnly;
            }

            private bool IsValidLocation(int? t, Vector3 v, float safeRadius, float minProtectionRadius, float railRadius)
            {
                if (IsLocationBlocked(v))
                {
                    return false;
                }

                if (!IsAreaSafe(v, 0f, safeRadius, safeRadius, safeRadius, gridLayers, false, out var cacheType))
                {
                    return false;
                }

                if (InDeepWater(v, false, 5f, 5f))
                {
                    return false;
                }

                if (IsMonumentPosition(v, config.Settings.Management.MonumentDistance > 0 ? config.Settings.Management.MonumentDistance : minProtectionRadius))
                {
                    return false;
                }

                return TopologyChecks(null, t, v, minProtectionRadius, railRadius, out var topology);
            }

            internal bool TopologyChecks(ManagementBiomeSettings biomes, int? t, Vector3 v, float radius, float railRadius, out string topology)
            {
                if (biomes != null && !biomes.IsBiomeEnabled(t, v, out var biome))
                {
                    topology = $"{biome} biome disabled";
                    return false;
                }

                int top = TerrainMeta.TopologyMap.GetTopology(v, radius);
                if (!config.Settings.Management.AllowOnBeach && ContainsTopology(TerrainTopology.Enum.Beach | TerrainTopology.Enum.Beachside, v, radius, top))
                {
                    topology = "Beach or Beachside";
                    return false;
                }

                if (!config.Settings.Management.AllowInland && !ContainsTopology(TerrainTopology.Enum.Beach | TerrainTopology.Enum.Beachside, v, radius, top))
                {
                    topology = "Inland";
                    return false;
                }

                if (!config.Settings.Management.AllowOnRailroads && (ContainsTopology(TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside, v, radius, top) || HasPointOnPathList(TerrainMeta.Path?.Rails, v, railRadius)))
                {
                    topology = "Rail or Railside";
                    return false;
                }

                if (!config.Settings.Management.AllowOnBuildingTopology && ContainsTopology(TerrainTopology.Enum.Building, v, radius, top))
                {
                    topology = "Building";
                    return false;
                }

                if (!config.Settings.Management.AllowOnMonumentTopology && ContainsTopology(TerrainTopology.Enum.Monument, v, radius, top))
                {
                    topology = "Monument";
                    return false;
                }

                if (!config.Settings.Management.AllowOnRivers && ContainsTopology(TerrainTopology.Enum.River | TerrainTopology.Enum.Riverside, v, radius, top))
                {
                    topology = "River or Riverside";
                    return false;
                }

                if (!config.Settings.Management.AllowOnRoads && ContainsTopology(TerrainTopology.Enum.Road | TerrainTopology.Enum.Roadside, v, radius, top)) // || HasPointOnPathList(TerrainMeta.Path?.Roads, v, Mathf.Max(M_RADIUS * 2f, radius)))
                {
                    topology = "Road or Roadside";
                    return false;
                }

                topology = "";
                return true;
            }

            private bool HasPointOnPathList(List<PathList> paths, Vector3 point, float radius)
            {
                return !paths.IsNullOrEmpty() && paths.Exists(path => path?.Path?.Points?.Exists(p => InRange(point, p, radius)) ?? false);
            }

            public bool IsBlockedByMapPrefab(List<(Vector3 pos, float dist)> prefabs, Vector3 position)
            {
                return prefabs.Exists(prefab => InRange(prefab.pos, position, prefab.dist));
            }

            public void ExtractLocation(RaidableSpawns spawns, Vector3 position, float maxLandLevel, float minProtectionRadius, float maxProtectionRadius, float railRadius, float maxWaterDepth)
            {
                int? t = TerrainMeta.BiomeMap?.GetBiomeMaxType(position);
                if (IsValidLocation(t, position, CELL_SIZE, minProtectionRadius, railRadius))
                {
                    var landLevel = GetLandLevel(position, 15f, 5f);

                    if (IsFlatTerrain(landLevel, maxLandLevel))
                    {
                        if (EnvironmentManager.Check(position, EnvironmentType.TrainTunnels, 25f))
                        {
#if RBDEBUG
                            Puts("Blocked by train tunnels: {0}", position);
#endif 
                            return;
                        }

                        var rsl = new RaidableSpawnLocation(position)
                        {
                            WaterHeight = Mathf.Max(0f, TerrainMeta.WaterMap.GetHeight(position)),
                            TerrainHeight = TerrainMeta.HeightMap.GetHeight(position),
                            SpawnHeight = GetSpawnHeight(position, false),
                            Radius = maxProtectionRadius,
                            RailRadius = railRadius,
                            LandLevel = landLevel,
                            AutoHeight = true,
                            biome = t
                        };

                        if (rsl.WaterHeight - rsl.SpawnHeight <= maxWaterDepth)
                        {
                            spawns.Spawns.Add(rsl);
                        }
                    }
                }
            }

            public bool IsSubmerged(BuildingWaterOptions options, RaidableSpawnLocation rsl)
            {
                if (rsl.WaterHeight - rsl.TerrainHeight > options.WaterDepth)
                {
                    if (!options.AllowSubmerged)
                    {
                        return true;
                    }

                    rsl.Location.y = rsl.WaterHeight;
                }

                return !options.AllowSubmerged && options.SubmergedAreaCheck && IsSubmerged(options, rsl, rsl.Radius);
            }

            public bool IsSubmerged(BuildingWaterOptions options, RaidableSpawnLocation rsl, float radius)
            {
                if (options.OceanLevel != WaterSystem.OceanLevel)
                {
                    options.OceanLevel = WaterSystem.OceanLevel;
                    rsl.Surroundings.Clear();
                }

                if (rsl.Surroundings.Count == 0)
                {
                    using var vectors = GetCircumferencePositions(rsl.Location, radius, 90f, true, false, 1f);
                    rsl.Surroundings.AddRange(vectors);
                }

                foreach (var vector in rsl.Surroundings)
                {
                    float w = Mathf.Max(0f, TerrainMeta.WaterMap.GetHeight(vector));
                    float h = GetSpawnHeight(vector, false); // TerrainMeta.HeightMap.GetHeight(vector);

                    if (w - h > options.WaterDepth)
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool IsMonumentPosition(Vector3 a, float extra)
            {
                return Monuments.Exists(mi =>
                {
                    var dist = a.Distance2D(mi.position);
                    var dir = (mi.position - a).normalized;

                    return dist <= mi.radius + a.Distance2D(mi.position + dir * extra) - dist;
                });
            }

            private List<(Vector3 position, float sqrDistance)> safeZones = new();

            public bool IsSafeZone(Vector3 a, float extra = 0f)
            {
                if (safeZones.Count == 0)
                {
                    foreach (var triggerSafeZone in TriggerSafeZone.allSafeZones)
                    {
                        float radius = (triggerSafeZone.triggerCollider == null ? 200f : ColliderEx.GetRadius(triggerSafeZone.triggerCollider, triggerSafeZone.transform.localScale)) + extra;
                        Vector3 center = triggerSafeZone.triggerCollider?.bounds.center ?? triggerSafeZone.transform.position;
                        safeZones.Add((center, radius * radius));
                    }
                }
                return safeZones.Exists(zone => (zone.position - a).sqrMagnitude <= zone.sqrDistance);
            }

            public bool IsAssetBlocked(BaseEntity entity, string colName, string entityName) => assets.Exists(colName.Contains) && (entity.IsNull() || entityName.Contains("/treessource/"));

            public bool IsAreaSafe(Vector3 area, float ignoreRadius, float protectionRadius, float cupboardRadius, float worldRadius, int layers, bool isCustomSpawn, out CacheType cacheType, RaidableType type = RaidableType.None)
            {
                if (IsSafeZone(area, config.Settings.Management.MonumentDistance))
                {
                    Instance.Queues.Messages.Add("Safe Zone", area);
                    cacheType = CacheType.Delete;
                    return false;
                }

                CacheType worldType = layers == gridLayers ? CacheType.Delete : CacheType.Temporary;

                cacheType = CacheType.Generic;

                Collider[] colliders = Physics.OverlapSphere(area, Mathf.Max(protectionRadius, cupboardRadius), layers, QueryTriggerInteraction.Collide);

                for (int i = 0; i < colliders.Length; i++)
                {
                    if (cacheType != CacheType.Generic)
                    {
                        goto next;
                    }

                    var collider = colliders[i];
                    var colName = collider.ObjectName();
                    var position = collider.GetPosition();

                    if (position == Vector3.zero || colName == "ZoneManager" || colName.Contains("xmas"))
                    {
                        goto next;
                    }

                    float dist = position.Distance(area);

                    if (ignoreRadius > 0f && dist <= ignoreRadius)
                    {
                        Instance.Queues.Messages.Add($"Ignored within radius", ignoreRadius);
                        goto next;
                    }

                    var e = collider.ToBaseEntity();

                    if (e is TutorialIsland || IsTutorialNetworkGroup(e))
                    {
                        Instance.Queues.Messages.Add($"Blocked by Tutorial Island");
                        cacheType = CacheType.Delete;
                        goto next;
                    }

                    if (e is BuildingPrivlidge or SteeringWheel)
                    {
                        if (e.OwnerID.IsSteamId() && dist <= cupboardRadius || Instance.IsEventEntity(e, dist, protectionRadius))
                        {
                            Instance.Queues.Messages.Add($"Blocked by a building privilege", position);
                            cacheType = CacheType.Privilege;
                        }
                        goto next;
                    }

                    string entityName = e.ObjectName();

                    if (!isCustomSpawn && IsAssetBlocked(e, colName, entityName))
                    {
                        if (layers == gridLayers || !collider.IsOnLayer(Layer.World))
                        {
                            Instance.Queues.Messages.Add("Blocked by a map prefab", $"{position} {colName}");
                            cacheType = CacheType.Delete;
                        }
                        goto next;
                    }

                    if (IsSputnik(e) || IsDangerousEvent(e))
                    {
                        if (!isCustomSpawn)
                        {
                            Instance.Queues.Messages.Add("Blocked by a deployable", $"{position} {colName}");
                            cacheType = CacheType.Temporary;
                        }
                        goto next;
                    }

                    if (dist > protectionRadius)
                    {
                        goto next;
                    }

                    if (e.IsNetworked())
                    {
                        if (e is Tugboat)
                        {
                            if (!isCustomSpawn)
                            {
                                Instance.Queues.Messages.Add("Tugboat is too close", $"{e.transform.position}");
                                cacheType = CacheType.Temporary;
                            }
                            goto next;
                        }

                        if (e.PrefabName.Contains("xmas") || entityName.StartsWith("assets/prefabs/plants") || entityName.Contains("tunnel") || e is BaseMountable or MetalDetectorSource)
                        {
                            goto next;
                        }

                        bool isSteamId = e.OwnerID.IsSteamId();

                        if (e is BasePlayer player)
                        {
                            if (type != RaidableType.Manual && !(!player.IsHuman() || player.IsFlying || config.Settings.Management.EjectSleepers && player.IsSleeping()))
                            {
                                Instance.Queues.Messages.Add("Player is too close", $"{player.displayName} ({player.userID}) {e.transform.position}");
                                cacheType = CacheType.Temporary;
                                goto next;
                            }
                        }
                        else if (isSteamId && e is SleepingBag)
                        {
                            goto next;
                        }
                        else if (isSteamId && config.Settings.Schedule.Skip && type == RaidableType.Scheduled)
                        {
                            goto next;
                        }
                        else if (isSteamId && config.Settings.Maintained.Skip && type == RaidableType.Maintained)
                        {
                            goto next;
                        }
                        else if (Instance.Has(e))
                        {
                            Instance.Queues.Messages.Add("Already occupied by a raidable base", e.transform.position);
                            cacheType = CacheType.Temporary;
                            goto next;
                        }
                        else if (e.IsNpc || e is SleepingBag)
                        {
                            goto next;
                        }
                        else if (e is BaseOven)
                        {
                            if (!isCustomSpawn && e.bounds.size.Max() > 1.6f && !CanIgnoreDeployable())
                            {
                                Instance.Queues.Messages.Add("An oven is too close", e.transform.position);
                                cacheType = CacheType.Temporary;
                                goto next;
                            }
                        }
                        else if (e is PlayerCorpse corpse)
                        {
                            if (corpse.playerSteamID == 0 || corpse.playerSteamID.IsSteamId())
                            {
                                Instance.Queues.Messages.Add("A player corpse is too close", e.transform.position);
                                cacheType = CacheType.Temporary;
                                goto next;
                            }
                        }
                        else if (e is DroppedItemContainer backpack && e.ShortPrefabName != "item_drop")
                        {
                            if (backpack.playerSteamID == 0 || backpack.playerSteamID.IsSteamId())
                            {
                                Instance.Queues.Messages.Add("A player's backpack is too close", e.transform.position);
                                cacheType = CacheType.Temporary;
                                goto next;
                            }
                        }
                        else if (!isSteamId)
                        {
                            if (e is BuildingBlock || e.ShortPrefabName.Contains("wall.external.high") || !e.enableSaving && e.HasFlag(BaseEntity.Flags.Busy) && e.HasFlag(BaseEntity.Flags.Locked))
                            {
                                Instance.Queues.Messages.Add("Entity is too close", $"{e.ShortPrefabName} {e.transform.position}");
                                cacheType = CacheType.Temporary;
                                goto next;
                            }
                            else if (e is MiningQuarry)
                            {
                                Instance.Queues.Messages.Add("A mining quarry is too close", $"{e.ShortPrefabName} {e.transform.position}");
                                cacheType = CacheType.Delete;
                                goto next;
                            }
                        }
                        else
                        {
                            if (!CanIgnoreDeployable() || !Instance.DeployableItems.ContainsKey(e.PrefabName))
                            {
                                Instance.Queues.Messages.Add("Blocked by other object", $"{e.ShortPrefabName} {e.transform.position}");
                                cacheType = CacheType.Temporary;
                            }
                            goto next;
                        }
                    }
                    else if (collider.gameObject.layer == (int)Layer.World && dist <= worldRadius && !isCustomSpawn)
                    {
                        if (colName.Contains("cliff_", CompareOptions.OrdinalIgnoreCase))
                        {
                            if (IsObstructed(area, M_RADIUS, 1f, -1))
                            {
                                Instance.Queues.Messages.Add("Cliff formation is too large", position);
                                cacheType = worldType;
                                goto next;
                            }
                        }
                        else if (colName.Contains("rock_") || colName.Contains("formation_", CompareOptions.OrdinalIgnoreCase))
                        {
                            if (collider.bounds.size.Max() > 2f && (e == null || e.OwnerID == 0))
                            {
                                Instance.Queues.Messages.Add("Rock is too large", position);
                                cacheType = worldType;
                                goto next;
                            }
                        }
                        else if (!config.Settings.Management.AllowOnRoads && colName.StartsWith("road_"))
                        {
                            Instance.Queues.Messages.Add("Not allowed on roads", position);
                            cacheType = CacheType.Delete;
                            goto next;
                        }
                        else if (!config.Settings.Management.AllowOnIceSheets && colName.StartsWith("ice_sheet"))
                        {
                            Instance.Queues.Messages.Add("Not allowed on ice sheets", position);
                            cacheType = CacheType.Delete;
                            goto next;
                        }
                    }
                    else if (collider.gameObject.layer == (int)Layer.Water && !isCustomSpawn)
                    {
                        if (!config.Settings.Management.AllowOnRivers && colName.StartsWith("River Mesh"))
                        {
                            Instance.Queues.Messages.Add("Not allowed on rivers", position);
                            cacheType = CacheType.Delete;
                            goto next;
                        }
                    }

                next:
                    colliders[i] = null;
                }

                return cacheType == CacheType.Generic;
            }

            public bool IsTutorialNetworkGroup(BaseEntity entity)
            {
                if (!entity.IsValid() || entity.net.group == null) return false;
                return TutorialIsland.IsTutorialNetworkGroup(entity.net.group.ID);
            }

            public bool CanIgnoreDeployable() => config.Settings.Management.EjectDeployables || config.Settings.Management.KillDeployables;

            public MinMax GetLandLevel(Vector3 from, float radius, float sampleSpacing, BasePlayer player = null)
            {
                float minY = float.MaxValue, maxY = float.MinValue;

                for (float dx = -radius; dx <= radius; dx += sampleSpacing)
                {
                    for (float dz = -radius; dz <= radius; dz += sampleSpacing)
                    {
                        if (dx * dx + dz * dz > radius * radius)
                        {
                            continue;
                        }

                        Vector3 a = new(from.x + dx, 0f, from.z + dz);
                        a.y = GetSpawnHeight(a, true, true);

                        if (player != null && player.IsAdmin)
                        {
                            DrawText(player, 30f, Color.blue, a, $"<size=24>{Abs(from.y - a.y):N1}</size>");
                            DrawLine(player, 30f, Color.blue, from, a);
                        }

                        if (a.y < minY) minY = a.y;
                        if (a.y > maxY) maxY = a.y;
                    }
                }

                return new(minY, maxY);
            }

            public bool IsFlatTerrain(MinMax landLevel, float maxLandLevel)
            {
                return (landLevel.y - landLevel.x) <= maxLandLevel;
            }

            public bool InDeepWater(Vector3 v, bool seabed, float minDepth, float maxDepth)
            {
                v.y = TerrainMeta.HeightMap.GetHeight(v);

                float waterDepth = WaterLevel.GetWaterDepth(v, true, true, null);

                if (seabed)
                {
                    return waterDepth >= 0 - minDepth && waterDepth <= 0 - maxDepth;
                }

                return waterDepth > maxDepth;
            }

            public void SetupZones(bool message)
            {
                ManagedZones.Clear();

                if (config.Settings.AllowedZones.Contains("*"))
                {
                    return;
                }

                var zoneIds = Instance.ZoneManager?.Call("GetZoneIDs") as string[];

                if (zoneIds == null || zoneIds.Length == 0)
                {
                    return;
                }

                config.Settings.AllowedZones.RemoveAll(string.IsNullOrWhiteSpace);

                int allowed = 0, blocked = 0;

                foreach (string zoneId in zoneIds)
                {
                    var isBlocked = AddZone(zoneId);

                    if (isBlocked) { blocked++; } else { allowed++; }
                }

                if (message && (allowed > 0 || blocked > 0))
                {
                    Puts(Instance.mx("AllowedZones", null, allowed));
                    Puts(Instance.mx("BlockedZones", null, blocked));
                }
            }

            public bool AddZone(string zoneId)
            {
                var obj = Instance?.ZoneManager?.Call("ZoneFieldList", zoneId);
                if (obj == null || obj is not Dictionary<string, string> dict)
                    return false;


                var zoneLoc = dict.TryGetValue("Location", out var loc) ? loc.ToVector3() : Vector3.zero;
                var radius = dict.TryGetValue("radius", out var rad) ? Convert.ToSingle(rad) : 0f;
                var size = dict.TryGetValue("size", out var sz) ? sz.ToVector3() : Vector3.zero;

                if (zoneLoc == Vector3.zero || (radius <= 0f && size == Vector3.zero))
                    return false;

                var zoneName = dict.TryGetValue("name", out var n) ? n : null;
                var zoneRot = dict.TryGetValue("rotation", out var rot) ? Quaternion.Euler(rot.ToVector3()) : Quaternion.identity;
                var isBlocked = !config.Settings.UseZoneManagerOnly && !config.Settings.AllowedZones.Exists(zone => zone == zoneId || (!string.IsNullOrEmpty(zoneName) && zoneName.Equals(zone, StringComparison.OrdinalIgnoreCase)));
                ManagedZones[zoneId] = new(zoneId, zoneLoc, zoneRot, radius, size, isBlocked, config.Settings.ZoneDistance);
                return isBlocked;
            }

            public bool IsObstructed(Vector3 from, float radius, float landLevel, float forcedHeight, BasePlayer player = null)
            {
                from.y = TerrainMeta.HeightMap.GetHeight(from);
                int n = 5;
                float f = radius * 0.2f;
                bool flag = false;
                bool valid = player != null;
                if (forcedHeight != -1)
                {
                    landLevel += forcedHeight;
                }
                while (n-- > 0)
                {
                    float step = f * n;
                    float next = 360f / step;
                    using var vectors = GetCircumferencePositions(from, step, next, true, true, 0f);
                    foreach (var to in vectors)
                    {
                        var distance = Abs((from - to).y);
                        if (distance > landLevel)
                        {
                            if (!valid) return true;
                            DrawText(player, 30f, Color.red, to, $"{distance:N1}");
                            flag = true;
                        }
                        else if (valid) DrawText(player, 30f, Color.green, to, $"{distance:N1}");
                    }
                }
                return flag;
            }
        }

        public static bool CanSamplePosition() => ConVar.AI.useUnityNavmesh || RustNavigation.Instance != null && RustNavigation.Instance.IsDefaultNavmeshBuilt();

        #region Hooks

        private void UnsubscribeHooks()
        {
            if (IsUnloading)
            {
                return;
            }

            Unsubscribe(nameof(OnCustomLootNPC));
            Unsubscribe(nameof(CanBGrade));
            Unsubscribe(nameof(CanDoubleJump));
            Unsubscribe(nameof(OnLifeSupportSavingLife));
            Unsubscribe(nameof(CanRevivePlayer));
            Unsubscribe(nameof(OnRestoreUponDeath));
            Unsubscribe(nameof(CanPopulateLoot));
            Unsubscribe(nameof(ShouldBLPopulate_NPC));
            Unsubscribe(nameof(OnNpcKits));
            Unsubscribe(nameof(CanTeleport));
            Unsubscribe(nameof(canTeleport));
            Unsubscribe(nameof(canRemove));
            Unsubscribe(nameof(CanEntityBeTargeted));
            Unsubscribe(nameof(CanEntityTrapTrigger));
            Unsubscribe(nameof(CanOpenBackpack));
            Unsubscribe(nameof(CanBePenalized));
            Unsubscribe(nameof(OnBaseRepair));
            Unsubscribe(nameof(OnClanMemberJoined));
            Unsubscribe(nameof(CanGainXp));
            Unsubscribe(nameof(OnNeverWear));

            Unsubscribe(nameof(OnLoseCondition));
            Unsubscribe(nameof(OnNearbyTurretsScan));
            Unsubscribe(nameof(OnInterferenceUpdate));
            Unsubscribe(nameof(OnMlrsFire));
            Unsubscribe(nameof(OnTeamAcceptInvite));
            Unsubscribe(nameof(OnElevatorMove));
            Unsubscribe(nameof(OnElevatorCall));
            Unsubscribe(nameof(OnButtonPress));
            Unsubscribe(nameof(OnElevatorButtonPress));
            Unsubscribe(nameof(OnSamSiteTargetScan));
            Unsubscribe(nameof(OnPlayerCommand));
            Unsubscribe(nameof(OnServerCommand));
            Unsubscribe(nameof(OnTrapTrigger));
            Unsubscribe(nameof(OnEntityBuilt));
            Unsubscribe(nameof(OnStructureUpgrade));
            Unsubscribe(nameof(OnEntityGroundMissing));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnLootEntityEnd));
            Unsubscribe(nameof(OnPlayerRespawn));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(OnPlayerLand));
            Unsubscribe(nameof(OnPlayerDeath));
            Unsubscribe(nameof(OnBackpackDrop));
            Unsubscribe(nameof(OnPlayerDropActiveItem));
            Unsubscribe(nameof(OnEntityEnter));
            Unsubscribe(nameof(OnNpcDuck));
            Unsubscribe(nameof(OnNpcDestinationSet));
            Unsubscribe(nameof(OnCupboardAuthorize));
            Unsubscribe(nameof(OnActiveItemChanged));
            Unsubscribe(nameof(OnFireBallSpread));
            Unsubscribe(nameof(OnFireBallDamage));
            Unsubscribe(nameof(OnCupboardProtectionCalculated));

            UnsubscribeDamageHook();
        }

        private void OnMapMarkerAdded(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player.IsAlive() && player.HasPermission("raidablebases.mapteleport") && !player.isMounted)
            {
                float y = GetSpawnHeight(note.worldPosition);
                if (player.IsFlying) y = Mathf.Max(y, player.transform.position.y);
                player.Teleport(note.worldPosition.WithY(y));
                if (config.Settings.DestroyMarker)
                {
                    player.State.pointsOfInterest?.Remove(note);
                    note.Dispose();
                    player.DirtyPlayerState();
                    player.SendMarkersToClient();
                }
            }
        }

        private void OnNewSave(string filename)
        {
            if (config.Settings.Wipe.Map)
            {
                Puts("New map detected; wiping ranked ladder");
                wiped = true;
            }
        }

        private void Init()
        {
            if (InstallationError)
            {
                return;
            }
            HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);
            Automated = new(this, config.Settings.Maintained.Enabled, config.Settings.Schedule.Enabled);
            UndoComparer.DeployableItems = DeployableItems;
            UndoComparer.IsBox = IsBox;
            SpawnsController.Instance = this;
            IsUnloading = false;
            Buildings = new();
            GridController.Instance = this;
            IsSpawnerBusy = true;
            RegisterPermissions();
            Unsubscribe(nameof(OnMapMarkerAdded));
            Unsubscribe(nameof(OnPlayerSleepEnded));
            Unsubscribe(nameof(CanBuild));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnEntitySpawned));
            UnsubscribeHooks();
            SpawnsController.Initialize();
            Queues = new(this);
        }

        private void OnServerSave()
        {
            if (InstallationError || IsUnloading)
            {
                return;
            }
            AddRustNavigationBuildData();
            timer.Once(5f, () =>
            {
                if (!IsUnloading) SaveData();
            });
        }

        private void OnServerShutdown()
        {
            if (!InstallationError)
            {
                AddRustNavigationBuildData();
            }
            IsShuttingDown = true;
            IsUnloading = true;
        }

        private void Unload()
        {
            if (InstallationError)
            {
                return;
            }
            IsUnloading = true;
            IsSpawnerBusy = true;
            SaveData();
            TryInvokeMethod(StopLoadCoroutines);
            TryInvokeMethod(UnsubscribeSky);
            TryInvokeMethod(StartEntityCleanup);
            DestroyProtection();
        }

        private void OnServerInitialized(bool initial)
        {
            if (InstallationError)
            {
                return;
            }
            RaidableBasesExtensionMethods.ExtensionMethods._permission ??= permission;
            Puts("Free version initialized.");
            SpawnsController.instruction0 = CoroutineEx.waitForSeconds(0.0025f);
            if (!string.IsNullOrWhiteSpace(config.Settings.EventCommand)) AddCovalenceCommand(config.Settings.EventCommand, nameof(CommandRaidBase));
            if (!string.IsNullOrWhiteSpace(config.Settings.HunterCommand)) AddCovalenceCommand(config.Settings.HunterCommand, nameof(CommandRaidHunter));
            if (!string.IsNullOrWhiteSpace(config.Settings.ConsoleCommand)) AddCovalenceCommand(config.Settings.ConsoleCommand, nameof(CommandRaidBase));
            AddCovalenceCommand("rb.reloadconfig", nameof(CommandReloadConfig));
            AddCovalenceCommand("rb.reloadprofiles", nameof(CommandReloadConfig));
            AddCovalenceCommand("rb.reloadtables", nameof(CommandReloadConfig));
            AddCovalenceCommand("rb.config", nameof(CommandConfig), "raidablebases.config");
            AddCovalenceCommand("rb.populate", nameof(CommandPopulate), "raidablebases.config");
            AddCovalenceCommand("rb.toggle", nameof(CommandToggle), "raidablebases.config");
            LoadPlayerData();
            InitializeSkins();
            Initialize();
            OceanLevel = WaterSystem.OceanLevel;
            Queues.RestartCoroutine();
            timer.Repeat(Mathf.Clamp(config.EventMessages.Interval, 1f, 60f), 0, CheckNotifications);
            timer.Repeat(30f, 0, UpdateAllMarkers);
            timer.Repeat(30f, 0, CheckOceanLevel);
            setupCopyPasteObstructionRadius = ServerMgr.Instance.StartCoroutine(SetupCopyPasteObstructionRadius());
            SubscribeDamageHook();
            BuildPrefabIds();
            LoadOwnership();
            RebuildNavigation(initial);
        }

        public void RebuildNavigation(bool initial)
        {
            if (initial)
            {
                data.RustNavigationBuilds.RemoveAll(x => x == null);
                if (data.RustNavigationBuilds.Count == 0)
                {
                    return;
                }
                ServerMgr.Instance.StartCoroutine(RebuildNavigationCo());
            }
        }

        private IEnumerator RebuildNavigationCo()
        {
            if (data.WipeId != SaveRestore.WipeId || ConVar.AI.useUnityNavmesh || Rust.Ai.AiManager.nav_disable)
            {
                data.RustNavigationBuilds.Clear();
                data.WipeId = SaveRestore.WipeId;
                yield break;
            }

            using var builds = data.RustNavigationBuilds.ToPooledList();

            while (!IsUnloading && (RustNavigation.Instance == null || !RustNavigation.Instance.IsDefaultNavmeshBuilt()))
            {
                yield return null;
            }

            if (IsUnloading)
            {
                yield break;
            }

            foreach (var build in builds)
            {
                RustNavigation.Instance.RebuildTilesInBounds(build.ToBounds());
            }

            AddRustNavigationBuildData();
        }

        private void OnSunrise()
        {
            Raids.ForEach(raid => raid.ToggleLights());
        }

        private void OnSunset()
        {
            Raids.ForEach(raid => raid.ToggleLights());
        }

        private object OnLifeSupportSavingLife(BasePlayer player)
        {
            return EventTerritory(player.transform.position) || HasPVPDelay(player.userID) ? true : (object)null;
        }

        private object CanDoubleJump(BasePlayer player)
        {
            return EventTerritory(player.transform.position) || HasPVPDelay(player.userID) ? true : (object)null;
        }

        private object OnRestoreUponDeath(BasePlayer player)
        {
            return Get(player, null, out var raid) && (raid.AllowPVP ? config.Settings.Management.BlockRestorePVP : config.Settings.Management.BlockRestorePVE) ? true : (object)null;
        }

        private object CanRevivePlayer(BasePlayer player, Vector3 pos)
        {
            return Get(player, null, out var raid) && (raid.AllowPVP ? config.Settings.Management.BlockRevivePVP : config.Settings.Management.BlockRevivePVE) ? true : (object)null;
        }

        private object OnCustomLootNPC(NetworkableId networkableId)
        {
            return Has(networkableId) ? true : (object)null;
        }

        private object CanPopulateLoot(BaseEntity entity, LootableCorpse corpse)
        {
            return corpse != null && corpse.skinID == RB_SKIN_ID ? true : (object)null;
        }

        private object ShouldBLPopulate_NPC(ulong playerSteamID)
        {
            return playerSteamID >= 514922525 && playerSteamID <= BotIdCounter ? true : (object)null;
        }

        private object OnNpcKits(ulong targetId)
        {
            return HumanoidBrains.ContainsKey(targetId) ? true : (object)null;
        }

        private object OnReflectDamage(BasePlayer victim, BasePlayer attacker)
        {
            return PlayerInEvent(victim) || PlayerInEvent(attacker) ? true : (object)null;
        }

        private object CanBGrade(BasePlayer player, int playerGrade, BuildingBlock block, Planner planner)
        {
            return PlayerInEvent(player) ? 0 : (object)null;
        }

        private object canRemove(BasePlayer player)
        {
            return !player.IsFlying && EventTerritory(player.transform.position) ? mx("CannotRemove", player.UserIDString) : null;
        }

        private object canTeleport(BasePlayer player)
        {
            return !player.IsFlying && (EventTerritory(player.transform.position) || HasPVPDelay(player.userID)) ? m("CannotTeleport", player.UserIDString) : null;
        }

        private object CanTeleport(BasePlayer player, Vector3 to)
        {
            return !player.IsFlying && (EventTerritoryAny(new Vector3[2] { to, player.transform.position }) || HasPVPDelay(player.userID)) ? m("CannotTeleport", player.UserIDString) : null;
        }

        private object OnBaseRepair(BuildingManager.Building building, BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? false : (object)null;
        }

        private object CanGainXp(BasePlayer player, double amount, string pluginName)
        {
            if (pluginName == Name)
            {
                foreach (var raid in Raids)
                {
                    if (raid.IsParticipant(player))
                    {
                        return true;
                    }
                }
            }
            return null;
        }

        private object OnRaidingUltimateTargetAcquire(BasePlayer player, Vector3 targetPoint)
        {
            return !Get(targetPoint, out var raid) || raid.Options.MLRS ? (object)null : true;
        }

        private void OnClanMemberJoined(ulong userid, string tag)
        {
            var player = BasePlayer.FindByID(userid);
            if (player == null) return;
            var raid = Raids.FirstOrDefault(other => other.ownerId == player.userID && other.IsAllyHogging(player));
            if (raid == null) return;
            Clans?.Call("cmdChatClan", player, "clan", new string[1] { "leave" });
        }

        private object OnTeamAcceptInvite(RelationshipManager.PlayerTeam playerTeam, BasePlayer player)
        {
            if (player == null) return null;
            var raid = Raids.FirstOrDefault(other => other.ownerId == player.userID && other.IsAllyHogging(player));
            if (raid == null) return null;
            playerTeam.RejectInvite(player);
            return true;
        }

        private object OnNeverWear(Item item, float amount)
        {
            var player = item?.parentItem?.GetOwnerPlayer() ?? item?.GetOwnerPlayer();

            if (player == null || !player.IsHuman() || player.HasPermission("raidablebases.durabilitybypass"))
            {
                return null;
            }

            if (!Get(player.transform.position, out var raid) || !raid.Options.EnforceConditionLoss)
            {
                return null;
            }

            return amount;
        }

        private void OnDeletedDynamicPVP(string zoneId, string eventName)
        {
            SpawnsController.ManagedZones.Remove(zoneId);
        }

        private void OnCreatedDynamicPVP(string zoneId, string eventName, Vector3 position, float duration)
        {
            if (ZoneManager != null)
            {
                SpawnsController.AddZone(zoneId);
            }
        }

        private void OnLoseCondition(Item item, ref float amount)
        {
            if (item == null || item.instanceData != null && item.instanceData.dataFloat > 0f)
            {
                return;
            }

            var player = item?.parentItem?.GetOwnerPlayer() ?? item?.GetOwnerPlayer();

            if (player == null || !player.userID.IsSteamId() || player.HasPermission("raidablebases.durabilitybypass"))
            {
                return;
            }

            if (!Get(player.transform.position, out var raid) || !raid.Options.EnforceConditionLoss)
            {
                return;
            }

            var uid = item.uid;

            if (!raid.conditions.TryGetValue(uid, out var condition))
            {
                raid.conditions[uid] = condition = item.condition;
            }

            float _previous = condition - amount;

            raid.Invoke(() =>
            {
                if (raid == null)
                {
                    return;
                }

                if (item == null || item.isBroken || !item.IsValid())
                {
                    raid.conditions.Remove(uid);
                    return;
                }

                if (_previous < item.condition)
                {
                    item.condition = _previous;
                }

                if (item.condition <= 0f && item.condition < condition)
                {
                    item.OnBroken();
                    raid.conditions.Remove(uid);
                }
                else raid.conditions[uid] = item.condition;
            }, 0.0625f);
        }

        private object OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade, ulong skin)
        {
            if (!Get(block.transform.position, out var raid))
            {
                return null;
            }

            if (block.OwnerID == 0uL && !block.enableSaving)
            {
                return config.Settings.Management.AllowUpgrade ? (object)null : true;
            }

            return grade switch
            {
                BuildingGrade.Enum.Wood when raid.Options.BuildingRestrictions.Wooden => true,
                BuildingGrade.Enum.Stone when raid.Options.BuildingRestrictions.Stone => true,
                BuildingGrade.Enum.Metal when raid.Options.BuildingRestrictions.Metal => true,
                BuildingGrade.Enum.TopTier when raid.Options.BuildingRestrictions.HQM => true,
                _ => null
            };
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (go == null)
            {
                return;
            }

            var e = go.ToBaseEntity();

            if (e == null || !Get(e.transform.position, out var raid, 0.6f))
            {
                return;
            }

            var player = planner.GetOwnerPlayer();

            if (player == null || IsPocketDimensions(player, e))
            {
                return;
            }

            if (raid.Options.Mounts.Siege && !raid.Options.Siege.Only)
            {
                if (e is BaseSiegeWeapon || e is ConstructableEntity)
                {
                    raid.Eject(e, raid.Location, raid.ProtectionRadius, true);
                    return;
                }
            }

            if (raid.Options.BuildingRestrictions.Any() && e is BuildingBlock block)
            {
                var grade = block.grade;

                block.Invoke(() =>
                {
                    if (raid == null || block.IsDestroyed)
                    {
                        return;
                    }

                    if (block.grade == grade || OnStructureUpgrade(block, player, block.grade, block.skinID) == null)
                    {
                        AddPlayerEntity(e, raid);
                        return;
                    }

                    foreach (var ia in block.BuildCost().Items)
                    {
                        player.GiveItem(ItemManager.Create(ia.itemDef, (int)ia.amount));
                    }

                    block.SafelyKill();
                }, 0.1f);
            }
            else if (raid.IsFoundation(e) && raid.NearFoundation(e.transform.position))
            {
                Message(player, "TooCloseToABuilding");
                e.Invoke(e.SafelyKill, 0.1f);
            }
            else AddPlayerEntity(e, raid);
        }

        private void AddPlayerEntity(BaseEntity e, RaidableBase raid)
        {
            raid.BuiltList.Add(e);
            raid.SetupEntity(e, false);

            if (e is ConstructableEntity || e.PrefabName.Contains("assets/prefabs/deployable/"))
            {
                if (config.Settings.Management.KeepDeployables)
                {
                    raid.DestroyGroundCheck(e);
                }
                else
                {
                    raid.AddEntity(e);
                }
            }
            else if (!config.Settings.Management.KeepStructures)
            {
                raid.AddEntity(e);
            }
        }

        private object OnElevatorButtonPress(ElevatorLift e, BasePlayer player, Elevator.Direction Direction, bool FullTravel)
        {
            var parent = e.IsValid() && e.HasParent() ? e.GetParentEntity() : e.owner;
            if (!parent.IsNetworked() || !Get(parent.transform.position, out var raid) || !raid.Elevators.TryGetValue(parent.net.ID, out var elevator))
            {
                return null;
            }
            if (elevator.IsBMG())
            {
                if (elevator.CanUseElevator(player))
                {
                    elevator.BMG.GoToFloor(Direction, FullTravel);
                    return null;
                }
                return true;
            }
            if (elevator.IsVanilla() && !elevator.CanUseElevator(player))
            {
                return true;
            }
            return null;
        }

        private void OnButtonPress(PressButton button, BasePlayer player)
        {
            if (button == null || button.OwnerID != 0 || button.IsDestroyed)
            {
                return;
            }
            var buttonPos = button.transform.position;
            if (!Get(buttonPos, out var raid) || raid.Elevators.Count == 0 || !raid.buttons.Contains(button))
            {
                return;
            }
            foreach (var ele in raid.Elevators.Values)
            {
                if (ele.IsVanilla())
                {
                    if (raid.Options.Elevators.RequiresPower && !button.IsPowered())
                    {
                        continue;
                    }
                    if (InRange2D(buttonPos, ele.Elevator.transform.position, 3f) && Abs(buttonPos.y - ele.Elevator.GetWorldSpaceFloorPosition(ele.Elevator.Floor).y) <= 1.5f && ele.CanUseElevator(player))
                    {
                        ele.Elevator.CallElevator();
                    }
                }
                else if (ele.IsBMG())
                {
                    if (BMGELEVATOR.GetElevatorLift(ele.BMG._elevator, out var lift) && InRange(buttonPos, lift.transform.position, 3f) && ele.CanUseElevator(player))
                    {
                        ele.BMG.GoToFloor(Elevator.Direction.Up, false, Mathf.CeilToInt(buttonPos.y));
                    }
                }
            }
        }

        private object OnElevatorMove(Elevator elevator, int targetFloor)
        {
            if (elevator.IsNetworked() && Get(elevator.transform.position, out var raid) && raid.Elevators.TryGetValue(elevator.net.ID, out var ele) && ele.IsBMG()) return true;
            return null;
        }

        private object OnElevatorCall(Elevator elevator, Elevator fromElevator) => OnElevatorMove(elevator, 0);

        private bool IsProtectedScientist(BasePlayer player, BaseEntity entity)
        {
            if (Has(player))
            {
                return false;
            }
            NPCPlayer npc = player as NPCPlayer;
            if (npc == null || string.IsNullOrEmpty(player.UserIDString))
            {
                return false;
            }
            if (!TypeNameLookup.TryGetValue(player.UserIDString, out string name))
            {
                TypeNameLookup[player.UserIDString] = name = player.GetType().Name;
            }
            if (!name.Contains("CustomScientist", CompareOptions.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!Get(npc.transform.position, out var raid) || !raid.Options.NPC.IgnorePlayerTrapsTurrets || !InRange(raid.Location, npc.spawnPos, raid.ProtectionRadius))
            {
                return false;
            }
            if (entity is AutoTurret turret && raid.Owns(turret))
            {
                turret.authorizedPlayers.Add(player.userID);
            }
            if (entity is StorageContainer && !IsContainerKilled(raid.priv))
            {
                raid.priv.authorizedPlayers.Add(player.userID);
            }
            return true;
        }

        private object OnNpcDuck(HumanoidNPC npc) => true;

        private object OnNpcDestinationSet(HumanoidNPC npc, Vector3 newDestination)
        {
            if (npc == null || !npc.NavAgent || !npc.NavAgent.enabled || !npc.NavAgent.isOnNavMesh)
            {
                return null;
            }

            if (!HumanoidBrains.TryGetValue(npc.userID, out var brain) || brain.CanRoam(newDestination))
            {
                return null;
            }

            return true;
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (!player.IsKilled() && player.IsHuman() && Get(player.transform.position, out var raid))
            {
                raid.StopUsingWeapon(player);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null)
                return;
            player.Invoke(() =>
            {
                if (player.IsDestroyed || !player.IsHuman())
                {
                    return;
                }

                if (data.Players.TryGetValue(player.UserIDString, out var info))
                {
                    info.Name = player.displayName.ToFriendlyJson();
                }

                if (GetPVPDelay(player.userID, false, out DelaySettings ds))
                {
                    if (config.UI.Delay.Enabled)
                    {
                        RemovePVPDelay(player.userID, ds);
                        UI.DestroyDelayUI(player);
                    }
                    ds.Destroy();
                }

                UI.DestroyStatusUI(player);

                if (!Get(player.transform.position, out var raid, 5f))
                {
                    return;
                }

                if (raid.IsUnderground(player.transform.position))
                {
                    raid.intruders.Remove(player.userID);
                    raid.enteredEntities.Remove(player);
                    return;
                }

                if (!config.Settings.Management.AllowTeleport && !raid.TeleportExceptions.Remove(player.userID) && !raid.CanBypass(player) && !raid.CanRespawnAt(player) && raid.Type != RaidableType.None && !raid.WasConnected(player))
                {
                    Message(player, "CannotTeleport");
                    raid.intruders.Remove(player.userID);
                    raid.RemovePlayer(player, raid.Location, raid.ProtectionRadius, raid.Type, true);
                }
                else
                {
                    if (!raid.intruders.Contains(player.userID))
                    {
                        raid.enteredEntities.Remove(player);
                    }
                    raid.HandlePlayerEntering(player);
                }
            }, 0.015f);
        }

        private object OnPlayerLand(BasePlayer player, float amount)
        {
            return player == null || !Get(player.transform.position, out var raid) || !raid.IsDespawning ? (object)null : true;
        }

        private object OnPlayerRespawn(BasePlayer player, BasePlayer.SpawnPoint spawnPoint)
        {
            if (player == null || !player.IsHuman() || !Get(spawnPoint.pos, out var raid, M_RADIUS))
            {
                return null;
            }

            for (int i = 0; i < 3; i++)
            {
                BasePlayer.SpawnPoint replacement = ServerMgr.FindSpawnPoint(player);

                if (!EventTerritory(replacement.pos, M_RADIUS))
                {
                    return replacement;
                }
            }

            spawnPoint.pos = raid.GetEjectLocation(spawnPoint.pos, 100f, raid.Location, raid.ProtectionRadius * 2f, towardsZero: true, setHeight: true);
            return spawnPoint;
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null && info != null)
            {
                player = info.HitEntity as BasePlayer;
            }

            if (player == null || !Get(player, info, out var raid))
            {
                return;
            }

            if (!player.IsHuman())
            {
                if (!Has(player) || !HumanoidBrains.TryGetValue(player.userID, out var brain))
                {
                    return;
                }

                brain.DisableShouldThink();

                var attacker = info?.Initiator as BasePlayer;

                if (config.Settings.Management.UseOwners && attacker != null && raid.AddLooter(attacker) && !raid.ownerId.IsSteamId())
                {
                    raid.TrySetOwner(attacker, player, info);
                }

                if (!raid.IsEngaged && raid.EngageOnNpcDeath && attacker != null && attacker.IsHuman() && !attacker.limitNetworking && !attacker.IsFlying)
                {
                    raid.IsEngaged = true;
                }

                raid.CheckDespawn();
            }
            else
            {
                if (CanDropPlayerBackpack(player, raid))
                {
                    Backpacks?.Call("API_DropBackpack", player);
                }

                if (!raid.intruders.Contains(player.userID))
                {
                    raid.OnPlayerExited(player);
                }

                raid.HandlePlayerExiting(player);
                raid.HandleTurretSight(player);
            }
        }

        private object OnBackpackDrop(Item backpack, PlayerInventory inv)
        {
            if (backpack == null || inv == null || inv.baseEntity == null) return null;
            BasePlayer player = inv.baseEntity;
            if (!player.IsHuman() || !Get(player, player.userID, out var raid)) return null;
            if (raid.CanDropRustBackpack(player.userID))
            {
                backpack.RemoveFromContainer();
                backpack.Drop(player.GetDropPosition() + new Vector3(0f, 0.035f), player.GetDropVelocity());
                return null;
            }
            return true;
        }

        private void DropRustBackpack(PlayerCorpse corpse)
        {
            if (corpse?.containers != null)
            {
                var position = corpse.GetDropPosition() + new Vector3(0f, 0.035f);
                var velocity = corpse.GetDropVelocity();
                foreach (var container in corpse.containers)
                {
                    if (container != null && container.itemList != null)
                    {
                        for (int i = container.itemList.Count - 1; i >= 0; i--)
                        {
                            Item item = container.itemList[i];
                            if (item != null && item.IsBackpack() && item.contents != null && !item.contents.itemList.IsNullOrEmpty())
                            {
                                if (PreventLooting != null) item.RemoveFromContainer();
                                item.Drop(position, velocity);
                            }
                        }
                    }
                }
            }
        }

        private void DropRustBackpack(DroppedItemContainer backpack)
        {
            if (backpack?.inventory?.itemList != null)
            {
                var position = backpack.GetDropPosition() + new Vector3(0f, 0.035f);
                var velocity = backpack.GetDropVelocity();
                for (int i = backpack.inventory.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = backpack.inventory.itemList[i];
                    if (item != null && item.IsBackpack() && item.contents != null && !item.contents.itemList.IsNullOrEmpty())
                    {
                        if (PreventLooting != null) item.RemoveFromContainer();
                        item.Drop(position, velocity);
                    }
                }
            }
        }

        private object OnPlayerDropActiveItem(BasePlayer player, Item item)
        {
            return EventTerritory(player.transform.position) ? true : (object)null;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsKilled() && !player.HasPermission("raidablebases.allow.commands"))
            {
                List<string> commands =
                    Get(player.transform.position, out var raid) ? raid.BlacklistedCommands :
                    config.Settings.Management.PVPDelayPersists && GetPVPDelay(player.userID, true, out var ds) && ds.raid != null ? ds.raid.BlacklistedCommands : null;
                if (commands != null && commands.Exists(value => command.EndsWith(value, StringComparison.OrdinalIgnoreCase)))
                {
                    Message(player, "CommandNotAllowed");
                    return true;
                }
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            return OnPlayerCommand(arg.Player(), arg.cmd.FullName, Array.Empty<string>());
        }

        private void OnEntityDeath(BuildingPrivlidge priv, HitInfo info)
        {
            if (!Get(priv, out var raid) || raid.priv != priv)
            {
                return;
            }

            if (!raid.IsEngaged && raid.EngageOnBaseDamage)
            {
                raid.IsEngaged = true;
            }

            if (!raid.IsDespawning && config.Settings.Management.AllowCupboardLoot)
            {
                DropOrRemoveItems(priv, raid, true, false);
            }

            if (raid.Options.RequiresCupboardAccess)
            {
                OnCupboardAuthorize(priv, null);
            }

            if (raid.GetInitiatorPlayer(info, DamageType.Generic, priv, out var attacker))
            {
                raid.GetRaider(attacker).HasDestroyed = true;
            }

            raid.OnBuildingPrivilegeDestroyed();
        }

        private void OnEntityKill(StorageContainer container)
        {
            if (container is BuildingPrivlidge priv)
            {
                OnEntityDeath(priv, null);
            }
            if (container != null)
            {
                EntityHandler(container, null);
            }
        }

        private void OnEntityDeath(StorageContainer container, HitInfo info) => EntityHandler(container, info);

        //private void OnEntityKill(BuildingBlock block) => OnEntityDeath(block, new HitInfo(block.lastAttacker, block, DamageType.Explosion, 9999f)); // ent kill testing
        private void OnEntityKill(Fridge io) => OnEntityDeath(io, null);

        private void OnEntityDeath(StabilityEntity entity, HitInfo info)
        {
            if (info == null || !Get(entity.transform.position, out var raid) || raid.IsDespawning || !raid.GetInitiatorPlayer(info, DamageType.Generic, entity, out var attacker))
            {
                return;
            }

            if (raid.AddLooter(attacker))
            {
                raid.AddMember(attacker.userID);

                raid.TrySetOwner(attacker, entity, info);

                raid.GetRaider(attacker).HasDestroyed = true;
            }

            if (raid.CanSetPVPDelay(attacker))
            {
                raid.TrySetPVPDelay(attacker, false, false, "AttackableFromOutside");
            }

            raid.CheckDespawn();

            if (raid.IsDamaged)
            {
                return;
            }

            if (entity is BuildingBlock or Door)
            {
                raid.IsDamaged = true;
            }
        }

        private object OnEntityGroundMissing(StorageContainer container)
        {
            return Get(container, out var raid) && !raid.CanHurtBox(container) ? true : (object)null;
        }

        //private void OnEntityKill(IOEntity io) => OnEntityDeath(io, null);

        private void OnEntityDeath(IOEntity io, HitInfo info)
        {
            if (io.IsKilled() || !config.Settings.Management.DropLoot.Get(io))
            {
                return;
            }
            if (!Get(io, out var raid) || raid.IsDespawning || raid.IsLoading)
            {
                return;
            }
            if (io is Fridge fridge && raid.fridges.Remove(fridge))
            {
                BaseEntity drop = DropLoot(io, fridge.inventory, raid.Options.BuoyantBox);
                if (raid.Options.DespawnGreyBoxBags) raid.SetupEntity(drop);
            }
            else if (io is AutoTurret turret && raid.TryRemoveOrConfirmTurret(turret))
            {
                BaseEntity drop = DropLoot(io, turret.inventory, raid.Options.BuoyantBox);
                if (config.Settings.Management.DropLoot.DespawnGreyWeaponBags) raid.SetupEntity(drop);
            }
            else if (io is SamSite samsite && raid.samsites.Remove(samsite))
            {
                BaseEntity drop = DropLoot(io, samsite.inventory, raid.Options.BuoyantBox);
                if (config.Settings.Management.DropLoot.DespawnGreyWeaponBags) raid.SetupEntity(drop);
            }
        }

        private void EntityHandler(StorageContainer container, HitInfo info)
        {
            if (!Get(container, out var raid) || raid.IsDespawning || raid.IsLoading)
            {
                return;
            }

            if (!raid.IsEngaged && raid.EngageOnBaseDamage)
            {
                raid.IsEngaged = true;
            }

            DropOrRemoveItems(container, raid, false, false);

            if (raid._containers.Remove(container))
            {
                Interface.CallHook("OnRaidableLootDestroyed", raid.Location, raid.ProtectionRadius, raid.GetLootAmountRemaining(), container, 512);
            }

            if (!raid.IsAnyLooted && info != null)
            {
                raid.IsAnyLooted = info.Initiator is BasePlayer || info.damageTypes.Has(DamageType.Heat);
            }

            if (IsLootingWeapon(info) && raid.GetInitiatorPlayer(info, DamageType.Generic, container, out var attacker) && raid.AddLooter(attacker))
            {
                raid.GetRaider(attacker).HasDestroyed = true;
            }

            if (raid.IsOpened && (IsBox(container, true) || container is BuildingPrivlidge))
            {
                raid.TryToEnd();
            }

            if (!Raids.Exists(x => x._containers.Count > 0))
            {
                Unsubscribe(nameof(OnEntityKill));
                Unsubscribe(nameof(OnEntityGroundMissing));
            }
        }

        private static bool IsLootingWeapon(HitInfo info)
        {
            if (info == null || info.damageTypes == null)
            {
                return false;
            }

            return info.damageTypes.Has(DamageType.Explosion) || info.damageTypes.Has(DamageType.Heat) || info.damageTypes.IsMeleeType() || info.WeaponPrefab is TimedExplosive;
        }

        private void OnCupboardAuthorize(BuildingPrivlidge priv, BasePlayer player)
        {
            bool isHookNeeded = false;

            foreach (var raid in Raids)
            {
                if (!raid.IsAuthed && raid.Options.RequiresCupboardAccess && raid.priv == priv)
                {
                    raid.IsAuthed = true;

                    if (config.EventMessages.AnnounceRaidUnlock)
                    {
                        foreach (var target in BasePlayer.activePlayerList)
                        {
                            if (!raid.IsRaider(target) && target.HasPermission("raidablebases.limitedannouncements")) continue;
                            raid.QueueNotification(target, "OnRaidFinished", FormatGridReference(target, raid.Location));
                        }
                    }
                }

                if (!raid.IsAuthed)
                {
                    isHookNeeded = true;
                }
            }

            if (!isHookNeeded)
            {
                Unsubscribe(nameof(OnCupboardAuthorize));
            }
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (!Get(entity, out var raid))
            {
                return null;
            }

            if (player.IsNetworked())
            {
                if (entity is BaseLadder || player.userID == entity.OwnerID)
                {
                    return true;
                }
                if (!raid.AddLooter(player))
                {
                    return raid.CanBypass(player);
                }
            }

            if (raid.IsPickupBlacklisted(entity.ShortPrefabName) || entity is DroppedItem di && di.item != null && raid.IsPickupBlacklisted(di.item.info.shortname))
            {
                return false;
            }

            if (!raid.Options.AllowPickup && entity.OwnerID == 0 && !raid.IsPickupAllowed(entity.ShortPrefabName))
            {
                return false;
            }

            if (entity.OwnerID == 0uL)
            {
                if (TryRemoveItems(entity))
                {
                    ItemManager.DoRemoves();
                }
                if (config.BlockPaidContent && DeployableItems.TryGetValue(entity.PrefabName, out var def))
                {
                    if (entity.skinID == RB_SKIN_ID) entity.skinID = 0;
                    if (RequiresOwnership(def, 0) && !HasUnlocked(player, def)) return false;
                    if (RequiresOwnership(def, entity.skinID) && !HasUnlocked(player, entity.skinID))
                    {
                        entity.skinID = 0;
                        entity.SendNetworkUpdateImmediate();
                    }
                    return null;
                }
            }

            if (entity.skinID == RB_SKIN_ID)
            {
                entity.skinID = 0;
            }

            return null;
        }

        private void OnFireBallSpread(FireBall fire, BaseEntity spread)
        {
            if (!spread.IsKilled() && EventTerritory(spread.transform.position))
            {
                spread.DelayedSafeKill();
            }
        }

        private void OnFireBallDamage(FireBall fire, BaseCombatEntity target, HitInfo info)
        {
            if (info != null && (info.Initiator == null || info.Initiator is FireBall) && !fire.IsKilled() && EventTerritory(fire.transform.position))
            {
                info.Initiator ??= fire.creatorEntity;
            }
        }

        private object CanMlrsTargetLocation(MLRS mlrs, BasePlayer player)
        {
            return Get(mlrs.TrueHitPos, out var raid, 25f) ? raid.Options.MLRS : (object)null;
        }

        private object OnMlrsFire(MLRS mlrs, BasePlayer player)
        {
            if (!Get(mlrs.TrueHitPos, out var raid, 25f) || raid.Options.MLRS) return null;
            Message(player, "MLRS Target Denied");
            return true;
        }

        private object OnNearbyTurretsScan(AutoTurret turret)
        {
            return OnInterferenceUpdate(turret);
        }

        private object OnInterferenceUpdate(AutoTurret turret)
        {
            if (turret == null || turret.IsDestroyed) return null;
            if (!Get(turret.transform.position, out var raid)) return null;
            if (!turret.enableSaving || raid.Owns(turret)) return true;
            return turret.OwnerID.IsSteamId() ? true : null;
        }

        private void OnEntitySpawned(TimedExplosive te)
        {
            if (te.IsKilled())
            {
                return;
            }
            var rocket = te as MLRSRocket;
            if (rocket != null)
            {
                OnEntitySpawnedMLRS(rocket);
                return;
            }
            if (te.creatorEntity == null && Get(te.transform.position, out var raid) && raid.UsableByTurret)
            {
                var pos = te.transform.position;
                foreach (var info in raid.turrets.Values)
                {
                    if (!IsContainerKilled(info) && InRange(info.turret.transform.position, pos, 3f))
                    {
                        te.creatorEntity = info.turret;
                        break;
                    }
                }
            }
        }

        private void OnEntitySpawnedMLRS(MLRSRocket rocket)
        {
            using var systems = FindEntitiesOfType<MLRS>(rocket.transform.position, 15f);
            if (systems.Count == 0 || !Get(systems[0].TrueHitPos, out var raid))
            {
                return;
            }
            BasePlayer owner = systems[0].rocketOwnerRef.Get(true) as BasePlayer;
            if (!raid.Options.MLRS)
            {
                if (owner != null) Message(owner, "MLRS Target Denied");
                else raid.Message("MLRS Target Denied");
                rocket.Invoke(rocket.SafelyKill, 0.1f);
                rocket.playerDamage?.Clear();
                rocket.damageTypes?.Clear();
            }
            else if (owner != null)
            {
                rocket.creatorEntity = owner;
                rocket.OwnerID = owner.userID;
            }
        }

        private void OnEntitySpawned(FireBall fire)
        {
            if (fire.IsKilled() || !Get(fire.transform.position, out var raid))
            {
                return;
            }
            else if (config.Settings.Management.PreventFireFromSpreading && fire.ShortPrefabName == "flamethrower_fireball" && fire.creatorEntity is BasePlayer player && !player.userID.IsSteamId())
            {
                fire.DelayedSafeKill();
            }
            else if (raid.cached_attacker != null && !(fire.creatorEntity is BasePlayer) && Time.realtimeSinceStartupAsDouble - raid.cached_attack_time < 1f && raid.raiders.ContainsKey(raid.cached_attacker_id))
            {
                fire.creatorEntity = raid.cached_attacker;
            }
            raid.cached_attacker = null;
            raid.cached_attacker_id = 0;
            raid.cached_attack_time = 0;
        }

        private List<ulong> NpcCorpse = new();
        private void OnEntitySpawned(DroppedItemContainer backpack)
        {
            if (backpack.IsKilled())
            {
                return;
            }
            backpack.Invoke(() =>
            {
                if (IsUnloading || backpack.IsDestroyed || !Get(backpack, backpack.playerSteamID, out var raid))
                {
                    return;
                }
                if (backpack.ShortPrefabName == "item_drop" || backpack.ShortPrefabName == "item_drop_buoyant")
                {
                    backpack.buryLeftoverItems = false;
                    return;
                }
                if (backpack.playerSteamID.IsSteamId())
                {
                    if (raid.CanDropRustBackpack(backpack.playerSteamID))
                    {
                        DropRustBackpack(backpack);
                    }
                    if (raid.CanDropBackpack(backpack.playerSteamID))
                    {
                        backpack.playerSteamID = 0;
                        return;
                    }
                }
                else if (NpcCorpse.Remove(backpack.playerSteamID))
                {
                    backpack.skinID = RB_SKIN_ID;
                    raid.SetupEntity(backpack);
                }
            }, 0.1f);
        }

        private void OnEntitySpawned(BaseLock entity)
        {
            if (entity.IsKilled() || !Get(entity.transform.position, out var raid) || raid.IsLoading)
            {
                return;
            }
            if (entity.GetParentEntity() is StorageContainer parent && raid._containers.Contains(parent))
            {
                entity.DelayedSafeKill();
            }
        }

        private void OnEntitySpawned(PlayerCorpse corpse)
        {
            if (corpse.IsKilled() || !Get(corpse, corpse.playerSteamID, out var raid))
            {
                return;
            }

            ulong playerSteamID = corpse.playerSteamID;
            if (playerSteamID.IsSteamId())
            {
                if (Interface.CallHook("OnRaidablePlayerCorpseCreate", new object[] { corpse, raid.Location, raid.AllowPVP, 512, raid.GetOwner(), raid.GetRaiders(), raid.BaseName, raid.PlayersLootable }) != null)
                {
                    return;
                }

                if ((raid.Options.EjectBackpacks || raid.EjectBackpacksPVE) && !playerSteamID.HasPermission("reviveplayer.use"))
                {
                    if (corpse.containers.IsNullOrEmpty())
                    {
                        goto done;
                    }

                    var container = GameManager.server.CreateEntity("assets/prefabs/misc/item drop/item_drop_backpack.prefab", corpse.transform.position) as DroppedItemContainer;
                    container.maxItemCount = 48;
                    container.lootPanelName = "generic_resizable";
                    container.playerName = corpse.playerName;
                    container.playerSteamID = playerSteamID;
                    container.Spawn();

                    if (container.IsKilled())
                    {
                        goto done;
                    }

                    container.TakeFrom(corpse.containers, 0f);
                    corpse.Invoke(corpse.SafelyKill, 0.0625f);

                    var player = RustCore.FindPlayerById(playerSteamID);
                    var backpack = raid.AddBackpack(container, playerSteamID, player);
                    bool canEjectBackpack = Interface.CallHook("OnRaidableBaseBackpackEject", new object[] { container, playerSteamID, raid.Location, raid.AllowPVP, 512, raid.GetOwner(), raid.GetRaiders(), raid.BaseName, raid.PlayersLootable }) == null;

                    if (canEjectBackpack && raid.EjectBackpack(backpack, raid.EjectBackpacksPVE))
                    {
                        raid.backpacks.Remove(backpack);
                        ResetToPool(ref backpack);
                    }

                    if (raid.PlayersLootable)
                    {
                        container.playerSteamID = 0;
                    }

                    return;
                }

            done:

                if (raid.CanDropRustBackpack(playerSteamID))
                {
                    DropRustBackpack(corpse);
                }

                if (raid.PlayersLootable)
                {
                    corpse.playerSteamID = 0;
                }
            }
        }

        private object CanBuild(Planner planner, Construction construction, Construction.Target target)
        {
            var buildPos = target.entity && target.entity.transform && target.socket ? target.GetWorldPosition() : target.position;
            if (!Get(buildPos, out var raid, Mathf.Clamp(construction.bounds.size.Max() * 0.85f, 2.4f, 4f)))
            {
                return null;
            }

            if (target.player != null && !InRange(raid.Location, target.player.transform.position, raid.ProtectionRadius - 0.6f))
            {
                Message(target.player, "Building is blocked!");
                return false;
            }

            if (!raid.Options.AllowBuildingPriviledges && CupboardPrefabIDs.Contains(construction.prefabID))
            {
                Message(target.player, "Cupboards are blocked!");
                return false;
            }
            else if (construction.prefabID == 2150203378)
            {
                if (!config.Settings.Management.AllowLadders || raid.Options.RequiresCupboardAccessLadders && !raid.CanBuild(target.player))
                {
                    Message(target.player, "Ladders are blocked!");
                    return false;
                }
                if (raid.raiders.TryGetValue(target.player.userID, out var ri) && ri.Input != null)
                {
                    ri.Input.Restart();
                    ri.Input.TryPlace(ConstructionType.Ladder);
                }
            }
            else if (construction.fullName.Contains("/barricades/barricade."))
            {
                if (raid.Options.AllowBarricades)
                {
                    if (raid.raiders.TryGetValue(target.player.userID, out var ri) && ri.Input != null)
                    {
                        ri.Input.Restart();
                        ri.Input.TryPlace(ConstructionType.Barricade);
                    }
                }
                else
                {
                    Message(target.player, "Barricades are blocked!");
                    return false;
                }
            }
            else if (!raid.Options.AllowBuilding)
            {
                var value = GetFileNameWithoutExtension(construction.fullName);
                if (value != "explosivesiegedeployable" && !raid.Options.AllowedBuildingBlockExceptions.Exists(value.Contains))
                {
                    Message(target.player, "Building is blocked!");
                    return false;
                }
            }

            return null;
        }

        [HookMethod("AddLootToDifficultyProfile")]
        public bool AddLootToDifficultyProfile(string mode, List<object[]> lootObjects)
        {
            if (lootObjects == null || lootObjects.Count < 1 || !Buildings.DifficultyLootLists.TryGetValue(mode, out var lootList))
            {
                return false;
            }

            bool success = false;
            foreach (var obj in lootObjects)
            {
                if (!(obj[0] is string shortname)) continue;
                int amountMin = obj.Length > 1 && obj[1] is int v1 ? v1 : 1;
                int amountMax = obj.Length > 2 && obj[2] is int v2 ? v2 : 1;
                ulong skin = obj.Length > 3 && obj[3] is ulong v3 ? v3 : 0;
                float probability = obj.Length > 4 && obj[4] is float v4 ? v4 : 1.0f;
                string displayName = obj.Length > 5 && obj[5] is string v5 ? v5 : null;
                int stackSize = obj.Length > 6 && obj[6] is int v6 ? v6 : -1;
                string text = obj.Length > 7 && obj[7] is string v7 ? v7 : null;

                LootItem ti = new(shortname, amountMin, amountMax, skin, false, probability, stackSize, displayName, text);
                ti.InitializeArmorSlots();
                lootList.Add(ti);
                success = true;
            }

            return success;
        }

        private void OnLootEntityEnd(BasePlayer player, ContainerIOEntity container)
        {
            if (config.BlockPaidContent && config.DestroyLootedContainer && !IsContainerKilled(container) && container.inventory.IsEmpty() && PaidDeployableItems.TryGetValue(container.PrefabName, out var def) && Has(container) && RequiresOwnership(def, container.skinID))
            {
                container.Invoke(container.SafelyKill, 0.1f);
            }
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer container)
        {
            if (player == null || player.limitNetworking || IsContainerKilled(container) || container.OwnerID.IsSteamId() || !Get(container, out var raid))
            {
                return;
            }

            if (player.userID.IsSteamId())
            {
                raid.IsAnyLooted = true;
            }

            bool kill = config.BlockPaidContent && config.DestroyLootedContainer && container.inventory.IsEmpty() && PaidDeployableItems.TryGetValue(container.PrefabName, out var def) && RequiresOwnership(def, container.skinID);
            if (kill)
            {
                container.Invoke(container.SafelyKill, 0.1f);
            }

            if (raid.Options.DropTimeAfterLooting <= 0 || (raid.Options.DropOnlyBoxesAndPrivileges && !IsBox(container, true) && !(container is BuildingPrivlidge)))
            {
                raid.TryToEnd();
                return;
            }

            if (container.inventory.IsEmpty() && IsBox(container, false))
            {
                if (!kill) container.Invoke(container.SafelyKill, 0.1f);
            }
            else container.Invoke(() => DropOrRemoveItems(container, raid, false, true), raid.Options.DropTimeAfterLooting);

            raid.TryToEnd();
        }

        private object CanLootDroppedItemContainer(BasePlayer player, BaseEntity entity) => entity switch
        {
            _ when entity.skinID != RB_SKIN_ID || !entity.OwnerID.IsSteamId() || entity.OwnerID == player.userID => null,
            _ when RelationshipManager.ServerInstance.playerToTeam.TryGetValue(entity.OwnerID, out var team) && team.members.Contains(player.userID) => null,
            _ when Convert.ToBoolean(Clans?.Call("IsClanMember", entity.OwnerID.ToString(), player.UserIDString)) => null,
            _ when Convert.ToBoolean(Friends?.Call("AreFriends", entity.OwnerID.ToString(), player.UserIDString)) => null,
            _ => ((Func<object>)(() => { Message(player, "You do not own this loot!"); return true; }))(),
        };

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity.IsKilled()) return null;
            if (CanLootDroppedItemContainer(player, entity) != null) return true;
            return Get(entity.transform.position, out var raid) ? raid.CanLootEntityInternal(player, entity) : (object)null;
        }

        private object CanBePenalized(BasePlayer player)
        {
            return Get(player, null, out var raid) && (raid.AllowPVP && !raid.Options.PenalizePVP || !raid.AllowPVP && !raid.Options.PenalizePVE) ? false : (object)null;
        }

        private object CanOpenBackpack(BasePlayer looter, ulong backpackOwnerID)
        {
            if (!Get(looter.transform.position, out var raid))
            {
                return null;
            }

            if (!raid.AllowPVP && !config.Settings.Management.BackpacksOpenPVE || raid.AllowPVP && !config.Settings.Management.BackpacksOpenPVP)
            {
                return lang.GetMessage("NotAllowed", this, looter.UserIDString);
            }

            return null;
        }

        private bool CanDropPlayerBackpack(BasePlayer player, RaidableBase raid)
        {
            if (GetPVPDelay(player.userID, true, out DelaySettings ds) && ds.raid != null && ds.raid.CanDropBackpack(player.userID))
            {
                return true;
            }

            return InRange(raid.Location, player.transform.position, raid.ProtectionRadius) && raid.CanDropBackpack(player.userID);
        }

        private bool ShouldIgnoreFlyingPlayer(BasePlayer player)
        {
            if (!config.Settings.Management.IgnoreFlying || !player.IsFlying) return false;
            Transform t = player.transform; // if this is null, your server is fucked and needs a restart
            return t != null && EventTerritory(t.position);
        }

        private static bool IsDangerousEvent(BaseEntity entity) => entity is StorageContainer && !entity.enableSaving && entity.OwnerID == 0;

        private static bool IsSputnik(BaseEntity entity) => entity != null && entity.ShortPrefabName == "large.rechargable.battery.deployed" && entity.OwnerID == 0 && !entity.enableSaving;

        private bool IsEventEntity(BaseEntity entity, float dist, float protectionRadius) => !entity.OwnerID.IsSteamId() && dist <= protectionRadius || IsAbandonedEntity(entity);

        private bool IsAbandonedEntity(BaseEntity entity) => AbandonedBases != null && Convert.ToBoolean(AbandonedBases?.Call("isAbandoned", entity));

        private bool IsArmoredTrain(BaseEntity entity) => entity.OwnerID == 0uL && entity is AutoTurret turret && !turret.isLootable && !turret.dropFloats && turret.parentEntity.IsSet();

        private static bool IsEventDrone(BaseEntity entity) => entity.OwnerID == 335576777746;
        private bool IsSentryTargetingNpc(BasePlayer player, BaseEntity entity) => entity is NPCAutoTurret && !Has(player) && !player.userID.IsSteamId();

        private bool IgnorePlayer(BasePlayer player, BaseEntity entity) => player.limitNetworking || IsSentryTargetingNpc(player, entity) || IsArmoredTrain(entity);

        private bool IsPositionInSpace(Vector3 a, Vector3 b, float r) => Space != null && a.y - b.y > r + M_RADIUS;

        private object OnEntityEnter(TriggerBase trigger, Drone drone)
        {
            if (drone == null || drone.IsDestroyed || !Get(trigger, out var raid)) return null;
            if (drone is DeliveryDrone) return true;
            return !InRange(drone.transform.position, raid.Location, config.Weapons.SamSiteRange) ? true : (object)null;
        }

        private object OnEntityEnter(TriggerBase trigger, BasePlayer player)
        {
            if (trigger == null || player.IsKilled()) return null;
            if (ShouldIgnoreFlyingPlayer(player)) return true;
            if (Has(player) && (Has(trigger) || (Get(player.userID, out HumanoidBrain brain) && brain.raid.Options.NPC.IgnorePlayerTrapsTurrets))) return true;
            BaseEntity entity = trigger is TriggerParent p ? p.Entity : trigger.gameObject.ToBaseEntity();
            if (IsProtectedScientist(player, entity)) return true;
            return CanEntityBeTargetedInternal(player, entity, IsPVE()) is true or null ? (object)null : true;
        }

        private bool _subscribeOnEntityEnterHopper = true;
        private object OnEntityEnter(TriggerEnterTimer trigger, BaseEntity target)
        {
            if (!_subscribeOnEntityEnterHopper || trigger == null) return null;
            Hopper hopper = trigger.gameObject.ToBaseEntity() as Hopper;
            return CanEntityBeTargetedInternal(target, hopper) is bool val && !val ? true : (object)null;
        }

        private object CanEntityBeTargeted(BaseEntity target, Hopper hopper)
        {
            _subscribeOnEntityEnterHopper = false;
            return CanEntityBeTargetedInternal(target, hopper) is bool val ? val : (object)null;
        }

        private object CanEntityBeTargetedInternal(BaseEntity target, Hopper hopper)
        {
            if (target.IsKilled() || hopper.IsKilled())
            {
                return null;
            }

            if (!Get(target.transform.position, out var raid) && !Get(hopper.transform.position, out raid))
            {
                return null;
            }

            if (hopper.OwnerID == 0 && raid.Owns(hopper))
            {
                return true;
            }

            if (hopper.OwnerID != 0 && !InRange(hopper.transform.position, raid.Location, raid.ProtectionRadius))
            {
                return false;
            }

            DroppedItem di = target as DroppedItem;
            if (di != null)
            {
                return raid.AllowPVP || di.DroppedBy == 0 || di.DroppedBy == hopper.OwnerID || raid.IsAlly(di.DroppedBy, hopper.OwnerID);
            }

            PlayerCorpse corpse = target as PlayerCorpse;
            if (corpse != null)
            {
                return raid.AllowPVP || corpse.playerSteamID == hopper.OwnerID || raid.IsAlly(corpse.playerSteamID, hopper.OwnerID);
            }

            return null;
        }

        private object CanEntityBeTargeted(BasePlayer player, BaseEntity entity)
        {
            if (player.IsKilled()) return null;
            return CanEntityBeTargetedInternal(player, entity, false);
        }

        private object CanEntityBeTargetedInternal(BasePlayer player, BaseEntity entity, bool earlyExit)
        {
            if (entity.IsKilled() || IgnorePlayer(player, entity))
            {
                return null;
            }

            if (!Get(player.transform.position, out var raid) && !Get(entity.transform.position, out raid))
            {
                return null;
            }

            if (earlyExit && (!raid.Options.BlockOutsideDamageToPlayersInside && !raid.Options.NPC.BlockOutsideDamageToNpcsInside))
            {
                return null;
            }

            if (Has(player))
            {
                if (entity.skinID == 3358068268)
                {
                    return null;
                }
                AutoTurret turret = entity as AutoTurret;
                if (entity.OwnerID.IsSteamId() ? raid.Options.NPC.IgnorePlayerTrapsTurrets : raid.Options.NPC.IgnoreTrapsTurrets)
                {
                    if (turret != null)
                    {
                        turret.SetNoTarget();
                        return null;
                    }
                    return false;
                }
                if (raid.Options.NPC.BlockOutsideDamageToNpcsInside && Has(player) && CanBlockOutsideDamage(raid, entity) && InRange(player.transform.position, raid.Location, raid.ProtectionRadius))
                {
                    if (turret != null)
                    {
                        turret.SetNoTarget();
                        return null;
                    }
                    return false;
                }
                return entity.OwnerID.IsSteamId() ? !raid.Options.NPC.IgnorePlayerTrapsTurrets : !Has(entity);
            }

            if (player.IsHuman())
            {
                bool? owns = null;
                AutoTurret turret = entity as AutoTurret;
                if (raid.Options.BlockOutsideDamageToPlayersInside && !(owns ??= raid.Owns(entity)) && CanBlockOutsideDamage(raid, entity))
                {
                    if (turret != null)
                    {
                        turret.SetNoTarget();
                        return null;
                    }
                    return false;
                }
                if (turret != null)
                {
                    var success = raid.OnTurretTarget(turret, player, owns ??= raid.Owns(turret));
                    if (success == DamageResult.None) return null;
                    return success == DamageResult.Allowed;
                }
                return entity is BaseDetector || HasPVPDelay(player.userID) || (owns ?? raid.Owns(entity));
            }

            return IsEventDrone(entity) ? (object)null : entity.OwnerID.IsSteamId() ? !raid.Options.NPC.IgnorePlayerTrapsTurrets : !raid.Options.NPC.IgnoreTrapsTurrets;
        }

        private object CanEntityBeTargeted(BaseEntity entity, SamSite ss)
        {
            if (entity.IsKilled() || ss.IsKilled())
            {
                return null;
            }

            if (Get(ss.transform.position, out var raid) && !IsPositionInSpace(entity.transform.position, raid.Location, raid.ProtectionRadius))
            {
                if (raid.IsLoading || HasRaidEntity(entity) && raid.Owns(ss))
                {
                    return false;
                }
                return (entity.transform.position - ss.transform.position).sqrMagnitude <= config.Weapons.SamSiteRange * config.Weapons.SamSiteRange;
            }

            return null;
        }

        private object OnSamSiteTargetScan(SamSite ss, List<SamSite.ISamSiteTarget> obj)
        {
            if (ss.IsKilled())
            {
                return null;
            }
            var a = ss.transform.position;
            if (!Get(a, out var raid))
            {
                return null;
            }
            if (!raid.IsLoading)
            {
                var sqrDistance = config.Weapons.SamSiteRange * config.Weapons.SamSiteRange;
                foreach (SamSite.ISamSiteTarget server in SamSite.ISamSiteTarget.serverList)
                {
                    if (server == null)
                    {
                        continue;
                    }
                    BaseEntity entity = server as BaseEntity;
                    if (entity == null || entity.IsDestroyed)
                    {
                        continue;
                    }
                    var b = server.CenterPoint();
                    var isValidTarget = server is MLRSRocket || (!HasRaidEntity(entity) && !ss.IsInDefenderMode() && !IsPositionInSpace(b, raid.Location, raid.ProtectionRadius));
                    if (isValidTarget && (a - b).sqrMagnitude <= sqrDistance)
                    {
                        obj.Add(server);
                    }
                }
                if (config.Weapons.SamSiteRepair > 0f && ss.staticRespawn && obj.Count > 0f)
                {
                    ss.staticRespawn = false;
                    ss.Invoke(() => ss.staticRespawn = true, 0.1f);
                }
            }

            return true;
        }

        private object OnTrapTrigger(BaseTrap trap, GameObject go)
        {
            var player = go.GetComponent<BasePlayer>();
            var success = CanEntityTrapTrigger(trap, player);

            return success is bool val && !val ? true : (object)null;
        }

        private object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            if (player == null || player.limitNetworking)
            {
                return null;
            }

            if (Has(player))
            {
                return false;
            }

            if (!Get(trap, out var raid))
            {
                return null;
            }

            if (raid.Options.RearmBearTraps && trap is BearTrap)
            {
                trap.Invoke(trap.Arm, 0.1f);
            }

            return true;
        }

        private void OnCupboardProtectionCalculated(BuildingPrivlidge priv, float cachedProtectedMinutes)
        {
            if (HasRaidEntity(priv))
            {
                priv.cachedProtectedMinutes = 1500;
            }
        }

        private object CanEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || entity == null || entity.IsDestroyed || entity.OwnerID == 1337422)
            {
                return null;
            }

            if (info.Initiator != null)
            {
                switch (info.Initiator.OwnerID)
                {
                    case 1309:
                    case 13099:
                    case 8002738255:
                    case 335576777746:
                        return null;
                }
            }

            DamageType damageType = info.damageTypes.GetMajorityDamageType();
            DamageResult success = entity is BasePlayer player ?
                HandlePlayerDamage(player, info, damageType, out var raid, out var attacker, out var isHuman) :
                HandleEntityDamage(entity, info, damageType, out raid, out attacker, out isHuman);

            if (success == DamageResult.None)
            {
                return null;
            }

            if (success == DamageResult.Blocked)
            {
                if (info.Weapon is BlowPipeWeapon)
                {
                    info.HitEntity = null;
                }
                return NullifyDamage(info);
            }

            if (isHuman && damageType != DamageType.Heat && raid != null)
            {
                raid.GetRaider(attacker).lastActiveTime = Time.realtimeSinceStartupAsDouble;
            }

            return true;
        }

        protected void UnsubscribeDamageHook()
        {
            if (Raids.Count > 0 || config == null || config.Settings.Management.PVPDelayPersists && PvpDelay.Count > 0)
            {
                return;
            }
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(CanEntityTakeDamage));
        }

        private void SubscribeDamageHook()
        {
            if (IsPVE())
            {
                Unsubscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(CanEntityTakeDamage));
            }
            else
            {
                Unsubscribe(nameof(CanEntityTakeDamage));
                Subscribe(nameof(OnEntityTakeDamage));
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info) => CanEntityTakeDamage(entity, info);

        private DamageResult HandlePlayerDamage(BasePlayer victim, HitInfo info, DamageType damageType, out RaidableBase raid, out BasePlayer attacker, out bool isHuman)
        {
            BaseEntity initiator = info.Initiator;
            attacker = null;
            isHuman = false;

            if (!Get(victim, info, out raid) || raid.IsDespawning)
            {
                if (config.Settings.Management.PVPDelayPersists && initiator.Is(out BasePlayer attacker2) && HasPVPDelay(attacker2.userID) && HasPVPDelay(victim.userID))
                {
                    return DamageResult.Allowed;
                }
                return DamageResult.None;
            }

            bool hasVictim = Has(victim);

            if (info.WeaponPrefab is MLRSRocket)
            {
                return ((raid.AllowPVP || hasVictim) && raid.Options.MLRS && (initiator?.OwnerID != 13099)) ? DamageResult.Allowed : DamageResult.Blocked;
            }

            if (IsHelicopter(info, out var eventHeli))
            {
                return eventHeli ? DamageResult.None : DamageResult.Allowed;
            }

            if (hasVictim && raid.Owns(initiator))
            {
                info.damageTypes.Clear();
                return DamageResult.None;
            }

            if (IsTrueDamage(initiator, raid.IsProtectedWeapon(initiator)))
            {
                return HandleTrueDamage(raid, info, initiator, victim);
            }

            if (raid.GetInitiatorPlayer(info, damageType, victim, out attacker))
            {
                return HandleAttacker(attacker, victim, info, damageType, raid, out isHuman);
            }

            return hasVictim ? DamageResult.Blocked : DamageResult.None;
        }

        private DamageResult HandleTrueDamage(RaidableBase raid, HitInfo info, BaseEntity weapon, BasePlayer victim)
        {
            if (victim is ScientistNPC && !Has(victim))
            {
                return DamageResult.None;
            }

            if (raid.Options.NPC.BlockOutsideDamageToNpcsInside && Has(victim) && CanBlockOutsideDamage(raid, weapon) && InRange(victim.transform.position, raid.Location, raid.ProtectionRadius))
            {
                return DamageResult.Blocked;
            }

            AutoTurret turret = weapon as AutoTurret;
            if (turret != null)
            {
                float min = raid.Options.AutoTurret.Min, max = raid.Options.AutoTurret.Max;

                if (min != 1 || max != 1)
                {
                    info.damageTypes.Scale(DamageType.Bullet, UnityEngine.Random.Range(min, max));
                }

                bool isRaidTurret = raid.Owns(turret);

                if (Has(victim) && (raid.Options.NPC.IgnorePlayerTrapsTurrets && weapon.OwnerID.IsSteamId() || weapon.OwnerID == 0uL && isRaidTurret))
                {
                    if (turret.target == victim)
                    {
                        turret.SetNoTarget();
                        return DamageResult.None;
                    }
                    return DamageResult.Blocked;
                }

                if (weapon.OwnerID.IsSteamId())
                {
                    if (!victim.IsHuman())
                    {
                        return DamageResult.Allowed;
                    }

                    if (InRange2D(weapon.transform.position, raid.Location, raid.ProtectionRadius))
                    {
                        return raid.AllowPVP ? DamageResult.Allowed : DamageResult.Blocked;
                    }
                }

                return raid.OnTurretTarget(turret, victim, isRaidTurret);
            }

            return DamageResult.Allowed;
        }

        private DamageResult HandleAttacker(BasePlayer attacker, BasePlayer victim, HitInfo info, DamageType damageType, RaidableBase raid, out bool isHuman)
        {
            isHuman = attacker.IsHuman();
            if (!isHuman && Has(attacker) && Has(victim))
            {
                return DamageResult.Blocked;
            }

            if (attacker.userID == victim.userID)
            {
                return raid.Options.AllowSelfDamage ? DamageResult.Allowed : DamageResult.Blocked;
            }

            if (HasPVPDelay(victim.userID))
            {
                if (EventTerritory(attacker.transform.position))
                {
                    raid.SetPVPDelay(attacker, damageType == DamageType.Heat);
                    return DamageResult.Allowed;
                }

                if (config.Settings.Management.PVPDelayAnywhere && HasPVPDelay(attacker.userID))
                {
                    return DamageResult.Allowed;
                }
            }

            if (config.Settings.Management.PVPDelayDamageInside && HasPVPDelay(attacker.userID) && InRange2D(raid.Location, victim.transform.position, raid.ProtectionRadius))
            {
                return DamageResult.Allowed;
            }

            if (isHuman && !victim.IsHuman())
            {
                return HandleNpcVictim(raid, victim, attacker, info);
            }

            if (isHuman && victim.IsHuman())
            {
                return HandlePVPDamage(raid, victim, attacker, info, damageType);
            }

            if (Has(attacker))
            {
                return HandleNpcAttacker(raid, victim, attacker, info, damageType);
            }

            return DamageResult.None;
        }

        private DamageResult HandleNpcVictim(RaidableBase raid, BasePlayer victim, BasePlayer attacker, HitInfo info)
        {
            if (!Has(victim) || !HumanoidBrains.TryGetValue(victim.userID, out var brain))
            {
                return DamageResult.Allowed;
            }

            if (config.Settings.Management.BlockMounts)
            {
                if (raid.IsMounted(attacker, raid.Options.Siege.Only || !config.Settings.Management.BlockSiegeMounts))
                {
                    return DamageResult.Blocked;
                }

                var parent = attacker.HasParent() ? attacker.GetParentEntity() : null;

                if (parent is BaseHelicopter || parent is HotAirBalloon)
                {
                    return DamageResult.Blocked;
                }
            }

            if (raid.Options.NPC.BlockOutsideDamageToNpcsInside && brain.AttackTarget != attacker && CanBlockOutsideDamage(raid, attacker) && InRange(victim.transform.position, raid.Location, raid.ProtectionRadius))
            {
                return DamageResult.Blocked;
            }

            if (!raid.Options.NPC.CanLeave && raid.Options.NPC.BlockOutsideDamageOnLeave && !InRange(attacker.transform.position, raid.Location, raid.ProtectionRadius) && InRange(victim.transform.position, raid.Location, raid.ProtectionRadius))
            {
                brain.Forget();
                if (!victim.IsDead())
                {
                    victim.Heal(victim.MaxHealth());
                }
                return DamageResult.Blocked;
            }

            ApplyMaxEffectiveRangeMultiplier(raid.Options.NPC.PlayerMaxEffectiveRange, raid.SqrProtectionRadius, attacker.transform.position, info, brain);

            if (victim.HasPlayerFlag(BasePlayer.PlayerFlags.Sleeping))
            {
                brain.SetSleeping(false);
            }

            brain.SetTarget(attacker);

            return DamageResult.Allowed;
        }

        private DamageResult HandlePVPDamage(RaidableBase raid, BasePlayer victim, BasePlayer attacker, HitInfo info, DamageType damageType)
        {
            if (playerDelayExclusions.Count > 1 && HasDelayExclusion(victim.userID) && HasDelayExclusion(attacker.userID))
            {
                return DamageResult.Allowed;
            }

            if (raid.Options.BlockOutsideDamageToPlayersInside && CanBlockOutsideDamage(raid, attacker) && !(info.WeaponPrefab is MLRSRocket))
            {
                if (config.EventMessages.NoDamageFromOutsideToPlayersInside && damageType != DamageType.Heat)
                {
                    TryMessage(attacker, "NoDamageFromOutsideToPlayersInside");
                }
                return DamageResult.Blocked;
            }

            if (IsPVE() && (!InRange(attacker.transform.position, raid.Location, raid.ProtectionRadius) || !InRange(victim.transform.position, raid.Location, raid.ProtectionRadius)))
            {
                return DamageResult.Blocked;
            }

            if (raid.IsAlly(attacker.userID, victim.userID))
            {
                return raid.Options.AllowFriendlyFire ? DamageResult.Allowed : DamageResult.Blocked;
            }

            if (raid.AllowPVP)
            {
                raid.SetPVPDelay(attacker, damageType == DamageType.Heat);
                return DamageResult.Allowed;
            }

            return DamageResult.Blocked;
        }

        private DamageResult HandleNpcAttacker(RaidableBase raid, BasePlayer victim, BasePlayer attacker, HitInfo info, DamageType damageType)
        {
            if (!Has(attacker) || !HumanoidBrains.TryGetValue(attacker.userID, out var brain))
            {
                return DamageResult.Allowed;
            }

            if (Has(victim))
            {
                return DamageResult.Blocked;
            }

            if (raid.Options.BlockNpcDamageToPlayersOutside && CanBlockOutsideDamage(raid, victim))
            {
                return victim.userID.IsSteamId() ? DamageResult.Blocked : DamageResult.None;
            }

            if (brain.attackType == HumanoidBrain.AttackType.BaseProjectile && brain.baseProjectile != null && UnityEngine.Random.Range(0f, 100f) > raid.Options.NPC.Accuracy.Get(brain))
            {
                return victim.userID.IsSteamId() ? DamageResult.Blocked : DamageResult.None;
            }

            ApplyMaxEffectiveRangeMultiplier(raid.Options.NPC.NpcMaxEffectiveRange, raid.SqrProtectionRadius, victim.transform.position, info, brain);

            if (damageType == DamageType.Explosion)
            {
                info.UseProtection = false;
            }

            switch (brain.attackType)
            {
                case HumanoidBrain.AttackType.BaseProjectile:
                    info.damageTypes.ScaleAll(raid.Options.NPC.Multipliers.ProjectileDamageMultiplier);
                    break;
                case HumanoidBrain.AttackType.Explosive:
                    info.damageTypes.ScaleAll(raid.Options.NPC.Multipliers.ExplosiveDamageMultiplier);
                    break;
                case HumanoidBrain.AttackType.Melee:
                    info.damageTypes.ScaleAll(raid.Options.NPC.Multipliers.MeleeDamageMultiplier);
                    break;
            }

            return DamageResult.Allowed;
        }

        private DamageResult HandleEntityDamage(BaseCombatEntity entity, HitInfo info, DamageType damageType, out RaidableBase raid, out BasePlayer attacker, out bool isHuman)
        {
            raid = null;
            attacker = null;
            isHuman = false;

            if (info.Initiator is SamSite ss)
            {
                return HasRaidEntity(ss) ? DamageResult.Allowed : DamageResult.None;
            }

            if (!Get(entity.transform.position, out raid) || !ValidateEventTurretDamage(info, raid, entity))
            {
                return DamageResult.None;
            }

            if (IsHelicopter(info, out bool eventHeli))
            {
                HandleHelicopterDamage(entity, info);
                return eventHeli ? DamageResult.None : DamageResult.Allowed;
            }

            bool isAttacker = raid.GetInitiatorPlayer(info, damageType, entity, out attacker);
            isHuman = isAttacker && attacker.IsHuman();

            if (raid.IsDespawning)
            {
                return !isAttacker ? DamageResult.Allowed : DamageResult.None;
            }

            if (HandleOwnerlessEntities(entity, info, raid, isHuman) == DamageResult.None)
            {
                return DamageResult.None;
            }

            ApplyPlayerDamageMultipliers(info, raid, damageType, isAttacker, isHuman, attacker);

            HandleSpecificEntities(entity, info, raid);

            if (ShouldBlockDamage(entity, info, damageType, raid))
            {
                return DamageResult.Blocked;
            }

            if (ShouldBlockDueToLoadingOrDecay(entity, damageType, raid))
            {
                return DamageResult.Blocked;
            }

            if (entity.IsNpc || entity is PlayerCorpse)
            {
                return DamageResult.Allowed;
            }

            if (entity is BuildingBlock block)
            {
                DamageResult handleBuildingResult = HandleBuildingBlock(block, raid);
                if (handleBuildingResult != DamageResult.None)
                {
                    return handleBuildingResult;
                }
            }
            else if (raid.IsMountable(entity))
            {
                DamageResult handleMountableResult = HandleMountable(entity, info, raid, isHuman, attacker);
                if (handleMountableResult != DamageResult.None)
                {
                    return handleMountableResult;
                }
            }

            if (!entity.IsValid())
            {
                return DamageResult.None;
            }

            bool checkList = raid.BuiltList.Contains(entity);

            if (!checkList && !raid.Has(entity))
            {
                return DamageResult.None;
            }

            if (info.WeaponPrefab is TimedExplosive && info.WeaponPrefab.ShortPrefabName == "torpedostraight")
            {
                ScaleTorpedoDamage(info, raid);
            }

            if (!attacker.IsNetworked())
            {
                return ValidateUnknownAttacker(info, raid, entity) ? DamageResult.Allowed : DamageResult.None;
            }

            if (!isHuman)
            {
                return HandleNonHumanAttacker(entity, raid, attacker, info, damageType);
            }

            if (info.IsProjectile())
            {
                raid.cached_attacker = attacker;
                raid.cached_attack_time = Time.realtimeSinceStartupAsDouble;
                raid.cached_attacker_id = attacker.userID;
            }

            UpdateAttackerInfo(entity, attacker);

            if (HandleEcoAndMountDamage(raid, attacker, info, damageType) == DamageResult.Blocked)
            {
                return DamageResult.Blocked;
            }

            if (raid.Options.BlockOutsideDamageToBaseInside && CanBlockOutsideDamage(raid, attacker) && !(info.WeaponPrefab is MLRSRocket))
            {
                TryMessage(attacker, "NoDamageFromOutsideToBaseInside");
                return DamageResult.Blocked;
            }

            if (HandleRaidAndTurretConditions(entity, raid, attacker, info, damageType) == DamageResult.Blocked)
            {
                return DamageResult.Blocked;
            }

            if (!checkList && FinalizeRaidChecks(entity, info, raid, attacker, damageType) == DamageResult.Blocked)
            {
                return DamageResult.Blocked;
            }

            return DamageResult.Allowed;
        }

        private bool ValidateEventTurretDamage(HitInfo info, RaidableBase raid, BaseCombatEntity entity)
        {
            AutoTurret turret = info.Initiator as AutoTurret;

            if (entity.OwnerID != 0uL || entity.enableSaving || turret.IsKilled() || !raid.Owns(turret))
            {
                return true;
            }

            BuildingBlock block = entity as BuildingBlock;
            if (block != null && block.grade == BuildingGrade.Enum.Twigs)
            {
                BasePlayer target = turret.target as BasePlayer;
                if (target != null && raid.intruders.Contains(target.userID))
                {
                    turret.target.Hurt(info);
                }
                if (raid.Options.TurretsHurtTwig)
                {
                    return true;
                }
            }

            info.damageTypes.Clear();
            return false;
        }

        private void HandleHelicopterDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (config.Settings.Management.BlockHelicopterDamage && entity.OwnerID == 0uL)
            {
                info.damageTypes.Clear();
            }
        }

        private DamageResult HandleOwnerlessEntities(BaseCombatEntity entity, HitInfo info, RaidableBase raid, bool isHuman)
        {
            if (isHuman && entity.OwnerID == 0uL && raid.Type != RaidableType.None)
            {
                raid.IsEngaged = true;
                raid.CheckDespawn();
            }
            if (info.Initiator.Is(out BaseEntity initiator) && raid.Owns(initiator) && raid.Owns(entity))
            {
                info.damageTypes.Clear();
                return DamageResult.None;
            }
            return DamageResult.Allowed;
        }

        private void ApplyMaxEffectiveRangeMultiplier(float maxEffectiveRange, float sqrProtectionRadius, Vector3 a, HitInfo info, HumanoidBrain brain)
        {
            if (maxEffectiveRange > 0f)
            {
                float distanceSq = (a - brain.ServerPosition).sqrMagnitude;

                if (distanceSq > sqrProtectionRadius)
                {
                    bool flag = distanceSq > maxEffectiveRange * maxEffectiveRange;

                    info.damageTypes.ScaleAll(flag ? 0f : 1f - (Mathf.Sqrt(distanceSq) / maxEffectiveRange));
                }
            }
        }

        private void ApplyPlayerDamageMultipliers(HitInfo info, RaidableBase raid, DamageType damageType, bool isAttacker, bool isHuman, BasePlayer attacker)
        {
            if (isAttacker ? isHuman : damageType == DamageType.Heat)
            {
                if (raid.PlayerDamageMultiplier.Count > 0)
                {
                    foreach (var m in raid.PlayerDamageMultiplier)
                    {
                        info.damageTypes.Scale(m.index, m.amount);
                    }
                }
                if (raid.Options.PlayerDamageMultiplierTC != 1f && info.HitEntity is BuildingPrivlidge)
                {
                    info.damageTypes.ScaleAll(raid.Options.PlayerDamageMultiplierTC);
                }
            }
            if (!raid.Options.Siege.Disabled)
            {
                raid.Options.Siege.Scale(attacker, info, isHuman);
            }
        }

        private void HandleSpecificEntities(BaseCombatEntity entity, HitInfo info, RaidableBase raid)
        {
            if (entity is BearTrap trap && trap != null)
            {
                if (raid.Options.BearTrapsImmuneToExplosives && info.WeaponPrefab is TimedExplosive)
                {
                    info.damageTypes.Clear();
                }
                if (raid.Options.RearmBearTraps)
                {
                    trap.Invoke(trap.Arm, 0.1f);
                }
            }
        }

        private bool ShouldBlockDamage(BaseCombatEntity entity, HitInfo info, DamageType damageType, RaidableBase raid)
        {
            return raid.IsDamageBlocked(entity) || (!raid.Options.MLRS && info.WeaponPrefab is MLRSRocket);
        }

        private bool ShouldBlockDueToLoadingOrDecay(BaseCombatEntity entity, DamageType damageType, RaidableBase raid)
        {
            if (damageType == DamageType.Decay)
            {
                return raid.Owns(entity);
            }
            return raid.IsLoading || entity is DroppedItemContainer;
        }

        private DamageResult HandleBuildingBlock(BuildingBlock block, RaidableBase raid)
        {
            if (raid.Options.Setup.FoundationsImmune || raid.Options.Setup.FoundationsImmuneForcedHeight && raid.Options.Setup.ForcedHeight != -1)
            {
                if (raid.foundations.Count > 0 && block.ShortPrefabName.StartsWith("foundation"))
                {
                    return DamageResult.Blocked;
                }

                if (raid.floors == null && block.ShortPrefabName.StartsWith("floor") && block.transform.position.y - raid.Location.y <= 3f)
                {
                    return DamageResult.Blocked;
                }
            }

            if (block.OwnerID == 0)
            {
                if (raid.Options.TwigImmune && block.grade == BuildingGrade.Enum.Twigs)
                {
                    return DamageResult.Blocked;
                }
                if (raid.Options.BlocksImmune)
                {
                    return block.grade == BuildingGrade.Enum.Twigs ? DamageResult.Allowed : DamageResult.Blocked;
                }
            }

            if (block.grade == BuildingGrade.Enum.Twigs)
            {
                return DamageResult.Allowed;
            }

            return DamageResult.None;
        }

        private DamageResult HandleMountable(BaseEntity entity, HitInfo info, RaidableBase raid, bool isHuman, BasePlayer attacker)
        {
            if (config.Settings.Management.MiniCollision && entity is Minicopter && entity == info.Initiator)
            {
                return DamageResult.Blocked;
            }

            if (isHuman && !ExcludedMountsExists(entity.ShortPrefabName))
            {
                BaseMountable mountable = entity as BaseMountable;
                if (mountable != null)
                {
                    BaseVehicle vehicle = mountable.HasParent() ? mountable.VehicleParent() : mountable as BaseVehicle;

                    if (vehicle != null && vehicle.GetDriver() == attacker)
                    {
                        return config.Settings.Management.MountDamageFromPlayers ? DamageResult.Allowed : DamageResult.Blocked;
                    }
                }
                if (!config.Settings.Management.MountDamageFromPlayers)
                {
                    TryMessage(attacker, "NoMountedDamageTo");
                    return DamageResult.Blocked;
                }
                if (config.Settings.Management.BlockMounts && raid.IsMounted(attacker, raid.Options.Siege.Only || !config.Settings.Management.BlockSiegeMounts))
                {
                    TryMessage(attacker, "NoMountedDamageFrom");
                    return DamageResult.Blocked;
                }
                if (raid.Options.BlockOutsideDamageToBaseInside && CanBlockOutsideDamage(raid, attacker) && !(info.WeaponPrefab is MLRSRocket))
                {
                    TryMessage(attacker, "NoDamageFromOutsideToBaseInside");
                    return DamageResult.Blocked;
                }
            }

            if (info.Initiator == entity)
            {
                return config.Settings.Management.MountDamageFromPlayers || (entity is BatteringRam or BatteringRamHead) ? DamageResult.Allowed : DamageResult.Blocked;
            }

            return DamageResult.None;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ExcludedMountsExists(string prefabName)
        {
            foreach (var prefix in ExcludedMounts)
            {
                if (prefabName.StartsWith(prefix))
                {
                    return true;
                }
            }
            return false;
        }

        private void ScaleTorpedoDamage(HitInfo info, RaidableBase raid)
        {
            info.damageTypes.ScaleAll(UnityEngine.Random.Range(raid.Options.Water.TorpedoMin, raid.Options.Water.TorpedoMax));
        }

        private bool ValidateUnknownAttacker(HitInfo info, RaidableBase raid, BaseCombatEntity entity)
        {
            BaseEntity initiator = info?.Initiator;
            return initiator.IsNull() || HasRaidEntity(initiator) || IsLootingWeapon(info);
        }

        private DamageResult HandleNonHumanAttacker(BaseCombatEntity entity, RaidableBase raid, BasePlayer attacker, HitInfo info, DamageType damageType)
        {
            if (entity.OwnerID == 0uL && !raid.Options.RaidingNpcs && !Has(attacker))
            {
                info.damageTypes.Clear();
                return DamageResult.None;
            }

            if (info.damageTypes.Has(DamageType.Explosion) || info.WeaponPrefab is TimedExplosive)
            {
                if (entity.OwnerID == 0uL && !(entity is BasePlayer) && Has(attacker))
                {
                    return DamageResult.Blocked;
                }

                //return raid.Has(entity) ? DamageResult.Allowed : DamageResult.Blocked;
                //return (entity.OwnerID == 0uL || raid.BuiltList.Contains(entity)) ? DamageResult.Allowed : DamageResult.Blocked;
            }

            return DamageResult.Allowed;
        }

        private void UpdateAttackerInfo(BaseCombatEntity entity, BasePlayer attacker)
        {
            entity.lastAttacker = attacker;
            attacker.lastDealtDamageTime = Time.time;
        }

        private DamageResult HandleEcoAndMountDamage(RaidableBase raid, BasePlayer attacker, HitInfo info, DamageType damageType)
        {
            if (raid.Options.Siege.Only && !raid.Options.Siege.IsSiegeTool(attacker, info, damageType))
            {
                TryMessage(attacker, "PrimitiveOnly");
                return DamageResult.Blocked;
            }

            if (config.Settings.Management.BlockMounts && raid.IsMounted(attacker, raid.Options.Siege.Only || !config.Settings.Management.BlockSiegeMounts))
            {
                TryMessage(attacker, "NoMountedDamageFrom");
                return DamageResult.Blocked;
            }

            return DamageResult.Allowed;
        }

        public bool CanBlockOutsideDamage(RaidableBase raid, BaseEntity attacker)
        {
            return !InRange(attacker.transform.position, raid.Location, Mathf.Max(raid.ProtectionRadius, raid.Options.ArenaWalls.Radius));
        }

        private DamageResult HandleRaidAndTurretConditions(BaseCombatEntity entity, RaidableBase raid, BasePlayer attacker, HitInfo info, DamageType damageType)
        {
            if (raid.ID.IsSteamId() && IsBox(entity, false) && (attacker.UserIDString == raid.ID || raid.IsAlly(attacker.userID, Convert.ToUInt64(raid.ID))))
            {
                return DamageResult.Blocked;
            }

            if (raid.ownerId.IsSteamId() && raid.CanEjectEnemy() && !raid.IsAlly(attacker))
            {
                TryMessage(attacker, "NoDamageToEnemyBase");
                return DamageResult.Blocked;
            }

            if (raid.Options.AutoTurret.AutoAdjust && entity is AutoTurret turret && turret.sightRange < raid.Options.AutoTurret.SightRange * 2 && raid.Owns(turret))
            {
                raid.SetupSightRange(turret, raid.Options.AutoTurret.SightRange, 2);
            }

            if (damageType == DamageType.Explosion && !raid.Options.ExplosionModifier.Equals(100f))
            {
                info.damageTypes.Scale(damageType, raid.Options.ExplosionModifier / 100f);
            }

            return DamageResult.None;
        }

        private DamageResult FinalizeRaidChecks(BaseCombatEntity entity, HitInfo info, RaidableBase raid, BasePlayer attacker, DamageType damageType)
        {
            if (raid.IsOpened && IsLootingWeapon(info) && raid.AddLooter(attacker, info))
            {
                if (damageType == DamageType.Explosion && info.WeaponPrefab is TimedExplosive)
                {
                    raid.GetRaider(attacker).HasDestroyed = true;
                }
                raid.TrySetOwner(attacker, entity, info);
            }

            if (!raid.CanHurtBox(entity))
            {
                if (damageType != DamageType.Heat)
                {
                    TryMessage(attacker, "NoDamageToBoxes");
                }
                return DamageResult.Blocked;
            }

            if (raid.Options.MLRS && info.WeaponPrefab is MLRSRocket)
            {
                raid.GetRaider(attacker).lastActiveTime = Time.realtimeSinceStartupAsDouble;
            }

            return DamageResult.None;
        }

        private readonly Dictionary<ulong, List<PlayerExclusion>> playerDelayExclusions = new();

        private class PlayerExclusion : Pool.IPooled
        {
            public Plugin plugin;
            public double time;
            public bool IsExpired => Time.realtimeSinceStartupAsDouble > time;
            public void EnterPool()
            {
                plugin = null;
                time = 0f;
            }
            public void LeavePool()
            {
                plugin = null;
                time = 0f;
            }
        }

        private void ExcludePlayer(ulong userid, float maxDelayLength, Plugin plugin)
        {
            if (plugin == null)
            {
                return;
            }
            if (!playerDelayExclusions.TryGetValue(userid, out var exclusions))
            {
                playerDelayExclusions[userid] = exclusions = Pool.Get<List<PlayerExclusion>>();
            }
            var exclusion = exclusions.Find(x => x.plugin == plugin);
            if (maxDelayLength <= 0f)
            {
                if (exclusion != null)
                {
                    exclusions.Remove(exclusion);
                    exclusion.plugin = null;
                    exclusion.time = 0f;
                    Pool.Free(ref exclusion);
                }
                if (exclusions.Count == 0)
                {
                    playerDelayExclusions.Remove(userid);
                    Pool.FreeUnmanaged(ref exclusions);
                }
            }
            else
            {
                if (exclusion == null)
                {
                    exclusion = Pool.Get<PlayerExclusion>();
                    exclusions.Add(exclusion);
                }
                exclusion.plugin = plugin;
                exclusion.time = Time.realtimeSinceStartupAsDouble + maxDelayLength;
            }
        }

        private bool HasDelayExclusion(ulong userid)
        {
            if (playerDelayExclusions.TryGetValue(userid, out var exclusions))
            {
                for (int i = 0; i < exclusions.Count; i++)
                {
                    var exclusion = exclusions[i];
                    if (!exclusion.IsExpired)
                    {
                        return true;
                    }
                    exclusions.RemoveAt(i);
                    exclusion.plugin = null;
                    exclusion.time = 0f;
                    Pool.Free(ref exclusion);
                    i--;
                }
                if (exclusions.Count == 0)
                {
                    playerDelayExclusions.Remove(userid);
                    Pool.Free(ref exclusions);
                }
            }
            return false;
        }

        #endregion Hooks

        #region Spawn

        private static void Shuffle<T>(IList<T> list) // Fisher-Yates shuffle
        {
            int count = list.Count;
            int n = count;
            while (n-- > 0)
            {
                int k = UnityEngine.Random.Range(0, count);
                int j = UnityEngine.Random.Range(0, count);
                T value = list[k];
                list[k] = list[j];
                list[j] = value;
            }
        }

        public RaidableBase OpenEvent(RandomBase rb)
        {
            var go = new GameObject();
            var raid = go.AddComponent<RaidableBase>();

            raid.go = go;
            raid.HumanoidBrains = HumanoidBrains;
            raid.Instance = this;
            raid.Options = rb.options;
            raid.ProtectionRadius = rb.options.ProtectionRadius(rb.type);
            raid.SqrProtectionRadius = raid.ProtectionRadiusSqr(0);
            raid.markerName = raid.MarkerName;
            raid.spawnDateTime = DateTime.Now;
            raid.stability = rb.stability;
            raid.name = Name;
            raid.SetAllowPVP(rb);
            raid.Location = rb.Position;
            raid.LocationXZ3D = rb.Position.XZ3D();
            raid.BaseName = rb.BaseName;
            raid.BaseHeight = rb.baseHeight;
            raid.ProfileName = rb.Profile.ProfileName;
            raid.IsLoading = true;
            raid.loadTime = Time.realtimeSinceStartup;
            raid.InitiateTurretOnSpawn = rb.options.AutoTurret.InitiateOnSpawn;

            foreach (var multiplier in raid.Options.PlayerDamageMultiplier)
            {
                float amount = multiplier.amount;
                if (amount == 1f) continue;
                DamageType index = multiplier.index;
                if (index == DamageType.Generic) continue;
                raid.PlayerDamageMultiplier.Add(new() { index = index, amount = amount });
            }

            if (!raid.Options.MLRS)
            {
                Subscribe(nameof(OnMlrsFire));
            }

            if (config.Settings.NoWizardry && Wizardry.CanCall())
            {
                Subscribe(nameof(OnActiveItemChanged));
            }
            else if (config.Settings.NoArchery && Archery.CanCall())
            {
                Subscribe(nameof(OnActiveItemChanged));
            }
            else if (raid.Options.Siege.Only)
            {
                Subscribe(nameof(OnActiveItemChanged));
            }

            if (raid.BlacklistedCommands.Count > 0)
            {
                Subscribe(nameof(OnPlayerCommand));
                Subscribe(nameof(OnServerCommand));
            }

            if (IsPVE())
            {
                Subscribe(nameof(CanEntityTrapTrigger));
                Subscribe(nameof(CanEntityBeTargeted));
            }
            else
            {
                Subscribe(nameof(OnTrapTrigger));
            }

            SubscribeDamageHook();
            Subscribe(nameof(OnSamSiteTargetScan));
            Subscribe(nameof(OnNearbyTurretsScan));
            Subscribe(nameof(OnInterferenceUpdate));
            Subscribe(nameof(OnStructureUpgrade));
            Subscribe(nameof(OnEntityEnter));
            Subscribe(nameof(CanBuild));
            Subscribe(nameof(OnEntitySpawned));
            Subscribe(nameof(OnPlayerRespawn));

            data.TotalEvents++;
            raid._undoLimit = Mathf.Clamp(raid.Options.Setup.DespawnLimit, 1, 500);

            Raids.Add(raid);

            raid.CheckPaste();
            raid.SendDronePatrol(rb);
            raid.SetupCollider();

            return raid;
        }

        #endregion

        #region Paste

        private double isSpawnerBusyTime;
        private bool isSpawnerBusy;

        private bool IsLoaderBusy => Raids.Exists(raid => raid.IsDespawning || raid.IsLoading);

        private bool IsSpawnerBusy
        {
            get
            {
                if (Time.realtimeSinceStartupAsDouble > isSpawnerBusyTime)
                {
                    isSpawnerBusy = false;
                }

                return IsUnloading || isSpawnerBusy;
            }
            set
            {
                isSpawnerBusyTime = Time.realtimeSinceStartupAsDouble + 180d;
                isSpawnerBusy = value;
            }
        }

        private bool IsGridLoading() => GridController.gridCoroutine != null;

        private bool IsPasteAvailable() => !Raids.Exists(raid => raid.IsLoading);

        private bool IsBusy() => IsSpawnerBusy || IsLoaderBusy || IsGridLoading();

        private bool PasteBuilding(RandomBase rb)
        {
            Queues.Messages.Print($"{rb.BaseName} trying to paste at {rb.Position}");

            if (!IsCopyPasteLoaded(out var error))
            {
                Puts(error);

                return false;
            }

            loadCoroutines.Add(ServerMgr.Instance.StartCoroutine(LoadCopyPasteFile(rb)));

            return true;
        }

        private void StopLoadCoroutines()
        {
            if (setupCopyPasteObstructionRadius != null)
            {
                ServerMgr.Instance.StopCoroutine(setupCopyPasteObstructionRadius);
                setupCopyPasteObstructionRadius = null;
            }
            foreach (var co in loadCoroutines)
            {
                if (co != null)
                {
                    ServerMgr.Instance.StopCoroutine(co);
                }
            }
            foreach (var raid in Raids)
            {
                raid.StopSetupCoroutine();
            }
            Queues?.StopCoroutine();
            Automated?.DestroyMe();
            GridController.StopCoroutine();
        }

        private bool IsPrefabFoundation(Dictionary<string, object> entity)
        {
            var prefabname = entity["prefabname"].ToString();

            return prefabname.Contains("/foundation.") || prefabname.EndsWith("diesel_collectable.prefab") && entity.TryGetValue("skinid", out var skinid) && skinid != null && skinid.ToString() == "1337424001";
        }

        private bool IsPrefabExternalWall(Dictionary<string, object> entity)
        {
            return entity["prefabname"].ToString().Contains("/wall.external.high.");
        }

        private bool IsPrefabFloor(Dictionary<string, object> entity)
        {
            return entity.TryGetValue("prefabname", out var obj) && obj != null && obj.ToString().Contains("/floor");
        }

        private IEnumerator SetupCopyPasteObstructionRadius()
        {
            foreach (var profile in Buildings.Profiles.ToPooledList())
            {
                var radius = profile.Value.Options.ProtectionRadii.Obstruction == -1 ? 0f : GetObstructionRadius(profile.Value.Options.ProtectionRadii, RaidableType.None);
                foreach (var extra in profile.Value.Options.AdditionalBases)
                {
                    if (!Buildings.Removed.Contains(extra.Key))
                    {
                        yield return SetupCopyPasteObstructionRadius(extra.Key, radius);
                    }
                }
                if (!Buildings.Removed.Contains(profile.Key))
                {
                    yield return SetupCopyPasteObstructionRadius(profile.Key, radius);
                }
            }

            setupCopyPasteObstructionRadius = null;
        }

        private IEnumerator SetupCopyPasteObstructionRadius(string baseName, float radius)
        {
            var filename = Path.Combine("copypaste", baseName);

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(filename))
            {
                yield break;
            }

            DynamicConfigFile data;

            try
            {
                data = Interface.Oxide.DataFileSystem.GetDatafile(filename);
            }
            catch (Exception ex)
            {
                Queues.Messages.Log(baseName, $"{baseName} could not be read from the disk #1: {ex}");
                Buildings.Remove(baseName);
                yield break;
            }

            if (data["entities"] == null)
            {
                Queues.Messages.Log(baseName, $"{baseName} is missing entity data");
                Buildings.Remove(baseName);
                yield break;
            }

            var entities = data["entities"] as List<object>;
            using var foundations = DisposableList<Vector3>();
            using var floors = DisposableList<Vector3>();
            //using var invalid = DisposableList<string>();
            int checks = 0;
            float x = 0f;
            float z = 0f;

            foreach (var obj in entities)
            {
                if (!(obj is Dictionary<string, object> entity))
                {
                    continue;
                }
                if (++checks >= 1000)
                {
                    checks = 0;
                    yield return Automated.instruction0;
                }
                if (!entity.ContainsKey("prefabname") || !entity.ContainsKey("pos"))
                {
                    continue;
                }
                var prefab = entity["prefabname"].ToString();
                try
                {
                    if (prefab.Contains("testridablehorse"))
                    {
                        Puts($"{baseName} contains a broken prefab that must be removed: {prefab}");
                        Queues.Messages.Log(baseName, $"Invalid entity! {prefab}");
                        Buildings.Remove(baseName);
                        //invalid.Add(prefab);
                        yield break;
                    }
                    var axes = entity["pos"] as Dictionary<string, object>;
                    var position = new Vector3(Convert.ToSingle(axes?["x"]), Convert.ToSingle(axes?["y"]), Convert.ToSingle(axes?["z"]));
                    if (IsPrefabFoundation(entity) || IsPrefabExternalWall(entity))
                    {
                        foundations.Add(position);
                        x += position.x;
                        z += position.z;
                    }
                    if (IsPrefabFloor(entity))
                    {
                        floors.Add(position);
                    }
                }
                catch (Exception ex)
                {
                    Puts(ex);
                    Puts("Invalid entity found in copypaste file: {0} ({1})", baseName, prefab);
                }
            }

            if (foundations.Count == 0)
            {
                foreach (var position in floors)
                {
                    foundations.Add(position);
                    x += position.x;
                    z += position.z;
                }
            }

            if (foundations.Count == 0)
            {
                Queues.Messages.Log(baseName, $"{baseName} is missing foundation/floor data #1");
                Buildings.Remove(baseName);
                yield break;
            }

            var center = new Vector3(x / foundations.Count, 0f, z / foundations.Count);

            center.y = GetSpawnHeight(center);

            if (radius == 0f)
            {
                foundations.Sort((a, b) => (a - center).sqrMagnitude.CompareTo((b - center).sqrMagnitude));

                radius = Vector3.Distance(foundations[0], foundations[^1]);
            }

            var pasteData = GetPasteData(baseName);

            pasteData.radius = Mathf.Ceil(Mathf.Max(CELL_SIZE, radius));
            pasteData.foundations = new(foundations);
            pasteData.valid = true;
            //if (invalid.Count > 0)
            //{
            //    pasteData.invalid = new(invalid);
            //}
        }

        private readonly Dictionary<string, object> _emptyProtocol = new();

        private IEnumerator LoadCopyPasteFile(RandomBase rb)
        {
            DynamicConfigFile data;

            try
            {
                data = Interface.Oxide.DataFileSystem.GetDatafile(Path.Combine("copypaste", rb.BaseName));
            }
            catch (Exception ex)
            {
                Queues.Messages.Log(rb.BaseName, $"{rb.BaseName} could not be read from the disk #2: {ex}");
                Buildings.Remove(rb.BaseName);
                IsSpawnerBusy = false;
                yield break;
            }

            yield return ApplyStartPositionAdjustment(rb, data);

            if (rb.pasteData.foundations.IsNullOrEmpty())
            {
                Queues.Messages.Log(rb.BaseName, $"{rb.BaseName} is missing foundation/floor data #2");
                Buildings.Remove(rb.BaseName);
                IsSpawnerBusy = false;
                yield break;
            }

            var entities = data["entities"] as List<object>;

            if (entities == null)
            {
                Queues.Messages.Log(rb.BaseName, $"{rb.BaseName} is missing entity data");
                Buildings.Remove(rb.BaseName);
                IsSpawnerBusy = false;
                yield break;
            }

            //if (!rb.pasteData.invalid.IsNullOrEmpty())
            //{
            //    foreach (var invalid in rb.pasteData.invalid)
            //    {
            //        foreach (var ent in entities)
            //        {
            //            if (ent is Dictionary<string, object> dict && dict.TryGetValue("prefabname", out object value) && value.ToString() == invalid)
            //            {
            //                entities.Remove(dict);
            //                break;
            //            }
            //        }
            //    }
            //}

            var preloadData = CopyPaste.Call("PreLoadData", entities, rb.Position, 0f, true, rb.inventories, false, true) as HashSet<Dictionary<string, object>>;

            yield return TryApplyAutoHeight(rb, preloadData);

            if (!IsUnloading)
            {
                TryInvokeMethod(() => RFManager.GetListenerSet(1).RemoveWhere(obj => obj == null || !BaseEntityEx.IsValidEntityReference(obj)));

                var raid = OpenEvent(rb);
                var protocol = data["protocol"] as Dictionary<string, object> ?? _emptyProtocol;
                object result = null;
                try
                {
                    result = CopyPaste.Call("Paste", new object[] { preloadData, protocol, false, rb.Position, _consolePlayer, rb.stability, 0f, rb.heightAdj, false, CreatePastedCallback(raid, rb), CreateSpawnCallback(raid), rb.BaseName, true, rb.Save });
                }
                catch (Exception ex)
                {
                    Puts(ex);
                }
                if (result == null)
                {
                    Queues.Messages.Print($"CopyPaste {CopyPaste.Version} did not respond for {rb.BaseName}!");
                    Puts($"\nCopyPaste {CopyPaste.Version} did not respond for {rb.BaseName}! Did CopyPaste plugin throw an error above?");
                    Puts("\nQueue will resume in 180 seconds to prevent the server from being spammed with errors.");
                    raid.Despawn();
                }
                else
                {
                    Queues.Messages.Print($"{rb.BaseName} is pasting at {rb.Position}");
                }
            }
        }

        private Action CreatePastedCallback(RaidableBase raid, RandomBase rb)
        {
            return new(() =>
            {
                if (raid == null)
                {
                    return;
                }

                raid.IsPasted = true;

                if (IsUnloading || raid.IsDespawning)
                {
                    raid.rb = rb;
                    raid.Despawn();
                }
                else
                {
                    raid.Init(rb);
                }
            });
        }

        private Action<BaseEntity> CreateSpawnCallback(RaidableBase raid)
        {
            return new(e =>
            {
                if (e == null || e.IsDestroyed)
                {
                    return;
                }
                if (IsUnloading || raid == null || raid.IsDespawning)
                {
                    if (!IsShuttingDown) e.DelayedSafeKill();
                    return;
                }
                if (e is BaseCombatEntity b)
                {
                    b.spawnDeployableCorpseOnDeath = false;
                }
                if (e.ShortPrefabName == "poweredwaterpurifier.storage" && !e.HasParent())
                {
                    e.DelayedSafeKill();
                    return;
                }
                Vector3 position = e.transform.position;
                if (e is AutoTurret turret)
                {
                    raid.PreSetupTurret(turret);
                }
                else if (raid.IsWeapon(e))
                {
                    e.skinID = RB_SKIN_ID;
                }
                else if (e is BaseMountable && !e.HasParent())
                {
                    e.skinID = RB_SKIN_ID;
                }
                else if (raid.IsFoundation(e))
                {
                    raid.foundations.Add(position);
                }
                else if (e.ShortPrefabName.Contains("floor"))
                {
                    raid.floors.Add(position);
                }
                if (!raid.stability && e is BuildingBlock block)
                {
                    block.grounded = true;
                }
                else if (raid.Options.EmptyAll && e is StorageContainer container)
                {
                    raid.TryEmptyContainer(container);
                }
                else if (raid.Options.EmptyAll && e is IOEntity io && io is IIndustrialStorage st)
                {
                    raid.TryEmptyIndustrialStorage(io, st);
                }
                foreach (var slot in _checkSlots)
                {
                    if (e.GetSlot(slot) is BaseEntity ent)
                    {
                        raid.AddEntity(ent);
                    }
                }
                if (e.net == null)
                {
                    e.net = Net.sv.CreateNetworkable();
                }
                if (Abs(position.y - raid.Location.y) < raid.ProtectionRadius && raid.IsCompound(e))
                {
                    raid.compound.Add(position);
                }
                if (e.children != null)
                {
                    foreach (var child in e.children)
                    {
                        if (child != null && (child.enableSaving || child is HeldEntity))
                            continue;
                        BaseEntity.saveList.Remove(child);
                    }
                }
                if (!raid.Options.Elevators.BMGOnly && e is Elevator elevator)
                {
                    raid.SetupElevator(elevator);
                }
                e.OwnerID = 0;
                raid.AddEntity(e);
            });
        }

        private IEnumerator ApplyStartPositionAdjustment(RandomBase rb, DynamicConfigFile data)
        {
            ParseListedOptions(rb);

            using var foundations = DisposableList<Vector3>();
            float x = 0f, z = 0f;

            if (!rb.pasteData.valid)
            {
                yield return SetupCopyPasteObstructionRadius(rb.BaseName, rb.options.ProtectionRadii.Obstruction == -1 ? 0f : GetObstructionRadius(rb.options.ProtectionRadii, RaidableType.None));
            }

            if (rb.pasteData.foundations.IsNullOrEmpty())
            {
                Queues.Messages.Log(rb.BaseName, $"{rb.BaseName} is missing foundation/floor data #3");
                yield break;
            }

            foreach (var foundation in rb.pasteData.foundations)
            {
                var a = foundation + rb.Position;
                a.y = GetSpawnHeight(a);
                foundations.Add(a);
                x += a.x;
                z += a.z;
            }

            var center = new Vector3(x / foundations.Count, 0f, z / foundations.Count);

            center.y = rb.isCustomSpawn ? rb.Position.y : GetSpawnHeight(center, true);

            rb.Position += (rb.Position - center);

            if (rb.options.Setup.ForcedHeight == -1)
            {
                rb.Position.y = GetSpawnHeight(rb.Position, true);

                TryApplyCustomAutoHeight(rb);
                TryApplyMultiFoundationSupport(rb);

                rb.Position.y += rb.baseHeight + rb.options.Setup.PasteHeightAdjustment;
            }
            else if (!rb.isCustomSpawn) rb.Position.y = rb.baseHeight + rb.options.Setup.PasteHeightAdjustment + rb.options.Setup.ForcedHeight;

            yield return CoroutineEx.waitForFixedUpdate;
        }

        private IEnumerator TryApplyAutoHeight(RandomBase rb, HashSet<Dictionary<string, object>> preloadData)
        {
            if (rb.autoHeight && !config.Settings.Experimental.Contains(ExperimentalSettings.Type.AutoHeight, rb))
            {
                var bestHeight = Convert.ToSingle(CopyPaste.Call("FindBestHeight", preloadData, rb.Position));
                int checks = 0;

                rb.heightAdj = bestHeight - rb.Position.y;

                foreach (var entity in preloadData)
                {
                    if (++checks >= 1000)
                    {
                        checks = 0;
                        yield return Automated.instruction0;
                    }

                    if (entity.TryGetValue("position", out var obj) && obj is Vector3 pos)
                    {
                        pos.y += rb.heightAdj;

                        entity["position"] = pos;
                    }
                }
            }
        }

        private void TryApplyCustomAutoHeight(RandomBase rb)
        {
            if (config.Settings.Experimental.Contains(ExperimentalSettings.Type.AutoHeight, rb))
            {
                foreach (var foundation in rb.pasteData.foundations)
                {
                    var a = foundation + rb.Position;

                    if (a.y < rb.Position.y)
                    {
                        rb.Position.y += rb.Position.y - a.y;
                        return;
                    }
                    else
                    {
                        rb.Position.y -= a.y - rb.Position.y;
                        return;
                    }
                }
            }
        }

        private void TryApplyMultiFoundationSupport(RandomBase rb)
        {
            float j = 0f, k = 0f, y = 0f;
            for (int i = 0; i < rb.pasteData.foundations.Count; i++)
            {
                y = (float)Math.Round(rb.pasteData.foundations[i].y, 1);
                j = Mathf.Max(y, j);
                k = Mathf.Min(y, k);
            }
            if (j != 0f && config.Settings.Experimental.Contains(ExperimentalSettings.Type.MultiFoundation, rb))
            {
                rb.Position.y += j + 1f;
            }
            else if (k != 0f && config.Settings.Experimental.Contains(ExperimentalSettings.Type.Bunker, rb))
            {
                y = rb.Position.y + Abs(k);
                if (y < rb.Position.y)
                {
                    rb.Position.y = y + 1.4f;
                }
            }
        }

        [HookMethod("GetSpawnHeight")]
        public float GetSpawnHeight(Vector3 a, bool flag = true, bool shouldSkipSmallRock = false) => SpawnsController.GetSpawnHeight(a, flag, shouldSkipSmallRock);

        private void ParseListedOptions(RandomBase rb)
        {
            rb.autoHeight = false;

            List<PasteOption> options = rb.options.PasteOptions;

            foreach (var (key, abo) in rb.options.AdditionalBases)
            {
                if (key.Equals(rb.BaseName, StringComparison.OrdinalIgnoreCase))
                {
                    options = abo;
                    break;
                }
            }

            foreach (var option in options)
            {
                switch (option.Key.ToLower())
                {
                    case "inventories": rb.inventories = option.Value.ToLower() == "true"; break;
                    case "stability": rb.stability = option.Value.ToLower() == "true"; break;
                    case "autoheight": rb.autoHeight = option.Value.ToLower() == "true"; break;
                    case "height" when float.TryParse(option.Value, out var y): rb.baseHeight = y; break;
                }
            }
        }

        private bool SpawnRandomBase(RaidableType type, string baseName = null, bool isAdmin = false, BasePlayer owner = null, IPlayer user = null, bool free = false)
        {
            var (key, profile) = GetBuilding(type, "Normal", baseName, owner);
            bool validProfile = IsProfileValid(key, profile);
            var spawns = GetSpawns(type, profile, out var checkTerrain);

            if (validProfile && spawns != null)
            {
                return AddSpawnToQueue(key, profile, checkTerrain, type, spawns, owner, user, Vector3.zero);
            }
            else if (type == RaidableType.Maintained || type == RaidableType.Scheduled)
            {
                Queues.Messages.PrintAll();
            }
            else Queues.Messages.Add(GetDebugMessage(validProfile, isAdmin, owner?.UserIDString, baseName, profile?.Options), null);

            if (!validProfile)
            {
                if (user != null)
                {
                    user.Message(Queues.Messages.GetLast());
                }
            }

            return false;
        }

        private bool AddSpawnToQueue(string key, BaseProfile profile, bool checkTerrain, RaidableType type, RaidableSpawns spawns, BasePlayer owner = null, IPlayer user = null, Vector3 point = default)
        {
            RandomBase rb = new();

            rb.Instance = this;
            rb.BaseName = key;
            rb.Profile = profile;
            rb.Position = point;
            rb.type = type;
            rb.spawns = spawns ??= new(this);
            rb.pasteData = GetPasteData(key);
            rb.checkTerrain = checkTerrain;
            rb.owner = owner;
            rb.user = user;
            rb.id = owner?.UserIDString ?? "";
            rb.userid = owner?.userID ?? 0;
            rb.username = owner?.displayName ?? "";
            rb.typeDistance = GetDistance(rb.type);
            rb.protectionRadius = rb.options.ProtectionRadius(rb.type);
            rb.safeRadius = Mathf.Max(rb.options.ArenaWalls.Radius, rb.protectionRadius);
            rb.buildRadius = Mathf.Max(config.Settings.Management.CupboardDetectionRadius, rb.options.ArenaWalls.Radius, rb.protectionRadius) + 5f;

            if (rb.buildRadius < 105f && !rb.spawns.IsCustomSpawn)
            {
                rb.buildRadius = 105f;
            }

            Queues.Add(rb);

            return true;
        }

        private string GetDebugMessage(bool validProfile, bool isAdmin, string id, string baseName, BuildingOptions options)
        {
            if (options != null && !options.Enabled)
            {
                return mx("Profile Not Enabled", id, baseName);
            }

            if (!validProfile)
            {
                return Queues.Messages.GetLast(id);
            }

            if (!string.IsNullOrEmpty(baseName))
            {
                if (!FileExists(baseName))
                {
                    return mx("FileDoesNotExist", id);
                }
                else if (!Buildings.IsConfigured(baseName))
                {
                    return mx("BuildingNotConfigured", id);
                }
            }

            return Buildings.Profiles.Count == 0 ? mx("NoBuildingsConfigured", id) : Queues.Messages.GetLast(id);
        }
        public RaidableSpawns GetSpawns(RaidableType type, BaseProfile profile, out bool checkTerrain)
        {
            checkTerrain = false;
            RaidableSpawns spawns;
            return type switch
            {
                RaidableType.Maintained when GridController.Spawns.TryGetValue(RaidableType.Maintained, out spawns) => spawns,
                RaidableType.Manual when GridController.Spawns.TryGetValue(RaidableType.Manual, out spawns) => spawns,
                RaidableType.Scheduled when GridController.Spawns.TryGetValue(RaidableType.Scheduled, out spawns) => spawns,
                _ => GridController.Spawns.TryGetValue(RaidableType.Grid, out spawns) && (checkTerrain = true) ? spawns : null
            };
        }

        public (string, BaseProfile) GetBuilding(RaidableType type, string mode, string baseName, BasePlayer player = null)
        {
            if (!string.IsNullOrWhiteSpace(baseName) && Buildings.Removed.Contains(baseName))
            {
                return default;
            }

            bool isBaseNull = string.IsNullOrWhiteSpace(baseName) || baseName.Length == 1 && baseName[0] >= '0' && baseName[0] <= '4';
            using var profiles = DisposableList<(string, BaseProfile)>();

            foreach (var (key, profile) in Buildings.Profiles)
            {
                if (MustExclude(type, profile.Options.AllowPVP))
                {
                    Queues.Messages.Add($"{type} is not configured to include {(profile.Options.AllowPVP ? "PVP" : "PVE")} bases.");
                    continue;
                }

                if (FileExists(key) && (key == baseName || data.Cycle.CanSpawn(type, mode, key, player)))
                {
                    if (!profile.Options.Enabled && key != baseName)
                    {
                        continue;
                    }

                    if (isBaseNull)
                    {
                        profiles.Add((key, profile));
                    }
                    else if (key.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (key, profile);
                    }
                }

                foreach (var (extra, abo) in profile.Options.AdditionalBases)
                {
                    if (FileExists(extra) && (extra == baseName || data.Cycle.CanSpawn(type, mode, extra, player)))
                    {
                        if (!profile.Options.Enabled && extra != baseName)
                        {
                            continue;
                        }

                        var clone = BaseProfile.Clone(profile, extra);
                        clone.Options.PasteOptions = abo.ToList();
                        clone.ProfileName = extra;

                        if (isBaseNull)
                        {
                            profiles.Add((extra, clone));
                        }
                        else if (extra.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        {
                            return (extra, clone);
                        }
                    }
                }
            }

            if (profiles.Count > 0)
            {
                return profiles.GetRandom();
            }

            if (!AnyCopyPasteFileExists)
            {
                Queues.Messages.Print("No copypaste file in any profile exists");
            }
            else Queues.Messages.Print($"Building is unavailable", $"{mode} {type}");

            return default;
        }

        private static bool IsProfileValid(string key, BaseProfile profile)
        {
            if (string.IsNullOrEmpty(key) || profile == null || profile.Options == null)
            {
                return false;
            }

            return profile.Options.Enabled;
        }

        private bool FileExists(string file)
        {
            return Interface.Oxide.DataFileSystem.ExistsDatafile(Path.Combine("copypaste", file));
        }

        private bool DataFileExists(string file)
        {
            return Interface.Oxide.DataFileSystem.ExistsDatafile(file);
        }

        #endregion

        #region Commands

        private void CommandReloadConfig(IPlayer user, string command, string[] args)
        {
            if (user.IsServer || user.Player().IsAdmin)
            {
                if (IsGridLoading() || !IsPasteAvailable())
                {
                    Message(user, IsGridLoading() ? "GridIsLoading" : "PasteOnCooldown");
                    return;
                }
                Message(user, "ReloadInit");
                if (command == "rb.reloadconfig")
                {
                    SetOnSun(false);
                    UI.DestroyAll();
                    Message(user, "ReloadConfig");
                    LoadConfig();
                    Automated.IsMaintainedEnabled = config.Settings.Maintained.Enabled;
                    Automated.StartCoroutine(RaidableType.Maintained, user);
                    Automated.IsScheduledEnabled = config.Settings.Schedule.Enabled;
                    Automated.StartCoroutine(RaidableType.Scheduled, user);
                    Initialize();
                }
                if (command == "rb.reloadprofiles")
                {
                    ServerMgr.Instance.StartCoroutine(ReloadProfiles(user));
                }
                if (command == "rb.reloadtables")
                {
                    ServerMgr.Instance.StartCoroutine(ReloadTables(user));
                }
            }
        }

        private void Initialize()
        {
            if (config.Settings.TeleportMarker)
            {
                Subscribe(nameof(OnMapMarkerAdded));
            }
            else Unsubscribe(nameof(OnMapMarkerAdded));
            Subscribe(nameof(OnPlayerSleepEnded));
            GridController.LoadSpawns();
            if (ZoneManager != null)
            {
                SpawnsController.SetupZones(true);
            }
            Skins.Clear();
            CreateDefaultFiles();
            SetOnSun(true);
            GridController.SetupGrid();
        }

        private bool RemovePlayer(BasePlayer player, bool justEntered = true, float tolerance = 1f)
        {
            if (player.IsKilled()) return false;
            var v = player.transform.position;
            foreach (var raid in Raids)
            {
                if (tolerance > raid.ProtectionRadius * 4f) continue;
                if (!raid.InRangeTolerance(v, tolerance)) continue;
                if (raid.RemovePlayer(player, raid.Location, raid.ProtectionRadius, raid.Type, justEntered))
                {
                    Message(player, "Another plugin has forcefully removed you from this event!");
                    return true;
                }
            }
            return false;
        }

        private bool HasBuyableCooldown(BasePlayer buyer, string mode, bool message = false) => false;

        private bool HasBuyableCooldown(BasePlayer buyer, int level) => false;

        private void CommandBlockRaids(BasePlayer player, string command, string[] args)
        {
            float radius = 5f;
            if (args.Length != 0 && float.TryParse(args[0], out float value) && value > 5f)
            {
                radius = value;
            }
            if (config.Settings.Management.BlockedPositions.RemoveAll(x => InRange(player.transform.position, x.position, radius)) == 0)
            {
                config.Settings.Management.BlockedPositions.Add(new(player.transform.position, radius));
                Player.Message(player, $"Block added; raids will no longer spawn within {radius}m of this position");
                SaveConfig();
            }
            else Player.Message(player, "Block removed; raids are now allowed to spawn at this position");
        }

        private void CommandRaidHunter(IPlayer user, string command, string[] args)
        {
            if (IsGridLoading())
            {
                Message(user, "GridIsLoading");
                return;
            }

            var player = user.Player();
            bool isAdmin = user.IsServer || player.IsAdmin;
            string arg = args.Length >= 1 ? args[0].ToLower() : string.Empty;

            switch (arg)
            {
                case "blockraids":
                    {
                        if (isAdmin)
                        {
                            CommandBlockRaids(player, command, args);
                        }
                        return;
                    }
                case "version":
                    {
                        Message(user, $"RaidableBases {Version} by nivex");
                        return;
                    }
                case "unban":
                    {
                        if (!isAdmin) return;
                        if (args.Length > 1)
                        {
                            foreach (var v in args.Skip(1))
                            {
                                if (RustCore.FindPlayerByName(v) is BasePlayer target)
                                {
                                    Revoke(target.UserIDString);
                                }
                                else if (v.IsSteamId())
                                {
                                    Revoke(v);
                                }
                            }
                        }
                        else
                        {
                            if (user.IsServer) { user.Message("You must specify a user! rb unban <steamid>"); return; }
                            Revoke(user.Id);
                        }
                        void Revoke(string userid)
                        {
                            foreach (var group in permission.GetUserGroups(userid))
                            {
                                if (permission.GroupHasPermission(group, "raidablebases.banned"))
                                {
                                    permission.RevokeGroupPermission(group, "raidablebases.banned");
                                    user.Message($"Banned permission has been removed from group: {group}");
                                }
                            }
                            if (permission.UserHasPermission(userid, "raidablebases.banned"))
                            {
                                permission.RevokeUserPermission(userid, "raidablebases.banned");
                                user.Message($"Banned permission has been revoked.");
                            }
                        }
                        return;
                    }
                case "invite":
                    {
                        CommandInvite(user, player, args);
                        return;
                    }
                case "resettime":
                    {
                        if (isAdmin)
                        {
                            data.RaidTime = DateTime.MinValue;
                        }

                        return;
                    }
                case "wipe":
                    {
                        if (isAdmin)
                        {
                            wiped = true;
                            bool ret = CheckForWipe(config.Settings.Wipe.RemoveFromList);
                            Message(user, ret ? "Wipe successful." : "There's nothing to wipe.");
                        }

                        return;
                    }
                case "revokepg":
                    {
                        if (isAdmin)
                        {
                            RevokePermissionsAndGroups(config.Settings.Wipe.Remove);
                        }

                        return;
                    }
                case "ignore_restart":
                    {
                        if (isAdmin)
                        {
                            bypassRestarting = !bypassRestarting;
                            Message(user, $"Bypassing restart check: {bypassRestarting}");
                        }

                        return;
                    }
                case "savefix":
                    {
                        if (user.IsAdmin || user.HasPermission("raidablebases.allow"))
                        {
                            int removed = BaseEntity.saveList.RemoveWhere(IsKilled);

                            Message(user, $"Removed {removed} invalid entities from the save list.");

                            if (SaveRestore.IsSaving)
                            {
                                SaveRestore.IsSaving = false;
                                Message(user, "Server save has been canceled. You must type server.save again, and then restart your server.");
                            }
                            else Message(user, "Server save is operating normally.");
                        }

                        return;
                    }
                case "tp":
                    {
                        if (player.IsNetworked() && (isAdmin || user.HasPermission("raidablebases.allow")))
                        {
                            RaidableBase raid = null;
                            float num = 9999f;

                            foreach (var other in Raids)
                            {
                                float num2 = player.Distance(other.Location);

                                if (num2 > other.ProtectionRadius * 2f && num2 < num)
                                {
                                    num = num2;
                                    raid = other;
                                }
                            }

                            if (raid != null)
                            {
                                raid.Teleport(player);
                            }
                        }
                        else CommandRaidHunter(user, command, new string[1] { "teleport" });

                        return;
                    }
                case "grid":
                    {
                        if (player.IsNetworked() && (isAdmin || user.HasPermission("raidablebases.ddraw")))
                        {
                            ShowGrid(player, args.Length == 2 && args[1] == "all", args.Length == 2 ? args[1] : string.Empty);
                        }
                        return;
                    }
                case "ladder":
                case "lifetime":
                    {
                        ShowLadder(user, args);
                        return;
                    }
                case "queue_clear":
                    {
                        if (isAdmin)
                        {
                            int num = Queues.queue.Count;
                            Queues.RestartCoroutine();
                            Message(user, $"Cleared and refunded {num} in the queue.");
                        }
                        return;
                    }
            }

            if (config.RankedLadder.Enabled)
            {
                PlayerInfo info = data.GetPlayerInfo(user.Id);

                user.Reply(m("Wins", user.Id, info.Raids, config.Settings.HunterCommand));
            }

            if (Automated.IsScheduledEnabled && (Raids.Count == 0 || !Automated.IsMaintainedEnabled) && GridController.GetRaidTime() > 0)
            {
                ShowNextScheduledEvent(user);
            }

            if (player.IsNetworked())
            {
                DrawRaidLocations(player, isAdmin || player.HasPermission("raidablebases.ddraw"));
            }
        }

        private void CommandInvite(IPlayer user, BasePlayer player, string[] args)
        {
            if (args.Length < 2) { Message(user, "Invite Usage", config.Settings.HunterCommand); return; }
            if (!(RustCore.FindPlayer(args[1]) is BasePlayer target)) { Message(user, "TargetNotFoundId", args[1]); return; }
            var isAllowed = user.IsServer || player.IsAdmin || player.HasPermission("fauxadmin.allowed");
            var raid = isAllowed ? GetNearestBase(target.transform.position) : Raids.FirstOrDefault(x => x.ownerId.IsSteamId() && (x.ownerId == player.userID || x.IsAlly(player.userID, x.ownerId)));
            if (raid == null) { Message(user, isAllowed ? "TargetTooFar" : "Invite Ownership Error"); return; }
            if (!isAllowed && !player.HasPermission("raidablebases.invitecommand") && !raid.IsAlly(player.userID, target.userID)) { Message(user, "Invite Not Ally"); return; }
            if (!raid.raiders.TryGetValue(target.userID, out var raider)) raid.raiders[target.userID] = raider = new(target);
            if (InRange(raid.Location, target.transform.position, raid.ProtectionRadius * 1.5f)) raider.lastActiveTime = Time.realtimeSinceStartupAsDouble;
            if (user.IsServer || player.IsAdmin || user.HasPermission("raidablebases.allow")) Message(user, $"You can use this command to set them as the owner of this raid: {config.Settings.EventCommand} setowner {target.userID}");
            raider.IsAlly = true;
            raider.IsAllowed = true;
            raider.IsParticipant = true;
            Message(target, "Invite Allowed", user.Name);
            Message(user, "Invite Success", target.displayName);
        }

        protected void DrawRaidLocations(BasePlayer player, bool hasPerm)
        {
            if (!player.HasPermission("raidablebases.block.filenames") && !player.IsAdmin && !player.IsDeveloper)
            {
                foreach (var raid in Raids)
                {
                    if (InRange2D(raid.Location, player.transform.position, 100f))
                    {
                        Player.Message(player, $"{raid.BaseName} @ {raid.Location} ({MapHelper.PositionToString(raid.Location)})");
                    }
                }
            }

            if (hasPerm)
            {
                AdminCommand(player, () =>
                {
                    foreach (var raid in Raids)
                    {
                        int num = BasePlayer.activePlayerList.Count(x => x.IsNetworked() && x.Distance(raid.Location) <= raid.ProtectionRadius * 3f);
                        int distance = Mathf.CeilToInt(player.transform.position.Distance(raid.Location));
                        string message = mx("RaidMessage", player.UserIDString, distance, num);
                        string flag = mx(raid.GetAllowKey(), player.UserIDString);

                        DrawText(player, 15f, Color.yellow, raid.Location, string.Format("<size=24>{0}{1} {2} [{3} {4}] {5}</size>", raid.BaseName, flag, raid.Type + ":" + raid.Mode(player.UserIDString, true), message, FormatGridReference(player, raid.Location), raid.Location));

                        foreach (var ri in raid.raiders.Values.Where(x => x.IsAlly && x.player.IsNetworked()))
                        {
                            DrawText(player, 15f, Color.yellow, ri.player.transform.position, $"<size=24>{mx("Ally", player.UserIDString).Replace(":", string.Empty)}</size>");
                        }

                        if (raid.ownerId.IsSteamId() && raid.GetOwner() is BasePlayer owner)
                        {
                            DrawText(player, 15f, Color.yellow, owner.transform.position, $"<size=24>{mx("Owner", player.UserIDString).Replace(":", string.Empty)}</size>");
                        }
                    }
                });
            }
        }

        protected void ShowNextScheduledEvent(IPlayer user)
        {
            string message;
            double time = GridController.GetRaidTime();
            int count = config.Settings.Schedule.GetPlayerCount();

            if (count < config.Settings.Schedule.PlayerLimitMin)
            {
                message = mx("Not Enough Online", user.Id, config.Settings.Schedule.PlayerLimitMin);
            }
            else if (count > config.Settings.Schedule.PlayerLimitMax)
            {
                message = mx("Too Many Online", user.Id, config.Settings.Schedule.PlayerLimitMax);
            }
            else message = FormatTime(time, user.Id);

            QueueNotification(user, "Next", message);
        }

        protected void ShowLadder(IPlayer user, string[] args)
        {
            if (!config.RankedLadder.Enabled || config.RankedLadder.Top < 1)
            {
                return;
            }

            if (args.Contains("resetme"))
            {
                if (data.Players.ContainsKey(user.Id))
                {
                    data.Players[user.Id] = new();
                }
                QueueNotification(user, "Your ranked stats have been reset.");
                return;
            }

            bool seed = args[0].Equals("ladder", StringComparison.OrdinalIgnoreCase);
            using var ladder = DisposableList<(PlayerInfo info, string userid, int val)>();
            foreach (var (userid, info) in data.Players)
            {
                int value = seed ? info.Raids : info.TotalRaids;

                if (value > 0)
                {
                    ladder.Add(new(info, userid, value));
                }
            }

            if (ladder.Count == 0)
            {
                QueueNotification(user, "Ladder Insufficient Players");
                return;
            }

            ladder.Sort((a, b) => b.val.CompareTo(a.val));

            using var sb = DisposableBuilder.Get();
            var ranked = mx(seed ? "RankedLadder" : "RankedTotal", user.Id, config.RankedLadder.Top, mx("Normal", user.Id));

            if (!string.IsNullOrWhiteSpace(ranked))
            {
                sb.AppendLine(ranked);
            }

            int me = ladder.FindIndex(e => e.userid == user.Id);
            int top = Math.Min(config.RankedLadder.Top, ladder.Count);
            for (int i = 0; i < ladder.Count; ++i)
            {
                if (i >= top && i != me)
                    continue;

                int rank = i + 1;
                var (info, userid, val) = ladder[i];
                string name = string.IsNullOrWhiteSpace(info.Name) ? covalence.Players.FindPlayerById(userid)?.Name ?? userid : info.Name.FromFriendlyJson();

                if (string.IsNullOrWhiteSpace(info.Name))
                {
                    info.Name = name.ToFriendlyJson();
                }

                sb.AppendLine(mx("NotifyPlayerFormatEx", user.Id))
                    .Replace("{rank}", $"{rank}")
                    .Replace("{name}", $"{name}")
                    .Replace("{value}", $"{val}");
            }

            QueueNotification(user, sb.ToString());
        }

        private bool Get(string baseName, out (string, BaseProfile) val)
        {
            foreach (var (key, profile) in Buildings.Profiles)
            {
                if (key.Equals(baseName, StringComparison.OrdinalIgnoreCase) || profile.Options.AdditionalBases.Exists(extra => extra.Key.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                {
                    val = (key, profile);
                    return true;
                }
            }
            val = default;
            return false;
        }

        protected void ShowGrid(BasePlayer player, bool showAll, string profile)
        {
            AdminCommand(player, () =>
            {
                foreach (var (type, spawns) in GridController.Spawns)
                {
                    ShowSpawns(player, spawns, showAll, type == RaidableType.Grid ? 500f : 0f);
                }

                foreach (var raid in Raids)
                {
                    if (raid != null && raid.spawns != null && raid.spawns.IsCustomSpawn)
                    {
                        ShowSpawns(player, raid.spawns, showAll, 0f);
                    }
                }

                foreach (var cmi in SpawnsController.Monuments)
                {
                    DrawSphere(player, 30f, Color.blue, cmi.position, cmi.radius);
                    DrawText(player, 30f, Color.cyan, cmi.position, $"<size=16>{cmi.text} ({cmi.radius})</size>");
                }
            });
        }

        private static void ShowSpawns(BasePlayer player, RaidableSpawns spawns, bool showAll, float distance)
        {
            foreach (var rsl in spawns.Spawns)
            {
                if (showAll || distance <= 0f || InRange2D(rsl.Location, player.transform.position, distance))
                {
                    DrawText(player, 30f, Color.green, rsl.Location, "X");
                }
            }

            foreach (CacheType cacheType in Enum.GetValues(typeof(CacheType)))
            {
                (Color color, string text) = cacheType switch
                {
                    CacheType.Generic => (Color.red, "X"),
                    CacheType.Temporary => (Color.cyan, "C"),
                    CacheType.Privilege => (Color.yellow, "TC"),
                    CacheType.Submerged => (Color.blue, "W"),
                    _ => (Color.red, "X")
                };

                foreach (var rsl in spawns.Inactive(cacheType))
                {
                    if (showAll || distance <= 0f || InRange2D(rsl.Location, player.transform.position, distance))
                    {
                        DrawText(player, 30f, color, rsl.Location, text);
                    }
                }
            }
        }

        private void CommandRaidBase(IPlayer user, string command, string[] args)
        {
            var player = user.Player();
            bool isAllowed = user.IsServer || player.IsAdmin || user.HasPermission("raidablebases.allow");
            if (!CanCommandContinue(player, user, isAllowed, args))
            {
                return;
            }
            if (command == config.Settings.EventCommand) // rbe
            {
                ProcessEventCommand(user, player, isAllowed, args);
            }
            else if (command == config.Settings.ConsoleCommand) // rbevent
            {
                ProcessConsoleCommand(user, player, isAllowed, args);
            }
        }

        protected void ProcessEventCommand(IPlayer user, BasePlayer player, bool isAllowed, string[] args) // rbe
        {
            if (!isAllowed || !player.IsNetworked())
            {
                return;
            }

            var baseName = Array.Find(args, FileExists);
            var (key, profile) = GetBuilding(RaidableType.Manual, baseName, null);

            if (!IsProfileValid(key, profile))
            {
                QueueNotification(user, profile == null ? "BuildingNotConfigured" : GetDebugMessage(false, true, user.Id, key, profile.Options));
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, isAllowed ? Mathf.Infinity : 100f, targetMask2, QueryTriggerInteraction.Ignore))
            {
                QueueNotification(user, "LookElsewhere");
                return;
            }

            var safeRadius = Mathf.Max(M_RADIUS * 2f, profile.Options.ArenaWalls.Radius);
            var safe = player.IsAdmin || SpawnsController.IsAreaSafe(hit.point, 0f, safeRadius, safeRadius, safeRadius, manualMask, false, out _, RaidableType.Manual);

            if (!safe && !player.IsFlying && InRange(player.transform.position, hit.point, 50f))
            {
                QueueNotification(user, "PasteIsBlockedStandAway");
                return;
            }

            bool pasted = false;

            if (safe && (isAllowed || !SpawnsController.IsMonumentPosition(hit.point, profile.Options.ProtectionRadius(RaidableType.Manual))))
            {
                var spawns = GridController.Spawns.Values.FirstOrDefault(s => s.Spawns.Exists(t => InRange2D(t.Location, hit.point, M_RADIUS)));
                var point = hit.point + new Vector3(0f, profile.Options.Setup.PasteHeightAdjustment);
                RandomBase rb = new();
                rb.Instance = this;
                rb.BaseName = key;
                rb.Profile = profile;
                rb.Position = point;
                rb.type = RaidableType.Manual;
                rb.spawns = spawns ??= new(this);
                rb.pasteData = GetPasteData(key);
                ParseListedOptions(rb);
                if (profile.Options.Setup.ForcedHeight != -1)
                {
                    point.y = profile.Options.Setup.ForcedHeight;
                }
                point.y += rb.baseHeight;
                if (PasteBuilding(rb))
                {
                    DrawText(player, 10f, Color.red, point, rb.BaseName);
                    if (ConVar.Server.hostname.Contains("Test Server"))
                    {
                        DrawSphere(player, 30f, Color.blue, point, rb.pasteData.radius);
                    }
                    pasted = true;
                }
            }
            else QueueNotification(user, "PasteIsBlocked");

            if (!pasted && Queues.Messages.Any())
            {
                QueueNotification(user, IsGridLoading() ? "GridIsLoading" : Queues.Messages.GetLast(user.Id));
            }
        }

        protected void ProcessConsoleCommand(IPlayer user, BasePlayer player, bool isAllowed, string[] args) // rbevent
        {
            if (IsGridLoading())
            {
                int count = GridController.Spawns.TryGetValue(RaidableType.Grid, out var value) ? value.Spawns.Count : 0;
                QueueNotification(user, "GridIsLoadingFormatted", (Time.realtimeSinceStartupAsDouble - GridController.gridTime).ToString("N02"), count);
                return;
            }
            if (isAllowed)
            {
                SpawnRandomBase(RaidableType.Manual, args.FirstOrDefault(value => FileExists(value)), isAllowed, null, isAllowed && user.IsConnected ? user : null);
                QueueNotification(player, "BaseQueued", Queues.queue.Count);
            }
        }

        private bool CanCommandContinue(BasePlayer player, IPlayer user, bool isAllowed, string[] args)
        {
            if (HandledCommandArguments(player, user, isAllowed, args))
            {
                return false;
            }

            if (!IsCopyPasteLoaded(out var error))
            {
                Message(user, error);
                return false;
            }

            if (!(user.IsServer || player.IsAdmin || user.HasPermission("raidablebases.bypassmaxmanualeventlimit")) && Get(RaidableType.Manual) >= config.Settings.Manual.Max)
            {
                QueueNotification(user, "Max Events", RaidableType.Manual, config.Settings.Manual.Max);
                return false;
            }

            return true;
        }

        private bool HandledCommandArguments(BasePlayer player, IPlayer user, bool isAllowed, string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            switch (args[0].ToLower())
            {
                case "despawn":
                    if (player.IsNetworked() && isAllowed)
                    {
                        DespawnBase(player);
                    }
                    return true;
                case "draw":
                    if (player.IsNetworked())
                    {
                        DrawSpheres(player, isAllowed);
                    }
                    return true;
                case "checkflat":
                    {
                        if (!isAllowed) return false;
                        if (args.Length != 2 || !float.TryParse(args[1], out var radius)) radius = 20f;
                        Message(user, SpawnsController.IsObstructed(player.transform.position, radius, 2.5f, -1f, player) ? "Obstruction test failed" : "Obstruction test passed");
                        var landLevel = SpawnsController.GetLandLevel(player.transform.position, radius, 5f, player);
                        DrawText(player, 30f, Color.red, player.transform.position, $"{landLevel.y - landLevel.x:N01}");
                        return true;
                    }
                case "debug":
                    {
                        if (!isAllowed) return false;
                        DebugMode = !DebugMode;
                        Queues.Messages._user = DebugMode ? user : null;
                        Message(user, $"Debug mode (v{Version}): {DebugMode} (Free version)");
                        ConfigCheckFrames(user);
                        if (DebugMode)
                        {
                            if (!_ownershipReady) Message(user, "Steam Inventory definitions are not yet available.");
                            TimeSpan uptime = TimeSpan.FromSeconds(Time.realtimeSinceStartup);
                            Message(user, $"Server Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s");
                            Message(user, $"Scheduled Events Running: {Automated._scheduledCoroutine != null}");
                            Message(user, $"Maintained Events Running: {Automated._maintainedCoroutine != null}");
                            Message(user, $"Queues Pending: {Queues.queue.Count}");
                            if (!AnyCopyPasteFileExists && !GridController.BadFrameRate)
                            {
                                Message(user, "No copypaste file in any profile exists!");
                            }
                            if (Queues.Messages.Any())
                            {
                                Message(user, $"DEBUG: Last messages:");
                                Queues.Messages.PrintAll(user);
                            }
                            else Message(user, "No debug messages.");
                            if (exConf is JsonException)
                            {
                                Message(user, $"{exConf.Message}\n\n\nYour config contains a json error!");
                            }
                            foreach (var error in profileErrors)
                            {
                                Message(user, $"Json error found in {error}");
                            }
                            foreach (var (type, spawns) in GridController.Spawns)
                            {
                                if (spawns.Spawns.Count > 0)
                                {
                                    Message(user, $"Potential points on {type}: {spawns.Spawns.Count}/{spawns.Cached.Select(x => x.Value).Count()}");
                                }
                            }
                        }
                        return true;
                    }
                case "kill_cleanup":
                    {
                        if (!isAllowed || player == null) return false;
                        var num = 0;
                        using var tmp = FindEntitiesOfType<BaseEntity>(player.transform.position, 100f);
                        foreach (var entity in tmp)
                        {
                            if (entity.OwnerID == 0 && IsKillableEntity(entity))
                            {
                                entity.SafelyKill();
                                num++;
                            }
                        }
                        if (num == 0) Message(user, "You must use the command near the base that you want to despawn. It cannot be owned by a player.");
                        else Message(user, $"Kill sent for {num} entities.");
                        return true;
                    }
                case "despawnall":
                case "despawn_inactive":
                    {
                        if (isAllowed && Raids.Count > 0)
                        {
                            DespawnAll(args[0].ToLower() == "despawn_inactive");
                            Puts(mx("DespawnedAll", null, user.Name));
                        }

                        return true;
                    }
                case "active":
                    {
                        if (!isAllowed) return false;

                        var sb = new StringBuilder();

                        sb.AppendLine($"Queue: {Queues.queue.Count}, Raids: {Raids.Count}");

                        foreach (var spq in Queues.queue)
                        {
                            sb.AppendLine($"{spq.type} with {spq.attempts} attempts");
                        }

                        foreach (var raid in Raids)
                        {
                            sb.AppendLine($"{raid.Type}: ({raid.AllowPVP}) {raid.GetPercentComplete()}% done with {raid.BaseName} at {raid.Location} in {PositionToGrid(raid.Location)} ({raid.GetPercentCompleteMessage()}) {raid.DespawnString}");
                        }

                        foreach (var (type, spawns) in GridController.Spawns)
                        {
                            sb.AppendLine($"{type} with {spawns.Spawns.Count} spawns and {spawns.Cached.Sum(x => x.Value.Count)} cached");
                        }

                        if (config.Settings.Management.RequireAllSpawned)
                        {
                            if (data.Cycle._buildings.Count > 0)
                            {
                                sb.AppendLine("Bases that cannot respawn yet:");
                                foreach (var (mode, buildings) in data.Cycle._buildings)
                                {
                                    sb.AppendLine($"{mode}: {string.Join(", ", buildings)}");
                                }
                            }

                            sb.AppendLine().Append("Bases that can spawn in the current rotation:");

                            var current = RaidableMode.Random;

                            foreach (var (key, profile) in Buildings.Profiles)
                            {
                                foreach (var extra in profile.Options.AdditionalBases.Keys)
                                {
                                    if (FileExists(extra) && data.Cycle.CanSpawn(RaidableType.Maintained, "Normal", extra, player))
                                    {
                                        if (current != "Normal")
                                        {
                                            current = "Normal";
                                            sb.AppendLine();
                                        }
                                        sb.Append(extra).Append(' ');
                                    }
                                }
                            }
                        }

                        Message(user, sb.ToString());

                        return true;
                    }
                case "setowner":
                case "lockraid":
                    {
                        if (args.Length >= 2 && (isAllowed || user.HasPermission("raidablebases.setowner")))
                        {
                            var target = RustCore.FindPlayer(args[1]);

                            if (target.IsNetworked())
                            {
                                var raid = GetNearestBase(target.transform.position);

                                if (raid != null)
                                {
                                    raid.SetOwner(target);
                                    user.Reply(m("RaidLockedTo", user.Id, target.displayName));
                                }
                                else user.Reply(m("TargetTooFar", user.Id));
                            }
                            else user.Reply(m("TargetNotFoundId", user.Id, args[1]));
                        }
                        return true;
                    }
                case "clearowner":
                    {
                        if (player.IsNetworked() && (isAllowed || user.HasPermission("raidablebases.clearowner")))
                        {
                            var target = player;
                            if (isAllowed && args.Length >= 2 && RustCore.FindPlayer(args[1]) is BasePlayer other)
                            {
                                target = other;
                            }
                            if (!(GetNearestBase(target.transform.position) is RaidableBase raid))
                            {
                                QueueNotification(user, "TooFar");
                            }
                            else if (isAllowed || raid.ownerId == player.userID)
                            {
                                raid.ResetEventLock();
                                raid.raiders.Clear();
                                QueueNotification(user, "RaidOwnerCleared");
                            }
                            else QueueNotification(user, "OwnerLocked");
                        }

                        return true;
                    }
            }

            return false;
        }

        private void DrawSpheres(BasePlayer player, bool isAllowed)
        {
            if (isAllowed || player.HasPermission("raidablebases.ddraw"))
            {
                AdminCommand(player, () =>
                {
                    foreach (var raid in Raids)
                    {
                        DrawSphere(player, 30f, Color.blue, raid.Location, raid.ProtectionRadius);
                    }
                });
            }
        }

        private bool IsScheduledReload;

        private void CommandToggle(IPlayer user, string command, string[] args)
        {
            if (!user.HasPermission("raidablebases.config"))
            {
                return;
            }

            if (config.Settings.Maintained.Enabled || args.Contains("maintained"))
            {
                Automated.IsMaintainedEnabled = !Automated.IsMaintainedEnabled;
                Automated.StartCoroutine(RaidableType.Maintained);
                Message(user, $"Toggled maintained events {(Automated.IsMaintainedEnabled ? "on" : "off")}");
                if (args.Contains("maintained"))
                {
                    config.Settings.Maintained.Enabled = Automated.IsMaintainedEnabled;
                    SaveConfig();
                    return;
                }
            }

            if (config.Settings.Schedule.Enabled || args.Contains("scheduled"))
            {
                Automated.IsScheduledEnabled = !Automated.IsScheduledEnabled;
                Automated.StartCoroutine(RaidableType.Scheduled);
                Message(user, $"Toggled scheduled events {(Automated.IsScheduledEnabled ? "on" : "off")}");
                if (args.Contains("scheduled"))
                {
                    config.Settings.Schedule.Enabled = Automated.IsScheduledEnabled;
                    SaveConfig();
                    return;
                }
            }

            Queues.Paused = !Automated.IsScheduledEnabled && !Automated.IsMaintainedEnabled;
            IsScheduledReload = args.Contains("scheduled_reload") && Queues.Paused;
            if (args.Contains("scheduled_reload"))
            {
                Message(user, $"Scheduled reload after all events despawn has been {(IsScheduledReload ? "enabled" : "disabled")}");
            }
            Message(user, $"Toggled queue/spawn manager {(Queues.Paused ? "off" : "on")}");
        }

        private void CommandPopulate(IPlayer user, string command, string[] args)
        {
            if (args.Length == 0 || args[0] != "all")
            {
                user.Reply("You must type: rb.populate all");
                return;
            }

            List<LootItem> items = new();

            ItemManager.GetItemDefinitions().ForEach(def =>
            {
                if (!BlacklistedItems.Contains(def.shortname))
                {
                    items.Add(new(def.shortname));
                }
            });


            AddToList("Normal", items);
            user.Reply("Created oxide/data/RaidableBases/Editable_Lists/Normal.json");

            AddToList("Random", items);
            user.Reply("Created oxide/data/RaidableBases/Editable_Lists/Default.json");

            SaveConfig();
        }

        private void AddToList(string mode, List<LootItem> source)
        {
            if (!Buildings.DifficultyLootLists.TryGetValue(mode, out var lootList))
            {
                Buildings.DifficultyLootLists[mode] = lootList = new();
            }

            foreach (var ti in source)
            {
                if (!lootList.Exists(x => x.shortname == ti.shortname))
                {
                    lootList.Add(ti);
                }
            }

            lootList.ForEach(ti => ti.InitializeArmorSlots());
            lootList.Sort((x, y) => x.shortname.CompareTo(y.shortname));
            Interface.Oxide.DataFileSystem.WriteObject(Path.Combine(Name, "Editable_Lists", mode), lootList);
        }

        private void CommandToggleProfile(IPlayer user, string command, string[] args)
        {
            if (args.Length == 2 && Get(args[1], out (string key, BaseProfile profile) val))
            {
                val.profile.Options.Enabled = !val.profile.Options.Enabled;
                SaveProfile(val.key, val.profile.Options);
                QueueNotification(user, val.profile.Options.Enabled ? "ToggleProfileEnabled" : "ToggleProfileDisabled", val.key);
            }
        }

        private void CommandPasteOption(IPlayer user, string command, string[] args)
        {
            if (args.Length < 2 || args[1] != "true" && args[1] != "false")
            {
                return;
            }
            var changes = 0;
            var search = args[0];
            var value = args[1];
            using var sb = DisposableBuilder.Get();
            var name = args.Length == 3 ? args[2] : null;
            foreach (var (key, profile) in Buildings.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(name) && key != name)
                {
                    continue;
                }
                var pop = profile.Options.PasteOptions.Find(o => o.Key == search);
                if (pop != null && pop.Value != value)
                {
                    changes++;
                    pop.Value = value;
                    sb.Append(key).Append(", ");
                }
                foreach (var (extra, abo) in profile.Options.AdditionalBases)
                {
                    var option = abo.Find(o => o.Key == search);
                    if (option == null)
                    {
                        changes++;
                        abo.Add(new() { Key = search, Value = value });
                        sb.Append(extra).Append(", ");
                    }
                    else if (option.Value != value)
                    {
                        changes++;
                        option.Value = value;
                        sb.Append(extra).Append(", ");
                    }
                }
            }
            if (changes > 0)
            {
                foreach (var (key, profile) in Buildings.Profiles)
                {
                    SaveProfile(key, profile.Options);
                }
                sb.Length -= 2;
                user.Message($"\n{sb}\nChanged {search} for {changes} bases to {value}");
            }
            else user.Message("No changes required.");
        }

        private void CommandConfig(IPlayer user, string command, string[] args)
        {
            if (!user.HasPermission("raidablebases.config"))
            {
                Message(user, "No Permission");
                return;
            }

            if (args.Length == 0 || !arguments.Exists(str => args[0].Equals(str, StringComparison.OrdinalIgnoreCase)))
            {
                Message(user, "ConfigUseFormat", string.Join("|", arguments));
                return;
            }

            string arg = args[0].ToLower();

            switch (arg)
            {
                case "add": ConfigAddBase(user, args); return;
                case "remove": case "clean": ConfigRemoveBase(user, args); return;
                case "list": ConfigListBases(user); return;
                case "toggle": CommandToggleProfile(user, command, args); return;
                case "stability": case "inventories": CommandPasteOption(user, command, args); return;
                case "maintained":
                    {
                        Automated.IsMaintainedEnabled = !Automated.IsMaintainedEnabled;
                        Automated.StartCoroutine(RaidableType.Maintained);
                        Message(user, $"Toggled maintained events {(Automated.IsMaintainedEnabled ? "on" : "off")}");
                        config.Settings.Maintained.Enabled = Automated.IsMaintainedEnabled;
                        SaveConfig();
                        return;
                    }
                case "scheduled":
                    {
                        Automated.IsScheduledEnabled = !Automated.IsScheduledEnabled;
                        Automated.StartCoroutine(RaidableType.Scheduled);
                        Message(user, $"Toggled scheduled events {(Automated.IsScheduledEnabled ? "on" : "off")}");
                        config.Settings.Schedule.Enabled = Automated.IsScheduledEnabled;
                        SaveConfig();
                        return;
                    }
            }

            if (arg.Equals("enable_dome_marker"))
            {
                if (config.Settings.Markers.Radius < 0.25f) config.Settings.Markers.Radius = 0.25f;
                if (config.Settings.Markers.SubRadius < 0.5f) config.Settings.Markers.SubRadius = 0.5f;
                config.Settings.Markers.Manual = true;
                config.Settings.Markers.Scheduled = true;
                config.Settings.Markers.Maintained = true;
                config.Settings.Markers.UseVendingMarker = true;
                config.Settings.Markers.UseExplosionMarker = false;
                SaveConfig();
                foreach (var (key, profile) in Buildings.Profiles)
                {
                    bool update = false;
                    if (profile.Options.SphereAmount < 5)
                    {
                        update = true;
                        profile.Options.SphereAmount = 5;
                    }
                    if (profile.Options.Silent)
                    {
                        update = true;
                        profile.Options.Silent = false;
                    }
                    if (update)
                    {
                        SaveProfile(key, profile.Options);
                    }
                }
                foreach (var raid in Raids)
                {
                    if (raid.Options.SphereAmount < 5)
                    {
                        raid.Options.SphereAmount = 5;
                    }
                    if (raid.Options.Silent)
                    {
                        raid.Options.Silent = false;
                    }
                    raid.ForceUpdateMarker();
                }
                user.Message("Enabled map markers and dome.");
                return;
            }
        }

        #endregion Commands

        #region Garbage

        public void RemoveHeldEntities()
        {
            foreach (var raid in Raids)
            {
                foreach (var re in raid.Entities)
                {
                    if (re is IItemContainerEntity ice && ice != null && re.OwnerID == 0uL)
                    {
                        RaidableBase.ClearInventory(ice.inventory);
                    }
                }
            }
            ItemManager.DoRemoves();
        }

        public void DespawnAll(bool inactiveOnly)
        {
            var bounds = new List<Bounds>();
            var entities = new List<BaseEntity>();
            using var filtered = DisposableList<BaseEntity>();
            int undoLimit = 1;

            using var tmp = Raids.ToPooledList();

            foreach (RaidableBase raid in tmp)
            {
                if (raid == null || !raid.IsPasted || inactiveOnly && (raid.intruders.Count > 0 || raid.ownerId.IsSteamId()))
                {
                    continue;
                }

                filtered.Clear();

                foreach (var entity in raid.Entities)
                {
                    if (!entity.IsKilled() && !raid.DespawnExceptions.Contains(entity))
                    {
                        filtered.Add(entity);
                    }
                }

                raid.Entities.Clear();

                PrepareUndoEntities(filtered);

                if (raid.HasRustNavigationBuild)
                {
                    bounds.Add(raid.RustNavigationBounds);
                }

                entities.AddRange(filtered);

                if (raid.Options.Setup.DespawnLimit > undoLimit)
                {
                    undoLimit = raid.Options.Setup.DespawnLimit;
                }

                raid.Despawn();
            }

            if (entities.Count > 0)
            {
                UndoLoop(entities, bounds, undoLimit);
            }
        }

        private void KillEntity(BaseEntity entity, UndoLoopSettings us)
        {
            if (entity.IsNull())
            {
                return;
            }

            if (entity.ShortPrefabName == "item_drop_backpack")
            {
                var backpack = entity as DroppedItemContainer;
                if (backpack == null || backpack.skinID != RB_SKIN_ID)
                {
                    return;
                }
            }

            var corpse = entity as PlayerCorpse;
            if (corpse != null)
            {
                if (corpse.skinID != RB_SKIN_ID)
                {
                    return;
                }
                corpse.blockBagDrop = true;
            }

            if (!us.DespawnMounts)
            {
                var m = entity.GetParentEntity() as BaseMountable ?? entity as BaseMountable;
                if (m != null && RaidableBase.AnyMounted(m))
                {
                    if (m.skinID == RB_SKIN_ID) m.skinID = 0;
                    return;
                }
                if (IsCustomEntity(entity))
                {
                    return;
                }
            }

            if (entity.OwnerID.IsSteamId() && (entity.PrefabName.Contains("building") ? us.KeepStructures : us.KeepDeployables))
            {
                return;
            }

            if (!(entity is LiquidContainer))
            {
                IInventoryProvider provider = entity as IInventoryProvider;
                if (provider != null)
                {
                    using var containers = DisposableList<ItemContainer>();
                    provider.GetAllInventories(containers);
                    bool doRemoves = false;
                    foreach (var container in containers)
                    {
                        if (container?.itemList?.Count > 0)
                        {
                            if (entity.OwnerID.IsSteamId())
                            {
                                DropLoot(entity, container, BuoyantBox);
                            }
                            else
                            {
                                container.Clear();
                                doRemoves = true;
                            }
                        }
                    }
                    if (doRemoves)
                    {
                        ItemManager.DoRemoves();
                    }
                }
            }

            var io = entity as IOEntity;
            if (io != null)
            {
                var ss = io as SamSite;
                if (ss != null)
                {
                    ss.staticRespawn = false;
                }
                var turret = io as AutoTurret;
                if (turret != null)
                {
                    AutoTurret.interferenceUpdateList.Remove(turret);
                }
                try { io.ClearConnections(); } catch { }
            }

            entity.SafelyKill();
        }

        private DroppedItemContainer DropLoot(BaseEntity ent, ItemContainer container, bool buoyant)
        {
            try
            {
                string prefab = buoyant ? "assets/prefabs/misc/item drop/item_drop_buoyant.prefab" : "assets/prefabs/misc/item drop/item_drop.prefab";
                Vector3 position = ent.CenterPoint();
                if (ent.skinID == 102201)
                {
                    position.y = Mathf.Max(position.y, TerrainMeta.HeightMap.GetHeight(position)) + 0.02f;
                }
                return container.Drop(prefab, position, ent.transform.rotation, 0f);
            }
            catch
            {
                return null;
            }
        }

        private UndoLoopSettings UndoSettings = new();

        private UndoLoopComparer UndoComparer = new();

        private TreeLoopComparer TreeComparer = new();

        public class UndoLoopSettings
        {
            public bool LogToFile, DespawnMounts, KeepStructures, KeepDeployables;
            public UndoLoopSettings() { }
            public UndoLoopSettings(ManagementSettings ms, bool logToFile) => (LogToFile, DespawnMounts, KeepStructures, KeepDeployables) = (logToFile, ms.DespawnMounts, ms.KeepStructures, ms.KeepDeployables);
        }

        public class UndoLoopComparer : IComparer<BaseNetworkable>
        {
            public Dictionary<string, ItemDefinition> DeployableItems;
            public Func<BaseEntity, bool, bool> IsBox;

            private int Evaluate(BaseNetworkable entity) => entity switch
            {
                AutoTurret => 0,
                WeaponRack => -1,
                IceFence or SimpleBuildingBlock => 6,
                BuildingBlock => 5,
                _ when DeployableItems.ContainsKey(entity.PrefabName) => 4,
                StorageContainer sc when IsBox(sc, true) => 3,
                IOEntity io when !IsBox(io, true) => 2,
                BaseVehicle => 1,
                _ => 4
            };

            public int Compare(BaseNetworkable x, BaseNetworkable y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                return Evaluate(x).CompareTo(Evaluate(y));
            }
        }

        public class TreeLoopComparer : IComparer<BaseNetworkable>
        {
            private int Evaluate(BaseNetworkable entity) => entity switch
            {
                VineSwingingTree => 2,
                TreeEntity => 1,
                NaturalBeehive => 0,
                _ => 9
            };

            public int Compare(BaseNetworkable x, BaseNetworkable y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                return Evaluate(x).CompareTo(Evaluate(y));
            }
        }

        private List<Bounds> PendingNavigationBounds = new();
        public void AddRustNavigationBuildData()
        {
            data.RustNavigationBuilds.Clear();
            data.WipeId = SaveRestore.WipeId;

            foreach (var raid in Raids)
            {
                if (raid.HasRustNavigationBuild)
                {
                    data.RustNavigationBuilds.Add(new(raid.RustNavigationBounds));
                }
            }

            foreach (Bounds bounds in PendingNavigationBounds)
            {
                data.RustNavigationBuilds.Add(new(bounds));
            }
        }

        private void PrepareUndoEntities(List<BaseEntity> entities)
        {
            entities.RemoveAll(entity => entity.IsKilled() || (entity.HasParent() && entity.GetParentEntity() is Tugboat));
            entities.Sort(UndoComparer);
        }

        public void UndoLoop(List<BaseEntity> entities, List<Bounds> bounds, int limit, object[] hookObjects = null)
        {
            if (entities != null && entities.Count > 0)
            {
                if (bounds.Count > 0)
                {
                    PendingNavigationBounds.AddRange(bounds);
                }

                ServerMgr.Instance.StartCoroutine(UndoLoopCo(entities, bounds, limit, hookObjects));
            }
        }

        private IEnumerator UndoLoopCo(List<BaseEntity> entities, List<Bounds> bounds, int limit, object[] hookObjects)
        {
            WaitForSeconds instruction = CoroutineEx.waitForSeconds(0.1f);

            int threshold = limit;

            int checks = 0;

            while (entities.Count > 0)
            {
                if (++checks >= threshold)
                {
                    checks = 0;
                    threshold = Performance.report.frameRate < 15 ? 1 : limit;
                    yield return instruction;
                }

                BaseEntity entity = entities[0];

                entities.RemoveAt(0);

                KillEntity(entity, UndoSettings);
            }

            if (bounds.Count > 0)
            {
                foreach (Bounds rebuildBounds in bounds)
                {
                    PendingNavigationBounds.Remove(rebuildBounds);
                }
            }

            if (hookObjects != null && hookObjects.Length > 0)
            {
                if (UndoSettings.LogToFile)
                {
                    LogToFile("despawn", $"{DateTime.Now} Despawn completed {hookObjects[0]}", this, true);
                }
                Interface.CallHook("OnRaidableBaseDespawned", hookObjects);
            }
        }

        #endregion Garbage

        #region IQDronePatrol

        private class CustomPatrol
        {
            public string pluginName;
            public Vector3 position;
            public PositionSetting settingPosition = new();
            public DroneSetting settingDrone = new();

            internal class DroneSetting
            {
                public int droneCountSpawned;
                public int droneAttackedCount;
                public Dictionary<string, int> keyDrones = new();
            }

            internal class PositionSetting
            {
                public int countSpawnPoint;
                public int radiusFindedPoints;
            }
        }

        #endregion

        #region Facepunch TOS Compliance

        private readonly HashSet<int> _dlcItemIds = new();
        private readonly HashSet<ulong> _ownershipIds = new();
        private bool _ownershipReady;

        public void LoadOwnership()
        {
            if (!config.BlockPaidContent)
            {
                _ownershipReady = true;
                return;
            }

            if ((Steamworks.SteamInventory.Definitions?.Length ?? 0) == 0)
            {
                timer.In(3f, LoadOwnership);
                return;
            }

            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (RequiresOwnership(def))
                {
                    _dlcItemIds.Add(def.itemid);
                }

                if (def.skins != null)
                {
                    foreach (var sk in def.skins)
                    {
                        if (sk.id != 0) _ownershipIds.Add((ulong)sk.id);
                    }
                }

                if (def.skins2 != null)
                {
                    foreach (var sk2 in def.skins2)
                    {
                        if (sk2.WorkshopId != 0) _ownershipIds.Add(sk2.WorkshopId);
                    }
                }
            }

            _ownershipReady = true;
        }

        public bool RequiresOwnership(ItemDefinition def, ulong skin)
        {
            if (!config.BlockPaidContent) return false;
            if (skin != 0uL && !_ownershipReady) return true;
            if (skin != 0uL && _ownershipIds.Contains(skin)) return true;
            if (def != null && !_ownershipReady) return RequiresOwnership(def);
            return def != null && _dlcItemIds.Contains(def.itemid);
        }

        public bool RequiresOwnership(ItemDefinition def) => def switch
        {
            null => false,
            { steamItem: { id: not 0 } } => true,
            { steamDlc: { dlcAppID: not 0 } } => true,
            { Blueprint: { NeedsSteamDLC: true } } => true,
            { Parent: { Blueprint: { NeedsSteamDLC: true } } } => true,
            { isRedirectOf: { Blueprint: { NeedsSteamDLC: true } } } => true,
            { isRedirectOf: not null } => true,
            _ => false
        };

        public bool HasUnlocked(BasePlayer player, ItemDefinition def)
        {
            return false;
            //if (def == null || !config.BlockPaidContent || player.UnlockAllSkins) return true;
            //if (_ownershipReady ? !_dlcItemIds.Contains(def.itemid) : !RequiresOwnership(def)) return true;
            //return def.steamDlc != null && def.steamDlc.HasLicense(player.userID);
        }

        public bool HasUnlocked(BasePlayer player, ulong skin)
        {
            return false;
            //if (skin == 0 || !config.BlockPaidContent || player.UnlockAllSkins) return true;
            //if (!_ownershipReady) return false;
            //if (!_ownershipIds.Contains(skin)) return true;
            //return player.blueprints.CheckSkinOwnership((int)skin, player.userID);
        }

        #endregion Facepunch TOS Compliance

        #region Helpers

        private static float Abs(float value) => value < 0 ? -value : value;

        private static bool IsDeepSeaOpen()
        {
            DeepSeaManager dsm = PointEntity<DeepSeaManager>.ServerInstance;
            if (dsm == null) return false;
            return dsm.IsBusy() || dsm.IsOpen();
        }

        public static void SafelyKillNpc(HumanoidNPC npc)
        {
            if (npc != null)
            {
                ulong userid = npc.userID;
                BasePlayer.bots.Remove(npc);
                npc.SafelyKill();
                BasePlayer.freeBotIds.Remove(userid);
            }
        }

        private static void ResetToPool<T>(ref List<T> obj)
        {
            if (obj != null) Pool.FreeUnmanaged(ref obj);
        }

        private static void ResetToPool<T>(ref HashSet<T> obj)
        {
            if (obj != null) Pool.FreeUnmanaged(ref obj);
        }

        private static void ResetToPool<TKey, TValue>(ref Dictionary<TKey, TValue> obj)
        {
            if (obj != null) Pool.FreeUnmanaged(ref obj);
        }

        private static void ResetToPool<T>(ref T obj) where T : class, Pool.IPooled, new()
        {
            if (obj != null) Pool.Free(ref obj);
        }

        public static bool IsCustomEntity(BaseEntity m) => m.PrefabName.StartsWith("assets/custom/");
        public static PooledList<T> DisposableList<T>() => Pool.Get<PooledList<T>>();
        private static void SafelyKill(BaseEntity entity) => entity.SafelyKill();

        private void RegisterPermissions()
        {
            permission.RegisterPermission("raidablebases.allow", this);
            permission.RegisterPermission("raidablebases.allow.commands", this);
            permission.RegisterPermission("raidablebases.bypassmaxmanualeventlimit", this);
            permission.RegisterPermission("raidablebases.setowner", this);
            permission.RegisterPermission("raidablebases.clearowner", this);
            permission.RegisterPermission("raidablebases.ladder.exclude", this);
            permission.RegisterPermission("raidablebases.durabilitybypass", this);
            permission.RegisterPermission("raidablebases.ddraw", this);
            permission.RegisterPermission("raidablebases.mapteleport", this);
            permission.RegisterPermission("raidablebases.canbypass", this);
            permission.RegisterPermission("raidablebases.blockbypass", this);
            permission.RegisterPermission("raidablebases.banned", this);
            permission.RegisterPermission("raidablebases.vipcooldown", this);
            permission.RegisterPermission("raidablebases.notitle", this);
            permission.RegisterPermission("raidablebases.block.fauxadmin", this);
            permission.RegisterPermission("raidablebases.elevators.bypass.building", this);
            permission.RegisterPermission("raidablebases.elevators.bypass.card", this);
            permission.RegisterPermission("raidablebases.hoggingbypass", this);
            permission.RegisterPermission("raidablebases.block.filenames", this);
            permission.RegisterPermission("raidablebases.keepbackpackplugin", this);
            permission.RegisterPermission("raidablebases.keepbackpackrust", this);
            permission.RegisterPermission("raidablebases.invitecommand", this);
            permission.RegisterPermission("raidablebases.limitedannouncements", this);
        }

        public void LoadPlayerData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); } catch (Exception ex) { Puts(ex); }
            data ??= new();
            data.Players ??= new();
            data.Cycle ??= new();
            data.Cycle.Instance = this;
            if (data.protocol == -1)
            {
                data.protocol = Rust.Protocol.save;
            }
            if (data.protocol != Rust.Protocol.save)
            {
                if (config.Settings.Wipe.Protocol)
                {
                    Puts("Protocol change detected; wiping ranked ladder");
                    wiped = true;
                }
                data.protocol = Rust.Protocol.save;
            }
        }

        private void SaveData()
        {
            SavePlayerData();
        }

        public void SavePlayerData()
        {
            if (data != null)
            {
                data.Players.RemoveAll((userid, playerInfo) => playerInfo.TotalRaids == 0 || playerInfo.IsExpired(config.RankedLadder.Days));
                Interface.Oxide.DataFileSystem.WriteObject(Name, data);
            }
        }

        private string GetPlayerData() => JsonConvert.SerializeObject(data.Players);

        internal void StartEntityCleanup()
        {
            IsSpawnerBusy = true;
            var bounds = new List<Bounds>();
            var entities = new List<BaseEntity>();
            using var tmp = Raids.ToPooledList();
            using var filtered = DisposableList<BaseEntity>();
            foreach (var raid in tmp)
            {
                if (!IsShuttingDown)
                {
                    filtered.Clear();
                    Puts(mx("Destroyed Raid"), $"{PositionToGrid(raid.Location)} {raid.Location} ({raid.BaseName})");
                    if (raid.IsOpened) TryInvokeMethod(raid.AwardRaiders);
                    filtered.AddRange(raid.Entities);
                    filtered.RemoveAll(raid.DespawnExceptions.Contains);
                    PrepareUndoEntities(filtered);
                    if (raid.HasRustNavigationBuild)
                    {
                        bounds.Add(raid.RustNavigationBounds);
                    }
                    entities.AddRange(filtered);
                }

                raid.Entities.Clear();
                raid.Despawn();
            }
            if (entities.Count == 0)
            {
                TryInvokeMethod(RemoveHeldEntities);
                TryInvokeMethod(UnsetStatics);
            }
            else UndoLoop(entities, bounds, despawnLimit);
        }

        private void UnsetStatics()
        {
            UI.DestroyAll();
            _permission = null;
            HtmlTagRegex = null;
            EntityToRaid.Clear();
        }

        private bool CheckForWipe(bool revoke)
        {
            bool ret = false;

            if (wiped)
            {
                using var raids = DisposableList<int>();

                if (data.Players.Count > 0)
                {
                    if (AssignTreasureHunters())
                    {
                        foreach (var info in data.Players.Values)
                        {
                            if (info.Raids > 0)
                            {
                                raids.Add(info.Raids);
                            }

                            if (config.Settings.Wipe.Current)
                            {
                                info.ResetWipe();
                            }

                            if (config.Settings.Wipe.Lifetime)
                            {
                                info.ResetLifetime();
                            }
                        }
                    }

                    if (raids.Count > 0)
                    {
                        ret = true;

                        var average = raids.Average();

                        data.Players.RemoveAll((userid, playerInfo) => playerInfo.TotalRaids < average);
                    }
                }

                wiped = false;
                NextTick(SaveData);

                if (revoke)
                {
                    RevokePermissionsAndGroups(config.Settings.Wipe.Remove);
                }
            }

            return ret;
        }

        private bool IsPocketDimensions(BasePlayer player, BaseEntity e)
        {
            if (e.skinID != 0 && e.ShortPrefabName == "woodbox_deployed" && PocketDimensions != null && player.GetActiveItem() is Item activeItem)
            {
                if (Convert.ToBoolean(PocketDimensions?.Call("CheckIsDimensionalItem", activeItem, true))) return true;
                if (Convert.ToBoolean(PocketDimensions?.Call("CheckIsDimensionalItem", activeItem, false))) return true;
            }
            return false;
        }

        private static float GetObstructionRadius(BuildingOptionsProtectionRadius radii, RaidableType type)
        {
            if (radii.Obstruction > 0)
            {
                return Mathf.Clamp(radii.Obstruction, CELL_SIZE, radii.Get(type));
            }
            return radii.Get(type);
        }

        public PasteData GetPasteData(string baseName)
        {
            if (!_pasteData.TryGetValue(baseName, out var pasteData))
            {
                _pasteData[baseName] = pasteData = new();
            }
            return pasteData;
        }

        private bool IsEventOwner(BasePlayer player, bool isLoading)
        {
            return Raids.Exists(raid => raid.ownerId == player.userID && (raid.IsOpened || raid.IsDespawning || isLoading && raid.IsLoading));
        }

        private bool Has(NetworkableId networkableId)
        {
            foreach (var brain in HumanoidBrains.Values)
            {
                if (brain.npc != null && brain.npc.EqualNetID(networkableId))
                {
                    return true;
                }
            }
            return false;
        }

        private bool Has(TriggerBase trigger)
        {
            if (trigger != null)
            {
                foreach (var raid in Raids)
                {
                    if (raid.triggers.ContainsKey(trigger))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool Has(BasePlayer player)
        {
            return player is HumanoidNPC;
        }

        private bool Has(BaseEntity entity)
        {
            if (!TryGetNetworkId(entity, out NetworkableId id))
            {
                return false;
            }

            if (entity.skinID == RB_SKIN_ID)
            {
                return true;
            }

            return EntityToRaid.ContainsKey(id);
        }

        public int Get(RaidableType type)
        {
            int count = 0;
            foreach (var sp in Queues.queue)
            {
                if (sp.type == type)
                {
                    count++;
                }
            }
            foreach (var raid in Raids)
            {
                if (raid.Type == type && !raid.IsDespawning)
                {
                    count++;
                }
            }
            return count;
        }

        private bool HasLimit(RaidableType type)
        {
            return type == RaidableType.Maintained || type == RaidableType.Scheduled;
        }

        public bool Get(ulong userID, out HumanoidBrain brain)
        {
            return HumanoidBrains.TryGetValue(userID, out brain) && brain.raid != null ? brain.raid : null;
        }

        public bool Get(Vector3 target, out RaidableBase raid, float f = 0f)
        {
            foreach (var x in Raids)
            {
                if (InRange(x.Location, target, x.ProtectionRadius + f))
                {
                    raid = x;
                    return true;
                }
            }
            raid = null;
            return false;
        }

        public bool Get(BasePlayer victim, HitInfo info, out RaidableBase raid)
        {
            if (Has(victim) && Get(victim.userID, out HumanoidBrain brain))
            {
                raid = brain.raid;
                return true;
            }
            if (GetPVPDelay(victim.userID, true, out DelaySettings ds) && ds.raid != null)
            {
                raid = ds.raid;
                return true;
            }
            if (Get(victim.transform.position, out raid))
            {
                return true;
            }
            if (info != null && info.PointStart != default && Get(info.PointStart, out raid))
            {
                return true;
            }
            raid = null;
            return false;
        }

        public bool Get(BaseEntity entity, ulong playerSteamID, out RaidableBase raid)
        {
            if (!playerSteamID.IsSteamId() && Get(playerSteamID, out HumanoidBrain brain))
            {
                raid = brain.raid;
                return true;
            }
            if (playerSteamID.IsSteamId() && GetPVPDelay(playerSteamID, true, out DelaySettings ds) && ds.raid != null)
            {
                raid = ds.raid;
                return true;
            }
            if (Get(entity.transform.position, out raid))
            {
                return true;
            }
            raid = null;
            return false;
        }

        public bool Get(BaseEntity entity, out RaidableBase raid)
        {
            if (!TryGetNetworkId(entity, out NetworkableId id))
            {
                raid = null;
                return false;
            }

            return EntityToRaid.TryGetValue(id, out raid) && raid != null;
        }

        private bool Get(TriggerBase trigger, out RaidableBase raid)
        {
            if (trigger != null)
            {
                foreach (var x in Raids)
                {
                    if (x.triggers.ContainsKey(trigger))
                    {
                        raid = x;
                        return true;
                    }
                }
            }
            raid = null;
            return false;
        }

        public bool IsTooClose(Vector3 target, float radius)
        {
            foreach (var raid in Raids)
            {
                if (InRange2D(raid.Location, target, radius))
                {
                    return true;
                }
            }
            return false;
        }

        private static void DrawText(BasePlayer player, float duration, Color color, Vector3 from, object text) => player?.SendConsoleCommand("ddraw.text", duration, color, from, $"<size=24>{text}</size>");
        private static void DrawLine(BasePlayer player, float duration, Color color, Vector3 from, Vector3 to) => player?.SendConsoleCommand("ddraw.line", duration, color, from, to);
        private static void DrawSphere(BasePlayer player, float duration, Color color, Vector3 from, float radius) => player?.SendConsoleCommand("ddraw.sphere", duration, color, from, radius);
        private static bool IsContainerKilled(RaidableBase.TurretInfo info) => info == null || IsContainerKilled(info.turret);
        private static bool IsContainerKilled(StorageContainer container) => container.IsKilled() || container.inventory == null || container.inventory.itemList == null;
        private static bool IsContainerKilled(ContainerIOEntity container) => container.IsKilled() || container.inventory == null || container.inventory.itemList == null;
        private static bool IsKilled(BaseEntity entity) => entity.IsKilled();

        private static bool TryGetNetworkId(BaseEntity entity, out NetworkableId id)
        {
            if (!HasNetworkId(entity))
            {
                id = default;
                return false;
            }

            id = entity.net.ID;
            return true;
        }

        private static bool HasNetworkId(BaseEntity entity) => entity.IsValid() && entity.net.ID.IsValid && !entity.IsDestroyed;

        internal void DestroyProtection()
        {
            if (_elevatorProtection != null)
            {
                UnityEngine.Object.DestroyImmediate(_elevatorProtection);
            }
            if (_turretProtection != null)
            {
                UnityEngine.Object.DestroyImmediate(_turretProtection);
            }
        }

        internal ProtectionProperties GetElevatorProtection()
        {
            if (_elevatorProtection == null)
            {
                _elevatorProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
                _elevatorProtection.name = "EventElevatorProtection";
            }
            return _elevatorProtection;
        }

        internal ProtectionProperties GetTurretProtection()
        {
            if (_turretProtection == null)
            {
                _turretProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
                _turretProtection.name = "EventTurretProtection";
            }
            return _turretProtection;
        }

        public void UpdateAllMarkers()
        {
            foreach (var raid in Raids)
            {
                raid.UpdateMarker();
            }
        }

        private bool IsBusy(out Vector3 pastedLocation)
        {
            foreach (RaidableBase raid in Raids)
            {
                if (raid.IsDespawning || raid.IsLoading)
                {
                    pastedLocation = raid.Location;
                    return true;
                }
            }
            pastedLocation = Vector3.zero;
            return false;
        }

        public static void TryInvokeMethod(Action action)
        {
            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                Puts("{0} ERROR: {1}", action.Method.Name, ex);
            }
        }

        private bool IsKillableEntity(BaseEntity entity)
        {
            return entity.PrefabName.Contains("building") || DeployableItems.ContainsKey(entity.PrefabName) || (entity is VendingMachineMapMarker or MapMarkerGenericRadius or SphereEntity or HumanoidNPC);
        }

        private static PooledList<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1, QueryTriggerInteraction queryTrigger = QueryTriggerInteraction.Collide) where T : BaseEntity
        {
            PooledList<T> entities = DisposableList<T>();
            Vis.Entities(a, n, entities, m, queryTrigger);
            entities.RemoveAll(IsKilled);
            return entities;
        }

        private void CheckOceanLevel()
        {
            if (OceanLevel != WaterSystem.OceanLevel)
            {
                OceanLevel = WaterSystem.OceanLevel;

                if (GridController.Spawns.TryGetValue(RaidableType.Grid, out var spawns))
                {
                    spawns.TryAddRange(CacheType.Submerged);
                }
            }
        }

        private void UnsubscribeSky() => SetOnSun(false, 0);

        private void SetOnSun(bool state, int retries = 0)
        {
            if (retries >= 3 || !config.Settings.Management.Lights)
            {
                return;
            }

            try
            {
                if (state)
                {
                    TOD_Sky.Instance.Components.Time.OnSunrise += OnSunrise;
                    TOD_Sky.Instance.Components.Time.OnSunset += OnSunset;
                }
                else
                {
                    TOD_Sky.Instance.Components.Time.OnSunrise -= OnSunrise;
                    TOD_Sky.Instance.Components.Time.OnSunset -= OnSunset;
                }
            }
            catch
            {
                timer.Once(10f, () => SetOnSun(state, ++retries));
            }
        }

        public void InitializeSkins()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (def.TryGetComponent<ItemModDeployable>(out var imd))
                {
                    if (RequiresOwnership(def)) PaidDeployableItems[imd.entityPrefab.resourcePath] = def;
                    DeployableItems[imd.entityPrefab.resourcePath] = def;
                    ItemDefinitions[def] = imd.entityPrefab.resourcePath;
                }
                if (def.category == ItemCategory.Food || def.category == ItemCategory.Medical)
                {
                    if (def.TryGetComponent<ItemModConsume>(out var con))
                    {
                        _itemModConsume[def] = con;
                    }
                }
            }
        }

        public static void AdminCommand(BasePlayer player, Action action)
        {
            if (!player.IsAdmin && !player.IsDeveloper && player.IsFlying)
            {
                return; // BasePlayer => FinalizeTick => NoteAdminHack => Ban => Cheat Detected!
            }

            bool isAdmin = player.IsAdmin;

            if (!isAdmin)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                player.SendNetworkUpdateImmediate();
            }
            try
            {
                action();
            }
            finally
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        private HashSet<ulong> GetMembers(ulong userid)
        {
            HashSet<ulong> members = new() { userid };

            if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(userid, out var team))
            {
                members.UnionWith(team.members);
            }

            if (Clans?.Call("GetClanMembers", userid) is List<string> clan && !clan.IsNullOrEmpty())
            {
                clan.ForEach(member => members.Add(Convert.ToUInt64(member)));
            }

            return members;
        }

        private uint heli_napalm = 184893264;
        private uint oilfireballsmall = 3550347674;
        private uint rocket_heli = 129320027;
        private uint rocket_heli_napalm = 200672762;

        private void BuildPrefabIds()
        {
            heli_napalm = StringPool.Get("assets/bundled/prefabs/napalm.prefab");
            oilfireballsmall = StringPool.Get("assets/bundled/prefabs/oilfireballsmall.prefab");
            rocket_heli = StringPool.Get("assets/prefabs/npc/patrol helicopter/rocket_heli.prefab");
            rocket_heli_napalm = StringPool.Get("assets/prefabs/npc/patrol helicopter/rocket_heli_napalm.prefab");
        }

        private bool IsHelicopter(HitInfo info, out bool eventHeli)
        {
            eventHeli = false;
            if (info.Initiator.Is(out BaseEntity initiator))
            {
                if (initiator is PatrolHelicopter heli)
                {
                    eventHeli = heli._name != null && !heli._name.Contains("patrolhelicopter");
                    return true;
                }
                if (initiator.prefabID == oilfireballsmall || initiator.prefabID == heli_napalm)
                {
                    return true;
                }
            }
            if (info.WeaponPrefab != null)
            {
                return info.WeaponPrefab.prefabID == rocket_heli || info.WeaponPrefab.prefabID == rocket_heli_napalm;
            }
            return false;
        }

        private Plugin CopyPaste => plugins.Find("CopyPaste");

        public enum CopyPasteError { None, Update, Steam };

        public bool IsCopyPasteLoaded(out string error) => IsCopyPasteLoaded(out error, out _);

        public bool IsCopyPasteLoaded(out string error, out CopyPasteError type)
        {
            type = CopyPasteError.Update;
            error = "You must update or reload CopyPaste (v4.3.0+): https://umod.org/plugins/copy-paste";
            try { if (CopyPaste.Version < new VersionNumber(4, 3, 0)) return false; } catch { return false; }
            type = CopyPasteError.Steam;
            error = "CopyPaste is waiting for Steam definitions.";
            try { if (CopyPaste.Call("IsPasteReady") is not true) return false; } catch { return false; }
            error = null;
            return true;
        }

        private bool PlayerInEvent(BasePlayer player)
        {
            return !player.IsKilled() && (HasPVPDelay(player.userID) || EventTerritory(player.transform.position));
        }

        private bool PlayerInEventPVE(BasePlayer player)
        {
            return !player.IsKilled() && !HasPVPDelay(player.userID) && Get(player.transform.position, out var raid) && !raid.AllowPVP;
        }

        private bool PlayerInEventPVP(BasePlayer player)
        {
            return !player.IsKilled() && (HasPVPDelay(player.userID) || Get(player.transform.position, out var raid) && raid.AllowPVP);
        }

        private float GetPVPDelay(ulong userid)
        {
            return userid.IsSteamId() && GetPVPDelay(userid, true, out DelaySettings ds) ? (float)ds.time : 0f;
        }

        private bool GetPVPDelay(ulong userid, bool check, out DelaySettings ds)
        {
            if (!PvpDelay.TryGetValue(userid, out ds))
            {
                return false;
            }
            if (check)
            {
                return ds != null && ds.time > Time.realtimeSinceStartupAsDouble;
            }
            return ds != null;
        }

        private float GetMaxPVPDelay()
        {
            return config.Settings.Management.PVPDelay;
        }

        [HookMethod("HasPVPDelay")]
        public bool HasPVPDelay(ulong userid)
        {
            return GetPVPDelay(userid) > 0f;
        }

        private void RemovePVPDelay(ulong userid, in DelaySettings ds)
        {
            if (ds != null && ds.Timer != null)
            {
                ds.Timer.Destroy();
            }
            PvpDelay.Remove(userid);
            UnsubscribeDamageHook();
        }

        private bool IsBox(BaseEntity entity, bool inherit) =>
            entity is DisplayingBoxStorage || IsBox(entity.ShortPrefabName, inherit);

        private bool IsBox(string prefab, bool inherit)
        {
            switch (prefab)
            {
                case "krieg_storage_vertical":
                case "krieg_storage_horizontal":
                case "abyss_barrel_horizontal":
                case "abyss_barrel_vertical":
                case "medieval.box.wooden.large":
                case "box.wooden.large":
                case "woodbox_deployed":
                case "coffinstorage":
                case "unused_storage_barrel_a":
                case "storage_barrel_b":
                case "storage_barrel_c":
                case "wicker_barrel":
                case "bamboo_barrel":
                case "industrial_storage_horizontal":
                case "industrial_storage_vertical":
                    return true;
                default:
                    if (inherit)
                    {
                        foreach (var sub in config.Settings.Management.Inherit)
                        {
                            if (prefab.Contains(sub)) return true;
                        }
                    }
                    return prefab.StartsWith("component.box.");
            }
        }

        public float GetDistance(RaidableType type)
        {
            return type switch
            {
                RaidableType.Maintained => Mathf.Clamp(config.Settings.Maintained.Distance, CELL_SIZE, 9000f),
                RaidableType.Scheduled => Mathf.Clamp(config.Settings.Schedule.Distance, CELL_SIZE, 9000f),
                RaidableType.None => Mathf.Max(config.Settings.Maintained.Distance, config.Settings.Schedule.Distance),
                _ => 100f
            };
        }

        private bool IsPVE() => TruePVE != null || SimplePVE != null || NextGenPVE != null || Imperium != null || AegisPVE != null;

        [HookMethod("IsPremium")]
        public bool IsPremium() => false;

        private static bool NullifyDamage(HitInfo info)
        {
            if (info != null)
            {
                info.damageTypes.Clear();
                info.DidHit = false;
                info.DoHitEffects = false;
            }
            return false;
        }

        public bool MustExclude(RaidableType type, bool allowPVP)
        {
            if (!config.Settings.Maintained.IncludePVE && type == RaidableType.Maintained && !allowPVP)
            {
                return true;
            }

            if (!config.Settings.Maintained.IncludePVP && type == RaidableType.Maintained && allowPVP)
            {
                return true;
            }

            if (!config.Settings.Schedule.IncludePVE && type == RaidableType.Scheduled && !allowPVP)
            {
                return true;
            }

            if (!config.Settings.Schedule.IncludePVP && type == RaidableType.Scheduled && allowPVP)
            {
                return true;
            }

            return false;
        }

        private bool AnyNpcs()
        {
            foreach (var brain in HumanoidBrains.Values)
            {
                if (brain == null || brain.raid == null) continue;
                if (brain.raid.ExtendHookSubscription || !brain.npc.IsKilled()) return true;
            }
            return false;
        }

        private string[] GetProfileFiles()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.GetFiles(Path.Combine(Name, "Profiles"));
            }
            catch (UnauthorizedAccessException ex)
            {
                Puts(ex);
                profileErrors.Add("Unauthorized");
            }

            return Array.Empty<string>();
        }

        private string[] GetCopyPasteFiles()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.GetFiles("copypaste");
            }
            catch (UnauthorizedAccessException ex)
            {
                Puts(ex);
                profileErrors.Add("Unauthorized");
            }

            return Array.Empty<string>();
        }

        private bool CheckAutoCorrect(IPlayer user, string file, ref string value)
        {
            string other = GetFileNameWithoutExtension(file);
            if (other == value) return true;
            if (!other.Equals(value, StringComparison.OrdinalIgnoreCase)) return false;
            Message(user, $"Auto-corrected spelling of '{value}' to '{other}'");
            value = other;
            return true;
        }

        private static string GetFileNameWithoutExtension(string file) => Utility.GetFileNameWithoutExtension(file);
        private void ConfigAddBase(IPlayer user, string[] args)
        {
            if (args.Length < 2)
            {
                user.Reply(mx("ConfigAddBaseSyntax", user.Id));
                return;
            }

            using var _sb = DisposableBuilder.Get();
            var values = new List<string>(args);
            values.RemoveAt(0);
            string profileName = values[0];

            user.Reply(mx("Adding", user.Id, string.Join(" ", values.ToArray())));

            if (!Buildings.Profiles.TryGetValue(profileName, out var profile))
            {
                Buildings.Profiles[profileName] = profile = new(this);
                _sb.AppendLine(mx("AddedPrimaryBase", user.Id, profileName));
            }

            foreach (string value in values)
            {
                if (!profile.Options.AdditionalBases.ContainsKey(value))
                {
                    profile.Options.AdditionalBases.Add(value, DefaultPasteOptions);
                    _sb.AppendLine(mx("AddedAdditionalBase", user.Id, value));
                }
            }

            if (_sb.Length > 0)
            {
                user.Reply(_sb.ToString());
                profile.Options.Enabled = true;
                SaveProfile(profileName, profile.Options);
                Buildings.Profiles[profileName] = profile;

                _sb.Clear();
            }
            else user.Reply(mx("EntryAlreadyExists", user.Id));

            values.Clear();
        }

        private void ConfigRemoveBase(IPlayer user, string[] args)
        {
            if (args.Length < 2)
            {
                user.Reply(mx("RemoveSyntax", user.Id));
                return;
            }

            int num = 0;
            var profiles = Buildings.Profiles.ToList();
            var files = (string.Join(" ", args[0].ToLower() == "remove" ? args.Skip(1) : args)).Replace(", ", " ");
            var split = files.Split(' ');

            using var _sb = DisposableBuilder.Get();
            _sb.AppendLine(mx("RemovingFrom", user.Id, string.Join(" ", files)));

            foreach (var profile in profiles)
            {
                foreach (var element in profile.Value.Options.AdditionalBases.ToList())
                {
                    if (split.Contains(element.Key))
                    {
                        _sb.AppendLine(mx("RemovedFrom", user.Id, element.Key, profile.Key));
                        if (profile.Value.Options.AdditionalBases.Remove(element.Key)) num++;
                        SaveProfile(profile.Key, profile.Value.Options);
                    }
                }

                if (split.Contains(profile.Key))
                {
                    _sb.AppendLine(mx("RemovedPrimary", user.Id, profile.Key));
                    if (Buildings.Profiles.Remove(profile.Key)) num++;
                    profile.Value.Options.Enabled = false;
                    SaveProfile(profile.Key, profile.Value.Options);
                }
            }

            _sb.AppendLine(mx("RemovedEntries", user.Id, num));
            user.Reply(_sb.ToString());
            _sb.Clear();
        }

        private void ConfigCheckFrames(IPlayer user)
        {
            if (GridController.BadFrameRate)
            {
                Message(user, "Server FPS must be above 15 for the plugin to function properly...");
            }
        }

        private void ConfigListBases(IPlayer user)
        {
            ConfigCheckFrames(user);
            using var _sb = DisposableBuilder.Get();
            using var _sb2 = DisposableBuilder.Get();
            _sb.AppendLine();

            bool validBase = false;

            if (Buildings.Profiles.Count == 0)
            {
                if (IsGridLoading()) Message(user, "GridIsLoading");
                Message(user, "No profiles are loaded!");
            }

            foreach (var (key, profile) in Buildings.Profiles)
            {
                if (FileExists(key))
                {
                    _sb.Append(key);
                    validBase = true;
                }
                else _sb.Append(key).Append(mx("IsProfile", user.Id));

                if (profile.Options.AdditionalBases.Count > 0)
                {
                    foreach (var extra in profile.Options.AdditionalBases.Keys)
                    {
                        if (FileExists(extra))
                        {
                            _sb.Append(extra).Append(", ");
                            validBase = true;
                        }
                        else _sb2.Append(extra).Append(mx("FileDoesNotExist", user.Id));
                    }

                    if (validBase)
                    {
                        _sb.Length -= 2;
                    }

                    _sb.AppendLine();
                    _sb.Append(_sb2);
                    _sb2.Clear();
                }

                _sb.AppendLine();
            }

            if (!validBase)
            {
                _sb.AppendLine(mx("NoBuildingsConfigured", user.Id));
            }

            Message(user, _sb.ToString());

            if (!IsCopyPasteLoaded(out var error))
            {
                user.Message(error);
            }
        }

        private bool TryRemoveItems(BaseEntity entity)
        {
            if (entity is IItemContainerEntity ice && ice != null && ice.inventory != null)
            {
                bool clearInventory = entity.OwnerID == 0 && entity switch
                {
                    FlameTurret or FogMachine or GunTrap when !config.Settings.Management.DropLoot.Get(entity) => true,
                    BuildingPrivlidge when !config.Settings.Management.AllowCupboardLoot => true,
                    _ => false
                };
                if (clearInventory)
                {
                    RaidableBase.ClearInventory(ice.inventory);
                    return true;
                }
            }
            return false;
        }

        private void DropOrRemoveItems(StorageContainer container, RaidableBase raid, bool forced, bool kill)
        {
            if (!container.inventory.IsEmpty() && (forced || !TryRemoveItems(container)))
            {
                var drop = DropLoot(container, container.inventory, container is BuildingPrivlidge ? raid.Options.BuoyantPrivilege : raid.Options.BuoyantBox);
                if (drop != null && container.OwnerID == 0uL)
                {
                    drop.buryLeftoverItems = false;
                    if (container switch
                    {
                        GunTrap or FlameTurret => config.Settings.Management.DropLoot.CanDespawnGreyWeaponBag(container),
                        _ => raid.Options.DespawnGreyBoxBags
                    })
                    {
                        raid.SetupEntity(drop);
                    }
                    else raid.DespawnExceptions.Add(drop);
                }
            }

            ItemManager.DoRemoves();

            if (kill && (container is BuildingPrivlidge || IsBox(container, false)))
            {
                container.Invoke(container.SafelyKill, 0.1f);
            }
        }

        protected bool DespawnBase(BasePlayer player)
        {
            var raid = GetNearestBase(player.transform.position);

            if (raid == null || raid.IsLoading)
            {
                return false;
            }

            if (raid.AddNearTime <= 0f)
            {
                raid.AddNearTime = 15f;
            }

            raid.Despawn();

            return true;
        }

        private RaidableBase GetNearestBase(Vector3 target, float radius = 100f)
        {
            return Raids.Where(x => InRange2D(x.Location, target, radius)).OrderByAscending(x => (x.Location - target).sqrMagnitude).FirstOrDefault();
        }

        private bool IsTrueDamage(BaseEntity entity, bool isProtectedWeapon)
        {
            if (entity.IsNull())
            {
                return false;
            }

            if (isProtectedWeapon || entity.skinID == 1587601905 || (entity is TeslaCoil or BaseTrap))
            {
                return true;
            }

            return TrueDamage.Contains(entity.ShortPrefabName);
        }

        private Vector3 GetCenterLocation(Vector3 position)
        {
            for (int i = 0; i < Raids.Count; i++)
            {
                if (InRange2D(Raids[i].Location, position, Raids[i].ProtectionRadius))
                {
                    return Raids[i].Location;
                }
            }

            return Vector3.zero;
        }

        private bool HasRaidEntity(BaseEntity entity)
        {
            if (!entity.IsValid() || entity.IsDestroyed || entity.OwnerID != 0uL || entity.enableSaving)
            {
                return false;
            }
            return entity.skinID == RB_SKIN_ID || EntityToRaid.ContainsKey(entity.net.ID);
        }

        private bool HasEventEntity(BaseEntity entity)
        {
            if (entity == null || entity.net == null || entity.IsDestroyed)
            {
                return false;
            }

            return entity is HumanoidNPC || entity.skinID == RB_SKIN_ID || EntityToRaid.ContainsKey(entity.net.ID);
        }

        [HookMethod("GetAllEventsCount")]
        public int GetAllEventsCount() => Raids.Count;

        [HookMethod("GetActiveEventCount")]
        public int GetActiveEventCount() => Raids.Sum(raid => raid.GetPercentComplete() > 0 ? 1 : 0);

        [HookMethod("GetAllEvents")]
        public List<(Vector3, string, int, bool, string, float, float, float, ulong, BasePlayer, List<BasePlayer>, List<BasePlayer>, HashSet<BaseEntity>, string, DateTime, DateTime, float, int)> GetAllEvents()
        {
            Puts("GetAllEvents is changing in the next update, any plugins like RealPVE using this API must update accordingly.");

            List<(Vector3, string, int, bool, string, float, float, float, ulong, BasePlayer, List<BasePlayer>, List<BasePlayer>, HashSet<BaseEntity>, string, DateTime, DateTime, float, int)> results = new(Raids.Count);

            GetAllEventsNonAlloc(results);

            return results;
        }

        [HookMethod("GetAllEventsNonAlloc")]
        public void GetAllEventsNonAlloc(List<(Vector3, string, int, bool, string, float, float, float, ulong, BasePlayer, List<BasePlayer>, List<BasePlayer>, HashSet<BaseEntity>, string, DateTime, DateTime, float, int)> m)
        {
            if (m == null)
            {
                return;
            }

            m.Clear();

            if (m.Capacity < Raids.Count)
            {
                m.Capacity = Raids.Count;
            }

            for (int i = 0; i < Raids.Count; i++)
            {
                RaidableBase r = Raids[i];

                m.Add((r.Location, RaidableMode.Normal, 512, r.AllowPVP, "", 0f, 0f, 0f, r.ownerId, r.GetOwner(), r.GetRaiders(), r.GetIntruders(), r.Entities, r.BaseName, r.spawnDateTime, r.despawnDateTime, r.ProtectionRadius, r.GetLootAmountRemaining()));
            }
        }

        [HookMethod("GetAllDifficulties")]
        public List<(string mode, int level)> GetAllDifficulties()
        {
            return new() { ("Normal", 512) };
        }

        [HookMethod("EventTerritory")]
        public bool EventTerritory(Vector3 position, float x = 0f)
        {
            for (int i = 0; i < Raids.Count; i++)
            {
                RaidableBase raid = Raids[i];
                if (InRange(raid.Location, position, raid.ProtectionRadius + x))
                {
                    return true;
                }
            }
            return false;
        }

        [HookMethod("EventTerritoryAny")]
        public bool EventTerritoryAny(Vector3[] positions, float x = 0f)
        {
            for (int j = 0; j < Raids.Count; j++)
            {
                for (int k = 0; k < positions.Length; k++)
                {
                    RaidableBase raid = Raids[j];
                    if (InRange(raid.Location, positions[k], raid.ProtectionRadius + x))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        [HookMethod("EventTerritoryAll")]
        public bool EventTerritoryAll(Vector3[] positions, float x = 0f)
        {
            for (int k = 0; k < positions.Length; k++)
            {
                bool isEventTerritory = false;
                for (int j = 0; j < Raids.Count; j++)
                {
                    RaidableBase raid = Raids[j];
                    if (InRange(raid.Location, positions[k], raid.ProtectionRadius + x))
                    {
                        isEventTerritory = true;
                        break;
                    }
                }
                if (!isEventTerritory)
                {
                    return false;
                }
            }
            return true;
        }

        [HookMethod("GetPlayersFrom")]
        public List<BasePlayer> GetPlayersFrom(Vector3 position, float x = 0f, bool intruders = false)
        {
            for (int i = 0; i < Raids.Count; i++)
            {
                if (InRange2D(Raids[i].Location, position, Raids[i].ProtectionRadius + x))
                {
                    return intruders ? Raids[i].GetIntruders() : Raids[i].GetRaiders();
                }
            }
            return null;
        }

        [HookMethod("GetOwnerFrom")]
        public BasePlayer GetOwnerFrom(Vector3 position, float x = 0f)
        {
            for (int i = 0; i < Raids.Count; i++)
            {
                if (InRange2D(Raids[i].Location, position, Raids[i].ProtectionRadius + x))
                {
                    return Raids[i].GetOwner();
                }
            }
            return null;
        }

        public static bool InRange2D(Vector3 a, Vector3 b, float distance)
        {
            return (a - b).SqrMagnitude2D() <= distance * distance;
        }

        public static bool InRange(Vector3 a, Vector3 b, float distance)
        {
            return (a - b).sqrMagnitude <= distance * distance;
        }

        private void RevokePermissionsAndGroups(IEnumerable<string> revokes)
        {
            if (revokes.Exists())
            {
                foreach (var target in covalence.Players.All)
                {
                    if (target == null) continue;
                    foreach (var revoke in revokes)
                    {
                        if (target.HasPermission(revoke))
                        {
                            permission.RevokeUserPermission(target.Id, revoke);
                        }

                        if (permission.UserHasGroup(target.Id, revoke))
                        {
                            permission.RemoveUserGroup(target.Id, revoke);
                        }
                    }
                }
            }
        }

        private bool AssignTreasureHunters()
        {
            var players = data.Players.ToList().Where(x => x.Key.IsSteamId() && IsNormalUser(x.Key));

            if (!players.Exists(entry => entry.Value.Any))
            {
                return false;
            }

            foreach (var target in covalence.Players.All)
            {
                if (target.Id.HasPermission("raidablebases.th"))
                {
                    permission.RevokeUserPermission(target.Id, "raidablebases.th");
                }

                if (permission.UserHasGroup(target.Id, "raidhunter"))
                {
                    permission.RemoveUserGroup(target.Id, "raidhunter");
                }
            }

            if (config.RankedLadder.Enabled && config.RankedLadder.Amount > 0 && players.Count > 0)
            {
                AssignTreasureHunters(players);

                Puts(mx("Log Saved", null, "topraider"));
            }

            return true;
        }

        private bool IsNormalUser(string userid)
        {
            if (userid.HasPermission("raidablebases.notitle") || userid.HasPermission("raidablebases.ladder.exclude"))
            {
                return false;
            }

            var user = covalence.Players.FindPlayerById(userid);

            return !(user == null || user.IsBanned);
        }

        private void AssignTreasureHunters(List<KeyValuePair<string, PlayerInfo>> players)
        {
            var ladder = new List<KeyValuePair<string, int>>();

            foreach (var entry in players)
            {
                if (entry.Value.Raids > 0)
                {
                    ladder.Add(new(entry.Key, entry.Value.Raids)); break;
                }
            }

            if (ladder.Count == 0)
            {
                return;
            }

            ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

            using var k = ladder.TakePooledList(config.RankedLadder.Amount);
            foreach (var kvp in k)
            {
                var p = covalence.Players.FindPlayerById(kvp.Key);

                if (p == null || p.HasPermission("raidablebases.notitle") || p.HasPermission("raidablebases.ladder.exclude"))
                {
                    continue;
                }

                permission.GrantUserPermission(p.Id, "raidablebases.th", this);
                permission.AddUserGroup(p.Id, "raidhunter");

                string message = mx("Log Stolen", null, p.Name, p.Id, kvp.Value);

                LogToFile("topraider", $"{DateTime.Now} : {message}", this, true);
                Puts(mx("Log Granted", null, p.Name, p.Id, "raidablebases.th", "raidhunter"));
            }
        }

        private bool CanContinueAutomation() => true;

        private static bool IsModeValid(string mode) => mode != RaidableMode.Disabled && mode != RaidableMode.Random && mode != RaidableMode.Points;

        public string PositionToGrid(Vector3 v) => config.Settings.ShowXZ ? $"{MapHelper.PositionToString(v)} ({v.x:N2} {v.z:N2})" : MapHelper.PositionToString(v);

        public string FormatGridReference(BasePlayer player, Vector3 v)
        {
            List<string> format = new();

            if (config.Settings.ShowGrid)
            {
                format.Add(MapHelper.PositionToString(v));
            }

            if (config.Settings.ShowDir && !player.IsKilled())
            {
                format.Add(format.Count > 0 ? $"({GetDirection(player, v)})" : $"{GetDirection(player, v)} ({Mathf.CeilToInt(player.Distance(v))}m)");
            }

            if (config.Settings.ShowXZ)
            {
                format.Add(format.Count > 0 ? $"({v.x:N2} {v.z:N2})" : $"{v.x:N2} {v.z:N2}");
            }

            return format.Count > 0 ? string.Join(" ", format) : $"{v}";
        }

        private string GetDirection(BasePlayer player, Vector3 target)
        {
            Vector3 targetDir = (target - player.eyes.position).normalized;
            float yaw = Quaternion.LookRotation(targetDir).eulerAngles.y;

            return yaw switch
            {
                >= 0 and < 45 => "North",
                >= 45 and < 90 => "North East",
                >= 90 and < 135 => "East",
                >= 135 and < 180 => "South East",
                >= 180 and < 225 => "South",
                >= 225 and < 270 => "South West",
                >= 270 and < 315 => "West",
                >= 315 and < 360 or _ => "North West",
            };
        }

        private string FormatTime(double seconds, string id = null)
        {
            if (seconds < 0)
            {
                return "0s";
            }

            var ts = TimeSpan.FromSeconds(seconds);

            return mx("TimeFormat", id, (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        #endregion

        #region Configuration

        private List<string> profileErrors = new();
        private bool AnyCopyPasteFileExists;
        private Exception exConf;

        protected void SaveProfile(string key, BuildingOptions options)
        {
            Interface.Oxide.DataFileSystem.WriteObject(Path.Combine(Name, "Profiles", key), options);
        }

        private void LoadTable(string mode, DisposableBuilder _sb, string file, List<LootItem> lootList)
        {
            if (lootList.Count == 0)
            {
                return;
            }

            bool zero = lootList.All(ti => ti.probability == 0f);
            bool stack = lootList.All(ti => ti.stacksize == 0);

            lootList.ForEach(ti =>
            {
                if (zero) ti.probability = 1f;
                if (stack) ti.stacksize = -1;
                ti.InitializeArmorSlots();
            });

            Interface.Oxide.DataFileSystem.WriteObject(file, lootList);

            //var probs = new Dictionary<float, int>();

            lootList.RemoveAll(ti =>
            {
                if (ti.amount == 0 || string.IsNullOrWhiteSpace(ti.shortname) || BlacklistedItems.Contains(ti.shortname))
                {
                    return true;
                }
                //if (!probs.ContainsKey(ti.probability))
                //{
                //    probs[ti.probability] = 0;
                //}
                //probs[ti.probability]++;
                if (ti.amount < ti.amountMin)
                {
                    ti.amount = ti.amountMin;
                }
                if (ti.shortname == "chocholate")
                {
                    ti.shortname = "chocolate";
                }
                if (ti.shortname.EndsWith(".bp"))
                {
                    ti.shortname = ti.shortname.Replace(".bp", "");
                    ti.isBlueprint = true;
                }
                return false;
            });

            if (lootList.Count == 0)
            {
                return;
            }

            //if (probs.Count > 0)
            //{
            //    _sb.Append(file);
            //    _sb.Append(string.Join("\n", probs.OrderBy(x => x.Key).Select(x => $"probability {x.Key} ({x.Value}x)")));
            //    _sb.AppendLine();
            //}

            _sb.AppendLine($"Loaded {lootList.Count} items from {file}");
            Interface.Oxide.CallHook("OnRaidableTableLoaded", file, lootList.Count, JsonConvert.SerializeObject(lootList));
        }

        private List<string> BlacklistedItems = new()
        {
            "ammo.snowballgun", "habrepair", "minihelicopter.repair", "scraptransport.repair", "vehicle.chassis", "vehicle.chassis.4mod", "vehicle.chassis.2mod", "vehicle.module", "car.key", "mlrs", "attackhelicopter",
            "scraptransportheli.repair", "snowmobile", "snowmobiletomaha", "submarineduo", "submarinesolo", "locomotive", "wagon", "workcart", "rhib", "rowboat", "tugboat", "door.key", "blueprintbase", "photo"
        };

        private bool GetTable(string file, out List<LootItem> lootList)
        {
            try
            {
                lootList = Interface.Oxide.DataFileSystem.ReadObject<List<LootItem>>(file);
            }
            catch (JsonReaderException ex)
            {
                Puts("Json error in loot table file: {0}\nUse a json validator: www.jsonlint.com\n\n{1}", file, ex);
                lootList = null;
                return false;
            }

            lootList ??= new();
            lootList.RemoveAll(ti => ti == null || string.IsNullOrWhiteSpace(ti.shortname));

            return lootList.Count > 0;
        }

        private bool isDefaultMessagesLoaded;

        protected override void LoadDefaultMessages()
        {
            if (!isDefaultMessagesLoaded)
            {
                lang.RegisterMessages(new()
                {
                    ["No Permission"] = "You do not have permission to use this command.",
                    ["Building is blocked for spawns!"] = "<color=#FF0000>Building is blocked until a raidable base spawns!</color>",
                    ["Building is blocked!"] = "<color=#FF0000>Building is blocked near raidable bases!</color>",
                    ["Ladders are blocked!"] = "<color=#FF0000>Ladders are blocked in raidable bases!</color>",
                    ["Barricades are blocked!"] = "<color=#FF0000>Barricades are blocked in raidable bases!</color>",
                    ["Cupboards are blocked!"] = "<color=#FF0000>Tool cupboards are blocked in raidable bases!</color>",
                    ["Ladders Require Building Privilege!"] = "<color=#FF0000>You need building privilege to place ladders!</color>",
                    ["Profile Not Enabled"] = "This profile is not enabled: <color=#FF0000>{0}</color>.",
                    ["Max Events"] = "Maximum limit of {0} events (<color=#FF0000>{1}</color>) has been reached!",
                    ["Manual Event Failed"] = "Event failed to start! Unable to obtain a valid position. Please try again.",
                    ["Help"] = "/{0} <tp> - start a manual event, and teleport to the position if TP argument is specified and you are an admin.",
                    ["RaidOpenMessage"] = "<color=#C0C0C0>A {0} raidable base event has opened at <color=#FFFF00>{1}</color>! You are <color=#FFA500>{2}m</color> away. [{3}]</color>",
                    ["RaidOpenNoMapMessage"] = "<color=#C0C0C0>A {0} raidable base event has opened! You are <color=#FFA500>{1}m</color> away. [{2}]</color>",
                    ["Next"] = "<color=#C0C0C0>No events are open. Next event in <color=#FFFF00>{0}</color></color>",
                    ["Wins"] = "<color=#C0C0C0>You have looted <color=#FFFF00>{0}</color> raid bases! View the ladder using <color=#FFA500>/{1} ladder</color> or <color=#FFA500>/{1} lifetime</color></color>",
                    ["RaidMessage"] = "Raidable Base {0}m [{1} players]",
                    ["RankedLadder"] = "<color=#FFFF00>[ Top {0} {1} (This Wipe) ]</color>:",
                    ["RankedTotal"] = "<color=#FFFF00>[ Top {0} {1} (Lifetime) ]</color>:",
                    ["Ladder Insufficient Players"] = "<color=#FFFF00>No players are on the ladder yet!</color>",
                    ["Next Automated Raid"] = "Next automated raid in {0} at {1}",
                    ["Not Enough Online"] = "Not enough players online ({0} minimum)",
                    ["Too Many Online"] = "Too many players online ({0} maximum)",
                    ["Raid Base Distance"] = "<color=#C0C0C0>Raidable Base <color=#FFA500>{0}m</color>",
                    ["Destroyed Raid"] = "Destroyed a left over raid base at {0}",
                    ["Indestructible"] = "<color=#FF0000>Treasure chests are indestructible!</color>",
                    ["Log Stolen"] = "{0} ({1}) Raids {2}",
                    ["Log Granted"] = "Granted {0} ({1}) permission {2} for group {3}",
                    ["Log Saved"] = "Raid Hunters have been logged to: {0}",
                    ["Prefix"] = "[ <color=#406B35>Raidable Bases</color> ] ",
                    ["RestartDetected"] = "Restart detected. Next event in {0} minutes.",
                    ["EconomicsDeposit"] = "You have received <color=#FFFF00>${0}</color> for stealing the treasure!",
                    ["EconomicsWithdraw"] = "You have paid <color=#FFFF00>${0}</color> for a raidable base!",
                    ["EconomicsWithdrawReset"] = "<color=#FFFF00>${0}</color> was paid for your cooldown reset!",
                    ["EconomicsWithdrawGift"] = "{0} has paid <color=#FFFF00>${1}</color> for your raidable base!",
                    ["EconomicsWithdrawFailed"] = "You do not have <color=#FFFF00>${0}</color> for a raidable base!",
                    ["ServerRewardPoints"] = "You have received <color=#FFFF00>{0} RP</color> for stealing the treasure!",
                    ["ServerRewardPointsTaken"] = "You have paid <color=#FFFF00>{0} RP</color> for a raidable base!",
                    ["ServerRewardPointsTakenReset"] = "<color=#FFFF00>{0} RP</color> was paid for your cooldown reset!",
                    ["ServerRewardPointsGift"] = "{0} has paid <color=#FFFF00>{1} RP</color> for your raidable base!",
                    ["ServerRewardPointsFailed"] = "You do not have <color=#FFFF00>{0} RP</color> for a raidable base!",
                    ["SkillTreeXP"] = "You have received <color=#FFFF00>{0} XP</color> for stealing the treasure!",
                    ["InvalidItem"] = "Invalid item shortname: {0}. Use /{1} additem <shortname> <amount> [skin]",
                    ["AddedItem"] = "Added item: {0} amount: {1}, skin: {2}",
                    ["CustomPositionSet"] = "Custom event spawn location set to: {0}",
                    ["CustomPositionRemoved"] = "Custom event spawn location removed.",
                    ["OpenedEvents"] = "Opened {0}/{1} events.",
                    ["OnPlayerEntered"] = "<color=#FF0000>You have entered a raidable PVP base!</color>",
                    ["OnPlayerEnteredPVE"] = "<color=#FF0000>You have entered a raidable PVE base!</color>",
                    ["OnPlayerEntryRejected"] = "<color=#FF0000>You cannot enter an event that does not belong to you!</color>",
                    ["OnLockedToRaid"] = "<color=#FF0000>You are now locked to this base.</color>",
                    ["OnFirstPlayerEntered"] = "<color=#FFFF00>{0}</color> is the first to enter the raidable base at <color=#FFFF00>{1}</color>",
                    ["OnChestOpened"] = "<color=#FFFF00>{0}</color> is the first to see the loot at <color=#FFFF00>{1}</color>!</color>",
                    ["OnRaidFinished"] = "The raid at <color=#FFFF00>{0}</color> has been unlocked!",
                    ["CannotBeMounted"] = "You cannot loot the treasure while mounted!",
                    ["CannotTeleport"] = "You are not allowed to teleport from this event.",
                    ["CannotRemove"] = "You are not allowed to remove entities from this event.",
                    ["MustBeAuthorized"] = "You must have building privilege to access this treasure!",
                    ["OwnerLocked"] = "This treasure belongs to someone else!",
                    ["CannotFindPosition"] = "Could not find a random position!",
                    ["PasteOnCooldown"] = "Paste is on cooldown!",
                    ["SpawnOnCooldown"] = "Try again, a manual spawn was already requested.",
                    ["ThievesDespawn"] = "<color=#FFFF00>The {0} base at <color=#FFFF00>{1}</color> has been despawned by <color=#FFFF00>{2}</color>!</color>",
                    ["Thieves"] = "<color=#FFFF00>The {0} base at <color=#FFFF00>{1}</color> has been raided by <color=#FFFF00>{2}</color>!</color>",
                    ["TargetNotFoundId"] = "<color=#FFFF00>Target {0} not found, or not online.</color>",
                    ["TargetNotFoundNoId"] = "<color=#FFFF00>No steamid provided.</color>",
                    ["BaseQueued"] = "<color=#FFFF00>Your base will spawn when a position is found. It is currently at position {0} in the queue.</color>",
                    ["DestroyingBaseAt"] = "<color=#C0C0C0>Destroying raid base at <color=#FFFF00>{0}</color> in <color=#FFFF00>{1}</color> minutes!</color>",
                    ["PasteIsBlocked"] = "You cannot start a raid base event there!",
                    ["LookElsewhere"] = "Unable to find a position; look elsewhere.",
                    ["BuildingNotConfigured"] = "You cannot spawn a base that is not configured.",
                    ["NoBuildingsConfigured"] = "No valid buildings have been configured.",
                    ["DespawnBaseSuccess"] = "<color=#C0C0C0>Despawning the nearest raid base to you!</color>",
                    ["DespawnedAt"] = "{0} despawned a base manually at {1}",
                    ["DespawnedAll"] = "{0} despawned all bases manually",
                    ["Normal"] = "normal",
                    ["DespawnBaseNoneAvailable"] = "<color=#C0C0C0>You must be within 100m of a raid base to despawn it.</color>",
                    ["GridIsLoading"] = "The grid is loading; please wait until it has finished.",
                    ["GridIsLoadingFormatted"] = "Grid is loading. The process has taken {0} seconds so far with {1} locations added on the grid.",
                    ["TooPowerful"] = "<color=#FF0000>This place is guarded by a powerful spirit. You sheath your wand in fear!</color>",
                    ["TooPowerfulDrop"] = "<color=#FF0000>This place is guarded by a powerful spirit. You drop your wand in fear!</color>",
                    ["InstallSupportedCopyPaste"] = "You must update your version of CopyPaste to 4.1.32 or higher!",
                    ["DoomAndGloom"] = "<color=#FF0000>You have left a {0} zone and can be attacked for another {1} seconds!</color>",
                    ["NoConfiguredLoot"] = "Error: No loot found in the config!",
                    ["NoContainersFound"] = "Error: No usable containers found for {0} @ {1}!",
                    ["NoEntitiesFound"] = "Error: No entities found at {0} @ {1}!",
                    ["NoBoxesFound"] = "Error: No usable boxes found for {0} @ {1}!",
                    ["NoLootSpawned"] = "Error: No loot was spawned!",
                    ["LoadedManual"] = "Loaded {0} manual spawns.",
                    ["LoadedMaintained"] = "Loaded {0} maintained spawns.",
                    ["LoadedScheduled"] = "Loaded {0} scheduled spawns.",
                    ["Initialized Grid"] = "Grid initialization completed in {0} seconds and {1} milliseconds on a {2} size map with {3} potential points.",
                    ["Initialized Grid Sea"] = "{0} potential points are on the seabed grid.",
                    ["EntityCountMax"] = "Command disabled due to entity count being greater than 300k",
                    ["NotifyPlayerFormatEx"] = "<color=#ADD8E6>{rank}</color>. <color=#C0C0C0>{name}</color> (raided <color=#FFFF00>{value}</color> bases)",
                    ["ConfigUseFormat"] = "Use: rb.config <{0}> [base] [subset]",
                    ["ConfigAddBaseSyntax"] = "Use: rb.config add nivex1 nivex4 nivex5 nivex6",
                    ["FileDoesNotExist"] = " > This file does not exist\n",
                    ["IsProfile"] = " > Profile\n",
                    ["ListingAll"] = "Listing all primary bases and their subsets:",
                    ["PrimaryBase"] = "Primary Base: ",
                    ["AdditionalBase"] = "Additional Base: ",
                    ["NoValidBuilingsWarning"] = "No valid buildings are configured with a valid file that exists. Did you configure valid files and reload the plugin?",
                    ["Adding"] = "Adding: {0}",
                    ["AddedPrimaryBase"] = "Added Primary Base: {0}",
                    ["AddedAdditionalBase"] = "Added Additional Base: {0}",
                    ["EntryAlreadyExists"] = "That entry already exists.",
                    ["RemoveSyntax"] = "Use: rb.config remove nivex1",
                    ["RemovingFrom"] = "\nRemoving: {0}",
                    ["RemovedPrimary"] = "Removed primary: {0}",
                    ["RemovedFrom"] = "Removed {0} from {1}",
                    ["RemovedEntries"] = "Removed {0} entries",
                    ["ToggleProfileEnabled"] = "{0} profile is now enabled.",
                    ["ToggleProfileDisabled"] = "{0} profile is now disabled.",
                    ["PVPFlag"] = "[<color=#FF0000>PVP</color>] ",
                    ["PVEFlag"] = "[<color=#008000>PVE</color>] ",
                    ["PVP ZONE"] = "PVP ZONE",
                    ["PVE ZONE"] = "PVE ZONE",
                    ["OnPlayerExit"] = "<color=#FF0000>You have left a raidable PVP base!</color>",
                    ["OnPlayerExitPVE"] = "<color=#FF0000>You have left a raidable PVE base!</color>",
                    ["PasteIsBlockedStandAway"] = "You cannot start a raid base event there because you are too close to the spawn. Either move or use noclip.",
                    ["ReloadConfig"] = "Reloading config...",
                    ["ReloadMaintainCo"] = "Stopped maintain coroutine.",
                    ["ReloadScheduleCo"] = "Stopped schedule coroutine.",
                    ["ReloadSpawnerCo"] = "Stopped spawner coroutine.",
                    ["ReloadInit"] = "Initializing...",
                    ["YourCorpse"] = "Your Corpse",
                    ["EjectedYourCorpse"] = "Your corpse has been ejected from your raid.",
                    ["NotAllowed"] = "<color=#FF0000>That action is not allowed in this zone.</color>",
                    ["AllowedZones"] = "Allowed spawn points in {0} zones.",
                    ["BlockedZones"] = "Blocked spawn points in {0} zones.",
                    ["UI Format"] = "{0} - Loot Remaining: {1} [Despawn in {2} mins]",
                    ["UI FormatContainers"] = "{0} - Loot Remaining: {1}",
                    ["UI FormatMinutes"] = "{0} [Despawn in {1} mins]",
                    ["UIFormatLockoutMinutes"] = "{0}m",
                    ["HoggingFinishYourRaid"] = "<color=#FF0000>You must finish your last raid at {0} before joining another.</color>",
                    ["HoggingFinishYourRaidClan"] = "<color=#FF0000>Your clan mate `{0}` must finish their last raid at {1}.</color>",
                    ["HoggingFinishYourRaidTeam"] = "<color=#FF0000>Your team mate `{0}` must finish their last raid at {1}.</color>",
                    ["HoggingFinishYourRaidFriend"] = "<color=#FF0000>Your friend `{0}` must finish their last raid at {1}.</color>",
                    ["TimeFormat"] = "{0:D2}h {1:D2}m {2:D2}s",
                    ["TargetTooFar"] = "Your target is not close enough to a raid.",
                    ["TooFar"] = "You are not close enough to a raid.",
                    ["RaidLockedTo"] = "Raid has been locked to: {0}",
                    ["RaidOwnerCleared"] = "Raid owner has been cleared.",
                    ["TooCloseToABuilding"] = "Too close to another building",
                    ["CommandNotAllowed"] = "You are not allowed to use this command right now.",
                    ["MapMarkerOrderWithMode"] = "{0}{1} {2}{3}",
                    ["MapMarkerOrderWithoutMode"] = "{0}{1}{2}",
                    ["BannedAdmin"] = "You have the raidablebases.banned permission and as a result are banned from these events.",
                    ["Banned"] = "You are banned from these events.",
                    ["PrimitiveOnly"] = "You must use primitive raiding tools only.",
                    ["NoMountedDamageTo"] = "You cannot damage mounts!",
                    ["NoMountedDamageFrom"] = "You cannot do damage while mounted to this!",
                    ["NoDamageFromOutsideToBaseInside"] = "You must be inside of the event to damage the base!",
                    ["NoDamageToEnemyBase"] = "You are not allowed to damage another players event!",
                    ["NoDamageToBoxes"] = "This box is immune to damage.",
                    ["None"] = "None",
                    ["You"] = "You",
                    ["Enemy"] = "Enemy",
                    ["RP"] = "RP",
                    ["Ally"] = "Ally",
                    ["Owner"] = "Owner:",
                    ["Completed"] = "COMPLETED",
                    ["No owner"] = "No owner",
                    ["Loot"] = "Loot:",
                    ["OwnerFormat"] = "OWNER: <color={0}>{1}</color> ",
                    ["Active"] = "Active",
                    ["Inactive"] = "Inactive",
                    ["InactiveTimeLeft"] = " [Inactive in {0} mins]",
                    ["Status:"] = "YOUR STATUS: <color={0}>{1}</color>{2}",
                    ["Claimed"] = "(Claimed)",
                    ["Refunded"] = "You have been refunded: {0}",
                    ["TryAgain"] = "Try again at a different location.",
                    ["Elevator Health"] = "Elevator Health:",
                    ["Elevator Green Card"] = "Elevator access requires a green access card!",
                    ["Elevator Blue Card"] = "Elevator access requires a blue access card!",
                    ["Elevator Red Card"] = "Elevator access requires a red access card!",
                    ["Elevator Special Card"] = "Elevator access requires a special access card!",
                    ["Elevator Privileges"] = "Elevator access requires building privileges!",
                    ["Invite Usage"] = "/{0} invite <name>",
                    ["Invite Ownership Error"] = "You must have ownership of a raid to invite someone.",
                    ["Invite Success"] = "You have invited {0} to join your raid.",
                    ["Invite Allowed"] = "You have been allowed to join the raid owned by {0}.",
                    ["No Reward: Flying"] = "You cannot earn a reward while flying.",
                    ["No Reward: Vanished"] = "You cannot earn a reward while vanished.",
                    ["No Reward: Inactive"] = "You cannot earn a reward while inactive.",
                    ["No Reward: Admin"] = "Administrators cannot earn rewards.",
                    ["No Reward: Not Owner"] = "You must be the owner of the raid to be eligible.",
                    ["No Reward: Not Ally"] = "You must be the owner or an ally of the raid to be eligible.",
                    ["No Reward: Not A Participant"] = "You must be a participant of the raid to be eligible.",
                    ["NoFauxAdmin"] = "You may not use admin cheats in this area.",
                    ["MLRS Target Denied"] = "You are not allowed to target this location!",
                }, this, "en");
                isDefaultMessagesLoaded = true;
            }
        }

        public void TryMessage(BasePlayer player, string key, params object[] args)
        {
            if (player.IsValid() && !waiting.Contains(player.userID))
            {
                ulong userid = player.userID;

                waiting.Add(userid);
                QueueNotification(player, key, args);
                timer.Once(10f, () => waiting.Remove(userid));
            }
        }

        public void Message(BasePlayer player, string key, params object[] args)
        {
            if (player.IsNetworked())
            {
                QueueNotification(player, key, args);
            }
        }

        public void Message(IPlayer user, string key, params object[] args)
        {
            if (user != null)
            {
                user.Reply(mx(key, null, args));
            }
        }

        private void CheckNotifications()
        {
            if (_notifications.Count == 0)
                return;

            for (int i = 0; i < _notifications.Count; i++)
            {
                var (userid, notes) = _notifications.ElementAt(i);

                if (notes.Count > 0)
                {
                    var n = notes[0];
                    int take = 1;
                    int len = n.messageBare.Length;
                    using var sbBare = DisposableBuilder.Get();
                    using var sbFull = DisposableBuilder.Get();
                    sbBare.Append(n.messageBare);
                    sbFull.Append(n.messageFull);

                    for (int j = 1; j < notes.Count; j++)
                    {
                        if (len + 2 + notes[j].messageBare.Length > 140) break;

                        sbBare.AppendLine().Append(notes[j].messageBare);
                        sbFull.AppendLine().Append(notes[j].messageFull);
                        len += 2 + notes[j].messageBare.Length;
                        take++;
                    }

                    n.messageBare = sbBare.ToString();
                    n.messageFull = sbFull.ToString();
                    SendNotification(n);

                    for (int j = 0; j < take; j++)
                    {
                        var obj = notes[0];
                        notes.RemoveAt(0);
                        Pool.Free(ref obj);
                    }
                }

                if (notes.Count == 0)
                {
                    _notifications.Remove(userid);
                    Pool.Free(ref notes);
                    i--;
                }
            }
        }

        private void QueueNotification(IPlayer user, string key, params object[] args)
        {
            if (user.Object is BasePlayer player)
            {
                QueueNotification(player, key, args);
            }
            else user.Reply(mx(key, user.Id, args));
        }

        private void QueueNotification(BasePlayer player, string key, params object[] args)
        {
            if (!player.IsOnline())
            {
                return;
            }

            string message = m(key, player.UserIDString, args);

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (config.EventMessages.Message)
            {
                Player.Message(player, message, config.Settings.ChatID);
            }

            if (config.GUIAnnouncement.Enabled || config.UI.AA.Enabled || config.EventMessages.NotifyType != -1 || config.EventMessages.RustStyle != EventMessageSettings.NoRustStyle)
            {
                if (!_notifications.TryGetValue(player.userID, out var notifications))
                {
                    _notifications[player.userID] = notifications = Pool.Get<List<Notification>>();
                }
                Notification notification = Pool.Get<Notification>();
                notification.player = player;
                notification.messageFull = mx(key, player.UserIDString, args);
                notification.messageBare = rf(mx(key, player.UserIDString, args));
                notifications.Add(notification);
            }
        }

        private void SendNotification(Notification notification)
        {
            if (!notification.player.IsOnline())
            {
                return;
            }

            bool messageWasSent = config.EventMessages.Message;
            if (config.GUIAnnouncement.Enabled && GUIAnnouncements.CanCall())
            {
                GUIAnnouncements?.Call("CreateAnnouncement", notification.messageFull, config.GUIAnnouncement.TintColor, config.GUIAnnouncement.TextColor, notification.player);
                messageWasSent = true;
            }

            if (config.UI.AA.Enabled && AdvancedAlerts.CanCall())
            {
                AdvancedAlerts?.Call("SpawnAlert", notification.player, "hook", notification.messageFull, config.UI.AA.AnchorMin, config.UI.AA.AnchorMax, config.UI.AA.Time);
                messageWasSent = true;
            }

            if (config.EventMessages.NotifyType != -1 && Notify.CanCall())
            {
                Notify?.Call("SendNotify", notification.player, config.EventMessages.NotifyType, notification.messageFull);
                messageWasSent = true;
            }

            if (config.EventMessages.RustStyle != EventMessageSettings.NoRustStyle)
            {
                if (notification.messageBare.Length > 140)
                {
                    if (!messageWasSent)
                    {
                        Player.Message(notification.player, notification.messageFull, config.Settings.ChatID);
                    }
                    return;
                }
                notification.player.ShowToast(config.EventMessages.RustStyle, config.EventMessages.StripRustTip ? notification.messageBare : notification.messageFull);
            }
        }

        public string m(string key, string id = null, params object[] args)
        {
            if (id == null || id == "server_console")
            {
                return mx(key, id, args);
            }

            using var _sb2 = DisposableBuilder.Get();

            if (config.EventMessages.Prefix)
            {
                _sb2.Append(lang.GetMessage("Prefix", this, id));
            }

            string message = lang.GetMessage(key, this, id);

            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            _sb2.Append(message);

            return args.Length > 0 ? string.Format(_sb2.ToString(), args) : _sb2.ToString();
        }

        public string mx(string key, string id = null, params object[] args)
        {
            using var _sb2 = DisposableBuilder.Get();

            string message = lang.GetMessage(key, this, id);

            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            _sb2.Append(id == null || id == "server_console" ? rf(message) : message);

            return args.Length > 0 ? string.Format(_sb2.ToString(), args) : _sb2.ToString();
        }

        public static Regex HtmlTagRegex;

        public static string rf(string source) => source.Contains('>') && HtmlTagRegex != null ? HtmlTagRegex.Replace(source, string.Empty) : source;

        public class Notification : Pool.IPooled
        {
            public BasePlayer player;
            public string messageBare;
            public string messageFull;
            public void Reset()
            {
                player = null;
                messageBare = null;
                messageFull = null;
            }
            public void EnterPool()
            {
                Reset();
            }
            public void LeavePool()
            {
                Reset();
            }
        }

        private Dictionary<ulong, List<Notification>> _notifications = new();

        private List<ulong> waiting = new();

        protected static void Puts(Exception ex)
        {
            Interface.Oxide.LogInfo("[{0}] {1}", Name, ex);
        }

        protected new static void Puts(string format, params object[] args)
        {
            if (!string.IsNullOrWhiteSpace(format))
            {
                Interface.Oxide.LogInfo("[{0}] {1}", Name, (args.Length != 0) ? string.Format(format, args) : format);
            }
        }

        private bool ProfilesExists()
        {
            try
            {
                Interface.Oxide.DataFileSystem.GetFiles(Path.Combine(Name, "Profiles"));
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void CreateDefaultFiles()
        {
            if (ProfilesExists())
            {
                return;
            }

            Interface.Oxide.DataFileSystem.GetDatafile(Path.Combine(Name, "Profiles", "_emptyfile"));

            foreach (var building in DefaultBuildingOptions)
            {
                string filename = Path.Combine("Name", "Profiles", building.Key);

                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(filename))
                {
                    SaveProfile(building.Key, building.Value);
                }
            }

            string lootFile = Path.Combine(Name, "Default_Loot");

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(lootFile))
            {
                var defaultLoot = DefaultLoot;
                defaultLoot.ForEach(ti => ti.InitializeArmorSlots());
                Interface.Oxide.DataFileSystem.WriteObject(lootFile, defaultLoot);
            }
        }

        protected void VerifyProfiles()
        {
            bool allowPVP = Buildings.Profiles.Values.Exists(profile => profile.Options.AllowPVP);
            bool allowPVE = Buildings.Profiles.Values.Exists(profile => !profile.Options.AllowPVP);

            if (config.Settings.Maintained.Enabled)
            {
                if (allowPVP && !config.Settings.Maintained.IncludePVP && !allowPVE)
                {
                    Puts("Invalid configuration detected: Maintained Events -> Include PVP Bases is set false, and all profiles have Allow PVP enabled. Therefore no bases can spawn for Maintained Events. The ideal configuration is for Include PVP Bases to be set true, and Convert PVP To PVE to be set true.");
                }

                if (allowPVE && !config.Settings.Maintained.IncludePVE && !allowPVP)
                {
                    Puts("Invalid configuration detected: Maintained Events -> Include PVE Bases is set false, and all profiles have Allow PVP disabled. Therefore no bases can spawn for Maintained Events. The ideal configuration is for Include PVE Bases to be set true, and Convert PVE To PVP to be set true.");
                }
            }

            if (config.Settings.Schedule.Enabled)
            {
                if (allowPVP && !config.Settings.Schedule.IncludePVP && !allowPVE)
                {
                    Puts("Invalid configuration detected: Scheduled Events -> Include PVP Bases is set false, and all profiles have Allow PVP enabled. Therefore no bases can spawn for Scheduled Events. The ideal configuration is for Include PVP Bases to be set true, and Convert PVP To PVE to be set true.");
                }

                if (allowPVE && !config.Settings.Schedule.IncludePVE && !allowPVP)
                {
                    Puts("Invalid configuration detected: Scheduled Events -> Include PVE Bases is set false, and all profiles have Allow PVP disabled. Therefore no bases can spawn for Scheduled Events. The ideal configuration is for Include PVE Bases to be set true, and Convert PVE To PVP to be set true.");
                }
            }
        }

        private IEnumerator ReloadProfiles(IPlayer user)
        {
            using var sb = DisposableBuilder.Get();
            yield return LoadProfiles(sb, user);
        }

        protected IEnumerator LoadProfiles(DisposableBuilder _sb, IPlayer user = null)
        {
            string folder = Path.Combine(Name, "Profiles");
            string[] files;

            try
            {
                files = Interface.Oxide.DataFileSystem.GetFiles(folder);
            }
            catch (UnauthorizedAccessException ex)
            {
                Puts("{0}", ex);
                yield break;
            }

            bool grey = false;

            foreach (string file in files)
            {
                yield return CoroutineEx.waitForFixedUpdate;

                string profileName = Oxide.Core.Utility.GetFileNameWithoutExtension(file);

                try
                {
                    if (file.Contains("_empty"))
                    {
                        continue;
                    }

                    var path = Path.Combine(folder, profileName);
                    var options = Interface.Oxide.DataFileSystem.ReadObject<BuildingOptions>(path);

                    if (options == null)
                    {
                        continue;
                    }

                    if (config.Settings.Management._Mounts != null)
                    {
                        options.Mounts = config.Settings.Management._Mounts;
                    }

                    if (options._EnforceDurability != null)
                    {
                        options.EnforceConditionLoss = options._EnforceDurability.Value;
                        options._EnforceDurability = null;
                    }

                    if (!config.Settings._BlacklistedCommands.IsNullOrEmpty())
                    {
                        options.BlacklistedPVECommands = config.Settings._BlacklistedCommands.ToList();
                        options.BlacklistedPVPCommands = config.Settings._BlacklistedCommands.ToList();
                    }

                    if (options.NPC.SpawnAmountMurderers == -9)
                    {
                        options.NPC.SpawnAmountMurderers = 2;
                    }

                    if (options.NPC.SpawnMinAmountMurderers == -9)
                    {
                        options.NPC.SpawnMinAmountMurderers = 2;
                    }

                    if (options.NPC.SpawnAmountScientists == -9)
                    {
                        options.NPC.SpawnAmountScientists = 2;
                    }

                    if (options.NPC.SpawnMinAmountScientists == -9)
                    {
                        options.NPC.SpawnMinAmountScientists = 2;
                    }

                    if (options.AdditionalBases == null)
                    {
                        options.AdditionalBases = new();
                    }

                    if (options.NPC.Accuracy == null)
                    {
                        options.NPC.Accuracy = new(25f);
                    }

                    if (options.Setup.DespawnLimit > despawnLimit)
                    {
                        despawnLimit = options.Setup.DespawnLimit;
                    }

                    if (options.ProtectionRadii.Max() <= 0f)
                    {
                        options.ProtectionRadii.Set(50f);
                    }

                    if (allowBuilding.HasValue)
                    {
                        options.AllowBuilding = allowBuilding.Value;
                    }

                    if (options.BuoyantBox)
                    {
                        BuoyantBox = true;
                    }

                    if (config.Settings.Management._Biomes != null && options.Biomes == null)
                    {
                        options.Biomes = config.Settings.Management._Biomes;
                    }

                    if (options.Rewards.XPerience == -125)
                    {
                        options.Rewards.XPerience = options.Rewards.SkillTree;
                    }

                    if (options.Rewards.XLevels == -125)
                    {
                        options.Rewards.XLevels = options.Rewards.SkillTree;
                    }

                    options.Biomes ??= new();

                    grey |= options.DespawnGreyBoxBags;
                    options.Siege.Disabled = !options.Siege.Any;
                    Buildings.Profiles[profileName] = new(this, options, profileName);
                }
                catch (Exception ex)
                {
                    Puts("\n\n\n{0}\n\n\n{1}", file, ex);
                }
            }

            bool saveConfig = false;

            if (config.Settings.Management._Mounts != null)
            {
                config.Settings.Management._Mounts = null;
                saveConfig = true;
            }

            if (config.Settings._BlacklistedCommands != null)
            {
                config.Settings._BlacklistedCommands = null;
                saveConfig = true;
            }

            if (config.Settings.Management.DropLoot.SET != null)
            {
                config.Settings.Management.DropLoot.DespawnGreyWeaponBags = grey;
                config.Settings.Management.DropLoot.SET = null;
                saveConfig = true;
            }

            if (config.Settings.Management._Biomes != null)
            {
                config.Settings.Management._Biomes = null;
                saveConfig = true;
            }

            if (saveConfig)
            {
                SaveConfig();
            }

            foreach (var (key, profile) in Buildings.Profiles)
            {
                if (!AnyCopyPasteFileExists && (FileExists(key) || profile.Options.AdditionalBases.Keys.Exists(FileExists)))
                {
                    AnyCopyPasteFileExists = true;
                }
                SaveProfile(key, profile.Options);
                yield return CoroutineEx.waitForFixedUpdate;
            }

            VerifyProfiles();
            LoadImportedSkins();

            if (user != null)
            {
                yield return LoadBaseTables(_sb, user);

                Message(user, "Initialized base loot tables and profiles.");
            }
        }

        private bool BuoyantBox;

        private IEnumerator ReloadTables(IPlayer user)
        {
            using var sb = DisposableBuilder.Get();
            yield return LoadTables(sb, user);
            yield return LoadBaseTables(sb, user);
        }

        private void LoadImportedSkins()
        {
            string skinBoxFilename = Path.Combine(Name, "ImportedWorkshopSkins");
            try
            {
                ImportedWorkshopSkins = Interface.Oxide.DataFileSystem.ReadObject<SkinSettingsImportedWorkshop>(skinBoxFilename);
            }
            catch (JsonException ex)
            {
                Puts(ex);
            }
            ImportedWorkshopSkins ??= new();
            ImportedWorkshopSkins.SkinList ??= new();
            string skinsFilename = Path.Combine(Name, "SkinsPlugin");
            try
            {
                skinsPlugin = Interface.Oxide.DataFileSystem.ReadObject<SkinsPlugin>(skinsFilename);
            }
            catch (Exception ex)
            {
                Puts(ex);
            }
            skinsPlugin ??= new();
            skinsPlugin.Skins ??= new();
        }

        public class SkinsPlugin
        {
            [JsonProperty(PropertyName = "Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SkinItem> Skins = new()
            {
                new() { Shortname = "jacket.snow", Skins = new() { 785868744, 939797621 } },
                new() { Shortname = "knife.bone", Skins = new() { 1228176194, 2038837066 } }
            };
        }

        public class SkinItem
        {
            [JsonProperty(PropertyName = "Item Shortname")]
            public string Shortname = "shortname";

            [JsonProperty(PropertyName = "Permission")]
            public string Permission = "";

            [JsonProperty(PropertyName = "Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Skins = new() { 0 };
        }

        protected IEnumerator LoadTables(DisposableBuilder _sb, IPlayer user = null)
        {
            _sb.AppendLine("-");

            var defaultLootTable = Buildings.DifficultyLootLists["Random"] = GetTable(Path.Combine(Name, "Default_Loot"));
            LoadTable(_sb, Path.Combine(Name, "Default_Loot"), defaultLootTable);
            yield return CoroutineEx.waitForFixedUpdate;

            var normalLootTable = Buildings.DifficultyLootLists["Normal"] = GetTable(Path.Combine(Name, "Difficulty_Loot", "Normal"));
            LoadTable(_sb, Path.Combine(Name, "Difficulty_Loot", "Normal"), normalLootTable);
            yield return CoroutineEx.waitForFixedUpdate;

            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                string file = Path.Combine(Name, "Weekday_Loot", day.ToString());
                var lootTable = Buildings.WeekdayLootLists[day] = GetTable(file);
                LoadTable(_sb, file, lootTable);
                yield return CoroutineEx.waitForFixedUpdate;
            }

            yield return LoadBaseTables(_sb, user);
        }

        public List<LootItem> BaseLootList = new();
        protected IEnumerator LoadBaseTables(DisposableBuilder _sb, IPlayer user = null)
        {
            foreach (var entry in Buildings.Profiles)
            {
                string file = Path.Combine(Name, "Base_Loot", entry.Key);
                try
                {
                    LoadTable(_sb, file, BaseLootList = GetTable(file));
                }
                catch (Exception ex)
                {
                    Puts("Error in file: {0} - {1}", file, ex);
                }
                if (BaseLootList.Count > 0) break;
                yield return CoroutineEx.waitForFixedUpdate;
            }

            if (user == null || user.IsServer) Interface.Oxide.LogInfo("{0}", _sb.ToString());
            else user.Reply(_sb.ToString());
        }

        private void LoadTable(DisposableBuilder _sb, string file, List<LootItem> lootList)
        {
            if (lootList.Count == 0)
            {
                return;
            }

            bool zero = lootList.All(ti => ti.probability == 0f);

            bool stack = lootList.All(ti => ti.stacksize == 0);

            lootList.ForEach(ti =>
            {
                if (zero) ti.probability = 1f;
                if (stack) ti.stacksize = -1;
                ti.InitializeArmorSlots();
            });

            if (!InstallationError)
            {
                Interface.Oxide.DataFileSystem.WriteObject(file, lootList);
            }

            lootList.ToList().ForEach(ti =>
            {
                if (ti.amount == 0 || string.IsNullOrEmpty(ti.shortname) || BlacklistedItems.Contains(ti.shortname))
                {
                    lootList.Remove(ti);
                    return;
                }
                if (ti.amount < ti.amountMin)
                {
                    ti.amount = ti.amountMin;
                }
                if (ti.shortname == "chocholate")
                {
                    ti.shortname = "chocolate";
                }
            });

            if (lootList.Count > 0)
            {
                _sb.AppendLine($"Loaded {lootList.Count} items from {file}");
            }
        }

        private List<LootItem> GetTable(string file)
        {
            var lootList = new List<LootItem>();

            try
            {
                lootList = Interface.Oxide.DataFileSystem.ReadObject<List<LootItem>>(file);
            }
            catch (JsonReaderException ex)
            {
                Puts("Json error in loot table file: {0}\n\n\nUse a json validator: www.jsonlint.com\n\n\n{1}", file, ex);
            }

            if (lootList == null)
            {
                return new();
            }

            return lootList.Where(ti => ti != null && !string.IsNullOrEmpty(ti.shortname));
        }

        private Configuration config;

        private static Dictionary<string, List<ulong>> DefaultImportedSkins
        {
            get
            {
                return new()
                {
                    ["jacket.snow"] = new() { 785868744, 939797621 },
                    ["knife.bone"] = new() { 1228176194, 2038837066 }
                };
            }
        }

        private static List<PasteOption> DefaultPasteOptions
        {
            get
            {
                return new()
                {
                    new() { Key = "stability", Value = "false" },
                    new() { Key = "autoheight", Value = "false" },
                    new() { Key = "height", Value = "1.0" },
                };
            }
        }

        private static Dictionary<string, BuildingOptions> DefaultBuildingOptions
        {
            get
            {
                return new()
                {
                    ["RaidBases"] = new("RaidBase1", "RaidBase2", "RaidBase3", "RaidBase4", "RaidBase5")
                    {
                        NPC = new(25.0)
                    }
                };
            }
        }

        private static List<LootItem> DefaultLoot
        {
            get
            {
                return new()
                {
                    new("ammo.pistol", 40, 40),
                    new("ammo.pistol.fire", 40, 40),
                    new("ammo.pistol.hv", 40, 40),
                    new("ammo.rifle", 60, 60),
                    new("ammo.rifle.explosive", 60, 60),
                    new("ammo.rifle.hv", 60, 60),
                    new("ammo.rifle.incendiary", 60, 60),
                    new("ammo.shotgun", 24, 24),
                    new("ammo.shotgun.slug", 40, 40),
                    new("surveycharge", 20, 20),
                    new("bucket.helmet", 1, 1),
                    new("cctv.camera", 1, 1),
                    new("coffeecan.helmet", 1, 1),
                    new("explosive.timed", 1, 1),
                    new("metal.facemask", 1, 1),
                    new("metal.plate.torso", 1, 1),
                    new("mining.quarry", 1, 1),
                    new("pistol.m92", 1, 1),
                    new("rifle.ak", 1, 1),
                    new("rifle.bolt", 1, 1),
                    new("rifle.lr300", 1, 1),
                    new("shotgun.pump", 1, 1),
                    new("shotgun.spas12", 1, 1),
                    new("smg.2", 1, 1),
                    new("smg.mp5", 1, 1),
                    new("smg.thompson", 1, 1),
                    new("supply.signal", 1, 1),
                    new("targeting.computer", 1, 1),
                    new("metal.refined", 150, 150),
                    new("stones", 7500, 15000),
                    new("sulfur", 2500, 7500),
                    new("metal.fragments", 2500, 7500),
                    new("charcoal", 1000, 5000),
                    new("gunpowder", 1000, 3500),
                    new("scrap", 100, 150)
                };
            }
        }

        public class Color1Settings
        {
            [JsonProperty(PropertyName = "Normal")]
            public string Normal = "000000";

            public string Get()
            {
                return Normal.StartsWith("#") ? Normal : $"#{Normal}";
            }
        }

        public class Color2Settings
        {
            [JsonProperty(PropertyName = "Normal")]
            public string Normal = "00FF00";

            public string Get()
            {
                return Normal.StartsWith("#") ? Normal : $"#{Normal}";
            }
        }

        public class ManagementMountableSettings
        {
            [JsonProperty(PropertyName = "All Controlled Mounts")]
            public bool ControlledMounts;

            [JsonProperty(PropertyName = "All Other Mounts")]
            public bool Other;

            [JsonProperty(PropertyName = "Attack Helicopters")]
            public bool AttackHelicopters;

            [JsonProperty(PropertyName = "Bikes")]
            public bool Bikes;

            [JsonProperty(PropertyName = "Boats")]
            public bool Boats;

            [JsonProperty(PropertyName = "Campers")]
            public bool Campers = true;

            [JsonProperty(PropertyName = "Cars (Basic)")]
            public bool BasicCars;

            [JsonProperty(PropertyName = "Cars (Modular)")]
            public bool ModularCars;

            [JsonProperty(PropertyName = "Chinook")]
            public bool CH47;

            [JsonProperty(PropertyName = "Drones")]
            public bool Drones;

            [JsonProperty(PropertyName = "RFExplosives Above Dome (experimental)")]
            public bool RFExplosivesAboveDome;

            [JsonProperty(PropertyName = "Flying Carpet")]
            public bool FlyingCarpet;

            [JsonProperty(PropertyName = "Horses")]
            public bool Hitchable;

            [JsonProperty(PropertyName = "HotAirBalloon")]
            public bool HotAirBalloon = true;

            [JsonProperty(PropertyName = "Invisible Chair")]
            public bool Invisible = true;

            [JsonProperty(PropertyName = "Jetpacks")]
            public bool Jetpacks = true;

            [JsonProperty(PropertyName = "MiniCopters")]
            public bool MiniCopters;

            [JsonProperty(PropertyName = "Parachutes")]
            public bool Parachutes;

            [JsonProperty(PropertyName = "Pianos")]
            public bool Pianos = true;

            [JsonProperty(PropertyName = "Scrap Transport Helicopters")]
            public bool Scrap;

            [JsonProperty(PropertyName = "Siege")]
            public bool Siege;

            [JsonProperty(PropertyName = "Snowmobiles")]
            public bool Snowmobile;

            [JsonProperty(PropertyName = "Tugboats")]
            public bool Tugboats;
        }

        public class BuildingOptionsSetupSettings
        {
            [JsonProperty(PropertyName = "Amount Of Entities To Spawn Per Batch")]
            public int SpawnLimit = 1;

            [JsonProperty(PropertyName = "Amount Of Entities To Despawn Per Batch")]
            public int DespawnLimit = 10;

            [JsonProperty(PropertyName = "Height Adjustment Applied To This Paste")]
            public float PasteHeightAdjustment;

            [System.ComponentModel.DefaultValue(-1f)]
            [JsonProperty(PropertyName = "Force All Bases To Spawn At Height Level (0 = Water)", DefaultValueHandling = DefaultValueHandling.Include)]
            public float ForcedHeightValue = -1f;

            [JsonProperty(PropertyName = "Enabled (Forced Height Level)")]
            public bool EnableForcedHeight;

            internal float ForcedHeight => EnableForcedHeight ? ForcedHeightValue : -1f;

            [JsonProperty(PropertyName = "Foundations Immune To Damage When Forced Height Is Applied")]
            public bool FoundationsImmuneForcedHeight;

            [JsonProperty(PropertyName = "Foundations Immune To Damage")]
            public bool FoundationsImmune;

            [JsonProperty(PropertyName = "Kill These Prefabs After Paste", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedPrefabs = new();
        }

        public class ManagementPlayerAmountsSettings
        {
            [JsonProperty(PropertyName = "Maintained Events")]
            public int Maintained;

            [JsonProperty(PropertyName = "Manual Events")]
            public int Manual;

            [JsonProperty(PropertyName = "Scheduled Events")]
            public int Scheduled;

            [JsonProperty(PropertyName = "Bypass For PVP Bases")]
            public bool BypassPVP;

            public int Get(RaidableType type)
            {
                switch (type)
                {
                    case RaidableType.Maintained: return Maintained;
                    case RaidableType.Scheduled: return Scheduled;
                    default: return Manual;
                }
            }
        }

        public class ManagementDropSettings
        {
            [JsonProperty(PropertyName = "SET", NullValueHandling = NullValueHandling.Ignore)]
            public bool? SET = null;

            [JsonProperty(PropertyName = "Despawn These Dropped Loot Bags When Base Despawns")]
            public bool DespawnGreyWeaponBags;

            [JsonProperty(PropertyName = "Auto Turrets")]
            public bool AUTOTURRET;

            [JsonProperty(PropertyName = "Flame Turret")]
            public bool FLAMETURRET;

            [JsonProperty(PropertyName = "Fog Machine")]
            public bool FOGMACHINE;

            [JsonProperty(PropertyName = "Gun Trap")]
            public bool GUNTRAP;

            [JsonProperty(PropertyName = "SAM Site")]
            public bool SAMSITE;

            public bool CanDespawnGreyWeaponBag(BaseEntity entity) => DespawnGreyWeaponBags && entity.OwnerID == 0 && (entity is AutoTurret or FlameTurret or FogMachine or GunTrap or SamSite);

            public bool Get(BaseEntity entity) => entity switch
            {
                AutoTurret _ => AUTOTURRET,
                FlameTurret _ => FLAMETURRET,
                FogMachine _ => FOGMACHINE,
                GunTrap _ => GUNTRAP,
                SamSite _ => SAMSITE,
                Fridge => true,
                _ => false
            };
        }

        public class ManagementSettingsLocations
        {
            [JsonProperty(PropertyName = "position")]
            public string _position;
            public float radius;
            public ManagementSettingsLocations() { }
            public ManagementSettingsLocations(Vector3 position, float radius)
            {
                this._position = position.ToString();
                this.radius = radius;
            }
            internal Vector3 position { get { try { return _position.ToVector3(); } catch { Puts("Block Spawns At Positions: {0} is an invalid position in config file.", _position); return Vector3.zero; } } }
        }

        public class ManagementBiomeSettings
        {
            [JsonProperty(PropertyName = "Arctic")]
            public bool Arctic = true;

            [JsonProperty(PropertyName = "Arid")]
            public bool Arid = true;

            [JsonProperty(PropertyName = "Temperate")]
            public bool Temperate = true;

            [JsonProperty(PropertyName = "Tundra")]
            public bool Tundra = true;

            [JsonProperty(PropertyName = "Jungle")]
            public bool Jungle = true;

            public bool IsBiomeEnabled(int? t, Vector3 a, out TerrainBiome.Enum biome)
            {
                if (!t.HasValue)
                {
                    biome = (TerrainBiome.Enum)0;
                    return true;
                }
                biome = (TerrainBiome.Enum)t.Value;
                return biome switch
                {
                    TerrainBiome.Enum.Arctic => Arctic,
                    TerrainBiome.Enum.Arid => Arid,
                    TerrainBiome.Enum.Temperate => Temperate,
                    TerrainBiome.Enum.Tundra => Tundra,
                    TerrainBiome.Enum.Jungle => Jungle,
                    _ => true
                };
            }
        }

        public class ManagementSettings
        {
            [JsonProperty(PropertyName = "Grids To Block Spawns At", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedGrids = new();

            [JsonProperty(PropertyName = "Block Spawns At Positions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ManagementSettingsLocations> BlockedPositions = new() { new(Vector3.zero, 200f) };

            [JsonProperty(PropertyName = "Blocked Monument Markers (* = everything)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedMonumentMarkers = new();

            [JsonProperty(PropertyName = "Additional Map Prefabs To Block Spawns At", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, float> BlockedPrefabs = new() { ["test_prefab"] = 150f, ["test_prefab_2"] = 125.25f };

            [JsonProperty(PropertyName = "Eject Mounts", NullValueHandling = NullValueHandling.Ignore)]
            public ManagementMountableSettings _Mounts = null;

            [JsonProperty(PropertyName = "Max Amount Of Players Allowed To Enter (0 = infinite, -1 = none)")]
            public ManagementPlayerAmountsSettings Players = new();

            [JsonProperty(PropertyName = "Additional Containers To Include As Boxes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Inherit = new() { "locker" };

            [JsonProperty(PropertyName = "Difficulty Colors (Border)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Color1Settings Colors1 = new();

            [JsonProperty(PropertyName = "Difficulty Colors (Inner)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Color2Settings Colors2 = new();

            [JsonProperty(PropertyName = "Entities Allowed To Drop Loot")]
            public ManagementDropSettings DropLoot = new();

            [JsonProperty(PropertyName = "Additional Blocked Colliders", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AdditionalBlockedColliders = new() { "cube" };

            [JsonProperty(PropertyName = "Allow Teleport")]
            public bool AllowTeleport;

            [JsonProperty(PropertyName = "Allow Teleport Ignores Respawning")]
            public bool AllowRespawn;

            [JsonProperty(PropertyName = "Allow Cupboard Loot To Drop")]
            public bool AllowCupboardLoot = true;

            [JsonProperty(PropertyName = "Allow Players To Build", NullValueHandling = NullValueHandling.Ignore)]
            public bool? _AllowBuilding = null;

            [JsonProperty(PropertyName = "Allow Players To Use Ladders")]
            public bool AllowLadders = true;

            [JsonProperty(PropertyName = "Allow Players To Upgrade Event Buildings")]
            public bool AllowUpgrade;

            [JsonProperty(PropertyName = "Allow Player Bags To Be Lootable At PVP Bases")]
            public bool PlayersLootableInPVP = true;

            [JsonProperty(PropertyName = "Allow Player Bags To Be Lootable At PVE Bases")]
            public bool PlayersLootableInPVE = true;

            [JsonProperty(PropertyName = "Allow Players To Loot Traps")]
            public bool LootableTraps;

            [JsonProperty(PropertyName = "Allow Npcs To Target Other Npcs")]
            public bool TargetNpcs;

            [JsonProperty(PropertyName = "Allow Raid Bases Inland")]
            public bool AllowInland = true;

            [JsonProperty(PropertyName = "Allow Raid Bases On Beaches")]
            public bool AllowOnBeach = true;

            [JsonProperty(PropertyName = "Allow Raid Bases On Ice Sheets")]
            public bool AllowOnIceSheets;

            [JsonProperty(PropertyName = "Allow Raid Bases On Roads")]
            public bool AllowOnRoads = true;

            [JsonProperty(PropertyName = "Allow Raid Bases On Rivers")]
            public bool AllowOnRivers = true;

            [JsonProperty(PropertyName = "Allow Raid Bases On Railroads")]
            public bool AllowOnRailroads;

            [JsonProperty(PropertyName = "Allow Raid Bases On Building Topology")]
            public bool AllowOnBuildingTopology = true;

            [JsonProperty(PropertyName = "Allow Raid Bases On Monument Topology")]
            public bool AllowOnMonumentTopology;

            [JsonProperty(PropertyName = "Allow Raid Bases In Biomes", NullValueHandling = NullValueHandling.Ignore)]
            public ManagementBiomeSettings _Biomes = null;

            [JsonProperty(PropertyName = "Amount Of Spawn Position Checks Per Frame (ADVANCED USERS ONLY)")]
            public int SpawnChecks = 25;

            [JsonProperty(PropertyName = "Allow Vending Machines To Broadcast")]
            public bool AllowBroadcasting;

            [JsonProperty(PropertyName = "Backpacks Can Be Opened At PVE Bases")]
            public bool BackpacksOpenPVE = true;

            [JsonProperty(PropertyName = "Backpacks Can Be Opened At PVP Bases")]
            public bool BackpacksOpenPVP = true;

            [JsonProperty(PropertyName = "Rust Backpacks Drop At PVE Bases")]
            public bool RustBackpacksPVE;

            [JsonProperty(PropertyName = "Rust Backpacks Drop At PVP Bases")]
            public bool RustBackpacksPVP = true;

            [JsonProperty(PropertyName = "Backpacks Drop At PVE Bases")]
            public bool BackpacksPVE;

            [JsonProperty(PropertyName = "Backpacks Drop At PVP Bases")]
            public bool BackpacksPVP;

            [JsonProperty(PropertyName = "Block Custom Loot Plugin")]
            public bool BlockCustomLootNPC;

            [JsonProperty(PropertyName = "Block AlphaLoot Plugin")]
            public bool BlockAlphaLoot;

            [JsonProperty(PropertyName = "Block BetterLoot Plugin")]
            public bool BlockBetterLoot = true;

            [JsonProperty(PropertyName = "Block Npc Kits Plugin")]
            public bool BlockNpcKits;

            [JsonProperty(PropertyName = "Block Helicopter Damage To Bases")]
            public bool BlockHelicopterDamage;

            [JsonProperty(PropertyName = "Block Mounted Damage To Bases And Players")]
            public bool BlockMounts;

            [JsonProperty(PropertyName = "Block Damage From Siege Weapons To Bases")]
            public bool BlockSiegeMounts;

            [JsonProperty(PropertyName = "Block Mini Collision Damage")]
            public bool MiniCollision;

            [JsonProperty(PropertyName = "Block DoubleJump Plugin")]
            public bool NoDoubleJump = true;

            [JsonProperty(PropertyName = "Block RevivePlayer Plugin For PVP Bases")]
            public bool BlockRevivePVP { get; set; }

            [JsonProperty(PropertyName = "Block RevivePlayer Plugin For PVE Bases")]
            public bool BlockRevivePVE { get; set; }

            [JsonProperty(PropertyName = "Block RestoreUponDeath Plugin For PVP Bases")]
            public bool BlockRestorePVP;

            [JsonProperty(PropertyName = "Block RestoreUponDeath Plugin For PVE Bases")]
            public bool BlockRestorePVE;

            [JsonProperty(PropertyName = "Block LifeSupport Plugin")]
            public bool NoLifeSupport = true;

            [JsonProperty(PropertyName = "Block Rewards During Server Restart")]
            public bool Restart;

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVE Bases")]
            public bool BypassUseOwnersForPVE;

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVP Bases")]
            public bool BypassUseOwnersForPVP = true;

            [JsonProperty(PropertyName = "Despawn Spawned Mounts")]
            public bool DespawnMounts = true;

            [JsonProperty(PropertyName = "Do Not Destroy Player Built Deployables")]
            public bool KeepDeployables = true;

            [JsonProperty(PropertyName = "Do Not Destroy Player Built Structures")]
            public bool KeepStructures = true;

            [JsonProperty(PropertyName = "Divide Rewards Among All Raiders")]
            public bool DivideRewards = true;

            [JsonProperty(PropertyName = "Draw Corpse Time (Seconds)")]
            public float DrawTime = 300f;

            [JsonProperty(PropertyName = "Destroy Boxes Clipped Too Far Into Terrain")]
            public bool ClippedBoxes = true;

            [JsonProperty(PropertyName = "Destroy Turrets Clipped Too Far Into Terrain")]
            public bool ClippedTurrets = true;

            [JsonProperty(PropertyName = "Eject Sleepers Before Spawning Base")]
            public bool EjectSleepers = true;

            [JsonProperty(PropertyName = "Eject Scavengers When Raid Is Completed")]
            public bool EjectScavengers = true;

            [JsonProperty(PropertyName = "Eject Mountables Before Spawning A Base")]
            public bool EjectMountables;

            [JsonProperty(PropertyName = "Kill Deployables Before Spawning A Base")]
            public bool KillDeployables;

            [JsonProperty(PropertyName = "Eject Deployables Before Spawning A Base")]
            public bool EjectDeployables;

            [JsonProperty(PropertyName = "Extra Distance To Spawn From Monuments")]
            public float MonumentDistance = 25f;

            [JsonProperty(PropertyName = "Move Cookables Into Ovens")]
            public bool Cook = true;

            [JsonProperty(PropertyName = "Move Food Into BBQ Or Fridge")]
            public bool Food = true;

            [JsonProperty(PropertyName = "Blacklist For BBQ And Fridge", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<string> Foods = new() { "syrup", "pancakes" };

            [JsonProperty(PropertyName = "Move Weapons Onto Weapon Racks")]
            public bool Racks = true;

            [JsonProperty(PropertyName = "Divide Weapon Rack Loot When Enabled")]
            public bool DivideRackLoot = true;

            [JsonProperty(PropertyName = "Move Resources Into Tool Cupboard")]
            public bool Cupboard = true;

            [JsonProperty(PropertyName = "Move Items Into Lockers")]
            public bool Lockers = true;

            [JsonProperty(PropertyName = "Divide Locker Loot When Enabled")]
            public bool DivideLockerLoot = true;

            [JsonProperty(PropertyName = "Lock Treasure To First Attacker")]
            public bool UseOwners = true;

            [JsonProperty(PropertyName = "Lock Treasure Max Inactive Time (Minutes)")]
            public float LockTime = 20f;

            [JsonProperty(PropertyName = "Lock Players To Raid Base After Entering Zone")]
            public bool LockToRaidOnEnter;

            [JsonProperty(PropertyName = "Only Award First Attacker and Allies")]
            public bool OnlyAwardAllies;

            [JsonProperty(PropertyName = "Only Award Owner Of Raid")]
            public bool OnlyAwardOwner;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Looting (min: 1)")]
            public int DespawnMinutes = 15;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Looting Resets When Damaged")]
            public bool DespawnMinutesReset;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Inactive (0 = disabled)")]
            public int DespawnMinutesInactive = 45;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Inactive Resets When Damaged")]
            public bool DespawnMinutesInactiveReset = true;

            [JsonProperty(PropertyName = "Mounts Can Take Damage From Players")]
            public bool MountDamageFromPlayers;

            [JsonProperty(PropertyName = "Player Cupboard Detection Radius")]
            public float CupboardDetectionRadius = 125f;

            [JsonProperty(PropertyName = "PVP Delay Triggers When Entity Destroyed From Outside Zone")]
            public bool PVPDelayTrigger;

            [JsonProperty(PropertyName = "Players With PVP Delay Can Damage Anything Inside Zone")]
            public bool PVPDelayDamageInside;

            [JsonProperty(PropertyName = "Players With PVP Delay Can Damage Other Players With PVP Delay Anywhere")]
            public bool PVPDelayAnywhere;

            [JsonProperty(PropertyName = "PVP Delay Between Zone Hopping")]
            public float PVPDelay = 10f;

            [JsonProperty(PropertyName = "PVP Delay Between Zone Hopping Persists After Despawn")]
            public bool PVPDelayPersists;

            [JsonProperty(PropertyName = "Prevent Fire From Spreading")]
            public bool PreventFireFromSpreading = true;

            [JsonProperty(PropertyName = "Prevent Players From Hogging Raids")]
            public bool PreventHogging = true;

            [JsonProperty(PropertyName = "Block Clans From Owning More Than One Raid")]
            public bool BlockClans;

            [JsonProperty(PropertyName = "Block Friends From Owning More Than One Raid")]
            public bool BlockFriends;

            [JsonProperty(PropertyName = "Block Teams From Owning More Than One Raid")]
            public bool BlockTeams;

            [JsonProperty(PropertyName = "Block Players From Joining A Clan/Team To Exploit Restrictions")]
            public bool AllyExploit;

            [JsonProperty(PropertyName = "Prevent Fall Damage When Base Despawns")]
            public bool PreventFallDamage;

            [JsonProperty(PropertyName = "Require Cupboard To Be Looted Before Despawning", NullValueHandling = NullValueHandling.Ignore)]
            public bool? _RequireCupboardLooted = null;

            [JsonProperty(PropertyName = "Require Cupboard To Be Looted Before Completion")]
            public bool RequireCupboardLooted;

            [JsonProperty(PropertyName = "Destroying The Cupboard Completes The Raid")]
            public bool EndWhenCupboardIsDestroyed;

            [JsonProperty(PropertyName = "Require All Bases To Spawn Before Respawning An Existing Base")]
            public bool RequireAllSpawned = true;

            [JsonProperty(PropertyName = "Turn Lights On At Night")]
            public bool Lights = true;

            [JsonProperty(PropertyName = "Turn Lights On Indefinitely")]
            public bool AlwaysLights;

            [JsonProperty(PropertyName = "Ignore List For Turn Lights On", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> IgnoredLights = new() { "laserlight", "weaponrack", "lightswitch", "soundlight", "xmas" };

            [JsonProperty(PropertyName = "Traps And Turrets Ignore Users Using NOCLIP")]
            public bool IgnoreFlying;

            [JsonProperty(PropertyName = "Use Random Codes On Code Locks")]
            public bool RandomCodes = true;

            [JsonProperty(PropertyName = "Wait To Start Despawn Timer When Base Takes Damage From Player")]
            public bool Engaged;

            [JsonProperty(PropertyName = "Wait To Start Despawn Timer Until Npc Is Killed By Player")]
            public bool EngagedNpc;

            [JsonProperty(PropertyName = "Maximum Water Depth For All Npcs")]
            public float WaterDepth = 3f;

            public bool IsBlocking() => BlockClans || BlockFriends || BlockTeams;
        }

        public class ProfileDespawnOptions
        {
            [JsonProperty(PropertyName = "Override Global Config With These Options For This Profile")]
            public bool OverrideConfig = false;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Looting (min: 1)")]
            public int DespawnMinutes = 15;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Looting Resets When Damaged")]
            public bool DespawnMinutesReset;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Inactive (0 = disabled)")]
            public int DespawnMinutesInactive = 45;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Inactive Resets When Damaged")]
            public bool DespawnMinutesInactiveReset = true;

            [JsonProperty(PropertyName = "Wait To Start Despawn Timer When Base Takes Damage From Player")]
            public bool Engaged;

            [JsonProperty(PropertyName = "Wait To Start Despawn Timer Until Npc Is Killed By Player")]
            public bool EngagedNpc;
        }

        public class PluginSettingsMapMarkers
        {
            [JsonProperty(PropertyName = "Marker Name")]
            public string MarkerName = "Raidable Base Event";

            [JsonProperty(PropertyName = "Radius")]
            public float Radius = 0.25f;

            [JsonProperty(PropertyName = "Radius (Map Size 3600 Or Less)")]
            public float SubRadius = 0.25f;

            [JsonProperty(PropertyName = "Use Vending Map Marker")]
            public bool UseVendingMarker = true;

            [JsonProperty(PropertyName = "Show Owners Name on Map Marker")]
            public bool ShowOwnersName = true;

            [JsonProperty(PropertyName = "Use Explosion Map Marker")]
            public bool UseExplosionMarker;

            [JsonProperty(PropertyName = "Create Markers For Maintained Events")]
            public bool Maintained = true;

            [JsonProperty(PropertyName = "Create Markers For Scheduled Events")]
            public bool Scheduled = true;

            [JsonProperty(PropertyName = "Create Markers For Manual Events")]
            public bool Manual = true;
        }

        public class ExperimentalSettings
        {
            [JsonProperty(PropertyName = "Apply Custom Auto Height To", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AutoHeight = new();

            [JsonProperty(PropertyName = "Bunker Bases Or Profiles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Bunker = new();

            [JsonProperty(PropertyName = "Multi Foundation Bases Or Profiles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MultiFoundation = new();

            public enum Type { AutoHeight, Bunker, MultiFoundation };

            public bool Contains(Type type, RandomBase rb)
            {
                switch (type)
                {
                    case Type.AutoHeight: return AutoHeight.Contains("*") || AutoHeight.Contains(rb.BaseName) || AutoHeight.Contains(rb.Profile.ProfileName);
                    case Type.Bunker: return Bunker.Contains("*") || Bunker.Contains(rb.BaseName) || Bunker.Contains(rb.Profile.ProfileName);
                    case Type.MultiFoundation: return MultiFoundation.Contains("*") || MultiFoundation.Contains(rb.BaseName) || MultiFoundation.Contains(rb.Profile.ProfileName);
                    default: return false;
                }
            }
        }

        public class WipeSettings
        {
            [JsonProperty(PropertyName = "Wipe triggers when Rust protocol changes")]
            public bool Protocol = true;

            [JsonProperty(PropertyName = "Wipe triggers on detection of map wipe")]
            public bool Map = true;

            [JsonProperty(PropertyName = "Wipe includes current data")]
            public bool Current = true;

            [JsonProperty(PropertyName = "Wipe includes lifetime data (NOT recommended!)")]
            public bool Lifetime;

            [JsonProperty(PropertyName = "Manual wipe (command: rb wipe) revokes below permissions and groups from players")]
            public bool RemoveFromList = true;

            [JsonProperty(PropertyName = "Permissions and groups to revoke on wipe (command: rb revokepg)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Remove = new();
        }

        public class PluginSettings
        {
            [JsonProperty(PropertyName = "Wipe Management (/data/RaidableBases.json)")]
            public WipeSettings Wipe = new();

            [JsonProperty(PropertyName = "Experimental [* = everything]")]
            public ExperimentalSettings Experimental = new();

            [JsonProperty(PropertyName = "Raid Management")]
            public ManagementSettings Management = new();

            [JsonProperty(PropertyName = "Map Markers")]
            public PluginSettingsMapMarkers Markers = new();

            [JsonProperty(PropertyName = "Maintained Events")]
            public RaidableBaseSettingsMaintained Maintained = new();

            [JsonProperty(PropertyName = "Manual Events")]
            public RaidableBaseSettingsManual Manual = new();

            [JsonProperty(PropertyName = "Scheduled Events")]
            public RaidableBaseSettingsScheduled Schedule = new();

            [JsonProperty(PropertyName = "Allowed Zone Manager Zones", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedZones = new() { "pvp", "99999999" };

            [JsonProperty(PropertyName = "Use Grid Locations In Allowed Zone Manager Zones Only")]
            public bool UseZoneManagerOnly;

            [JsonProperty(PropertyName = "Extended Distance To Spawn Away From Zone Manager Zones")]
            public float ZoneDistance = 25f;

            [JsonProperty(PropertyName = "Blacklisted Commands", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> _BlacklistedCommands = null;

            [JsonProperty(PropertyName = "Automatically Teleport Admins To Their Map Marker Positions")]
            public bool TeleportMarker = true;

            [JsonProperty(PropertyName = "Automatically Destroy Markers That Admins Teleport To")]
            public bool DestroyMarker;

            [JsonProperty(PropertyName = "Block Archery Plugin At Events")]
            public bool NoArchery;

            [JsonProperty(PropertyName = "Block Wizardry Plugin At Events")]
            public bool NoWizardry;

            [JsonProperty(PropertyName = "Block Weapons From Use", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedWeapons = new() { "toolgun" };

            [JsonProperty(PropertyName = "Chat Steam64ID")]
            public ulong ChatID = 76561199564392767;

            [JsonProperty(PropertyName = "Expansion Mode (Dangerous Treasures)")]
            public bool ExpansionMode;

            [JsonProperty(PropertyName = "Remove Admins From Raiders List")]
            public bool RemoveAdminRaiders;

            [JsonProperty(PropertyName = "Show X Z Coordinates")]
            public bool ShowXZ;

            [JsonProperty(PropertyName = "Show Grid Coordinates")]
            public bool ShowGrid = true;

            [JsonProperty(PropertyName = "Show Direction To Coordinates")]
            public bool ShowDir;

            [JsonProperty(PropertyName = "Event Command")]
            public string EventCommand = "rbe";

            [JsonProperty(PropertyName = "Hunter Command")]
            public string HunterCommand = "rb";

            [JsonProperty(PropertyName = "Server Console Command")]
            public string ConsoleCommand = "rbevent";
        }

        public class EventMessageRewardSettings
        {
            [JsonProperty(PropertyName = "Flying")]
            public bool Flying;

            [JsonProperty(PropertyName = "Vanished")]
            public bool Vanished;

            [JsonProperty(PropertyName = "Inactive")]
            public bool Inactive = true;

            [JsonProperty(PropertyName = "Not An Ally")]
            public bool NotAlly = true;

            [JsonProperty(PropertyName = "Not The Owner")]
            public bool NotOwner = true;

            [JsonProperty(PropertyName = "Not A Participant")]
            public bool NotParticipant = true;

            [JsonProperty(PropertyName = "Remove Admins From Raiders List")]
            public bool RemoveAdmin;
        }

        public class EventMessageSettings
        {
            [JsonProperty(PropertyName = "Show Message For Block Damage Outside Of The Dome To Players Inside")]
            public bool NoDamageFromOutsideToPlayersInside;

            [JsonProperty(PropertyName = "Ineligible For Rewards")]
            public EventMessageRewardSettings Rewards = new();

            [JsonProperty(PropertyName = "Announce Raid Unlocked")]
            public bool AnnounceRaidUnlock;

            [JsonProperty(PropertyName = "Announce Thief Message")]
            public bool AnnounceThief = true;

            [JsonProperty(PropertyName = "Announce PVE/PVP Enter/Exit Messages")]
            public bool AnnounceEnterExit = true;

            [JsonProperty(PropertyName = "Announce When Blocks Are Immune To Damage")]
            public bool BlocksImmune;

            [JsonProperty(PropertyName = "Show Destroy Warning")]
            public bool ShowWarning = true;

            [JsonProperty(PropertyName = "Show Opened Message For PVE Bases")]
            public bool OpenedPVE = true;

            [JsonProperty(PropertyName = "Show Opened Message For PVP Bases")]
            public bool OpenedPVP = true;

            [JsonProperty(PropertyName = "Show Prefix")]
            public bool Prefix = true;

            [JsonProperty(PropertyName = "Notify Plugin - Type (-1 = disabled)")]
            public int NotifyType = -1;

            [JsonProperty(PropertyName = "Rust Game Tip Style (0 = blue norm, 1 = red norm, 2 = blue long, 3 = blue short, 4 = server)")]
            public GameTip.Styles RustStyle = NoRustStyle;
            internal const GameTip.Styles NoRustStyle = (GameTip.Styles)(-1);

            [JsonProperty(PropertyName = "Strip Colors From Rust Game Tip Messages")]
            public bool StripRustTip;

            [JsonProperty(PropertyName = "Notification Interval")]
            public float Interval = 1f;

            [JsonProperty(PropertyName = "Send Messages To Player")]
            public bool Message = true;

            [JsonProperty(PropertyName = "Save Thieves To Log File")]
            public bool LogThieves;
        }

        public class GUIAnnouncementSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled;

            [JsonProperty(PropertyName = "Banner Tint Color")]
            public string TintColor = "Grey";

            [JsonProperty(PropertyName = "Maximum Distance")]
            public float Distance = 300f;

            [JsonProperty(PropertyName = "Text Color")]
            public string TextColor = "White";
        }

        public class NpcKitSettings
        {
            [JsonProperty(PropertyName = "Helm", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Helm = new();

            [JsonProperty(PropertyName = "Torso", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Torso = new();

            [JsonProperty(PropertyName = "Pants", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Pants = new();

            [JsonProperty(PropertyName = "Gloves", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Gloves = new();

            [JsonProperty(PropertyName = "Boots", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Boots = new();

            [JsonProperty(PropertyName = "Shirt", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shirt = new();

            [JsonProperty(PropertyName = "Kilts", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kilts = new();

            [JsonProperty(PropertyName = "Weapon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Weapon = new();

            public NpcKitSettings(bool isMurderer)
            {
                if (isMurderer)
                {
                    Helm.Add("metal.facemask");
                    Torso.Add("metal.plate.torso");
                    Pants.Add("pants");
                    Gloves.Add("tactical.gloves");
                    Boots.Add("boots.frog");
                    Shirt.Add("tshirt");
                    Weapon.Add("machete");
                }
                else
                {
                    Torso.Add("hazmatsuit_scientist_peacekeeper");
                    Weapon.Add("rifle.ak");
                }
            }
        }

        public class NpcMultiplierSettings
        {
            [JsonProperty(PropertyName = "Explosive Damage Multiplier")]
            public float ExplosiveDamageMultiplier = 1f;

            [JsonProperty(PropertyName = "Gun Damage Multiplier")]
            public float ProjectileDamageMultiplier = 1f;

            [JsonProperty(PropertyName = "Melee Damage Multiplier")]
            public float MeleeDamageMultiplier = 1f;
        }

        public class NpcSettingsAccuracyDifficulty
        {
            [JsonProperty(PropertyName = "AK47")]
            public double AK47;

            [JsonProperty(PropertyName = "AK47 ICE")]
            public double AK47ICE;

            [JsonProperty(PropertyName = "Bolt Rifle")]
            public double BOLT_RIFLE;

            [JsonProperty(PropertyName = "Compound Bow")]
            public double COMPOUND_BOW;

            [JsonProperty(PropertyName = "Crossbow")]
            public double CROSSBOW;

            [JsonProperty(PropertyName = "Double Barrel Shotgun")]
            public double DOUBLE_SHOTGUN;

            [JsonProperty(PropertyName = "Eoka")]
            public double EOKA;

            [JsonProperty(PropertyName = "Glock")]
            public double GLOCK;

            [JsonProperty(PropertyName = "HMLMG")]
            public double HMLMG;

            [JsonProperty(PropertyName = "L96")]
            public double L96;

            [JsonProperty(PropertyName = "LR300")]
            public double LR300;

            [JsonProperty(PropertyName = "M249")]
            public double M249;

            [JsonProperty(PropertyName = "Minigun")]
            public double MINIGUN;

            [JsonProperty(PropertyName = "M39")]
            public double M39;

            [JsonProperty(PropertyName = "M92")]
            public double M92;

            [JsonProperty(PropertyName = "MP5")]
            public double MP5;

            [JsonProperty(PropertyName = "Nailgun")]
            public double NAILGUN;

            [JsonProperty(PropertyName = "Pump Shotgun")]
            public double PUMP_SHOTGUN;

            [JsonProperty(PropertyName = "Python")]
            public double PYTHON;

            [JsonProperty(PropertyName = "Revolver")]
            public double REVOLVER;

            [JsonProperty(PropertyName = "Semi Auto Pistol")]
            public double SEMI_AUTO_PISTOL;

            [JsonProperty(PropertyName = "Semi Auto Rifle")]
            public double SEMI_AUTO_RIFLE;

            [JsonProperty(PropertyName = "Spas12")]
            public double SPAS12;

            [JsonProperty(PropertyName = "Speargun")]
            public double SPEARGUN;

            [JsonProperty(PropertyName = "SMG")]
            public double SMG;

            [JsonProperty(PropertyName = "Snowball Gun")]
            public double SNOWBALL_GUN;

            [JsonProperty(PropertyName = "Thompson")]
            public double THOMPSON;

            [JsonProperty(PropertyName = "Waterpipe Shotgun")]
            public double WATERPIPE_SHOTGUN;

            public NpcSettingsAccuracyDifficulty(double accuracy)
            {
                AK47 = AK47ICE = BOLT_RIFLE = DOUBLE_SHOTGUN = EOKA = GLOCK = HMLMG = L96 = LR300 = M249 = MINIGUN = M39 = M92 = MP5 = NAILGUN = PUMP_SHOTGUN = PYTHON = REVOLVER = SEMI_AUTO_PISTOL = SEMI_AUTO_RIFLE = SPAS12 = SPEARGUN = SMG = SNOWBALL_GUN = THOMPSON = WATERPIPE_SHOTGUN = accuracy;
                COMPOUND_BOW = CROSSBOW = 50;
            }

            public double Get(HumanoidBrain brain)
            {
                if (string.IsNullOrEmpty(brain.AttackName))
                    return 0;
                return brain.AttackName switch
                {
                    "ak47u.entity" or "ak47u_med.entity" or "ak47u_diver.entity" or "sks.entity" => AK47,
                    "ak47u_ice.entity" => AK47ICE,
                    "bolt_rifle.entity" => BOLT_RIFLE,
                    "compound_bow.entity" or "legacybow.entity" => COMPOUND_BOW,
                    "crossbow.entity" or "bow_hunting.entity" or "mini_crossbow.entity" => CROSSBOW,
                    "double_shotgun.entity" => DOUBLE_SHOTGUN,
                    "glock.entity" or "hc_revolver.entity" => GLOCK,
                    "hmlmg.entity" or "mgl.entity" => HMLMG,
                    "l96.entity" => L96,
                    "lr300.entity" => LR300,
                    "m249.entity" => M249,
                    "minigun.entity" => MINIGUN,
                    "m39.entity" => M39,
                    "m92.entity" => M92,
                    "mp5.entity" => MP5,
                    "nailgun.entity" => NAILGUN,
                    "pistol_eoka.entity" => EOKA,
                    "pistol_revolver.entity" => REVOLVER,
                    "pistol_semiauto.entity" => SEMI_AUTO_PISTOL,
                    "python.entity" => PYTHON,
                    "semi_auto_rifle.entity" => SEMI_AUTO_RIFLE,
                    "shotgun_pump.entity" or "blunderbuss.entity" or "m4_shotgun.entity" => PUMP_SHOTGUN,
                    "shotgun_waterpipe.entity" => WATERPIPE_SHOTGUN,
                    "spas12.entity" => SPAS12,
                    "speargun.entity" or "blowpipe.entity" or "boomerang.entity" => SPEARGUN,
                    "smg.entity" or "t1_smg" => SMG,
                    "snowballgun.entity" => SNOWBALL_GUN,
                    "thompson.entity" or _ => THOMPSON,
                };
            }
        }

        public class ScientistLootSettings
        {
            [JsonProperty(PropertyName = "Prefab ID List", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> IDs = new() { "cargo", "turret_any", "ch47_gunner", "excavator", "full_any", "heavy", "junkpile_pistol", "oilrig", "patrol", "peacekeeper", "roam", "roamtethered" };

            [JsonProperty(PropertyName = "Enabled", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public bool Enabled;

            [JsonProperty(PropertyName = "Disable All Prefab Loot Spawns", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public bool None;

            [JsonProperty(PropertyName = "Call OnCorpsePopulate Hook (some plugins require this)")]
            public bool CallHook;

            public uint GetRandom(List<string> ids) => ids.GetRandom() switch
            {
                "cargo" => 3623670799u,
                "turret_any" => 1639447304u,
                "ch47_gunner" => 1017671955u,
                "excavator" => 4293908444u,
                "full_any" => 1539172658u,
                "heavy" => 1536035819u,
                "junkpile_pistol" => 2066159302u,
                "cargo_turret" => 881071619u,
                "oilrig" => 548379897u,
                "patrol" => 4272904018u,
                "peacekeeper" => 2390854225u,
                "roam" => 4199494415u,
                "roamtethered" => 529928930u,
                "scarecrow" => 3473349223u,
                "scarecrow_dungeon" => 3019050354u,
                "scarecrow_dungeonnoroam" => 70161046u,
                _ => 1536035819u
            };
        }

        public class NpcSettings
        {
            public NpcSettings() { }

            public NpcSettings(double accuracy)
            {
                Accuracy = new(accuracy);
            }

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Decrease Damage Linearly From Npcs With A Maximum Effective Range Of")]
            public float NpcMaxEffectiveRange;

            [JsonProperty(PropertyName = "Decrease Damage Linearly From Players With A Maximum Effective Range Of")]
            public float PlayerMaxEffectiveRange;

            [JsonProperty(PropertyName = "Weapon Accuracy (0 - 100)")]
            public NpcSettingsAccuracyDifficulty Accuracy;

            [JsonProperty(PropertyName = "Damage Multipliers")]
            public NpcMultiplierSettings Multipliers = new();

            [JsonProperty(PropertyName = "Murderer Items Dropped On Death", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> MurdererDrops = new() { new("ammo.pistol", 1, 30, 0, false, 0) };

            [JsonProperty(PropertyName = "Scientist Items Dropped On Death", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> ScientistDrops = new() { new("ammo.rifle", 1, 30, 0, false, 0) };

            [JsonProperty(PropertyName = "Murderer (Items)")]
            public NpcKitSettings MurdererLoadout = new(true);

            [JsonProperty(PropertyName = "Scientist (Items)")]
            public NpcKitSettings ScientistLoadout = new(false);

            [JsonProperty(PropertyName = "Murderer Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MurdererKits = new() { "murderer_kit_1", "murderer_kit_2" };

            [JsonProperty(PropertyName = "Scientist Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ScientistKits = new() { "scientist_kit_1", "scientist_kit_2" };

            [JsonProperty(PropertyName = "Use Random Names")]
            public bool UseRandomNames = true;

            [JsonProperty(PropertyName = "Use Capitalized Names")]
            public bool Capitalize;

            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNpcNames = new();

            [JsonProperty(PropertyName = "Spawn Alternate Default Scientist Loot")]
            public ScientistLootSettings AlternateScientistLoot = new();

            [JsonProperty(PropertyName = "Amount Of Murderers To Spawn")]
            public int SpawnAmountMurderers = -9;

            [JsonProperty(PropertyName = "Minimum Amount Of Murderers To Spawn")]
            public int SpawnMinAmountMurderers = -9;

            [JsonProperty(PropertyName = "Spawn Random Amount Of Murderers")]
            public bool SpawnRandomAmountMurderers;

            [JsonProperty(PropertyName = "Amount Of Scientists To Spawn")]
            public int SpawnAmountScientists = -9;

            [JsonProperty(PropertyName = "Minimum Amount Of Scientists To Spawn")]
            public int SpawnMinAmountScientists = -9;

            [JsonProperty(PropertyName = "Spawn Random Amount Of Scientists")]
            public bool SpawnRandomAmountScientists;

            [JsonProperty(PropertyName = "Allow Npcs To Leave Dome When Attacking")]
            public bool CanLeave = true;

            [JsonProperty(PropertyName = "Allow Npcs To Shoot Players Outside Of The Dome")]
            public bool CanShoot = true;

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange = 70f;

            [JsonProperty(PropertyName = "Block Damage Outside To Npcs When Not Allowed To Leave Dome")]
            public bool BlockOutsideDamageOnLeave = true;

            [JsonProperty(PropertyName = "Block Damage Outside Of The Dome To Npcs Inside")]
            public bool BlockOutsideDamageToNpcsInside;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory = true;

            [JsonProperty(PropertyName = "Health For Murderers (100 min, 5000 max)")]
            public float MurdererHealth = 150f;

            [JsonProperty(PropertyName = "Health For Scientists (100 min, 5000 max)")]
            public float ScientistHealth = 150f;

            [JsonProperty(PropertyName = "Kill Underwater Npcs")]
            public bool KillUnderwater = true;

            [JsonProperty(PropertyName = "Player Traps And Turrets Ignore Npcs")]
            public bool IgnorePlayerTrapsTurrets;

            [JsonProperty(PropertyName = "Event Traps And Turrets Ignore Npcs")]
            public bool IgnoreTrapsTurrets = true;

            [JsonProperty(PropertyName = "Use Dangerous Treasures NPCs")]
            public bool UseExpansionNpcs;
        }

        public class PasteOption
        {
            [JsonProperty(PropertyName = "Option")]
            public string Key;

            [JsonProperty(PropertyName = "Value")]
            public string Value;
        }

        public class BuildingLevels
        {
            [JsonProperty(PropertyName = "Level 2 - Final Death")]
            public bool Level2;
        }

        public class DoorTypes
        {
            [JsonProperty(PropertyName = "Wooden")]
            public bool Wooden;

            [JsonProperty(PropertyName = "Metal")]
            public bool Metal;

            [JsonProperty(PropertyName = "HQM")]
            public bool HQM;

            [JsonProperty(PropertyName = "Include Garage Doors")]
            public bool GarageDoor;

            public bool Any() => Wooden || Metal || HQM;
        }

        public class BuildingGradeLevels
        {
            [JsonProperty(PropertyName = "Wooden")]
            public bool Wooden;

            [JsonProperty(PropertyName = "Stone")]
            public bool Stone;

            [JsonProperty(PropertyName = "Metal")]
            public bool Metal;

            [JsonProperty(PropertyName = "HQM")]
            public bool HQM;

            public bool Any() => Wooden || Stone || Metal || HQM;
        }

        public class BuildingOptionsAutoTurrets
        {
            [JsonProperty(PropertyName = "Aim Cone")]
            public float AimCone = 5f;

            [JsonProperty(PropertyName = "Wait To Power On Until Event Starts")]
            public bool InitiateOnSpawn;

            [JsonProperty(PropertyName = "Minimum Damage Modifier")]
            public float Min = 1f;

            [JsonProperty(PropertyName = "Maximum Damage Modifier")]
            public float Max = 1f;

            [JsonProperty(PropertyName = "Start Health")]
            public float Health = 1000f;

            [JsonProperty(PropertyName = "Sight Range")]
            public float SightRange = 30f;

            [JsonProperty(PropertyName = "Double Sight Range When Shot")]
            public bool AutoAdjust;

            [JsonProperty(PropertyName = "Set Hostile (False = Do Not Set Any Mode)")]
            public bool Hostile = true;

            [JsonProperty(PropertyName = "Requires Power Source")]
            public bool RequiresPower;

            [JsonProperty(PropertyName = "Remove Equipped Weapon")]
            public bool RemoveWeapon;

            [JsonProperty(PropertyName = "Random Weapons To Equip When Unequipped", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shortnames = new() { "rifle.ak" };

            [JsonProperty(PropertyName = "Remove Event Turrets For How Many Hours After Map Wipe")]
            public double TurretHours;
        }

        public class BuildingOptionsProtectionRadius
        {
            [JsonProperty(PropertyName = "Maintained Events")]
            public float Maintained = 50f;

            [JsonProperty(PropertyName = "Manual Events")]
            public float Manual = 50f;

            [JsonProperty(PropertyName = "Scheduled Events")]
            public float Scheduled = 50f;

            [JsonProperty(PropertyName = "Obstruction Distance Check")]
            public float Obstruction = -1f;

            public void Set(float value)
            {
                Maintained = value;
                Manual = value;
                Scheduled = value;
            }

            public float Get(RaidableType type)
            {
                switch (type)
                {
                    case RaidableType.Maintained: return Maintained;
                    case RaidableType.Scheduled: return Scheduled;
                    case RaidableType.Manual: return Manual;
                    default: return Max();
                }
            }

            public float Max() => Mathf.Max(Maintained, Manual, Scheduled);

            public float Min() => Mathf.Min(Maintained, Manual, Scheduled);
        }

        public class BuildingWaterOptions
        {
            [JsonProperty(PropertyName = "Allow Bases To Float Above Water")]
            public bool AllowSubmerged;

            [JsonProperty(PropertyName = "Prevent Bases From Floating Above Water By Also Checking Surrounding Area")]
            public bool SubmergedAreaCheck;

            [JsonProperty(PropertyName = "Maximum Water Depth Level Used For Float Above Water Option")]
            public float WaterDepth = 1f;

            [JsonProperty(PropertyName = "Torpedo Damage Multiplier (Min)")]
            public float TorpedoMin = 3f;

            [JsonProperty(PropertyName = "Torpedo Damage Multiplier (Max)")]
            public float TorpedoMax = 3f;

            internal float OceanLevel;
        }

        public class IQDronePatrolSettings
        {
            [JsonProperty("Use drone support")]
            public bool UseDronePatrol;

            [JsonProperty("How many drones will be spawned near the base?")]
            public int droneCountSpawned = 10;

            [JsonProperty("How many drones can attack simultaneously?")]
            public int droneAttackedCount = 2;

            [JsonProperty("Drone presets configuration [Drone preset key from the drone config] - chance")]
            public Dictionary<String, int> keyDrones = new()
            {
                ["LITE_DRONE"] = 100, //Ключи дронов с их пресетами и шансом (ключи берутся из конфига дронов)
            };
        }

        public class PlayerDamageMultiplier
        {
            [JsonProperty(PropertyName = "Type")]
            public string Type;

            [JsonProperty(PropertyName = "Min")]
            public float Min = 1f;

            [JsonProperty(PropertyName = "Max")]
            public float Max = 1f;

            internal float amount => UnityEngine.Random.Range(Min, Max);

            internal DamageType[] _damageTypes;

            internal DamageType index => Array.Find(_damageTypes ??= (DamageType[])Enum.GetValues(typeof(DamageType)), type => type.ToString().Equals(Type, StringComparison.OrdinalIgnoreCase));

            public PlayerDamageMultiplier() { }

            public PlayerDamageMultiplier(string type, float min, float max)
            {
                (Type, Min, Max) = (type, min, max);
            }
        }

        public class SiegeSettings
        {
            [JsonProperty(PropertyName = "Allow Siege Raiding Only")]
            public bool Only;

            [JsonProperty(PropertyName = "Damage Multiplier")]
            public float SiegeMultiplier = 1f;

            [JsonProperty(PropertyName = "Damage Multiplier (Ballista)")]
            public float BallistaMultiplier = 1f;

            [JsonProperty(PropertyName = "Damage Multiplier (Catapult)")]
            public float CatapultMultiplier = 1f;

            [JsonProperty(PropertyName = "Damage Multiplier (Ram)")]
            public float RamMultiplier = 1f;

            [JsonProperty(PropertyName = "Damage Multiplier (Cannon)")]
            public float CannonMultiplier = 1f;

            internal bool Disabled;

            internal bool Any => BallistaMultiplier != 1 || CatapultMultiplier != 1 || RamMultiplier != 1 || CannonMultiplier != 1;

            public void Scale(BasePlayer attacker, HitInfo info, bool isHuman)
            {
                if (BallistaMultiplier != 1f && isHuman && !info.IsProjectile() && !(info.WeaponPrefab is TimedExplosive) && attacker.GetMounted() is BallistaGun)
                {
                    info.damageTypes.ScaleAll(BallistaMultiplier);
                }
                else if (CatapultMultiplier != 1f && info.WeaponPrefab != null && info.WeaponPrefab.ShortPrefabName.Contains("boulder_"))
                {
                    info.damageTypes.ScaleAll(CatapultMultiplier);
                }
                else if (RamMultiplier != 1f && info.WeaponPrefab is BatteringRam)
                {
                    info.damageTypes.ScaleAll(RamMultiplier);
                }
                else if (CannonMultiplier != 1f && info.WeaponPrefab is Cannon)
                {
                    info.damageTypes.ScaleAll(CannonMultiplier);
                }
            }

            public bool IsSiegeTool(BasePlayer attacker, HitInfo info, DamageType damageType)
            {
                if (info.WeaponPrefab is TimedExplosive te && te != null)
                {
                    return te.ShortPrefabName.Contains("boulder");
                }
                if (damageType.IsMeleeType() || info.WeaponPrefab is BaseSiegeWeapon or BallistaGun or Cannon)
                {
                    return true;
                }
                if (info.Weapon != null)
                {
                    Item weapon = info.Weapon.GetCachedItem();
                    if (weapon != null && weapon.info != null)
                    {
                        return weapon.info.IsAllowedInEra(EraRestriction.Default, Era.Primitive);
                    }
                }
                Item item = attacker.GetActiveItem();
                if (item != null && item.info != null)
                {
                    if (!item.info.IsAllowedInEra(EraRestriction.Default, Era.Primitive))
                    {
                        return false;
                    }
                    BaseProjectile projectile = item.GetHeldEntity() as BaseProjectile;
                    if (projectile == null || projectile.primaryMagazine == null || projectile.primaryMagazine.ammoType == null)
                    {
                        return true;
                    }
                    return projectile.primaryMagazine.ammoType.IsAllowedInEra(EraRestriction.Default, Era.Primitive);
                }
                return attacker.GetMounted() is BaseSiegeWeapon or BatteringRamSeat or BallistaGun;
            }

            public SiegeSettings() { }
        }

        public class BuildingOptions
        {
            internal float GetLandLevel => Mathf.Clamp(LandLevel, 0.5f, 3f);

            public BuildingOptions() { }

            public BuildingOptions(params string[] bases)
            {
                PasteOptions = DefaultPasteOptions;
                AdditionalBases = new();

                if (bases?.Length > 0)
                {
                    foreach (string value in bases)
                    {
                        AdditionalBases[value] = DefaultPasteOptions;
                    }
                }
            }

            [JsonProperty(PropertyName = "Advanced Protection Radius")]
            public BuildingOptionsProtectionRadius ProtectionRadii = new();

            [JsonProperty(PropertyName = "Advanced Setup Settings")]
            public BuildingOptionsSetupSettings Setup = new();

            [JsonProperty(PropertyName = "Allow Raid Bases In Biomes")]
            public ManagementBiomeSettings Biomes = null;

            [JsonProperty(PropertyName = "Blacklisted Commands (PVE)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlacklistedPVECommands = new();

            [JsonProperty(PropertyName = "Blacklisted Commands (PVP)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlacklistedPVPCommands = new();

            [JsonProperty(PropertyName = "Despawn Options Override")]
            public ProfileDespawnOptions DespawnOptions = new();

            [JsonProperty(PropertyName = "Eject Mounts")]
            public ManagementMountableSettings Mounts = new();

            [JsonProperty(PropertyName = "Elevators")]
            public BuildingOptionsElevators Elevators = new();

            [JsonProperty(PropertyName = "Entities Not Allowed To Be Picked Up", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlacklistedPickupItems = new() { "generator.small", "generator.static", "autoturret_deployed" };

            [JsonProperty(PropertyName = "Entities Allowed To Be Picked Up", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> WhitelistedPickupItems = new() { "shutter" };

            [JsonProperty(PropertyName = "Additional Bases For This Difficulty", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<PasteOption>> AdditionalBases = new();

            [JsonProperty(PropertyName = "Paste Options", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PasteOption> PasteOptions = new();

            [JsonProperty(PropertyName = "Arena Walls")]
            public RaidableBaseWallOptions ArenaWalls = new();

            [JsonProperty(PropertyName = "NPC Levels")]
            public BuildingLevels Levels = new();

            [JsonProperty(PropertyName = "NPCs")]
            public NpcSettings NPC = new();

            [JsonProperty(PropertyName = "Rewards")]
            public RewardSettings Rewards = new();

            [JsonProperty(PropertyName = "Change Building Material Tier To")]
            public BuildingGradeLevels Blocks = new();

            [JsonProperty(PropertyName = "Change Door Type To")]
            public DoorTypes Doors = new();

            [JsonProperty(PropertyName = "Player Damage To Base Multipliers", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PlayerDamageMultiplier> PlayerDamageMultiplier = new()
            {
                new("Arrow", 1f, 1f),
                new("Blunt", 1f, 1f),
                new("Bullet", 1f, 1f),
                new("Heat", 1f, 1f),
                new("Explosion", 1f, 1f),
                new("Slash", 1f, 1f),
                new("Stab", 1f, 1f),
            };

            [JsonProperty(PropertyName = "Auto Turrets")]
            public BuildingOptionsAutoTurrets AutoTurret = new();

            [JsonProperty(PropertyName = "Player Building Restrictions")]
            public BuildingGradeLevels BuildingRestrictions = new();

            [JsonProperty(PropertyName = "Water Settings")]
            public BuildingWaterOptions Water = new();

            [JsonProperty(PropertyName = "IQDronePatrol : Setting up for spawn drones on raid bases")]
            public IQDronePatrolSettings DronePatrols = new();

            [JsonProperty(PropertyName = "Siege")]
            public SiegeSettings Siege = new();

            [JsonProperty(PropertyName = "Profile Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Maximum Land Level")]
            public float LandLevel = 2.5f;

            [JsonProperty(PropertyName = "Player Damage To Tool Cupboard Multiplier")]
            public float PlayerDamageMultiplierTC = 1f;

            [JsonProperty(PropertyName = "Allow Players To Use MLRS")]
            public bool MLRS = true;

            [JsonProperty(PropertyName = "Allow Third-Party Npc Explosive Damage To Bases")]
            public bool RaidingNpcs;

            [JsonProperty(PropertyName = "Door Controller Requires Power Source")]
            public bool DoorControllersRequiresPower = true;

            [JsonProperty(PropertyName = "Add Code Lock To Unlocked Or KeyLocked Doors")]
            public bool CodeLockDoors = true;

            [JsonProperty(PropertyName = "Add Key Lock To Unlocked Or CodeLocked Doors")]
            public bool KeyLockDoors;

            [JsonProperty(PropertyName = "Add Code Lock To Tool Cupboards")]
            public bool CodeLockPrivilege;

            [JsonProperty(PropertyName = "Add Key Lock To Tool Cupboards")]
            public bool KeyLockPrivilege;

            [JsonProperty(PropertyName = "Add Code Lock To Boxes")]
            public bool CodeLockBoxes;

            [JsonProperty(PropertyName = "Add Key Lock To Boxes")]
            public bool KeyLockBoxes;

            [JsonProperty(PropertyName = "Add Code Lock To Lockers")]
            public bool CodeLockLockers = true;

            [JsonProperty(PropertyName = "Add Key Lock To Lockers")]
            public bool KeyLockLockers;

            [JsonProperty(PropertyName = "Close Open Doors With No Door Controller Installed")]
            public bool CloseOpenDoors = true;

            [JsonProperty(PropertyName = "Allow Duplicate Items")]
            public bool AllowDuplicates;

            [JsonProperty(PropertyName = "Allow Players To Pickup Deployables")]
            public bool AllowPickup;

            [JsonProperty(PropertyName = "Allow Players To Deploy A Cupboard")]
            public bool AllowBuildingPriviledges = true;

            [JsonProperty(PropertyName = "Allow Players To Build")]
            public bool AllowBuilding = true;

            [JsonProperty(PropertyName = "Allow Players To Build (Exclusions)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedBuildingBlockExceptions = new();

            [JsonProperty(PropertyName = "Allow Players To Deploy Barricades")]
            public bool AllowBarricades = true;

            [JsonProperty(PropertyName = "Allow PVP")]
            public bool AllowPVP = true;

            [JsonProperty(PropertyName = "Allow Self Damage")]
            public bool AllowSelfDamage = true;

            [JsonProperty(PropertyName = "Allow Friendly Fire (Teams)")]
            public bool AllowFriendlyFire = true;

            [JsonProperty(PropertyName = "Minimum Amount Of Items To Spawn (0 = Use Max Value)")]
            public int MinTreasure;

            [JsonProperty(PropertyName = "Amount Of Items To Spawn")]
            public int MaxTreasure = 30;

            [JsonProperty(PropertyName = "Amount Of Items To Spawn Increased By Item Splits")]
            public bool Dynamic;

            [JsonProperty(PropertyName = "Check Lower Probability Once Per Loot Item")]
            public bool EnforceProbability;

            [JsonProperty(PropertyName = "Flame Turret Health")]
            public float FlameTurretHealth = 300f;

            [JsonProperty(PropertyName = "Briefly Holster Weapon To Prevent Camping The Entrance Of Events")]
            public bool Holster { get; set; }

            [JsonProperty(PropertyName = "Block Plugins Which Prevent Item Durability Loss", NullValueHandling = NullValueHandling.Ignore)]
            public bool? _EnforceDurability = null;

            [JsonProperty(PropertyName = "Items Always Take Condition Loss And Cannot Be Repaired")]
            public bool EnforceConditionLoss;

            [JsonProperty(PropertyName = "Block Damage To Players From Player Turrets Deployed Outside Of The Dome")]
            public bool BlockOutsideTurrets;

            [JsonProperty(PropertyName = "Block Damage Outside Of The Dome To Players Inside")]
            public bool BlockOutsideDamageToPlayersInside;

            [JsonProperty(PropertyName = "Block Damage Outside Of The Dome To Bases Inside")]
            public bool BlockOutsideDamageToBaseInside;

            [JsonProperty(PropertyName = "Block Damage Inside From Npcs To Players Outside")]
            public bool BlockNpcDamageToPlayersOutside;

            [JsonProperty(PropertyName = "Building Blocks Are Immune To Damage")]
            public bool BlocksImmune;

            [JsonProperty(PropertyName = "Building Blocks Are Immune To Damage (Twig Only)")]
            public bool TwigImmune;

            [JsonProperty(PropertyName = "Turrets Can Hurt Event Twig")]
            public bool TurretsHurtTwig;

            [JsonProperty(PropertyName = "Boxes Are Invulnerable")]
            public bool Invulnerable;

            [JsonProperty(PropertyName = "Boxes Are Invulnerable Until Cupboard Is Destroyed")]
            public bool InvulnerableUntilCupboardIsDestroyed;

            [JsonProperty(PropertyName = "Spawn Silently (No Notifcation, No Dome, No Map Marker)")]
            public bool Silent;

            [JsonProperty(PropertyName = "Hide Despawn Time On Map Marker (PVP)")]
            public bool HideDespawnTimePVP;

            [JsonProperty(PropertyName = "Hide Despawn Time On Map Marker (PVE)")]
            public bool HideDespawnTimePVE;

            [JsonProperty(PropertyName = "Use Simple Messaging")]
            public bool Smart;

            [JsonProperty(PropertyName = "Despawn Dropped Loot Bags From Raid Boxes When Base Despawns")]
            public bool DespawnGreyBoxBags;

            [JsonProperty(PropertyName = "Despawn Dropped Loot Bags From Npc When Base Despawns")]
            public bool DespawnGreyNpcBags;

            [JsonProperty(PropertyName = "Protect Loot Bags From Raid Boxes For X Seconds After Base Despawns")]
            public float PreventLooting;

            [JsonProperty(PropertyName = "Divide Loot Into All Containers")]
            public bool DivideLoot = true;

            [JsonProperty(PropertyName = "Drop Tool Cupboard Loot After Raid Is Completed")]
            public bool DropPrivilegeLoot;

            [JsonProperty(PropertyName = "Drop Container Loot X Seconds After It Is Looted")]
            public float DropTimeAfterLooting;

            [JsonProperty(PropertyName = "Drop Container Loot Applies Only To Boxes And Cupboards")]
            public bool DropOnlyBoxesAndPrivileges = true;

            [JsonProperty(PropertyName = "Create Dome Around Event Using Spheres (0 = disabled, recommended = 5)")]
            public int SphereAmount = 5;

            [JsonProperty(PropertyName = "Empty All Containers Before Spawning Loot")]
            public bool EmptyAll = true;

            [JsonProperty(PropertyName = "Empty All Containers (Exclusions)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> EmptyExemptions = new() { "xmas_tree.deployed", "xmas_tree_a.deployed", "torchholder.deployed" };

            [JsonProperty(PropertyName = "Eject Corpses From Enemy Raids (Advanced Users Only)")]
            public bool EjectBackpacks;

            [JsonProperty(PropertyName = "Eject Corpses From PVE Instantly (Advanced Users Only)")]
            public bool EjectBackpacksPVE;

            [JsonProperty(PropertyName = "Eject Enemies From Locked PVE Raids")]
            public bool EjectLockedPVE = true;

            [JsonProperty(PropertyName = "Eject Enemies From Locked PVP Raids")]
            public bool EjectLockedPVP;

            [JsonProperty(PropertyName = "Eject Tree Radius When Spawning Base")]
            public float TreeRadius;

            [JsonProperty(PropertyName = "Delete Tree Radius When Spawning Base")]
            public float DeleteRadius;

            [JsonProperty(PropertyName = "Respawn Deleted Trees After Despawning Base")]
            public bool RespawnTrees = true;

            [JsonProperty(PropertyName = "Explosion Damage Modifier (0-999)")]
            public float ExplosionModifier = 100f;

            [JsonProperty(PropertyName = "Force All Boxes To Have Same Skin")]
            public bool SetSkins = true;

            [JsonProperty(PropertyName = "Ignore Containers That Spawn With Loot Already")]
            public bool IgnoreContainedLoot;

            [JsonProperty(PropertyName = "Loot Amount Multiplier")]
            public float Multiplier = 1f;

            [JsonProperty(PropertyName = "Maximum Respawn Npc X Seconds After Death")]
            public float RespawnRateMax;

            [JsonProperty(PropertyName = "Minimum Respawn Npc X Seconds After Death")]
            public float RespawnRateMin;

            [JsonProperty(PropertyName = "No Item Input For Boxes And TC")]
            public bool NoItemInput = true;

            [JsonProperty(PropertyName = "Penalize Players On Death In PVE (ZLevels)")]
            public bool PenalizePVE = true;

            [JsonProperty(PropertyName = "Penalize Players On Death In PVP (ZLevels)")]
            public bool PenalizePVP = true;

            [JsonProperty(PropertyName = "Require Cupboard Access To Loot")]
            public bool RequiresCupboardAccess;

            [JsonProperty(PropertyName = "Require Cupboard Access To Place Ladders")]
            public bool RequiresCupboardAccessLadders;

            [JsonProperty(PropertyName = "Skip Treasure Loot And Use Loot In Base Only")]
            public bool SkipTreasureLoot;

            [JsonProperty(PropertyName = "Use Buoyant Boxex For Dropped Privilege Loot")]
            public bool BuoyantPrivilege;

            [JsonProperty(PropertyName = "Use Buoyant Boxex For Dropped Box Loot")]
            public bool BuoyantBox;

            [JsonProperty(PropertyName = "Rearm Bear Traps When Damaged")]
            public bool RearmBearTraps;

            [JsonProperty(PropertyName = "Bear Traps Are Immune To Timed Explosives")]
            public bool BearTrapsImmuneToExplosives;

            [JsonProperty(PropertyName = "Remove Locks When Event Is Completed")]
            public bool UnlockEverything;

            [JsonProperty(PropertyName = "Required Loot Percentage For Rewards")]
            public double RequiredLootPercentage;

            [JsonProperty(PropertyName = "Each Player Must Destroy An Entity For Reward Eligibility")]
            public bool RequiredDestroyEntity;

            [JsonProperty(PropertyName = "Always Spawn Base Loot Table")]
            public bool AlwaysSpawnBaseLoot;

            //[JsonProperty(PropertyName = "Eco Raiding", NullValueHandling = NullValueHandling.Ignore)]
            //public BuildingOptionsEco Eco = null;

            public BuildingOptions Clone() => MemberwiseClone() as BuildingOptions;

            public float ProtectionRadius(RaidableType type)
            {
                float radius = ProtectionRadii.Get(type);

                return radius < CELL_SIZE ? 50f : radius;
            }

            public int GetLootAmount(RaidableType type)
            {
                return MinTreasure > 0 ? UnityEngine.Random.Range(MinTreasure, MaxTreasure + 1) : MaxTreasure;
            }
        }

        public class BuildingOptionsEco
        {
            [JsonProperty(PropertyName = "Allow Eco Raiding Only")]
            public bool Enabled;

            [JsonProperty(PropertyName = "Allow Flame Throwers")]
            public bool FlameThrowers;
        }

        public class RaidableBaseSettingsEventTypeBase
        {
            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE;

            [JsonProperty(PropertyName = "Convert PVP To PVE")]
            public bool ConvertPVP;

            [JsonProperty(PropertyName = "Ignore Safe Checks")]
            public bool Ignore;

            [JsonProperty(PropertyName = "Ignore Safe Checks In X Radius Only")]
            public float SafeRadius;

            [JsonProperty(PropertyName = "Ignore Player Entities At Custom Spawn Locations")]
            public bool Skip;

            [JsonProperty(PropertyName = "Spawn Bases X Distance Apart")]
            public float Distance = 100f;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile = "none";
        }

        public class RaidableBaseSettingsEventTypeBaseExtended : RaidableBaseSettingsEventTypeBase
        {
            [JsonProperty(PropertyName = "Chance To Randomly Spawn PVP Bases (0 = Ignore Setting)")]
            public decimal Chance;

            [JsonProperty(PropertyName = "Include PVE Bases")]
            public bool IncludePVE = true;

            [JsonProperty(PropertyName = "Include PVP Bases")]
            public bool IncludePVP = true;

            [JsonProperty(PropertyName = "Minimum Required Players Online")]
            public int PlayerLimitMin = 1;

            [JsonProperty(PropertyName = "Maximum Limit Of Players Online")]
            public int PlayerLimitMax = 300;

            [JsonProperty(PropertyName = "Time To Wait Between Spawns")]
            public float Time = 15f;

            public int GetPlayerCount()
            {
                return BasePlayer.activePlayerList.Count;
            }
        }

        public class RaidableBaseSettingsScheduled : RaidableBaseSettingsEventTypeBaseExtended
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled;

            [JsonProperty(PropertyName = "Every Min Seconds")]
            public double IntervalMin = 3600f;

            [JsonProperty(PropertyName = "Every Max Seconds")]
            public double IntervalMax = 7200f;

            [JsonProperty(PropertyName = "Max Scheduled Events")]
            public int MaxInt = 3;

            [JsonProperty(PropertyName = "Max Scheduled Events (When Deep Sea Is Open)")]
            public int MaxDeepSea = 3;

            [JsonProperty(PropertyName = "Max To Spawn At Once (0 = Use Max Scheduled Events Amount)")]
            public int MaxOnce;

            internal int Max => MaxDeepSea == -1 ? MaxInt : (IsDeepSeaOpen() ? MaxDeepSea : MaxInt);
        }

        public class RaidableBaseSettingsMaintained : RaidableBaseSettingsEventTypeBaseExtended
        {
            [JsonProperty(PropertyName = "Always Maintain Max Events")]
            public bool Enabled;

            [JsonProperty(PropertyName = "Max Maintained Events")]
            public int MaxInt = 3;

            [JsonProperty(PropertyName = "Max Maintained Events (When Deep Sea Is Open)")]
            public int MaxDeepSea = 3;

            internal int Max => MaxDeepSea == -1 ? MaxInt : (IsDeepSeaOpen() ? MaxDeepSea : MaxInt);
        }

        public class RaidableBaseSettingsManual
        {
            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE;

            [JsonProperty(PropertyName = "Convert PVP To PVE")]
            public bool ConvertPVP;

            [JsonProperty(PropertyName = "Max Manual Events")]
            public int MaxInt = 1;

            [JsonProperty(PropertyName = "Max Manual Events (When Deep Sea Is Open)")]
            public int MaxDeepSea = 1;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile = "none";

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVE Bases")]
            public bool BypassUseOwnersForPVE;

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVP Bases")]
            public bool BypassUseOwnersForPVP = true;

            internal int Max => MaxDeepSea == -1 ? MaxInt : (IsDeepSeaOpen() ? MaxDeepSea : MaxInt);
        }

        public class RaidableBaseWallOptions
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Stacks")]
            public int Stacks = 1;

            [JsonProperty(PropertyName = "Ignore Stack Limit When Clipping Terrain")]
            public bool IgnoreWhenClippingTerrain = true;

            [JsonProperty(PropertyName = "Ignore Forced Height Option")]
            public bool IgnoreForcedHeight = true;

            [JsonProperty(PropertyName = "Use Stone Walls")]
            public bool Stone = true;

            [JsonProperty(PropertyName = "Use Iced Walls")]
            public bool Ice;

            [JsonProperty(PropertyName = "Use Least Amount Of Walls")]
            public bool LeastAmount = true;

            [JsonProperty(PropertyName = "Use UFO Walls")]
            public bool UseUFOWalls;

            [JsonProperty(PropertyName = "Radius")]
            public float Radius = 25f;
        }

        public class RankedLadderSettings
        {
            [JsonProperty(PropertyName = "Award Top X Players On Wipe")]
            public int Amount = 3;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Show Top X Ladder")]
            public int Top = 10;

            [System.ComponentModel.DefaultValue(60d)]
            [JsonProperty(PropertyName = "Amount Of Days Before Removing Inactive User From The Lifetime Ladder?")]
            public double Days = 60;
        }

        public class RewardSettings
        {
            [JsonProperty(PropertyName = "Economics Money")]
            public double Money;

            [JsonProperty(PropertyName = "ServerRewards Points")]
            public int Points;

            [JsonProperty(PropertyName = "SkillTree XP")]
            public double SkillTree;

            [JsonProperty(PropertyName = "XLevels XP")]
            public double XLevels = -125;

            [JsonProperty(PropertyName = "XPerience XP")]
            public double XPerience = -125;

            [JsonProperty(PropertyName = "Double Rewards At Night Time Hours")]
            public bool DoubleAtNighttime;

            internal bool IsDoubledAtNighttime() => DoubleAtNighttime && TOD_Sky.Instance?.IsNight == true;
        }

        public class SkinSettingsDefault
        {
            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool Random = true;

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool Workshop = true;

            [JsonProperty(PropertyName = "Use Imported Workshop Skins File")]
            public bool Imported = true;

            [JsonProperty(PropertyName = "Use Approved Workshop Skins Only")]
            public bool ApprovedOnly;

            [JsonProperty(PropertyName = "Ignore If Skinned Already")]
            public bool IgnoreSkinned;

            [JsonProperty(PropertyName = "Use Identical Skins")]
            public bool Unique;

            [JsonProperty(PropertyName = "Preset Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Skins = new();
        }

        public class SkinSettingsLoot
        {
            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool Workshop = true;

            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool Random = true;

            [JsonProperty(PropertyName = "Use Imported Workshop Skins File")]
            public bool Imported;

            [JsonProperty(PropertyName = "Use Identical Skins For Stackable Items")]
            public bool Stackable = true;

            [JsonProperty(PropertyName = "Use Identical Skins For Non-Stackable Items")]
            public bool NonStackable;

            [JsonProperty(PropertyName = "Use Approved Workshop Skins Only")]
            public bool ApprovedOnly;
        }

        public class SkinSettingsDeployables
        {
            [JsonProperty(PropertyName = "Partial Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Names = new()
            {
                "door", "barricade", "chair", "fridge", "furnace", "locker", "reactivetarget", "rug", "sleepingbag", "table", "vendingmachine", "waterpurifier", "skullspikes", "skulltrophy", "summer_dlc", "sled"
            };

            [JsonProperty(PropertyName = "Use Approved Workshop Skins Only")]
            public bool ApprovedOnly;

            [JsonProperty(PropertyName = "Use Imported Workshop Skins File")]
            public bool ImportedWorkshop;

            [JsonProperty(PropertyName = "Preset Door Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Doors = new();

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool Workshop = true;

            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool Random = true;

            [JsonProperty(PropertyName = "Skin Everything")]
            public bool Everything = true;

            [JsonProperty(PropertyName = "Ignore If Skinned Already")]
            public bool IgnoreSkinned;

            [JsonProperty(PropertyName = "Use Identical Skins")]
            public bool Unique;
        }

        public class SkinSettingsNpcs : SkinSettingsDefault
        {
            [JsonProperty(PropertyName = "Use Skins With Murderer Kits")]
            public bool MurdererKits;

            [JsonProperty(PropertyName = "Use Skins With Scientist Kits")]
            public bool ScientistKits;

            [JsonProperty(PropertyName = "Ignore Skinned Murderer Kits")]
            public bool IgnoreSkinnedMurderer;

            [JsonProperty(PropertyName = "Ignore Skinned Scientist Kits")]
            public bool IgnoreSkinnedScientist;

            internal bool CanSkinKit(ulong skin, bool isMurderer) => (MurdererKits && isMurderer && (skin == 0uL || !IgnoreSkinnedMurderer)) || (ScientistKits && !isMurderer && (skin == 0uL || !IgnoreSkinnedScientist));
        }

        public class SkinSettings
        {
            [JsonProperty(PropertyName = "Boxes")]
            public SkinSettingsDefault Boxes = new();

            [JsonProperty(PropertyName = "Loot Items")]
            public SkinSettingsLoot Loot = new();

            [JsonProperty(PropertyName = "Npcs")]
            public SkinSettingsNpcs Npc = new();

            [JsonProperty(PropertyName = "Deployables")]
            public SkinSettingsDeployables Deployables = new();

            [JsonProperty(PropertyName = "Randomize Npc Item Skins")]
            public bool Npcs = true;

            [JsonProperty(PropertyName = "Use Identical Skins For All Npcs")]
            public bool UniqueNpcs = true;

            [JsonProperty(PropertyName = "Ignore If Skinned Already")]
            public bool IgnoreSkinned = true;
        }

        public class SkinSettingsImportedWorkshop
        {
            [JsonProperty(PropertyName = "Imported Workshop Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<ulong>> SkinList = DefaultImportedSkins;
        }

        public class LootItem : IEquatable<LootItem>
        {
            public class ArmorSlots
            {
                [JsonProperty(PropertyName = "min")]
                public int min;
                [JsonProperty(PropertyName = "max")]
                public int max;
                internal int amount => max > 0 ? UnityEngine.Random.Range(min, max + 1) : 0;
                public void TryAdd(Item item)
                {
                    if (item == null || item.info == null || !item.info.TryGetComponent(out ItemModContainerArmorSlot slot))
                    {
                        return;
                    }
                    int cap = amount;
                    if (cap > 0)
                    {
                        slot.CreateAtCapacity(cap, item);
                        slot.OnItemCreated(item);
                    }
                }
            }

            public LootItem() { }

            public LootItem(string shortname, int amountMin = 1, int amount = 1, ulong skin = 0, bool isBlueprint = false, float probability = 1.0f, int stacksize = -1, string name = null, string text = null, bool isModified = false, bool hasPriority = false, ArmorSlots slots = null)
            {
                this.shortname = shortname;
                this.amountMin = amountMin;
                this.amount = amount;
                this.skin = skin;
                this.isBlueprint = isBlueprint;
                this.probability = probability;
                this.stacksize = stacksize;
                this.name = name;
                this.text = text;
                this.isModified = isModified;
                this.hasPriority = hasPriority;
                this.slots = slots;
            }

            internal void InitializeArmorSlots()
            {
                if (slots != null || definition == null || !definition.TryGetComponent(out ItemModContainerArmorSlot slot))
                {
                    return;
                }
                slots = new()
                {
                    min = slot.MinSlots,
                    max = slot.MaxSlots
                };
            }

            [JsonProperty(PropertyName = "armor module slots", NullValueHandling = NullValueHandling.Ignore)]
            public ArmorSlots slots;

            [JsonProperty(PropertyName = "shortname")]
            public string shortname;

            [JsonProperty(PropertyName = "name")]
            public string name = null;

            [JsonProperty(PropertyName = "text")]
            public string text = null;

            [JsonProperty(PropertyName = "amount")]
            public int amount;

            [JsonProperty(PropertyName = "skin")]
            public ulong skin;

            [JsonProperty(PropertyName = "amountMin")]
            public int amountMin;

            [JsonProperty(PropertyName = "probability")]
            public float probability = 1f;

            [JsonProperty(PropertyName = "stacksize")]
            public int stacksize = -1;

            public bool HasProbability() => UnityEngine.Random.value <= probability;

            internal bool hasPriority;
            internal bool isSplit;
            internal ItemDefinition _def;

            [JsonIgnore]
            public ItemDefinition definition
            {
                get
                {
                    if (_def == null)
                    {
                        string _shortname = shortname.EndsWith(".bp") ? shortname.Replace(".bp", string.Empty) : shortname;

                        if (shortname.Contains("_") && ItemManager.FindItemDefinition(_shortname) == null)
                        {
                            _shortname = _shortname.Substring(_shortname.IndexOf("_") + 1);
                        }

                        _def = ItemManager.FindItemDefinition(_shortname);
                    }

                    return _def;
                }
            }

            [JsonIgnore]
            public bool isBlueprint;

            [JsonIgnore]
            public bool isModified;

            public LootItem Clone() => new(shortname, amountMin, amount, skin, isBlueprint, probability, stacksize, name, text, isModified, hasPriority, slots);

            public bool Equals(LootItem other) => shortname == other.shortname && amount == other.amount && skin == other.skin && amountMin == other.amountMin && text == other.text;

            public override bool Equals(object obj) => obj is LootItem ti && Equals(ti);

            public override int GetHashCode() => base.GetHashCode();
        }

        public class TreasureSettings
        {
            [JsonProperty(PropertyName = "Resources Not Moved To Cupboards", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ExcludeFromCupboard = new()
            {
                "skull.human", "battery.small", "bone.fragments", "can.beans.empty", "can.tuna.empty", "water.salt", "water", "skull.wolf"
            };

            [JsonProperty(PropertyName = "Use Day Of Week Loot")]
            public bool UseDOWL;

            [JsonProperty(PropertyName = "Do Not Duplicate Base Loot")]
            public bool Base;

            [JsonProperty(PropertyName = "Do Not Duplicate Difficulty Loot")]
            public bool Difficulty;

            [JsonProperty(PropertyName = "Do Not Duplicate Default Loot")]
            public bool Default;

            [JsonProperty(PropertyName = "Use Stack Size Limit For Spawning Items")]
            public bool Stacks;
        }

        public class UIBaseSettings
        {
            [JsonProperty(PropertyName = "Enabled", Order = 1)]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Anchor Min", Order = 2)]
            public string AnchorMin;

            [JsonProperty(PropertyName = "Anchor Max", Order = 3)]
            public string AnchorMax;

            [JsonProperty(PropertyName = "Panel Alpha", NullValueHandling = NullValueHandling.Ignore, Order = 4)]
            public float? PanelAlpha = 0.98f;

            [JsonProperty(PropertyName = "Panel Color", NullValueHandling = NullValueHandling.Ignore, Order = 5)]
            public string PanelColor = "#000000";
        }

        public class BuildingOptionsElevators : UIBaseSettings
        {
            public BuildingOptionsElevators()
            {
                AnchorMin = "0.406 0.915";
                AnchorMax = "0.59 0.949";
                PanelAlpha = 0f;
            }

            [JsonProperty(PropertyName = "Required Access Level", Order = 5)]
            public int RequiredAccessLevel;

            [JsonProperty(PropertyName = "Required Access Level Grants Permanent Use", Order = 6)]
            public bool RequiredAccessLevelOnce;

            [JsonProperty(PropertyName = "Required Keycard Skin ID", Order = 7)]
            public ulong SkinID = 2690554489;

            [JsonProperty(PropertyName = "Requires Building Permission", Order = 8)]
            public bool RequiresBuildingPermission;

            [JsonProperty(PropertyName = "Button Health", Order = 9)]
            public float ButtonHealth = 1000f;

            [JsonProperty(PropertyName = "Elevator Health", Order = 10)]
            public float ElevatorHealth = 600f;

            [JsonProperty(PropertyName = "Requires Power Source")]
            public bool RequiresPower;

            [JsonProperty(PropertyName = "Use Static Elevators Only (bmgjet)")]
            public bool BMGOnly;
        }

        public class UIDelaySettings : UIBaseSettings
        {
            public UIDelaySettings()
            {
                AnchorMin = "0.472 0.172";
                AnchorMax = "0.55 0.311";
                Enabled = false;
            }

            [JsonProperty(PropertyName = "Text Color", Order = 5)]
            public string Foreground = "#FF0000";

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize = 12;
        }

        public class Vector2Converter : JsonConverter
        {
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector2(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]));
                }
                var o = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector2(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]));
            }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var vector = (Vector2)value;
                writer.WriteValue($"{vector.x} {vector.y}");
            }
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }

        public class UIStatusSettings : UIBaseSettings
        {
            public UIStatusSettings()
            {
                (OffsetMin, OffsetMax, PanelColor, PanelAlpha) = (new(191.957f, 17.056f), new(327.626f, 79.024f), "#252121", 0.98f);
            }

            [JsonProperty(PropertyName = "Offset Min", Order = 2, NullValueHandling = NullValueHandling.Ignore)]
            [JsonConverter(typeof(Vector2Converter))]
            public Vector2 OffsetMin;

            [JsonProperty(PropertyName = "Offset Max", Order = 3, NullValueHandling = NullValueHandling.Ignore)]
            [JsonConverter(typeof(Vector2Converter))]
            public Vector2 OffsetMax;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize = 12;

            [JsonProperty(PropertyName = "PVP Color")]
            public string ColorPVP = "#FF0000";

            [JsonProperty(PropertyName = "PVE Color")]
            public string ColorPVE = "#008000";

            [JsonProperty(PropertyName = "No Owner Color", Order = 7)]
            public string NoneColor = "#FFFFFF";

            [JsonProperty(PropertyName = "Negative Color", Order = 7)]
            public string NegativeColor = "#FF0000";

            [JsonProperty(PropertyName = "Positive Color", Order = 8)]
            public string PositiveColor = "#008000";

            [JsonProperty(PropertyName = "Title Background Color", Order = 6)]
            public string TitlePanelColor = "#000000";

            [JsonProperty(PropertyName = "Show Loot Left")]
            public bool ShowLootLeft = true;
        }

        public class UIAdvancedAlertSettings : UIBaseSettings
        {
            [JsonProperty(PropertyName = "Time Shown", Order = 5)]
            public float Time = 5f;

            public UIAdvancedAlertSettings()
            {
                AnchorMin = "0.35 0.85";
                AnchorMax = "0.65 0.95";
                PanelAlpha = null;
                PanelColor = null;
            }
        }

        public class UISettings
        {
            [JsonProperty(PropertyName = "Advanced Alerts UI")]
            public UIAdvancedAlertSettings AA = new();

            [JsonProperty(PropertyName = "Delay")]
            public UIDelaySettings Delay = new();

            [JsonProperty(PropertyName = "Status UI")]
            public UIStatusSettings Status = new();
        }

        public class WeaponTypeStateSettings
        {
            [JsonProperty(PropertyName = "AutoTurret")]
            public bool AutoTurret = true;

            [JsonProperty(PropertyName = "FlameTurret")]
            public bool FlameTurret = true;

            [JsonProperty(PropertyName = "FogMachine")]
            public bool FogMachine = true;

            [JsonProperty(PropertyName = "GunTrap")]
            public bool GunTrap = true;

            [JsonProperty(PropertyName = "SamSite")]
            public bool SamSite = true;
        }

        public class WeaponTypeAmountSettings
        {
            [JsonProperty(PropertyName = "AutoTurret")]
            public int AutoTurret = 256;

            [JsonProperty(PropertyName = "FlameTurret")]
            public int FlameTurret = 256;

            [JsonProperty(PropertyName = "FogMachine")]
            public int FogMachine = 5;

            [JsonProperty(PropertyName = "GunTrap")]
            public int GunTrap = 128;

            [JsonProperty(PropertyName = "SamSite")]
            public int SamSite = 24;
        }

        public class WeaponSettingsTeslaCoil
        {
            [JsonProperty(PropertyName = "Requires A Power Source")]
            public bool RequiresPower = true;

            [JsonProperty(PropertyName = "Max Discharge Self Damage Seconds (0 = None, 120 = Rust default)")]
            public float MaxDischargeSelfDamageSeconds;

            [JsonProperty(PropertyName = "Max Damage Output")]
            public float MaxDamageOutput = 35f;

            [JsonProperty(PropertyName = "Health")]
            public float Health = 250f;
        }

        public class WeaponSettings
        {
            [JsonProperty(PropertyName = "No Fuel Source", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Burn = new() { "skull_fire_pit", "cursedcauldron.deployed" };

            [JsonProperty(PropertyName = "Infinite Ammo")]
            public WeaponTypeStateSettings InfiniteAmmo = new();

            [JsonProperty(PropertyName = "Ammo")]
            public WeaponTypeAmountSettings Ammo = new();

            [JsonProperty(PropertyName = "Tesla Coil")]
            public WeaponSettingsTeslaCoil TeslaCoil = new();

            [JsonProperty(PropertyName = "Fog Machine Allows Motion Toggle")]
            public bool FogMotion = true;

            [JsonProperty(PropertyName = "Fog Machine Requires A Power Source")]
            public bool FogRequiresPower = true;

            [JsonProperty(PropertyName = "SamSite Repairs Every X Minutes (0.0 = disabled)")]
            public float SamSiteRepair;

            [JsonProperty(PropertyName = "SamSite Range (350.0 = Rust default)")]
            public float SamSiteRange = 75f;

            [JsonProperty(PropertyName = "SamSite Requires Power Source")]
            public bool SamSiteRequiresPower;

            [JsonProperty(PropertyName = "Spooky Speakers Requires Power Source")]
            public bool SpookySpeakersRequiresPower;

            [JsonProperty(PropertyName = "Sprinkler Requires A Power Source")]
            public bool SprinklerRequiresPower = true;

            [JsonProperty(PropertyName = "Test Generator Power")]
            public float TestGeneratorPower = 100f;

            [JsonProperty(PropertyName = "Furnace Starting Fuel")]
            public int Furnace = 1000;
        }

        public class Configuration
        {
            [JsonProperty(PropertyName = "Settings")]
            public PluginSettings Settings = new();

            [JsonProperty(PropertyName = "Event Messages")]
            public EventMessageSettings EventMessages = new();

            [JsonProperty(PropertyName = "GUIAnnouncements")]
            public GUIAnnouncementSettings GUIAnnouncement = new();

            [JsonProperty(PropertyName = "Ranked Ladder")]
            public RankedLadderSettings RankedLadder = new();

            [JsonProperty(PropertyName = "Skins")]
            public SkinSettings Skins = new();

            [JsonProperty(PropertyName = "Treasure")]
            public TreasureSettings Loot = new();

            [JsonProperty(PropertyName = "UI")]
            public UISettings UI = new();

            [JsonProperty(PropertyName = "Weapons")]
            public WeaponSettings Weapons = new();

            [JsonProperty(PropertyName = "Log Debug To File")]
            public bool LogToFile;

            [JsonProperty(PropertyName = "Block paid and restricted content to comply with Facepunch TOS")]
            public bool BlockPaidContent = true;

            [JsonProperty(PropertyName = "Destroy DLC containers once looted")]
            public bool? DestroyDlcContainerOnceLooted = null;

            internal bool DestroyLootedContainer => DestroyDlcContainerOnceLooted == true;
        }

        private bool canSaveConfig = true;
        private bool InstallationError;
        private bool? allowBuilding = null;
        private bool IsEnPremium() => Config.Get("Settings", "Buyable Events") != null;

        private bool IsRuPremium() => Config.Get("Настройки", "Покупаемые События") != null;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            canSaveConfig = false;
            try
            {
                if (IsEnPremium() || IsRuPremium())
                {
                    Puts(
                        @"STOP: The paid version files are installed, and you are attempting to install the FREE version over the top it.

                        Installing FREE on top of PAID will remove paid-only options as they do not exist in the free plugin.

                        - If you intend to use the free plugin with paid files, then open a support ticket for the correct steps.
                        - If you intended to install the PAID plugin, then download it from the site you purchased it from only.

                        This protection exists solely to prevent accidental data loss. You can override this protection by renaming Buyable Events to ANYTHING in the config."
                    );
                    InstallationError = true; NextTick(() => Interface.Oxide.UnloadPlugin(Name)); UnsubscribeHooks(); return;
                }
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new NullReferenceException("config");
                canSaveConfig = true;
                if (config.Settings.Management._RequireCupboardLooted != null)
                {
                    config.Settings.Management.RequireCupboardLooted = config.Settings.Management._RequireCupboardLooted.Value;
                    config.Settings.Management._RequireCupboardLooted = null;
                }
                if (config.Settings.Management._AllowBuilding.HasValue)
                {
                    allowBuilding = config.Settings.Management._AllowBuilding.Value;
                    config.Settings.Management._AllowBuilding = null;
                }
                if (config.DestroyDlcContainerOnceLooted == null)
                {
                    config.DestroyDlcContainerOnceLooted = config.BlockPaidContent;
                }
                SaveConfig();
            }
            catch (Exception ex)
            {
                Puts(ex.ToString());
                LoadDefaultConfig();
                exConf = ex;
            }
            UndoSettings = new(config.Settings.Management, config.LogToFile);
        }

        public List<LootItem> TreasureLoot
        {
            get
            {
                if (!Buildings.DifficultyLootLists.TryGetValue("Random", out var lootList))
                {
                    Buildings.DifficultyLootLists["Random"] = lootList = new();
                }

                return lootList.ToList();
            }
        }

        public List<LootItem> WeekdayLoot
        {
            get
            {
                if (!config.Loot.UseDOWL || !Buildings.WeekdayLootLists.TryGetValue(DateTime.Now.DayOfWeek, out var lootList))
                {
                    Buildings.WeekdayLootLists[DateTime.Now.DayOfWeek] = lootList = new();
                }

                return lootList.ToList();
            }
        }

        protected override void SaveConfig()
        {
            if (canSaveConfig && !InstallationError)
            {
                Config.WriteObject(config);
            }
        }

        protected override void LoadDefaultConfig()
        {
            config = new();
            Puts("Loaded default configuration file");
        }

        #endregion

        #region UI

        public class UI
        {
            public static void AddCuiPanel(CuiElementContainer container, string color, string amin, string amax, string omin, string omax, string parent, string name, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    CursorEnabled = cursor,
                    Image = { Color = color },
                    RectTransform = { AnchorMin = amin, AnchorMax = amax, OffsetMin = omin, OffsetMax = omax }
                }, parent, name, name);
            }

            public static void AddCuiButton(CuiElementContainer container, string buttonColor, string command, string text, string textColor, int fontSize, TextAnchor align, string amin, string amax, string omin, string omax, string parent, string name, string font = "robotocondensed-regular.ttf")
            {
                container.Add(new CuiButton
                {
                    Button = { Color = buttonColor, Command = command },
                    Text = { Text = text, Font = font, FontSize = fontSize, Align = align, Color = textColor },
                    RectTransform = { AnchorMin = amin, AnchorMax = amax, OffsetMin = omin, OffsetMax = omax }
                }, parent, name, name);
            }

            public static void AddCuiElement(CuiElementContainer container, string text, int fontSize, TextAnchor align, string textColor, string amin, string amax, string omin, string omax, string parent, string name, string font = "robotocondensed-bold.ttf", string distance = "1 -1")
            {
                container.Add(new CuiElement
                {
                    DestroyUi = name,
                    Name = name,
                    Parent = parent,
                    Components = {
                    new CuiTextComponent { Text = text, Font = font, FontSize = fontSize, Align = align, Color = textColor },
                    new CuiOutlineComponent { Color = "0 0 0 0", Distance = distance },
                    new CuiRectTransformComponent { AnchorMin = amin, AnchorMax = amax, OffsetMin = omin, OffsetMax = omax }
                }
                });
            }

            public static double ParseHexComponent(string hex, int j, int k) => hex.Length >= 6 && int.TryParse(hex.TrimStart('#').AsSpan(j, k), NumberStyles.AllowHexSpecifier, NumberFormatInfo.CurrentInfo, out var num) ? num : 1;

            public static string ConvertHexToRGBA(string hex, float a) => $"{ParseHexComponent(hex, 0, 2) / 255} {ParseHexComponent(hex, 2, 2) / 255} {ParseHexComponent(hex, 4, 2) / 255} {Mathf.Clamp(a, 0f, 1f)}";

            public static void DestroyDelayUI(BasePlayer player)
            {
                if (player.IsNetworked() && Delay.Remove(player))
                {
                    CuiHelper.DestroyUi(player, DelayPanelName);
                    DestroyDelayUpdate(player);
                }
            }

            public static void DestroyStatusUI(BasePlayer player)
            {
                if (player.IsNetworked() && Players.Remove(player))
                {
                    CuiHelper.DestroyUi(player, StatusPanelName);
                    DestroyStatusUpdate(player);
                }
            }

            public static void DestroyAll()
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (Players.Contains(player))
                    {
                        CuiHelper.DestroyUi(player, StatusPanelName);
                    }
                    if (Delay.Contains(player))
                    {
                        CuiHelper.DestroyUi(player, DelayPanelName);
                    }
                }
                Delay.Clear();
                Players.Clear();
                InvokeTimers.Clear();
            }

            private static void ShowDelayUI(RaidableBases Instance, BasePlayer player)
            {
                if (player.IsKilled())
                {
                    return;
                }

                if (!Instance.PvpDelay.TryGetValue(player.userID, out var ds))
                {
                    return;
                }

                if (ds.time < Time.realtimeSinceStartupAsDouble)
                {
                    if (ds.Timer != null && !ds.Timer.Destroyed)
                    {
                        ds.Timer.Callback.Invoke();
                        ds.Timer.Destroy();
                    }

                    Instance.PvpDelay.Remove(player.userID);
                    DestroyDelayUI(player);
                    return;
                }

                if (Instance.EventTerritory(player.transform.position))
                {
                    return;
                }

                var ui = Instance.config.UI.Delay;

                CreateDelayUI(Instance.config, player, DelayPanelName, Math.Ceiling(ds.time - Time.realtimeSinceStartupAsDouble).ToString(), ui.Foreground, ConvertHexToRGBA(ui.PanelColor, ui.PanelAlpha.Value), ui.AnchorMin, ui.AnchorMax);

                Delay.Add(player);
            }

            private static void CreateDelayUI(Configuration config, BasePlayer player, string panelName, string text, string color, string panelColor, string aMin, string aMax)
            {
                var container = new CuiElementContainer();
                AddCuiPanel(container, panelColor, aMin, aMax, null, null, "Hud", panelName, false);
                AddCuiElement(container, text, config.UI.Delay.FontSize, TextAnchor.MiddleCenter, ConvertHexToRGBA(color, 1), "0 0", "1 1", null, null, panelName, "LBL");
                CuiHelper.AddUi(player, container);
            }

            public static void UpdateDelayUI(RaidableBases Instance, BasePlayer player)
            {
                Delay.RemoveAll(x => !x.IsNetworked());

                if (!player.IsNetworked())
                {
                    return;
                }

                DestroyDelayUI(player);

                if (Instance.config == null || !Instance.config.UI.Delay.Enabled)
                {
                    return;
                }

                ShowDelayUI(Instance, player);
                SetDelayUpdate(Instance, player);
            }

            private static void SetDelayUpdate(RaidableBases Instance, BasePlayer player)
            {
                if (!InvokeTimers.TryGetValue(player.userID, out var timers))
                {
                    InvokeTimers[player.userID] = timers = new();
                }

                if (timers.Delay == null || timers.Delay.Destroyed)
                {
                    timers.Delay = Instance.timer.Once(1f, () => UpdateDelayUI(Instance, player));
                }
                else timers.Delay.Reset();
            }

            public static void DestroyDelayUpdate(BasePlayer player)
            {
                if (InvokeTimers.TryGetValue(player.userID, out var timers) && timers.Delay != null && !timers.Delay.Destroyed)
                {
                    timers.Delay.Destroy();
                }
            }

            public static bool ShowStatusUi(RaidableBases Instance, BasePlayer player)
            {
                float radius = 5f;

                if (player.HasParent())
                {
                    BaseEntity parent = player.GetParentEntity();
                    if (parent != null)
                    {
                        radius += parent.bounds.size.Max();
                    }
                }

                if (!Instance.Get(player.transform.position, out var raid, radius) || raid.IsDespawning)
                {
                    DestroyStatusUI(player);
                    return false;
                }

                var mx = Instance.mx;
                var ui = Instance.config.UI.Status;
                var container = new CuiElementContainer();
                var panelAlpha = ui.PanelAlpha ?? 1f;
                var colorPanel = ConvertHexToRGBA(ui.PanelColor, panelAlpha);
                var colorTitle = ConvertHexToRGBA(ui.TitlePanelColor, panelAlpha);
                var colorAllow = ConvertHexToRGBA(raid.AllowPVP ? ui.ColorPVP : ui.ColorPVE, 1f);
                string textAllow = raid.AllowPVP ? Instance.mx("PVP ZONE", player.UserIDString) : Instance.mx("PVE ZONE", player.UserIDString);
                var minString = $"{ui.OffsetMin.x} {ui.OffsetMin.y}";
                var maxString = $"{ui.OffsetMax.x} {ui.OffsetMax.y}";

                SetOwner(raid, ui, player, out var ownerName, out var ownerColor);
                AddCuiPanel(container, colorPanel, "0.5 0", "0.5 0", minString, maxString, "Hud", StatusPanelName);
                AddCuiPanel(container, colorTitle, "0.5 0.5", "0.5 0.5", "-58.355 18.811", "58.265 44.436", StatusPanelName, "ST_TITLE_PANEL");
                AddCuiPanel(container, colorPanel, "0.5 0.5", "0.5 0.5", "-53.432 -10.288", "53.432 10.288", "ST_TITLE_PANEL", "ST_PVP_PANEL");
                if (raid.DespawnTime > 0)
                {
                    AddCuiButton(container, colorPanel, "", mx("UIFormatLockoutMinutes", player.UserIDString, raid.DespawnTime), "1 1 1 1", ui.FontSize, TextAnchor.MiddleCenter, "0 0", "1 1", "55.871 0.697", "0.333 0", "ST_PVP_PANEL", "ST_DESPAWN_LABEL");
                }
                AddCuiButton(container, colorPanel, "", textAllow, colorAllow, ui.FontSize, TextAnchor.MiddleCenter, "0.5 0.5", "0.5 0.5", "-53.429 -10.288", "2.439 10.288", "ST_PVP_PANEL", "ST_PVP_LABEL");
                AddCuiElement(container, raid.IsOpened ? mx("Owner", player.UserIDString) : ownerName, ui.FontSize, TextAnchor.MiddleLeft, "1 0.87 0.05 1", "0 0", "1 1", "9.853 2.775", "-85.197 -38.414", StatusPanelName, "ST_OWNER_LABEL");
                AddCuiElement(container, raid.IsOpened ? ownerName : mx("Completed", player.UserIDString), ui.FontSize, TextAnchor.MiddleRight, ownerColor, "0 0", "1 1", "50.473 2.774", "-9.194 -38.415", StatusPanelName, "ST_NAME_LABEL");
                if (ui.ShowLootLeft)
                {
                    AddCuiElement(container, mx("Loot", player.UserIDString), ui.FontSize, TextAnchor.MiddleLeft, "1 0.87 0.05 1", "0 0", "1 1", "9.851 23.555", "-69.078 -17.644", StatusPanelName, "ST_LOOT_LABEL");
                    AddCuiElement(container, raid.GetLootAmountRemaining().ToString(), ui.FontSize, TextAnchor.MiddleRight, "1 1 1 1", "0 0", "1 1", "66.595 23.555", "-9.195 -17.644", StatusPanelName, "ST_LOOTLEFT_LABEL");
                }

                Players.Remove(player);
                CuiHelper.AddUi(player, container);
                Players.Add(player);

                return true;
            }

            private static void SetOwner(RaidableBase raid, UIStatusSettings ui, BasePlayer player, out string ownerName, out string ownerColor)
            {
                var mx = raid.Instance.mx;
                var config = raid.config;
                ownerColor = ui.NoneColor;
                ownerName = mx("None", player.UserIDString);

                if (raid.ownerId.IsSteamId())
                {
                    if (raid.ownerId == player.userID)
                    {
                        ownerColor = ui.PositiveColor;
                        ownerName = mx("You", player.UserIDString);
                    }
                    else if (raid.IsAlly(raid.ownerId, player.userID))
                    {
                        ownerColor = ui.PositiveColor;
                        ownerName = mx("Ally", player.UserIDString);
                    }
                    else
                    {
                        ownerColor = ui.NegativeColor;
                        ownerName = mx("Enemy", player.UserIDString);
                    }
                }

                if (config.Settings.Management.LockTime > 0f)
                {
                    double time = raid.GetRaider(player).lastActiveTime;
                    double secondsLeft = Math.Max(0d, (config.Settings.Management.LockTime * 60d) - (Time.realtimeSinceStartupAsDouble - time));
                    ownerName = $"{ownerName} ({mx("UiInactiveTimeLeft", player.UserIDString, GetMinutes(secondsLeft).ToString())})";
                }

                ownerColor = ConvertHexToRGBA(ownerColor, 1f);
            }

            private static double GetMinutes(double value) => Math.Ceiling(TimeSpan.FromSeconds(value).TotalMinutes);

            public static void UpdateStatusUI(RaidableBase raid)
            {
                raid.GetRaiders(false).ForEach(player => UpdateStatusUI(raid.Instance, player));
            }

            public static void UpdateStatusUI(RaidableBases Instance, BasePlayer player)
            {
                Players.RemoveAll(x => !x.IsNetworked());

                if (Instance.config != null && player.IsNetworked())
                {
                    if (Instance.config.UI.Status.Enabled)
                    {
                        ShowStatusUi(Instance, player);
                        SetStatusUpdate(Instance, player);
                    }
                    else DestroyStatusUI(player);
                }
            }

            private static void SetStatusUpdate(RaidableBases Instance, BasePlayer player)
            {
                float radius = 5f;

                if (player.HasParent())
                {
                    BaseEntity parent = player.GetParentEntity();
                    if (parent != null)
                    {
                        radius += parent.bounds.size.Max();
                    }
                }

                if (!Instance.Get(player.transform.position, out var raid, radius) || raid.IsDespawning)
                {
                    return;
                }


                if (!InvokeTimers.TryGetValue(player.userID, out var timers))
                {
                    InvokeTimers[player.userID] = timers = new();
                }

                if (timers.Status == null || timers.Status.Destroyed)
                {
                    timers.Status = Instance.timer.Once(1f, () => UpdateStatusUI(Instance, player));
                }
                else timers.Status.Reset();
            }

            public static void DestroyStatusUpdate(BasePlayer player)
            {
                if (InvokeTimers.TryGetValue(player.userID, out var timers) && timers.Status != null && !timers.Status.Destroyed)
                {
                    timers.Status.Destroy();
                }
            }

            public const string DelayPanelName = "RB_UI_Delay";
            public const string StatusPanelName = "RB_UI_Status";

            public static List<BasePlayer> Delay = new();
            public static List<BasePlayer> Players = new();
            public static Dictionary<ulong, Timers> InvokeTimers = new();

            public class Timers
            {
                public Timer Delay;
                public Timer Status;
                public Timer Lockout;
            }
        }

        #endregion UI
    }
}

namespace Oxide.Plugins.RaidableBasesExtensionMethods
{
    public static class ExtensionMethods
    {
        internal static Core.Libraries.Permission _permission;
        public class DisposableBuilder : IDisposable, Pool.IPooled
        {
            private StringBuilder _builder;
            public DisposableBuilder() { }
            public void LeavePool() => _builder = Pool.Get<StringBuilder>();
            public void EnterPool() => Pool.FreeUnmanaged(ref _builder);
            public void Dispose() { DisposableBuilder obj = this; Pool.Free(ref obj); }
            public static DisposableBuilder Get() => Pool.Get<DisposableBuilder>();
            public DisposableBuilder Append(DisposableBuilder obj) { _builder.Append(obj._builder); return this; }
            public DisposableBuilder Append(string value) { _builder.Append(value); return this; }
            public DisposableBuilder AppendLine(string value = null) { if (value != null) _builder.AppendLine(value); else _builder.AppendLine(); return this; }
            public DisposableBuilder Replace(string oldValue, string newValue) { _builder.Replace(oldValue, newValue); return this; }
            public DisposableBuilder Clear() { _builder.Clear(); return this; }
            public override string ToString() => _builder.ToString();
            public int Length { get => _builder.Length; set => _builder.Length = value; }
        }
        public static string ToFriendlyJson(this string s) => string.IsNullOrEmpty(s) ? s : Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public static string FromFriendlyJson(this string s) => string.IsNullOrEmpty(s) ? s : Encoding.UTF8.GetString(Convert.FromBase64String((s.Replace('-', '+').Replace('_', '/')).PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
        public static PooledList<T> ToPooledList<T>(this IEnumerable<T> a) { var b = Facepunch.Pool.Get<PooledList<T>>(); if (a != null) b.AddRange(a); return b; }
        public static PooledList<T> TakePooledList<T>(this IEnumerable<T> a, int n) { var b = Facepunch.Pool.Get<PooledList<T>>(); if (a != null) { foreach (var d in a) { b.Add(d); if (b.Count >= n) { break; } } } return b; }
        public static PooledList<Item> GetAllItems(this BasePlayer a) { var b = Facepunch.Pool.Get<PooledList<Item>>(); if (a != null && a.inventory != null) { a.inventory.GetAllItems(b); } return b; }
        public static KeyValuePair<K, V> GetRandom<K, V>(this IDictionary<K, V> a) => a == null || a.Count == 0 ? default : a.ElementAt(UnityEngine.Random.Range(0, a.Count));
        public static bool All<T>(this IEnumerable<T> a, Func<T, bool> b) { foreach (T c in a) { if (!b(c)) { return false; } } return true; }
        public static int Average(this IList<int> a) { if (a.Count == 0) { return 0; } int b = 0; for (int i = 0; i < a.Count; i++) { b += a[i]; } return b != 0 ? b / a.Count : 0; }
        public static T ElementAt<T>(this IEnumerable<T> a, int b) { if (a is IList<T> c) { return c[b]; } using IEnumerator<T> d = a.GetEnumerator(); while (d.MoveNext()) { if (b == 0) { return d.Current; } b--; } return default; }
        public static bool Exists<T>(this HashSet<T> a) where T : BaseEntity { foreach (var b in a) { if (!b.IsKilled()) { return true; } } return false; }
        public static bool Exists<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using var c = a.GetEnumerator(); while (c.MoveNext()) { if (b == null || b(c.Current)) { return true; } } return false; }
        public static T FirstOrDefault<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == null || b(c.Current)) { return c.Current; } } } return default; }
        public static void ForEach<T>(this IEnumerable<T> a, Action<T> action) { foreach (T n in a) { action(n); } }
        public static int RemoveAll<TKey, TValue>(this IDictionary<TKey, TValue> c, Func<TKey, TValue, bool> d) { int a = 0; if (c.IsNullOrEmpty()) return a; using var e = c.ToPooledList(); foreach (var b in e) { if (d(b.Key, b.Value)) { c.Remove(b.Key); a++; } } return a; }
        public static IEnumerable<V> Select<T, V>(this IEnumerable<T> a, Func<T, V> b) { var c = new List<V>(); using (var d = a.GetEnumerator()) { while (d.MoveNext()) { c.Add(b(d.Current)); } } return c; }
        public static string[] Skip(this string[] a, int b) { if (a.Length == 0 || b >= a.Length) { return Array.Empty<string>(); } int n = a.Length - b; string[] c = new string[n]; Array.Copy(a, b, c, 0, n); return c; }
        public static Dictionary<T, V> ToDictionary<S, T, V>(this IEnumerable<S> a, Func<S, T> b, Func<S, V> c) { var d = new Dictionary<T, V>(); using (var e = a.GetEnumerator()) { while (e.MoveNext()) { d[b(e.Current)] = c(e.Current); } } return d; }
        public static List<T> ToList<T>(this IEnumerable<T> a) => new(a);
        public static List<T> Where<T>(this IEnumerable<T> a, Func<T, bool> b) { List<T> c = new(a is ICollection<T> n ? n.Count : 4); foreach (var d in a) { if (b(d)) { c.Add(d); } } return c; }
        public static List<T> OrderByAscending<T, TKey>(this IEnumerable<T> a, Func<T, TKey> s) { List<T> m = new(a); m.Sort((x, y) => Comparer<TKey>.Default.Compare(s(x), s(y))); return m; }
        public static int Sum<T>(this IEnumerable<T> a, Func<T, int> b) { int c = 0; foreach (T d in a) { c += b(d); } return c; }
        public static int Count<T>(this IEnumerable<T> a, Func<T, bool> b = null) { int c = 0; foreach (T d in a) { if (b == null || b(d)) { c++; } } return c; }
        public static IEnumerable<T> Union<T>(this IEnumerable<T> a, IEnumerable<T> b, IEqualityComparer<T> c = null) { HashSet<T> d = new(c); foreach (T e in a) { if (d.Add(e)) { yield return e; } } foreach (T f in b) { if (d.Add(f)) { yield return f; } } }
        public static bool HasPermission(this string a, string b) { _permission ??= Interface.Oxide.GetLibrary<Core.Libraries.Permission>(null); return !string.IsNullOrEmpty(a) && _permission.UserHasPermission(a, b); }
        public static bool HasPermission(this BasePlayer a, string b) => a != null && a.UserIDString.HasPermission(b);
        public static bool HasPermission(this ulong a, string b) => a.IsSteamId() && a.ToString().HasPermission(b);
        public static bool BelongsToGroup(this string a, string b) { _permission ??= Interface.Oxide.GetLibrary<Core.Libraries.Permission>(null); return !string.IsNullOrEmpty(a) && _permission.UserHasGroup(a, b); }
        public static bool BelongsToGroup(this ulong a, string b) => a.ToString().BelongsToGroup(b);
        public static bool BelongsToGroup(this BasePlayer a, string b) => a != null && a.UserIDString.BelongsToGroup(b);
        public static bool IsOnline(this BasePlayer a) => a.IsNetworked() && a.net.connection != null;
        public static bool IsKilled(this BaseNetworkable a) => a == null || a.IsDestroyed || !a.isSpawned;
        public static bool IsNull(this BaseNetworkable a) => a == null || a.IsDestroyed;
        public static bool IsNullOrEmpty<T>(this IReadOnlyCollection<T> c) => c == null || c.Count == 0;
        public static bool IsNetworked(this BaseNetworkable a) => !(a == null || a.IsDestroyed || !a.isSpawned || a.net == null);
        public static void SafelyKill(this BaseNetworkable a) { try { if (!a.IsKilled()) a.Kill(BaseNetworkable.DestroyMode.None); } catch { } }
        public static void DelayedSafeKill(this BaseNetworkable a) { if (!a.IsKilled()) a.Invoke(a.SafelyKill, 0.0625f); }
        public static bool CanCall(this Plugin o) => o != null && o.IsLoaded;
        public static bool IsHuman(this BasePlayer a) => a.userID.IsSteamId();
        public static bool IsCheating(this BasePlayer a) => a._limitedNetworking || a.IsFlying || a.UsedAdminCheat(30f) || a.IsGod() || a.metabolism?.calories?.min == 500;
        public static void SetAiming(this BasePlayer a, bool f) { a.modelState.aiming = f; a.SendNetworkUpdate(); }
        public static void SetNoTarget(this AutoTurret a) { if (a == null) return; a.SetTarget(null); a.target = null; }
        public static void SafelyStrip(this PlayerInventory inv) { if (inv == null) return; inv.containerMain?.Clear(); inv.containerWear?.Clear(); inv.containerBelt?.Clear(); ItemManager.DoRemoves(); }
        public static void SafelyRemove(this ItemContainer inv, string shortname) { if (inv == null) return; Item item = inv.FindItemByItemName(shortname); if (item == null) return; item.RemoveFromContainer(); item.Remove(); }
        public static BasePlayer Player(this IPlayer user) => user?.Object as BasePlayer;
        public static string MaterialName(this Collider collider) { try { return collider.sharedMaterial.name; } catch { return string.Empty; } }
        public static string ObjectName(this Collider collider) { try { return collider.name ?? string.Empty; } catch { return string.Empty; } }
        public static Vector3 GetPosition(this Collider collider) { try { return collider.transform.position; } catch { return Vector3.zero; } }
        public static string ObjectName(this BaseEntity entity) { try { return entity.name; } catch { return string.Empty; } }
        public static T GetRandom<T>(this HashSet<T> h) { if (h == null || h.Count == 0) { return default; } return h.ElementAt(UnityEngine.Random.Range(0, h.Count)); }
        public static float Distance(this Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Distance2D(this Vector3 a, Vector3 b) => (a.XZ2D() - b.XZ2D()).magnitude;
        public static ulong userid(this BasePlayer player) => (ulong)player.userID;
    }
}
