using System;
using RimMind.Presentation.Agent;
using Verse;

namespace RimMind.Infrastructure.Verse
{
    /// <summary>Runs <see cref="ColonySafetyNet"/> on every player home map every 250 ticks while it is enabled.</summary>
    public sealed class ColonySafetyNetGameComponent : GameComponent
    {
        private const int EvaluateEveryTicks = 250;

        public ColonySafetyNetGameComponent(Game game) { }

        public override void StartedNewGame() => ColonySafetyNet.Reset();

        public override void LoadedGame() => ColonySafetyNet.Reset();

        public override void GameComponentTick()
        {
            if (!ColonySafetyNet.Enabled || Find.TickManager.TicksGame % EvaluateEveryTicks != 0) return;
            foreach (var map in Find.Maps)
            {
                if (map == null || !map.IsPlayerHome) continue;
                try
                {
                    ColonySafetyNet.Evaluate(map);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimMind-Safety] evaluation failed on map {map.uniqueID}: {ex.Message}");
                }
            }
        }
    }
}
