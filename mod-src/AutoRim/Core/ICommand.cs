using AutoRim.Bridge;

namespace AutoRim.Core
{
    public enum SafetyTier
    {
        /// <summary>Reversible through normal play: priorities, designations, research, bills.</summary>
        Safe,

        /// <summary>
        /// Irreversible or costly: slaughter, execution, deconstruction, trades, caravan launch.
        /// Requires an explicit confirm flag and is audited.
        /// </summary>
        Destructive
    }

    public interface ICommand
    {
        /// <summary>Wire name, always "subsystem.action".</summary>
        string Name { get; }

        SafetyTier Tier { get; }

        /// <summary>One line, surfaced through meta.list_commands.</summary>
        string Description { get; }

        /// <summary>False only for commands that are meaningful at the main menu.</summary>
        bool RequiresGame { get; }

        JsonValue Execute(JsonValue args);
    }

    /// <summary>
    /// Implemented by destructive commands so the safety gate can describe what would happen
    /// when the caller has not passed confirm:true. Must not mutate any state.
    /// </summary>
    public interface IPreviewable
    {
        JsonValue Preview(JsonValue args);
    }

    public abstract class CommandBase : ICommand
    {
        public abstract string Name { get; }
        public virtual SafetyTier Tier => SafetyTier.Safe;
        public virtual string Description => string.Empty;
        public virtual bool RequiresGame => true;
        public abstract JsonValue Execute(JsonValue args);
    }
}
