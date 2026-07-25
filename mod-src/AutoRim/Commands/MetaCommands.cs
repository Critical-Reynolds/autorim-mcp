using AutoRim.Bridge;
using AutoRim.Core;

namespace AutoRim.Commands
{
    /// <summary>
    /// Self-description. The MCP server calls meta.list_commands at startup and warns if its
    /// own tool set has drifted from what this build of the mod actually provides, which is
    /// the usual symptom of a stale deploy.
    /// </summary>
    public class ListCommandsCommand : CommandBase
    {
        public override string Name => "meta.list_commands";
        public override string Description => "Lists every command this build exposes, with its safety tier.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            var commands = JsonValue.NewArray();
            foreach (var command in CommandRegistry.All)
            {
                commands.Add(JsonValue.NewObject()
                    .Set("name", command.Name)
                    .Set("tier", command.Tier == SafetyTier.Destructive ? "destructive" : "safe")
                    .Set("requiresGame", command.RequiresGame)
                    .Set("description", command.Description));
            }

            return JsonValue.NewObject()
                .Set("version", typeof(ListCommandsCommand).Assembly.GetName().Version.ToString())
                .Set("count", commands.Count)
                .Set("commands", commands);
        }
    }

    /// <summary>Cheapest possible round trip; proves the main-thread pump is running.</summary>
    public class PingCommand : CommandBase
    {
        public override string Name => "meta.ping";
        public override string Description => "Round-trip check through the main-thread dispatcher.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            return JsonValue.NewObject()
                .Set("pong", true)
                .Set("echo", args["echo"].AsString(""))
                .Set("gameLoaded", GameState.IsPlaying);
        }
    }
}
