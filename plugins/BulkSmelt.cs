using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Bulk Smelt", "MrDoo", "1.0.1")]
    [Description("Увеличивает выход плавки: 1 руда = N металла")]
    public class BulkSmelt : RustPlugin
    {
        private PluginConfig _config;
        private readonly Dictionary<ItemModCookable, float> _originalAmounts = new Dictionary<ItemModCookable, float>();
        private bool _applied;

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            ApplyBulkSmelt();
            _applied = true;
            Puts($"Bulk Smelt загружен. Множитель выхода: x{_config.OutputMultiplier}");
        }

        private void Unload()
        {
            if (!_applied) return;
            foreach (var kvp in _originalAmounts)
                if (kvp.Key != null) kvp.Key.amountOfBecome = kvp.Value;
        }

        private void ApplyBulkSmelt()
        {
            if (_config.OutputMultiplier <= 1) return;

            foreach (var itemDef in ItemManager.itemList)
            {
                if (itemDef == null) continue;
                var cookable = itemDef.GetComponent<ItemModCookable>();
                if (cookable == null) continue;

                // Если список не пустой — применяем только к нему
                if (_config.ApplyToList != null && _config.ApplyToList.Count > 0)
                {
                    if (!_config.ApplyToList.Contains(itemDef.shortname)) continue;
                }

                if (!_originalAmounts.ContainsKey(cookable))
                    _originalAmounts[cookable] = cookable.amountOfBecome;

                cookable.amountOfBecome = _originalAmounts[cookable] * _config.OutputMultiplier;
            }
        }

        private class PluginConfig
        {
            public int OutputMultiplier { get; set; } = 100;

            // Пустой список = применить ко всем cooked-предметам
            public List<string> ApplyToList { get; set; } = new List<string>
            {
                "metal.ore",
                "sulfur.ore",
                "hq.metal.ore",
                "wood"
            };
        }
    }
}