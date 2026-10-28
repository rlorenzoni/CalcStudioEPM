using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// An Auto Predict job (IPM, Planning's Global Artifacts › Auto Predict) as an .ini whose
    /// sections follow the steps of EPM's Auto Predict wizard:
    ///
    ///   [1 Types]          Description, Generate predictions, the insights       (APBatch attributes)
    ///   [2 Calendar]       Calendar, periods, start / end                        (calendarName, APBatchDateRange)
    ///   [3 Historical Data] Define Model Scope                                    (Historical data source slice)
    ///   [3 Historical Data | Output | AMP], [… | Driver | Marketing]             (MLModelMapping "features")
    ///   [4 Future Data]    Define Model Scope, fitted values                     (Base prediction target, Fitted Values slices)
    ///   [4 Future Data | Output | AMP] …                                         (MLModelMapping "forecastFeatures")
    ///   [5 Prepare Data]   missing values, outliers, feature engineering         (features, APPredictionOptions)
    ///   [6 Model Settings] the other prediction options                          (APPredictionOptions)
    ///
    /// Advanced Predictions keep the members of each output and driver by member ID; here they
    /// are names (see <see cref="IpmMembers"/>). In Advanced Predictions the best and worst case
    /// slices hold the Model Scope's dimension names: they name the scope's lines and are not shown.
    ///
    /// Deploy applies the .ini to the pulled XML, so anything the .ini doesn't show (insight
    /// thresholds, report and bookkeeping attributes …) stays as EPM has it. A line left out keeps
    /// its value; an output or driver section left out removes it.
    /// </summary>
    internal static class AutoPredictIni
    {
        public const string Types = "1 Types", Calendar = "2 Calendar", Historical = "3 Historical Data",
                            Future = "4 Future Data", Prepare = "5 Prepare Data", Settings = "6 Model Settings";
        private static readonly string[] Steps = { Types, Calendar, Historical, Future, Prepare, Settings };

        private const string Bar = " | ";
        private const string OutputRole = "Output", DriverRole = "Driver";
        private const string InputTypeKey = "Input type", CalendarKey = "Calendar";
        private const string MissingKey = "Missing values", OutliersKey = "Outliers";

        private const string AutoPredict = "Auto Predict", Advanced = "Advanced Predictions", NoPrediction = "None";

        private static readonly (string Label, string Attr)[] Insights =
        {
            ("Forecast Variance & Bias Insights",  "isHistoricalInsightJob"),
            ("Prediction Insights",                "isPredictionInsightJob"),
            ("Anomaly Insights",                   "isAnomalyInsightJob"),
            ("Period Movement Variance Insights",  "isMovementVarianceInsightJob"),
        };

        /// <summary>SliceMapping/@sliceDefType → where it is shown.</summary>
        private static readonly (string Type, string Section, string Key)[] Slices =
        {
            ("Historical data source slice",       Historical, "Members"),
            ("Historical forecast slice",          Historical, "Historical forecast"),
            ("Base prediction target slice",       Future,     "Members"),
            ("Fitted Values Slice",                Future,     "Fitted values"),
            ("Best case prediction target slice",  Future,     "Best case"),
            ("Worst case prediction target slice", Future,     "Worst case"),
            ("Future forecast slice",              Future,     "Future forecast"),
        };
        private const string BestCase = "Best case prediction target slice", WorstCase = "Worst case prediction target slice";

        /// <summary>Advanced Predictions: options shown in [5 Prepare Data] under the wizard's label.</summary>
        private static readonly (string Label, string Option)[] PrepareOptions =
        {
            ("Predict missing input driver values",                "isImputeFutureValues"),
            ("Enable automated feature engineering and selection", "isEnableFeatureSelection"),
        };
        /// <summary>Auto Predict: the data-quality options shown in [5 Prepare Data] (by their own name).</summary>
        private static readonly string[] PrepareOptionsUnivariate =
            { "fillInMissingValues", "minimumMissingValueThreshold", "fillMissingLastPeriodHistData", "adjustOutliers" };

        public sealed class Applied
        {
            public string Xml;
            public List<string> Changes = new List<string>();
        }

        // ------------------------------------------------------------------ //
        //  XML helpers
        // ------------------------------------------------------------------ //

        public static string JobName(string xml) => (string)Root(XDocument.Parse(xml)).Attribute("name");

        private static XElement Root(XDocument doc) =>
            doc.Root != null && doc.Root.Name.LocalName == "APBatch"
                ? doc.Root : throw new EpmException("The file is not an Auto Predict job (no <APBatch>).");

        private static bool Flag(XElement e, string attr) =>
            string.Equals((string)e.Attribute(attr), "true", StringComparison.OrdinalIgnoreCase);

        private static string Prediction(XElement r) =>
            Flag(r, "isMultivariateJob") ? Advanced : Flag(r, "isAutoPredictJob") ? AutoPredict : NoPrediction;

        private static (string Type, string Section, string Key)? KeyOf(XElement slice)
        {
            string type = (string)slice.Attribute("sliceDefType");
            foreach (var s in Slices)
                if (string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        private static string Members(XElement slice) => slice.Element("MbrSelection")?.Value ?? "";

        /// <summary>The slices shown, first of each type, in the order of <see cref="Slices"/> (the Model Scope first).</summary>
        private static List<XElement> ShownSlices(XElement r)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return r.Elements("SliceMapping").Where(s => KeyOf(s) != null && seen.Add((string)s.Attribute("sliceDefType")))
                    .OrderBy(s => Array.FindIndex(Slices, x => string.Equals(x.Type, (string)s.Attribute("sliceDefType"), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
        }

        /// <summary>Advanced Predictions: the Model Scope's dimension names (held by the best case slice).</summary>
        private static List<string> ScopeDimensions(XElement r)
        {
            if (!Flag(r, "isMultivariateJob")) return null;
            XElement best = r.Elements("SliceMapping").FirstOrDefault(s => (string)s.Attribute("sliceDefType") == BestCase);
            return best == null ? null : IpmList.Split(Members(best));
        }

        private static bool IsScopeHeader(XElement r, XElement slice)
        {
            string type = (string)slice.Attribute("sliceDefType");
            if (!Flag(r, "isMultivariateJob") || (type != BestCase && type != WorstCase)) return false;
            XElement best = r.Elements("SliceMapping").FirstOrDefault(s => (string)s.Attribute("sliceDefType") == BestCase);
            return best != null && Members(slice) == Members(best);
        }

        /// <summary>Model Scope with one line per dimension: its members line up with the dimension names.</summary>
        private static bool PerDimension(XElement slice, string key, List<string> scope) =>
            scope != null && key == "Members" && IpmList.Split(Members(slice)).Count == scope.Count;

        private static JObject Options(XElement r)
        {
            string json = r.Element("APPredictionOptions")?.Value;
            return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
        }

        private static JObject Mapping(XElement r)
        {
            string json = r.Element("MLModelMapping")?.Value;
            return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
        }

        private static IEnumerable<JObject> FeaturesOf(JObject group) =>
            (group?["feature"] as JArray ?? new JArray()).OfType<JObject>();

        private static HashSet<string> Outputs(JObject group) =>
            new HashSet<string>((group?["outputFeatures"] as JArray ?? new JArray()).Select(t => (string)t), StringComparer.OrdinalIgnoreCase);

        /// <summary>(section, key) of a prediction option.</summary>
        private static (string Section, string Key) OptionKey(string option, bool multivariate)
        {
            if (multivariate)
            {
                foreach (var p in PrepareOptions)
                    if (p.Option == option) return (Prepare, p.Label);
            }
            else if (PrepareOptionsUnivariate.Contains(option)) return (Prepare, option);
            return (Settings, option);
        }

        /// <summary>
        /// For autocomplete: does the line <paramref name="key"/> of <paramref name="section"/>
        /// hold members, and of which dimension (null: any — a member list such as Members =)?
        /// </summary>
        public static bool IsMemberLine(string section, string key, out string dimension)
        {
            dimension = null;
            if (section.IndexOf(Bar, StringComparison.Ordinal) >= 0)
            {
                if (string.Equals(key, InputTypeKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, CalendarKey, StringComparison.OrdinalIgnoreCase)) return false;
                dimension = key;                                   // an output's or driver's dimension
                return key.Length > 0;
            }
            if (!string.Equals(section, Historical, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(section, Future, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Slices.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
                dimension = key;                                   // a Model Scope line
            return key.Length > 0;
        }

        // ------------------------------------------------------------------ //
        //  Pull: XML → .ini
        // ------------------------------------------------------------------ //

        public static string ToIni(string xml, IpmContext ctx)
        {
            XElement r = Root(XDocument.Parse(xml));
            string name = (string)r.Attribute("name");
            bool multi = Flag(r, "isMultivariateJob");
            JObject mapping = multi ? Mapping(r) : null;
            JObject options = Options(r);
            List<string> scope = ScopeDimensions(r);
            var L = new List<string>
            {
                $"; {name} — Auto Predict · {ctx.Application}",
                "; The sections follow EPM's Auto Predict wizard: 1 Types · 2 Calendar · 3 Historical Data ·",
                "; 4 Future Data · 5 Prepare Data · 6 Model Settings.",
                "; Deploy (from this file) imports the job into Planning only: Auto Predict is not part of",
                "; Calculation Manager. Execute runs it as an Auto Predict job.",
                "; Members by name, lists separated by commas: type a few letters for the member list. A line",
                "; left out keeps its value; settings not shown here (insight thresholds …) stay as in EPM.",
            };
            bool unnamed = false;
            string ShowIds(JToken ids)
            {
                var shown = (ids as JArray ?? new JArray()).Select(t => ctx.Members.Show((string)t)).ToList();
                unnamed |= shown.Any(s => s.StartsWith("{id:", StringComparison.Ordinal));
                return IpmList.Join(shown);
            }

            var body = new List<string>();

            // ---- 1 Types ----
            body.Add($"[{Types}]");
            body.Add("Description = " + (r.Element("description")?.Value ?? "").Replace("\r", " ").Replace("\n", " "));
            body.Add($"; {AutoPredict}, {Advanced} or {NoPrediction} ({Advanced} is chosen when the job is created in EPM)");
            body.Add("Generate predictions = " + Prediction(r));
            body.Add("; Insights: true or false");
            foreach (var (label, attr) in Insights)
                body.Add($"{label} = {(Flag(r, attr) ? "true" : "false")}");
            body.Add("");

            // ---- 2 Calendar ----
            body.Add($"[{Calendar}]");
            if (ctx.Calendars.Count > 0)
                body.Add("; Calendars (Calendars.ini): " + string.Join(", ", ctx.Calendars.Select(c => c.Name)));
            body.Add($"{CalendarKey} = {(string)r.Attribute("calendarName")}");
            XElement range = r.Element("APBatchDateRange");
            if (range != null)
            {
                body.Add("Historical periods = " + (string)range.Attribute("historicalPeriod"));
                body.Add("Future periods = " + (string)range.Attribute("futurePeriod"));
                body.Add("; Start and end of the prediction: year->period");
                body.Add("Start = " + Period(range.Element("startDate")));
                body.Add("End = " + Period(range.Element("endDate")));
            }
            body.Add("");

            // ---- 3 Historical Data, 4 Future Data ----
            foreach (string step in new[] { Historical, Future })
            {
                body.Add($"[{step}]");
                body.Add(step == Historical
                    ? "; Define Model Scope: where the historical data that trains the model is"
                    : "; Define Model Scope: where the prediction data is stored");
                foreach (XElement s in ShownSlices(r))
                {
                    var k = KeyOf(s).Value;
                    if (k.Section != step || IsScopeHeader(r, s)) continue;
                    List<string> items = IpmList.Split(Members(s));
                    if (PerDimension(s, k.Key, scope))
                        for (int i = 0; i < scope.Count; i++) body.Add($"{scope[i]} = {items[i]}");
                    else
                    {
                        if (k.Key != "Members") body.Add($"; {k.Type}");
                        body.Add($"{k.Key} = {IpmList.Join(items)}");
                    }
                }
                body.Add("");

                if (mapping == null) continue;
                JObject group = mapping[step == Historical ? "features" : "forecastFeatures"] as JObject;
                HashSet<string> outputs = Outputs(group);
                if (step == Historical)
                {
                    body.Add("; Output to predict and drivers (inputs): one section each, in this order. Add a driver with a");
                    body.Add($"; new [{Historical}{Bar}{DriverRole}{Bar}name] section (and its {Future} one, else it copies this");
                    body.Add("; one); delete both sections to remove it. An empty dimension line removes that dimension.");
                    body.Add("");
                }
                foreach (JObject f in FeaturesOf(group))
                {
                    string fname = (string)f["featureName"];
                    body.Add($"[{step}{Bar}{(outputs.Contains(fname) ? OutputRole : DriverRole)}{Bar}{fname}]");
                    body.Add($"{InputTypeKey} = {(string)f["inputType"]}");
                    body.Add($"{CalendarKey} = {(string)f["frequency"]}");
                    foreach (JObject dm in (f["dimMappings"] as JArray ?? new JArray()).OfType<JObject>())
                        body.Add($"{(string)dm["dimension"]} = {ShowIds(dm["members"])}");
                    body.Add("");
                }
            }

            // ---- 5 Prepare Data ----
            body.Add($"[{Prepare}]");
            if (options != null)
                foreach (JProperty p in options.Properties())
                {
                    var (section, key) = OptionKey(p.Name, multi);
                    if (section == Prepare) body.Add($"{key} = {IpmList.Show(p.Value)}");
                }
            if (mapping != null)
            {
                body.Add($"; Per output and driver — {MissingKey}: none, zero …  {OutliersKey}: none, three_sigma (Replace with z_score) …");
                foreach (JObject f in FeaturesOf(mapping["features"] as JObject))
                {
                    string fname = (string)f["featureName"];
                    body.Add($"{fname}{Bar}{MissingKey} = {(string)f["missingValues"]}");
                    body.Add($"{fname}{Bar}{OutliersKey} = {(string)f["outliers"]}");
                }
            }
            body.Add("");

            // ---- 6 Model Settings ----
            body.Add($"[{Settings}]");
            body.Add("; Prediction options as EPM stores them: true / false, numbers, text");
            if (options != null)
                foreach (JProperty p in options.Properties())
                {
                    var (section, key) = OptionKey(p.Name, multi);
                    if (section == Settings) body.Add($"{key} = {IpmList.Show(p.Value)}");
                }

            if (unnamed)
                L.Add("; {id:…} is a member whose name isn't known: download the dimensions and members (Configure EPM " +
                      "Connection), then pull IPM again. It is kept as it is.");
            L.Add("");
            L.AddRange(body);
            return string.Join("\n", L).TrimEnd('\n') + "\n";
        }

        private static string Period(XElement d) =>
            d == null ? "" : $"{(string)d.Attribute("year")}->{(string)d.Attribute("period")}";

        // ------------------------------------------------------------------ //
        //  Deploy: .ini → XML (applied to the pulled XML)
        // ------------------------------------------------------------------ //

        public static Applied Apply(string xml, string iniText, IpmContext ctx, string fileName)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> ini;
            try { ini = GraphicalRule.ParseIni(iniText); }
            catch (FormatException ex) { throw new EpmException($"{fileName}: {ex.Message}"); }

            XDocument doc = XDocument.Parse(xml);
            XElement r = Root(doc);
            bool multi = Flag(r, "isMultivariateJob");
            var problems = new List<string>();
            var changes  = new List<string>();
            var used     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sec in ini)
                foreach (var g in sec.Value.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    problems.Add($"[{sec.Key}] '{g.Key}' appears more than once.");

            string Get(string section, string key)
            {
                if (!ini.TryGetValue(section, out var rows)) return null;
                foreach (var kv in rows)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        used.Add(section + "\u0001" + kv.Key);
                        return kv.Value.Trim();
                    }
                return null;
            }
            void Changed(string section, string key, string before, string after) =>
                changes.Add($"[{section}] {key}: {(before.Length == 0 ? "(none)" : before)} → {(after.Length == 0 ? "(none)" : after)}");

            // ---- 1 Types ----
            string desc = Get(Types, "Description");
            if (desc != null)
            {
                XElement d = r.Element("description");
                string before = d?.Value ?? "";
                if (desc != before.Replace("\r", " ").Replace("\n", " "))
                {
                    if (d == null) r.AddFirst(d = new XElement("description"));
                    d.Value = desc;
                    Changed(Types, "Description", before, desc);
                }
            }
            string pred = Get(Types, "Generate predictions");
            if (pred != null)
            {
                string want = new[] { AutoPredict, Advanced, NoPrediction }.FirstOrDefault(p => string.Equals(p, pred, StringComparison.OrdinalIgnoreCase));
                string cur = Prediction(r);
                if (want == null) problems.Add($"[{Types}] Generate predictions must be {AutoPredict}, {Advanced} or {NoPrediction}, not '{pred}'.");
                else if (want != cur)
                {
                    if (want == Advanced || cur == Advanced)
                        problems.Add($"[{Types}] Generate predictions: switching to or from {Advanced} is done in EPM (it needs outputs and drivers).");
                    else
                    {
                        r.SetAttributeValue("isAutoPredictJob", want == AutoPredict ? "true" : "false");
                        Changed(Types, "Generate predictions", cur, want);
                    }
                }
            }
            foreach (var (label, attr) in Insights)
            {
                string v = Get(Types, label);
                if (v == null) continue;
                if (!IpmList.Bool(v, out bool on)) { problems.Add($"[{Types}] {label} must be true or false, not '{v}'."); continue; }
                if (on == Flag(r, attr)) continue;
                Changed(Types, label, Flag(r, attr) ? "true" : "false", on ? "true" : "false");
                r.SetAttributeValue(attr, on ? "true" : "false");
            }

            // ---- 2 Calendar ----
            string cal = Get(Calendar, CalendarKey);
            if (cal != null && !string.Equals(cal, (string)r.Attribute("calendarName"), StringComparison.OrdinalIgnoreCase))
            {
                IpmCalendar c = ctx.Calendar(cal);
                if (c == null) problems.Add($"[{Calendar}] '{cal}' is not a calendar in EPM (the pulled ones: {CalendarNames(ctx)}). A new calendar is deployed from Calendars.ini first.");
                else
                {
                    Changed(Calendar, CalendarKey, (string)r.Attribute("calendarName") ?? "", c.Name);
                    r.SetAttributeValue("calendarName", c.Name);
                    r.SetAttributeValue("calendarUuid", c.Id);
                }
            }
            XElement range = r.Element("APBatchDateRange");
            if (range != null)
            {
                foreach (var (key, attr) in new[] { ("Historical periods", "historicalPeriod"), ("Future periods", "futurePeriod") })
                {
                    string v = Get(Calendar, key);
                    if (v == null || v == ((string)range.Attribute(attr) ?? "")) continue;
                    if (!int.TryParse(v, out int n) || n < 0) { problems.Add($"[{Calendar}] {key} must be a whole number, not '{v}'."); continue; }
                    Changed(Calendar, key, (string)range.Attribute(attr) ?? "", n.ToString());
                    range.SetAttributeValue(attr, n.ToString());
                }
                foreach (var (key, el) in new[] { ("Start", "startDate"), ("End", "endDate") })
                {
                    string v = Get(Calendar, key);
                    XElement d = range.Element(el);
                    if (v == null || d == null || v.Replace(" ", "") == Period(d).Replace(" ", "")) continue;
                    if (!IpmList.SplitPeriod(v, out string year, out string period)) { problems.Add($"[{Calendar}] {key} must be year->period (e.g. FY26->Jan), not '{v}'."); continue; }
                    Changed(Calendar, key, Period(d), $"{year}->{period}");
                    d.SetAttributeValue("year", year);
                    d.SetAttributeValue("period", period);
                }
            }

            // ---- Model Scope and the other slices ----
            List<string> scope = ScopeDimensions(r);
            foreach (XElement s in ShownSlices(r))
            {
                var k = KeyOf(s).Value;
                if (IsScopeHeader(r, s)) continue;
                List<string> items = IpmList.Split(Members(s));
                List<string> after;
                if (PerDimension(s, k.Key, scope))
                {
                    after = new List<string>();
                    for (int i = 0; i < scope.Count; i++)
                    {
                        string v = Get(k.Section, scope[i]);
                        after.Add(v == null ? items[i] : string.Join(",", IpmList.Split(v)));
                    }
                }
                else
                {
                    string v = Get(k.Section, k.Key);
                    if (v == null) continue;
                    after = IpmList.Split(v);
                }
                if (after.Any(x => x.Length == 0) || after.Count == 0)
                {
                    problems.Add($"[{k.Section}] {(PerDimension(s, k.Key, scope) ? "the Model Scope lines" : k.Key)} can't be empty.");
                    continue;
                }
                string joined = string.Join(",", after);
                if (joined == Members(s)) continue;
                string what = PerDimension(s, k.Key, scope) ? "Model Scope" : k.Key;
                if (items.Count + after.Count <= 12) Changed(k.Section, what, IpmList.Join(items), IpmList.Join(after));
                else
                {
                    // A long list: only what was added and removed
                    var added = after.Except(items, StringComparer.Ordinal).ToList();
                    var removed = items.Except(after, StringComparer.Ordinal).ToList();
                    changes.Add($"[{k.Section}] {what}: " + string.Join("; ", new[]
                    {
                        added.Count > 0 ? "added " + IpmList.Join(added) : null,
                        removed.Count > 0 ? "removed " + IpmList.Join(removed) : null,
                        added.Count + removed.Count == 0 ? "order changed" : null,
                    }.Where(x => x != null)));
                }
                s.SetElementValue("MbrSelection", joined);
            }

            // ---- Prediction options (5 Prepare Data, 6 Model Settings) ----
            JObject options = Options(r);
            if (options != null)
            {
                bool any = false;
                foreach (JProperty p in options.Properties().ToList())
                {
                    var (section, key) = OptionKey(p.Name, multi);
                    string v = Get(section, key);
                    if (v == null) continue;
                    JToken parsed = IpmList.Parse(p.Value, v, out string problem);
                    if (problem != null) { problems.Add($"[{section}] {key} {problem}, not '{v}'."); continue; }
                    if (JToken.DeepEquals(parsed, p.Value)) continue;
                    Changed(section, key, IpmList.Show(p.Value), IpmList.Show(parsed));
                    p.Value = parsed;
                    any = true;
                }
                if (any) r.Element("APPredictionOptions").ReplaceNodes(new XCData(options.ToString(Formatting.None)));
            }

            // ---- Outputs and drivers ----
            var featureSections = ini.Keys.Where(k => k.IndexOf(Bar, StringComparison.Ordinal) >= 0).ToList();
            JObject mapping = multi ? Mapping(r) : null;
            if (mapping != null)
            {
                if (ApplyFeatures(mapping, ini, ctx, Get, used, problems, changes))
                    r.Element("MLModelMapping").ReplaceNodes(new XCData(mapping.ToString(Formatting.None)));
            }
            else if (featureSections.Count > 0)
                problems.Add($"[{featureSections[0]}]: outputs and drivers are only part of {Advanced}.");

            // ---- What is left: unknown sections and lines ----
            foreach (var sec in ini)
            {
                bool isFeature = sec.Key.IndexOf(Bar, StringComparison.Ordinal) >= 0;
                if (!isFeature && !Steps.Contains(sec.Key, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"[{sec.Key}] is not a section of an Auto Predict job (they are: {string.Join(", ", Steps)}, and the outputs and drivers).");
                    continue;
                }
                if (isFeature && mapping == null) continue;
                foreach (var kv in sec.Value)
                    if (!used.Contains(sec.Key + "\u0001" + kv.Key))
                        problems.Add($"[{sec.Key}] unknown line '{kv.Key}'.");
            }

            if (problems.Count > 0)
                throw new EpmException($"{fileName} can't be deployed:\n\n• " + string.Join("\n• ", problems.Distinct()));
            return new Applied { Xml = IpmList.Xml(doc), Changes = changes };
        }

        private static string CalendarNames(IpmContext ctx) =>
            ctx.Calendars.Count == 0 ? "none pulled" : string.Join(", ", ctx.Calendars.Select(c => c.Name));

        private sealed class FeatureSection
        {
            public string Section, Role, Name;
        }

        /// <summary>The [step | role | name] sections of one step, in file order.</summary>
        private static List<FeatureSection> SectionsOf(Dictionary<string, List<KeyValuePair<string, string>>> ini, string step, List<string> problems)
        {
            var list = new List<FeatureSection>();
            foreach (string key in ini.Keys)
            {
                int first = key.IndexOf(Bar, StringComparison.Ordinal);
                if (first < 0 || !string.Equals(key.Substring(0, first).Trim(), step, StringComparison.OrdinalIgnoreCase)) continue;
                int second = key.IndexOf(Bar, first + Bar.Length, StringComparison.Ordinal);
                string role = second < 0 ? "" : key.Substring(first + Bar.Length, second - first - Bar.Length).Trim();
                string name = second < 0 ? "" : key.Substring(second + Bar.Length).Trim();
                role = new[] { OutputRole, DriverRole }.FirstOrDefault(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));
                if (role == null || name.Length == 0)
                {
                    problems.Add($"[{key}] should be [{step}{Bar}{OutputRole}{Bar}name] or [{step}{Bar}{DriverRole}{Bar}name].");
                    continue;
                }
                if (list.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"[{key}]: '{name}' has more than one section in {step}.");
                    continue;
                }
                list.Add(new FeatureSection { Section = key, Role = role, Name = name });
            }
            return list;
        }

        /// <summary>Rebuilds both feature groups from the sections; true when anything changed.</summary>
        private static bool ApplyFeatures(JObject mapping, Dictionary<string, List<KeyValuePair<string, string>>> ini, IpmContext ctx,
                                          Func<string, string, string> get, HashSet<string> used, List<string> problems, List<string> changes)
        {
            JObject hist = mapping["features"] as JObject, fut = mapping["forecastFeatures"] as JObject;
            if (hist == null) return false;
            string before = mapping.ToString(Formatting.None);
            var histSecs = SectionsOf(ini, Historical, problems);
            var futSecs  = SectionsOf(ini, Future, problems);
            if (!histSecs.Any(s => s.Role == OutputRole))
            {
                problems.Add($"an output is needed: a [{Historical}{Bar}{OutputRole}{Bar}name] section.");
                return false;
            }
            foreach (FeatureSection f in futSecs)
            {
                FeatureSection h = histSecs.FirstOrDefault(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                if (h == null) problems.Add($"[{f.Section}] has no [{Historical}{Bar}{f.Role}{Bar}{f.Name}] section.");
                else if (h.Role != f.Role) problems.Add($"[{f.Section}]: '{f.Name}' is an {h.Role.ToLowerInvariant()} in {Historical}.");
            }

            List<JObject> oldHist = FeaturesOf(hist).ToList();
            List<JObject> oldFut  = FeaturesOf(fut).ToList();
            JObject Named(List<JObject> list, string n) =>
                list.FirstOrDefault(x => string.Equals((string)x["featureName"], n, StringComparison.OrdinalIgnoreCase));

            var newHist = new List<JObject>();
            var newFut  = new List<JObject>();
            foreach (FeatureSection s in histSecs)
            {
                JObject f;
                JObject old = Named(oldHist, s.Name);
                if (old != null) f = (JObject)old.DeepClone();
                else
                {
                    // A new output or driver: shaped like an existing one, with only the lines written
                    JObject like = oldHist.FirstOrDefault(x => Outputs(hist).Contains((string)x["featureName"]) == (s.Role == OutputRole))
                                   ?? oldHist.FirstOrDefault();
                    f = like != null ? (JObject)like.DeepClone() : new JObject();
                    f["label"] = "";
                    f["dimMappings"] = new JArray();
                    f["missingValues"] = "none";
                    f["outliers"] = "none";
                }
                f["featureName"] = s.Name;
                ApplyFeatureRows(f, s.Section, ini, ctx, get, problems);
                newHist.Add(f);

                FeatureSection fs = futSecs.FirstOrDefault(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                JObject oldF = Named(oldFut, s.Name);
                // Future data starts from its own pulled version, or from the historical one
                JObject g = oldF != null ? (JObject)oldF.DeepClone() : (JObject)f.DeepClone();
                g["featureName"] = s.Name;
                if (fs != null) ApplyFeatureRows(g, fs.Section, ini, ctx, get, problems);
                newFut.Add(g);
            }

            // 5 Prepare Data: per output and driver, on both copies
            if (ini.TryGetValue(Prepare, out var prepRows))
                foreach (var kv in prepRows)
                {
                    int bar = kv.Key.LastIndexOf(Bar, StringComparison.Ordinal);
                    if (bar < 0) continue;
                    string fname = kv.Key.Substring(0, bar).Trim(), what = kv.Key.Substring(bar + Bar.Length).Trim();
                    string prop = string.Equals(what, MissingKey, StringComparison.OrdinalIgnoreCase) ? "missingValues"
                                : string.Equals(what, OutliersKey, StringComparison.OrdinalIgnoreCase) ? "outliers" : null;
                    if (prop == null) continue;                                    // reported as an unknown line
                    used.Add(Prepare + "\u0001" + kv.Key);
                    JObject h = Named(newHist, fname);
                    if (h == null) { problems.Add($"[{Prepare}] {kv.Key}: '{fname}' is not an output or driver."); continue; }
                    string v = kv.Value.Trim();
                    if (v.Length == 0) { problems.Add($"[{Prepare}] {kv.Key} can't be empty."); continue; }
                    h[prop] = v;
                    JObject ff = Named(newFut, fname);
                    if (ff != null) ff[prop] = v;
                }

            JArray Names(IEnumerable<FeatureSection> secs, string role) =>
                new JArray(secs.Where(x => x.Role == role).Select(x => x.Name));
            void Fill(JObject group, List<JObject> features)
            {
                group["outputFeatures"] = Names(histSecs, OutputRole);
                group["inputFeatures"]  = Names(histSecs, DriverRole);
                group["feature"]        = new JArray(features);
            }
            List<JObject> previousHist = oldHist.Select(x => (JObject)x.DeepClone()).ToList();
            HashSet<string> previousOutputs = Outputs(hist);
            Fill(hist, newHist);
            if (fut != null) Fill(fut, newFut);
            else mapping["forecastFeatures"] = fut = Fill2(hist, newFut);

            if (mapping.ToString(Formatting.None) == before) return false;
            Describe(previousHist, previousOutputs, oldFut, newHist, newFut, histSecs, ctx, changes);
            return true;
        }

        private static JObject Fill2(JObject hist, List<JObject> features)
        {
            var g = (JObject)hist.DeepClone();
            g["feature"] = new JArray(features);
            return g;
        }

        /// <summary>Input type, calendar and the dimension lines of one [step | role | name] section.</summary>
        private static void ApplyFeatureRows(JObject f, string section, Dictionary<string, List<KeyValuePair<string, string>>> ini,
                                             IpmContext ctx, Func<string, string, string> get, List<string> problems)
        {
            string type = get(section, InputTypeKey);
            if (type != null && type.Length > 0) f["inputType"] = type;
            string cal = get(section, CalendarKey);
            if (cal != null && !string.Equals(cal, (string)f["frequency"], StringComparison.OrdinalIgnoreCase))
            {
                IpmCalendar c = ctx.Calendar(cal);
                if (c == null) problems.Add($"[{section}] Calendar: '{cal}' is not a calendar in EPM (the pulled ones: {CalendarNames(ctx)}).");
                else { f["frequency"] = c.Name; f["frequencyUuid"] = c.Id; }
            }

            var maps = (f["dimMappings"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            foreach (var kv in ini[section])
            {
                if (string.Equals(kv.Key, InputTypeKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, CalendarKey, StringComparison.OrdinalIgnoreCase)) continue;
                string dim = get(section, kv.Key);
                JObject dm = maps.FirstOrDefault(m => string.Equals((string)m["dimension"], kv.Key, StringComparison.OrdinalIgnoreCase));
                List<string> items = IpmList.Split(dim);
                if (items.Count == 0)
                {
                    if (dm != null) maps.Remove(dm);                            // an empty line removes the dimension
                    continue;
                }
                var ids = new JArray();
                // A member shown as it was keeps its ID (a shared member's positions can differ)
                var shownBefore = (dm?["members"] as JArray ?? new JArray()).Select(t => (string)t)
                    .GroupBy(ctx.Members.Show).ToDictionary(g => IpmList.Unquote(g.Key), g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (string item in items)
                {
                    if (shownBefore.TryGetValue(IpmList.Unquote(item), out string same)) { ids.Add(same); continue; }
                    string id = ctx.Members.Id((string)dm?["dimension"] ?? kv.Key, item, out string problem);
                    if (id == null) problems.Add($"[{section}] {kv.Key}: {problem}");
                    else ids.Add(id);
                }
                if (dm == null) maps.Add(dm = new JObject { ["dimension"] = kv.Key });
                dm["members"] = ids;
            }
            f["dimMappings"] = new JArray(maps);
        }

        /// <summary>What changed among the outputs and drivers, for the deploy confirmation.</summary>
        private static void Describe(List<JObject> oldHist, HashSet<string> oldOutputs, List<JObject> oldFut,
                                     List<JObject> newHist, List<JObject> newFut, List<FeatureSection> secs, IpmContext ctx,
                                     List<string> changes)
        {
            string Role(string n) => secs.First(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)).Role;
            JObject Named(List<JObject> list, string n) =>
                list.FirstOrDefault(x => string.Equals((string)x["featureName"], n, StringComparison.OrdinalIgnoreCase));

            foreach (JObject o in oldHist.Where(o => Named(newHist, (string)o["featureName"]) == null))
                changes.Add($"removed {(oldOutputs.Contains((string)o["featureName"]) ? "output" : "driver")} '{(string)o["featureName"]}'");
            foreach (JObject n in newHist.Where(n => Named(oldHist, (string)n["featureName"]) == null))
                changes.Add($"added {Role((string)n["featureName"]).ToLowerInvariant()} '{(string)n["featureName"]}'");
            var kept = newHist.Select(n => (string)n["featureName"]).Where(n => Named(oldHist, n) != null).ToList();
            if (!kept.SequenceEqual(oldHist.Select(o => (string)o["featureName"]).Where(n => kept.Contains(n, StringComparer.OrdinalIgnoreCase)),
                                    StringComparer.OrdinalIgnoreCase))
                changes.Add("the order of the outputs and drivers");

            foreach (var (step, before, after) in new[] { (Historical, oldHist, newHist), (Future, oldFut, newFut) })
                foreach (JObject n in after)
                {
                    string name = (string)n["featureName"];
                    JObject o = Named(before, name);
                    if (o == null) continue;
                    string label = $"[{step}{Bar}{Role(name)}{Bar}{name}]";
                    foreach (var (prop, key) in new[] { ("inputType", InputTypeKey), ("frequency", CalendarKey) })
                        if ((string)o[prop] != (string)n[prop])
                            changes.Add($"{label} {key}: {(string)o[prop]} → {(string)n[prop]}");
                    if (step == Historical)
                        foreach (var (prop, key) in new[] { ("missingValues", MissingKey), ("outliers", OutliersKey) })
                            if ((string)o[prop] != (string)n[prop])
                                changes.Add($"[{Prepare}] {name}{Bar}{key}: {(string)o[prop]} → {(string)n[prop]}");
                    var om = Dims(o); var nm = Dims(n);
                    foreach (string dim in om.Keys.Union(nm.Keys, StringComparer.OrdinalIgnoreCase))
                    {
                        om.TryGetValue(dim, out var a); nm.TryGetValue(dim, out var b);
                        if (a != null && b != null && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase)) continue;
                        string Show(List<string> ids) => ids == null ? "(none)" : IpmList.Join(ids.Select(ctx.Members.Show));
                        changes.Add($"{label} {dim}: {Show(a)} → {Show(b)}");
                    }
                }
        }

        private static Dictionary<string, List<string>> Dims(JObject f) =>
            (f["dimMappings"] as JArray ?? new JArray()).OfType<JObject>()
                .GroupBy(m => (string)m["dimension"] ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()["members"] is JArray a ? a.Select(t => (string)t).ToList() : new List<string>(),
                              StringComparer.OrdinalIgnoreCase);
    }
}
