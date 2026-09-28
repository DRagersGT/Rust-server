using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Achievements", "MrDoo", "1.0.1")]
    [Description("Система достижений с наградой в RP")]
    public class Achievements : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;
        private Dictionary<ulong, PlayerAchievements> _data = new Dictionary<ulong, PlayerAchievements>();
        private Timer _saveTimer;
        private const string PERM_ADMIN = "achievements.admin";

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            LoadData();
            permission.RegisterPermission(PERM_ADMIN, this);
            cmd.AddChatCommand("achievements", this, nameof(CmdAchievements));
            _saveTimer = timer.Every(180f, SaveData);
            Puts($"Achievements загружен. Достижений: {_config.Achievements.Count}");
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        private void CmdAchievements(BasePlayer player, string command, string[] args)
        {
            if (args.Length > 0 && args[0].ToLower() == "reset" &&
                permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                _data.Remove(player.userID);
                SendReply(player, "Прогресс достижений сброшен.");
                return;
            }

            var data = GetOrCreate(player.userID);

            SendReply(player, "<color=#FFA500>=== Достижения ===</color>");
            foreach (var a in _config.Achievements)
            {
                bool unlocked = data.Unlocked.Contains(a.Id);
                string status = unlocked ? "<color=green>[✓]</color>" : "<color=yellow>[ ]</color>";
                string progress = unlocked ? "получено" : $"{GetProgress(data, a.Id)}/{a.TargetAmount}";
                SendReply(player, $"{status} {a.Name} — {progress} | {a.RewardRP} RP");
            }

            int total = _config.Achievements.Count;
            int done = data.Unlocked.Count;
            SendReply(player, $"<color=grey>Открыто: {done}/{total}</color>");
        }

        private void AddProgress(BasePlayer player, string achievementId, int amount)
        {
            var data = GetOrCreate(player.userID);
            if (data.Unlocked.Contains(achievementId)) return;

            var a = _config.Achievements.FirstOrDefault(x => x.Id == achievementId);
            if (a == null) return;

            if (!data.Progress.ContainsKey(achievementId))
                data.Progress[achievementId] = 0;
            data.Progress[achievementId] += amount;

            if (data.Progress[achievementId] >= a.TargetAmount)
            {
                data.Unlocked.Add(achievementId);
                ServerRewards?.Call("AddPoints", player.userID, a.RewardRP);
                SendReply(player, $"<color=gold>[Достижение]</color> {a.Name}! +{a.RewardRP} RP");
                SaveData();
            }
        }

        private int GetProgress(PlayerAchievements data, string id)
        {
            return data.Progress.TryGetValue(id, out var v) ? v : 0;
        }

        // ===== Хуки трекинга =====

        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null) return;

            if (item.info.shortname == "stones")
                AddProgress(player, "stone_100k", item.amount);
            else if (item.info.shortname == "wood")
                AddProgress(player, "wood_100k", item.amount);
            else if (item.info.shortname == "metal.ore" || item.info.shortname == "hqm.metal.ore")
                AddProgress(player, "metal_50k", item.amount);
            else if (item.info.shortname == "sulfur.ore")
                AddProgress(player, "sulfur_20k", item.amount);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null) return;
            var attacker = info.InitiatorPlayer;

            if (entity is BaseAnimalNPC)
                AddProgress(attacker, "hunter_100", 1);

            var npc = entity as BasePlayer;
            if (npc != null && npc.IsNpc)
            {
                string name = npc.displayName ?? "";
                if (name.Contains("BOSS") || name.Contains("Warlord"))
                    AddProgress(attacker, "boss_slayer_10", 1);
                AddProgress(attacker, "npc_hunter_500", 1);
            }
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            if (!(entity is LootContainer)) return;
            AddProgress(player, "looter_200", 1);
        }

        // ===== Данные =====
        private PlayerAchievements GetOrCreate(ulong userId)
        {
            if (!_data.TryGetValue(userId, out var d))
                d = _data[userId] = new PlayerAchievements();
            return d;
        }

        private void LoadData()
        {
            try
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerAchievements>>("Achievements");
                if (stored != null) _data = stored;
            }
            catch { PrintWarning("Не удалось загрузить данные достижений"); }
        }

        private void SaveData()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject("Achievements", _data); }
            catch (Exception e) { PrintWarning($"Ошибка сохранения: {e.Message}"); }
        }

        // ===== Классы =====
        private class PlayerAchievements
        {
            public List<string> Unlocked { get; set; } = new List<string>();
            public Dictionary<string, int> Progress { get; set; } = new Dictionary<string, int>();
        }

        private class AchievementTemplate
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int TargetAmount { get; set; }
            public int RewardRP { get; set; }
        }

        private class PluginConfig
        {
            public List<AchievementTemplate> Achievements { get; set; } = new List<AchievementTemplate>
            {
                new AchievementTemplate { Id = "stone_100k",     Name = "Каменотёс — 100 000 камня",     TargetAmount = 100000, RewardRP = 1000 },
                new AchievementTemplate { Id = "wood_100k",      Name = "Лесоруб — 100 000 дерева",     TargetAmount = 100000, RewardRP = 1000 },
                new AchievementTemplate { Id = "metal_50k",      Name = "Металлург — 50 000 руды",      TargetAmount = 50000,  RewardRP = 1500 },
                new AchievementTemplate { Id = "sulfur_20k",     Name = "Химик — 20 000 серы",          TargetAmount = 20000,  RewardRP = 1500 },
                new AchievementTemplate { Id = "hunter_100",     Name = "Охотник — 100 животных",       TargetAmount = 100,    RewardRP = 1000 },
                new AchievementTemplate { Id = "npc_hunter_500", Name = "Чистильщик — 500 NPC",         TargetAmount = 500,    RewardRP = 2500 },
                new AchievementTemplate { Id = "boss_slayer_10", Name = "Убийца боссов — 10 боссов",    TargetAmount = 10,     RewardRP = 3000 },
                new AchievementTemplate { Id = "looter_200",     Name = "Мародёр — 200 ящиков",         TargetAmount = 200,    RewardRP = 1000 }
            };
        }
    }
}