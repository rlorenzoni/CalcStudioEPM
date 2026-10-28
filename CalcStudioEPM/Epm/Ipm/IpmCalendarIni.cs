using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// The IPM calendars (Global Artifacts › Calendar: one &lt;IPMCalendar&gt; per calendar, its
    /// settings as JSON) as Calendars.ini — a section per calendar, named like it:
    ///
    ///   [Budget Calendar2]
    ///   Description =
    ///   Cubes = Visio
    ///   Members = IRSiblings(FY24)->ILvl0Descendants(YearTotal)
    ///   Start = FY24->Jan · Current = FY26->Sep · End = FY27->Dec
    ///   Historical periods = 32 · Future periods = 16
    ///   Date mapping frequency = 3 · Date mapping start date = 2026-09-01
    ///   Dimensions = Years, Period
    ///
    /// Settings EPM adds later appear under their own JSON name. Jobs refer to a calendar by its
    /// ID, which isn't shown: a new section is a new calendar (with a new ID).
    /// </summary>
    internal static class IpmCalendarIni
    {
        private static readonly (string Key, string Json, bool List)[] Fields =
        {
            ("Cubes",                   "planType",             true),
            ("Members",                 "membersExpression",    false),
            ("Start",                   "startDate",            false),
            ("Current",                 "currentDate",          false),
            ("End",                     "endDate",              false),
            ("Historical periods",      "historicalPeriods",    false),
            ("Future periods",          "futurePeriods",        false),
            ("Date mapping frequency",  "dateMappingFreq",      false),
            ("Date mapping start date", "dateMappingStartDate", false),
            ("Dimensions",              "dims",                 true),
        };
        private const string DescriptionKey = "Description";
        private const string IdJson = "id";

        public sealed class Applied
        {
            public string Xml;
            public List<string> Changes = new List<string>();
            /// <summary>Calendars left out of the .ini: deployed unchanged.</summary>
            public List<string> Notes = new List<string>();
        }

        private static IEnumerable<XElement> Calendars(XDocument doc) =>
            doc.Root?.Elements("IPMCalendar") ?? Enumerable.Empty<XElement>();

        private static JObject Details(XElement cal)
        {
            string json = cal.Element("IPMCalendarDetails")?.Value;
            return string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
        }

        /// <summary>The calendars' names, IDs and cubes.</summary>
        public static List<IpmCalendar> Read(string xml) =>
            Calendars(XDocument.Parse(xml)).Select(c =>
            {
                JObject d = Details(c);
                return new IpmCalendar
                {
                    Name  = (string)c.Attribute("name"),
                    Id    = (string)d[IdJson],
                    Cubes = (d["planType"] as JArray ?? new JArray()).Select(t => (string)t).ToList(),
                };
            }).ToList();

        private static string Show(JToken t, bool list) =>
            list ? IpmList.Join((t as JArray ?? new JArray()).Select(x => IpmList.Quote((string)x))) : IpmList.Show(t);

        // ------------------------------------------------------------------ //
        //  Pull: XML → .ini
        // ------------------------------------------------------------------ //

        public static string ToIni(string xml, string application)
        {
            var L = new List<string>
            {
                $"; Calendars — IPM · {application}",
                "; One section per calendar, named like it. Start, Current, End: year->period (substitution",
                "; variables allowed, e.g. &Budget_Year->Jan). Cubes and Dimensions: lists separated by commas.",
                "; Deploy (from this file) imports the calendars into Planning only. A new section adds a",
                "; calendar. Auto Predict jobs refer to a calendar by an ID kept out of sight: renaming a section",
                "; makes a new calendar, and a section removed here is kept as it is (remove calendars in EPM).",
                "",
            };
            foreach (XElement c in Calendars(XDocument.Parse(xml)))
            {
                JObject d = Details(c);
                L.Add($"[{(string)c.Attribute("name")}]");
                L.Add($"{DescriptionKey} = {(string)c.Attribute("description") ?? ""}");
                foreach (var f in Fields)
                    if (d[f.Json] != null) L.Add($"{f.Key} = {Show(d[f.Json], f.List)}");
                foreach (JProperty p in d.Properties())
                    if (p.Name != IdJson && Fields.All(f => f.Json != p.Name)) L.Add($"{p.Name} = {IpmList.Show(p.Value)}");
                L.Add("");
            }
            return string.Join("\n", L).TrimEnd('\n') + "\n";
        }

        // ------------------------------------------------------------------ //
        //  Deploy: .ini → XML (applied to the pulled XML)
        // ------------------------------------------------------------------ //

        public static Applied Apply(string xml, string iniText, string fileName, string modifiedBy)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> ini;
            try { ini = GraphicalRule.ParseIni(iniText); }
            catch (FormatException ex) { throw new EpmException($"{fileName}: {ex.Message}"); }

            XDocument doc = XDocument.Parse(xml);
            if (doc.Root == null || doc.Root.Name.LocalName != "IPMCalendars")
                throw new EpmException("The pulled Calendar.xml has no <IPMCalendars>. Pull IPM again.");
            var problems = new List<string>();
            var changes  = new List<string>();
            List<XElement> before = Calendars(doc).ToList();
            var after = new List<XElement>();

            foreach (var sec in ini)
            {
                string name = sec.Key.Trim();
                foreach (var g in sec.Value.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    problems.Add($"[{name}] '{g.Key}' appears more than once.");
                XElement old = before.FirstOrDefault(c => string.Equals((string)c.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
                XElement cal = old != null ? new XElement(old) : new XElement("IPMCalendar",
                    new XAttribute("name", name), new XAttribute("description", ""), new XAttribute("modifiedBy", modifiedBy ?? ""),
                    new XElement("IPMCalendarDetails"));
                JObject d = Details(cal);
                string beforeJson = d.ToString(Formatting.None), beforeDesc = (string)cal.Attribute("description") ?? "";
                if (old == null) d[IdJson] = Guid.NewGuid().ToString();

                foreach (var kv in sec.Value)
                {
                    string v = kv.Value.Trim();
                    if (string.Equals(kv.Key, DescriptionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        cal.SetAttributeValue("description", v);
                        continue;
                    }
                    var field = Fields.FirstOrDefault(f => string.Equals(f.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                    string json = field.Json ?? d.Properties().Select(p => p.Name)
                        .FirstOrDefault(n => n != IdJson && string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (json == null) { problems.Add($"[{name}] unknown line '{kv.Key}'."); continue; }
                    if (field.List)
                    {
                        d[json] = new JArray(IpmList.Split(v).Select(IpmList.Unquote));
                        continue;
                    }
                    JToken current = d[json] ?? (json == "historicalPeriods" || json == "futurePeriods" || json == "dateMappingFreq"
                                                 ? (JToken)new JValue(0L) : new JValue(""));
                    JToken parsed = IpmList.Parse(current, v, out string problem);
                    if (problem != null) problems.Add($"[{name}] {kv.Key} {problem}, not '{v}'.");
                    else d[json] = parsed;
                }
                if (old == null)
                    foreach (var f in Fields.Where(f => f.Json != "dateMappingStartDate" && d[f.Json] == null))
                        problems.Add($"[{name}] is a new calendar: '{f.Key}' is needed.");

                cal.Element("IPMCalendarDetails").ReplaceNodes(new XCData(d.ToString(Formatting.None)));
                after.Add(cal);
                if (old == null) changes.Add($"new calendar [{name}]");
                else
                {
                    if (beforeDesc != ((string)cal.Attribute("description") ?? ""))
                        changes.Add($"[{name}] {DescriptionKey}: {beforeDesc} → {(string)cal.Attribute("description")}");
                    JObject was = JObject.Parse(beforeJson);
                    foreach (JProperty p in d.Properties())
                        if (!JToken.DeepEquals(p.Value, was[p.Name]))
                        {
                            var f = Fields.FirstOrDefault(x => x.Json == p.Name);
                            string key = f.Key ?? p.Name;
                            changes.Add($"[{name}] {key}: {Show(was[p.Name], f.List)} → {Show(p.Value, f.List)}");
                        }
                }
            }
            var notes = new List<string>();
            foreach (XElement gone in before.Where(c => !after.Any(a => string.Equals((string)a.Attribute("name"), (string)c.Attribute("name"), StringComparison.OrdinalIgnoreCase))))
            {
                notes.Add($"[{(string)gone.Attribute("name")}] isn't in {fileName}: it is kept as it is (calendars are removed in EPM).");
                after.Add(gone);
            }

            if (problems.Count > 0)
                throw new EpmException($"{fileName} can't be deployed:\n\n• " + string.Join("\n• ", problems.Distinct()));
            doc.Root.ReplaceNodes(after);
            return new Applied { Xml = IpmList.Xml(doc), Changes = changes, Notes = notes };
        }
    }
}
