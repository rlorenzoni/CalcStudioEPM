using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace CalcStudioEPM.Forms
{
    /// <summary>What an IPM deploy and / or Auto Predict run did.</summary>
    public sealed class IpmRunResult
    {
        public DeployResult  Deploy;
        public ExecuteResult Run;
    }

    /// <summary>
    /// Progress dialog for IPM files: deploying one (to Planning only — IPM is not part of
    /// Calculation Manager), running an Auto Predict job, or both (deploy the changes, then run).
    /// See <see cref="StepProgressForm{TResult}"/> for the shared look and feel.
    /// </summary>
    public class IpmProgressForm : StepProgressForm<IpmRunResult>
    {
        private static string[] DeploySteps(IpmDeployPlan plan) => new[]
        {
            "Build the deployment package",                   // DeployStep.BuildPackage
            "Upload the package to EPM Cloud",                // DeployStep.Upload
            $"Import {plan.Label} into Planning",             // DeployStep.Import
            "Remove the temporary snapshot from EPM Cloud",   // DeployStep.CleanUp
        };
        private static readonly string[] RunSteps =
        {
            "Start the Auto Predict job in EPM Cloud",        // ExecuteStep.Submit
            "Wait for the job to finish",                     // ExecuteStep.Run
        };

        private readonly ConnectionConfig _cfg;
        private readonly IpmDeployPlan    _plan;      // null = nothing to deploy
        private readonly string           _jobName;   // null = don't run
        private readonly bool             _forceRun;
        private readonly string           _paginatedDim;

        /// <param name="plan">What to deploy first; null to only run.</param>
        /// <param name="jobName">The Auto Predict job to run afterwards; null to only deploy.</param>
        public IpmProgressForm(ConnectionConfig cfg, IpmDeployPlan plan, string jobName = null, bool forceRun = false, string paginatedDim = null)
            : base(title:        jobName != null ? $"Execute — {jobName}" : $"Deploy — {plan.ArtifactName}",
                   headline:     jobName != null ? $"Running Auto Predict '{jobName}' in {ConnLabel(cfg)}…"
                                                 : $"Deploying {plan.Label} to {ConnLabel(cfg)} (Planning)…",
                   failHeadline: jobName != null ? "Execution failed" : "Deploy failed",
                   steps:        (plan != null ? DeploySteps(plan) : new string[0]).Concat(jobName != null ? RunSteps : new string[0]).ToArray(),
                   withResponse: jobName != null)
        {
            _cfg = cfg; _plan = plan; _jobName = jobName; _forceRun = forceRun; _paginatedDim = paginatedDim;
        }

        private static string ConnLabel(ConnectionConfig cfg) =>
            string.IsNullOrWhiteSpace(cfg?.Name) ? "EPM Cloud" : cfg.Name;

        private int RunOffset => _plan != null ? 4 : 0;

        protected override async Task<IpmRunResult> RunAsync(IProgress<string> log)
        {
            var result = new IpmRunResult();
            if (_plan != null)
            {
                result.Deploy = await IpmService.DeployAsync(_cfg, _plan, log, StepProgress<DeployStep>(s => (int)s)).ConfigureAwait(false);
                if (_jobName != null) log.Report("──── Executing ────");
            }
            if (_jobName != null)
                result.Run = await IpmService.RunAutoPredictAsync(_cfg, _jobName, _forceRun, _paginatedDim, log,
                                                                  StepProgress<ExecuteStep>(s => RunOffset + (int)s - 1)).ConfigureAwait(false);
            return result;
        }

        protected override void OnSucceeded(IpmRunResult r)
        {
            bool warnings = false;
            var summary = new List<string>();
            if (r.Deploy != null)
            {
                if (r.Deploy.JobId != null) SetStepState((int)DeployStep.Import, StepState.Done, $"job {r.Deploy.JobId}");
                if (r.Deploy.CleanupError != null)
                {
                    SetStepState((int)DeployStep.CleanUp, StepState.Warning, "could not remove it (see details)");
                    warnings = true;
                }
                summary.Add($"{char.ToUpper(_plan.Label[0])}{_plan.Label.Substring(1)} is now deployed to {ConnLabel(_cfg)} (Planning)." +
                            (r.Deploy.ImportDetails != null ? $"\nEPM Cloud reported: {r.Deploy.ImportDetails}" : ""));
            }
            if (r.Run != null)
            {
                SetStepState(RunOffset + 1, StepState.Done, $"{r.Run.StatusText} (job {r.Run.JobId})");
                summary.Add($"Job {r.Run.JobId} finished: {r.Run.StatusText}.");
                SetResponse(r.Run.Response);
            }
            SetHeadline(_jobName != null ? $"'{_jobName}' completed" + (warnings ? ", with warnings" : "")
                                         : warnings ? "Deploy complete, with warnings" : "Deploy complete",
                        warnings ? Color.DarkOrange : Color.DarkGreen);
            SetSummary(string.Join("\n", summary));
        }

        protected override void OnFailed(Exception ex)
        {
            if (ex is RuleFailedException rf)
                SetResponse(rf.Result.Response);
        }
    }
}
