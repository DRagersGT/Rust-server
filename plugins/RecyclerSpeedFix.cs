using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Recycler Speed Fix", "MrDoo", "1.0.0")]
    [Description("Устанавливает скорость переработки через InvokeRepeating")]
    public class RecyclerSpeedFix : RustPlugin
    {
        private float _recycleTime;

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new Dictionary<string, object>
            {
                ["Recycle Time (Seconds)"] = 0.1f
            }, true);
        }

        private void OnServerInitialized()
        {
            _recycleTime = Config.Get<float>("Recycle Time (Seconds)");
            ApplyToAllRecyclers();
            Puts($"Recycler Speed Fix загружен. Время переработки: {_recycleTime} сек.");
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var recycler = entity as Recycler;
            if (recycler != null)
                ApplySpeed(recycler);
        }

        private void ApplyToAllRecyclers()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var recycler = entity as Recycler;
                if (recycler != null)
                    ApplySpeed(recycler);
            }
        }

        private void ApplySpeed(Recycler recycler)
        {
            if (recycler == null) return;

            // Отменяем стандартный повтор (с ванильным временем)
            recycler.CancelInvoke(nameof(Recycler.RecycleThink));

            // Запускаем с нашей скоростью
            recycler.InvokeRepeating(new Action(recycler.RecycleThink), _recycleTime, _recycleTime);
        }
    }
}