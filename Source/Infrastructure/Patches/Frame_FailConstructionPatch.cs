using HarmonyLib;
using RimMind.Presentation.Agent;
using RimWorld;
using Verse;

namespace RimMind.Infrastructure.Patches
{
    /// <summary>Reports every botched construction ("Construction botched") to the colony safety net.</summary>
    [HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
    internal static class Frame_FailConstructionPatch
    {
        [HarmonyPostfix]
        private static void Record(Frame __instance, Pawn worker)
        {
            ColonySafetyNet.RecordBotchedConstruction(worker, __instance);
        }
    }
}
