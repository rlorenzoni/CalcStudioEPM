using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// Where the IPM artifacts live. Pull exports them as their own package (Planning's
    /// Global Artifacts: the Auto Predict folder, Calendar and Events) and extracts it to
    /// {LocalFolder}\IPM, apart from Calculation Manager's export:
    ///   {LocalFolder}\IPM\HP-{app}\resource\Global Artifacts\Auto Predict\{job}.xml
    ///   {LocalFolder}\IPM\HP-{app}\resource\Global Artifacts\Calendar.xml
    ///   {LocalFolder}\IPM\HP-{app}\resource\Global Artifacts\Events.xml
    /// The files to edit go to the scripts root:
    ///   {name}.Scripts\IPM\Auto Predict\{job}.autopredict.ini   (see <see cref="AutoPredictIni"/>)
    ///   {name}.Scripts\IPM\Calendars.ini                        (see <see cref="IpmCalendarIni"/>)
    ///   {name}.Scripts\IPM\Events.xml                           (as EPM exports it)
    /// </summary>
    internal static class IpmFiles
    {
        public const string AutoPredictFolder = "Auto Predict";
        public const string AutoPredictSuffix = ".autopredict.ini";
        public const string CalendarsIni      = "Calendars.ini";
        public const string EventsXml         = "Events.xml";

        /// <summary>LCM artifacts: parent path and name, as Import.xml and listing.xml name them.</summary>
        public const string GlobalArtifacts   = "/Global Artifacts";
        public const string AutoPredictPath   = "/Global Artifacts/Auto Predict";
        public const string CalendarArtifact  = "Calendar";
        public const string EventsArtifact    = "Events";

        public enum Kind { None, AutoPredict, Calendars, Events }

        public static string PackageDir(string localFolder) => Path.Combine(localFolder ?? "", PathConstants.IpmDirName);

        public static string EditDir(ConnectionConfig cfg) =>
            Path.Combine(PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name), PathConstants.IpmDirName);

        /// <summary>The HP-{app} folder of the pulled package; null when IPM was never pulled.</summary>
        public static string HpDir(string localFolder)
        {
            string dir = PackageDir(localFolder);
            if (!Directory.Exists(dir)) return null;
            return Directory.GetDirectories(dir, "HP-*").FirstOrDefault(d => File.Exists(Path.Combine(d, "info", "listing.xml")));
        }

        public static string GlobalArtifactsDir(string localFolder)
        {
            string hp = HpDir(localFolder);
            return hp == null ? null : Path.Combine(hp, "resource", "Global Artifacts");
        }

        public static string PulledCalendar(string localFolder)
        {
            string g = GlobalArtifactsDir(localFolder);
            return g == null ? null : Path.Combine(g, CalendarArtifact + ".xml");
        }

        public static string PulledEvents(string localFolder)
        {
            string g = GlobalArtifactsDir(localFolder);
            return g == null ? null : Path.Combine(g, EventsArtifact + ".xml");
        }

        /// <summary>The pulled XML of an Auto Predict job (found by its name when the file name
        /// differs); null when it isn't in the last pull.</summary>
        public static string PulledAutoPredict(string localFolder, string jobName)
        {
            string g = GlobalArtifactsDir(localFolder);
            if (g == null) return null;
            string dir = Path.Combine(g, AutoPredictFolder);
            string path = Path.Combine(dir, PathConstants.ToFileName(jobName) + ".xml");
            if (File.Exists(path)) return path;
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.xml").FirstOrDefault(f =>
            {
                try { return string.Equals(AutoPredictIni.JobName(File.ReadAllText(f, Encoding.UTF8)), jobName, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }

        /// <summary>What an IPM file to edit is; <paramref name="jobName"/> is the Auto Predict job's name.</summary>
        public static Kind KindOf(string path, out string jobName)
        {
            jobName = null;
            if (string.IsNullOrEmpty(path) || LocalChanges.IsLocalCopy(path)) return Kind.None;
            string file = Path.GetFileName(path);
            string dir  = Path.GetDirectoryName(path) ?? "";
            if (file.EndsWith(AutoPredictSuffix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(dir), AutoPredictFolder, StringComparison.OrdinalIgnoreCase) &&
                IsIpmDir(Path.GetDirectoryName(dir)))
            {
                jobName = PathConstants.FromFileName(file.Substring(0, file.Length - AutoPredictSuffix.Length));
                return Kind.AutoPredict;
            }
            if (!IsIpmDir(dir)) return Kind.None;
            if (string.Equals(file, CalendarsIni, StringComparison.OrdinalIgnoreCase)) return Kind.Calendars;
            if (string.Equals(file, EventsXml, StringComparison.OrdinalIgnoreCase)) return Kind.Events;
            return Kind.None;
        }

        /// <summary>…\{Connection}.Scripts\IPM</summary>
        private static bool IsIpmDir(string dir) =>
            dir != null && string.Equals(Path.GetFileName(dir), PathConstants.IpmDirName, StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(Path.GetDirectoryName(dir) ?? "") ?? "").EndsWith(".Scripts", StringComparison.OrdinalIgnoreCase);

        /// <summary>True for a file in the IPM folder of the scripts root <paramref name="scriptsRoot"/>
        /// (left alone by a pull of the calculations).</summary>
        public static bool IsInEditDir(string scriptsRoot, string file) =>
            file.StartsWith(Path.Combine(scriptsRoot, PathConstants.IpmDirName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A pulled calendar: what jobs refer to it by.</summary>
    internal sealed class IpmCalendar
    {
        public string Name, Id;
        public List<string> Cubes = new List<string>();
    }

    /// <summary>What turning IPM XML into .ini files and back needs to know about the connection.</summary>
    internal sealed class IpmContext
    {
        public string Application;
        public IpmMembers Members;
        public List<IpmCalendar> Calendars = new List<IpmCalendar>();

        public static IpmContext For(ConnectionConfig cfg)
        {
            var ctx = new IpmContext
            {
                Application = cfg.Application,
                Members     = new IpmMembers(EpmMetadata.Load(cfg.LocalFolderPath)),
            };
            string cal = IpmFiles.PulledCalendar(cfg.LocalFolderPath);
            if (cal != null && File.Exists(cal))
                try { ctx.Calendars = IpmCalendarIni.Read(File.ReadAllText(cal, Encoding.UTF8)); } catch { }
            return ctx;
        }

        public IpmCalendar Calendar(string name) =>
            Calendars.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Member IDs ↔ names. Auto Predict stores the members of its outputs and drivers by
    /// Planning member ID; the dimension download (Configure EPM Connection) has every
    /// member's ID. A member it doesn't know is shown as {id:…} and kept as it is.
    /// </summary>
    internal sealed class IpmMembers
    {
        private static readonly Regex Guid = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
        private static readonly Regex IdRef = new Regex(@"^\{id:\s*([^}]+?)\s*\}$", RegexOptions.IgnoreCase);

        private readonly EpmMetadata _md;
        private readonly Dictionary<string, Dictionary<string, string>> _ids =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public IpmMembers(EpmMetadata md) { _md = md; }

        public bool HasDimensions => _md.HasDimensions;

        /// <summary>The member's name, or {id:…} when it isn't known.</summary>
        public string Show(string id)
        {
            string name = _md.MemberById(id)?.Node.Name;
            return name != null ? IpmList.Quote(name) : "{id:" + id + "}";
        }

        /// <summary>The ID of the member named <paramref name="item"/> (or written {id:…} or as a
        /// bare ID) in <paramref name="dimension"/>; null, with the reason, when there is none.</summary>
        public string Id(string dimension, string item, out string problem)
        {
            problem = null;
            string v = IpmList.Unquote(item);
            Match m = IdRef.Match(v);
            if (m.Success) return m.Groups[1].Value;
            if (Guid.IsMatch(v)) return v;
            if (!_md.HasDimensions)
            {
                problem = $"'{v}': member names need the dimensions and members (tick 'Download dimensions and members' in " +
                          "Configure EPM Connection and pull them).";
                return null;
            }
            Dictionary<string, string> byName = IdsOf(dimension);
            if (byName == null) { problem = $"'{dimension}' is not a dimension of the application."; return null; }
            if (byName.TryGetValue(v, out string id)) return id;
            problem = _md.Dims.ByName[dimension].Members.Contains(v, StringComparer.OrdinalIgnoreCase)
                ? $"{dimension} '{v}' has no member ID in the downloaded dimensions; pull the dimensions and members again."
                : $"'{v}' is not a member of {dimension}.";
            return null;
        }

        private Dictionary<string, string> IdsOf(string dimension)
        {
            if (_ids.TryGetValue(dimension, out var hit)) return hit;
            if (!_md.Dims.ByName.TryGetValue(dimension, out EpmMetadata.DimensionInfo d)) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EpmMetadata.MemberNode n in d.Nodes ?? new List<EpmMetadata.MemberNode>())
                if (!string.IsNullOrEmpty(n.Id) && !map.ContainsKey(n.Name)) map[n.Name] = n.Id;
            foreach (EpmMetadata.MemberNode n in d.Nodes ?? new List<EpmMetadata.MemberNode>())
                if (!string.IsNullOrEmpty(n.Id) && !string.IsNullOrEmpty(n.Alias) && !map.ContainsKey(n.Alias)) map[n.Alias] = n.Id;
            return _ids[dimension] = map;
        }
    }

    /// <summary>Comma lists in the IPM .ini files: "a, b, c"; an item holding a comma or a quote
    /// is written in "quotes" (a quote inside doubled). Commas inside (…) or […] don't split.</summary>
    internal static class IpmList
    {
        public static List<string> Split(string s)
        {
            var items = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            bool quoted = false;
            s = s ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && (c == '(' || c == '[')) depth++;
                else if (!quoted && (c == ')' || c == ']')) depth = Math.Max(0, depth - 1);
                else if (!quoted && depth == 0 && c == ',')
                {
                    items.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            items.Add(sb.ToString().Trim());
            return items.Where(x => x.Length > 0).ToList();
        }

        public static string Join(IEnumerable<string> items) => string.Join(", ", items);

        public static string Quote(string name) =>
            name.IndexOfAny(new[] { ',', '"' }) >= 0 || name != name.Trim()
                ? "\"" + name.Replace("\"", "\"\"") + "\"" : name;

        public static string Unquote(string item)
        {
            string t = (item ?? "").Trim();
            return t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"' ? t.Substring(1, t.Length - 2).Replace("\"\"", "\"") : t;
        }

        /// <summary>"FY26->Sep" ↔ (FY26, Sep).</summary>
        public static bool SplitPeriod(string v, out string year, out string period)
        {
            int arrow = (v ?? "").IndexOf("->", StringComparison.Ordinal);
            year = arrow < 0 ? null : v.Substring(0, arrow).Trim();
            period = arrow < 0 ? null : v.Substring(arrow + 2).Trim();
            return arrow > 0 && year.Length > 0 && period.Length > 0;
        }

        public static bool Bool(string v, out bool b)
        {
            b = string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            return b || string.Equals(v?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A JSON option as written in the .ini, and back (typed like its current value).</summary>
        public static string Show(JToken t) =>
            t == null || t.Type == JTokenType.Null ? "" :
            t.Type == JTokenType.Boolean ? ((bool)t ? "true" : "false") :
            t.Type == JTokenType.String ? (string)t :
            t.Type == JTokenType.Float || t.Type == JTokenType.Integer
                ? Convert.ToString(((JValue)t).Value, System.Globalization.CultureInfo.InvariantCulture)
                : t.ToString(Newtonsoft.Json.Formatting.None);

        public static JToken Parse(JToken current, string text, out string problem)
        {
            problem = null;
            string v = (text ?? "").Trim();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (current?.Type ?? JTokenType.String)
            {
                case JTokenType.Boolean:
                    if (Bool(v, out bool b)) return new JValue(b);
                    problem = "must be true or false"; return null;
                case JTokenType.Integer:
                    if (long.TryParse(v, System.Globalization.NumberStyles.Integer, inv, out long l)) return new JValue(l);
                    problem = "must be a whole number"; return null;
                case JTokenType.Float:
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out double d)) return new JValue(d);
                    problem = "must be a number"; return null;
                case JTokenType.String:
                case JTokenType.Null:
                    return new JValue(v);
                default:
                    try { return JToken.Parse(v); } catch { problem = "must be JSON"; return null; }
            }
        }

        /// <summary>XML the way the IPM export writes it (declaration with " ?", two-space indent, CDATA kept).</summary>
        public static string Xml(XDocument doc) => RuleXmlWriter.Hp(doc);
    }
}
