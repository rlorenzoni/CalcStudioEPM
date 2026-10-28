using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>Outcome of an IPM pull.</summary>
    public sealed class IpmPullResult
    {
        public int AutoPredictJobs, Calendars, Skipped;
        public bool Events;
        public string Folder;
        /// <summary>Why the server snapshot could not be deleted; null when it was.</summary>
        public string CleanupError;
    }

    /// <summary>What deploying one IPM file sends: the artifact, its XML and what changed.</summary>
    public sealed class IpmDeployPlan
    {
        internal IpmFiles.Kind Kind;
        /// <summary>LCM artifact: the job's name, "Calendar" or "Events", under <see cref="ParentPath"/>.</summary>
        public string ArtifactName, ParentPath;
        /// <summary>The pulled file the deployed XML replaces once imported.</summary>
        public string PulledPath;
        public string Xml;
        public List<string> Changes = new List<string>();
        public List<string> Notes = new List<string>();

        public bool IsAutoPredict => Kind == IpmFiles.Kind.AutoPredict;
        public string Label =>
            Kind == IpmFiles.Kind.AutoPredict ? $"Auto Predict '{ArtifactName}'"
            : Kind == IpmFiles.Kind.Calendars ? "the IPM calendars" : "the IPM events";
    }

    /// <summary>
    /// IPM (Intelligent Performance Management) artifacts of Planning — Auto Predict jobs,
    /// calendars and events (Global Artifacts › Auto Predict, Calendar, Events):
    ///
    /// Pull — exports only those artifacts, as a package of their own:
    ///   1. GET  /interop/rest/v2/migration/categories/list, then
    ///      POST /interop/rest/v2/migration/categories/artifacts/list per category until one holds
    ///      Global Artifacts › Auto Predict / Calendar / Events (their paths go in the export)
    ///   2. POST /interop/rest/v2/migration/categories/artifacts/export with selectedArtifacts
    ///   3. wait, download, delete the snapshot (as the calculation pull does)
    ///   4. extract to {LocalFolder}\IPM and write the files to edit to {name}.Scripts\IPM
    ///
    /// Deploy — Auto Predict is not part of Calculation Manager, so a deploy imports into
    /// Planning only: an LCM package with just HP-{app} and the one artifact, uploaded and
    /// imported like a rule's.
    ///
    /// Run — POST …/applications/{app}/jobs { jobType: "Auto Predict", jobName,
    /// parameters: { forceRun, paginatedDim } }, polled like a rule.
    /// </summary>
    internal static class IpmService
    {
        private const string SnapshotNamePrefix = "IPM_";
        public const string AutoPredictJobType = "Auto Predict";

        private static readonly TimeSpan ExportJobTimeout   = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromSeconds(5);

        /// <summary>Categories that never hold Planning's global artifacts (not searched).</summary>
        private static readonly string[] OtherCategories =
            { "Calculation Manager", "Groups and Membership", "Document Repository", "Data Management", "Data Integration" };

        // ------------------------------------------------------------------ //
        //  Pull
        // ------------------------------------------------------------------ //

        public static async Task<IpmPullResult> PullAsync(ConnectionConfig cfg, IProgress<string> log)
        {
            void Log(string m) => log?.Report("[IPM] " + m);
            var result = new IpmPullResult();
            string snapshotName = SnapshotNamePrefix + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string tempDir = Path.Combine(Path.GetTempPath(), "CalcStudioEPM_" + Guid.NewGuid().ToString("N"));
            string tempZip = Path.Combine(tempDir, snapshotName + ".zip");

            using (var client = new EpmClient(cfg))
            {
                var (category, paths) = await FindArtifactsAsync(client, cfg.Application, Log).ConfigureAwait(false);

                Log($"Submitting the IPM export from '{category}' (snapshot: {snapshotName}): {string.Join(" · ", paths)}");
                MigrationExportResponse export = await client.ExportMigrationArtifactsAsync(snapshotName, category, paths).ConfigureAwait(false)
                    ?? throw new EpmException("EPM Cloud returned an empty response to the IPM export request.");
                if (export.Status > 0)
                    throw new EpmException($"EPM rejected the IPM export (status {export.Status}): {export.Details ?? "(no details)"}");
                string statusUrl = export.JobStatusUrl;
                bool cleanedUp = false;
                try
                {
                    if (export.Status != 0)
                    {
                        if (string.IsNullOrWhiteSpace(statusUrl))
                            throw new EpmException("The IPM export response has no 'Job Status' link to wait on.");
                        Log("Export accepted; waiting for it…");
                        InteropJobStatusResponse done = await client.PollInteropJobByUrlAsync(statusUrl, ExportJobTimeout).ConfigureAwait(false);
                        Log($"Export done. details='{done?.Details}'");
                    }

                    Directory.CreateDirectory(tempDir);
                    Exception downloadError = null;
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            await SnapshotService.DownloadSnapshotAsync(client, snapshotName, tempZip, log).ConfigureAwait(false);
                            downloadError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            downloadError = ex;
                            if (attempt == 1)
                            {
                                Log($"[WARN] Download failed: {ex.Message}. Retrying once in {DownloadRetryDelay.TotalSeconds:0} s…");
                                await Task.Delay(DownloadRetryDelay).ConfigureAwait(false);
                            }
                        }
                    }

                    result.CleanupError = await SnapshotService.DeleteSnapshotAsync(client, snapshotName, log).ConfigureAwait(false);
                    cleanedUp = true;
                    if (downloadError != null)
                        throw new EpmException("The IPM download failed twice: " + downloadError.Message, downloadError);

                    Extract(cfg, tempZip, Log);
                    WriteEditFiles(cfg, result, Log);
                }
                catch (Exception ex) when (!cleanedUp)
                {
                    Log($"The IPM pull stopped ({ex.Message}). Removing its export from EPM Cloud…");
                    string cleanup = await SnapshotService.DeleteSnapshotAsync(client, snapshotName, log).ConfigureAwait(false);
                    throw new EpmException(ex.Message + (cleanup == null ? "" : $" (the export could not be removed from EPM Cloud: {cleanup})"), ex);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
            Log($"IPM ready in {result.Folder}: {result.AutoPredictJobs} Auto Predict job(s), {result.Calendars} calendar(s)" +
                (result.Events ? ", events." : "."));
            return result;
        }

        /// <summary>
        /// The migration category holding Planning's Global Artifacts, and the paths to export:
        /// the Auto Predict folder ("…/Auto Predict/"), Calendar and Events.
        /// </summary>
        private static async Task<(string Category, List<string> Paths)> FindArtifactsAsync(EpmClient client, string app, Action<string> log)
        {
            var categories = new List<string>();
            try
            {
                JObject list = JObject.Parse(await client.GetLargeJsonAsync("/interop/rest/v2/migration/categories/list").ConfigureAwait(false));
                categories = (list["items"] as JArray ?? new JArray())
                    .Select(i => (string)i["categoryName"] ?? (string)i["name"])
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                log("Migration categories: " + string.Join(", ", categories));
            }
            catch (EpmException ex) { log("Could not list the migration categories: " + ex.Message); }
            if (categories.Count == 0) categories.Add("Core");

            var searched = new List<string>();
            foreach (string category in categories
                         .Where(c => !OtherCategories.Contains(c, StringComparer.OrdinalIgnoreCase))
                         .OrderBy(c => string.Equals(c, "Core", StringComparison.OrdinalIgnoreCase) ? 0
                                     : c.IndexOf("Planning", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2))
            {
                searched.Add(category);
                JObject tree;
                try
                {
                    tree = JObject.Parse(await client.PostLargeJsonAsync("/interop/rest/v2/migration/categories/artifacts/list",
                                                                         new { categoryName = category }).ConfigureAwait(false));
                }
                catch (Exception ex) { log($"'{category}': could not list its artifacts — {ex.Message}"); continue; }
                if ((int?)tree["status"] > 0) { log($"'{category}': {(string)tree["details"]}"); continue; }

                var found = new List<(string What, string Path)>();
                void Walk(JToken nodes, string parent)
                {
                    foreach (JObject n in (nodes as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string name = (string)n["name"] ?? (string)n["displayName"] ?? "";
                        string full = ((string)n["path"] ?? "").TrimEnd('/') + "/" + name;
                        if (string.Equals(parent, "Global Artifacts", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(name, IpmFiles.AutoPredictFolder, StringComparison.OrdinalIgnoreCase)) found.Add((name, full + "/"));
                            else if (string.Equals(name, IpmFiles.CalendarArtifact, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(name, IpmFiles.EventsArtifact, StringComparison.OrdinalIgnoreCase)) found.Add((name, full));
                        }
                        Walk(n["children"], name);
                    }
                }
                Walk(tree["items"], null);
                if (found.Count == 0) { log($"'{category}': no IPM artifacts."); continue; }

                // Several applications: this connection's
                var mine = found.Where(f => f.Path.IndexOf("/" + app + "/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (mine.Count > 0) found = mine;
                List<string> paths = found.GroupBy(f => f.What, StringComparer.OrdinalIgnoreCase).Select(g => g.First().Path).ToList();
                log($"'{category}' holds the IPM artifacts: {string.Join(" · ", paths)}");
                return (category, paths);
            }
            throw new EpmException(
                "EPM's migration lists don't show the IPM artifacts (Global Artifacts › Auto Predict, Calendar, Events) " +
                $"in any category searched ({string.Join(", ", searched)}). Check in EPM (Tools › Migration) that the " +
                "application has them and that this user is a Service Administrator.");
        }

        /// <summary>Unzips the export into {LocalFolder}\IPM (emptied first), keeping only the IPM artifacts.</summary>
        private static void Extract(ConnectionConfig cfg, string zip, Action<string> log)
        {
            string dir = IpmFiles.PackageDir(cfg.LocalFolderPath);
            ReadOnlyScripts.ClearDirectory(dir);
            Directory.CreateDirectory(dir);
            ZipFile.ExtractToDirectory(zip, dir);

            string hp = IpmFiles.HpDir(cfg.LocalFolderPath)
                ?? throw new EpmException("The IPM export holds no Planning (HP-…) package.");
            foreach (string other in Directory.GetDirectories(dir).Where(d => !string.Equals(d, hp, StringComparison.OrdinalIgnoreCase)))
            {
                log($"Not part of IPM, removed: {Path.GetFileName(other)}");
                ReadOnlyScripts.DeleteDirectory(other);
            }
            string resource = Path.Combine(hp, "resource");
            string global = Path.Combine(resource, "Global Artifacts") + Path.DirectorySeparatorChar;
            string autoPredict = Path.Combine(global, IpmFiles.AutoPredictFolder) + Path.DirectorySeparatorChar;
            if (Directory.Exists(resource))
                foreach (string f in Directory.EnumerateFiles(resource, "*", SearchOption.AllDirectories).ToList())
                {
                    bool keep = f.StartsWith(autoPredict, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(f, global + IpmFiles.CalendarArtifact + ".xml", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(f, global + IpmFiles.EventsArtifact + ".xml", StringComparison.OrdinalIgnoreCase);
                    if (keep) continue;
                    log($"Not part of IPM, removed: {f.Substring(resource.Length + 1)}");
                    File.Delete(f);
                }
            ReadOnlyScripts.DeleteEmptyFolders(resource);
        }

        /// <summary>Writes the files to edit ({name}.Scripts\IPM) from the pulled package.</summary>
        internal static void WriteEditFiles(ConnectionConfig cfg, IpmPullResult result, Action<string> log)
        {
            string edit = IpmFiles.EditDir(cfg);
            ReadOnlyScripts.ClearFiles(edit);
            Directory.CreateDirectory(edit);
            result.Folder = edit;
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            IpmContext ctx = IpmContext.For(cfg);

            string global = IpmFiles.GlobalArtifactsDir(cfg.LocalFolderPath);
            string apDir = global == null ? null : Path.Combine(global, IpmFiles.AutoPredictFolder);
            if (apDir != null && Directory.Exists(apDir))
            {
                string target = Path.Combine(edit, IpmFiles.AutoPredictFolder);
                Directory.CreateDirectory(target);
                foreach (string xmlFile in Directory.EnumerateFiles(apDir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        string xml = File.ReadAllText(xmlFile, Encoding.UTF8);
                        string name = AutoPredictIni.JobName(xml);
                        File.WriteAllText(Path.Combine(target, PathConstants.ToFileName(name) + IpmFiles.AutoPredictSuffix),
                                          AutoPredictIni.ToIni(xml, ctx), utf8);
                        result.AutoPredictJobs++;
                    }
                    catch (Exception ex)
                    {
                        log($"[WARN] {Path.GetFileName(xmlFile)}: could not be written as an .ini — {ex.Message}");
                        result.Skipped++;
                    }
                }
            }

            string calendar = IpmFiles.PulledCalendar(cfg.LocalFolderPath);
            if (calendar != null && File.Exists(calendar))
            {
                string xml = File.ReadAllText(calendar, Encoding.UTF8);
                File.WriteAllText(Path.Combine(edit, IpmFiles.CalendarsIni), IpmCalendarIni.ToIni(xml, cfg.Application), utf8);
                result.Calendars = ctx.Calendars.Count;
            }
            string events = IpmFiles.PulledEvents(cfg.LocalFolderPath);
            if (events != null && File.Exists(events))
            {
                File.Copy(events, Path.Combine(edit, IpmFiles.EventsXml), overwrite: true);
                File.SetAttributes(Path.Combine(edit, IpmFiles.EventsXml), FileAttributes.Normal);
                result.Events = true;
            }
            ReadOnlyScripts.DeleteEmptyFolders(edit);

            if (cfg.ReadOnly)
            {
                int marked = ReadOnlyScripts.Apply(edit, readOnly: true) + ReadOnlyScripts.Apply(IpmFiles.PackageDir(cfg.LocalFolderPath), readOnly: true);
                log($"Read-only connection: {marked} IPM files marked read-only.");
            }
        }

        // ------------------------------------------------------------------ //
        //  Local changes (the pull's "Overwrite / Keep both")
        // ------------------------------------------------------------------ //

        /// <summary>The IPM files with saved changes that weren't deployed.</summary>
        public static List<LocalChanges.Change> Scan(ConnectionConfig cfg)
        {
            var result = new List<LocalChanges.Change>();
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            string edit = IpmFiles.EditDir(cfg);
            if (!Directory.Exists(edit)) return result;
            foreach (string f in Directory.EnumerateFiles(edit, "*", SearchOption.AllDirectories))
            {
                if (LocalChanges.IsLocalCopy(f) || Path.GetFileName(f).StartsWith(".")) continue;
                string reason;
                try
                {
                    IpmDeployPlan plan = Plan(cfg, f);
                    reason = plan == null ? "not in the last pull" : plan.Changes.Count + plan.Notes.Count > 0 ? "changed" : null;
                }
                catch (EpmException ex) when (ex.Message.Contains("isn't in the last IPM pull")) { reason = "not in the last pull"; }
                catch { reason = "changed"; }                                      // can't be read back: it was edited
                if (reason != null)
                    result.Add(new LocalChanges.Change { File = f, Relative = f.Substring(root.TrimEnd('\\').Length + 1), Reason = reason });
            }
            return result.OrderBy(c => c.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Earlier "keep both" copies in the IPM folder.</summary>
        public static List<LocalChanges.Change> LocalCopies(ConnectionConfig cfg)
        {
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            string edit = IpmFiles.EditDir(cfg);
            if (!Directory.Exists(edit)) return new List<LocalChanges.Change>();
            return Directory.EnumerateFiles(edit, "*", SearchOption.AllDirectories).Where(LocalChanges.IsLocalCopy)
                .Select(f => new LocalChanges.Change { File = f, Relative = f.Substring(root.TrimEnd('\\').Length + 1), Reason = "local copy" })
                .ToList();
        }

        // ------------------------------------------------------------------ //
        //  Deploy (Planning only)
        // ------------------------------------------------------------------ //

        /// <summary>What deploying the IPM file <paramref name="path"/> sends; null when it isn't an
        /// IPM file. Throws <see cref="EpmException"/> when the file can't be deployed.</summary>
        public static IpmDeployPlan Plan(ConnectionConfig cfg, string path)
        {
            IpmFiles.Kind kind = IpmFiles.KindOf(path, out string job);
            if (kind == IpmFiles.Kind.None) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            string file = Path.GetFileName(path);
            string local = cfg.LocalFolderPath;
            switch (kind)
            {
                case IpmFiles.Kind.AutoPredict:
                {
                    string pulled = IpmFiles.PulledAutoPredict(local, job)
                        ?? throw new EpmException($"'{job}' isn't in the last IPM pull. Auto Predict jobs are created in EPM; " +
                                                  "pull IPM to get a new one (or one renamed there).");
                    string xml = File.ReadAllText(pulled, Encoding.UTF8);
                    AutoPredictIni.Applied a = AutoPredictIni.Apply(xml, text, IpmContext.For(cfg), file);
                    return new IpmDeployPlan
                    {
                        Kind = kind, ArtifactName = AutoPredictIni.JobName(xml), ParentPath = IpmFiles.AutoPredictPath,
                        PulledPath = pulled, Xml = a.Xml, Changes = a.Changes,
                    };
                }
                case IpmFiles.Kind.Calendars:
                {
                    string pulled = IpmFiles.PulledCalendar(local);
                    if (pulled == null || !File.Exists(pulled))
                        throw new EpmException("The calendars aren't in the last IPM pull. Pull IPM first.");
                    IpmCalendarIni.Applied a = IpmCalendarIni.Apply(File.ReadAllText(pulled, Encoding.UTF8), text, file, cfg.Username);
                    return new IpmDeployPlan
                    {
                        Kind = kind, ArtifactName = IpmFiles.CalendarArtifact, ParentPath = IpmFiles.GlobalArtifacts,
                        PulledPath = pulled, Xml = a.Xml, Changes = a.Changes, Notes = a.Notes,
                    };
                }
                default:
                {
                    string pulled = IpmFiles.PulledEvents(local);
                    if (pulled == null || !File.Exists(pulled))
                        throw new EpmException("The events aren't in the last IPM pull. Pull IPM first.");
                    try { XDocument.Parse(text); }
                    catch (XmlException ex) { throw new EpmException($"{file} is not valid XML: {ex.Message}"); }
                    string Norm(string s) => s.Replace("\r\n", "\n").Trim();
                    return new IpmDeployPlan
                    {
                        Kind = kind, ArtifactName = IpmFiles.EventsArtifact, ParentPath = IpmFiles.GlobalArtifacts,
                        PulledPath = pulled, Xml = text,
                        Changes = Norm(text) == Norm(File.ReadAllText(pulled, Encoding.UTF8)) ? new List<string>() : new List<string> { file },
                    };
                }
            }
        }

        public static async Task<DeployResult> DeployAsync(ConnectionConfig cfg, IpmDeployPlan plan,
                                                           IProgress<string> log, IProgress<DeployStep> steps = null)
        {
            var result = new DeployResult();
            string snapshotName = DeployService.BuildSnapshotName(cfg.Username);
            log?.Report($"Snapshot name: {snapshotName}.zip");
            using (var client = new EpmClient(cfg))
            {
                steps?.Report(DeployStep.BuildPackage);
                log?.Report($"Building the deployment package for {plan.Label} (Planning only)…");
                byte[] zip;
                try { zip = BuildPackage(cfg, plan); }
                catch (EpmException) { throw; }
                catch (Exception ex) { throw new EpmException("Failed to build deployment package: " + ex.Message, ex); }
                log?.Report($"Package built ({zip.Length / 1024} KB). Proceeding with upload…");

                await DeployService.UploadAndImportAsync(client, snapshotName, zip, plan.Label, result, log, steps).ConfigureAwait(false);

                // What was imported is now the pulled version (the base of the next deploy)
                try
                {
                    if (File.Exists(plan.PulledPath)) File.SetAttributes(plan.PulledPath, FileAttributes.Normal);
                    File.WriteAllText(plan.PulledPath, plan.Xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex) { log?.Report($"  [WARN] Could not record the deployed version locally: {ex.Message}"); }

                steps?.Report(DeployStep.CleanUp);
                result.CleanupError = await DeployService.CleanUpSnapshotAsync(client, snapshotName, log).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>
        /// The LCM package for one IPM artifact: the pulled package's manifests narrowed to it, and
        /// its XML — HP-{app} only, nothing for Calculation Manager.
        /// </summary>
        internal static byte[] BuildPackage(ConnectionConfig cfg, IpmDeployPlan plan)
        {
            string pkg = IpmFiles.PackageDir(cfg.LocalFolderPath);
            string hp = IpmFiles.HpDir(cfg.LocalFolderPath) ?? throw new EpmException("IPM was never pulled for this connection. Pull IPM first.");
            string hpFolder = Path.GetFileName(hp);
            string resource = plan.PulledPath.Substring(hp.TrimEnd('\\').Length + 1).Replace('\\', '/');

            string Manifest(string path)
            {
                if (!File.Exists(path)) throw new EpmException($"{path} is missing. Pull IPM again.");
                XDocument doc = XDocument.Load(path);
                foreach (XElement task in doc.Descendants("Task"))
                {
                    task.Elements("Artifact").Remove();
                    task.Add(new XElement("Artifact", new XAttribute("recursive", "false"),
                                          new XAttribute("parentPath", plan.ParentPath), new XAttribute("pattern", plan.ArtifactName)));
                }
                return Text(doc);
            }

            string listingPath = Path.Combine(hp, "info", "listing.xml");
            if (!File.Exists(listingPath)) throw new EpmException($"{listingPath} is missing. Pull IPM again.");
            XDocument listing = XDocument.Load(listingPath);
            listing.Root?.Elements("resource")
                .Where(e => !(string.Equals((string)e.Attribute("name"), plan.ArtifactName, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals((string)e.Attribute("path"), plan.ParentPath, StringComparison.OrdinalIgnoreCase)))
                .Remove();
            listing.Root?.Elements("folder")
                .Where(e => !(plan.ParentPath + "/").StartsWith(((string)e.Attribute("path") ?? "") + "/", StringComparison.OrdinalIgnoreCase))
                .Remove();
            if (listing.Root?.Elements("resource").Any() != true)
                listing.Root?.AddFirst(new XElement("resource",
                    new XAttribute("name", plan.ArtifactName), new XAttribute("id", plan.ArtifactName),
                    new XAttribute("type", plan.IsAutoPredict ? "APJob" : plan.Kind == IpmFiles.Kind.Calendars ? "IPM Calendar" : "IPM Event"),
                    new XAttribute("path", plan.ParentPath), new XAttribute("pathAlias", plan.ParentPath)));

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Add(string entry, string content)
                    {
                        using (var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), utf8))
                            w.Write(content);
                    }
                    if (File.Exists(Path.Combine(pkg, "Export.xml"))) Add("Export.xml", Manifest(Path.Combine(pkg, "Export.xml")));
                    Add("Import.xml", Manifest(Path.Combine(pkg, "Import.xml")));
                    Add($"{hpFolder}/Import.xml", Manifest(Path.Combine(hp, "Import.xml")));
                    Add($"{hpFolder}/info/listing.xml", Text(listing));
                    string sourceInfo = Path.Combine(hp, "info", "sourceInfo.xml");
                    if (File.Exists(sourceInfo)) Add($"{hpFolder}/info/sourceInfo.xml", File.ReadAllText(sourceInfo, Encoding.UTF8));
                    Add($"{hpFolder}/{resource}", plan.Xml);
                }
                return ms.ToArray();
            }
        }

        private static string Text(XDocument doc) =>
            (doc.Declaration != null ? doc.Declaration + "\n" : "") + doc.Root;

        // ------------------------------------------------------------------ //
        //  Run
        // ------------------------------------------------------------------ //

        public static Task<ExecuteResult> RunAutoPredictAsync(ConnectionConfig cfg, string jobName, bool forceRun, string paginatedDim,
                                                              IProgress<string> log, IProgress<ExecuteStep> steps = null) =>
            RuleExecutor.ExecuteJobAsync(cfg, AutoPredictJobType, jobName,
                new JObject { ["forceRun"] = forceRun, ["paginatedDim"] = paginatedDim ?? "" },
                AutoPredictJobType, log, steps);

        /// <summary>The dimensions a job can page through: those of its calendar's cubes (all
        /// downloaded ones when unknown).</summary>
        public static List<string> PaginationDimensions(ConnectionConfig cfg, string jobName)
        {
            EpmMetadata md = EpmMetadata.Load(cfg.LocalFolderPath);
            if (!md.HasDimensions) return new List<string>();
            var cubes = new List<string>();
            try
            {
                string pulled = IpmFiles.PulledAutoPredict(cfg.LocalFolderPath, jobName);
                if (pulled != null)
                {
                    string calendar = (string)XDocument.Parse(File.ReadAllText(pulled, Encoding.UTF8)).Root?.Attribute("calendarName");
                    cubes = IpmContext.For(cfg).Calendar(calendar)?.Cubes ?? cubes;
                }
            }
            catch { }
            var dims = cubes.Where(c => md.Dims.Cubes.ContainsKey(c)).SelectMany(c => md.Dims.Cubes[c]).ToList();
            if (dims.Count == 0) dims = md.Dims.ByName.Keys.ToList();
            return dims.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
