using System;
using Newtonsoft.Json.Linq;

namespace RimMind.Application.Features.Telemetry
{
    /// <summary>
    /// Emit point for the live event stream (docs/autonomous-play/EVENTS.md). Layer-neutral: the game-side writer
    /// installs <see cref="Sink"/>; with no writer installed, emitting is a no-op. Thread-safe as long as the sink is
    /// (the writer queues lines and appends them on the main thread).
    /// </summary>
    public static class LiveEvents
    {
        /// <summary>Installed by the live event writer; receives (event type, payload).</summary>
        public static Action<string, JObject>? Sink { get; set; }

        public static bool Enabled => Sink != null;

        public static void Emit(string type, JObject payload)
        {
            var sink = Sink;
            if (sink == null) return;
            try
            {
                sink(type, payload);
            }
            catch
            {
                // Telemetry must never break gameplay.
            }
        }
    }
}
