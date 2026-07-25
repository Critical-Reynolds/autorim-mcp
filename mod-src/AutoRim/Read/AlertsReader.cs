using System;
using System.Collections.Generic;
using AutoRim.Core;
using RimWorld;

namespace AutoRim.Read
{
    /// <summary>
    /// Reads the game's alerts.
    ///
    /// AlertsReadout keeps its live instances private and only exposes the static list of
    /// alert *types*, so we instantiate our own set once and evaluate Active on demand — the
    /// same thing the vanilla readout does each frame. Alert.Active is a computed property, so
    /// a separately-owned instance reports the same answer as the on-screen one.
    /// </summary>
    internal static class AlertsReader
    {
        private static List<Alert> _instances;

        private static List<Alert> Instances()
        {
            if (_instances != null) return _instances;

            _instances = new List<Alert>();
            var types = AlertsReadout.allAlertTypesCached;
            if (types == null) return _instances;

            foreach (var type in types)
            {
                if (type == null) continue;
                try
                {
                    var alert = (Alert)Activator.CreateInstance(type);
                    if (!alert.EnabledWithActiveExpansions) continue; // skip alerts for DLC that is not active
                    _instances.Add(alert);
                }
                catch (Exception ex)
                {
                    ARLog.Exception($"instantiating alert {type.Name}", ex);
                }
            }

            ARLog.Message($"Alert reader ready ({_instances.Count} alert types).");
            return _instances;
        }

        /// <summary>
        /// Currently-active alerts. Individual alerts that throw while evaluating are skipped
        /// rather than failing the whole read; a snapshot is more useful slightly incomplete
        /// than not at all.
        /// </summary>
        public static List<Alert> Active()
        {
            var active = new List<Alert>();
            foreach (var alert in Instances())
            {
                try
                {
                    if (alert.Active) active.Add(alert);
                }
                catch (Exception)
                {
                }
            }
            return active;
        }

        public static string SafeLabel(Alert alert)
        {
            try
            {
                return alert.Label;
            }
            catch (Exception)
            {
                return alert.GetType().Name;
            }
        }

        public static string SafeExplanation(Alert alert)
        {
            try
            {
                return alert.GetExplanation().ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
