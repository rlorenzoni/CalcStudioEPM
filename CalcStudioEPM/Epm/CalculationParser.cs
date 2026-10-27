using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using CalcStudioEPM;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Post-processes a Calculation Manager export by reading listing.xml,
    /// locating every artifact file (which have no extension), and copying each
    /// one to a "scripts" output folder with the correct extension:
    ///   • CalcScript  → .csc
    ///   • Groovy rule → .js
    ///
    /// The artifact files are left completely intact — only the extension is added.
    /// The output folder mirrors the path hierarchy from listing.xml so the
    /// plan type / cube structure is preserved.
    ///
    /// Export layout produced by EPM Cloud:
    ///   {CalcManagerDir}/
    ///     CALC-Calculation Manager/
    ///       info/
    ///         listing.xml          ← master index (path + type per artifact)
    ///       resource/
    ///         Planning/
    ///           {PlanType}/
    ///             Rules/           ← no-extension artifact files
    ///             Templates/
    ///             RuleSets/
    ///
    /// Output:
    ///   {CalcManagerDir}/scripts/
    ///     Planning/{PlanType}/Rules/{name}.csc|js
    ///     Planning/{PlanType}/Templates/{name}.csc
    ///     …
    /// </summary>
    public static class CalculationParser
    {
        /// <param name="connectionName">
        /// The user-visible connection name (from <see cref="Config.ConnectionConfig.Name"/>).
        /// Scripts are written to <c>{CalcManagerDir}\{connectionName}.Scripts\</c>.
        /// Pass null or empty to use the legacy "scripts" folder name.
        /// </param>
        /// <returns>Scripts written, artifacts skipped, the scripts folder (null if the export had no listing) and the number of variables in the dictionary.</returns>
        public static (int Copied, int Skipped, string ScriptsDir, int Variables) ExtractScripts(
            string basePath,
            string connectionName,
            IProgress<string> progress = null)
        {
            string calcManagerDir = Path.Combine(basePath, PathConstants.CalcManagerDirName);
            if (!Directory.Exists(calcManagerDir))
            {
                progress?.Report("CalcManager directory not found; nothing to parse.");
                return (0, 0, null, 0);
            }

            // ---------------------------------------------------------------- //
            //  Locate listing.xml (inside any "info" subdirectory)
            // ---------------------------------------------------------------- //
            string listingPath = Directory
                .GetFiles(calcManagerDir, "listing.xml", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (listingPath == null)
            {
                progress?.Report("listing.xml not found under CalcManager/; cannot determine artifacts.");
                return (0, 0, null, 0);
            }

            progress?.Report($"Found listing.xml: {listingPath}");

            // The resource folder is a sibling of the info folder
            // …/CALC-Calculation Manager/info/listing.xml
            //                            ^ infoDir
            // …/CALC-Calculation Manager/resource/
            string infoDir      = Path.GetDirectoryName(listingPath);
            string calcDir      = Path.GetDirectoryName(infoDir);      // "CALC-Calculation Manager"
            string resourceBase = Path.Combine(calcDir, "resource");

            // Scripts are written to {ConnectionName}.Scripts (or the legacy "scripts" fallback)
            string scriptsDir = PathConstants.GetScriptsRootPath(basePath, connectionName);

            // Fresh start – remove old scripts so stale files don't linger
            // Only the files go; the folder tree stays (Folder as Workspace and Explorer just see
            // files change), the Folder as Workspace marker too. Folders left empty once the new
            // scripts are written are removed at the end.
            ReadOnlyScripts.ClearFiles(scriptsDir, Path.Combine(scriptsDir, PathConstants.WorkspaceSentinelName));
            Directory.CreateDirectory(scriptsDir);

            // ---------------------------------------------------------------- //
            //  Parse listing.xml
            // ---------------------------------------------------------------- //
            XDocument listing;
            try
            {
                listing = XDocument.Load(listingPath);
            }
            catch (Exception ex)
            {
                progress?.Report($"Failed to parse listing.xml: {ex.Message}");
                return (0, 0, scriptsDir, 0);
            }

            var resources = listing.Descendants("resource").ToList();
            progress?.Report($"Artifacts found in listing.xml: {resources.Count}");

            int copied = 0, skipped = 0;
            var variables = new VariableDictionaryBuilder();
            EpmMetadata metadata = EpmMetadata.Load(basePath);   // dimension lists for graphical rules

            foreach (XElement res in resources)
            {
                string name       = ((string)res.Attribute("name"))?.Trim();
                string type       = ((string)res.Attribute("type"))?.Trim();
                string artifactPath = ((string)res.Attribute("path"))?.Trim();

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(artifactPath))
                    continue;

                // Convert the virtual path (/Planning/Vision/Plan1/Rules) to a local path
                string relPath    = artifactPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                // The path comes from the downloaded export: it must not lead outside the
                // export or the scripts folder (rooted, UNC or ".." paths)
                if (!PathConstants.IsSafeRelativePath(resourceBase, relPath) ||
                    !PathConstants.IsSafeRelativePath(scriptsDir, relPath))
                {
                    progress?.Report($"  [WARN] Skipped '{name}': unexpected artifact path '{artifactPath}'");
                    skipped++;
                    continue;
                }
                string sourceFile = FindArtifactFile(Path.Combine(resourceBase, relPath), name);

                if (sourceFile == null)
                {
                    progress?.Report($"  [WARN] Not found: {Path.Combine(resourceBase, relPath, name)}");
                    skipped++;
                    continue;
                }

                // Compile the connection-wide runtime-prompt dictionary as we go
                variables.Add(sourceFile, name);

                // Create output folder mirroring the virtual path
                string outDir  = Path.Combine(scriptsDir, relPath);
                Directory.CreateDirectory(outDir);

                // A RuleSet only runs other rules: its definition becomes an .ini (see RuleSet).
                // Its XML embeds copies of those rules, so it is never read as a script.
                if (string.Equals(type, "RuleSet", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string ini = RuleSet.ToIni(XDocument.Load(sourceFile), name);
                        File.WriteAllText(Path.Combine(outDir, PathConstants.ToFileName(name) + RuleSet.IniSuffix), ini,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"  [WARN] Could not write RuleSet '{name}': {ex.Message}");
                        skipped++;
                    }
                    continue;
                }

                // A graphical rule (designer flow of steps) becomes a folder of step files; so does a
                // graphical template, read only (its file holds no single script to show)
                XDocument graphical = null;
                bool isTemplate = string.Equals(type, "Template", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var d = XDocument.Load(sourceFile);
                    if (isTemplate ? GraphicalRule.IsGraphicalTemplate(d, name) : GraphicalRule.IsGraphical(d)) graphical = d;
                }
                catch { }
                if (graphical != null)
                {
                    string what = isTemplate ? "graphical template" : "graphical rule";
                    try
                    {
                        string ruleDir = Path.Combine(outDir, PathConstants.ToFileName(name));
                        // The cube's dimensions (last download), so each FIX lists them all
                        string[] pathParts = artifactPath.Trim('/').Split('/');     // Planning/{app}/{cube}/Rules
                        System.Collections.Generic.List<string> cubeDims = null;
                        if (metadata.HasDimensions && pathParts.Length >= 3) metadata.Dims.Cubes.TryGetValue(pathParts[2], out cubeDims);
                        int files = isTemplate
                            ? GraphicalRule.ExtractTemplate(graphical, name, ruleDir, progress, cubeDims)
                            : GraphicalRule.Extract(graphical, name, ruleDir, progress, cubeDims);
                        progress?.Report($"  {(isTemplate ? "Graphical template" : "Graphical rule")} '{name}': {files} files in {ruleDir}");
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"  [WARN] Could not write {what} '{name}': {ex.Message}");
                        skipped++;
                    }
                    continue;
                }

                // Determine output extension and — when the artifact wraps one — the
                // decoded script body to write to disk, falling back to listing type
                // and a raw copy for artifacts that don't wrap a <script> element.
                (string ext, string scriptText) = AnalyzeArtifact(sourceFile, type);

                string destFile = Path.Combine(outDir, PathConstants.ToFileName(name) + ext);

                try
                {
                    if (scriptText != null)
                        // Write the decoded script only — real "<"/">" instead of the
                        // "&lt;"/"&gt;" entities EPM uses to embed it inside the XML —
                        // so the file is pleasant to read and edit in Notepad++. The
                        // untouched raw artifact stays at sourceFile as the metadata
                        // template Deploy merges edits back into.
                        File.WriteAllText(destFile, scriptText,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    else
                        File.Copy(sourceFile, destFile, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    progress?.Report($"  [WARN] Could not copy '{name}': {ex.Message}");
                    skipped++;
                }
            }

            int emptied = ReadOnlyScripts.DeleteEmptyFolders(scriptsDir);
            if (emptied > 0) progress?.Report($"Removed {emptied} folder(s) left empty (rules or cubes no longer in the export).");

            progress?.Report(
                $"Scripts ready: {copied} copied, {skipped} skipped. " +
                $"Output folder: {scriptsDir}");

            try
            {
                variables.Build().Save(basePath);
                progress?.Report(
                    $"Variable dictionary: {variables.Count} variables → " +
                    VariableDictionary.GetPath(basePath));
            }
            catch (Exception ex)
            {
                progress?.Report($"  [WARN] Could not save the variable dictionary: {ex.Message}");
            }

            return (copied, skipped, scriptsDir, variables.Count);
        }

        /// <summary>
        /// Finds the exported file for artifact <paramref name="name"/> in
        /// <paramref name="dir"/>. The listing holds the real name ("FCST/BUD - …") while
        /// EPM writes the file percent-encoded ("FCST%2FBUD - …"), so try that first and
        /// fall back to decoding every file name in the folder (covers e.g. lower-case
        /// escapes). Returns null when nothing matches.
        /// </summary>
        private static string FindArtifactFile(string dir, string name)
        {
            string direct = Path.Combine(dir, PathConstants.ToFileName(name));
            if (File.Exists(direct)) return direct;

            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                string.Equals(PathConstants.FromFileName(Path.GetFileName(f)), name,
                    StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ //
        //  Extension resolution
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Reads the &lt;script type="..."&gt; element from the artifact XML to decide
        /// both the correct file extension and, when present, the decoded script body
        /// (<see cref="XElement.Value"/> already decodes entities and CDATA alike) to
        /// write to disk instead of the raw XML. Falls back to the listing.xml type,
        /// and a <c>null</c> script body meaning "copy the artifact as-is", when the
        /// XML cannot be parsed or has no &lt;script&gt; element.
        /// </summary>
        private static (string ext, string scriptText) AnalyzeArtifact(string filePath, string listingType)
        {
            try
            {
                XDocument doc = XDocument.Load(filePath);

                // Look for any <script> element (case-insensitive local name)
                XElement scriptEl = doc.Descendants()
                    .FirstOrDefault(e =>
                        string.Equals(e.Name.LocalName, "script",
                            StringComparison.OrdinalIgnoreCase));

                if (scriptEl != null)
                {
                    string scriptType = ((string)scriptEl.Attribute("type") ?? "")
                        .ToLowerInvariant();

                    string ext = scriptType.Contains("groovy") ? ".js"
                               : scriptType.Contains("calc")   ? ".csc"
                               : FallbackExtension(listingType);

                    return (ext, scriptEl.Value);
                }

                // No <script> element — fall through to listing-type heuristic
            }
            catch
            {
                // If the file isn't valid XML, fall through
            }

            return (FallbackExtension(listingType), null);
        }

        private static string FallbackExtension(string listingType) =>
            // Rule → CalcScript or Groovy is ambiguous without the <script> element,
            // but Rule historically defaults to Groovy; Template/RuleSet/anything
            // else defaults to CalcScript.
            string.Equals(listingType, "Rule", StringComparison.OrdinalIgnoreCase)
                ? ".js"
                : ".csc";
    }
}
