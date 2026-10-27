using System.Drawing;
using System.Threading.Tasks;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Progress dialog for "Deploy Active Tab" — see <see cref="StepProgressForm{TResult}"/>
    /// for the shared checklist / summary / details behaviour.
    ///
    /// Usage:
    ///   using (var dlg = new DeployProgressForm(cfg, planType, cube, ruleName, plan))
    ///       dlg.ShowDialog();
    /// </summary>
    public class DeployProgressForm : StepProgressForm<DeployResult>
    {
        // Same order as the DeployStep enum — the enum value is the row index.
        private static string[] Steps(DeployPlan plan) => new[]
        {
            "Build the deployment package",                   // DeployStep.BuildPackage
            "Upload the package to EPM Cloud",                // DeployStep.Upload
            plan.IsRuleSet                                    // DeployStep.Import
                ? "Import the RuleSet into Calculation Manager"
                : plan.IncludesPlanning
                    ? "Import the rule into Calculation Manager and Planning"
                    : "Import the rule into Calculation Manager",
            "Remove the temporary snapshot from EPM Cloud",   // DeployStep.CleanUp
        };

        private readonly ConnectionConfig _cfg;
        private readonly string           _planType;
        private readonly string           _cube;
        private readonly string           _ruleName;
        private readonly DeployPlan       _plan;

        public DeployProgressForm(
            ConnectionConfig cfg,
            string planType,
            string cube,
            string ruleName,
            DeployPlan plan)
            : base(title:        $"Deploy — {ruleName}",
                   headline:     $"Deploying '{ruleName}' to {ConnLabel(cfg)} ({plan.TargetsText})…",
                   failHeadline: "Deploy failed",
                   steps:        Steps(plan))
        {
            _cfg      = cfg;
            _planType = planType;
            _cube     = cube;
            _ruleName = ruleName;
            _plan     = plan;
        }

        private static string ConnLabel(ConnectionConfig cfg) =>
            string.IsNullOrWhiteSpace(cfg?.Name) ? "EPM Cloud" : cfg.Name;

        /// <summary>The red reminder shown when Planning did not get the rule.</summary>
        internal static string ActionNeededText(string ruleName, DeployPlan plan) =>
            plan.ActionNeeded
                ? "ACTION NEEDED: open Calculation Manager and deploy '" + ruleName + "' to Planning.\n" +
                  "It uses complex templates, which only Calculation Manager can deploy to Planning, " +
                  "so Planning still runs the previous version."
                : "ACTION NEEDED: Planning still runs the previous version of '" + ruleName + "'. " +
                  "Deploy it to Planning from Calculation Manager, or with 'Deploy CM+HP'.";

        /// <summary>A RuleSet put together by <see cref="RuleSet.Assemble"/> (plan from <see cref="DeployPlan.ForRuleSet"/>).</summary>
        public static DeployProgressForm ForRuleSet(ConnectionConfig cfg, string application, string ruleSetName, DeployPlan plan) =>
            new DeployProgressForm(cfg, application, null, ruleSetName, plan);

        protected override Task<DeployResult> RunAsync(System.IProgress<string> log) =>
            _plan.IsRuleSet
                ? DeployService.DeployRuleSetAsync(_cfg, _planType, _ruleName, _plan, log, StepProgress<DeployStep>(s => (int)s))
                : DeployService.DeployRuleAsync(
                    _cfg, _planType, _cube, _ruleName, _plan,
                    log, StepProgress<DeployStep>(s => (int)s));

        protected override void OnSucceeded(DeployResult r)
        {
            if (r.JobId != null)
                SetStepState((int)DeployStep.Import, StepState.Done, $"job {r.JobId}");

            bool warnings = r.CleanupError != null || _plan.ActionNeeded;
            if (r.CleanupError != null)
                SetStepState((int)DeployStep.CleanUp, StepState.Warning,
                    "could not remove it (see details)");

            SetHeadline(warnings ? "Deploy complete, with warnings" : "Deploy complete",
                        warnings ? Color.DarkOrange : Color.DarkGreen);

            string templates = _plan.InlinedTemplates.Count == 0 ? "" :
                $"\nPlanning copy built with {_plan.InlinedTemplates.Count} template(s) inlined.";
            SetSummary(
                $"'{_ruleName}' ({_planType}{(_cube != null ? " / " + _cube : " RuleSet")}) is now deployed to {ConnLabel(_cfg)}: " +
                $"{_plan.TargetsText}." + templates +
                (_plan.IsRuleSet ? "\nDeploy it to Planning from Calculation Manager when you want Planning to run this version." : "") +
                (r.ImportDetails != null ? $"\nEPM Cloud reported: {r.ImportDetails}" : ""));

            // CM-only by choice: a reminder. CM+HP asked for but blocked: the red alert.
            if (_plan.ActionNeeded)
                SetAlert(ActionNeededText(_ruleName, _plan));
        }
    }
}
