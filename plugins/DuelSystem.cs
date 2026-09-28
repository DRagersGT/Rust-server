using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Duel System", "MrDoo", "1.0.0")]
    [Description("Дуэли 1x1 с разрешением урона через TruePVE")]
    public class DuelSystem : RustPlugin
    {
        private PluginConfig _config;

        // userID -> userID соперника (двусторонняя связь)
        private readonly Dictionary<ulong, ulong> _activeDuels = new Dictionary<ulong, ulong>();
        // userID -> приглашение от кого
        private readonly Dictionary<ulong, ulong> _pendingInvites = new Dictionary<ulong, ulong>();
        // userID -> время окончания дуэли
        private readonly Dictionary<ulong, DateTime> _duelEndTime = new Dictionary<ulong, DateTime>();
        // userID -> время окончания кулдауна
        private readonly Dictionary<ulong, DateTime> _cooldowns = new Dictionary<ulong, DateTime>();
        // userID -> таймер проверки
        private readonly Dictionary<ulong, Timer> _checkTimers = new Dictionary<ulong, Timer>();

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) { LoadDefaultConfig(); _config = new PluginConfig(); }

            cmd.AddChatCommand("duel", this, nameof(CmdDuel));
            Puts($"Duel System загружен. Макс. длительность: {_config.MaxDuelSeconds}с, кулдаун: {_config.CooldownSeconds}с");
        }

        private void Unload()
        {
            foreach (var t in _checkTimers.Values) t?.Destroy();
        }

        private void CmdDuel(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                SendReply(player, "Использование: /duel <ник> — вызвать, /duel accept — принять, /duel decline — отклонить, /duel stop — прекратить");
                return;
            }

            string sub = args[0].ToLower();

            if (sub == "accept") { AcceptDuel(player); return; }
            if (sub == "decline") { DeclineDuel(player); return; }
            if (sub == "stop") { StopDuel(player, true); return; }
            if (sub == "list") { ShowList(player); return; }

            // Вызов игрока
            string targetName = string.Join(" ", args);
            var target = FindPlayer(targetName);
            if (target == null)
            {
                SendReply(player, $"<color=red>Игрок '{targetName}' не найден.</color>");
                return;
            }
            if (target == player)
            {
                SendReply(player, "<color=red>Себя вызвать нельзя.</color>");
                return;
            }
            if (_activeDuels.ContainsKey(player.userID))
            {
                SendReply(player, "<color=red>Ты уже в дуэли. /duel stop — прекратить.</color>");
                return;
            }
            if (_activeDuels.ContainsKey(target.userID))
            {
                SendReply(player, "<color=red>Этот игрок уже в дуэли.</color>");
                return;
            }
            if (IsOnCooldown(player.userID, out var cdLeftP))
            {
                SendReply(player, $"<color=red>Кулдаун: {cdLeftP} сек.</color>");
                return;
            }
            if (IsOnCooldown(target.userID, out var cdLeftT))
            {
                SendReply(player, $"<color=red>У соперника кулдаун: {cdLeftT} сек.</color>");
                return;
            }

            _pendingInvites[target.userID] = player.userID;
            SendReply(player, $"<color=yellow>Вызов отправлен игроку {target.displayName}. Ждём ответа.</color>");
            SendReply(target, $"<color=yellow>{player.displayName}</color> вызывает тебя на дуэль! /duel accept — принять, /duel decline — отклонить. (30 сек)");
            timer.Once(30f, () =>
            {
                if (_pendingInvites.ContainsKey(target.userID) && _pendingInvites[target.userID] == player.userID)
                {
                    _pendingInvites.Remove(target.userID);
                    if (player.IsConnected) SendReply(player, "<color=red>Вызов истёк.</color>");
                    if (target.IsConnected) SendReply(target, "<color=red>Вызов истёк.</color>");
                }
            });
        }

        private void AcceptDuel(BasePlayer player)
        {
            if (!_pendingInvites.TryGetValue(player.userID, out var challengerId))
            {
                SendReply(player, "<color=red>Нет активных вызовов.</color>");
                return;
            }
            _pendingInvites.Remove(player.userID);

            var challenger = BasePlayer.FindByID(challengerId);
            if (challenger == null || !challenger.IsConnected)
            {
                SendReply(player, "<color=red>Соперник уже не в сети.</color>");
                return;
            }

            StartDuel(challenger, player);
        }

        private void DeclineDuel(BasePlayer player)
        {
            if (!_pendingInvites.TryGetValue(player.userID, out var challengerId))
            {
                SendReply(player, "<color=red>Нет активных вызовов.</color>");
                return;
            }
            _pendingInvites.Remove(player.userID);

            var challenger = BasePlayer.FindByID(challengerId);
            if (challenger != null && challenger.IsConnected)
                SendReply(challenger, $"<color=yellow>{player.displayName}</color> отклонил вызов.");
            SendReply(player, "Вызов отклонён.");
        }

        private void StartDuel(BasePlayer a, BasePlayer b)
        {
            _activeDuels[a.userID] = b.userID;
            _activeDuels[b.userID] = a.userID;
            _duelEndTime[a.userID] = DateTime.Now.AddSeconds(_config.MaxDuelSeconds);
            _duelEndTime[b.userID] = DateTime.Now.AddSeconds(_config.MaxDuelSeconds);

            // Восстанавливаем здоровье
            a.health = a._maxHealth;
            b.health = b._maxHealth;
            a.SendNetworkUpdate();
            b.SendNetworkUpdate();

            SendReply(a, $"<color=green>ДУЭЛЬ НАЧАЛАСЬ!</color> Против: {b.displayName}. Время: {_config.MaxDuelSeconds / 60} мин. Выход: /duel stop");
            SendReply(b, $"<color=green>ДУЭЛЬ НАЧАЛАСЬ!</color> Против: {a.displayName}. Время: {_config.MaxDuelSeconds / 60} мин. Выход: /duel stop");

            // Таймер окончания
            _checkTimers[a.userID] = timer.Once(_config.MaxDuelSeconds, () =>
            {
                if (_activeDuels.ContainsKey(a.userID)) EndDuel(a.userID, "время вышло");
            });
        }

        private void StopDuel(BasePlayer player, bool byPlayer)
        {
            if (!_activeDuels.ContainsKey(player.userID))
            {
                SendReply(player, "<color=red>Ты не в дуэли.</color>");
                return;
            }
            EndDuel(player.userID, byPlayer ? "по решению игрока" : "завершено");
        }

        private void EndDuel(ulong initiatorId, string reason)
        {
            if (!_activeDuels.TryGetValue(initiatorId, out var otherId)) return;

            var a = BasePlayer.FindByID(initiatorId);
            var b = BasePlayer.FindByID(otherId);

            _activeDuels.Remove(initiatorId);
            _activeDuels.Remove(otherId);
            _duelEndTime.Remove(initiatorId);
            _duelEndTime.Remove(otherId);

            _cooldowns[initiatorId] = DateTime.Now.AddSeconds(_config.CooldownSeconds);
            _cooldowns[otherId] = DateTime.Now.AddSeconds(_config.CooldownSeconds);

            if (_checkTimers.TryGetValue(initiatorId, out var t1)) { t1?.Destroy(); _checkTimers.Remove(initiatorId); }
            if (_checkTimers.TryGetValue(otherId, out var t2)) { t2?.Destroy(); _checkTimers.Remove(otherId); }

            if (a != null && a.IsConnected) SendReply(a, $"<color=yellow>Дуэль завершена ({reason}). Кулдаун {_config.CooldownSeconds / 60} мин.</color>");
            if (b != null && b.IsConnected) SendReply(b, $"<color=yellow>Дуэль завершена ({reason}). Кулдаун {_config.CooldownSeconds / 60} мин.</color>");
        }

        private void ShowList(BasePlayer player)
        {
            if (_activeDuels.Count == 0)
            {
                SendReply(player, "Активных дуэлей нет.");
                return;
            }
            SendReply(player, $"<color=yellow>Активные дуэли:</color>");
            var seen = new HashSet<ulong>();
            foreach (var kvp in _activeDuels)
            {
                if (seen.Contains(kvp.Key)) continue;
                seen.Add(kvp.Key);
                seen.Add(kvp.Value);
                var a = BasePlayer.FindByID(kvp.Key);
                var b = BasePlayer.FindByID(kvp.Value);
                string nameA = a?.displayName ?? "?";
                string nameB = b?.displayName ?? "?";
                SendReply(player, $"  {nameA} vs {nameB}");
            }
        }

        private BasePlayer FindPlayer(string name)
        {
            name = name.ToLower();
            BasePlayer best = null;
            int bestLen = int.MaxValue;
            foreach (var p in BasePlayer.activePlayerList)
            {
                var dn = p.displayName.ToLower();
                if (dn == name) return p;
                if (dn.Contains(name) && dn.Length < bestLen) { best = p; bestLen = dn.Length; }
            }
            return best;
        }

        private bool IsOnCooldown(ulong userId, out int secondsLeft)
        {
            secondsLeft = 0;
            if (!_cooldowns.TryGetValue(userId, out var endTime)) return false;
            if (endTime <= DateTime.Now) { _cooldowns.Remove(userId); return false; }
            secondsLeft = (int)(endTime - DateTime.Now).TotalSeconds;
            return true;
        }

        // ===== Интеграция с TruePVE =====
        // TruePVE вызывает этот хук. Если вернуть true — урон разрешён.
        private object CanEntityTakeDamage(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || info == null) return null;
            var attacker = info.InitiatorPlayer;
            if (attacker == null) return null;
            if (attacker == victim) return null;

            var victimPlayer = victim as BasePlayer;
            if (victimPlayer == null) return null;
            if (victimPlayer.IsNpc || attacker.IsNpc) return null;

            // Если оба в дуэли друг с другом — разрешаем
            if (_activeDuels.TryGetValue(attacker.userID, out var opponent) && opponent == victimPlayer.userID)
            {
                return true;
            }

            return null; // Отдаём решение TruePVE (по умолчанию — урон запрещён в PvE)
        }

        // При смерти — завершаем дуэль, победитель получает уведомление
        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null) return;
            if (!_activeDuels.TryGetValue(victim.userID, out var opponentId)) return;

            var winner = BasePlayer.FindByID(opponentId);
            if (winner != null && winner.IsConnected)
            {
                SendReply(winner, $"<color=green>Победа! Ты победил {victim.displayName} в дуэли.</color>");
            }
            if (victim.IsConnected)
            {
                SendReply(victim, "<color=red>Ты погиб в дуэли. Поражение.</color>");
            }

            EndDuel(victim.userID, "смерть");
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            // Если был в дуэли — завершаем
            if (_activeDuels.ContainsKey(player.userID))
                EndDuel(player.userID, "соперник вышел");

            // Если был вызов — убираем
            _pendingInvites.Remove(player.userID);
            // И убираем вызовы, где он был инициатором
            var toRemove = new List<ulong>();
            foreach (var kvp in _pendingInvites)
                if (kvp.Value == player.userID) toRemove.Add(kvp.Key);
            foreach (var k in toRemove) _pendingInvites.Remove(k);
        }

        private class PluginConfig
        {
            public int MaxDuelSeconds { get; set; } = 900;      // 15 минут
            public int CooldownSeconds { get; set; } = 300;      // 5 минут
        }
    }
}