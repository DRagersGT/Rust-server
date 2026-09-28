using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Supply Drop Event", "MrDoo", "1.0.0")]
    [Description("Периодический ивент: самолёт + 3 ящика + NPC-охрана")]
    public class SupplyDropEvent : RustPlugin
    {
        private PluginConfig _config;
        private Timer _eventTimer;
        private readonly List<BaseEntity> _guards = new List<BaseEntity>();

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            _eventTimer = timer.Every(_config.IntervalSeconds, StartEvent);
            Puts($"Supply Drop Event загружен. Интервал: {_config.IntervalSeconds}с, ящиков: {_config.CrateCount}, охрана: {_config.GuardCount}");
        }

        private void Unload()
        {
            _eventTimer?.Destroy();
            foreach (var g in _guards)
                if (g != null && !g.IsDestroyed) g.Kill();
            _guards.Clear();
        }

        private void StartEvent()
        {
            if (BasePlayer.activePlayerList.Count == 0) return;

            var pos = FindDropPosition();
            if (pos == null) { PrintWarning("Не найдена точка для ивента"); return; }

            // Уведомление
            int gridX = (int)(pos.Value.x / 146.3f);
            int gridZ = (int)(pos.Value.z / 146.3f);
            char gridLetter = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int gridNum = Mathf.Clamp(gridZ + 1, 1, 99);

            foreach (var p in BasePlayer.activePlayerList)
                SendReply(p, $"<color=#FF4500>[Supply Drop]</color> Груз сброшен в квадрате <color=yellow>{gridLetter}{gridNum}</color>! Охрана на месте.");

            // Спавн ящиков
            for (int i = 0; i < _config.CrateCount; i++)
            {
                var offset = new Vector3(UnityEngine.Random.Range(-5f, 5f), 2f + i, UnityEngine.Random.Range(-5f, 5f));
                SpawnCrate(pos.Value + offset);
            }

            // Спавн охраны
            for (int i = 0; i < _config.GuardCount; i++)
                SpawnGuard(pos.Value);

            Puts($"Supply Drop Event: ивент в {pos.Value}");
        }

        private Vector3? FindDropPosition()
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                float size = TerrainMeta.Size.x;
                float margin = size * 0.15f;
                float x = UnityEngine.Random.Range(margin, size - margin);
                float z = UnityEngine.Random.Range(margin, size - margin);
                float y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
                if (y < 1f) continue;
                var pos = new Vector3(x, y + 1f, z);
                if (TerrainMeta.WaterMap.GetDepth(pos) > 0.5f) continue;
                return pos;
            }
            return null;
        }

        private void SpawnCrate(Vector3 pos)
        {
            var entity = GameManager.server.CreateEntity("assets/prefabs/misc/supply drop/supply_drop.prefab", pos, Quaternion.identity);
            if (entity == null) return;
            entity.Spawn();
        }

        private void SpawnGuard(Vector3 center)
        {
            var angle = UnityEngine.Random.Range(0f, 360f);
            var dist = UnityEngine.Random.Range(4f, 10f);
            var x = center.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
            var z = center.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);
            var pos = new Vector3(x, center.y, z);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.5f;

            var npc = GameManager.server.CreateEntity(_config.GuardPrefab, pos, Quaternion.identity);
            if (npc == null) return;
            npc.Spawn();

            var bp = npc as BasePlayer;
            if (bp != null)
            {
                bp.displayName = "Supply Guard";
                bp._maxHealth = _config.GuardHealth;
                bp.health = _config.GuardHealth;
                bp.SendNetworkUpdate();
            }
            _guards.Add(npc);
        }

        private class PluginConfig
        {
            public int IntervalSeconds { get; set; } = 2700; // 45 минут
            public int CrateCount { get; set; } = 3;
            public int GuardCount { get; set; } = 4;
            public float GuardHealth { get; set; } = 250f;
            public string GuardPrefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab";
        }
    }
}