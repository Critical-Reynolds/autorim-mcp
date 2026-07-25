using System;

namespace AutoRim.Core
{
    /// <summary>
    /// Stable error codes. The MCP server keys its user-facing messages off these, so treat
    /// them as part of the wire contract and add rather than rename.
    /// </summary>
    public static class ErrorCode
    {
        public const string UnknownCommand = "UNKNOWN_COMMAND";
        public const string BadArgs = "BAD_ARGS";
        public const string NoGame = "NO_GAME";
        public const string NotFound = "NOT_FOUND";
        public const string Ambiguous = "AMBIGUOUS";
        public const string NeedsConfirm = "NEEDS_CONFIRM";
        public const string NotAllowed = "NOT_ALLOWED";
        public const string DlcNotActive = "DLC_NOT_ACTIVE";
        public const string BridgeDisabled = "BRIDGE_DISABLED";
        public const string Timeout = "TIMEOUT";
        public const string Failed = "FAILED";
        public const string Internal = "INTERNAL";
    }

    /// <summary>
    /// Expected, reportable failure. Anything thrown as a CommandException becomes a clean
    /// structured error on the wire; anything else becomes INTERNAL and is logged.
    /// </summary>
    public class CommandException : Exception
    {
        public string Code { get; }

        /// <summary>Actionable follow-up for the caller, e.g. which candidates to pick from.</summary>
        public string Hint { get; }

        /// <summary>
        /// Structured payload attached to the error: disambiguation candidates for AMBIGUOUS,
        /// the consequence preview for NEEDS_CONFIRM.
        /// </summary>
        public Bridge.JsonValue Payload { get; set; }

        public CommandException(string code, string message, string hint = null) : base(message)
        {
            Code = code;
            Hint = hint;
        }

        public static CommandException BadArgs(string message, string hint = null) =>
            new CommandException(ErrorCode.BadArgs, message, hint);

        public static CommandException NotFound(string message, string hint = null) =>
            new CommandException(ErrorCode.NotFound, message, hint);

        public static CommandException Failed(string message, string hint = null) =>
            new CommandException(ErrorCode.Failed, message, hint);

        public static CommandException NoGame() =>
            new CommandException(ErrorCode.NoGame, "No game is loaded.",
                "Load a colony save in RimWorld, then retry.");
    }
}
