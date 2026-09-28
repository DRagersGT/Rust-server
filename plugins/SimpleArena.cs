using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Simple Arena", "MrDoo", "1.0.3")]
    [Description("PvE арена с волнами NPC, награды через ServerRewards")]
    public class SimpleArena : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards;

        private PluginConfig _config;
        private Dictionary<ulong, Vector3> _returnPositions = new Dictionary<ulong, Vector3>();
        private Dictionary<ulong, int> _currentWave = new Dictionary<ulong, int>();
        private Dictionary<ulong, HashSet<BaseEntity>> _arenaNpcs = new Dictionary<ulong, HashSet<BaseEntity>>();
        private Dictionary<ulong, Timer> _waveTimers = new Dictionary<ulong, Timer>();
        private Dictionary<ulong, DateTime> _cooldowns = new Dictionary<ulong, DateTime>();

        private const string PERM_ADMIN = "simplearena.admin";

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            permission.RegisterPermission(PERM_ADMIN, this);
            cmd.AddChatCommand("arena", this, nameof(CmdArena));
            Puts($"Simple Arena загружен. Позиция: {_config.ArenaPosition}");
        }

        private void Unload()
        {
            foreach (var kvp in _arenaNpcs)
                foreach (var npc in kvp.Value)
                    if (npc != null && !npc.IsDestroyed) npc.Kill();
            foreach (var t in _waveTimers.Values) t?.Destroy();
        }

        private void CmdArena(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                if (_returnPositions.ContainsKey(player.userID))
                {
                    SendReply(player, "Ты уже на арене. /arena leave — выйти.");
                    return;
                }
                if (_cooldowns.TryGetValue(player.userID, out var cd) && cd > DateTime.Now)
                {
                    var left = (int)(cd - DateTime.Now).TotalSeconds;
                    SendReply(player, $"Кулдаун: {left / 60} мин {left % 60} сек.");
                    return;
                }
                StartArena(player);
                return;
            }

            if (args[0].ToLower() == "leave") { LeaveArena(player); return; }

            if (args[0].ToLower() == "setpos" && permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                var pos = player.transform.position;
                _config.ArenaPosition = $"{pos.x:F2} {pos.y:F2} {pos.z:F2}";
                Config.WriteObject(_config, true);
                SendReply(player, $"Позиция арены сохранена: {_config.ArenaPosition}");
                return;
            }

            if (args[0].ToLower() == "stop" && permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                StopArena(player.userID);
                SendReply(player, "Арена остановлена.");
                return;
            }

            SendReply(player, "Команды: /arena, /arena leave. Админ: /arena setpos, /arena stop");
        }

        private void StartArena(BasePlayer player)
        {
            var parts = _config.ArenaPosition.Split(' ');
            if (parts.Length != 3)
            {
                SendReply(player, "Позиция арены не задана. Админ: /arena setpos");
                return;
            }
            Vector3 center;
            try { center = new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2])); }
            catch { SendReply(player, "Ошибка координат арены."); return; }

            _returnPositions[player.userID] = player.transform.position;
            _currentWave[player.userID] = 0;
            _arenaNpcs[player.userID] = new HashSet<BaseEntity>();

            player.Teleport(center);
            SendReply(player, $"<color=yellow>Ты на арене! Волна 1 из {_config.MaxWaves}</color>");
            SpawnWave(player, 1);
        }

        private void SpawnWave(BasePlayer player, int waveNum)
        {
            if (!_returnPositions.ContainsKey(player.userID)) return;
            if (waveNum > _config.MaxWaves) return;

            _currentWave[player.userID] = waveNum;
            var comp = _config.WaveComposition[waveNum - 1];
            var center = GetArenaCenter();

            for (int i = 0; i < comp.Roam; i++) SpawnNpc(player, center, _config.NpcRoam, "Roam");
            for (int i = 0; i < comp.Peacekeeper; i++) SpawnNpc(player, center, _config.NpcPeacekeeper, "Peacekeeper");
            for (int i = 0; i < comp.Heavy; i++) SpawnNpc(player, center, _config.NpcHeavy, "Heavy");
            if (comp.Boss) SpawnNpc(player, center, _config.NpcHeavy, "Boss");

            SendReply(player, $"<color=yellow>Волна {waveNum} из {_config.MaxWaves}</color>");
            SendReply(player, $"<color=grey>Заспавнено: Roam={comp.Roam}, PK={comp.Peacekeeper}, Heavy={comp.Heavy}, Boss={(comp.Boss ? 1 : 0)}</color>");
        }

        private Vector3 GetArenaCenter()
        {
            var parts = _config.ArenaPosition.Split(' ');
            return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
        }

        private void SpawnNpc(BasePlayer owner, Vector3 center, string prefab, string tier)
        {
            Vector3 pos = Vector3.zero;
            bool found = false;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(_config.MinSpawnRadius, _config.MaxSpawnRadius);
                var x = center.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
                var z = center.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);

                float terrainY = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
                var candidate = new Vector3(x, terrainY + 0.5f, z);

                if (TerrainMeta.WaterMap.GetDepth(candidate) > 0.5f) continue;
                if (Mathf.Abs(terrainY - center.y) > _config.MaxHeightDifference) continue;

                pos = candidate;
                found = true;
                break;
            }

            if (!found)
            {
                var angle = UnityEngine.Random.Range(0f, 360f);
                var dist = UnityEngine.Random.Range(_config.MinSpawnRadius, _config.MinSpawnRadius + 5f);
                var x = center.x + dist * Mathf.Cos(angle * Mathf.Deg2Rad);
                var z = center.z + dist * Mathf.Sin(angle * Mathf.Deg2Rad);
                float terrainY = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
                pos = new Vector3(x, terrainY + 0.5f, z);
                Puts($"[Арена] Не нашли точку для {tier}, спавним ближе к центру");
            }

            var entity = GameManager.server.CreateEntity(prefab, pos, Quaternion.identity);
            if (entity == null)
            {
                PrintWarning($"Не удалось создать NPC: {prefab}");
                return;
            }
            entity.Spawn();

            var npc = entity as BasePlayer;
            if (npc != null)
            {
                npc.displayName = tier switch
                {
                    "Boss" => "BOSS Raider",
                    "Heavy" => "Heavy Scientist",
                    "Peacekeeper" => "Peacekeeper",
                    _ => "Road Scientist"
                };

                if (tier == "Boss")
                {
                    npc._maxHealth = 1500f;
                    npc.health = 1500f;
                }
                npc.SendNetworkUpdate();
            }

            _arenaNpcs[owner.userID].Add(entity);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            ulong foundOwner = 0;
            foreach (var kvp in _arenaNpcs)
            {
                if (kvp.Value.Contains(entity))
                {
                    foundOwner = kvp.Key;
                    kvp.Value.Remove(entity);
                    break;
                }
            }
            if (foundOwner == 0) return;

            GiveKillReward(attacker, entity);

            if (_arenaNpcs.ContainsKey(foundOwner) && _currentWave.ContainsKey(foundOwner))
                CheckWaveComplete(foundOwner);
        }

        private void GiveKillReward(BasePlayer attacker, BaseCombatEntity entity)
        {
            int reward = _config.Rewards.PerRoamKill;
            var name = (entity as BasePlayer)?.displayName ?? "";
            if (name.Contains("BOSS")) reward = _config.Rewards.PerBossKill;
            else if (name.Contains("Heavy")) reward = _config.Rewards.PerHeavyKill;
            else if (name.Contains("Peacekeeper")) reward = _config.Rewards.PerPeacekeeperKill;

            ServerRewards?.Call("AddPoints", attacker.userID, reward);
            DropLoot(entity.transform.position);
        }

        private void DropLoot(Vector3 pos)
        {
            SpawnItem(pos, "syringe.medical", UnityEngine.Random.Range(1, 3));
            SpawnItem(pos, "ammo.rifle", UnityEngine.Random.Range(20, 50));
            if (UnityEngine.Random.Range(0f, 1f) < 0.2f)
                SpawnItem(pos, "largemedkit", 1);
        }

        private void SpawnItem(Vector3 pos, string shortname, int amount)
        {
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            item.Drop(pos + Vector3.up * 1f, Vector3.up * 2f);
        }

        private void CheckWaveComplete(ulong userID)
        {
            if (!_arenaNpcs.TryGetValue(userID, out var npcs)) return;
            if (npcs.Count > 0) return;

            if (!_currentWave.TryGetValue(userID, out int currentWaveNum)) return;

            var player = BasePlayer.FindByID(userID);
            if (player == null) { StopArena(userID); return; }

            int reward = _config.Rewards.PerWaveComplete;
            ServerRewards?.Call("AddPoints", userID, reward);
            SendReply(player, $"<color=green>Волна пройдена! +{reward} RP</color>");

            // === ХУК ДЛЯ SKILLLEVELS ===
            Interface.CallHook("OnArenaWaveComplete", player, currentWaveNum);

            if (currentWaveNum >= _config.MaxWaves)
            {
                int bonus = _config.Rewards.AllWavesBonus;
                ServerRewards?.Call("AddPoints", userID, bonus);
                SendReply(player, $"<color=green>Все волны пройдены! Бонус: {bonus} RP</color>");
                LeaveArena(player);
                return;
            }

            int nextWave = currentWaveNum + 1;
            if (_waveTimers.TryGetValue(userID, out var oldTimer)) oldTimer?.Destroy();
            _waveTimers[userID] = timer.Once(_config.TimeBetweenWaves, () =>
            {
                if (BasePlayer.FindByID(userID) != null)
                    SpawnWave(player, nextWave);
            });
            SendReply(player, $"Следующая волна через {_config.TimeBetweenWaves} сек.");
        }

        private void LeaveArena(BasePlayer player)
        {
            if (!_returnPositions.TryGetValue(player.userID, out var pos)) return;
            player.Teleport(pos);
            StopArena(player.userID);
            _cooldowns[player.userID] = DateTime.Now.AddSeconds(_config.CooldownSeconds);
            SendReply(player, $"Ты покинул арену. Кулдаун: {_config.CooldownSeconds / 60} мин.");
        }

        private void StopArena(ulong userID)
        {
            HashSet<BaseEntity> npcs = null;
            if (_arenaNpcs.TryGetValue(userID, out npcs))
                _arenaNpcs.Remove(userID);
            _returnPositions.Remove(userID);
            _currentWave.Remove(userID);
            if (_waveTimers.TryGetValue(userID, out var t)) { t?.Destroy(); _waveTimers.Remove(userID); }

            if (npcs != null)
            {
                foreach (var npc in npcs)
                    if (npc != null && !npc.IsDestroyed) npc.Kill();
            }
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (_returnPositions.ContainsKey(player.userID))
            {
                SendReply(player, "Ты погиб на арене. Кулдаун 10 минут.");
                StopArena(player.userID);
                _cooldowns[player.userID] = DateTime.Now.AddSeconds(_config.CooldownSeconds);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (_returnPositions.ContainsKey(player.userID))
            {
                StopArena(player.userID);
                _cooldowns[player.userID] = DateTime.Now.AddSeconds(_config.CooldownSeconds);
            }
        }

        private class PluginConfig
        {
            public string ArenaPosition { get; set; } = "0 0 0";
            public float MinSpawnRadius { get; set; } = 15f;
            public float MaxSpawnRadius { get; set; } = 50f;
            public float MaxHeightDifference { get; set; } = 5f;
            public int CooldownSeconds { get; set; } = 600;
            public int TimeBetweenWaves { get; set; } = 15;
            public int MaxWaves { get; set; } = 5;
            public string NpcRoam { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            public string NpcPeacekeeper { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab";
            public string NpcHeavy { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
            public WaveComposition[] WaveComposition { get; set; } = new WaveComposition[]
            {
                new WaveComposition { Roam = 3, Peacekeeper = 0, Heavy = 0, Boss = false },
                new WaveComposition { Roam = 5, Peacekeeper = 0, Heavy = 0, Boss = false },
                new WaveComposition { Roam = 6, Peacekeeper = 2, Heavy = 0, Boss = false },
                new WaveComposition { Roam = 7, Peacekeeper = 3, Heavy = 1, Boss = false },
                new WaveComposition { Roam = 8, Peacekeeper = 5, Heavy = 1, Boss = true }
            };
            public RewardConfig Rewards { get; set; } = new RewardConfig();
        }

        private class WaveComposition
        {
            public int Roam { get; set; }
            public int Peacekeeper { get; set; }
            public int Heavy { get; set; }
            public bool Boss { get; set; }
        }

        private class RewardConfig
        {
            public int PerRoamKill { get; set; } = 5;
            public int PerPeacekeeperKill { get; set; } = 10;
            public int PerHeavyKill { get; set; } = 20;
            public int PerBossKill { get; set; } = 100;
            public int PerWaveComplete { get; set; } = 25;
            public int AllWavesBonus { get; set; } = 250;
        }
    }
}