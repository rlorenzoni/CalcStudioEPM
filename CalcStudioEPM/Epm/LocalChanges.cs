using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Saved edits a pull would wipe out: Pull deletes {LocalFolder}\CalcManager and writes the
    /// scripts again from the export. Before that, <see cref="Scan"/> finds every file whose
    /// content differs from the last pulled / deployed copy of its rule (or that the export
    /// doesn't produce at all), and <see cref="KeepBoth"/> backs files up and copies them back
    /// after the pull as "{name}.local.001{ext}", beside EPM's version, ready to compare.
    /// "*.local.NNN.*" files from earlier pulls are kept automatically.
    /// </summary>
    internal static class LocalChanges
    {
        public sealed class Change
        {
            /// <summary>Full path of the local file.</summary>
            public string File;
            /// <summary>Path below the scripts folder (Planning\Vision\Plan1\Rules\X.csc).</summary>
            public string Relative;
            /// <summary>Why it is listed: "changed" or "not in the last pull".</summary>
            public string Reason;
        }

        private static readonly Regex LocalCopy = new Regex(@"\.local\.\d{3}\.[^.\\/]+$", RegexOptions.IgnoreCase);

        public static bool IsLocalCopy(string path) => LocalCopy.IsMatch(Path.GetFileName(path ?? ""));

        private static string ResourceRoot(string localFolder) =>
            Path.Combine(localFolder, PathConstants.CalcManagerDirName, "CALC-Calculation Manager", "resource");

        /// <summary>The saved, undeployed changes in the connection's scripts folder (empty when none).</summary>
        public static List<Change> Scan(ConnectionConfig cfg)
        {
            var result = new List<Change>();
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            if (!Directory.Exists(root)) return result;
            string resources = ResourceRoot(cfg.LocalFolderPath);

            // Graphical rules: a folder with .flow.json, compared step by step
            var graphical = Directory.EnumerateFiles(root, GraphicalRule.ManifestName, SearchOption.AllDirectories)
                                     .Select(Path.GetDirectoryName).ToList();
            foreach (string dir in graphical)
            {
                if (GraphicalRule.IsTemplateFolder(dir)) continue;             // read only, never deployed
                string rel = Rel(root, dir);
                string xmlPath = Path.Combine(resources, rel);                 // the folder is named like the artifact file
                List<string> changed = null;
                if (File.Exists(xmlPath))
                {
                    try
                    {
                        changed = GraphicalRule.Apply(File.ReadAllText(xmlPath, Encoding.UTF8), dir).Changes
                            .Select(c => { int b = c.IndexOf(" [", StringComparison.Ordinal); return b > 0 ? c.Substring(0, b) : c; })
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    catch { changed = null; }                                 // files it can't read back: list them all
                }
                IEnumerable<string> files = changed != null
                    ? changed.Select(f => Path.Combine(dir, f)).Where(File.Exists)
                    : Directory.EnumerateFiles(dir).Where(f => Path.GetFileName(f) != GraphicalRule.ManifestName && !IsLocalCopy(f));
                string reason = File.Exists(xmlPath) ? "changed" : "not in the last pull";
                result.AddRange(files.Select(f => new Change { File = f, Relative = Rel(root, f), Reason = reason }));
            }

            // Script rules and templates: the script vs the <script> of the pulled / deployed XML
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (IsLocalCopy(f) || Path.GetFileName(f).StartsWith(".") ||          // .epm_workspace and other helpers
                    graphical.Any(g => f.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                string rel = Rel(root, f);
                if (RuleSet.IsRuleSetIni(f))
                {
                    // A RuleSet's .ini vs the definition in its pulled XML
                    string name = Path.GetFileName(f);
                    string setXml = Path.Combine(resources, Path.GetDirectoryName(rel) ?? "", name.Substring(0, name.Length - RuleSet.IniSuffix.Length));
                    if (!File.Exists(setXml)) { result.Add(new Change { File = f, Relative = rel, Reason = "not in the last pull" }); continue; }
                    bool changedSet;
                    try
                    {
                        XElement def = RuleSet.Definition(XDocument.Load(setXml), PathConstants.FromFileName(name.Substring(0, name.Length - RuleSet.IniSuffix.Length)));
                        changedSet = def == null || RuleSet.ApplyIni(new XElement(def), File.ReadAllText(f, Encoding.UTF8), name).Count > 0;
                    }
                    catch { changedSet = true; }                              // can't be read back: it was edited
                    if (changedSet) result.Add(new Change { File = f, Relative = rel, Reason = "changed" });
                    continue;
                }
                string xmlPath = Path.Combine(resources, Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(f));
                if (!File.Exists(xmlPath))
                {
                    result.Add(new Change { File = f, Relative = rel, Reason = "not in the last pull" });
                    continue;
                }
                string text = File.ReadAllText(f, Encoding.UTF8);
                string xml  = File.ReadAllText(xmlPath, Encoding.UTF8);
                bool differs = PackageBuilder.ScriptOf(xml).Length > 0
                    ? PackageBuilder.HasUndeployedChanges(text, xml)
                    : Norm(text) != Norm(xml);                               // an artifact copied as-is
                if (differs) result.Add(new Change { File = f, Relative = rel, Reason = "changed" });
            }
            return result.OrderBy(c => c.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Earlier "keep both" copies, kept across pulls without asking.</summary>
        public static List<Change> LocalCopies(ConnectionConfig cfg)
        {
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            if (!Directory.Exists(root)) return new List<Change>();
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsLocalCopy)
                            .Select(f => new Change { File = f, Relative = Rel(root, f), Reason = "local copy" }).ToList();
        }

        /// <summary>
        /// Copies the files to {LocalFolder}\.epm\backup\{yyyyMMdd-HHmmss}\ (same relative paths);
        /// returns that folder.
        /// </summary>
        public static string Backup(ConnectionConfig cfg, IEnumerable<Change> files)
        {
            string dir = Path.Combine(EpmMetadata.Folder(cfg.LocalFolderPath), "backup",
                                      DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            foreach (Change c in files)
            {
                string target = Path.Combine(dir, c.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(c.File, target, overwrite: true);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }
            return dir;
        }

        /// <summary>
        /// After a successful pull: each kept file goes back beside EPM's version as
        /// "{name}.local.NNN{ext}" (the first free number), or under its own name when the pull
        /// didn't write one; earlier local copies go back as they were. Returns the files written.
        /// </summary>
        public static List<string> RestoreAfterPull(ConnectionConfig cfg, string backupDir, IEnumerable<Change> keepBoth, IEnumerable<Change> copies)
        {
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            var written = new List<string>();
            foreach (Change c in copies)
                written.Add(Put(Path.Combine(backupDir, c.Relative), Path.Combine(root, c.Relative)));
            foreach (Change c in keepBoth)
            {
                string original = Path.Combine(root, c.Relative);
                string target = File.Exists(original) ? NextLocalName(original) : original;
                written.Add(Put(Path.Combine(backupDir, c.Relative), target));
            }
            return written;
        }

        /// <summary>After a failed pull: kept files whose originals are gone go back under their own names.</summary>
        public static List<string> RestoreAfterFailedPull(ConnectionConfig cfg, string backupDir, IEnumerable<Change> files)
        {
            string root = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            var written = new List<string>();
            foreach (Change c in files)
            {
                string original = Path.Combine(root, c.Relative);
                if (!File.Exists(original)) written.Add(Put(Path.Combine(backupDir, c.Relative), original));
            }
            return written;
        }

        /// <summary>"X.csc" → "X.local.001.csc" (next free number); "02 - F.formula.csc" → "02 - F.formula.local.001.csc".</summary>
        public static string NextLocalName(string path)
        {
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, $"{stem}.local.{i:000}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}.local.{DateTime.Now:yyyyMMddHHmmss}{ext}");
        }

        private static string Put(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, overwrite: true);
            File.SetAttributes(to, File.GetAttributes(to) & ~FileAttributes.ReadOnly);   // the user's own copy
            return to;
        }

        private static string Rel(string root, string path) =>
            path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);

        private static string Norm(string s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
    }
}
