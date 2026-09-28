using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Skill Levels", "MrDoo", "2.0.0")]
    [Description("RPG-прокачка с XP-бустерами, ивентами, daily-бонусами")]
    public class SkillLevels : RustPlugin
    {
        private PluginConfig _config;
        private readonly Dictionary<ulong, PlayerData> _data = new Dictionary<ulong, PlayerData>();
        private readonly HashSet<ulong> _lootedContainers = new HashSet<ulong>();
        private readonly List<Vector3> _boostZones = new List<Vector3>();
        private Timer _saveTimer;
        private const string PERM_ADMIN = "skilllevels.admin";

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

            if (_config.BoostZone.Enabled)
                CacheBoostZones();

            permission.RegisterPermission(PERM_ADMIN, this);
            cmd.AddChatCommand("skills", this, nameof(CmdSkills));
            _saveTimer = timer.Every(180f, SaveData);
            Puts($"Skill Levels v2 загружен. MaxLevel: {_config.MaxLevel}, MaxBonus: {_config.MaxBonusPercent}%");
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        // ===== Кэш зон буста =====
        private void CacheBoostZones()
        {
            foreach (var m in TerrainMeta.Path.Monuments)
            {
                if (m == null) continue;
                if (m.name.ToLower().Contains(_config.BoostZone.MonumentName.ToLower()))
                    _boostZones.Add(m.transform.position);
            }
            Puts($"[BoostZone] Найдено зон: {_boostZones.Count}");
        }

        // ===== Команда /skills =====
        private void CmdSkills(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                ShowSkills(player);
                return;
            }

            string sub = args[0].ToLower();

            // Админ-команды
            if (sub == "give" || sub == "reset" || sub == "set")
            {
                if (!permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
                {
                    SendReply(player, "<color=red>Нет прав.</color>");
                    return;
                }

                if (sub == "give" && args.Length >= 4)
                {
                    var target = FindPlayer(args[1]);
                    if (target == null) { SendReply(player, "Игрок не найден."); return; }
                    float amount;
                    if (!float.TryParse(args[3], out amount)) { SendReply(player, "XP должен быть числом."); return; }
                    AddXP(target, args[2], amount, true);
                    SendReply(player, $"Выдано {amount} XP в {args[2]} игроку {target.displayName}");
                }
                else if (sub == "reset" && args.Length >= 2)
                {
                    var target = FindPlayer(args[1]);
                    if (target == null) { SendReply(player, "Игрок не найден."); return; }
                    _data.Remove(target.userID);
                    SendReply(player, $"Прогресс {target.displayName} сброшен.");
                }
                else if (sub == "set" && args.Length >= 4)
                {
                    var target = FindPlayer(args[1]);
                    if (target == null) { SendReply(player, "Игрок не найден."); return; }
                    int lvl;
                    if (!int.TryParse(args[3], out lvl)) { SendReply(player, "Уровень должен быть числом."); return; }
                    if (!_data.TryGetValue(target.userID, out var d))
                        d = _data[target.userID] = new PlayerData();
                    if (!d.Skills.TryGetValue(args[2], out var s))
                        s = d.Skills[args[2]] = new SkillData();
                    s.Level = Mathf.Clamp(lvl, 0, _config.MaxLevel);
                    s.XP = 0;
                    SendReply(player, $"{target.displayName}: {args[2]} → уровень {s.Level}");
                }
                else
                {
                    SendReply(player, "Использование: /skills give <ник> <навык> <XP> | /skills reset <ник> | /skills set <ник> <навык> <уровень>");
                }
                return;
            }

            if (sub == "potion")
            {
                BuyPotion(player);
                return;
            }

            SendReply(player, "Использование: /skills | /skills potion");
        }

        private void ShowSkills(BasePlayer player)
        {
            if (!_data.TryGetValue(player.userID, out var data))
                data = _data[player.userID] = new PlayerData();

            UpdateStreak(player, data);

            SendReply(player, "<color=#FFA500>=== Твои навыки ===</color>");
            foreach (var skill in new[] { "Mining", "Woodcutting", "Skinning", "Looting" })
            {
                if (!data.Skills.TryGetValue(skill, out var s))
                    s = new SkillData();
                float bonus = GetBonusPercent(s.Level);
                float needed = GetRequiredXP(s.Level + 1);
                SendReply(player, $"<color=yellow>{skill}</color>: ур. {s.Level}/{_config.MaxLevel} | +{bonus:F1}% | XP: {s.XP:F0}/{needed:F0}");
            }

            // Активные баффы
            var buffs = new List<string>();
            if (data.PotionBoostEnd > DateTime.Now)
                buffs.Add($"XP Potion (+{_config.Potion.BoostPercent}%) до {data.PotionBoostEnd:HH:mm}");
            if (data.FirstHourEnd > DateTime.Now)
                buffs.Add($"Первый час дня (x{_config.Daily.FirstHourMultiplier})");
            if (IsWeekend() && _config.Weekend.Enabled)
                buffs.Add($"Выходной (x{_config.Weekend.Multiplier})");
            if (data.Streak >= _config.Streak.DaysRequired && _config.Streak.Enabled)
                buffs.Add($"Серия {data.Streak} дней (+{_config.Streak.BonusPercent}%)");
            if (IsInBoostZone(player))
                buffs.Add($"Зона буста (x{_config.BoostZone.Multiplier})");

            if (buffs.Count > 0)
            {
                SendReply(player, "<color=#00BFFF>=== Активные баффы ===</color>");
                foreach (var b in buffs) SendReply(player, $"  {b}");
            }

            if (data.Streak > 0)
                SendReply(player, $"<color=grey>Серия входов: {data.Streak} дн. подряд</color>");
        }

        // ===== Покупка XP Potion =====
        private void BuyPotion(BasePlayer player)
        {
            if (!_config.Potion.Enabled) { SendReply(player, "XP Potion отключён."); return; }

            int scrapHave = player.inventory.GetAmount(-932201673); // scrap id
            int canHave = player.inventory.GetAmount(-700591459); // can.tuna.empty id

            if (scrapHave < _config.Potion.ScrapCost || canHave < _config.Potion.EmptyCanCost)
            {
                SendReply(player, $"<color=red>Нужно: {_config.Potion.ScrapCost} скрапа и {_config.Potion.EmptyCanCost} пустую банку.</color>");
                return;
            }

            player.inventory.Take(null, -932201673, _config.Potion.ScrapCost);
            player.inventory.Take(null, -700591459, _config.Potion.EmptyCanCost);

            if (!_data.TryGetValue(player.userID, out var data))
                data = _data[player.userID] = new PlayerData();

            data.PotionBoostEnd = DateTime.Now.AddMinutes(_config.Potion.DurationMinutes);
            SendReply(player, $"<color=green>XP Potion активен! +{_config.Potion.BoostPercent}% XP на {_config.Potion.DurationMinutes} мин.</color>");
            SaveData();
        }

        // ===== Добыча =====
        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null) return;
            string sn = item.info.shortname;

            if (sn == "wood")
            {
                float xp = item.amount * _config.XPWoodcutting;
                AddXP(player, "Woodcutting", xp);
                ApplyResourceBonus(player, item, "Woodcutting");
                return;
            }

            if (sn == "stones" || sn == "metal.ore" || sn == "sulfur.ore" ||
                sn == "hq.metal.ore" || sn == "metal.fragments" || sn == "sulfur" || sn == "metal.refined")
            {
                float xp = item.amount * _config.XPMining;
                AddXP(player, "Mining", xp);
                ApplyResourceBonus(player, item, "Mining");
                return;
            }

            if (sn.Contains("meat") || sn == "leather" || sn == "bone.fragments" ||
                sn == "fat.animal" || sn == "cloth")
            {
                float xp = item.amount * _config.XPSkinning;
                AddXP(player, "Skinning", xp);
                ApplyResourceBonus(player, item, "Skinning");
            }
        }

        private void ApplyResourceBonus(BasePlayer player, Item item, string skill)
        {
            float bonus = GetBonusPercent(GetLevel(player.userID, skill)) / 100f;
            if (bonus <= 0) return;
            item.amount = Mathf.Max(1, (int)(item.amount * (1 + bonus)));
        }

        // ===== Лут =====
        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            var container = entity as LootContainer;
            if (container == null || container.net == null) return;

            ulong id = container.net.ID.Value;
            if (_lootedContainers.Contains(id)) return;
            _lootedContainers.Add(id);

            AddXP(player, "Looting", _config.XPLooting);

            // Бонус к содержимому
            float bonus = GetBonusPercent(GetLevel(player.userID, "Looting")) / 100f;
            if (bonus > 0 && container.inventory != null)
            {
                foreach (var it in container.inventory.itemList)
                {
                    if (it == null || it.info.stackable <= 1) continue;
                    int extra = Mathf.Max(1, (int)(it.amount * bonus));
                    it.amount = Mathf.Min(it.info.stackable, it.amount + extra);
                }
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net != null) _lootedContainers.Remove(entity.net.ID.Value);
        }

        // ===== Ивенты и убийства =====
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null) return;
            var attacker = info.InitiatorPlayer;

            if (entity is BaseAnimalNPC)
            {
                AddXP(attacker, "Skinning", 10f);
                return;
            }

            var npc = entity as BasePlayer;
            if (npc != null && npc.IsNpc)
            {
                string name = npc.displayName ?? "";

                // Босс
                if (name.Contains("BOSS") || name.Contains("Warlord"))
                {
                    string[] skills = { "Mining", "Woodcutting", "Skinning", "Looting" };
                    string random = skills[UnityEngine.Random.Range(0, skills.Length)];
                    AddXP(attacker, random, _config.EventXP.BossKill);
                    SendReply(attacker, $"<color=#FFD700>[Бонус] +{_config.EventXP.BossKill} XP в {random} за убийство босса!</color>");
                    return;
                }

                // Патрульный
                if (name.Contains("Road") || name.Contains("Patrol") || name.Contains("Scientist"))
                {
                    string[] skills = { "Mining", "Woodcutting", "Skinning", "Looting" };
                    string random = skills[UnityEngine.Random.Range(0, skills.Length)];
                    AddXP(attacker, random, _config.EventXP.PatrolKill);
                    return;
                }
            }
        }

        // ===== Аирдроп =====
        private void OnAirdrop(CargoPlane plane, Vector3 dropPosition)
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (Vector3.Distance(p.transform.position, dropPosition) < 100f)
                {
                    string[] skills = { "Mining", "Woodcutting", "Skinning", "Looting" };
                    string random = skills[UnityEngine.Random.Range(0, skills.Length)];
                    AddXP(p, random, _config.EventXP.Airdrop);
                    SendReply(p, $"<color=#FFD700>[Бонус] +{_config.EventXP.Airdrop} XP в {random} за близость к аирдропу!</color>");
                }
            }
        }

        // ===== Хук от SimpleArena =====
        private void OnArenaWaveComplete(BasePlayer player, int waveNumber)
        {
            if (player == null) return;
            string[] skills = { "Mining", "Woodcutting", "Skinning", "Looting" };
            string random = skills[UnityEngine.Random.Range(0, skills.Length)];
            AddXP(player, random, _config.EventXP.ArenaWave);
            SendReply(player, $"<color=#FFD700>[Бонус] +{_config.EventXP.ArenaWave} XP в {random} за волну {waveNumber}!</color>");
        }

        // ===== Логика XP =====
        private int GetLevel(ulong userId, string skill)
        {
            if (!_data.TryGetValue(userId, out var d)) return 0;
            if (!d.Skills.TryGetValue(skill, out var s)) return 0;
            return s.Level;
        }

        private void AddXP(BasePlayer player, string skill, float baseAmount, bool ignoreMultipliers = false)
        {
            if (baseAmount <= 0) return;
            if (!_data.TryGetValue(player.userID, out var data))
                data = _data[player.userID] = new PlayerData();
            if (!data.Skills.TryGetValue(skill, out var s))
                s = data.Skills[skill] = new SkillData();
            if (s.Level >= _config.MaxLevel) return;

            float final = ignoreMultipliers ? baseAmount : baseAmount * GetXPMultiplier(player, data);
            s.XP += final;

            while (s.Level < _config.MaxLevel)
            {
                float needed = GetRequiredXP(s.Level + 1);
                if (s.XP < needed) break;
                s.XP -= needed;
                s.Level++;

                if (_config.ShowLevelUpMessages && s.Level % _config.LevelUpMessageInterval == 0)
                {
                    float b = GetBonusPercent(s.Level);
                    SendReply(player, $"<color=green>[Навык]</color> {skill} → ур. {s.Level} (+{b:F1}%)");
                }
            }
        }

        private float GetXPMultiplier(BasePlayer player, PlayerData data)
        {
            float mult = 1f;

            // Potion
            if (_config.Potion.Enabled && data.PotionBoostEnd > DateTime.Now)
                mult *= 1 + _config.Potion.BoostPercent / 100f;

            // Первый час дня
            if (_config.Daily.Enabled && data.FirstHourEnd > DateTime.Now)
                mult *= _config.Daily.FirstHourMultiplier;

            // Выходные
            if (_config.Weekend.Enabled && IsWeekend())
                mult *= _config.Weekend.Multiplier;

            // Серия
            if (_config.Streak.Enabled && data.Streak >= _config.Streak.DaysRequired)
                mult *= 1 + _config.Streak.BonusPercent / 100f;

            // Зона буста
            if (_config.BoostZone.Enabled && IsInBoostZone(player))
                mult *= _config.BoostZone.Multiplier;

            return mult;
        }

        private bool IsWeekend()
        {
            var day = DateTime.Now.DayOfWeek;
            return day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
        }

        private bool IsInBoostZone(BasePlayer player)
        {
            if (_boostZones.Count == 0) return false;
            var pos = player.transform.position;
            foreach (var z in _boostZones)
                if (Vector3.Distance(pos, z) <= _config.BoostZone.Radius)
                    return true;
            return false;
        }

        private void UpdateStreak(BasePlayer player, PlayerData data)
        {
            if (!_config.Streak.Enabled) return;
            var today = DateTime.Now.Date;

            if (data.LastSeenDate == DateTime.MinValue)
            {
                data.Streak = 1;
                data.LastSeenDate = today;
                data.FirstHourEnd = DateTime.Now.AddHours(1);
                return;
            }

            if (data.LastSeenDate == today) return;

            if ((today - data.LastSeenDate).TotalDays <= 1.5)
                data.Streak++;
            else
                data.Streak = 1;

            data.LastSeenDate = today;
            data.FirstHourEnd = DateTime.Now.AddHours(1);
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!_data.TryGetValue(player.userID, out var data))
                data = _data[player.userID] = new PlayerData();
            UpdateStreak(player, data);
        }

        private float GetRequiredXP(int level) => _config.XPBase * level;

        private float GetBonusPercent(int level)
        {
            if (level <= 0) return 0;
            float bonus = level * _config.BonusPerLevel;
            return Mathf.Min(bonus, _config.MaxBonusPercent);
        }

        private BasePlayer FindPlayer(string name)
        {
            name = name.ToLower();
            foreach (var p in BasePlayer.activePlayerList)
                if (p.displayName.ToLower().Contains(name)) return p;
            return null;
        }

        // ===== Данные =====
        private void LoadData()
        {
            try
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>("SkillLevels");
                if (stored != null) foreach (var kvp in stored) _data[kvp.Key] = kvp.Value;
            }
            catch { PrintWarning("Не удалось загрузить данные"); }
        }

        private void SaveData()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject("SkillLevels", _data); }
            catch (Exception e) { PrintWarning($"Ошибка сохранения: {e.Message}"); }
        }

        // ===== Классы =====
        private class PlayerData
        {
            public Dictionary<string, SkillData> Skills { get; set; } = new Dictionary<string, SkillData>();
            public DateTime PotionBoostEnd { get; set; } = DateTime.MinValue;
            public DateTime FirstHourEnd { get; set; } = DateTime.MinValue;
            public DateTime LastSeenDate { get; set; } = DateTime.MinValue;
            public int Streak { get; set; } = 0;
        }

        private class SkillData
        {
            public int Level { get; set; } = 0;
            public float XP { get; set; } = 0;
        }

        private class PluginConfig
        {
            public int MaxLevel { get; set; } = 10000;
            public float BonusPerLevel { get; set; } = 0.5f;
            public float MaxBonusPercent { get; set; } = 10000f;
            public float XPBase { get; set; } = 1f;
            public bool ShowLevelUpMessages { get; set; } = true;
            public int LevelUpMessageInterval { get; set; } = 10;

            public float XPMining { get; set; } = 3f;
            public float XPWoodcutting { get; set; } = 3f;
            public float XPSkinning { get; set; } = 5f;
            public float XPLooting { get; set; } = 2f;

            public EventXPConfig EventXP { get; set; } = new EventXPConfig();
            public PotionConfig Potion { get; set; } = new PotionConfig();
            public BoostZoneConfig BoostZone { get; set; } = new BoostZoneConfig();
            public DailyConfig Daily { get; set; } = new DailyConfig();
            public WeekendConfig Weekend { get; set; } = new WeekendConfig();
            public StreakConfig Streak { get; set; } = new StreakConfig();
        }

        private class EventXPConfig
        {
            public int BossKill { get; set; } = 1000;
            public int ArenaWave { get; set; } = 500;
            public int Airdrop { get; set; } = 300;
            public int PatrolKill { get; set; } = 50;
        }

        private class PotionConfig
        {
            public bool Enabled { get; set; } = true;
            public int ScrapCost { get; set; } = 10;
            public int EmptyCanCost { get; set; } = 1;
            public float BoostPercent { get; set; } = 100f;
            public int DurationMinutes { get; set; } = 30;
        }

        private class BoostZoneConfig
        {
            public bool Enabled { get; set; } = true;
            public string MonumentName { get; set; } = "sphere_tank";
            public float Radius { get; set; } = 50f;
            public float Multiplier { get; set; } = 2f;
        }

        private class DailyConfig
        {
            public bool Enabled { get; set; } = true;
            public float FirstHourMultiplier { get; set; } = 2f;
        }

        private class WeekendConfig
        {
            public bool Enabled { get; set; } = true;
            public float Multiplier { get; set; } = 1.5f;
        }

        private class StreakConfig
        {
            public bool Enabled { get; set; } = true;
            public int DaysRequired { get; set; } = 5;
            public float BonusPercent { get; set; } = 10f;
        }
    }
}