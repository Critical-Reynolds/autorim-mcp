using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;

namespace AutoRim.Commands
{
    public class PawnsRenameCommand : CommandBase
    {
        public override string Name => "pawns.rename";
        public override string Description => "Renames a pawn. Pass nick, and optionally first and last.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            string previous = pawn.Name?.ToStringFull ?? pawn.LabelShort;

            if (pawn.Name is NameTriple triple)
            {
                string first = args.OptString("first", triple.First);
                string nick = args.OptString("nick", triple.Nick);
                string last = args.OptString("last", triple.Last);

                if (string.IsNullOrEmpty(nick))
                    throw CommandException.BadArgs("A nickname is required.");

                pawn.Name = new NameTriple(first, nick, last);
            }
            else
            {
                // Animals and some other pawns carry a single name.
                string single = args.OptString("nick") ?? args.OptString("name");
                if (string.IsNullOrEmpty(single))
                    throw CommandException.BadArgs("Pass 'nick' with the new name.");
                pawn.Name = new NameSingle(single);
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("from", previous)
                .Set("to", pawn.Name?.ToStringFull ?? pawn.LabelShort)
                .Set("summary", $"Renamed {previous} to {pawn.Name?.ToStringFull}.");
        }
    }

    public class PawnsSetMedicalCareCommand : CommandBase
    {
        public override string Name => "pawns.set_medical_care";
        public override string Description =>
            "Sets what medicine may be used on a pawn: none, nomeds, herbal, normal or best.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.playerSettings == null)
                throw CommandException.Failed($"{pawn.LabelShort} has no medical care setting.");

            string requested = args.RequireString("care").Trim().ToLowerInvariant();
            MedicalCareCategory care;
            switch (requested)
            {
                case "none": case "nocare": care = MedicalCareCategory.NoCare; break;
                case "nomeds": case "no_meds": care = MedicalCareCategory.NoMeds; break;
                case "herbal": care = MedicalCareCategory.HerbalOrWorse; break;
                case "normal": care = MedicalCareCategory.NormalOrWorse; break;
                case "best": care = MedicalCareCategory.Best; break;
                default:
                    throw CommandException.BadArgs($"Unknown care level '{requested}'.",
                        "Use none, nomeds, herbal, normal or best.");
            }

