using System;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Airdrop Alert", "MrDoo", "1.1.0")]
    [Description("Уведомляет о сбросе аирдропа и гарантирует минимум сбросов в день")]
    public class AirdropAlert : RustPlugin
    {
        private PluginConfig _config;
        private Timer _trackerTimer;
        private Timer _autoDropTimer;
        private Vector3? _currentDropPos;
        private DateTime _lastAirdropTime = DateTime.MinValue;
        private int _dropsToday = 0;
        private int _lastDay = -1;

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null)
            {
                LoadDefaultConfig();
                _config = new PluginConfig();
            }

            if (_config.MinAirdropsPerDay > 0)
            {
                _autoDropTimer = timer.Every(60f, CheckAutoDrop);
                var sky = TOD_Sky.Instance;
                if (sky != null) _lastDay = sky.Cycle.DateTime.Day;
                Puts($"[Airdrop Alert] Автосброс включён: минимум {_config.MinAirdropsPerDay} аирдропов в день, проверка раз в минуту.");
            }
        }

        private void Unload()
        {
            _trackerTimer?.Destroy();
            _autoDropTimer?.Destroy();
        }

        private void CheckAutoDrop()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null) return;

            int currentDay = sky.Cycle.DateTime.Day;
            if (currentDay != _lastDay)
            {
                _lastDay = currentDay;
                _dropsToday = 0;
                Puts($"[Airdrop Alert] Новый игровой день #{currentDay}. Счётчик сброшен.");
            }

            if (_dropsToday >= _config.MinAirdropsPerDay) return;
            if ((DateTime.Now - _lastAirdropTime).TotalMinutes < _config.MinMinutesBetweenDrops) return;

            SpawnAirdrop();
        }

        private void SpawnAirdrop()
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "airdrop");
            _lastAirdropTime = DateTime.Now;
            _dropsToday++;
            Puts($"[Airdrop Alert] Автоматический аирдроп вызван. Сегодня: {_dropsToday}/{_config.MinAirdropsPerDay}");
        }

        private void OnAirdrop(CargoPlane plane, Vector3 dropPosition)
        {
            if (_config == null) return;
            
            _currentDropPos = dropPosition;

            int gridX = (int)(dropPosition.x / 146.3f);
            int gridZ = (int)(dropPosition.z / 146.3f);
            char gridLetter = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int gridNum = Mathf.Clamp(gridZ + 1, 1, 99);

            foreach (var player in BasePlayer.activePlayerList)
            {
                SendReply(player,
                    $"<color=#FFA500>[Аирдроп]</color> Сброс замечен! Квадрат: <color=yellow>{gridLetter}{gridNum}</color> " +
                    $"({(int)dropPosition.x}, {(int)dropPosition.z}). Летите быстрее!");
            }

            _trackerTimer?.Destroy();
            _trackerTimer = timer.Once(_config.ReminderSeconds, () =>
            {
                if (_currentDropPos == null) return;
                foreach (var player in BasePlayer.activePlayerList)
                {
                    SendReply(player,
                        $"<color=#FFA500>[Аирдроп]</color> Напоминание: ящик был в квадрате <color=yellow>{gridLetter}{gridNum}</color>. Возможно, ещё не разграблен!");
                }
                _currentDropPos = null;
            });
        }

        private class PluginConfig
        {
            public int ReminderSeconds { get; set; } = 300;
            public int MinAirdropsPerDay { get; set; } = 1;
            public int MinMinutesBetweenDrops { get; set; } = 60;
        }
    }
}