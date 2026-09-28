using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Base Upgrades", "MrDoo", "2.2.0")]
    [Description("Улучшения базы за RP: слоты ящиков, автопополнение печей, бесплатный ресёрч, защита от decay")]
    public class BaseUpgrades : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;
        private Dictionary<ulong, PlayerUpgrades> _data = new Dictionary<ulong, PlayerUpgrades>();
        private Timer _saveTimer;
        private Timer _autoFillTimer;
        private const string PERM_ADMIN = "baseupgrades.admin";

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
            cmd.AddChatCommand("upgrades", this, nameof(CmdUpgrades));

            _saveTimer = timer.Every(180f, SaveData);
            _autoFillTimer = timer.Every(_config.AutoFill.IntervalSeconds, AutoFillOvens);

            Puts($"Base Upgrades загружен. Улучшений: 4");
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            _autoFillTimer?.Destroy();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        private void CmdUpgrades(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { ShowMenu(player); return; }

            string sub = args[0].ToLower();

            if (sub == "buy" && args.Length >= 2) { BuyUpgrade(player, args[1]); return; }

            if (sub == "reset")
            {
                _data.Remove(player.userID);
                SendReply(player, "Все улучшения сброшены.");
                SaveData();
                return;
            }

            SendReply(player, "Использование: /upgrades | /upgrades buy <название> | /upgrades reset");
        }

        private void ShowMenu(BasePlayer player)
        {
            var u = GetOrCreate(player.userID);
            int rp = GetPlayerRP(player);

            SendReply(player, $"<color=#FFA500>=== Улучшения базы ===</color> <color=grey>(RP: {rp})</color>");
            SendReply(player, $"<color=yellow>more_slots</color> (ур. {u.MoreSlotsLevel}/{_config.MoreSlots.MaxLevel}) — +{_config.MoreSlots.SlotsPerLevel} слотов в больших ящиках | След.: {GetPrice("more_slots", u.MoreSlotsLevel + 1)} RP");
            SendReply(player, $"<color=yellow>auto_fill</color> (ур. {u.AutoFillLevel}/{_config.AutoFill.MaxLevel}) — автопополнение печей углём | След.: {GetPrice("auto_fill", u.AutoFillLevel + 1)} RP");
            SendReply(player, $"<color=yellow>research_free</color> (ур. {u.ResearchFreeLevel}/{_config.ResearchFree.MaxLevel}) — ресёрч без скрапа | След.: {GetPrice("research_free", u.ResearchFreeLevel + 1)} RP");
            SendReply(player, $"<color=yellow>no_decay</color> (ур. {u.NoDecayLevel}/{_config.NoDecay.MaxLevel}) — постройки не гниют | След.: {GetPrice("no_decay", u.NoDecayLevel + 1)} RP");
            SendReply(player, "<color=grey>Купить: /upgrades buy <название> | Сброс: /upgrades reset");
        }

        private int GetPrice(string type, int nextLevel)
        {
            switch (type)
            {
                case "more_slots":     return _config.MoreSlots.BasePrice    * nextLevel;
                case "auto_fill":      return _config.AutoFill.BasePrice     * nextLevel;
                case "research_free":  return _config.ResearchFree.BasePrice * nextLevel;
                case "no_decay":       return _config.NoDecay.BasePrice      * nextLevel;
                default: return 999999;
            }
        }

        private void BuyUpgrade(BasePlayer player, string type)
        {
            var u = GetOrCreate(player.userID);
            int nextLevel, price, maxLevel;

            switch (type)
            {
                case "more_slots":    nextLevel = u.MoreSlotsLevel + 1;    maxLevel = _config.MoreSlots.MaxLevel;    break;
                case "auto_fill":     nextLevel = u.AutoFillLevel + 1;     maxLevel = _config.AutoFill.MaxLevel;     break;
                case "research_free": nextLevel = u.ResearchFreeLevel + 1; maxLevel = _config.ResearchFree.MaxLevel; break;
                case "no_decay":      nextLevel = u.NoDecayLevel + 1;      maxLevel = _config.NoDecay.MaxLevel;      break;
                default: SendReply(player, "Неизвестное улучшение."); return;
            }

            price = GetPrice(type, nextLevel);

            if (nextLevel > maxLevel)
            {
                SendReply(player, "<color=red>Максимальный уровень достигнут.</color>");
                return;
            }

            int balance = GetPlayerRP(player);
            if (balance < price)
            {
                SendReply(player, $"<color=red>Не хватает RP. Нужно: {price}, у тебя: {balance}</color>");
                return;
            }

            if (!TakeRP(player, price))
            {
                SendReply(player, "<color=red>Ошибка списания RP.</color>");
                return;
            }

            switch (type)
            {
                case "more_slots":    u.MoreSlotsLevel++;    break;
                case "auto_fill":     u.AutoFillLevel++;     break;
                case "research_free": u.ResearchFreeLevel++; break;
                case "no_decay":      u.NoDecayLevel++;      break;
            }

            if (type == "more_slots")
                UpdatePlayerBoxes(player, u.MoreSlotsLevel);

            SendReply(player, $"<color=green>Куплено: {type} → уровень {nextLevel}/{maxLevel}.</color>");
            SaveData();
        }

        // ===== Обновление всех больших ящиков игрока =====
        private void UpdatePlayerBoxes(BasePlayer player, int level)
        {
            int extraSlots = level * _config.MoreSlots.SlotsPerLevel;
            int newCapacity = _config.MoreSlots.BaseCapacity + extraSlots;

            int updated = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var container = entity as StorageContainer;
                if (container == null || container.IsDestroyed) continue;
                if (container.ShortPrefabName != "box.wooden.large") continue;
                if (container.OwnerID != player.userID) continue;

                var inv = container.inventory;
                if (inv == null) continue;

                inv.capacity = newCapacity;
                inv.MarkDirty();
                container.SendNetworkUpdate();
                updated++;
            }

            SendReply(player, $"<color=green>[Улучшение]</color> Обновлено больших ящиков: {updated}, теперь слотов: {newCapacity}");
        }

        // ===== RP =====
        private int GetPlayerRP(BasePlayer player)
        {
            if (ServerRewards == null) return 0;
            var result = ServerRewards.Call("CheckPoints", player.userID);
            if (result is int i) return i;
            return 0;
        }

        private bool TakeRP(BasePlayer player, int amount)
        {
            if (ServerRewards == null) return false;
            var result = ServerRewards.Call("TakePoints", player.userID, amount);
            return result is bool b && b;
        }

        // ===== 1. more_slots — вместимость большого ящика при постройке =====
        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var entity = go?.ToBaseEntity();
            if (entity == null) return;
            var owner = plan.GetOwnerPlayer();
            if (owner == null) return;
            var u = GetOrCreate(owner.userID);
            if (u.MoreSlotsLevel == 0) return;

            var container = entity as StorageContainer;
            if (container == null) return;
            if (container.ShortPrefabName != "box.wooden.large") return;

            int extraSlots = u.MoreSlotsLevel * _config.MoreSlots.SlotsPerLevel;
            int newCapacity = _config.MoreSlots.BaseCapacity + extraSlots;
            container.inventory.capacity = newCapacity;
            container.inventory.MarkDirty();
            container.SendNetworkUpdate();
            SendReply(owner, $"<color=green>[Улучшение]</color> Большой ящик создан с {newCapacity} слотами (more_slots ур. {u.MoreSlotsLevel})");
        }

        // ===== 2. auto_fill — пополнение печей =====
        private void AutoFillOvens()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                var u = GetOrCreate(p.userID);
                if (u.AutoFillLevel == 0) continue;

                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var oven = entity as BaseOven;
                    if (oven == null || oven.IsDestroyed) continue;
                    if (oven.OwnerID != p.userID) continue;

                    var inv = oven.inventory;
                    if (inv == null) continue;

                    int fuelCount = 0;
                    foreach (var it in inv.itemList)
                    {
                        if (it.info.shortname == "charcoal" || it.info.shortname == "wood")
                        {
                            fuelCount = it.amount;
                            break;
                        }
                    }

                    if (fuelCount >= _config.AutoFill.MaxFuel) continue;

                    int need = _config.AutoFill.MaxFuel - fuelCount;
                    var playerFuel = p.inventory.FindItemByItemName("charcoal") ?? p.inventory.FindItemByItemName("wood");
                    if (playerFuel == null || playerFuel.amount <= 0) continue;

                    int take = Mathf.Min(need, playerFuel.amount);
                    var newItem = playerFuel.SplitItem(take);
                    if (newItem != null) inv.GiveItem(newItem);
                }
            }
        }

        // ===== 3. research_free — ресёрч без скрапа =====
        private object OnResearchItem(BasePlayer player, Item item, Item researchTable)
        {
            if (player == null || item == null) return null;
            var u = GetOrCreate(player.userID);
            if (u.ResearchFreeLevel == 0) return null;

            var blueprint = item.blueprintTargetDef;
            if (blueprint == null) return null;

            player.blueprints.Unlock(blueprint);
            item.Remove();
            SendReply(player, $"<color=green>[Улучшение]</color> Ресёрч {blueprint.displayName.english} без затрат!");
            return true;
        }

        // ===== 4. no_decay — постройки не гниют =====
        private object OnEntityTakeDamage(BuildingBlock block, HitInfo info)
        {
            if (block == null || info == null) return null;
            if (block.OwnerID == 0) return null;

            var owner = BasePlayer.FindByID(block.OwnerID);
            if (owner == null) return null;

            var u = GetOrCreate(owner.userID);
            if (u.NoDecayLevel == 0) return null;

            var majorType = info.damageTypes.GetMajorityDamageType();
            if (majorType == Rust.DamageType.Decay)
                return true;

            return null;
        }

        // ===== Хелперы =====
        private PlayerUpgrades GetOrCreate(ulong userId)
        {
            if (!_data.TryGetValue(userId, out var u))
                u = _data[userId] = new PlayerUpgrades();
            return u;
        }

        private void LoadData()
        {
            try
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerUpgrades>>("BaseUpgrades");
                if (stored != null) _data = stored;
            }
            catch { PrintWarning("Не удалось загрузить данные"); }
        }

        private void SaveData()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject("BaseUpgrades", _data); }
            catch (Exception e) { PrintWarning($"Ошибка сохранения: {e.Message}"); }
        }

        // ===== Классы =====
        private class PlayerUpgrades
        {
            public int MoreSlotsLevel { get; set; } = 0;
            public int AutoFillLevel { get; set; } = 0;
            public int ResearchFreeLevel { get; set; } = 0;
            public int NoDecayLevel { get; set; } = 0;
        }

        private class PluginConfig
        {
            public MoreSlotsConfig MoreSlots { get; set; } = new MoreSlotsConfig();
            public AutoFillConfig AutoFill { get; set; } = new AutoFillConfig();
            public ResearchFreeConfig ResearchFree { get; set; } = new ResearchFreeConfig();
            public NoDecayConfig NoDecay { get; set; } = new NoDecayConfig();
        }

        private class MoreSlotsConfig
        {
            public int MaxLevel { get; set; } = 5;
            public int BasePrice { get; set; } = 800;
            public int SlotsPerLevel { get; set; } = 6;
            public int BaseCapacity { get; set; } = 48;
        }

        private class AutoFillConfig
        {
            public int MaxLevel { get; set; } = 1;
            public int BasePrice { get; set; } = 2000;
            public float IntervalSeconds { get; set; } = 120f;
            public int MaxFuel { get; set; } = 100;
        }

        private class ResearchFreeConfig
        {
            public int MaxLevel { get; set; } = 1;
            public int BasePrice { get; set; } = 5000;
        }

        private class NoDecayConfig
        {
            public int MaxLevel { get; set; } = 1;
            public int BasePrice { get; set; } = 8000;
        }
    }
}