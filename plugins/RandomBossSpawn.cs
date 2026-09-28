using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Random Boss Spawn", "MrDoo", "1.0.0")]
    [Description("Раз в час спавнит босса с охраной в случайной точке карты")]
    public class RandomBossSpawn : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;
        private Timer _spawnTimer;
        private BaseEntity _currentBoss;
        private readonly HashSet<BaseEntity> _bossGuards = new HashSet<BaseEntity>();
        private DateTime _lastSpawnTime = DateTime.MinValue;

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

            _spawnTimer = timer.Every(_config.IntervalSeconds, TrySpawnBoss);
            Puts($"Random Boss Spawn загружен. Интервал: {_config.IntervalSeconds}с, HP босса: {_config.BossHealth}, охрана: {_config.GuardCount}");
        }

        private void Unload()
        {
            _spawnTimer?.Destroy();
            if (_currentBoss != null && !_currentBoss.IsDestroyed) _currentBoss.Kill();
            foreach (var g in _bossGuards)
                if (g != null && !g.IsDestroyed) g.Kill();
            _bossGuards.Clear();
        }

        private void TrySpawnBoss()
        {
            if (_currentBoss != null && !_currentBoss.IsDestroyed) return;
            if ((DateTime.Now - _lastSpawnTime).TotalSeconds < _config.IntervalSeconds - 10) return;

            var pos = FindSpawnPosition();
            if (pos == null)
            {
                PrintWarning("Не удалось найти точку для босса");
                return;
            }

            var boss = SpawnNpc(pos.Value, _config.BossPrefab, "BOSS Warlord", _config.BossHealth);
            if (boss == null) return;

            _currentBoss = boss;
            _lastSpawnTime = DateTime.Now;

            // Охрана вокруг
            for (int i = 0; i < _config.GuardCount; i++)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(3f, 8f);
                var gx = pos.Value.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
                var gz = pos.Value.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);
                var gpos = new Vector3(gx, pos.Value.y, gz);
                gpos.y = TerrainMeta.HeightMap.GetHeight(gpos) + 0.5f;

                var guard = SpawnNpc(gpos, _config.GuardPrefab, "Elite Guard", _config.GuardHealth);
                if (guard != null) _bossGuards.Add(guard);
            }

            // Уведомление
            int gridX = (int)(pos.Value.x / 146.3f);
            int gridZ = (int)(pos.Value.z / 146.3f);
            char gridLetter = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int gridNum = Mathf.Clamp(gridZ + 1, 1, 99);

            foreach (var player in BasePlayer.activePlayerList)
            {
                SendReply(player,
                    $"<color=#8B0000>[БОСС]</color> <color=red>Warlord</color> появился в квадрате <color=yellow>{gridLetter}{gridNum}</color>! " +
                    $"({(int)pos.Value.x}, {(int)pos.Value.z}) — убей и получи {_config.RewardRP} RP!");
            }

            Puts($"Босс заспавнен в {pos.Value}");
        }

        private Vector3? FindSpawnPosition()
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                float size = TerrainMeta.Size.x;
                float margin = size * 0.15f;
                float x = UnityEngine.Random.Range(margin, size - margin);
                float z = UnityEngine.Random.Range(margin, size - margin);

                float y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
                if (y < 1f) continue;

                var pos = new Vector3(x, y + 0.5f, z);

                // Проверка: не в воде
                if (TerrainMeta.WaterMap.GetDepth(pos) > 0.5f) continue;

                return pos;
            }
            return null;
        }

        private BaseEntity SpawnNpc(Vector3 pos, string prefab, string name, float health)
        {
            var entity = GameManager.server.CreateEntity(prefab, pos, Quaternion.identity);
            if (entity == null) return null;
            entity.Spawn();

            var npc = entity as BasePlayer;
            if (npc != null)
            {
                npc.displayName = name;
                npc._maxHealth = health;
                npc.health = health;
                npc.SendNetworkUpdate();
            }
            return entity;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            bool isBoss = entity == _currentBoss;
            bool isGuard = _bossGuards.Contains(entity);
            if (!isBoss && !isGuard) return;

            var killer = info?.InitiatorPlayer;

            if (isBoss)
            {
                _currentBoss = null;
                if (killer != null)
                {
                    if (ServerRewards != null)
                        ServerRewards.Call("AddPoints", killer.userID, _config.RewardRP);
                    SendReply(killer, $"<color=green>[БОСС]</color> Ты убил Warlord! +{_config.RewardRP} RP");
                }
                foreach (var p in BasePlayer.activePlayerList)
                    SendReply(p, "<color=green>[БОСС]</color> Warlord повержен! Новый появится позже.");
                DropBossLoot(entity.transform.position);
            }
            else
            {
                _bossGuards.Remove(entity);
                DropGuardLoot(entity.transform.position);
            }
        }

        private void DropBossLoot(Vector3 pos)
        {
            if (!_config.LootEnabled) return;
            SpawnItem(pos, "ammo.rifle", UnityEngine.Random.Range(100, 200));
            SpawnItem(pos, "syringe.medical", 5);
            SpawnItem(pos, "largemedkit", 3);
            SpawnItem(pos, "metal.refined", UnityEngine.Random.Range(50, 150));
            SpawnItem(pos, "explosive.timed", 2);
            if (UnityEngine.Random.Range(0f, 1f) < 0.3f)
                SpawnItem(pos, "rifle.ak", 1);
            if (UnityEngine.Random.Range(0f, 1f) < 0.15f)
                SpawnItem(pos, "lmg.m249", 1);
        }

        private void DropGuardLoot(Vector3 pos)
        {
            if (!_config.LootEnabled) return;
            SpawnItem(pos, "ammo.rifle", UnityEngine.Random.Range(20, 50));
            SpawnItem(pos, "syringe.medical", 1);
            if (UnityEngine.Random.Range(0f, 1f) < 0.1f)
                SpawnItem(pos, "largemedkit", 1);
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
            public int IntervalSeconds { get; set; } = 3600;
            public float BossHealth { get; set; } = 2000f;
            public float GuardHealth { get; set; } = 300f;
            public int GuardCount { get; set; } = 3;
            public int RewardRP { get; set; } = 500;
            public bool LootEnabled { get; set; } = true;
            public string BossPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
            public string GuardPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab";
        }
    }
}