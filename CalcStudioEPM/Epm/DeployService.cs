using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Orchestrates the full "Deploy Active Tab" workflow:
    ///   1. Build the migration ZIP in memory (PackageBuilder)
    ///   2. Upload it with the 4-step Upload Application Snapshot (v2) protocol —
    ///      EPM extracts the ZIP into a snapshot named after the file
    ///   3. Start the import job (LCM Import v2)
    ///   4. Poll until the import job completes
    ///   5. Delete the temporary snapshot (best-effort)
    /// Every deploy uses a fresh timestamped snapshot name, so there is never an
    /// older same-named snapshot to clear out first.
    /// </summary>
    /// <summary>User-facing stages of a deploy, reported as each one starts.</summary>
    public enum DeployStep { BuildPackage, Upload, Import, CleanUp }

    /// <summary>Outcome of a successful deploy.</summary>
    public sealed class DeployResult
    {
        public string JobId;
        /// <summary>Text EPM attached to the finished import job, if any.</summary>
        public string ImportDetails;
        /// <summary>Why the temporary snapshot could not be deleted; null when it was.</summary>
        public string CleanupError;
        /// <summary>What was deployed where.</summary>
        public DeployPlan Plan;
    }

    public static class DeployService
    {
        private static readonly TimeSpan ImportJobTimeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Deploys the rule described by the given parameters to EPM Cloud.
        /// Reports step-by-step progress through <paramref name="progress"/>.
        /// </summary>
        /// <param name="cfg">Connection configuration.</param>
        /// <param name="planType">Plan type extracted from the file path.</param>
        /// <param name="cube">Cube name extracted from the file path.</param>
        /// <param name="ruleName">Rule name (file name without extension).</param>
        /// <param name="plan">
        /// The active tab content (plain script, or legacy raw CALC XML — see
        /// <see cref="PackageBuilder.Build"/>) and whether Planning gets the rule too.
        /// </param>
        /// <param name="progress">Detailed technical log lines.</param>
        /// <param name="steps">Coarse, user-facing stage changes.</param>
        public static async Task<DeployResult> DeployRuleAsync(
            ConnectionConfig cfg,
            string planType,
            string cube,
            string ruleName,
            DeployPlan plan,
            IProgress<string> progress,
            IProgress<DeployStep> steps = null)
        {
            if (cfg == null)  throw new ArgumentNullException(nameof(cfg));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var result = new DeployResult { Plan = plan };

            // ---------------------------------------------------------- //
            //  Derive snapshot name: NPP_{usernameWithoutDomain}_TMP
            // ---------------------------------------------------------- //
            string snapshotName = BuildSnapshotName(cfg.Username);
            progress?.Report($"Snapshot name: {snapshotName}.zip");

            using (var client = new EpmClient(cfg))
            {
                // ---------------------------------------------------------- //
                //  Step 1: Build ZIP package
                // ---------------------------------------------------------- //
                steps?.Report(DeployStep.BuildPackage);
                progress?.Report($"Building deployment package for {plan.TargetsText}…");
                foreach (string t in plan.InlinedTemplates)
                    progress?.Report($"  Planning copy: inlined template {t}");
                foreach (string b in plan.PlanningBlockers)
                    progress?.Report($"  [ACTION NEEDED] Not deployable to Planning: {b}");

                byte[] zipBytes;
                string deployedRuleXml;
                try
                {
                    zipBytes = PackageBuilder.Build(
                        application: cfg.Application,
                        planType:    planType,
                        cube:        cube,
                        ruleName:    ruleName,
                        calcContent: plan.Content,
                        username:    cfg.Username,
                        localFolder: cfg.LocalFolderPath,
                        planningScript: plan.PlanningScript,
                        calcRuleXml: out deployedRuleXml,
                        progress:    progress,
                        prebuiltCalcRuleXml: plan.RuleXml);
                }
                catch (EpmException) { throw; }
                catch (Exception ex)
                {
                    throw new EpmException("Failed to build deployment package: " + ex.Message, ex);
                }

                progress?.Report($"Package built ({zipBytes.Length / 1024} KB). Proceeding with upload…");

                // Steps 2–4: upload, import, wait for the import
                await UploadAndImportAsync(client, snapshotName, zipBytes, $"Rule '{ruleName}'", result, progress, steps)
                    .ConfigureAwait(false);

                // The import succeeded: what we uploaded is now the deployed version.
                // Record it locally so Execute knows which script and prompts EPM holds.
                try
                {
                    PackageBuilder.SaveDeployedRuleXml(cfg.LocalFolderPath, planType, cube, ruleName, deployedRuleXml);
                }
                catch (Exception ex)
                {
                    progress?.Report($"  [WARN] Could not record the deployed version locally: {ex.Message}");
                }

                // Remember whether Planning is now behind Calculation Manager (Execute warns)
                PlanningPending.Set(cfg.LocalFolderPath, planType, cube, ruleName, pending: !plan.IncludesPlanning);

                // This deploy's change is the rule's new baseline (so it isn't reported as someone else's)
                await RuleChangeCheck.RecordOwnDeployAsync(client, cfg, planType, cube, ruleName, progress).ConfigureAwait(false);

                // ---------------------------------------------------------- //
                //  Step 5: Delete the temporary snapshot (best-effort)
                // ---------------------------------------------------------- //
                steps?.Report(DeployStep.CleanUp);
                result.CleanupError = await CleanUpSnapshotAsync(client, snapshotName, progress)
                    .ConfigureAwait(false);
            }

            return result;
        }

        /// <summary>
        /// Deploys a RuleSet put together by <see cref="RuleSet.Assemble"/> to Calculation
        /// Manager: package, upload, import, then the uploaded XML becomes the pulled RuleSet
        /// (the base of its next deploy) and the temporary snapshot is removed.
        /// </summary>
        public static async Task<DeployResult> DeployRuleSetAsync(
            ConnectionConfig cfg,
            string application,
            string ruleSetName,
            DeployPlan plan,
            IProgress<string> progress,
            IProgress<DeployStep> steps = null)
        {
            if (cfg == null)  throw new ArgumentNullException(nameof(cfg));
            if (plan?.RuleXml == null) throw new ArgumentNullException(nameof(plan));

            var result = new DeployResult { Plan = plan };
            string snapshotName = BuildSnapshotName(cfg.Username);
            progress?.Report($"Snapshot name: {snapshotName}.zip");

            using (var client = new EpmClient(cfg))
            {
                steps?.Report(DeployStep.BuildPackage);
                progress?.Report($"Building the deployment package for RuleSet '{ruleSetName}' (Calculation Manager only)…");
                byte[] zipBytes;
                try
                {
                    zipBytes = PackageBuilder.BuildRuleSet(application, ruleSetName, plan.RuleXml, cfg.Username, cfg.LocalFolderPath);
                }
                catch (Exception ex)
                {
                    throw new EpmException("Failed to build deployment package: " + ex.Message, ex);
                }
                progress?.Report($"Package built ({zipBytes.Length / 1024} KB). Proceeding with upload…");

                await UploadAndImportAsync(client, snapshotName, zipBytes, $"RuleSet '{ruleSetName}'", result, progress, steps)
                    .ConfigureAwait(false);

                try
                {
                    string pulled = RuleSet.PulledPath(cfg.LocalFolderPath, application, ruleSetName);
                    Directory.CreateDirectory(Path.GetDirectoryName(pulled));
                    File.WriteAllText(pulled, plan.RuleXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex)
                {
                    progress?.Report($"  [WARN] Could not record the deployed version locally: {ex.Message}");
                }

                await RuleChangeCheck.RecordOwnDeployAsync(client, cfg, application, RuleSet.FolderName, ruleSetName, progress).ConfigureAwait(false);

                steps?.Report(DeployStep.CleanUp);
                result.CleanupError = await CleanUpSnapshotAsync(client, snapshotName, progress)
                    .ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>
        /// Uploads the package as a snapshot (4-step EPM v2 protocol), starts the LCM import and
        /// waits for it. Throws <see cref="EpmException"/> when any of it fails.
        /// </summary>
        private static async Task UploadAndImportAsync(
            EpmClient client, string snapshotName, byte[] zipBytes, string what,
            DeployResult result, IProgress<string> progress, IProgress<DeployStep> steps)
        {
            // ---------------------------------------------------------- //
            //  Step 2: Upload ZIP (4-step EPM v2 protocol)
            // ---------------------------------------------------------- //
            steps?.Report(DeployStep.Upload);
            try
            {
                await client.UploadSnapshotAsync(snapshotName, zipBytes, progress)
                    .ConfigureAwait(false);
            }
            catch (EpmException ex)
            {
                throw new EpmException($"Upload failed: {ex.Message}", ex);
            }

            progress?.Report("Upload complete.");

            // ---------------------------------------------------------- //
            //  Step 3: Start import job
            // ---------------------------------------------------------- //
            steps?.Report(DeployStep.Import);
            progress?.Report("Starting migration import job…");

            MigrationExportResponse importResp;
            try
            {
                importResp = await client
                    .ImportSnapshotAsync(snapshotName)
                    .ConfigureAwait(false);
            }
            catch (EpmException ex)
            {
                throw new EpmException($"Failed to start import: {ex.Message}", ex);
            }

            if (importResp == null)
                throw new EpmException("EPM Cloud returned an empty response to the import request.");

            // HTTP 200 can still carry a business error (status > 0) — surface EPM's text
            if (importResp.Status > 0)
                throw new EpmException(
                    $"EPM rejected the import (status {importResp.Status}): " +
                    (importResp.Details ?? "(no details)"));

            // A status of 0 means the import completed synchronously (unlikely but handle it)
            if (importResp.Status == 0)
            {
                progress?.Report($"{what} deployed successfully to EPM Cloud (synchronous).");
                return;
            }

            string statusUrl = importResp.JobStatusUrl;
            if (string.IsNullOrWhiteSpace(statusUrl))
                throw new EpmException(
                    "Import response did not include a 'Job Status' link. " +
                    "Cannot poll for completion.");

            string jobId = statusUrl.TrimEnd('/');
            int slash = jobId.LastIndexOf('/');
            if (slash >= 0) jobId = jobId.Substring(slash + 1);
            result.JobId = jobId;

            progress?.Report($"Import job accepted (ID: {jobId}). Waiting for completion…");

            // ------------------------------------------------------ //
            //  Step 4: Poll until import job finishes
            // ------------------------------------------------------ //
            InteropJobStatusResponse finalStatus;
            try
            {
                finalStatus = await client
                    .PollInteropJobByUrlAsync(statusUrl, ImportJobTimeout)
                    .ConfigureAwait(false);
            }
            catch (EpmException ex)
            {
                throw new EpmException(
                    $"Import job {jobId} failed: {ex.Message}", ex);
            }

            string detail = finalStatus?.Details;
            result.ImportDetails = string.IsNullOrWhiteSpace(detail) ? null : detail;
            if (result.ImportDetails == null)
                progress?.Report($"{what} deployed successfully to EPM Cloud (job {jobId}).");
            else
                progress?.Report($"{what} deployed (job {jobId}): {detail}");
        }

        /// <summary>
        /// Deletes the temporary snapshot — best-effort, never fails the deploy.
        /// Returns null on success, otherwise a short reason.
        /// </summary>
        private static async Task<string> CleanUpSnapshotAsync(
            EpmClient client, string snapshotName, IProgress<string> progress)
        {
            progress?.Report($"Deleting temporary snapshot '{snapshotName}'…");
            try
            {
                bool deleted = await client.DeleteSnapshotAsync(snapshotName, progress)
                    .ConfigureAwait(false);
                progress?.Report(deleted
                    ? "Temporary snapshot deleted."
                    : "Note: temporary snapshot not found on server.");
                return deleted ? null : "EPM Cloud reported the snapshot as not found.";
            }
            catch (Exception ex)
            {
                progress?.Report($"Note: could not delete temporary snapshot: {ex.Message}");
                return ex.Message;
            }
        }

        // ------------------------------------------------------------------ //
        //  Helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns a unique snapshot name in the form
        /// <c>NPP_{usernameLocalPart}_{yyyyMMddHHmmss}</c>.
        ///
        /// A timestamp suffix guarantees uniqueness across concurrent or retried deploys,
        /// which avoids the "File already exists or upload is in progress" 409/status-1
        /// error that occurred when a fixed name was left behind by a failed upload attempt.
        /// </summary>
        public static string BuildSnapshotName(string username)
        {
            string part = username ?? "User";
            int at = part.IndexOf('@');
            if (at > 0)
                part = part.Substring(0, at);

            // Replace any characters that are invalid in EPM snapshot names. Dots go too:
            // EPM derives the snapshot name from the uploaded "{name}.zip", so keep the
            // only dot in the file name the one before "zip".
            part = System.Text.RegularExpressions.Regex.Replace(part, @"[^\w-]", "_");

            return $"NPP_{part}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        }
    }
}
