using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Server Events", "MrDoo", "1.0.0")]
    [Description("Случайные PvE-события на карте: босс-конвой, осада монумента, ценный ящик")]
    public class ServerEvents : RustPlugin
    {
        private PluginConfig _config;
        private Timer _eventTimer;
        private readonly List<BaseEntity> _eventEntities = new List<BaseEntity>();
        private bool _eventActive;

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            _eventTimer = timer.Every(_config.IntervalSeconds, TryStartEvent);
            Puts($"Server Events загружен. Интервал: {_config.IntervalSeconds}с");
        }

        private void Unload()
        {
            _eventTimer?.Destroy();
            foreach (var e in _eventEntities)
                if (e != null && !e.IsDestroyed) e.Kill();
        }

        private void TryStartEvent()
        {
            if (_eventActive) return;
            if (BasePlayer.activePlayerList.Count < _config.MinPlayersOnline) return;

            int type = UnityEngine.Random.Range(0, 3);
            switch (type)
            {
                case 0: StartBossConvoy(); break;
                case 1: StartMonumentSiege(); break;
                case 2: StartLootCache(); break;
            }
        }

        // ===== Событие 1: Конвой с боссом =====
        private void StartBossConvoy()
        {
            var pos = FindEventPosition();
            if (pos == null) return;

            _eventActive = true;
            NotifyAll($"<color=#FF4500>[Событие]</color> <color=red>КОНВОЙ БОССА</color> замечен в квадрате {GetGrid(pos.Value)}! Летите быстрее!");

            // Босс
            var boss = SpawnNpc(pos.Value, _config.BossPrefab, "BOSS Convoy Leader", 2500f);
            if (boss != null) _eventEntities.Add(boss);

            // Охрана
            for (int i = 0; i < _config.BossGuardCount; i++)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(3f, 8f);
                var gp = new Vector3(
                    pos.Value.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad),
                    0,
                    pos.Value.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad));
                gp.y = TerrainMeta.HeightMap.GetHeight(gp) + 0.5f;

                var guard = SpawnNpc(gp, _config.GuardPrefab, "Convoy Guard", 400f);
                if (guard != null) _eventEntities.Add(guard);
            }

            Puts($"Событие: Конвой босса в {pos.Value}");
            timer.Once(_config.EventDurationSeconds, EndEvent);
        }

        // ===== Событие 2: Осада монумента =====
        private void StartMonumentSiege()
        {
            var monuments = TerrainMeta.Path.Monuments.Where(m => m != null).ToList();
            if (monuments.Count == 0) return;

            var monument = monuments[UnityEngine.Random.Range(0, monuments.Count)];
            var pos = monument.transform.position;

            _eventActive = true;
            NotifyAll($"<color=#FF4500>[Событие]</color> <color=orange>ОСАДА</color> монумента <color=yellow>{monument.displayPhrase.english}</color> в квадрате {GetGrid(pos)}! NPC атакуют!");

            for (int i = 0; i < _config.SiegeNpcCount; i++)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(10f, 30f);
                var np = new Vector3(
                    pos.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad),
                    0,
                    pos.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad));
                np.y = TerrainMeta.HeightMap.GetHeight(np) + 0.5f;

                string prefab = (i % 4 == 0) ? _config.BossPrefab : _config.GuardPrefab;
                float hp = (i % 4 == 0) ? 800f : 250f;
                string name = (i % 4 == 0) ? "Siege Leader" : "Siege Soldier";

                var npc = SpawnNpc(np, prefab, name, hp);
                if (npc != null) _eventEntities.Add(npc);
            }

            Puts($"Событие: Осада {monument.name} в {pos}");
            timer.Once(_config.EventDurationSeconds, EndEvent);
        }

        // ===== Событие 3: Ценный ящик =====
        private void StartLootCache()
        {
            var pos = FindEventPosition();
            if (pos == null) return;

            _eventActive = true;
            NotifyAll($"<color=#FF4500>[Событие]</color> <color=gold>ЦЕННЫЙ ЯЩИК</color> найден в квадрате {GetGrid(pos.Value)}! Охраняется NPC!");

            // Ящик
            var crate = GameManager.server.CreateEntity("assets/prefabs/misc/supply drop/supply_drop.prefab", pos.Value, Quaternion.identity);
            if (crate != null)
            {
                crate.Spawn();
                _eventEntities.Add(crate);
            }

            // Охрана
            for (int i = 0; i < _config.CacheGuardCount; i++)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(5f, 12f);
                var gp = new Vector3(
                    pos.Value.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad),
                    0,
                    pos.Value.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad));
                gp.y = TerrainMeta.HeightMap.GetHeight(gp) + 0.5f;

                var guard = SpawnNpc(gp, _config.GuardPrefab, "Cache Guard", 350f);
                if (guard != null) _eventEntities.Add(guard);
            }

            Puts($"Событие: Ценный ящик в {pos.Value}");
            timer.Once(_config.EventDurationSeconds, EndEvent);
        }

        // ===== Завершение =====
        private void EndEvent()
        {
            int killed = 0;
            foreach (var e in _eventEntities)
            {
                if (e != null && !e.IsDestroyed)
                {
                    e.Kill();
                    killed++;
                }
            }
            _eventEntities.Clear();
            _eventActive = false;

            if (killed > 0)
                NotifyAll($"<color=grey>[Событие] Ивент завершён.</color>");
            Puts($"Событие завершено, удалено сущностей: {killed}");
        }

        // ===== Хелперы =====
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

        private Vector3? FindEventPosition()
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
                if (TerrainMeta.WaterMap.GetDepth(pos) > 0.5f) continue;
                return pos;
            }
            return null;
        }

        private string GetGrid(Vector3 pos)
        {
            int gridX = (int)(pos.x / 146.3f);
            int gridZ = (int)(pos.z / 146.3f);
            char letter = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int num = Mathf.Clamp(gridZ + 1, 1, 99);
            return $"{letter}{num}";
        }

        private void NotifyAll(string msg)
        {
            foreach (var p in BasePlayer.activePlayerList)
                SendReply(p, msg);
        }

        // ===== Классы =====
        private class PluginConfig
        {
            public float IntervalSeconds { get; set; } = 7200f;    // 2 часа
            public int EventDurationSeconds { get; set; } = 1800;  // 30 минут
            public int MinPlayersOnline { get; set; } = 1;

            public string BossPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
            public string GuardPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab";

            public int BossGuardCount { get; set; } = 6;
            public int SiegeNpcCount { get; set; } = 8;
            public int CacheGuardCount { get; set; } = 5;
        }
    }
}