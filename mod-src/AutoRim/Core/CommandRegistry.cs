using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoRim.Bridge;

namespace AutoRim.Core
{
    /// <summary>
    /// Discovers every ICommand in this assembly at startup. Adding a subsystem means adding
    /// files, never editing a wiring table.
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommand> Commands =
            new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        public static int Count => Commands.Count;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            var commandType = typeof(ICommand);
            Type[] types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            int failed = 0;
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!commandType.IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    ARLog.Warning($"Command type {type.Name} has no parameterless constructor; skipped.");
                    failed++;
                    continue;
                }

                try
                {
                    var command = (ICommand)Activator.CreateInstance(type);
                    if (string.IsNullOrEmpty(command.Name))
                    {
                        ARLog.Warning($"Command type {type.Name} has an empty Name; skipped.");
                        failed++;
                        continue;
                    }

                    if (Commands.TryGetValue(command.Name, out var existing))
                    {
                        ARLog.Warning($"Duplicate command '{command.Name}' from {type.Name}; keeping {existing.GetType().Name}.");
                        failed++;
                        continue;
                    }

                    Commands[command.Name] = command;
                }
                catch (Exception ex)
                {
                    ARLog.Exception($"instantiating command type {type.Name}", ex);
                    failed++;
                }
            }

            ARLog.Message($"Registered {Commands.Count} commands{(failed > 0 ? $" ({failed} skipped)" : "")}.");
        }

        public static bool TryGet(string name, out ICommand command) => Commands.TryGetValue(name, out command);

        public static IEnumerable<ICommand> All => Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

        /// <summary>
        /// Runs a command on the main thread. Callers are responsible for having marshalled
        /// here; see Dispatcher.
        /// </summary>
        public static JsonValue Execute(string name, JsonValue args)
        {
            if (string.IsNullOrEmpty(name))
                throw CommandException.BadArgs("Missing 'command'.");

            if (!TryGet(name, out var command))
                throw new CommandException(ErrorCode.UnknownCommand, $"Unknown command '{name}'.",
                    "Call meta.list_commands for the available set.");

            if (command.RequiresGame && !GameState.IsPlaying)
                throw CommandException.NoGame();

            return SafetyGate.Run(command, args ?? JsonValue.NewObject());
        }
    }
}
