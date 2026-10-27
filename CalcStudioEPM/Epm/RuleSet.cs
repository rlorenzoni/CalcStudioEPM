using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// RuleSets. In Calculation Manager a RuleSet only runs existing rules (and other RuleSets)
    /// in order; what is edited on the RuleSet itself is a few settings and its runtime-prompt
    /// variables. EPM's export of a RuleSet still embeds a full copy of every rule, template and
    /// variable it uses — copies that are out of date as soon as one of those rules changes.
    ///
    /// So Pull writes only the definition, as {RuleSet}.ruleset.ini next to the other scripts
    /// (Planning\{app}\RuleSets\), and Deploy puts the RuleSet together from the pulled
    /// definition, the .ini, and the deployed version of every rule it runs. Like EPM's export
    /// it also holds the rules those rules call (%Rule), which a rule's own file embeds. The ids
    /// that link the pieces are numbered per file, so they are renumbered on the way:
    ///   variable_reference/@id   → variables/variable  (in rules, templates and RuleSets)
    ///   property "rule" of a variable or variable_reference → rules/rule (rule-scoped prompts)
    ///   template_ref/@id         → templates/template  (graphical rules)
    ///   ruleset/step/@id         → rules/rule or rulesets/ruleset
    ///   deployobject/@obj_id     → rule (obj_type 1), RuleSet (2) or template (19)
    /// The result goes to Calculation Manager only.
    /// </summary>
    internal static class RuleSet
    {
        public const string IniSuffix  = ".ruleset.ini";
        public const string FolderName = "RuleSets";

        // [RuleSet] keys → properties of <ruleset>. Bookkeeping properties aren't shown; any
        // other property is shown under its own name.
        private static readonly Field[] RuleSetFields =
        {
            new Field("Description",     "description",   false),
            new Field("Merge variables", "mergeVars",     true),
            new Field("Run in parallel", "parallel",      true),
            new Field("Display label",   "display_label", false, onlyWhenSet: true),
        };
        private static readonly string[] RuleSetHidden = { "application", "seeded" };

        // [Variable] keys → properties of <variable_reference>
        private static readonly Field[] VariableFields =
        {
            new Field("Hidden",                "hidden",             true),
            new Field("Value",                 "value",              false),
            new Field("Use as override value", "useAsOverrideValue", true),
            new Field("Validation value",      "validation_value",   false),
        };
        private static readonly string[] VariableHidden =
            { "application", "plantype", "rule_name", "rule", "seq", "seq_index", "type", "hasvalue" };

        private sealed class Field
        {
            public readonly string Key, Prop;
            public readonly bool IsBool, OnlyWhenSet;
            public Field(string key, string prop, bool isBool, bool onlyWhenSet = false)
            { Key = key; Prop = prop; IsBool = isBool; OnlyWhenSet = onlyWhenSet; }
        }

        // ------------------------------------------------------------------ //
        //  Paths
        // ------------------------------------------------------------------ //

        public static bool IsRuleSetIni(string path) =>
            path != null && path.EndsWith(IniSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>The pulled RuleSet XML: …\resource\Planning\{app}\RuleSets\{name}.</summary>
        public static string PulledPath(string localFolder, string app, string name) =>
            Path.Combine(localFolder, PathConstants.CalcManagerDirName, "CALC-Calculation Manager", "resource",
                         PathConstants.PlanningDirName, app, FolderName, PathConstants.ToFileName(name));

        /// <summary>(application, RuleSet name) of …\{Connection}.Scripts\Planning\{app}\RuleSets\{name}.ruleset.ini.</summary>
        public static (string App, string Name) ParseIniPath(string path)
        {
            string dir = Path.GetDirectoryName(path ?? "") ?? "";
            string file = Path.GetFileName(path ?? "");
            if (!IsRuleSetIni(file) || !string.Equals(Path.GetFileName(dir), FolderName, StringComparison.OrdinalIgnoreCase))
                throw new EpmException($"'{file}' is not a RuleSet file (…\\Planning\\{{application}}\\RuleSets\\{{name}}{IniSuffix}).");
            string app = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
            string name = PathConstants.FromFileName(file.Substring(0, file.Length - IniSuffix.Length));
            return (app, name);
        }

        /// <summary>The &lt;ruleset&gt; element named <paramref name="name"/> in a RuleSet file.</summary>
        public static XElement Definition(XDocument doc, string name) =>
            doc.Root?.Element("rulesets")?.Elements("ruleset")
               .FirstOrDefault(r => string.Equals((string)r.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ //
        //  Pull: definition → .ini
        // ------------------------------------------------------------------ //

        public static string ToIni(XDocument doc, string name)
        {
            XElement rs = Definition(doc, name) ?? throw new EpmException($"the file has no RuleSet '{name}'.");
            string realName = (string)rs.Attribute("name");
            var lines = new List<string>
            {
                $"; {realName} — RuleSet · {Prop(rs, "application")}",
                "; A RuleSet runs existing rules: edit them in their own files. Here you change the RuleSet's",
                "; settings and its runtime prompts. Which rules it runs, and their order, change in",
                "; Calculation Manager.",
                "; Deploy (from this file) puts the RuleSet together from the deployed version of each rule it",
                "; runs and sends it to Calculation Manager only: deploy it to Planning from Calculation Manager.",
                ";",
                $"; Runs, in this order{(Prop(rs, "parallel") == "true" ? " (in parallel)" : "")}:",
            };
            foreach (XElement step in Steps(rs))
            {
                bool isSet = (string)step.Attribute("type") == "ruleset";
                string pt = Prop(step, "plantype");
                lines.Add($";  {(string)step.Attribute("seq"),3}  {(isSet ? "RuleSet" : "Rule"),-7}  {StepDisplayName(doc, step)}" +
                          (pt.Length > 0 ? $"  · {pt}" : ""));
            }
            lines.Add("");
            lines.Add("[RuleSet]");
            lines.AddRange(Rows(rs, RuleSetFields, RuleSetHidden));
            lines.Add("");

            var sections = VariableSections(rs);
            if (sections.Count > 0)
            {
                lines.Add("; Runtime prompts. Hidden, Use as override value: true or false. Value, Validation value: as");
                lines.Add("; Calculation Manager shows them (member names in \"quotes\"); empty = none.");
                lines.Add("");
            }
            foreach (var (section, vr) in sections)
            {
                lines.Add($"[{section}]");
                string pt = Prop(vr, "plantype");
                lines.Add($"; Scope: {(pt.Length > 0 ? "Cube " + pt : "Application")} · from rule {Prop(vr, "rule_name")}");
                lines.AddRange(Rows(vr, VariableFields, VariableHidden));
                lines.Add("");
            }
            return string.Join("\n", lines);
        }

        /// <summary>"Key = value" lines: the fields (padded), then any other shown property by its name.</summary>
        private static IEnumerable<string> Rows(XElement owner, Field[] fields, string[] hidden)
        {
            var rows = new List<KeyValuePair<string, string>>();
            foreach (Field f in fields)
            {
                if (f.OnlyWhenSet && PropElement(owner, f.Prop) == null) continue;
                rows.Add(new KeyValuePair<string, string>(f.Key, Effective(owner, f)));
            }
            foreach (XElement p in Props(owner))
            {
                string n = (string)p.Attribute("name");
                if (hidden.Contains(n) || fields.Any(f => f.Prop == n)) continue;
                rows.Add(new KeyValuePair<string, string>(n, p.Value));
            }
            int width = rows.Count == 0 ? 0 : rows.Max(r => r.Key.Length);
            return rows.Select(r => $"{r.Key.PadRight(width)} = {r.Value}".TrimEnd());
        }

        /// <summary>One [section] per variable: its name, or "name · rule" when the name repeats.</summary>
        private static List<(string Section, XElement Ref)> VariableSections(XElement rs)
        {
            var refs = rs.Element("variable_references")?.Elements("variable_reference").ToList() ?? new List<XElement>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RuleSet" };
            var result = new List<(string, XElement)>();
            foreach (XElement r in refs)
            {
                string name = (string)r.Attribute("name") ?? "";
                string section = refs.Count(x => string.Equals((string)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase)) > 1
                    ? $"{name} · {Prop(r, "rule_name")}" : name;
                string unique = section;
                for (int i = 2; !used.Add(unique); i++) unique = $"{section} #{i}";
                result.Add((unique, r));
            }
            return result;
        }

        // ------------------------------------------------------------------ //
        //  Deploy: .ini → definition
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Applies an edited .ini to a RuleSet definition. Returns what changed ("[Currency] Hidden:
        /// true → false"); throws <see cref="EpmException"/> listing every problem.
        /// </summary>
        public static List<string> ApplyIni(XElement rs, string iniText, string fileName)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> ini;
            try { ini = GraphicalRule.ParseIni(iniText); }
            catch (FormatException ex) { throw new EpmException($"{fileName}: {ex.Message}"); }

            var problems = new List<string>();
            var changes  = new List<string>();
            var sections = VariableSections(rs);
            var known = new HashSet<string>(sections.Select(s => s.Section), StringComparer.OrdinalIgnoreCase) { "RuleSet" };
            foreach (string extra in ini.Keys.Where(k => !known.Contains(k)))
                problems.Add($"{fileName}: [{extra}] is not a runtime prompt of this RuleSet (they come from its rules; change them in Calculation Manager).");

            if (ini.TryGetValue("RuleSet", out var setRows))
                ApplyRows(rs, setRows, RuleSetFields, RuleSetHidden, "RuleSet", changes, problems, fileName);
            else problems.Add($"{fileName}: section [RuleSet] is missing.");

            foreach (var (section, vr) in sections)
            {
                if (ini.TryGetValue(section, out var rows))
                    ApplyRows(vr, rows, VariableFields, VariableHidden, section, changes, problems, fileName);
                else problems.Add($"{fileName}: section [{section}] is missing (restore it, or pull again).");
            }

            if (problems.Count > 0) throw new EpmException(Problems(problems));
            return changes.Select(c => $"{fileName} {c}").ToList();
        }

        private static string Problems(IEnumerable<string> problems) =>
            "The RuleSet can't be deployed:\n\n• " + string.Join("\n• ", problems);

        private static void ApplyRows(XElement owner, List<KeyValuePair<string, string>> rows, Field[] fields, string[] hidden,
                                      string section, List<string> changes, List<string> problems, string fileName)
        {
            // Allowed: the fields, and the other properties the element already has
            var raw = Props(owner).Select(p => (string)p.Attribute("name"))
                                  .Where(n => !hidden.Contains(n) && fields.All(f => f.Prop != n)).ToList();
            string expected = string.Join(", ", fields.Where(f => !f.OnlyWhenSet || PropElement(owner, f.Prop) != null)
                                                      .Select(f => f.Key).Concat(raw));
            foreach (var g in rows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"{fileName} [{section}]: '{g.Key}' appears more than once.");

            foreach (var row in rows)
            {
                Field f = fields.FirstOrDefault(x => string.Equals(x.Key, row.Key, StringComparison.OrdinalIgnoreCase));
                if (f != null && f.OnlyWhenSet && PropElement(owner, f.Prop) == null) f = null;
                string prop = f?.Prop ?? raw.FirstOrDefault(n => string.Equals(n, row.Key, StringComparison.OrdinalIgnoreCase));
                if (prop == null) { problems.Add($"{fileName} [{section}]: unknown key '{row.Key}' (expected {expected})."); continue; }

                string value = row.Value.Trim();
                if (f != null && f.IsBool)
                {
                    if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    { problems.Add($"{fileName} [{section}]: {row.Key} must be true or false, not '{row.Value}'."); continue; }
                    value = value.ToLowerInvariant();
                }

                string before = f != null ? Effective(owner, f) : PropElement(owner, prop)?.Value ?? "";
                if (value == before) continue;
                SetProp(owner, prop, value, removeWhenEmpty: f != null && !f.IsBool);
                changes.Add($"[{section}] {f?.Key ?? prop}: {Show(before)} → {Show(value)}");
            }
        }

        private static string Show(string v) => v.Length == 0 ? "(none)" : v;

        // ------------------------------------------------------------------ //
        //  Deploy: putting the RuleSet together
        // ------------------------------------------------------------------ //

        public sealed class Assembled
        {
            public XDocument Doc;
            public string Xml;
            /// <summary>Changes made in the .ini files ("x.ruleset.ini [Currency] Hidden: true → false").</summary>
            public List<string> Changes = new List<string>();
            /// <summary>Things to know that don't stop the deploy.</summary>
            public List<string> Notes = new List<string>();
            /// <summary>The rules it holds, as "name (plan type)": the ones it runs, then those they call.</summary>
            public List<string> Rules = new List<string>();
            /// <summary>The RuleSets it includes, itself first.</summary>
            public List<string> RuleSets = new List<string>();
        }

        private sealed class Member
        {
            public string Name, PlanType, NewId;
            public XElement Rule;
            public XDocument Source;
        }

        private sealed class SetDef
        {
            public string Name, NewId;
            public XElement Def;
            public XDocument Source;
        }

        /// <summary>
        /// The RuleSet <paramref name="name"/> as it will be deployed: its definition and those of
        /// the RuleSets it runs (pulled XML + their .ini in <paramref name="iniFolder"/>), and the
        /// deployed version of every rule. Throws <see cref="EpmException"/> listing every problem.
        /// </summary>
        public static Assembled Assemble(string localFolder, string app, string name, string iniFolder)
        {
            var result   = new Assembled();
            var problems = new List<string>();
            var sets     = new List<SetDef>();
            var members  = new List<Member>();
            string appDir = Path.GetDirectoryName(iniFolder);          // …\Planning\{app}

            Member FindMember(string ruleName, string pt) => members.FirstOrDefault(m =>
                string.Equals(m.Name, ruleName, StringComparison.OrdinalIgnoreCase) &&
                (pt == null || string.Equals(m.PlanType, pt, StringComparison.OrdinalIgnoreCase)));

            // A rule: its own deployed file when it was pulled, else the copy embedded where it was
            // found; then the rules that file embeds (the ones it calls)
            void AddRule(string ruleName, string pt, XDocument embeddedIn, XElement embedded, string calledBy)
            {
                if (FindMember(ruleName, pt) != null) return;
                string path = PackageBuilder.GetDeployedRulePath(localFolder, app, pt, ruleName);
                XDocument src = null;
                XElement rule = null;
                if (File.Exists(path))
                {
                    src = XDocument.Load(path);
                    rule = RulesOf(src).FirstOrDefault(r => string.Equals((string)r.Attribute("name"), ruleName, StringComparison.OrdinalIgnoreCase));
                }
                if (rule == null) { src = embeddedIn; rule = embedded; }
                if (rule == null) { problems.Add($"Rule '{ruleName}' ({pt}) isn't in the last pull — pull again."); return; }

                var m = new Member { Name = (string)rule.Attribute("name"), PlanType = pt, Rule = rule, Source = src };
                members.Add(m);
                result.Rules.Add($"{m.Name} ({pt})" + (calledBy != null ? $" — called by {calledBy}" : ""));
                if (src != embeddedIn || embeddedIn == null) NoteUndeployed(appDir, m, path, result.Notes);

                foreach (XElement other in RulesOf(src).Where(r => r != rule))
                    AddRule((string)other.Attribute("name"), PlanTypeOf(other, pt), src, other, m.Name);
            }

            void AddSet(string setName)
            {
                if (sets.Any(s => string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase))) return;   // repeats and loops
                string pulled = PulledPath(localFolder, app, setName);
                if (!File.Exists(pulled)) { problems.Add($"RuleSet '{setName}' isn't in the last pull — pull again."); return; }
                XDocument src = XDocument.Load(pulled);
                XElement def = Definition(src, setName);
                if (def == null) { problems.Add($"RuleSet '{setName}': its pulled file has no definition — pull again."); return; }
                var set = new SetDef { Name = (string)def.Attribute("name"), Def = new XElement(def), Source = src };
                sets.Add(set);
                result.RuleSets.Add(set.Name);

                string ini = Path.Combine(iniFolder, PathConstants.ToFileName(set.Name) + IniSuffix);
                if (File.Exists(ini))
                {
                    try { result.Changes.AddRange(ApplyIni(set.Def, File.ReadAllText(ini, Encoding.UTF8), Path.GetFileName(ini))); }
                    catch (EpmException ex) { problems.Add(ex.Message.Replace(Problems(new string[0]), "").Replace("\n• ", "\n  ")); }
                }
                else result.Notes.Add($"{Path.GetFileName(ini)} wasn't found: '{set.Name}' keeps the settings of the last pull.");

                foreach (XElement step in Steps(set.Def))
                {
                    string stepName = (string)step.Attribute("name") ?? "";
                    if ((string)step.Attribute("type") == "ruleset") { AddSet(stepName); continue; }

                    string stepApp = Prop(step, "application");
                    if (stepApp.Length > 0 && !string.Equals(stepApp, app, StringComparison.OrdinalIgnoreCase))
                    { problems.Add($"'{set.Name}' runs '{stepName}' of application {stepApp}; only rules of {app} can be put together here."); continue; }
                    AddRule(stepName, Prop(step, "plantype"), null, null, null);
                }
            }

            AddSet(name);
            if (problems.Count > 0) throw new EpmException(Problems(problems));

            // ---- New ids: rules and RuleSets first (variables can point at rules) ----
            for (int i = 0; i < members.Count; i++) members[i].NewId = (i + 1).ToString();
            for (int i = 0; i < sets.Count; i++) sets[i].NewId = (i + 1).ToString();

            // The new id of a rule a file refers to by its own id
            string NewRuleId(XDocument src, string oldId)
            {
                XElement r = RulesOf(src).FirstOrDefault(x => (string)x.Attribute("id") == oldId);
                Member m = r == null ? null : FindMember((string)r.Attribute("name"), PlanTypeOf(r, null)) ?? FindMember((string)r.Attribute("name"), null);
                return m?.NewId ?? oldId;
            }

            // ---- Variables: every member file's, then those the definitions refer to ----
            var variables = new List<XElement>();
            var idByKey   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var maps      = new Dictionary<XDocument, Dictionary<string, string>>();

            string NewVarId(XDocument src, string oldId)
            {
                if (!maps.TryGetValue(src, out var map)) maps[src] = map = new Dictionary<string, string>();
                if (map.TryGetValue(oldId ?? "", out string id)) return id;
                XElement v = src.Root?.Element("variables")?.Elements().FirstOrDefault(x => (string)x.Attribute("id") == oldId);
                if (v == null) return oldId;                 // dangling in EPM's own file too: leave as is
                string key = VariableKey(v, RuleNamer(src));
                if (!idByKey.TryGetValue(key, out id))
                {
                    id = (variables.Count + 1).ToString();
                    var copy = new XElement(v);
                    copy.SetAttributeValue("id", id);
                    RemapRuleProperty(copy, src);
                    variables.Add(copy);
                    idByKey[key] = id;
                }
                return map[oldId] = id;
            }
            void RemapRuleProperty(XElement e, XDocument src)
            {
                XElement p = PropElement(e, "rule");
                if (p != null) p.Value = NewRuleId(src, p.Value);
            }
            void RemapVariables(XElement el, XDocument src)
            {
                foreach (XElement vr in el.Descendants("variable_reference"))
                {
                    vr.SetAttributeValue("id", NewVarId(src, (string)vr.Attribute("id")));
                    RemapRuleProperty(vr, src);
                }
            }

            foreach (Member m in members)                   // templates use variables by name: keep them all
                foreach (XElement v in m.Source.Root?.Element("variables")?.Elements() ?? Enumerable.Empty<XElement>())
                    NewVarId(m.Source, (string)v.Attribute("id"));

            // ---- Templates, rules and their deploy objects, in run order ----
            var rules = new List<XElement>();
            var templates = new List<XElement>();
            var deployObjects = new List<XElement>();
            var templateId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deployKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddDeployObject(XElement d, string objId)
            {
                if (d == null || !deployKeys.Add($"{(string)d.Attribute("obj_type")}|{(string)d.Attribute("name")}|{(string)d.Attribute("plantype")}")) return;
                var copy = new XElement(d);
                copy.SetAttributeValue("obj_id", objId);
                deployObjects.Add(copy);
            }

            foreach (Member m in members)
                foreach (XElement t in m.Source.Root?.Element("templates")?.Elements("template") ?? Enumerable.Empty<XElement>())
                {
                    string tName = (string)t.Attribute("name") ?? "";
                    if (templateId.ContainsKey(tName)) continue;
                    string tid = templateId[tName] = (templates.Count + 1).ToString();
                    var copy = new XElement(t);
                    copy.SetAttributeValue("id", tid);
                    templates.Add((copy));
                    RemapVariables(copy, m.Source);
                }
            void RemapTemplates(XElement el)
            {
                foreach (XElement tr in el.Descendants("template_ref"))
                    if (templateId.TryGetValue((string)tr.Attribute("name") ?? "", out string tid)) tr.SetAttributeValue("id", tid);
            }
            foreach (XElement t in templates) RemapTemplates(t);

            foreach (Member m in members)
            {
                foreach (XElement t in m.Source.Root?.Element("templates")?.Elements("template") ?? Enumerable.Empty<XElement>())
                    AddDeployObject(DeployObject(m.Source, "19", (string)t.Attribute("name")), templateId[(string)t.Attribute("name") ?? ""]);
                var rule = new XElement(m.Rule);
                rule.SetAttributeValue("id", m.NewId);
                RemapVariables(rule, m.Source);
                RemapTemplates(rule);
                rules.Add(rule);
                AddDeployObject(DeployObject(m.Source, "1", m.Name), m.NewId);
            }

            // ---- RuleSet definitions; steps point at the new ids ----
            var definitions = new List<XElement>();
            foreach (SetDef s in sets)
            {
                var def = new XElement(s.Def);
                def.SetAttributeValue("id", s.NewId);
                RemapVariables(def, s.Source);
                foreach (XElement step in def.Elements("step"))
                {
                    string stepName = (string)step.Attribute("name") ?? "";
                    string id = (string)step.Attribute("type") == "ruleset"
                        ? sets.First(x => string.Equals(x.Name, stepName, StringComparison.OrdinalIgnoreCase)).NewId
                        : FindMember(stepName, Prop(step, "plantype")).NewId;
                    step.SetAttributeValue("id", id);
                }
                definitions.Add(def);
                // Only when the pulled file had one: EPM leaves it out for some RuleSets
                AddDeployObject(DeployObject(s.Source, "2", s.Name), s.NewId);
            }

            // Sections as EPM writes them; rules, RuleSets and templates sorted by name like its exports
            XElement Sorted(string section, IEnumerable<XElement> items) =>
                new XElement(section, items.OrderBy(e => (string)e.Attribute("name"), StringComparer.OrdinalIgnoreCase));
            var doc = new XDocument(new XElement("HBRRepo",
                new XElement("variables", variables),
                Sorted("rulesets", definitions),
                Sorted("rules", rules),
                new XElement("components"),
                new XElement("deployobjects", deployObjects),
                Sorted("templates", templates)));

            result.Doc = doc;
            result.Xml = RuleXmlWriter.Calc(doc);
            return result;
        }

        /// <summary>A rule with saved changes that aren't deployed: the RuleSet gets its deployed version.</summary>
        private static void NoteUndeployed(string appDir, Member m, string deployedPath, List<string> notes)
        {
            try
            {
                if (appDir == null || !File.Exists(deployedPath)) return;
                string rulesDir = Path.Combine(appDir, m.PlanType, "Rules");
                string file = new[] { ".csc", ".js" }.Select(ext => Path.Combine(rulesDir, PathConstants.ToFileName(m.Name) + ext))
                                                     .FirstOrDefault(File.Exists);
                if (file == null) return;                    // graphical rule (a folder), or not pulled as a script
                if (PackageBuilder.HasUndeployedChanges(File.ReadAllText(file, Encoding.UTF8), File.ReadAllText(deployedPath, Encoding.UTF8)))
                    notes.Add($"'{m.Name}' has saved changes that aren't deployed: the RuleSet runs its deployed version.");
            }
            catch { /* only a note */ }
        }

        private static IEnumerable<XElement> RulesOf(XDocument doc) =>
            doc?.Root?.Element("rules")?.Elements("rule") ?? Enumerable.Empty<XElement>();

        private static string PlanTypeOf(XElement rule, string fallback)
        {
            string pt = Prop(rule, "plantype");
            return pt.Length > 0 ? pt : fallback;
        }

        private static XElement DeployObject(XDocument src, string objType, string name) =>
            src.Root?.Element("deployobjects")?.Elements("deployobject").FirstOrDefault(d =>
                (string)d.Attribute("obj_type") == objType &&
                string.Equals((string)d.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ //
        //  Comparing two RuleSet files by name (ids are per file)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// What differs between two RuleSet files once their ids are replaced by the names they
        /// point at: "rule 'X': changed", "template 'Y': only in the second". Empty = the same.
        /// </summary>
        public static List<string> Differences(XDocument a, XDocument b)
        {
            var ca = Canonical(a);
            var cb = Canonical(b);
            var diffs = new List<string>();
            foreach (string key in ca.Keys.Union(cb.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                bool inA = ca.TryGetValue(key, out string va), inB = cb.TryGetValue(key, out string vb);
                if (!inA) diffs.Add($"{key}: only in the second");
                else if (!inB) diffs.Add($"{key}: only in the first");
                else if (va != vb) diffs.Add($"{key}: changed");
            }
            return diffs;
        }

        private static Dictionary<string, string> Canonical(XDocument doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            XElement root = doc.Root;
            if (root == null) return result;

            Func<string, string> ruleName = RuleNamer(doc);
            var varKey = (root.Element("variables")?.Elements() ?? Enumerable.Empty<XElement>())
                .GroupBy(v => (string)v.Attribute("id") ?? "")
                .ToDictionary(g => g.Key, g => VariableKey(g.First(), ruleName));

            string Clean(XElement e, bool steps)
            {
                var c = new XElement(e);
                c.Attribute("id")?.Remove();
                foreach (XElement vr in c.DescendantsAndSelf().Where(x => x.Name == "variable_reference" || x.Name == "variable"))
                {
                    if (vr.Name == "variable_reference")
                    {
                        string id = (string)vr.Attribute("id") ?? "";
                        vr.SetAttributeValue("id", varKey.TryGetValue(id, out string k) ? k : "?" + id);
                    }
                    XElement rp = PropElement(vr, "rule");
                    if (rp != null) rp.Value = ruleName(rp.Value);
                }
                foreach (XElement tr in c.Descendants("template_ref")) tr.Attribute("id")?.Remove();
                if (steps)
                    foreach (XElement s in c.Elements("step"))
                    {
                        s.Attribute("id")?.Remove();
                        s.SetAttributeValue("name", ((string)s.Attribute("name") ?? "").ToUpperInvariant());
                    }
                return c.ToString(SaveOptions.DisableFormatting);
            }

            foreach (XElement v in root.Element("variables")?.Elements() ?? Enumerable.Empty<XElement>())
                result[$"variable '{VariableKey(v, ruleName)}'"] = Clean(v, false);
            foreach (XElement r in root.Element("rules")?.Elements("rule") ?? Enumerable.Empty<XElement>())
                result[$"rule '{(string)r.Attribute("name")}'"] = Clean(r, false);
            foreach (XElement t in root.Element("templates")?.Elements("template") ?? Enumerable.Empty<XElement>())
                result[$"template '{(string)t.Attribute("name")}'"] = Clean(t, false);
            foreach (XElement s in root.Element("rulesets")?.Elements("ruleset") ?? Enumerable.Empty<XElement>())
                result[$"RuleSet '{(string)s.Attribute("name")}'"] = Clean(s, true);
            foreach (XElement d in root.Element("deployobjects")?.Elements("deployobject") ?? Enumerable.Empty<XElement>())
            {
                var c = new XElement(d);
                c.Attribute("obj_id")?.Remove();
                result[$"deploy object {(string)d.Attribute("obj_type")} '{(string)d.Attribute("name")}' {(string)d.Attribute("plantype")}"] =
                    c.ToString(SaveOptions.DisableFormatting);
            }
            return result;
        }

        // ------------------------------------------------------------------ //
        //  Helpers
        // ------------------------------------------------------------------ //

        private static IEnumerable<XElement> Steps(XElement rs) =>
            rs.Elements("step").OrderBy(s => int.TryParse((string)s.Attribute("seq"), out int n) ? n : int.MaxValue);

        /// <summary>A step's name as its rule or RuleSet spells it (steps hold it in capitals).</summary>
        private static string StepDisplayName(XDocument doc, XElement step)
        {
            string name = (string)step.Attribute("name") ?? "";
            string section = (string)step.Attribute("type") == "ruleset" ? "rulesets" : "rules";
            return doc.Root?.Element(section)?.Elements()
                       .Select(e => (string)e.Attribute("name"))
                       .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        }

        /// <summary>A file's rule id → that rule's name (ids are per file).</summary>
        private static Func<string, string> RuleNamer(XDocument doc)
        {
            var names = RulesOf(doc).GroupBy(r => (string)r.Attribute("id") ?? "")
                                    .ToDictionary(g => g.Key, g => (string)g.First().Attribute("name"));
            return id => names.TryGetValue(id ?? "", out string n) ? n : "#" + id;
        }

        /// <summary>A variable's identity across files: name, application, cube, scope and the
        /// rule it belongs to (rule-scoped prompts exist once per rule).</summary>
        private static string VariableKey(XElement v, Func<string, string> ruleName)
        {
            XElement rule = PropElement(v, "rule");
            return $"{(string)v.Attribute("name")}|{Prop(v, "application")}|{Prop(v, "plantype")}|{Prop(v, "scope")}|" +
                   (rule == null ? "" : ruleName(rule.Value));
        }

        private static IEnumerable<XElement> Props(XElement e) => e.Elements("property");

        private static XElement PropElement(XElement e, string name) =>
            Props(e).FirstOrDefault(p => (string)p.Attribute("name") == name);

        private static string Prop(XElement e, string name) => PropElement(e, name)?.Value ?? "";

        /// <summary>The value the .ini shows: the property's, or false / empty when it's absent.</summary>
        private static string Effective(XElement owner, Field f)
        {
            XElement p = PropElement(owner, f.Prop);
            return p != null ? p.Value : f.IsBool ? "false" : "";
        }

        /// <summary>
        /// Sets a property, adding it in name order (as EPM writes them) after the other properties;
        /// an empty text value removes it.
        /// </summary>
        private static void SetProp(XElement owner, string name, string value, bool removeWhenEmpty)
        {
            XElement p = PropElement(owner, name);
            if (removeWhenEmpty && value.Length == 0) { p?.Remove(); return; }
            if (p != null) { p.Value = value; return; }

            p = new XElement("property", new XAttribute("name", name), value);
            XElement after = Props(owner).LastOrDefault(x => string.CompareOrdinal((string)x.Attribute("name"), name) < 0);
            if (after != null) after.AddAfterSelf(p);
            else if (Props(owner).FirstOrDefault() is XElement first) first.AddBeforeSelf(p);
            else owner.AddFirst(p);
        }
    }
}
