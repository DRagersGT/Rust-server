using System;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Action Reward", "MrDoo", "1.0.0")]
    [Description("Выдаёт RP за активные действия через ServerRewards")]
    public class ActionReward : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            if (ServerRewards == null)
                PrintWarning("ServerRewards не загружен — награды выдаваться не будут!");

            Puts($"Action Reward загружен. NPC: {_config.PerNpcKill} RP, боссы: {_config.PerBossKill} RP, животные: {_config.PerAnimalKill} RP");
        }

        private void GiveRP(BasePlayer player, int amount, string reason = null)
        {
            if (player == null || amount <= 0) return;
            if (ServerRewards == null) return;

            ServerRewards.Call("AddPoints", player.userID, amount);
            if (_config.ShowMessages && reason != null)
                SendReply(player, $"<color=green>[Награда]</color> +{amount} RP — {reason}");
        }

        // ===== Убийства =====
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            // NPC (учёные, патрули, боссы из плагинов)
            if (entity is BasePlayer npc && npc.IsNpc)
            {
                var name = npc.displayName ?? "";
                int reward;

                if (name.Contains("BOSS") || name.Contains("Warlord"))
                    reward = _config.PerBossKill;
                else if (name.Contains("Heavy") || name.Contains("Elite"))
                    reward = _config.PerBossKill / 4; // 50 по дефолту
                else
                    reward = _config.PerNpcKill;

                GiveRP(attacker, reward, "убийство NPC");
                return;
            }

            // Животные (медведь, волк и т.д.)
            if (entity is BaseAnimalNPC animal)
            {
                GiveRP(attacker, _config.PerAnimalKill, "убийство животного");
                return;
            }

            // Игроки (в PvE редко, но бывает)
            if (entity is BasePlayer victim && !victim.IsNpc && victim != attacker)
            {
                GiveRP(attacker, _config.PerPlayerKill, "убийство игрока");
            }
        }

        // ===== Добыча ресурсов =====
        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null) return;
            if (item.amount < _config.GatherAmountThreshold) return;

            int reward = item.amount / _config.GatherAmountThreshold * _config.PerGatherBatch;
            GiveRP(player, reward, "добыча ресурсов");
        }

        // ===== Разбор предметов =====
        private void OnItemRecycle(Item item, Recycler recycler)
        {
            if (item == null || recycler == null) return;
            var player = recycler.OwnerID != 0 ? BasePlayer.FindByID(recycler.OwnerID) : null;
            if (player == null) return;

            if (item.amount < _config.RecycleAmountThreshold) return;

            int reward = item.amount / _config.RecycleAmountThreshold * _config.PerRecycleBatch;
            GiveRP(player, reward, "разбор предметов");
        }

        // ===== Добыча из бочек / ящиков (опционально) =====
        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!_config.RewardForLoot) return;
            if (player == null || entity == null) return;
            if (entity is LootContainer container && container != null)
            {
                // Проверяем, что это не наш ящик с лутом (например, из ивентов)
                // Просто даём награду за открытие контейнера
                GiveRP(player, _config.PerLootOpen, "открытие ящика");
            }
        }

        private class PluginConfig
        {
            // Убийства
            public int PerNpcKill { get; set; } = 10;
            public int PerBossKill { get; set; } = 200;
            public int PerAnimalKill { get; set; } = 5;
            public int PerPlayerKill { get; set; } = 50;

            // Добыча
            public int GatherAmountThreshold { get; set; } = 100;
            public int PerGatherBatch { get; set; } = 1;

            // Разбор
            public int RecycleAmountThreshold { get; set; } = 5;
            public int PerRecycleBatch { get; set; } = 1;

            // Ящики
            public bool RewardForLoot { get; set; } = false; // По дефолту выключено
            public int PerLootOpen { get; set; } = 2;

            // Прочее
            public bool ShowMessages { get; set; } = true;
        }
    }
}