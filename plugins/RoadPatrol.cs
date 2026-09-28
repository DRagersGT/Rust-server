using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Road Patrol", "MrDoo", "1.1.0")]
    [Description("Спавнит патрули учёных на дорогах, с лутом и ночным режимом")]
    public class RoadPatrol : RustPlugin
    {
        private PluginConfig _config;
        private Timer _timer;
        private readonly HashSet<BaseEntity> _spawned = new HashSet<BaseEntity>();
        private readonly Dictionary<ulong, DateTime> _lastSpawnTime = new Dictionary<ulong, DateTime>();

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null)
            {
                LoadDefaultConfig();
                _config = new PluginConfig();
            }

            _timer = timer.Every(_config.IntervalSeconds, TrySpawnPatrol);

            Puts($"Road Patrol загружен. Интервал: {_config.IntervalSeconds}с, за волну: {_config.PerWave} (ночью x{_config.NightMultiplier}), максимум живых: {_config.MaxAlive}");
        }

        private void Unload()
        {
            _timer?.Destroy();
            foreach (var e in _spawned)
            {
                if (e != null && !e.IsDestroyed)
                    e.Kill();
            }
            _spawned.Clear();
        }

        private bool IsNight()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null) return false;
            int hour = sky.Cycle.DateTime.Hour;
            return hour >= 19 || hour < 7;
        }

        private void TrySpawnPatrol()
        {
            _spawned.RemoveWhere(e => e == null || e.IsDestroyed);

            if (_spawned.Count >= _config.MaxAlive)
                return;

            var roads = TerrainMeta.Path?.Roads;
            if (roads == null || roads.Count == 0)
            {
                PrintWarning("Дороги на карте не найдены");
                return;
            }

            int perWave = _config.PerWave;
            if (IsNight() && _config.NightMultiplier > 1)
                perWave *= _config.NightMultiplier;

            for (int i = 0; i < perWave; i++)
            {
                if (_spawned.Count >= _config.MaxAlive) break;
                SpawnOnRandomRoad(roads);
            }
        }

        private void SpawnOnRandomRoad(List<PathList> roads)
        {
            var road = roads[UnityEngine.Random.Range(0, roads.Count)];
            if (road?.Path == null) return;

            var points = road.Path.Points;
            if (points == null || points.Length == 0) return;

            Vector3 pos = points[UnityEngine.Random.Range(0, points.Length)];
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.5f;

            var entity = GameManager.server.CreateEntity(_config.NpcPrefab, pos, Quaternion.identity);
            if (entity == null)
            {
                PrintWarning($"Не удалось создать NPC: {_config.NpcPrefab}");
                return;
            }

            entity.Spawn();
            _spawned.Add(entity);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            if (!_spawned.Contains(entity)) return;

            _spawned.Remove(entity);
            DropLoot(entity.transform.position);
        }

        private void DropLoot(Vector3 pos)
        {
            if (!_config.LootEnabled) return;

            // Всегда падает
            SpawnItem(pos, "ammo.rifle", UnityEngine.Random.Range(_config.AmmoMin, _config.AmmoMax + 1));
            SpawnItem(pos, "syringe.medical", UnityEngine.Random.Range(1, 4));
            if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
                SpawnItem(pos, "bandage", UnityEngine.Random.Range(1, 3));

            // 20% шанс
            if (UnityEngine.Random.Range(0f, 1f) < 0.2f)
                SpawnItem(pos, "largemedkit", 1);

            // 5% шанс — компоненты
            if (UnityEngine.Random.Range(0f, 1f) < 0.05f)
            {
                string[] comps = { "metal.fragments", "metal.refined", "cloth", "gears" };
                var comp = comps[UnityEngine.Random.Range(0, comps.Length)];
                SpawnItem(pos, comp, UnityEngine.Random.Range(10, 31));
            }

            // 1% шанс — оружие
            if (UnityEngine.Random.Range(0f, 1f) < 0.01f)
            {
                string[] guns = { "pistol.revolver", "pistol.semiauto", "shotgun.double", "smg.2" };
                var gun = guns[UnityEngine.Random.Range(0, guns.Length)];
                SpawnItem(pos, gun, 1);
            }
        }

        private void SpawnItem(Vector3 pos, string shortname, int amount)
        {
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            item.Drop(pos + Vector3.up * 0.5f, new Vector3(
                UnityEngine.Random.Range(-1f, 1f),
                UnityEngine.Random.Range(1f, 2f),
                UnityEngine.Random.Range(-1f, 1f)
            ));
        }

        private class PluginConfig
        {
            public float IntervalSeconds { get; set; } = 300f;
            public int PerWave { get; set; } = 5;
            public int MaxAlive { get; set; } = 40;
            public string NpcPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            public int NightMultiplier { get; set; } = 2;
            public bool LootEnabled { get; set; } = true;
            public int AmmoMin { get; set; } = 20;
            public int AmmoMax { get; set; } = 50;
        }
    }
}