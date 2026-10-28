using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Progress dialog for "Pull Calculations" — see <see cref="StepProgressForm{TResult}"/>
    /// for the shared checklist / summary / details behaviour. Its rows follow what is pulled:
    /// the calculation steps, the metadata (variables, dimensions) running in parallel with its
    /// own live status, or the metadata alone — and IPM last, when asked.
    ///
    /// Usage:
    ///   using (var dlg = new PullProgressForm(cfg))
    ///       dlg.ShowDialog();
    /// </summary>
    public class PullProgressForm : StepProgressForm<PullResult>
    {
        private static readonly PullStep[] CalculationSteps =
            { PullStep.RequestExport, PullStep.WaitForExport, PullStep.Download, PullStep.CleanUp, PullStep.ExtractScripts };

        private static string Label(PullStep s, bool dimensions, bool parallel)
        {
            switch (s)
            {
                case PullStep.RequestExport:  return "Request an export from EPM Cloud";
                case PullStep.WaitForExport:  return "Wait for EPM Cloud to prepare the export";
                case PullStep.Download:       return "Download the export";
                case PullStep.CleanUp:        return "Remove the temporary export from EPM Cloud";
                case PullStep.ExtractScripts: return "Extract the calculation scripts";
                case PullStep.Ipm:            return "Export IPM configuration, calendars and events, and extract them";
                default:
                    string what = dimensions ? "variables, dimensions and members" : "substitution and user variables";
                    return parallel ? "In parallel: " + what : "Download " + what;
            }
        }

        private readonly ConnectionConfig  _cfg;
        private readonly bool              _dimensions, _calculations, _metadata, _ipm;
        private readonly List<PullStep>    _rows;
        private readonly IProgress<string> _metadataStatus;

        /// <param name="includeCalculations">Export Calculation Manager and write the scripts.</param>
        /// <param name="includeMetadata">Substitution and user variables (and dimensions, see next).</param>
        /// <param name="includeDimensions">With the metadata, also dimensions and members.</param>
        /// <param name="includeIpm">Export IPM (Auto Predict, calendars, events) as its own package.</param>
        public PullProgressForm(ConnectionConfig cfg, bool includeCalculations, bool includeMetadata, bool includeDimensions,
                                bool includeIpm = false)
            : this(cfg, includeCalculations, includeMetadata, includeDimensions, includeIpm,
                   Rows(includeCalculations, includeMetadata, includeIpm)) { }

        private PullProgressForm(ConnectionConfig cfg, bool calculations, bool metadata, bool dimensions, bool ipm, List<PullStep> rows)
            : base(title:        $"Pull Calculations — {ConnLabel(cfg)}",
                   headline:     calculations ? $"Pulling calculation scripts from {ConnLabel(cfg)}…"
                                 : !metadata  ? $"Pulling IPM configuration from {ConnLabel(cfg)}…"
                                              : $"Updating variables{(dimensions ? " and dimensions" : "")}{(ipm ? " and IPM" : "")} from {ConnLabel(cfg)}…",
                   failHeadline: "Pull failed",
                   steps:        rows.Select(r => Label(r, dimensions, parallel: calculations)).ToArray())
        {
            _cfg = cfg; _calculations = calculations; _metadata = metadata; _dimensions = dimensions; _ipm = ipm; _rows = rows;
            if (metadata) _metadataStatus = ParallelStatus(Row(PullStep.Metadata));
        }

        private static List<PullStep> Rows(bool calculations, bool metadata, bool ipm)
        {
            var rows = new List<PullStep>();
            if (calculations) rows.AddRange(CalculationSteps);
            if (metadata) rows.Add(PullStep.Metadata);
            if (ipm) rows.Add(PullStep.Ipm);
            return rows;
        }

        /// <summary>The row showing a step; -1 when this pull doesn't have it.</summary>
        private int Row(PullStep s) => _rows.IndexOf(s);

        private static string ConnLabel(ConnectionConfig cfg) =>
            string.IsNullOrWhiteSpace(cfg?.Name) ? "EPM Cloud" : cfg.Name;

        protected override Task<PullResult> RunAsync(IProgress<string> log) =>
            SnapshotService.PullCalculationsAsync(_cfg, log, StepProgress<PullStep>(Row), _dimensions,
                _metadataStatus,
                m => FinishParallelStep(Row(PullStep.Metadata),
                                        MetadataWarning(m) ? StepState.Warning : StepState.Done, MetadataText(m)),
                includeCalculations: _calculations, includeMetadata: _metadata, includeIpm: _ipm);

        private static bool MetadataWarning(MetadataResult m) => m.VariablesError != null || m.DimensionsError != null;

        private static string MetadataText(MetadataResult m)
        {
            var parts = new List<string>
            {
                m.VariablesError == null
                    ? $"{m.SubstitutionVariables} substitution, {m.UserVariables} user variables"
                    : "variables failed",
            };
            if (m.DimensionsRequested)
                parts.Add(m.DimensionsError == null
                    ? $"{m.Dimensions} dimensions, {m.Members:N0} members"
                    : m.Dimensions > 0 ? $"{m.Dimensions} dimensions, some failed" : "dimensions failed");
            return string.Join("; ", parts);
        }

        /// <summary>A failed download is shown on its own row (the clean-up after it still ran).</summary>
        protected override int FailedStep(Exception ex, int currentStep) =>
            ex is PullException pe ? Math.Max(Row(pe.Step), 0) : currentStep;

        protected override void OnFailed(Exception ex)
        {
            if (ex is PullException pe && Row(PullStep.CleanUp) >= 0)
                SetStepState(Row(PullStep.CleanUp),
                    pe.CleanupError == null ? StepState.Done : StepState.Warning,
                    pe.CleanupError == null ? "removed" : "could not remove it (see details)");
        }

        private static string IpmText(IpmPullResult p) =>
            $"{p.AutoPredictJobs} Auto Predict job{(p.AutoPredictJobs == 1 ? "" : "s")}, {p.Calendars} calendar{(p.Calendars == 1 ? "" : "s")}" +
            (p.Events ? ", events" : "");

        /// <summary>The IPM row and summary line; true when it is a warning.</summary>
        private bool ShowIpm(PullResult r, out string summary)
        {
            summary = "";
            if (!_ipm) return false;
            int row = Row(PullStep.Ipm);
            if (r.IpmError != null || r.Ipm == null)
            {
                SetStepState(row, StepState.Warning, "failed (see details)");
                summary = "\nIPM: " + (r.IpmError ?? "not pulled");
                return true;
            }
            bool warn = r.Ipm.Skipped > 0 || r.Ipm.CleanupError != null;
            SetStepState(row, warn ? StepState.Warning : StepState.Done,
                IpmText(r.Ipm) + (r.Ipm.Skipped > 0 ? $"; {r.Ipm.Skipped} skipped" : "") +
                (r.Ipm.CleanupError != null ? "; export not removed from EPM" : ""));
            summary = $"\nIPM ({IpmText(r.Ipm)}) is ready to edit in {r.Ipm.Folder}";
            return warn;
        }

        protected override void OnSucceeded(PullResult r)
        {
            if (!_calculations)
            {
                // Variables (and dimensions), IPM: the rows already show the counts
                MetadataResult only = r.Metadata;
                bool metaWarn = only != null && MetadataWarning(only);
                bool warn = ShowIpm(r, out string ipmText) | metaWarn;
                SetHeadline(warn ? (_metadata ? "Update complete, with warnings" : "Pull complete, with warnings")
                                 : (_metadata ? "Update complete" : "Pull complete"),
                            warn ? Color.DarkOrange : Color.DarkGreen);
                string varsText = !_metadata ? ""
                    : metaWarn ? (only.VariablesError ?? only.DimensionsError) + " (see details; the previous data is kept).\n"
                    : $"The variables{(_dimensions ? ", dimensions and members" : "")} are up to date.\n";
                SetSummary(varsText + "The calculation scripts were left as they are." + ipmText);
                return;
            }

            int extract = Row(PullStep.ExtractScripts);
            int cleanup = Row(PullStep.CleanUp);
            bool warnings = false;

            if (r.Copied == 0)
            {
                SetStepState(extract, StepState.Warning, "no scripts found");
                warnings = true;
            }
            else if (r.Skipped > 0)
            {
                SetStepState(extract, StepState.Warning, $"{r.Copied} extracted, {r.Skipped} skipped");
                warnings = true;
            }
            else
            {
                SetStepState(extract, StepState.Done, $"{r.Copied} scripts, {r.Variables} variables");
            }

            // The metadata row already shows its final state (set when that download ended)
            MetadataResult m = r.Metadata;
            string metaText = "";
            if (m != null && MetadataWarning(m))
            {
                warnings = true;
                metaText = "\nValidation metadata: " + (m.VariablesError ?? m.DimensionsError) + " (see details; the previous data is kept).";
            }

            if (r.CleanupError != null)
            {
                SetStepState(cleanup, StepState.Warning, "could not remove it (see details)");
                warnings = true;
            }
            warnings |= ShowIpm(r, out string ipmSummary);

            SetHeadline(warnings ? "Pull complete, with warnings" : "Pull complete",
                        warnings ? Color.DarkOrange : Color.DarkGreen);

            // What changed in EPM since the previous pull (this PC's own deploys aside)
            string changes = "";
            if (r.ChangedSincePull != null && r.PreviousPullUtc != null)
            {
                string when = r.PreviousPullUtc.Value.ToLocalTime().ToString("g");
                int n = r.ChangedSincePull.Count;
                changes = n == 0
                    ? $"\nNo calculations had changed in EPM since your last pull ({when})."
                    : $"\n{n} calculation{(n == 1 ? "" : "s")} changed in EPM since your last pull ({when}): " +
                      string.Join(", ", r.ChangedSincePull.Take(5)) + (n > 5 ? $" and {n - 5} more (see details)." : ".");
            }

            SetSummary(r.Copied == 0
                ? "No calculation scripts were found in the export. Show details for the full log."
                : $"{r.Copied} scripts are ready in {r.ScriptsDir}" +
                  (r.Skipped > 0
                      ? $"\n{r.Skipped} artifacts could not be extracted — show details for the list."
                      : "") + changes + metaText + ipmSummary);
        }
    }
}
