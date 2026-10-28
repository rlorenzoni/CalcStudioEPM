using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Builds an EPM Cloud LCM migration ZIP package in memory for deploying a single
    /// Calculation Manager rule back to EPM Cloud.
    ///
    /// Package structure (the HP section only when also deploying to Planning):
    ///   Export.xml
    ///   Import.xml
    ///   HP-{Application}/
    ///     Import.xml
    ///     info/listing.xml
    ///     info/sourceInfo.xml
    ///     resource/Cube/{cube}/Calculation Manager Rules/{ruleName}.xml
    ///   CALC-Calculation Manager/
    ///     Import.xml
    ///     info/listing.xml
    ///     info/sourceInfo.xml
    ///     resource/Planning/{planType}/{cube}/Rules/{ruleName}
    /// </summary>
    internal static class PackageBuilder
    {
        // ------------------------------------------------------------------ //
        //  Public entry point
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Builds the deployment ZIP in memory and returns it as a byte array.
        /// </summary>
        /// <param name="application">EPM application name (from ConnectionConfig.Application).</param>
        /// <param name="planType">Plan type parsed from the file path (e.g. "Vision").</param>
        /// <param name="cube">Cube name parsed from the file path (e.g. "Plan1").</param>
        /// <param name="ruleName">Rule name (file name without extension).</param>
        /// <param name="calcContent">
        /// Current content of the active .csc/.js tab. Normally the plain, already-decoded
        /// script text (what "Pull Calculations" now writes to disk). For backward
        /// compatibility with files pulled before that change, a buffer that still looks
        /// like the full raw CALC XML (starts with "&lt;" and contains a &lt;script&gt;
        /// element) is detected and unwrapped automatically — see
        /// <see cref="ResolveScriptText"/>.
        /// </param>
        /// <param name="username">Username from ConnectionConfig (used as ExportedBy).</param>
        /// <param name="localFolder">LocalFolderPath — used to locate CalcManager files on disk.</param>
        /// <param name="planningScript">Script for the Planning (HP) copy of the rule — the
        /// editor script with its %Template calls inlined (see <see cref="TemplateExpander"/>).
        /// Null builds a Calculation Manager-only package (no HP section or tasks).</param>
        /// <param name="calcRuleXml">The rebuilt CALC rule XML inside the package — once the
        /// import succeeds, this is what EPM Cloud holds (see <see cref="SaveDeployedRuleXml"/>).</param>
        /// <param name="progress">Optional log for runtime-prompt changes and warnings.</param>
        public static byte[] Build(
            string application,
            string planType,
            string cube,
            string ruleName,
            string calcContent,
            string username,
            string localFolder,
            string planningScript,
            out string calcRuleXml,
            IProgress<string> progress = null,
            string prebuiltCalcRuleXml = null)
        {
            if (string.IsNullOrWhiteSpace(application))  throw new ArgumentException(nameof(application));
            if (string.IsNullOrWhiteSpace(planType))      throw new ArgumentException(nameof(planType));
            if (string.IsNullOrWhiteSpace(cube))          throw new ArgumentException(nameof(cube));
            if (string.IsNullOrWhiteSpace(ruleName))      throw new ArgumentException(nameof(ruleName));
            if (string.IsNullOrWhiteSpace(calcContent) && prebuiltCalcRuleXml == null) throw new ArgumentException(nameof(calcContent));

            string hpFolder   = "HP-" + application;
            string calcFolder = "CALC-Calculation Manager";

            string now             = DateTime.UtcNow.ToString("yyyyMMdd");
            string time            = DateTime.UtcNow.ToString("HH:mm");
            string exportedVersion = ReadExportedVersion(localFolder);
            bool   includeHp       = planningScript != null;
            string hpSourceInfo    = includeHp ? ReadOrBuildHpSourceInfo(localFolder, application, hpFolder) : null;
            string calcSourceInfo  = ReadOrBuildCalcSourceInfo(localFolder, calcFolder);

            // Resolve the plain script text once, then rebuild BOTH the CALC-side XML
            // (from the original pulled artifact, so entity/CDATA formatting stays
            // faithful to what EPM exported) and the HP-side XML (which can fall back
            // to deriving its structure from the just-rebuilt CALC XML) from it.
            // A graphical rule comes with its whole rule XML already rebuilt from its folder
            bool promptsChanged = false;
            string calcRuleContent = prebuiltCalcRuleXml ??
                ReadOrBuildCalcRuleXml(localFolder, calcFolder, planType, cube, ruleName,
                                       ResolveScriptText(calcContent), progress, out promptsChanged);
            string hpRuleContent   = includeHp
                ? ReadOrBuildHpRuleXml(localFolder, hpFolder, cube, ruleName, LfOnly(planningScript),
                                       calcRuleContent, preferCalc: promptsChanged)
                : null;
            long   timestamp       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string ruleFileName    = PathConstants.ToFileName(ruleName);
            calcRuleXml            = calcRuleContent;

            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // ---- Root manifests ----
                    Add(zip, "Export.xml",
                        BuildExportXml(application, planType, cube, ruleName, includeHp ? hpFolder : null));

                    Add(zip, "Import.xml",
                        BuildRootImportXml(application, planType, cube, ruleName,
                                           includeHp ? hpFolder : null, username, exportedVersion, now, time));

                    // ---- HP section (Planning) ----
                    if (includeHp)
                    {
                        Add(zip, $"{hpFolder}/Import.xml",
                            BuildScopedImportXml(
                                product:    "HP",
                                project:    "Default Application Group",
                                targetApp:  application,
                                parentPath: $"/Cube/{cube}/Calculation Manager Rules",
                                ruleName:   ruleName,
                                username:   username,
                                exportedVersion: exportedVersion,
                                exportedDate:    now,
                                exportedTime:    time));

                        Add(zip, $"{hpFolder}/info/listing.xml",
                            BuildHpListingXml(cube, ruleName, timestamp));

                        Add(zip, $"{hpFolder}/info/sourceInfo.xml", hpSourceInfo);

                        // Entry names use EPM's on-disk encoding ("/" → "%2F"); the XML
                        // manifests above carry the real rule name.
                        Add(zip, $"{hpFolder}/resource/Cube/{cube}/Calculation Manager Rules/{ruleFileName}.xml",
                            hpRuleContent);
                    }

                    // ---- CALC section ----
                    Add(zip, $"{calcFolder}/Import.xml",
                        BuildScopedImportXml(
                            product:    "CALC",
                            project:    "Foundation",
                            targetApp:  "Calculation Manager",
                            parentPath: $"/Planning/{planType}/{cube}/Rules",
                            ruleName:   ruleName,
                            username:   username,
                            exportedVersion: exportedVersion,
                            exportedDate:    now,
                            exportedTime:    time));

                    Add(zip, $"{calcFolder}/info/listing.xml",
                        BuildCalcListingXml(planType, cube, ruleName, timestamp));

                    Add(zip, $"{calcFolder}/info/sourceInfo.xml", calcSourceInfo);

                    // CALC resource file has NO extension — the rebuilt CALC XML goes in here
                    Add(zip, $"{calcFolder}/resource/Planning/{planType}/{cube}/Rules/{ruleFileName}",
                        calcRuleContent);
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// A Calculation Manager-only package holding one RuleSet, already put together by
        /// <see cref="RuleSet.Assemble"/>:
        ///   CALC-Calculation Manager/resource/Planning/{application}/RuleSets/{ruleSetName}
        /// </summary>
        public static byte[] BuildRuleSet(string application, string ruleSetName, string ruleSetXml,
                                          string username, string localFolder)
        {
            if (string.IsNullOrWhiteSpace(application)) throw new ArgumentException(nameof(application));
            if (string.IsNullOrWhiteSpace(ruleSetName)) throw new ArgumentException(nameof(ruleSetName));
            if (string.IsNullOrWhiteSpace(ruleSetXml))  throw new ArgumentException(nameof(ruleSetXml));

            const string calcFolder = "CALC-Calculation Manager";
            string parentPath = $"/Planning/{application}/{RuleSet.FolderName}";
            string now  = DateTime.UtcNow.ToString("yyyyMMdd");
            string time = DateTime.UtcNow.ToString("HH:mm");
            string exportedVersion = ReadExportedVersion(localFolder);
            string artifact = $@"<Artifact recursive=""false"" parentPath=""{XmlAttr(parentPath)}"" pattern=""{XmlAttr(ruleSetName)}""/>";

            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    Add(zip, "Export.xml",
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Package>
   <LOCALE>en_GB</LOCALE>
   <User name="""" password=""""/>
   <Task>
      <Source type=""Application"" product=""CALC"" project=""Foundation"" application=""Calculation Manager""/>
      <Target type=""FileSystem"" filePath=""/{calcFolder}""/>
      {artifact}
   </Task>
</Package>");
                    Add(zip, "Import.xml",
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Package>
   <LOCALE>en_GB</LOCALE>
   <User name="""" password=""""/>
   <ExportedVersion>{XmlVal(exportedVersion)}</ExportedVersion>
   <ExportedDateUTC>{XmlVal(now)}</ExportedDateUTC>
   <ExportedTimeUTC>{XmlVal(time)}</ExportedTimeUTC>
   <ExportedBy>{XmlVal(username)}</ExportedBy>
   <IDMDomain></IDMDomain>
   <ServiceInstance></ServiceInstance>
   <Task>
      <Source type=""FileSystem"" filePath=""/{calcFolder}""/>
      <Target type=""Application"" product=""CALC"" project=""Foundation"" application=""Calculation Manager""/>
      {artifact}
   </Task>
</Package>");
                    Add(zip, $"{calcFolder}/Import.xml",
                        BuildScopedImportXml("CALC", "Foundation", "Calculation Manager", parentPath, ruleSetName,
                                             username, exportedVersion, now, time));

                    string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                    string Folder(string name, string path) =>
                        $@"<folder name=""{XmlAttr(name)}"" type=""Folder"" size="""" id="""" path=""{XmlAttr(path)}"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />";
                    Add(zip, $"{calcFolder}/info/listing.xml",
                        @"<?xml version=""1.0"" encoding=""utf-8""?><artifactListing>" +
                        $@"<resource name=""{XmlAttr(ruleSetName)}"" id=""{XmlAttr(ruleSetName)}"" type=""RuleSet"" cloneOnly=""false"" size="""" " +
                        $@"path=""{XmlAttr(parentPath)}"" pathAlias=""{XmlAttr(parentPath)}"" modifiedBy="""" lastUpdated=""{ts}"" description="""" />" +
                        Folder("Planning", "/Planning") +
                        Folder(application, $"/Planning/{application}") +
                        Folder(RuleSet.FolderName, parentPath) +
                        "</artifactListing>");
                    Add(zip, $"{calcFolder}/info/sourceInfo.xml", ReadOrBuildCalcSourceInfo(localFolder, calcFolder));

                    Add(zip, $"{calcFolder}/resource/Planning/{application}/{RuleSet.FolderName}/{PathConstants.ToFileName(ruleSetName)}",
                        ruleSetXml);
                }
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------ //
        //  Deployed version (the pulled CALC artifact, refreshed after each deploy)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Local copy of the rule's CALC XML as EPM Cloud last had it: written by Pull,
        /// and overwritten by <see cref="SaveDeployedRuleXml"/> after every successful deploy.
        /// </summary>
        public static string GetDeployedRulePath(string localFolder, string planType, string cube, string ruleName) =>
            Path.Combine(localFolder, PathConstants.CalcManagerDirName, "CALC-Calculation Manager",
                         "resource", PathConstants.PlanningDirName, planType, cube, "Rules",
                         PathConstants.ToFileName(ruleName));

        /// <summary>The deployed rule XML, or null when the rule was never pulled.</summary>
        public static string ReadDeployedRuleXml(string localFolder, string planType, string cube, string ruleName)
        {
            if (string.IsNullOrWhiteSpace(localFolder)) return null;
            string path = GetDeployedRulePath(localFolder, planType, cube, ruleName);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        /// <summary>Records what was just deployed, so it becomes the new "deployed version".</summary>
        public static void SaveDeployedRuleXml(string localFolder, string planType, string cube, string ruleName, string xml)
        {
            string path = GetDeployedRulePath(localFolder, planType, cube, ruleName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// The CALC rule XML a deploy of <paramref name="editedContent"/> would upload — used
        /// to know the runtime prompts the rule will have once deployed.
        /// </summary>
        public static string BuildRuleXml(string localFolder, string planType, string cube,
                                          string ruleName, string editedContent, IProgress<string> progress = null) =>
            ReadOrBuildCalcRuleXml(localFolder, "CALC-Calculation Manager", planType, cube, ruleName,
                                   ResolveScriptText(editedContent), progress, out _);

        /// <summary>The script text inside a rule XML ("" when there is none).</summary>
        public static string ScriptOf(string ruleXml)
        {
            try { return FindScriptElement(XDocument.Parse(ruleXml))?.Value ?? ""; }
            catch { return ""; }
        }

        /// <summary>True when the XML is a script rule or template — it has a &lt;script&gt;, even an
        /// empty one (a rule whose script was never written).</summary>
        public static bool HasScript(string ruleXml)
        {
            try { return FindScriptElement(XDocument.Parse(ruleXml)) != null; }
            catch { return false; }
        }

        /// <summary>
        /// True when the editor content differs from the deployed script, ignoring
        /// line-ending style and trailing whitespace at the end of the file.
        /// </summary>
        public static bool HasUndeployedChanges(string editorContent, string deployedRuleXml)
        {
            string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
            return Normalize(ResolveScriptText(editorContent)) != Normalize(ScriptOf(deployedRuleXml));
        }

        // ------------------------------------------------------------------ //
        //  Parsing the file path to extract planType, cube, ruleName
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Parses a .csc file path of the form
        /// <c>{any}\scripts\Planning\{planType}\{cube}\Rules\{ruleName}.csc</c>
        /// and returns (planType, cube, ruleName). Throws if the path does not match.
        /// </summary>
        public static (string planType, string cube, string ruleName) ParseScriptPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new EpmException("No file is open in Notepad++. Please open a .csc or .js rule file first.");

            // Normalize separators
            string norm = filePath.Replace('/', '\\');

            // Find ".Scripts\Planning\" in the path (case-insensitive).
            // The scripts root is named "{ConnectionName}.Scripts" so any folder ending in
            // ".Scripts" followed by "\Planning\" is a valid anchor.
            const string marker = @".Scripts\Planning\";
            int idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new EpmException(
                    "The active file does not appear to be inside a connection scripts folder.\n\n" +
                    "Expected a path like:\n" +
                    "  …\\{Connection}.Scripts\\Planning\\{planType}\\{cube}\\Rules\\{ruleName}.csc\n" +
                    "  …\\{Connection}.Scripts\\Planning\\{planType}\\{cube}\\Rules\\{ruleName}.js\n\n" +
                    $"Actual path: {filePath}\n\n" +
                    "Run 'Pull Calculations' first to download the scripts, then open one of those files.");

            string relative = norm.Substring(idx + marker.Length); // "Vision\Plan1\Rules\MY_RULE.csc"
            string[] parts  = relative.Split('\\');

            // Expect: [planType] [cube] "Rules" [ruleName.ext]
            if (parts.Length < 4 ||
                !string.Equals(parts[2], "Rules", StringComparison.OrdinalIgnoreCase))
                throw new EpmException(
                    "Unexpected path structure under scripts\\Planning.\n" +
                    "Expected …\\Planning\\{planType}\\{cube}\\Rules\\{ruleName}.csc\n\n" +
                    $"Actual path: {filePath}");

            string planType = parts[0];
            string cube     = parts[1];
            // "FCST%2FBUD - X.js" → real rule name "FCST/BUD - X"; a step file of a graphical
            // rule (…\Rules\{Rule}\02 - Step.csc) belongs to the rule its folder is named after
            string ruleName = parts.Length >= 5 && GraphicalRule.RuleFolderOf(filePath) != null
                ? PathConstants.FromFileName(parts[3])
                : PathConstants.FromFileName(Path.GetFileNameWithoutExtension(parts[3]));

            if (string.IsNullOrWhiteSpace(ruleName))
                throw new EpmException("Could not determine rule name from file path: " + filePath);

            return (planType, cube, ruleName);
        }

        // ------------------------------------------------------------------ //
        //  File readers (from CalcManager pull directory)
        // ------------------------------------------------------------------ //

        private static string ReadExportedVersion(string localFolder)
        {
            const string defaultVersion = "26.05.66";
            if (string.IsNullOrWhiteSpace(localFolder)) return defaultVersion;

            // Try to read ExportedVersion from the root Import.xml that was extracted by Pull
            string importXml = Path.Combine(localFolder, PathConstants.CalcManagerDirName, "Import.xml");
            if (!File.Exists(importXml)) return defaultVersion;
            try
            {
                XDocument doc = XDocument.Load(importXml);
                string ver = (string)doc.Root?.Element("ExportedVersion");
                return string.IsNullOrWhiteSpace(ver) ? defaultVersion : ver.Trim();
            }
            catch
            {
                return defaultVersion;
            }
        }

        private static string ReadOrBuildHpSourceInfo(string localFolder, string application, string hpFolder)
        {
            if (!string.IsNullOrWhiteSpace(localFolder))
            {
                string path = Path.Combine(localFolder, PathConstants.CalcManagerDirName,
                                           hpFolder, "info", "sourceInfo.xml");
                if (File.Exists(path))
                    try { return File.ReadAllText(path, Encoding.UTF8); } catch { }
            }

            // Default — matches what EPM generates
            return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<sourceInfo>
   <Application>{XmlAttr(application)}</Application>
   <Product>HP</Product>
   <ProductVersion>11.1.2.3</ProductVersion>
   <Project>Default Application Group</Project>
   <usesFriendlyNames>false</usesFriendlyNames>
   <metadataFileSupported>false</metadataFileSupported>
   <groupingSupported>true</groupingSupported>
   <LCMVersion>11.1.2</LCMVersion>
</sourceInfo>";
        }

        private static string ReadOrBuildCalcSourceInfo(string localFolder, string calcFolder)
        {
            if (!string.IsNullOrWhiteSpace(localFolder))
            {
                string path = Path.Combine(localFolder, PathConstants.CalcManagerDirName,
                                           calcFolder, "info", "sourceInfo.xml");
                if (File.Exists(path))
                    try { return File.ReadAllText(path, Encoding.UTF8); } catch { }
            }

            return
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<sourceInfo>
   <Application>CalcApp</Application>
   <Product>CALC</Product>
   <ProductVersion>11.1.2.3</ProductVersion>
   <Project>Foundation</Project>
   <usesFriendlyNames>false</usesFriendlyNames>
   <metadataFileSupported>false</metadataFileSupported>
   <groupingSupported>true</groupingSupported>
   <LCMVersion>11.1.2</LCMVersion>
</sourceInfo>";
        }

        /// <summary>
        /// Tries to read the original HP rule file from the CalcManager pull directory and
        /// update its script element with <paramref name="scriptText"/>. Falls back to
        /// deriving the HP structure from <paramref name="calcRuleContent"/> (the rebuilt
        /// CALC XML) when no original HP file was pulled.
        /// </summary>
        private static string ReadOrBuildHpRuleXml(
            string localFolder,
            string hpFolder,
            string cube,
            string ruleName,
            string scriptText,
            string calcRuleContent,
            bool preferCalc)
        {
            // --- Try to read original HP file and update its script ---
            // Skipped when the runtime prompts changed: the original HP file still has the
            // old prompts, while the rebuilt CALC XML already carries the new ones.
            if (!preferCalc && !string.IsNullOrWhiteSpace(localFolder))
            {
                string hpPath = Path.Combine(
                    localFolder, PathConstants.CalcManagerDirName, hpFolder,
                    "resource", "Cube", cube, "Calculation Manager Rules",
                    PathConstants.ToFileName(ruleName) + ".xml");

                if (File.Exists(hpPath))
                {
                    try
                    {
                        string hpOriginal = File.ReadAllText(hpPath, Encoding.UTF8);
                        return UpdateHpScriptContent(hpOriginal, scriptText);
                    }
                    catch { /* fall through to convert from CALC */ }
                }
            }

            // --- Convert CALC XML to HP format ---
            return ConvertCalcToHpXml(calcRuleContent, scriptText);
        }

        /// <summary>
        /// Reads the original CALC rule artifact pulled from EPM Cloud and swaps in
        /// <paramref name="scriptText"/> as its &lt;script&gt; content, preserving
        /// whichever text-node style (CDATA vs. plain escaped text) the original used
        /// — see <see cref="UpdateCalcScriptContent"/>. This artifact is never opened
        /// for editing; it exists purely as the reconstruction template so the .csc/.js
        /// file the user edits can hold plain, human-readable script text.
        /// </summary>
        private static string ReadOrBuildCalcRuleXml(
            string localFolder,
            string calcFolder,
            string planType,
            string cube,
            string ruleName,
            string scriptText,
            IProgress<string> progress,
            out bool promptsChanged)
        {
            string calcPath = string.IsNullOrWhiteSpace(localFolder) ? null : Path.Combine(
                localFolder, PathConstants.CalcManagerDirName, calcFolder,
                "resource", PathConstants.PlanningDirName, planType, cube, "Rules",
                PathConstants.ToFileName(ruleName));

            if (calcPath == null || !File.Exists(calcPath))
                throw new EpmException(
                    "Could not find the original pulled artifact for this rule:\n" +
                    (calcPath ?? "(no local folder configured for this connection)") +
                    "\n\nRun 'Pull Calculations' again so the plugin has the original CALC " +
                    "metadata to merge your edited script back into.");

            string calcOriginal = File.ReadAllText(calcPath, Encoding.UTF8);
            XDocument doc = XDocument.Parse(calcOriginal);

            // Add / remove runtime prompts to match references added to or deleted from
            // the script since it was pulled.
            string originalScript = FindScriptElement(doc)?.Value ?? "";
            promptsChanged = RuleVariableSync.Apply(
                doc, originalScript, scriptText, cube, ruleName,
                () => VariableDictionary.LoadOrRebuild(localFolder), progress);

            return UpdateCalcScriptContent(doc, scriptText);
        }

        private static XElement FindScriptElement(XDocument doc) =>
            doc.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ //
        //  HP XML manipulation
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Resolves the plain script text to deploy from what's currently in the editor.
        /// New-format pulls write the plain, already-decoded script directly to the
        /// .csc/.js file, so <paramref name="editedContent"/> is used as-is. Pre-refactor
        /// pulls wrote the entire raw CALC XML artifact instead (with "&amp;lt;"/"&amp;gt;"
        /// standing in for "&lt;"/"&gt;"), so as a backward-compatibility path we detect
        /// that shape — starts with "&lt;" and parses as XML with a &lt;script&gt; element
        /// — and unwrap it. Anything else (including a buffer that merely starts with "&lt;"
        /// but isn't actually XML) is treated as plain script.
        /// </summary>
        internal static string ResolveScriptText(string editedContent)
        {
            string trimmed = editedContent?.TrimStart();
            if (!string.IsNullOrEmpty(trimmed) && trimmed[0] == '<')
            {
                try
                {
                    XDocument doc = XDocument.Parse(editedContent);
                    XElement scriptEl = doc.Descendants()
                        .FirstOrDefault(e => string.Equals(e.Name.LocalName, "script",
                            StringComparison.OrdinalIgnoreCase));
                    if (scriptEl != null)
                        return scriptEl.Value; // .Value handles both XText and XCData nodes
                }
                catch { /* not actually XML — fall through and treat as plain script */ }
            }

            return editedContent;
        }

        /// <summary>
        /// Updates the &lt;script&gt; element of the HP rule XML with a new script body,
        /// wrapping it in CDATA as the HP format requires. Returns the updated XML string.
        /// </summary>
        private static string UpdateHpScriptContent(string hpXml, string newScript)
        {
            XDocument doc = XDocument.Parse(hpXml);
            XElement scriptEl = doc.Descendants("script").FirstOrDefault();
            if (scriptEl != null)
            {
                scriptEl.Nodes().Remove();                      // remove old CDATA / text
                scriptEl.Add(new XCData(LfOnly(newScript)));    // add new CDATA node
            }
            return RuleXmlWriter.Hp(doc);
        }

        /// <summary>
        /// Updates the &lt;script&gt; element of the original CALC rule XML with a new
        /// script body, preserving whichever text-node style (CDATA vs. plain escaped
        /// text) the original artifact used — so the rebuilt XML stays byte-for-byte
        /// faithful to what EPM exported other than the script content itself.
        /// </summary>
        private static string UpdateCalcScriptContent(XDocument doc, string newScript)
        {
            XElement scriptEl = FindScriptElement(doc);

            if (scriptEl != null)
            {
                bool wasCData = scriptEl.Nodes().OfType<XCData>().Any();
                string text   = LfOnly(newScript);
                scriptEl.Nodes().Remove();
                scriptEl.Add(wasCData ? (XNode)new XCData(text) : new XText(text));
            }

            return RuleXmlWriter.Calc(doc);
        }

        /// <summary>
        /// Converts the (rebuilt) CALC XML document into HP format the way EPM writes it:
        ///  1. &lt;deployobjects&gt; removed (HP files have none) and no root attributes
        ///  2. variable ids renumbered 1..n in document order, references re-pointed
        ///  3. &lt;script&gt; in CDATA
        ///  4. EPM's HP layout — see <see cref="RuleXmlWriter.Hp"/>
        /// Used whenever no original HP artifact was pulled (Pull exports CALC only).
        /// </summary>
        private static string ConvertCalcToHpXml(string calcRuleContent, string scriptText)
        {
            try
            {
                XDocument doc = XDocument.Parse(calcRuleContent);

                // Remove <deployobjects> element (HP format doesn't have it); the root
                // carries no attributes in HP files (CALC exports may have version="…")
                doc.Descendants("deployobjects").Remove();
                doc.Root?.RemoveAttributes();

                // Renumber variable ids 1..n as EPM does, keeping references consistent
                var newIds = new Dictionary<string, string>();
                int next = 1;
                foreach (XElement v in doc.Root?.Element("variables")?.Elements("variable") ?? Enumerable.Empty<XElement>())
                {
                    string oldId = (string)v.Attribute("id");
                    string newId = (next++).ToString();
                    if (oldId != null && !newIds.ContainsKey(oldId)) newIds[oldId] = newId;
                    v.SetAttributeValue("id", newId);
                }
                foreach (XElement r in doc.Descendants("variable_reference"))
                {
                    string oldId = (string)r.Attribute("id");
                    if (oldId != null && newIds.TryGetValue(oldId, out string newId))
                        r.SetAttributeValue("id", newId);
                }

                // Wrap <script> content in CDATA
                XElement scriptEl = doc.Descendants("script").FirstOrDefault();
                if (scriptEl != null)
                {
                    scriptEl.Nodes().Remove();
                    scriptEl.Add(new XCData(LfOnly(scriptText)));
                }

                return RuleXmlWriter.Hp(doc);
            }
            catch (Exception ex)
            {
                throw new EpmException(
                    "Could not convert the rebuilt CALC XML to HP format.\n\n" + ex.Message);
            }
        }

        /// <summary>Script text with EPM's line endings: "\n" only.</summary>
        private static string LfOnly(string script) =>
            (script ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        // ------------------------------------------------------------------ //
        //  XML manifest builders
        // ------------------------------------------------------------------ //

        /// <param name="hpFolder">Null for a Calculation Manager-only package (no HP task).</param>
        private static string BuildExportXml(
            string application, string planType, string cube, string ruleName, string hpFolder)
        {
            string hpTask = hpFolder == null ? "" :
$@"
   <Task>
      <Source type=""Application"" product=""HP"" project=""Default Application Group"" application=""{XmlAttr(application)}""/>
      <Target type=""FileSystem"" filePath=""/{XmlAttr(hpFolder)}""/>
      <Artifact recursive=""false"" parentPath=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" pattern=""{XmlAttr(ruleName)}""/>
   </Task>";
            return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Package>
   <LOCALE>en_GB</LOCALE>
   <User name="""" password=""""/>{hpTask}
   <Task>
      <Source type=""Application"" product=""CALC"" project=""Foundation"" application=""Calculation Manager""/>
      <Target type=""FileSystem"" filePath=""/CALC-Calculation Manager""/>
      <Artifact recursive=""false"" parentPath=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" pattern=""{XmlAttr(ruleName)}""/>
   </Task>
</Package>";
        }

        private static string BuildRootImportXml(
            string application, string planType, string cube, string ruleName,
            string hpFolder, string username, string exportedVersion,
            string exportedDate, string exportedTime)
        {
            string hpTask = hpFolder == null ? "" :
$@"
   <Task>
      <Source type=""FileSystem"" filePath=""/{XmlAttr(hpFolder)}""/>
      <Target type=""Application"" product=""HP"" project=""Default Application Group"" application=""{XmlAttr(application)}""/>
      <Artifact recursive=""false"" parentPath=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" pattern=""{XmlAttr(ruleName)}""/>
   </Task>";
            return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Package>
   <LOCALE>en_GB</LOCALE>
   <User name="""" password=""""/>
   <ExportedVersion>{XmlVal(exportedVersion)}</ExportedVersion>
   <ExportedDateUTC>{XmlVal(exportedDate)}</ExportedDateUTC>
   <ExportedTimeUTC>{XmlVal(exportedTime)}</ExportedTimeUTC>
   <ExportedBy>{XmlVal(username)}</ExportedBy>
   <IDMDomain></IDMDomain>
   <ServiceInstance></ServiceInstance>{hpTask}
   <Task>
      <Source type=""FileSystem"" filePath=""/CALC-Calculation Manager""/>
      <Target type=""Application"" product=""CALC"" project=""Foundation"" application=""Calculation Manager""/>
      <Artifact recursive=""false"" parentPath=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" pattern=""{XmlAttr(ruleName)}""/>
   </Task>
</Package>";
        }

        private static string BuildScopedImportXml(
            string product, string project, string targetApp,
            string parentPath, string ruleName,
            string username, string exportedVersion,
            string exportedDate, string exportedTime)
        {
            return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Package>
   <LOCALE>en_GB</LOCALE>
   <User name="""" password=""""/>
   <ExportedVersion>{XmlVal(exportedVersion)}</ExportedVersion>
   <ExportedDateUTC>{XmlVal(exportedDate)}</ExportedDateUTC>
   <ExportedTimeUTC>{XmlVal(exportedTime)}</ExportedTimeUTC>
   <ExportedBy>{XmlVal(username)}</ExportedBy>
   <IDMDomain></IDMDomain>
   <ServiceInstance></ServiceInstance>
   <Task>
      <Source type=""FileSystem"" filePath=""/""/>
      <Target type=""Application"" product=""{XmlAttr(product)}"" project=""{XmlAttr(project)}"" application=""{XmlAttr(targetApp)}""/>
      <Artifact recursive=""false"" parentPath=""{XmlAttr(parentPath)}"" pattern=""{XmlAttr(ruleName)}""/>
   </Task>
</Package>";
        }

        private static string BuildHpListingXml(string cube, string ruleName, long timestamp)
        {
            string ts = timestamp.ToString();
            return
$@"<?xml version=""1.0"" encoding=""utf-8""?>" +
$@"<artifactListing>" +
$@"<resource name=""{XmlAttr(ruleName)}"" id=""{XmlAttr(ruleName)}"" type=""Rule"" cloneOnly=""false"" size="""" " +
$@"path=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" " +
$@"pathAlias=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" " +
$@"modifiedBy="""" lastUpdated=""{ts}"" description="""" />" +
$@"<folder name=""Cube"" type=""Folder"" size="""" id="""" path=""/Cube"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />" +
$@"<folder name=""{XmlAttr(cube)}"" type=""Folder"" size="""" id="""" path=""/Cube/{XmlAttr(cube)}"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />" +
$@"<folder name=""Calculation Manager Rules"" type=""Folder"" size="""" id="""" " +
$@"path=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" " +
$@"pathAlias=""/Cube/{XmlAttr(cube)}/Calculation Manager Rules"" " +
$@"modifiedBy="""" lastUpdated="""" description="""" />" +
$@"</artifactListing>";
        }

        private static string BuildCalcListingXml(string planType, string cube, string ruleName, long timestamp)
        {
            string ts = timestamp.ToString();
            return
$@"<?xml version=""1.0"" encoding=""utf-8""?>" +
$@"<artifactListing>" +
$@"<resource name=""{XmlAttr(ruleName)}"" id=""{XmlAttr(ruleName)}"" type=""Rule"" cloneOnly=""false"" size="""" " +
$@"path=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" " +
$@"pathAlias=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" " +
$@"modifiedBy="""" lastUpdated=""{ts}"" description="""" />" +
$@"<folder name=""Planning"" type=""Folder"" size="""" id="""" path=""/Planning"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />" +
$@"<folder name=""{XmlAttr(planType)}"" type=""Folder"" size="""" id="""" path=""/Planning/{XmlAttr(planType)}"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />" +
$@"<folder name=""{XmlAttr(cube)}"" type=""Folder"" size="""" id="""" " +
$@"path=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}"" pathAlias="""" modifiedBy="""" lastUpdated="""" description="""" />" +
$@"<folder name=""Rules"" type=""Folder"" size="""" id="""" " +
$@"path=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" " +
$@"pathAlias=""/Planning/{XmlAttr(planType)}/{XmlAttr(cube)}/Rules"" " +
$@"modifiedBy="""" lastUpdated="""" description="""" />" +
$@"</artifactListing>";
        }

        // ------------------------------------------------------------------ //
        //  ZIP entry helper
        // ------------------------------------------------------------------ //

        private static void Add(ZipArchive zip, string entryName, string content)
        {
            // ZIP entries use forward slashes; ZipArchive on Windows accepts them
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream s = entry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        // ------------------------------------------------------------------ //
        //  XML escaping helpers
        // ------------------------------------------------------------------ //

        /// <summary>Escapes a value for use in an XML attribute (double-quoted).</summary>
        private static string XmlAttr(string v)
        {
            if (string.IsNullOrEmpty(v)) return string.Empty;
            return v.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        /// <summary>Escapes a value for use as XML element text content.</summary>
        private static string XmlVal(string v)
        {
            if (string.IsNullOrEmpty(v)) return string.Empty;
            return v.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
        }
    }
}
