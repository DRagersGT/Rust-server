using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Daily Quests", "MrDoo", "1.0.0")]
    [Description("3 ежедневных задания с наградой в RP")]
    public class DailyQuests : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;
        private Dictionary<ulong, PlayerQuestData> _data = new Dictionary<ulong, PlayerQuestData>();
        private Timer _saveTimer;
        private const string PERM_ADMIN = "dailyquests.admin";

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
            cmd.AddChatCommand("quests", this, nameof(CmdQuests));
            _saveTimer = timer.Every(180f, SaveData);
            Puts($"Daily Quests загружен. Заданий в день: {_config.QuestsPerDay}");
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        // ===== Команда /quests =====
        private void CmdQuests(BasePlayer player, string command, string[] args)
        {
            if (args.Length > 0 && args[0].ToLower() == "reset" &&
                permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                _data.Remove(player.userID);
                SendReply(player, "Прогресс заданий сброшен.");
                return;
            }

            var data = GetOrCreate(player.userID);

            // Проверка нового дня
            if (data.LastDay != DateTime.Now.Date)
                GenerateNewQuests(data, player);

            SendReply(player, "<color=#FFA500>=== Ежедневные задания ===</color>");
            for (int i = 0; i < data.Quests.Count; i++)
            {
                var q = data.Quests[i];
                string status = q.Completed ? "<color=green>[✓]</color>" : "<color=yellow>[ ]</color>";
                string progress = q.Completed ? "выполнено" : $"{q.CurrentProgress}/{q.TargetAmount}";
                SendReply(player, $"{status} {q.Description} — {progress} | Награда: {q.RewardRP} RP");
            }

            int completed = data.Quests.Count(q => q.Completed);
            SendReply(player, $"<color=grey>Выполнено: {completed}/{data.Quests.Count}</color>");
        }

        private PlayerQuestData GetOrCreate(ulong userId)
        {
            if (!_data.TryGetValue(userId, out var d))
            {
                d = new PlayerQuestData();
                d.LastDay = DateTime.Now.Date;
                _data[userId] = d;
            }
            return d;
        }

        // ===== Генерация квестов =====
        private void GenerateNewQuests(PlayerQuestData data, BasePlayer player)
        {
            data.LastDay = DateTime.Now.Date;
            data.Quests.Clear();

            var pool = new List<QuestTemplate>(_config.QuestPool);
            for (int i = 0; i < _config.QuestsPerDay && pool.Count > 0; i++)
            {
                int idx = UnityEngine.Random.Range(0, pool.Count);
                var template = pool[idx];
                pool.RemoveAt(idx);

                int amount = UnityEngine.Random.Range(template.MinAmount, template.MaxAmount + 1);
                data.Quests.Add(new Quest
                {
                    Type = template.Type,
                    Shortname = template.Shortname,
                    Description = template.Description.Replace("{N}", amount.ToString()),
                    TargetAmount = amount,
                    CurrentProgress = 0,
                    RewardRP = template.RewardRP,
                    Completed = false
                });
            }

            SendReply(player, "<color=#FFA500>[Квесты]</color> Получены новые ежедневные задания! Введи /quests");
            SaveData();
        }

        // ===== Прогресс — добыча =====
        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null) return;
            AddProgress(player, "gather", item.info.shortname, item.amount);
        }

        // ===== Прогресс — убийства NPC/животных =====
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null) return;
            var attacker = info.InitiatorPlayer;

            if (entity is BaseAnimalNPC)
                AddProgress(attacker, "kill_animal", null, 1);

            var npc = entity as BasePlayer;
            if (npc != null && npc.IsNpc)
                AddProgress(attacker, "kill_npc", null, 1);
        }

        // ===== Прогресс — лут =====
        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            if (!(entity is LootContainer)) return;
            AddProgress(player, "loot", null, 1);
        }

        private void AddProgress(BasePlayer player, string type, string shortname, int amount)
        {
            var data = GetOrCreate(player.userID);
            if (data.LastDay != DateTime.Now.Date)
                GenerateNewQuests(data, player);

            bool changed = false;
            foreach (var q in data.Quests)
            {
                if (q.Completed) continue;
                if (q.Type != type) continue;
                if (type == "gather" && !string.IsNullOrEmpty(q.Shortname) && q.Shortname != shortname) continue;

                q.CurrentProgress += amount;
                if (q.CurrentProgress >= q.TargetAmount)
                {
                    q.Completed = true;
                    CompleteQuest(player, q);
                }
                changed = true;
            }

            if (changed) SaveData();
        }

        private void CompleteQuest(BasePlayer player, Quest q)
        {
            ServerRewards?.Call("AddPoints", player.userID, q.RewardRP);
            SendReply(player, $"<color=green>[Квест]</color> Выполнено: {q.Description}! Награда: {q.RewardRP} RP");

            // Проверка всех выполненных
            var data = GetOrCreate(player.userID);
            if (data.Quests.All(x => x.Completed) && _config.AllQuestsBonusRP > 0)
            {
                ServerRewards?.Call("AddPoints", player.userID, _config.AllQuestsBonusRP);
                SendReply(player, $"<color=gold>[Квест]</color> Все задания выполнены! Бонус: {_config.AllQuestsBonusRP} RP");
            }
        }

        // ===== Данные =====
        private void LoadData()
        {
            try
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerQuestData>>("DailyQuests");
                if (stored != null) _data = stored;
            }
            catch { PrintWarning("Не удалось загрузить данные квестов"); }
        }

        private void SaveData()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject("DailyQuests", _data); }
            catch (Exception e) { PrintWarning($"Ошибка сохранения: {e.Message}"); }
        }

        // ===== Классы =====
        private class PlayerQuestData
        {
            public DateTime LastDay { get; set; } = DateTime.MinValue;
            public List<Quest> Quests { get; set; } = new List<Quest>();
        }

        private class Quest
        {
            public string Type { get; set; }           // gather / kill_npc / kill_animal / loot
            public string Shortname { get; set; }      // для gather
            public string Description { get; set; }
            public int TargetAmount { get; set; }
            public int CurrentProgress { get; set; }
            public int RewardRP { get; set; }
            public bool Completed { get; set; }
        }

        private class QuestTemplate
        {
            public string Type { get; set; }
            public string Shortname { get; set; }
            public string Description { get; set; }
            public int MinAmount { get; set; }
            public int MaxAmount { get; set; }
            public int RewardRP { get; set; }
        }

        private class PluginConfig
        {
            public int QuestsPerDay { get; set; } = 3;
            public int AllQuestsBonusRP { get; set; } = 500;

            public List<QuestTemplate> QuestPool { get; set; } = new List<QuestTemplate>
            {
                new QuestTemplate { Type = "gather", Shortname = "stones",        Description = "Добудь {N} камня",           MinAmount = 3000, MaxAmount = 8000, RewardRP = 100 },
                new QuestTemplate { Type = "gather", Shortname = "wood",          Description = "Сруби {N} дерева",           MinAmount = 3000, MaxAmount = 8000, RewardRP = 100 },
                new QuestTemplate { Type = "gather", Shortname = "metal.ore",     Description = "Добудь {N} железной руды",   MinAmount = 1500, MaxAmount = 5000, RewardRP = 150 },
                new QuestTemplate { Type = "gather", Shortname = "sulfur.ore",    Description = "Добудь {N} серы",            MinAmount = 1000, MaxAmount = 3000, RewardRP = 150 },
                new QuestTemplate { Type = "gather", Shortname = "hq.metal.ore",  Description = "Добудь {N} HQM руды",        MinAmount = 500,  MaxAmount = 1500, RewardRP = 200 },
                new QuestTemplate { Type = "kill_animal",                          Description = "Убей {N} животных",          MinAmount = 10,   MaxAmount = 25,   RewardRP = 100 },
                new QuestTemplate { Type = "kill_npc",                             Description = "Убей {N} NPC",               MinAmount = 20,   MaxAmount = 50,   RewardRP = 200 },
                new QuestTemplate { Type = "loot",                                 Description = "Открой {N} ящиков",          MinAmount = 15,   MaxAmount = 40,   RewardRP = 100 }
            };
        }
    }
}