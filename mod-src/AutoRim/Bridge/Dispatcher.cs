using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using AutoRim.Core;

namespace AutoRim.Bridge
{
    /// <summary>
    /// Moves work from the bridge's socket threads onto RimWorld's main thread.
    ///
    /// This is the load-bearing piece of the whole mod. Unity and RimWorld are not
    /// thread-safe; reading Find.CurrentMap or mutating a pawn from a socket thread corrupts
    /// state or crashes the process. Socket threads therefore only ever enqueue, and every
    /// call into the game happens inside Pump(), which runs from GameComponentUpdate.
    /// </summary>
    public static class Dispatcher
    {
        /// <summary>Per-frame execution budget. Overflow rolls to the next frame.</summary>
        private const double FrameBudgetMs = 8.0;

        /// <summary>Backstop against a runaway client; requests beyond this are rejected outright.</summary>
        private const int MaxQueueDepth = 256;

        /// <summary>How stale the pump heartbeat may get before we treat the game loop as gone.</summary>
        private const long LivenessWindowMs = 1000;

        private static readonly ConcurrentQueue<PendingCommand> Queue = new ConcurrentQueue<PendingCommand>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static long _lastPumpMs = long.MinValue;
        private static int _queueDepth;

        /// <summary>
        /// True while the main-thread pump has run recently. The socket thread uses this
        /// instead of reading game state directly, which it must not do.
        /// </summary>
        public static bool GameLoopAlive =>
            Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastPumpMs) < LivenessWindowMs;

        public static int QueueDepth => Volatile.Read(ref _queueDepth);

        // ---- socket thread ----------------------------------------------------------------

        /// <summary>
        /// Enqueues a command and blocks until the main thread has run it. Always returns a
        /// complete envelope; never throws.
        /// </summary>
        public static JsonValue ExecuteBlocking(string command, JsonValue args, int timeoutMs)
        {
            if (!GameLoopAlive)
                return Envelope.Error(ErrorCode.NoGame,
                    "RimWorld is running but no game is being updated.",
                    "Load a colony save (or leave the main menu) and retry.");

            if (Volatile.Read(ref _queueDepth) >= MaxQueueDepth)
                return Envelope.Error(ErrorCode.Failed,
                    "AutoRim command queue is full.",
                    "Too many concurrent requests; retry shortly.");

            var pending = new PendingCommand(command, args);
            Interlocked.Increment(ref _queueDepth);
            Queue.Enqueue(pending);

            if (pending.Completed.Wait(timeoutMs))
                return pending.Response ?? Envelope.Error(ErrorCode.Internal, "Command produced no response.");

            // Abandon it. The pump will skip it rather than apply an effect the caller has
            // already been told did not happen.
            pending.Abandon();
            return Envelope.Error(ErrorCode.Timeout,
                $"'{command}' did not complete within {timeoutMs} ms.",
                "The game may be paused mid-load or busy. Retry, or check that RimWorld is responsive.");
        }

        // ---- main thread ------------------------------------------------------------------

        /// <summary>
        /// Drains queued commands. Called every frame from GameComponentUpdate. Nothing may
        /// escape from here: an exception reaching the caller lands inside the game's update
        /// loop.
        /// </summary>
        public static void Pump()
        {
            Interlocked.Exchange(ref _lastPumpMs, Clock.ElapsedMilliseconds);

            if (Queue.IsEmpty) return;

            double startedMs = Clock.Elapsed.TotalMilliseconds;
            while (Clock.Elapsed.TotalMilliseconds - startedMs < FrameBudgetMs && Queue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queueDepth);
                Execute(pending);
            }
        }

        private static void Execute(PendingCommand pending)
        {
            if (pending.IsAbandoned) return;

            JsonValue response;
            try
            {
                var data = CommandRegistry.Execute(pending.Command, pending.Args);
                response = Envelope.Ok(data);
            }
            catch (CommandException ex)
            {
                response = Envelope.Error(ex.Code, ex.Message, ex.Hint, ex.Payload);
            }
            catch (Exception ex)
            {
                ARLog.Exception($"command '{pending.Command}'", ex);
                response = Envelope.Error(ErrorCode.Internal,
                    $"{ex.GetType().Name}: {ex.Message}",
                    "This is a bug in AutoRim. See Player.log for the stack trace.");
            }

            pending.Complete(response);
        }

        /// <summary>
        /// Fails every queued request. Called when the game is torn down so socket threads
        /// waiting on a pump that will never come are released immediately.
        /// </summary>
        public static void DrainAndFail(string reason)
        {
            while (Queue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queueDepth);
                pending.Complete(Envelope.Error(ErrorCode.NoGame, reason));
            }
        }

        private sealed class PendingCommand
        {
            public readonly string Command;
            public readonly JsonValue Args;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);

            public JsonValue Response { get; private set; }

            private int _abandoned;

            public PendingCommand(string command, JsonValue args)
            {
                Command = command;
                Args = args;
            }

            public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

            public void Abandon() => Interlocked.Exchange(ref _abandoned, 1);

            public void Complete(JsonValue response)
            {
                Response = response;
                try
                {
                    Completed.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Waiter gave up and disposed; nothing to hand back.
                }
            }
        }
    }
}
