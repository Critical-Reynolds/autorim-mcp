using AutoRim.Bridge;
using AutoRim.Core;
using Verse;

namespace AutoRim
{
    /// <summary>
    /// Main-thread anchor. RimWorld auto-instantiates every GameComponent subclass found in a
    /// loaded mod assembly, so no Harmony patching is needed to get a per-frame hook.
    ///
    /// Everything that touches Verse/RimWorld state must run from here: the game is not
    /// thread-safe, and the bridge's socket threads must never call into it directly.
    ///
    /// GameComponentUpdate is used rather than GameComponentTick because Update runs every
    /// frame even while the game is paused, which is exactly when a player is most likely to
    /// be asking questions about the colony.
    /// </summary>
    public class AutoRimGameComponent : GameComponent
    {
        public static AutoRimGameComponent Current { get; private set; }

        public AutoRimGameComponent(Game game)
        {
            Current = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ARLog.Message($"Game ready. Bridge {(HttpBridge.Running ? $"listening on port {HttpBridge.Port}" : "not running")}.");
        }

        public override void GameComponentUpdate()
        {
            // Re-asserted here rather than only at startup: opening the options screen can put
            // the preference back, and a suspended game means every bridge request times out.
            BackgroundRunning.Tick();

            Dispatcher.Pump();
        }
    }
}
