using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Animal Invasion", "MrDoo", "1.0.1")]
    [Description("Периодические волны враждебных животных в одном регионе")]
    public class AnimalInvasion : RustPlugin
    {
        private PluginConfig _config;
        private Timer _invasionTimer;
        private readonly List<BaseEntity> _currentAnimals = new List<BaseEntity>();

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            _invasionTimer = timer.Every(_config.IntervalSeconds, StartInvasion);
            Puts($"Animal Invasion загружен. Интервал: {_config.IntervalSeconds}с, животных за волну: {_config.AnimalsPerWave}");
        }

        private void Unload()
        {
            _invasionTimer?.Destroy();
            foreach (var a in _currentAnimals)
                if (a != null && !a.IsDestroyed) a.Kill();
            _currentAnimals.Clear();
        }

        private void StartInvasion()
        {
            if (BasePlayer.activePlayerList.Count == 0) return;

            var center = FindInvasionCenter();
            if (center == null) { PrintWarning("Не найдена точка для инвазии"); return; }

            int gridX = (int)(center.Value.x / 146.3f);
            int gridZ = (int)(center.Value.z / 146.3f);
            char gridLetter = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int gridNum = Mathf.Clamp(gridZ + 1, 1, 99);

            foreach (var p in BasePlayer.activePlayerList)
                SendReply(p, $"<color=#8B0000>[Инвазия]</color> Волна животных в квадрате <color=yellow>{gridLetter}{gridNum}</color>! Берегись!");

            string[] animals = { "bear", "wolf", "polarbear", "boar" };
            for (int i = 0; i < _config.AnimalsPerWave; i++)
            {
                var animalName = animals[UnityEngine.Random.Range(0, animals.Length)];
                if (animalName == "polarbear" && !_config.AllowPolarBears) animalName = "bear";
                SpawnAnimal(animalName, center.Value);
            }

            Puts($"Animal Invasion: волна в {center.Value}");
        }

        private Vector3? FindInvasionCenter()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float size = TerrainMeta.Size.x;
                float margin = size * 0.15f;
                float x = UnityEngine.Random.Range(margin, size - margin);
                float z = UnityEngine.Random.Range(margin, size - margin);
                float y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
                if (y < 1f) continue;
                var pos = new Vector3(x, y, z);
                if (TerrainMeta.WaterMap.GetDepth(pos) > 0.5f) continue;
                return pos;
            }
            return null;
        }

        private void SpawnAnimal(string name, Vector3 center)
        {
            var angle = UnityEngine.Random.Range(0f, 360f);
            var dist = UnityEngine.Random.Range(5f, _config.SpawnRadius);
            var x = center.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
            var z = center.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);
            var pos = new Vector3(x, center.y, z);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.5f;

            string prefab;
            switch (name)
            {
                case "polarbear":
                    prefab = "assets/rust.ai/agents/bear/polarbear.prefab";
                    break;
                case "bear":
                    prefab = "assets/rust.ai/agents/bear/bear.prefab";
                    break;
                case "wolf":
                    prefab = "assets/rust.ai/agents/wolf/wolf.prefab";
                    break;
                case "boar":
                    prefab = "assets/rust.ai/agents/boar/boar.prefab";
                    break;
                default:
                    prefab = $"assets/rust.ai/agents/{name}/{name}.prefab";
                    break;
            }

            var entity = GameManager.server.CreateEntity(prefab, pos, Quaternion.identity);
            if (entity == null)
            {
                PrintWarning($"Не удалось создать животное: {prefab}");
                return;
            }
            entity.Spawn();
            _currentAnimals.Add(entity);
            Puts($"[Animal Invasion] Спавн: {name} в {pos}");
        }

        private class PluginConfig
        {
            public int IntervalSeconds { get; set; } = 2400;
            public int AnimalsPerWave { get; set; } = 6;
            public float SpawnRadius { get; set; } = 15f;
            public bool AllowPolarBears { get; set; } = true;
        }
    }
}