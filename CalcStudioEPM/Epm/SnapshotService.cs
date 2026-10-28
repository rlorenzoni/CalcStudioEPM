using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Orchestrates a full "Pull Calculations" cycle:
    ///   1. POST /interop/rest/v2/migration/categories/artifacts/export
    ///      with a timestamped snapshotName and "Calculation Manager" category.
    ///      Response contains a "links" array; the "Job Status" entry gives the poll URL.
    ///   2. Poll that URL (GET …/interop/rest/v2/status/migration/{id}) every 3 s.
    ///      status -1 = running, 0 = done, anything else = error.
    ///   3. POST /interop/rest/v2/files/download  { "fileName": "<snapshotName>.zip" }
    ///      to retrieve the ZIP (retried once if it fails).
    ///   4. Delete the snapshot from EPM Cloud — straight after the download, whether or not it
    ///      worked, so it is never left behind (also when the export itself fails).
    ///   5. Extract to {LocalFolderPath}/CalcManager/ and post-process the XML files into
    ///      .csc / .js script files.
    /// The substitution / user variables (and, when asked, dimensions) download in parallel
    /// from the start, with their own live status.
    /// IPM (Auto Predict, calendars, events), when asked, is exported last as a package of its
    /// own — after the metadata, whose member IDs name the members — see <see cref="IpmService"/>.
    /// </summary>
    /// <summary>User-facing stages of a pull, reported as each one starts. Metadata runs in
    /// parallel with the others; it is reported as a step only when the pull waits for it.</summary>
    public enum PullStep { RequestExport, WaitForExport, Download, CleanUp, ExtractScripts, Metadata, Ipm }

    /// <summary>A pull that failed at <see cref="Step"/>; says whether the server snapshot
    /// was removed anyway.</summary>
    public sealed class PullException : EpmException
    {
        public PullStep Step { get; }
        /// <summary>Why the snapshot could not be removed; null when it was.</summary>
        public string CleanupError { get; }

        public PullException(PullStep step, string message, string cleanupError, Exception inner)
            : base(message, inner)
        {
            Step = step;
            CleanupError = cleanupError;
        }
    }

    /// <summary>Outcome of a successful pull.</summary>
    public sealed class PullResult
    {
        public int    Copied;
        public int    Skipped;
        public string ScriptsDir;
        /// <summary>Runtime-prompt variables found and saved to the variable dictionary.</summary>
        public int    Variables;
        /// <summary>Why the server snapshot could not be deleted; null when it was.</summary>
        public string CleanupError;
        /// <summary>Substitution / user variables (and, when asked, dimensions) downloaded alongside.</summary>
        public MetadataResult Metadata;

        /// <summary>Calculation Manager changes in EPM since the previous pull (this PC's deploys
        /// aside): their names, newest first; null when it couldn't be told.</summary>
        public System.Collections.Generic.List<string> ChangedSincePull;
        /// <summary>When the previous pull was (UTC); with <see cref="ChangedSincePull"/>.</summary>
        public DateTime? PreviousPullUtc;

        /// <summary>The IPM pull, when asked; <see cref="IpmError"/> says why it failed alongside
        /// the calculations (on its own, a failure fails the pull).</summary>
        public IpmPullResult Ipm;
        public string IpmError;
    }

    public static class SnapshotService
    {
        private const string CalcManagerCategory  = "Calculation Manager";
        private const string SnapshotNamePrefix   = "AllCalcArtifacts_";

        private static readonly TimeSpan ExportJobTimeout   = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan DownloadJobTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromSeconds(5);

        /// <param name="progress">Detailed technical log lines.</param>
        /// <param name="steps">Coarse, user-facing stage changes.</param>
        /// <param name="includeDimensions">Also download dimensions and their members (the
        /// substitution and user variables are always downloaded).</param>
        /// <param name="metadataStatus">Live status of the parallel metadata download.</param>
        /// <param name="metadataDone">Called (on a worker thread) as soon as the metadata
        /// download ends — also when the pull itself fails.</param>
        public static async Task<PullResult> PullCalculationsAsync(
            ConnectionConfig cfg,
            IProgress<string> progress,
            IProgress<PullStep> steps = null,
            bool includeDimensions = false,
            IProgress<string> metadataStatus = null,
            Action<MetadataResult> metadataDone = null,
            bool includeCalculations = true,
            bool includeMetadata = true,
            bool includeIpm = false)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (!includeCalculations && !includeMetadata && !includeIpm) throw new ArgumentException("Nothing to pull.");

            var result = new PullResult();

            // Metadata for local validation (substitution / user variables, and dimensions when
            // asked), downloaded in parallel with everything else — or on its own
            Task<MetadataResult> metadata = !includeMetadata ? Task.FromResult<MetadataResult>(null) : Task.Run(async () =>
            {
                MetadataResult m;
                try { m = await MetadataService.PullAsync(cfg, includeDimensions, progress, metadataStatus).ConfigureAwait(false); }
                catch (Exception ex) { m = new MetadataResult { DimensionsRequested = includeDimensions, VariablesError = ex.Message }; }
                try { metadataDone?.Invoke(m); } catch { /* the window may be gone */ }
                return m;
            });

            if (!includeCalculations)
            {
                // No Calculation Manager export: the calculation scripts are left alone
                if (includeMetadata)
                {
                    steps?.Report(PullStep.Metadata);
                    result.Metadata = await metadata.ConfigureAwait(false);
                    progress?.Report("Variables" + (includeDimensions ? ", dimensions and members" : "") + " updated; the calculation scripts were left as they are.");
                }
                if (includeIpm) await PullIpmAsync(cfg, result, progress, steps, alone: true).ConfigureAwait(false);
                return result;
            }

            // Unique, human-readable snapshot name (UTC timestamp).
            string snapshotName = SnapshotNamePrefix + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            // Temp folder for the ZIP; always removed at the end
            string tempDir = Path.Combine(Path.GetTempPath(), "CalcStudioEPM_" + Guid.NewGuid().ToString("N"));
            string tempZip = Path.Combine(tempDir, snapshotName + ".zip");

            using (var client = new EpmClient(cfg))
            {
                bool exportAccepted = false, cleanedUp = false;
                PullStep current = PullStep.RequestExport;
                try
                {
                    // ---------------------------------------------------------- //
                    //  Step 1: Submit export job
                    // ---------------------------------------------------------- //
                    steps?.Report(PullStep.RequestExport);
                    progress?.Report($"Submitting '{CalcManagerCategory}' export (snapshot: {snapshotName})…");

                    MigrationExportResponse exportResponse;
                    try
                    {
                        exportResponse = await client
                            .ExportMigrationCategoryAsync(snapshotName, CalcManagerCategory)
                            .ConfigureAwait(false);
                    }
                    catch (EpmException ex)
                    {
                        throw new EpmException("Failed to submit Calculation Manager export: " + ex.Message, ex);
                    }

                    if (exportResponse == null)
                        throw new EpmException("EPM Cloud returned an empty response to the export request.");

                    progress?.Report($"Export response: status={exportResponse.Status}, links={exportResponse.Links?.Count ?? 0}");
                    if (exportResponse.Links != null)
                        foreach (var lnk in exportResponse.Links)
                            progress?.Report($"  Link rel='{lnk.Rel}' href='{lnk.Href}'");

                    string statusUrl = exportResponse.JobStatusUrl;
                    if (string.IsNullOrWhiteSpace(statusUrl))
                        throw new EpmException(
                            "Export response did not include a 'Job Status' link. Cannot poll for completion.");
                    exportAccepted = true;   // from here on, a snapshot may exist on the server

                    string jobIdDisplay = LastSegment(statusUrl);
                    progress?.Report($"Export job accepted (ID: {jobIdDisplay}). Polling for completion…");

                    // ---------------------------------------------------------- //
                    //  Step 2: Poll until done
                    // ---------------------------------------------------------- //
                    steps?.Report(current = PullStep.WaitForExport);
                    InteropJobStatusResponse completedExport =
                        await client.PollInteropJobByUrlAsync(statusUrl, ExportJobTimeout).ConfigureAwait(false);

                    progress?.Report(
                        $"Export job {jobIdDisplay} completed. " +
                        $"details='{completedExport?.Details}' links={completedExport?.Links?.Count ?? 0}");
                    if (completedExport?.Links != null)
                        foreach (var lnk in completedExport.Links)
                            progress?.Report($"  Export-done link rel='{lnk.Rel}' action='{lnk.Action}' href='{lnk.Href}'");

                    // What changed since the previous pull (for the summary), then what EPM reports
                    // as changed so far: Deploy warns about anything newer
                    try
                    {
                        RuleChangeCheck.PullCheck since = await RuleChangeCheck.ChangesSincePullAsync(cfg).ConfigureAwait(false);
                        if (since != null)
                        {
                            result.PreviousPullUtc  = since.PulledAtUtc;
                            result.ChangedSincePull = since.Changed.Select(c => c.ArtifactName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            progress?.Report($"Change check: {result.ChangedSincePull.Count} Calculation Manager change(s) since the last pull" +
                                             (since.Changed.Count == 0 ? "." : ": " + string.Join("; ", since.Changed.Take(20)
                                                 .Select(c => $"{c.ArtifactName} ({c.ModifiedBy}, {RuleChangeCheck.ShowDate(c.ModifiedDate)})"))));
                        }
                    }
                    catch (Exception ex) { progress?.Report($"Change check not possible: {ex.Message}"); }
                    await RuleChangeCheck.RecordBaselineAsync(client, cfg, progress).ConfigureAwait(false);

                    // ---------------------------------------------------------- //
                    //  Step 3: Download (one retry)
                    // ---------------------------------------------------------- //
                    steps?.Report(current = PullStep.Download);
                    Directory.CreateDirectory(tempDir);
                    Exception downloadError = null;
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            await DownloadSnapshotAsync(client, snapshotName, tempZip, progress).ConfigureAwait(false);
                            downloadError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            downloadError = ex;
                            if (attempt == 1)
                            {
                                progress?.Report($"[WARN] Download failed: {ex.Message}");
                                progress?.Report($"Retrying the download once in {DownloadRetryDelay.TotalSeconds:0} s…");
                                await Task.Delay(DownloadRetryDelay).ConfigureAwait(false);
                            }
                        }
                    }

                    // ---------------------------------------------------------- //
                    //  Step 4: Delete the snapshot from EPM Cloud — downloaded or not
                    // ---------------------------------------------------------- //
                    steps?.Report(PullStep.CleanUp);
                    result.CleanupError = await DeleteSnapshotAsync(client, snapshotName, progress).ConfigureAwait(false);
                    cleanedUp = true;

                    if (downloadError != null)
                        throw new PullException(PullStep.Download,
                            "The download failed twice: " + downloadError.Message +
                            (result.CleanupError == null
                                ? " (the export was removed from EPM Cloud)"
                                : " (the export could not be removed from EPM Cloud: " + result.CleanupError + ")"),
                            result.CleanupError, downloadError);

                    // ---------------------------------------------------------- //
                    //  Step 5: Extract ZIP → {LocalFolderPath}/CalcManager/, then scripts
                    // ---------------------------------------------------------- //
                    steps?.Report(PullStep.ExtractScripts);
                    string calcManagerDir = Path.Combine(cfg.LocalFolderPath, PathConstants.CalcManagerDirName);
                    // Empty CalcManager (files of a read-only pull included) but keep the scripts
                    // folder itself: the extraction below empties it and writes the scripts again
                    ReadOnlyScripts.ClearDirectory(calcManagerDir, PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name));
                    Directory.CreateDirectory(calcManagerDir);

                    ZipFile.ExtractToDirectory(tempZip, calcManagerDir);
                    progress?.Report("Extraction complete. Parsing Calculation Manager XML files…");

                    (result.Copied, result.Skipped, result.ScriptsDir, result.Variables) =
                        CalculationParser.ExtractScripts(cfg.LocalFolderPath, cfg.Name, progress);

                    if (cfg.ReadOnly)
                    {
                        int marked = ReadOnlyScripts.Apply(calcManagerDir, readOnly: true);
                        progress?.Report($"Read-only connection: {marked} pulled files marked read-only.");
                    }

                    // ---------------------------------------------------------- //
                    //  Metadata downloaded in parallel: wait for it if still going
                    // ---------------------------------------------------------- //
                    if (includeMetadata && !metadata.IsCompleted)
                    {
                        steps?.Report(PullStep.Metadata);
                        progress?.Report("Scripts ready; waiting for the metadata download to finish…");
                    }
                    result.Metadata = await metadata.ConfigureAwait(false);
                }
                catch (Exception ex) when (exportAccepted && !cleanedUp)
                {
                    // The export failed or timed out: don't leave its snapshot behind
                    progress?.Report($"The pull stopped ({ex.Message}). Removing the export from EPM Cloud so it isn't left behind…");
                    string cleanupError = await DeleteSnapshotAsync(client, snapshotName, progress).ConfigureAwait(false);
                    throw new PullException(current, ex.Message, cleanupError, ex);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                }

                progress?.Report("Pull complete. All Calculation Manager scripts are up to date.");
            }

            if (includeIpm) await PullIpmAsync(cfg, result, progress, steps, alone: false).ConfigureAwait(false);
            return result;
        }

        /// <summary>The IPM export (its own snapshot). <paramref name="alone"/>: nothing else was
        /// pulled, so a failure fails the pull; otherwise it is reported in the result.</summary>
        private static async Task PullIpmAsync(ConnectionConfig cfg, PullResult result, IProgress<string> progress,
                                               IProgress<PullStep> steps, bool alone)
        {
            steps?.Report(PullStep.Ipm);
            try { result.Ipm = await IpmService.PullAsync(cfg, progress).ConfigureAwait(false); }
            catch (Exception ex) when (!alone)
            {
                result.IpmError = ex.Message;
                progress?.Report("[IPM] [WARN] " + ex.Message);
            }
            catch (Exception ex)
            {
                throw new PullException(PullStep.Ipm, ex.Message, null, ex);
            }
        }

        /// <summary>Deletes the snapshot; returns why it couldn't, or null when it was deleted.</summary>
        internal static async Task<string> DeleteSnapshotAsync(EpmClient client, string snapshotName, IProgress<string> progress)
        {
            progress?.Report($"Deleting server snapshot '{snapshotName}'…");
            try
            {
                bool deleted = await client.DeleteSnapshotAsync(snapshotName, progress).ConfigureAwait(false);
                progress?.Report(deleted
                    ? "Server snapshot deleted."
                    : "Note: snapshot not found on server (may have been deleted already).");
                return deleted ? null : "EPM Cloud reported the snapshot as not found.";
            }
            catch (Exception ex)
            {
                progress?.Report($"[WARN] Could not delete server snapshot: {ex.Message}");
                return ex.Message;
            }
        }

        /// <summary>
        /// Asks EPM Cloud for the snapshot file, waits until it is ready, saves it as
        /// <paramref name="tempZip"/> and checks it is a ZIP. Throws on any failure.
        /// </summary>
        internal static async Task DownloadSnapshotAsync(EpmClient client, string snapshotName, string tempZip, IProgress<string> progress)
        {
            progress?.Report($"Requesting download of '{snapshotName}.zip'…");
            MigrationExportResponse downloadResp;
            try
            {
                downloadResp = await client.InitiateSnapshotDownloadAsync(snapshotName).ConfigureAwait(false);
            }
            catch (EpmException ex)
            {
                throw new EpmException($"Failed to initiate download for '{snapshotName}': {ex.Message}", ex);
            }

            if (downloadResp == null)
                throw new EpmException("EPM Cloud returned an empty response to the download request.");

            progress?.Report($"Download response: status={downloadResp.Status}, links={downloadResp.Links?.Count ?? 0}");
            if (downloadResp.Links != null)
                foreach (var lnk in downloadResp.Links)
                    progress?.Report($"  Download link rel='{lnk.Rel}' href='{lnk.Href}'");

            // Poll the download job until the file is ready (status == 0)
            InteropJobStatusResponse completedDlStatus = null;
            if (downloadResp.Status == -1)
            {
                string dlStatusUrl = downloadResp.JobStatusUrl;
                if (string.IsNullOrWhiteSpace(dlStatusUrl))
                    throw new EpmException("Download response had status=-1 but no 'Job Status' link to poll.");

                string dlJobId = LastSegment(dlStatusUrl);
                progress?.Report($"Download job accepted (ID: {dlJobId}). Waiting for file to be ready…");

                completedDlStatus = await client.PollInteropJobByUrlAsync(dlStatusUrl, DownloadJobTimeout).ConfigureAwait(false);

                progress?.Report($"Download job {dlJobId} ready. Completed status links: {completedDlStatus?.Links?.Count ?? 0}");
                if (completedDlStatus?.Links != null)
                    foreach (var lnk in completedDlStatus.Links)
                        progress?.Report($"  Completed link rel='{lnk.Rel}' action='{lnk.Action}' href='{lnk.Href}'");
            }
            else if (downloadResp.Status != 0)
            {
                throw new EpmException($"Download request returned unexpected status {downloadResp.Status}.");
            }

            // Retrieve the actual ZIP via GET
            string fileUrl = completedDlStatus?.Links?
                .FirstOrDefault(l =>
                    string.Equals(l.Action, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(l.Rel, "Job Status", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(l.Rel, "self",       StringComparison.OrdinalIgnoreCase))
                ?.Href;

            if (!string.IsNullOrWhiteSpace(fileUrl))
            {
                progress?.Report($"Fetching file from completed-status link: {fileUrl}");
            }
            else
            {
                fileUrl = $"{client.GetServiceBaseUrl()}/interop/rest/v1/files/outbox/" +
                          Uri.EscapeDataString(snapshotName + ".zip");
                progress?.Report($"No GET link in completed status. Trying v1 outbox: {fileUrl}");
            }

            try
            {
                await client.GetFileAsync(fileUrl, tempZip).ConfigureAwait(false);
            }
            catch (EpmException ex)
            {
                throw new EpmException($"Failed to retrieve snapshot file: {ex.Message}", ex);
            }

            long fileSize = new FileInfo(tempZip).Length;
            progress?.Report($"Fetch complete ({fileSize} bytes). Validating…");

            // Verify the file starts with the ZIP magic bytes "PK" (0x50 0x4B).
            var magic = new byte[4];
            using (var fs = File.OpenRead(tempZip))
                fs.Read(magic, 0, magic.Length);

            if (magic[0] != 0x50 || magic[1] != 0x4B) // 'P','K'
            {
                string serverResponse = "(could not read response body)";
                try { serverResponse = File.ReadAllText(tempZip); } catch { }
                throw new EpmException(
                    $"Downloaded file is not a valid ZIP ({fileSize} bytes). Server response:\n{serverResponse}");
            }

            progress?.Report($"ZIP valid ({fileSize / 1024} KB).");
        }

        private static string LastSegment(string url)
        {
            string u = url.TrimEnd('/');
            int slash = u.LastIndexOf('/');
            return slash >= 0 ? u.Substring(slash + 1) : u;
        }
    }
}
