using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Monument NPC", "MrDoo", "2.0.0")]
    [Description("Автоматически спавнит группы NPC на монументах")]
    public class MonumentNPC : RustPlugin
    {
        private PluginConfig _config;
        private Timer _spawnTimer;
        private readonly Dictionary<string, List<BaseEntity>> _spawnedNpcs = new Dictionary<string, List<BaseEntity>>();
        private const string PERM_ADMIN = "monumentnpc.admin";

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            permission.RegisterPermission(PERM_ADMIN, this);
            cmd.AddChatCommand("monumentnpc", this, nameof(CmdMonumentNpc));

            _spawnTimer = timer.Every(_config.IntervalSeconds, SpawnAll);
            Puts($"Monument NPC загружен. Автоматический режим: {_config.AutoMonuments}, интервал: {_config.IntervalSeconds}с");
        }

        private void Unload()
        {
            _spawnTimer?.Destroy();
            foreach (var kvp in _spawnedNpcs)
                foreach (var npc in kvp.Value)
                    if (npc != null && !npc.IsDestroyed) npc.Kill();
        }

        private void CmdMonumentNpc(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                SendReply(player, "Нет прав.");
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, "Использование: /monumentnpc add <имя> | remove <имя> | list | spawn");
                return;
            }

            string sub = args[0].ToLower();

            if (sub == "add" && args.Length >= 2)
            {
                string name = args[1];
                var pos = player.transform.position;
                _config.ManualSpawnPoints[name] = $"{pos.x:F2} {pos.y:F2} {pos.z:F2}";
                Config.WriteObject(_config, true);
                SendReply(player, $"Точка '{name}' добавлена.");
                return;
            }

            if (sub == "remove" && args.Length >= 2)
            {
                string name = args[1];
                if (_config.ManualSpawnPoints.Remove(name))
                {
                    Config.WriteObject(_config, true);
                    SendReply(player, $"Точка '{name}' удалена.");
                }
                else SendReply(player, $"Точка '{name}' не найдена.");
                return;
            }

            if (sub == "list")
            {
                SendReply(player, "=== Автоматические монументы ===");
                foreach (var m in TerrainMeta.Path.Monuments)
                {
                    if (m == null) continue;
                    string name = m.name.ToLower();
                    if (_config.BlacklistedMonuments.Contains(name)) continue;
                    SendReply(player, $"  {m.displayPhrase.english} ({name})");
                }
                SendReply(player, "=== Ручные точки ===");
                foreach (var kvp in _config.ManualSpawnPoints)
                    SendReply(player, $"  {kvp.Key}: {kvp.Value}");
                return;
            }

            if (sub == "spawn")
            {
                SpawnAll();
                SendReply(player, "Спавн запущен вручную.");
                return;
            }

            SendReply(player, "Неизвестная команда.");
        }

        private void SpawnAll()
        {
            if (_config.AutoMonuments)
                foreach (var monument in TerrainMeta.Path.Monuments)
                    SpawnAtMonument(monument);

            foreach (var kvp in _config.ManualSpawnPoints)
                SpawnAtPoint(kvp.Key, kvp.Value);
        }

        private void SpawnAtMonument(MonumentInfo monument)
        {
            if (monument == null) return;
            string name = monument.name.ToLower();
            if (_config.BlacklistedMonuments.Contains(name)) return;

            string key = $"monument_{monument.transform.position}";
            if (!_spawnedNpcs.ContainsKey(key))
                _spawnedNpcs[key] = new List<BaseEntity>();

            _spawnedNpcs[key].RemoveAll(e => e == null || e.IsDestroyed);

            int totalComposition = _config.Composition.Sum(c => c.Count);
            if (_spawnedNpcs[key].Count >= totalComposition) return;

            var center = monument.transform.position;
            foreach (var entry in _config.Composition)
            {
                int alive = _spawnedNpcs[key].Count(n => n != null && !n.IsDestroyed && (n as BasePlayer)?.displayName == entry.Name);
                int toSpawn = entry.Count - alive;
                for (int i = 0; i < toSpawn; i++)
                {
                    var pos = GetRandomPointAround(center, _config.SpawnRadius);
                    var npc = SpawnNpc(pos, entry);
                    if (npc != null) _spawnedNpcs[key].Add(npc);
                }
            }
        }

        private void SpawnAtPoint(string pointName, string coords)
        {
            string key = $"manual_{pointName}";
            if (!_spawnedNpcs.ContainsKey(key))
                _spawnedNpcs[key] = new List<BaseEntity>();

            _spawnedNpcs[key].RemoveAll(e => e == null || e.IsDestroyed);

            int totalComposition = _config.Composition.Sum(c => c.Count);
            if (_spawnedNpcs[key].Count >= totalComposition) return;

            var parts = coords.Split(' ');
            if (parts.Length != 3) return;
            Vector3 center;
            try { center = new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2])); }
            catch { return; }

            foreach (var entry in _config.Composition)
            {
                int alive = _spawnedNpcs[key].Count(n => n != null && !n.IsDestroyed && (n as BasePlayer)?.displayName == entry.Name);
                int toSpawn = entry.Count - alive;
                for (int i = 0; i < toSpawn; i++)
                {
                    var pos = GetRandomPointAround(center, _config.SpawnRadius);
                    var npc = SpawnNpc(pos, entry);
                    if (npc != null) _spawnedNpcs[key].Add(npc);
                }
            }
        }

        private Vector3 GetRandomPointAround(Vector3 center, float radius)
        {
            var angle = UnityEngine.Random.Range(0f, 360f);
            var dist = UnityEngine.Random.Range(3f, radius);
            var x = center.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
            var z = center.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);
            var pos = new Vector3(x, center.y, z);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.5f;
            return pos;
        }

        private BaseEntity SpawnNpc(Vector3 pos, NpcEntry entry)
        {
            var entity = GameManager.server.CreateEntity(entry.Prefab, pos, Quaternion.identity);
            if (entity == null) return null;
            entity.Spawn();

            var bp = entity as BasePlayer;
            if (bp != null)
            {
                bp.displayName = entry.Name;
                bp._maxHealth = entry.Health;
                bp.health = entry.Health;
                bp.SendNetworkUpdate();
            }
            return entity;
        }

        private class PluginConfig
        {
            public bool AutoMonuments { get; set; } = true;
            public float IntervalSeconds { get; set; } = 300f;
            public float SpawnRadius { get; set; } = 20f;
            public List<string> BlacklistedMonuments { get; set; } = new List<string>
            {
                "outpost", "bandit_town", "lighthouse", "oilrig_1", "oilrig_2", "excavator", "launch_site"
            };
            public List<NpcEntry> Composition { get; set; } = new List<NpcEntry>
            {
                new NpcEntry { Prefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab", Count = 1, Name = "Juggernaut", Health = 1000f },
                new NpcEntry { Prefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab", Count = 4, Name = "Spec Ops", Health = 300f },
                new NpcEntry { Prefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab", Count = 2, Name = "Scientist", Health = 150f }
            };
            public Dictionary<string, string> ManualSpawnPoints { get; set; } = new Dictionary<string, string>();
        }

        private class NpcEntry
        {
            public string Prefab { get; set; }
            public int Count { get; set; }
            public string Name { get; set; }
            public float Health { get; set; }
        }
    }
}