            var previous = pawn.playerSettings.medCare;
            pawn.playerSettings.medCare = care;

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("from", previous.ToString())
                .Set("to", care.ToString())
                .Set("summary", $"{pawn.LabelShort} medical care {previous} -> {care}.");
        }
    }

    public class PawnsSetAreaCommand : CommandBase
    {
        public override string Name => "pawns.set_area";
        public override string Description =>
            "Restricts a pawn to an allowed area. Pass area:null (or omit) to remove the restriction.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.playerSettings == null)
                throw CommandException.Failed($"{pawn.LabelShort} cannot be area-restricted.");

            var map = pawn.Map ?? GameState.RequireMap();
            string previous = pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label ?? "unrestricted";

            string name = args.OptString("area");
            if (string.IsNullOrEmpty(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("unrestricted", StringComparison.OrdinalIgnoreCase))
            {
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                return JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("from", previous)
                    .Set("to", "unrestricted")
                    .Set("summary", $"{pawn.LabelShort} is no longer area-restricted.");
            }

            var area = map.areaManager.AllAreas
                .FirstOrDefault(a => a.AssignableAsAllowed() &&
                                     string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase));

            if (area == null)
            {
                var error = CommandException.NotFound($"No assignable area named '{name}'.");
                error.Payload = JsonValue.NewObject().Set("areas", Describe.ToArray(
                    map.areaManager.AllAreas.Where(a => a.AssignableAsAllowed()).Select(a => a.Label)));
                throw error;
            }

            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = (Area_Allowed)area;

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("from", previous)
                .Set("to", area.Label)
                .Set("summary", $"{pawn.LabelShort} restricted to '{area.Label}'.");
        }
    }

    public class PawnsSetHostilityCommand : CommandBase
    {
        public override string Name => "pawns.set_hostility_response";
        public override string Description => "How a pawn reacts to hostiles: ignore, attack or flee.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.playerSettings == null || !pawn.playerSettings.UsesConfigurableHostilityResponse)
                throw CommandException.Failed($"{pawn.LabelShort} has no hostility response setting.");

            string requested = args.RequireString("response").Trim().ToLowerInvariant();
            HostilityResponseMode mode;
            switch (requested)
            {
                case "ignore": mode = HostilityResponseMode.Ignore; break;
                case "attack": mode = HostilityResponseMode.Attack; break;
                case "flee": mode = HostilityResponseMode.Flee; break;
                default:
                    throw CommandException.BadArgs($"Unknown response '{requested}'.",
                        "Use ignore, attack or flee.");
            }

            var previous = pawn.playerSettings.hostilityResponse;
            pawn.playerSettings.hostilityResponse = mode;

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("from", previous.ToString())
                .Set("to", mode.ToString())
                .Set("summary", $"{pawn.LabelShort} hostility response {previous} -> {mode}.");
        }
    }

    // ---- schedule ------------------------------------------------------------------------------

    public class ScheduleGetCommand : CommandBase
    {
        public override string Name => "schedule.get";
        public override string Description => "A pawn's 24-hour timetable, hour by hour.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.timetable?.times == null)
                throw CommandException.Failed($"{pawn.LabelShort} has no timetable.");

            var hours = JsonValue.NewArray();
            for (int hour = 0; hour < pawn.timetable.times.Count; hour++)
                hours.Add(JsonValue.NewObject()
                    .Set("hour", hour)
                    .Set("assignment", pawn.timetable.times[hour]?.defName ?? "Anything"));

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("current", pawn.timetable.CurrentAssignment?.defName ?? "")
                .Set("hours", hours);
        }
    }

    public class ScheduleSetCommand : CommandBase
    {
        public override string Name => "schedule.set";
        public override string Description =>
            "Sets timetable hours. Pass hours (list of 0-23) or fromHour+toHour, plus assignment: Work, Sleep, Joy, Anything or Meditate.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.timetable?.times == null)
                throw CommandException.Failed($"{pawn.LabelShort} has no timetable.");

            var assignment = DefResolver.Resolve<TimeAssignmentDef>(args.RequireString("assignment"), "assignment");

            var hours = new List<int>();
            if (args.Has("hours"))
            {
                hours.AddRange(args.OptIntList("hours"));
            }
            else if (args.Has("fromHour") && args.Has("toHour"))
            {
                int from = args.RequireIntInRange("fromHour", 0, 23);
                int to = args.RequireIntInRange("toHour", 0, 23);

                // Wrapping ranges are normal: a night shift runs 22 to 6.
                int hour = from;
                while (true)
                {
                    hours.Add(hour);
                    if (hour == to) break;
                    hour = (hour + 1) % 24;
                }
            }
            else
            {
                throw CommandException.BadArgs("Pass either 'hours' or both 'fromHour' and 'toHour'.");
            }

            int applied = 0;
            foreach (int hour in hours)
            {
                if (hour < 0 || hour >= pawn.timetable.times.Count) continue;
                pawn.timetable.times[hour] = assignment;
                applied++;
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("assignment", assignment.defName)
                .Set("hoursSet", applied)
                .Set("summary", $"{pawn.LabelShort}: {applied} hour(s) set to {assignment.defName}.");
        }
    }

    // ---- policies ------------------------------------------------------------------------------

    public class PoliciesListCommand : CommandBase
    {
        public override string Name => "policies.list";
        public override string Description => "Apparel, food and drug policies that exist, and who is on each.";

        public override JsonValue Execute(JsonValue args)
        {
            var game = Current.Game;

            var apparel = JsonValue.NewArray();
            foreach (var policy in game.outfitDatabase.AllOutfits)
                apparel.Add(JsonValue.NewObject().Set("id", policy.id).Set("label", policy.label));

            var food = JsonValue.NewArray();
            foreach (var policy in game.foodRestrictionDatabase.AllFoodRestrictions)
                food.Add(JsonValue.NewObject().Set("id", policy.id).Set("label", policy.label));

            var drug = JsonValue.NewArray();
            foreach (var policy in game.drugPolicyDatabase.AllPolicies)
                drug.Add(JsonValue.NewObject().Set("id", policy.id).Set("label", policy.label));

            var assignments = JsonValue.NewArray();
            foreach (var pawn in Refs.AddressablePawns().Where(p => p.IsFreeColonist && !p.Dead))
            {
                assignments.Add(JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("apparel", pawn.outfits?.CurrentApparelPolicy?.label ?? "")
                    .Set("food", pawn.foodRestriction?.CurrentFoodPolicy?.label ?? "")
                    .Set("drug", pawn.drugs?.CurrentPolicy?.label ?? ""));
            }

            return JsonValue.NewObject()
                .Set("apparelPolicies", apparel)
                .Set("foodPolicies", food)
                .Set("drugPolicies", drug)
                .Set("assignments", assignments);
        }
    }

    public class PoliciesAssignCommand : CommandBase
    {
        public override string Name => "policies.assign";
        public override string Description =>
            "Assigns a policy to a pawn. type: apparel, food or drug. policy: the policy name.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            string type = args.RequireString("type").Trim().ToLowerInvariant();
            string wanted = args.RequireString("policy");
            var game = Current.Game;

            string previous;
            string applied;

            switch (type)
            {
                case "apparel": case "outfit":
                {
                    if (pawn.outfits == null) throw CommandException.Failed($"{pawn.LabelShort} does not wear apparel.");
                    var policy = Pick(game.outfitDatabase.AllOutfits, p => p.label, wanted, "apparel");
                    previous = pawn.outfits.CurrentApparelPolicy?.label ?? "none";
                    pawn.outfits.CurrentApparelPolicy = policy;
                    applied = policy.label;
                    break;
                }
                case "food":
                {
                    if (pawn.foodRestriction == null) throw CommandException.Failed($"{pawn.LabelShort} has no food policy.");
                    var policy = Pick(game.foodRestrictionDatabase.AllFoodRestrictions, p => p.label, wanted, "food");
                    previous = pawn.foodRestriction.CurrentFoodPolicy?.label ?? "none";
                    pawn.foodRestriction.CurrentFoodPolicy = policy;
                    applied = policy.label;
                    break;
                }
                case "drug":
                {
                    if (pawn.drugs == null) throw CommandException.Failed($"{pawn.LabelShort} has no drug policy.");
                    var policy = Pick(game.drugPolicyDatabase.AllPolicies, p => p.label, wanted, "drug");
                    previous = pawn.drugs.CurrentPolicy?.label ?? "none";
                    pawn.drugs.CurrentPolicy = policy;
                    applied = policy.label;
                    break;
                }
                default:
                    throw CommandException.BadArgs($"Unknown policy type '{type}'.",
                        "Use apparel, food or drug.");
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("type", type)
                .Set("from", previous)
                .Set("to", applied)
                .Set("summary", $"{pawn.LabelShort} {type} policy {previous} -> {applied}.");
        }

        private static T Pick<T>(List<T> policies, Func<T, string> label, string wanted, string kind) where T : class
        {
            var exact = policies.Where(p => string.Equals(label(p), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];

            var partial = policies.Where(p =>
                (label(p) ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];

            var error = partial.Count == 0
                ? CommandException.NotFound($"No {kind} policy matches '{wanted}'.")
                : new CommandException(ErrorCode.Ambiguous, $"'{wanted}' matches {partial.Count} {kind} policies.",
                    "Use the exact policy name.");
            error.Payload = JsonValue.NewObject()
                .Set("available", Describe.ToArray(policies.Select(label)));
            throw error;
        }
    }
}
