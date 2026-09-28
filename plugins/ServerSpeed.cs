using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Server Speed", "MrDoo", "1.6.0")]
    [Description("Ускоряет крафт в 50 раз")]
    public class ServerSpeed : RustPlugin
    {
        private PluginConfig _config;
        private readonly Dictionary<ItemBlueprint, float> _originalCraftTimes = new Dictionary<ItemBlueprint, float>();
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

            ApplyCraftingSpeed();

            _applied = true;
            Puts($"Server Speed загружен. Крафт: x{_config.CraftingSpeed}");
        }

        private void Unload()
        {
            if (!_applied) return;

            foreach (var kvp in _originalCraftTimes)
                if (kvp.Key != null) kvp.Key.time = kvp.Value;
        }

        private void ApplyCraftingSpeed()
        {
            if (_config.CraftingSpeed <= 0) return;

            foreach (var bp in ItemManager.bpList)
            {
                if (bp == null || bp.targetItem == null) continue;

                if (!_originalCraftTimes.ContainsKey(bp))
                    _originalCraftTimes[bp] = bp.time;

                bp.time = _originalCraftTimes[bp] / _config.CraftingSpeed;
                bp.ForceThisCraftTime = true;
            }
        }

        private class PluginConfig
        {
            public float CraftingSpeed { get; set; } = 50f;
        }
    }
}