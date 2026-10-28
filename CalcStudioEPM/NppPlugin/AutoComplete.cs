using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CalcStudioEPM;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace NppPluginNET
{
    /// <summary>
    /// Autocomplete lists (Scintilla's own list box) for CalcStudio scripts.
    ///
    /// Calc Script (.csc):
    ///   @        → functions from calcscript_catalog.json; picking one shows its syntax
    ///   SET␠     → SET options
    ///   {        → runtime prompts: those this script uses, a separator, then the rest of
    ///              the connection's variables; the closing } is added
    ///
    /// Groovy (.groovy, or .js in a connection's scripts folder):
    ///   .        → members of the receiver's EPM type first (operation.application.getCube(…)
    ///              → Cube), a separator, then every other EPM method; picking one shows its
    ///              signatures. rtps. lists the rule's runtime prompts.
    ///
    /// Types come from the embedded EPM Groovy API (<see cref="GroovyApi"/>) and simple
    /// inference: the script bindings (operation, rule), typed declarations (Cube c = …),
    /// def x = &lt;expression&gt;, and closure parameters of each/find/collect… over grid
    /// iterators and lists.
    ///
    /// Notifications arrive raw from beNotified; fields are read at their native x64
    /// SCNotification offsets.
    /// </summary>
    public static class AutoComplete
    {
        private const string Separator = "──────────────────";   // no spaces: Scintilla splits on them

        private enum ListKind { None, CalcFunctions, CalcSetOptions, CalcVariables, Members, FixTemplate, GroovyMembers, GroovyRtps }

        private static ListKind _kind;
        private static string   _groovyType;            // type whose members the open list shows
        private static string   _selected;
        private static int      _prevIgnoreCase, _prevOrder;

        // ------------------------------------------------------------------ //
        //  Notification entry point (from Plugin.BeNotified)
        // ------------------------------------------------------------------ //

        public const uint SCN_CHARADDED      = 2001;
        public const uint SCN_AUTOCSELECTION = 2022;
        public const uint SCN_AUTOCCANCELLED = 2025;
        public const uint SCN_AUTOCCOMPLETED = 2030;

        public static void OnScintillaNotification(uint code, IntPtr scn)
        {
            if (code != SCN_CHARADDED && code != SCN_AUTOCSELECTION &&
                code != SCN_AUTOCCANCELLED && code != SCN_AUTOCCOMPLETED) return;
            try
            {
                IntPtr sci = Marshal.ReadIntPtr(scn, 0);   // NMHDR.hwndFrom
                if (sci != PluginBase.nppData._scintillaMainHandle && sci != PluginBase.nppData._scintillaSecondHandle)
                    return;

                switch (code)
                {
                    case SCN_CHARADDED:
                        OnChar(sci, Marshal.ReadInt32(scn, 32));                        // ch
                        break;
                    case SCN_AUTOCSELECTION:
                        OnSelection(sci, Marshal.ReadIntPtr(scn, 48));                  // text
                        break;
                    case SCN_AUTOCCOMPLETED:
                        OnCompleted(sci, (int)Marshal.ReadIntPtr(scn, 24).ToInt64());   // position
                        break;
                    case SCN_AUTOCCANCELLED:
                        Close(sci);
                        break;
                }
            }
            catch { /* never let an exception escape into Notepad++ */ }
        }

        // ------------------------------------------------------------------ //
        //  Triggers
        // ------------------------------------------------------------------ //

        private enum Script { None, CalcScript, Groovy }

        private static void OnChar(IntPtr sci, int ch)
        {
            bool trigger = ch == '@' || ch == ' ' || ch == '{' || ch == '.' || ch == '(' || ch == ',' || ch == ')';
            bool memberChar = ch >= 0x20 && ch < 0x10000 && ch != '"';   // may be typing a "member name" or alias
            if (!trigger && !memberChar) return;
            Script script = CurrentScript(out string path);
            if (script == Script.None)
            {
                if (memberChar && IpmFiles.KindOf(path, out _) == IpmFiles.Kind.AutoPredict) IpmCompletion(sci, path);
                return;
            }
            if (script == Script.CalcScript && !PluginSettings.Current.AutoCompleteCalcScript) return;
            if (script == Script.Groovy && !PluginSettings.Current.AutoCompleteGroovy) return;

            // Typing a "member name" (also inside a function's brackets): the open list is rebuilt for
            // the longer prefix, since it also matches aliases (which Scintilla cannot narrow by)
            if (memberChar && _kind == ListKind.Members &&
                ScintillaText.Send(sci, SCI_AUTOCACTIVE) != 0)
                ScintillaText.Send(sci, SCI_AUTOCCANCEL);

            int pos = ScintillaText.Send(sci, SCI_GETCURRENTPOS);
            int from = Math.Max(0, pos - 40000);
            string before = ScintillaText.GetRange(sci, from, pos);   // includes the typed character

            // A graphical rule's 00 - Rule.ini: only the value after "Name =" is Calc Script, and
            // its members come from the line's dimension
            _dimension = null;
            if (IsRuleIni(path))
            {
                if (!RuleIniValue(before, out string value, out _dimension)) return;
                before = value;
                if (memberChar && BareMemberCompletion(sci, path, value)) return;
            }

            if (memberChar && MemberCompletion(sci, path, before, script)) return;
            if (!trigger) return;
            if (InCommentOrString(before.Substring(0, before.Length - 1), script)) return;

            if (ch == '(' && script == Script.CalcScript && !IsRuleIni(path) && FixTemplateOffer(sci, path, before, pos))
                return;
            if (ch == '(' || ch == ',' || ch == ')')
            {
                ParameterHint(sci, script, before, pos);
                return;
            }

            if (script == Script.CalcScript)
            {
                if (ch == '@')
                    Show(sci, ListKind.CalcFunctions, 1,
                         CalcScriptCatalog.Load().Functions.Select(f => f.Name.ToUpperInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal));
                else if (ch == ' ' && Regex.IsMatch(before, @"(^|[;\s])SET $", RegexOptions.IgnoreCase))
                    Show(sci, ListKind.CalcSetOptions, 0,
                         CalcScriptCatalog.Load().SetOptions.Select(o => o.ToUpperInvariant()).Distinct().OrderBy(o => o, StringComparer.Ordinal));
                else if (ch == '{')
                    ShowVariables(sci, path, Regex.Matches(FullText(sci), @"\{\s*(\w+)\s*\}").Cast<Match>().Select(m => m.Groups[1].Value),
                                  ListKind.CalcVariables);
            }
            else if (ch == '.')
            {
                GroovyDot(sci, path, before.Substring(0, before.Length - 1));
            }
        }

        private static void GroovyDot(IntPtr sci, string path, string before)
        {
            List<GroovyTypeInference.Segment> chain = GroovyTypeInference.ParseChain(before);
            if (chain.Count == 0) return;                                   // e.g. after a number or a string

            if (chain.Count == 1 && chain[0].Name == "rtps" && !chain[0].Call)
            {
                string text = FullText(sci);
                var used = Regex.Matches(text, @"/\*\s*RTPS:([^*]*)\*/").Cast<Match>()
                                .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"\{(\w+)\}").Cast<Match>().Select(x => x.Groups[1].Value))
                                .Concat(Regex.Matches(text, @"\brtps\.(\w+)").Cast<Match>().Select(m => m.Groups[1].Value));
                ShowVariables(sci, path, used, ListKind.GroovyRtps);
                return;
            }

            GroovyApi api = GroovyApi.Instance;
            string typeName = GroovyTypeInference.ResolveChain(chain, before, api, 0);
            GroovyApi.TypeInfo type = api.Find(typeName);

            var first = new List<string>();
            if (type != null)
            {
                bool staticAccess = chain.Count == 1 && !chain[0].Call && api.Find(chain[0].Name) == type &&
                                    !GroovyTypeInference.IsLocalName(chain[0].Name, before);
                List<GroovyApi.Method> methods = api.AllMethods(type).ToList();
                IEnumerable<string> own = staticAccess
                    ? type.Constants.Concat(methods.Where(m => m.Static).Select(m => m.Name))
                    : methods.Where(m => !m.Static).Select(GroovyApi.PropertyOf).Where(p => p != null)
                             .Concat(methods.Where(m => !m.Static).Select(m => m.Name));
                first = own.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            var rest = api.AllMethodNames().Except(first, StringComparer.Ordinal)
                          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            _groovyType = type?.Name;
            Show(sci, ListKind.GroovyMembers, 0, first.Count > 0 ? first.Concat(new[] { Separator }).Concat(rest) : rest);
        }

        /// <summary>Variables this script uses (sorted), a separator, then the connection's others.</summary>
        private static void ShowVariables(IntPtr sci, string path, IEnumerable<string> usedNames, ListKind kind)
        {
            var used = usedNames.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var others = new List<string>();
            ConnectionConfig cfg = ConnectionFor(path);
            if (cfg != null)
            {
                try
                {
                    others = VariableDictionary.LoadOrRebuild(cfg.LocalFolderPath).Variables
                        .Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n) && !n.Contains(" "))
                        .Except(used, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch { }
            }
            if (used.Count + others.Count == 0) return;
            IEnumerable<string> items = used.Count > 0 && others.Count > 0
                ? used.Concat(new[] { Separator }).Concat(others)
                : used.Concat(others);
            Show(sci, kind, 0, items);
        }

        // ------------------------------------------------------------------ //
        //  The list
        // ------------------------------------------------------------------ //

        /// <param name="separator">Between list items: ' ' (Scintilla's default), or '\n' for
        /// items that contain spaces (member names).</param>
        private static void Show(IntPtr sci, ListKind kind, int lenEntered, IEnumerable<string> items, char separator = ' ')
        {
            string list = string.Join(separator.ToString(),
                items.Where(i => !string.IsNullOrEmpty(i) && i.IndexOf(separator) < 0));
            if (list.Length == 0) return;

            _prevIgnoreCase = ScintillaText.Send(sci, SCI_AUTOCGETIGNORECASE);
            _prevOrder      = ScintillaText.Send(sci, SCI_AUTOCGETORDER);
            ScintillaText.Send(sci, SCI_AUTOCSETSEPARATOR, separator);
            ScintillaText.Send(sci, SCI_AUTOCSETIGNORECASE, 1);
            ScintillaText.Send(sci, SCI_AUTOCSETCASEINSENSITIVEBEHAVIOUR, 1);   // ignore case when picking
            ScintillaText.Send(sci, SCI_AUTOCSETORDER, 2);                     // custom: keep our groups
            ScintillaText.Send(sci, SCI_AUTOCSETAUTOHIDE, 1);
            ScintillaText.Send(sci, SCI_AUTOCSETMAXHEIGHT, 12);
            ScintillaText.Send(sci, SCI_CALLTIPCANCEL);

            _kind = kind;
            _selected = null;
            ScintillaText.SendString(sci, SCI_AUTOCSHOW, lenEntered, list);
        }

        private static void OnSelection(IntPtr sci, IntPtr text)
        {
            if (_kind == ListKind.None) return;
            _selected = ScintillaText.FromPointer(text, ScintillaText.EncodingOf(sci));
            if (_selected == Separator)
            {
                ScintillaText.Send(sci, SCI_AUTOCCANCEL);   // the separator is not a value
                Close(sci);
            }
        }

        private static void OnCompleted(IntPtr sci, int wordStart)
        {
            ListKind kind = _kind;
            string picked = _selected, groovyType = _groovyType;
            Close(sci);
            if (string.IsNullOrEmpty(picked)) return;

            int pos = ScintillaText.Send(sci, SCI_GETCURRENTPOS);
            switch (kind)
            {
                case ListKind.CalcFunctions:
                {
                    CalcFunction f = CalcScriptCatalog.Load().Functions
                        .FirstOrDefault(x => string.Equals(x.Name, picked, StringComparison.OrdinalIgnoreCase));
                    if (f != null && !string.IsNullOrWhiteSpace(f.Syntax))
                        ShowTip(sci, wordStart, f.Syntax, -1, -1);
                    break;
                }
                case ListKind.Members:
                {
                    // What was typed (a name or an alias) and the inserted "Member - Alias" line become
                    // the member name; then close it: step over an existing quote, or add one
                    if (!_memberPicks.TryGetValue(picked, out string member) || _memberStart < 0 || _memberStart > pos) break;
                    member = _memberOpen + member;                    // bare 00 - Rule.ini value: quote it
                    ScintillaText.Replace(sci, _memberStart, pos, member);
                    int end = _memberStart + ScintillaText.EncodingOf(sci).GetByteCount(member);
                    int closeLen = ScintillaText.EncodingOf(sci).GetByteCount(_memberClose);
                    int docEnd = ScintillaText.Send(sci, SCI_GETLENGTH);
                    if (ScintillaText.GetRange(sci, end, Math.Min(end + closeLen, docEnd)) != _memberClose)
                        ScintillaText.Replace(sci, end, end, _memberClose);
                    ScintillaText.Send(sci, SCI_GOTOPOS, end + closeLen);
                    break;
                }
                case ListKind.FixTemplate:
                {
                    if (_fixDimensions == null || wordStart < 0 || wordStart > pos) break;
                    // A ")" right after (Notepad++'s auto-close) becomes the FIX's closing bracket
                    bool hasClose = (char)ScintillaText.Send(sci, SCI_GETCHARAT, pos) == ')';
                    string text = FixTemplate(_fixDimensions, _fixIndent, !hasClose, out int caretChars);
                    ScintillaText.Replace(sci, wordStart, pos, text);
                    ScintillaText.Send(sci, SCI_GOTOPOS, wordStart + ScintillaText.EncodingOf(sci).GetByteCount(text.Substring(0, caretChars)));
                    break;
                }
                case ListKind.CalcVariables:
                    if ((char)ScintillaText.Send(sci, SCI_GETCHARAT, pos) != '}')
                    {
                        ScintillaText.Replace(sci, pos, pos, "}");
                        ScintillaText.Send(sci, SCI_GOTOPOS, pos + 1);
                    }
                    break;
                case ListKind.GroovyMembers:
                {
                    GroovyApi api = GroovyApi.Instance;
                    GroovyApi.TypeInfo t = api.Find(groovyType);
                    IEnumerable<GroovyApi.Method> pool = t != null ? api.AllMethods(t) : api.Types.Values.SelectMany(x => x.Methods);
                    var sigs = pool.Where(m => m.Name == picked || GroovyApi.PropertyOf(m) == picked)
                                   .Select(m => m.ToString()).Distinct().Take(8).ToList();
                    if (sigs.Count == 0)   // picked from "other options": say which types have it
                        sigs = api.Types.Values
                                  .SelectMany(x => x.Methods.Where(m => m.Name == picked).Select(m => $"{x.Name}: {m}"))
                                  .Distinct().Take(8).ToList();
                    if (sigs.Count == 0 && t != null && t.Constants.Contains(picked)) sigs.Add($"{t.Name}.{picked}");
                    if (sigs.Count > 0)
                        ShowTip(sci, wordStart, string.Join("\n", sigs), -1, -1);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Parameter hints: the function's syntax while its arguments are typed
        // ------------------------------------------------------------------ //

        private static bool   _hintActive;
        private static string _hintText;
        private static int    _hintPos;

        /// <summary>
        /// Called on ( , and ): finds the call the caret is inside, shows its syntax (Essbase
        /// function, or the overloads of a Groovy method) and highlights the parameter being typed.
        /// </summary>
        private static void ParameterHint(IntPtr sci, Script script, string before, int caret)
        {
            int open = InnermostOpenParen(before, script, out int arg);
            if (open < 0) { CancelHint(sci); return; }

            string head = before.Substring(0, open).TrimEnd();
            List<string> lines = null;
            int nameLength = 0;
            if (script == Script.CalcScript)
            {
                Match m = Regex.Match(head, @"@\w+$");
                if (m.Success)
                {
                    CalcFunction f = CalcScriptCatalog.Load().Functions
                        .FirstOrDefault(x => string.Equals(x.Name, m.Value, StringComparison.OrdinalIgnoreCase));
                    if (f != null && !string.IsNullOrWhiteSpace(f.Syntax)) lines = new List<string> { f.Syntax };
                    nameLength = m.Length;
                }
            }
            else
            {
                List<GroovyTypeInference.Segment> chain = GroovyTypeInference.ParseChain(head);
                if (chain.Count > 0)
                {
                    GroovyApi api = GroovyApi.Instance;
                    string method = chain[chain.Count - 1].Name;
                    GroovyApi.TypeInfo receiver = chain.Count > 1
                        ? api.Find(GroovyTypeInference.ResolveChain(chain.Take(chain.Count - 1).ToList(), head, api, 0))
                        : null;
                    List<GroovyApi.Method> found = receiver != null
                        ? api.AllMethods(receiver).Where(x => x.Name == method).ToList()
                        : new List<GroovyApi.Method>();
                    if (found.Count == 0)
                        found = api.Types.Values.SelectMany(x => x.Methods).Where(x => x.Name == method).ToList();
                    lines = found.Select(x => x.ToString()).Distinct().Take(8).ToList();
                    nameLength = method.Length;
                }
            }
            if (lines == null || lines.Count == 0) { CancelHint(sci); return; }

            // Highlight the parameter in the first signature that has that many
            int pick = lines.FindIndex(l => ParamRanges(l).Count > arg);
            if (pick < 0) pick = 0;
            string text = string.Join("\n", lines);
            int lineStart = lines.Take(pick).Sum(l => l.Length + 1);
            List<(int Start, int End)> ranges = ParamRanges(lines[pick]);
            int hs = -1, he = -1;
            if (ranges.Count > 0)
            {
                var r = ranges[Math.Min(arg, ranges.Count - 1)];
                hs = lineStart + r.Start;
                he = lineStart + r.End;
            }

            Encoding enc = ScintillaText.EncodingOf(sci);
            int nameIndex = Math.Max(0, head.Length - nameLength);
            int tipPos = caret - enc.GetByteCount(before.Substring(nameIndex));
            int hsb = hs < 0 ? -1 : enc.GetByteCount(text.Substring(0, hs));
            int heb = he < 0 ? -1 : enc.GetByteCount(text.Substring(0, he));

            bool same = _hintActive && _hintText == text && _hintPos == tipPos &&
                        ScintillaText.Send(sci, SCI_CALLTIPACTIVE) != 0;
            if (same)
                ScintillaText.Send(sci, SCI_CALLTIPSETHLT, hsb, heb);   // just move the highlight
            else
                ShowTip(sci, tipPos, text, hsb, heb);
            _hintActive = true;
            _hintText = text;
            _hintPos = tipPos;
        }

        private static void CancelHint(IntPtr sci)
        {
            if (!_hintActive) return;
            _hintActive = false;
            ScintillaText.Send(sci, SCI_CALLTIPCANCEL);
        }

        /// <summary>
        /// The '(' of the call the end of <paramref name="text"/> is inside, and how many
        /// arguments come before the caret; -1 when it isn't inside a call. Skips strings,
        /// /* comments */, nested calls, [lists] and {blocks}.
        /// </summary>
        private static int InnermostOpenParen(string text, Script script, out int argIndex)
        {
            argIndex = 0;
            int depth = 0;
            int stop = Math.Max(0, text.Length - 8000);
            for (int i = text.Length - 1; i >= stop; i--)
            {
                char c = text[i];
                if (c == '/' && i > 0 && text[i - 1] == '*')            // end of a comment: skip it
                {
                    int o = text.LastIndexOf("/*", i - 1, StringComparison.Ordinal);
                    if (o < 0) return -1;
                    i = o;
                    continue;
                }
                if (c == '"' || (c == '\'' && script == Script.Groovy))   // a string: skip it
                {
                    int o = text.LastIndexOf(c, Math.Max(0, i - 1));
                    if (o < 0 || o == i) return -1;
                    i = o;
                    continue;
                }
                if (c == ')' || c == ']') depth++;
                else if (c == '(')
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if (c == '[')
                {
                    if (depth == 0) return -1;
                    depth--;
                }
                else if (c == ',' && depth == 0) argIndex++;
                else if (depth == 0 && (c == ';' || (script == Script.Groovy && (c == '{' || c == '}'))))
                    return -1;
            }
            return -1;
        }

        /// <summary>Where each parameter sits inside a signature line ("name(a, b [, c])"),
        /// without spaces and optional-brackets around it.</summary>
        private static List<(int Start, int End)> ParamRanges(string signature)
        {
            var result = new List<(int, int)>();
            int open = signature.IndexOf('('), close = signature.LastIndexOf(')');
            if (open < 0 || close <= open + 1) return result;
            int depth = 0, start = open + 1;
            for (int i = open + 1; i <= close; i++)
            {
                char c = i < close ? signature[i] : ',';
                if (c == '(' || c == '<') depth++;
                else if (c == ')' || c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    int a = start, b = i;
                    while (a < b && (signature[a] == ' ' || signature[a] == '[')) a++;
                    while (b > a && (signature[b - 1] == ' ' || signature[b - 1] == ']')) b--;
                    if (b > a) result.Add((a, b));
                    start = i + 1;
                }
            }
            return result;
        }

        /// <summary>
        /// Shows a calltip in colours that read on the current theme: Notepad++'s dark mode
        /// leaves Scintilla's calltip black on black, so set them from the editor background.
        /// </summary>
        private static void ShowTip(IntPtr sci, int pos, string text, int hlStart, int hlEnd)
        {
            int bg = ScintillaText.Send(sci, SCI_STYLEGETBACK, STYLE_DEFAULT);       // 0xBBGGRR
            int r = bg & 0xFF, g = (bg >> 8) & 0xFF, b = (bg >> 16) & 0xFF;
            bool dark = (r * 299 + g * 587 + b * 114) / 1000 < 128;
            int back, fore, hlt;
            if (dark)
            {
                int Lift(int v) => Math.Min(255, v + 0x22);
                back = Lift(r) | (Lift(g) << 8) | (Lift(b) << 16);
                fore = 0xDCDCDC;
                hlt  = 0xFFB66C;      // #6CB6FF
            }
            else
            {
                back = 0xE1FFFF;      // #FFFFE1, the classic tooltip yellow
                fore = 0x202020;
                hlt  = 0xC05000;      // #0050C0
            }
            ScintillaText.Send(sci, SCI_CALLTIPSETBACK, back);
            ScintillaText.Send(sci, SCI_CALLTIPSETFORE, fore);
            ScintillaText.Send(sci, SCI_CALLTIPSETFOREHLT, hlt);
            ScintillaText.SendString(sci, SCI_CALLTIPSHOW, pos, text);
            if (hlStart >= 0 && hlEnd > hlStart)
                ScintillaText.Send(sci, SCI_CALLTIPSETHLT, hlStart, hlEnd);
        }

        // ------------------------------------------------------------------ //
        //  Calc Script: "member names" from the downloaded dimensions
        // ------------------------------------------------------------------ //

        private const int MaxMemberItems = 400;

        /// <summary>A cube's member and dimension names, sorted, with their default aliases.</summary>
        private sealed class MemberIndex
        {
            public EpmMetadata Md;
            public string Cube, Dimension;
            public string[] Names;                          // sorted
            public Dictionary<string, string> AliasOf;      // member → alias
            public string[] Aliases, AliasMembers;          // sorted by alias, parallel
        }
        private static MemberIndex _memberIndex;
        /// <summary>The open list's lines → the member each one inserts.</summary>
        private static readonly Dictionary<string, string> _memberPicks = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Where the typed name starts (just after the opening quote).</summary>
        private static int _memberStart = -1;
        /// <summary>Closes the name like it was opened: ", ' (Groovy) or \" (inside a Groovy string).</summary>
        private static string _memberClose = "\"";
        /// <summary>Put before the picked name: "\"" when it was typed without its opening quote
        /// (a bare value in 00 - Rule.ini), "" otherwise.</summary>
        private static string _memberOpen = "";
        /// <summary>Only this dimension's members (00 - Rule.ini FIX lines); null = every dimension.</summary>
        private static string _dimension;

        /// <summary>A graphical rule's 00 - Rule.ini (its folder holds the rule's .flow.json).</summary>
        private static bool IsRuleIni(string path) =>
            string.Equals(Path.GetFileName(path ?? ""), GraphicalRule.RuleIniName, StringComparison.OrdinalIgnoreCase) &&
            GraphicalRule.RuleFolderOf(path) != null;

        /// <summary>
        /// On a "Name = value" line of 00 - Rule.ini: the value typed so far and the dimension
        /// whose members belong there — the line's name under [Global range] and [NN FIX]; null
        /// (every dimension of the cube) for Member = and Test =. False on comments, [headers]
        /// and the name before the "=".
        /// </summary>
        private static bool RuleIniValue(string before, out string value, out string dimension)
        {
            value = null; dimension = null;
            string line = before.Substring(before.LastIndexOf('\n') + 1);
            string t = line.TrimStart();
            if (t.StartsWith(";") || t.StartsWith("[")) return false;
            int eq = line.IndexOf('=');
            if (eq < 0) return false;
            string key = line.Substring(0, eq).Trim();
            value = line.Substring(eq + 1);

            MatchCollection headers = Regex.Matches(before.Substring(0, before.Length - line.Length), @"(?m)^[ \t]*\[([^\]\r\n]*)\]");
            string section = headers.Count > 0 ? headers[headers.Count - 1].Groups[1].Value.Trim() : "";
            if (string.Equals(section, "Global range", StringComparison.OrdinalIgnoreCase) ||
                section.EndsWith(" FIX", StringComparison.OrdinalIgnoreCase))
            {
                dimension = key;
                return key.Length > 0;
            }
            return string.Equals(key, "Member", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "Test", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 00 - Rule.ini: a value typed without quotes ("Scenario = Fore…") lists the dimension's
        /// members too; the pick goes in quoted ("Forecast"). True when the value is a bare word.
        /// </summary>
        private static bool BareMemberCompletion(IntPtr sci, string path, string value)
        {
            string v = value.TrimStart();
            if (v.Length == 0 || v.Length > 80 || v.IndexOfAny(new[] { '"', '@', '(', ')', ',', '{', '}', '&', '[', ']', ':', '#', '-', '>' }) >= 0)
                return false;
            _memberOpen = "\"";
            _memberClose = "\"";
            return ShowMembers(sci, path, v, inRuleCube: true);
        }

        /// <summary>
        /// Inside a "quoted name" where a member belongs: lists the members whose name or
        /// default alias starts with what was typed, as "Member - Alias". Calc Script: the
        /// rule's cube's members, in "…". Groovy: every cube's members, in any '…' or "…"
        /// string (also the Calc Script lines of a """…""" block). True when the caret is
        /// inside a string (handled, even if nothing is shown), false otherwise. With no
        /// downloaded dimensions it leaves the string to Notepad++'s own word completion.
        /// </summary>
        private static bool MemberCompletion(IntPtr sci, string path, string before, Script script)
        {
            _memberOpen = "";
            int open = before.LastIndexOf("/*", StringComparison.Ordinal);
            if (open >= 0 && before.IndexOf("*/", open, StringComparison.Ordinal) < 0) return false;   // in a comment

            int nl = before.LastIndexOf('\n');
            string line = before.Substring(nl + 1);
            int quote = script == Script.Groovy ? OpenGroovyQuote(line) : OpenCalcQuote(line);
            if (quote < 0) return false;                                                             // not in a string
            string prefix = line.Substring(quote + 1);
            if (prefix.Length == 0 || prefix.Length > 80 || prefix.IndexOfAny(new[] { '{', '[', '&', '$' }) >= 0) return true;
            _memberClose = (quote > 0 && line[quote - 1] == '\\' ? "\\" : "") + line[quote];
            if (script == Script.Groovy) return ShowMembers(sci, path, prefix, inRuleCube: false);

            // Where a member belongs (same rules as member validation)
            string beforeQuote = before.Substring(0, before.Length - prefix.Length - 1);
            int stmt = Math.Max(beforeQuote.LastIndexOf(';'), beforeQuote.LastIndexOf("*/", StringComparison.Ordinal));
            Match first = Regex.Match(beforeQuote.Substring(stmt + 1), @"^\s*([A-Za-z_]\w*)");
            if (first.Success && CalcStudioEPM.Epm.Validation.CalcScriptChecks.NonMemberStatements.Contains(first.Groups[1].Value))
                return true;
            int paren = InnermostOpenParen(beforeQuote, Script.CalcScript, out _);
            if (paren >= 0)
            {
                string head = beforeQuote.Substring(0, paren).TrimEnd();
                if (head.EndsWith("%Template", StringComparison.OrdinalIgnoreCase)) return true;
                Match fn = Regex.Match(head, @"@(\w+)$");
                if (fn.Success && !CalcStudioEPM.Epm.Validation.CalcScriptChecks.MemberFunctions.Contains(fn.Groups[1].Value))
                    return true;
            }
            return ShowMembers(sci, path, prefix, inRuleCube: true);
        }

        /// <summary>The opening '"' of the Calc Script string the line ends inside, or -1.</summary>
        private static int OpenCalcQuote(string line) =>
            line.Count(c => c == '"') % 2 == 1 ? line.LastIndexOf('"') : -1;

        /// <summary>
        /// The opening quote of the Groovy string the line ends inside, or -1: the last ' or "
        /// when it opens a string — an escaped one (\"GL_…) always does, otherwise an odd
        /// count of that quote. Triple quotes are left out, so the Calc Script lines of a
        /// """…""" block count their own "member" quotes; a // comment before it is skipped.
        /// </summary>
        private static int OpenGroovyQuote(string line)
        {
            string l = line.Replace("\"\"\"", "   ").Replace("'''", "   ");
            int q = l.LastIndexOfAny(new[] { '"', '\'' });
            if (q < 0) return -1;
            int comment = l.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && comment < q && l.Take(comment).Count(c => c == '"' || c == '\'') % 2 == 0) return -1;
            if (q > 0 && l[q - 1] == '\\') return q;
            char quote = l[q];
            int count = 0;
            for (int i = 0; i <= q; i++)
                if (l[i] == quote && (i == 0 || l[i - 1] != '\\')) count++;
            return count % 2 == 1 ? q : -1;
        }

        /// <summary>
        /// Lists the members starting with <paramref name="prefix"/> by name, then by alias.
        /// <paramref name="inRuleCube"/>: only the rule's cube's (when it has been downloaded).
        /// </summary>
        private static bool ShowMembers(IntPtr sci, string path, string prefix, bool inRuleCube)
        {
            ConnectionConfig cfg = ConnectionFor(path);
            if (cfg == null) return false;
            EpmMetadata md = EpmMetadata.Load(cfg.LocalFolderPath);
            if (!md.HasDimensions) return false;
            string cube = null;
            if (inRuleCube) try { cube = PackageBuilder.ParseScriptPath(path).cube; } catch { }
            MemberIndex ix = IndexFor(md, md.HasCube(cube) ? cube : null, _dimension);

            // Members by name first, then (after a separator) the ones found by alias
            var byName = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = LowerBound(ix.Names, prefix); i < ix.Names.Length && byName.Count < MaxMemberItems; i++)
            {
                if (!ix.Names[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
                if (seen.Add(ix.Names[i])) byName.Add(ix.Names[i]);
            }
            var byAlias = new List<string>();
            for (int i = LowerBound(ix.Aliases, prefix); i < ix.Aliases.Length && byName.Count + byAlias.Count < MaxMemberItems; i++)
            {
                if (!ix.Aliases[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
                if (seen.Add(ix.AliasMembers[i])) byAlias.Add(ix.AliasMembers[i]);
            }
            if (byName.Count + byAlias.Count == 0) return true;

            _memberPicks.Clear();
            string Line(string member)
            {
                string text = ix.AliasOf.TryGetValue(member, out string alias) ? member + " - " + alias : member;
                if (!_memberPicks.ContainsKey(text)) _memberPicks[text] = member;
                return text;
            }
            List<string> items = byName.Select(Line).ToList();
            if (byName.Count > 0 && byAlias.Count > 0) items.Add(Separator);
            items.AddRange(byAlias.Select(Line));

            // Nothing counts as "entered": the lines need not start with what was typed (alias
            // matches), and the first one stays selected. The pick replaces the typed text.
            int pos = ScintillaText.Send(sci, SCI_GETCURRENTPOS);
            Show(sci, ListKind.Members, 0, items, '\n');
            _memberStart = pos - ScintillaText.EncodingOf(sci).GetByteCount(prefix);
            return true;
        }

        /// <summary>The cube's names and aliases (all cubes when null; one dimension's when
        /// <paramref name="dimension"/> is one of them), built once per download.</summary>
        private static MemberIndex IndexFor(EpmMetadata md, string cube, string dimension = null)
        {
            MemberIndex c = _memberIndex;
            if (c != null && ReferenceEquals(c.Md, md) && string.Equals(c.Cube, cube, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Dimension, dimension, StringComparison.OrdinalIgnoreCase)) return c;

            List<EpmMetadata.DimensionInfo> dims = (cube != null ? md.Dims.ByCube[cube].Values : md.Dims.ByName.Values).ToList();
            if (dimension != null && dims.Any(d => string.Equals(d.Name, dimension, StringComparison.OrdinalIgnoreCase)))
                dims = dims.Where(d => string.Equals(d.Name, dimension, StringComparison.OrdinalIgnoreCase)).ToList();
            bool Usable(string n) => !string.IsNullOrEmpty(n) && n.IndexOf('\n') < 0;
            var aliasOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EpmMetadata.DimensionInfo d in dims)
                foreach (EpmMetadata.MemberNode n in d.Nodes ?? new List<EpmMetadata.MemberNode>())
                    if (Usable(n.Name) && Usable(n.Alias) && !string.Equals(n.Alias, n.Name, StringComparison.OrdinalIgnoreCase) &&
                        !aliasOf.ContainsKey(n.Name))
                        aliasOf[n.Name] = n.Alias;
            var byAlias = aliasOf.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToList();

            c = new MemberIndex
            {
                Md = md, Cube = cube, Dimension = dimension, AliasOf = aliasOf,
                Names = dims.SelectMany(d => new[] { d.Name }.Concat(d.Members ?? new List<string>()))
                            .Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                Aliases      = byAlias.Select(kv => kv.Value).ToArray(),
                AliasMembers = byAlias.Select(kv => kv.Key).ToArray(),
            };
            _memberIndex = c;
            return c;
        }

        // ------------------------------------------------------------------ //
        //  IPM: an Auto Predict job's .autopredict.ini
        // ------------------------------------------------------------------ //

        /// <summary>
        /// An Auto Predict .ini: typing a value lists what belongs there — on a dimension's line (an
        /// output's or driver's, or the Model Scope's) that dimension's members, in a member list
        /// (Members =, Fitted values = …) every dimension's, after Calendar = the pulled calendars.
        /// The item after the last comma is completed; the pick goes in as it is, without quotes.
        /// </summary>
        private static void IpmCompletion(IntPtr sci, string path)
        {
            if (!PluginSettings.Current.AutoCompleteCalcScript) return;
            if (_kind == ListKind.Members && ScintillaText.Send(sci, SCI_AUTOCACTIVE) != 0)
                ScintillaText.Send(sci, SCI_AUTOCCANCEL);   // rebuilt for the longer prefix (aliases)

            int pos = ScintillaText.Send(sci, SCI_GETCURRENTPOS);
            string before = ScintillaText.GetRange(sci, Math.Max(0, pos - 40000), pos);
            string line = before.Substring(before.LastIndexOf('\n') + 1);
            string t = line.TrimStart();
            int eq = line.IndexOf('=');
            if (t.StartsWith(";") || t.StartsWith("[") || eq < 0) return;
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1);
            int depth = 0, comma = -1;
            bool quoted = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && (c == '(' || c == '[')) depth++;
                else if (!quoted && (c == ')' || c == ']')) depth = Math.Max(0, depth - 1);
                else if (!quoted && depth == 0 && c == ',') comma = i;
            }
            string prefix = value.Substring(comma + 1).TrimStart();
            if (quoted || depth > 0 || prefix.Length == 0 || prefix.Length > 80 ||
                prefix.IndexOfAny(new[] { '"', '(', ')', '{', '}', '[', ']', '&', '@' }) >= 0) return;

            MatchCollection headers = Regex.Matches(before.Substring(0, before.Length - line.Length), @"(?m)^[ \t]*\[([^\]\r\n]*)\]");
            string section = headers.Count > 0 ? headers[headers.Count - 1].Groups[1].Value.Trim() : "";
            _memberOpen = "";
            _memberClose = "";

            if (string.Equals(key, "Calendar", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionConfig cfg = ConnectionFor(path);
                if (cfg == null) return;
                List<string> names = IpmContext.For(cfg).Calendars.Select(c => c.Name)
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (names.Count == 0) return;
                _memberPicks.Clear();
                foreach (string n in names) _memberPicks[n] = n;
                Show(sci, ListKind.Members, 0, names, '\n');
                _memberStart = pos - ScintillaText.EncodingOf(sci).GetByteCount(prefix);
                return;
            }
            if (!AutoPredictIni.IsMemberLine(section, key, out _dimension)) return;
            ShowMembers(sci, path, prefix, inRuleCube: false);
        }

        // ------------------------------------------------------------------ //
        //  FIX( → the cube's dimensions as a commented list
        // ------------------------------------------------------------------ //

        private static List<(string Name, bool Dense)> _fixDimensions;
        private static string _fixIndent = "";

        /// <summary>
        /// Typing "(" after FIX or EXCLUDE (nothing else on the line, or only the ")" Notepad++
        /// adds) offers the rule's cube's dimensions as a commented list: sparse ones first, then
        /// the dense ones (see <see cref="CubeDensity"/>), attribute dimensions left out. Needs
        /// downloaded dimensions. True when the offer is shown.
        /// </summary>
        private static bool FixTemplateOffer(IntPtr sci, string path, string before, int pos)
        {
            string line = before.Substring(before.LastIndexOf('\n') + 1);
            Match m = Regex.Match(line, @"^([ \t]*)(?:.*?[\s;)])?(FIX|EXCLUDE)\s*\($", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            if (InCommentOrString(before.Substring(0, before.Length - 1), Script.CalcScript)) return false;

            int lineNo  = ScintillaText.Send(sci, SCI_LINEFROMPOSITION, pos);
            int lineEnd = ScintillaText.Send(sci, SCI_GETLINEENDPOSITION, lineNo);
            string after = ScintillaText.GetRange(sci, pos, lineEnd).Trim();
            if (after.Length > 0 && after != ")") return false;

            ConnectionConfig cfg = ConnectionFor(path);
            if (cfg == null) return false;
            EpmMetadata md = EpmMetadata.Load(cfg.LocalFolderPath);
            string cube = null;
            try { cube = PackageBuilder.ParseScriptPath(path).cube; } catch { }
            List<(string Name, bool Dense)> dims = CubeDensity.FixDimensions(cfg.LocalFolderPath, md, cube);
            if (dims.Count == 0) return false;

            _fixDimensions = dims;
            _fixIndent = m.Groups[1].Value;
            int dense = dims.Count(d => d.Dense);
            Show(sci, ListKind.FixTemplate, 0,
                 new[] { $"Dimensions of {cube} ({dims.Count - dense} sparse, {dense} dense)" }, '\n');
            return true;
        }

        /// <summary>
        /// The text after "FIX(": one "/*Dimension*/" line per dimension (dense ones marked
        /// " - Dense", after a blank line), indented under the FIX, then the closing bracket.
        /// <paramref name="caret"/>: where the first dimension's members go.
        /// </summary>
        private static string FixTemplate(List<(string Name, bool Dense)> dims, string indent, bool closeBracket, out int caret)
        {
            string inner = indent + "\t";
            string Comment((string Name, bool Dense) d) => "/*" + d.Name + (d.Dense ? " - Dense" : "") + "*/";
            int width = dims.Max(d => Comment(d).Length) + 1;
            var sb = new StringBuilder();
            caret = -1;
            bool firstDense = true;
            foreach (var d in dims)
            {
                if (d.Dense && firstDense && dims.Any(x => !x.Dense)) sb.Append('\n');   // blank line before the dense group
                if (d.Dense) firstDense = false;
                sb.Append('\n').Append(inner).Append(Comment(d).PadRight(width));
                if (caret < 0) caret = sb.Length;
            }
            sb.Append('\n').Append(indent);
            if (closeBracket) sb.Append(')');
            return sb.ToString();
        }

        private static int LowerBound(string[] sorted, string prefix)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (StringComparer.OrdinalIgnoreCase.Compare(sorted[mid], prefix) < 0) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static void Close(IntPtr sci)
        {
            if (_kind == ListKind.None) return;
            if (_kind == ListKind.Members || _kind == ListKind.FixTemplate)
                ScintillaText.Send(sci, SCI_AUTOCSETSEPARATOR, ' ');   // Notepad++'s lists use spaces
            _kind = ListKind.None;
            ScintillaText.Send(sci, SCI_AUTOCSETIGNORECASE, _prevIgnoreCase);
            ScintillaText.Send(sci, SCI_AUTOCSETORDER, _prevOrder);
        }

        // ------------------------------------------------------------------ //
        //  Context
        // ------------------------------------------------------------------ //

        private static Script CurrentScript(out string path)
        {
            var sb = new StringBuilder(1024);
            SendMessage(PluginBase.nppData._nppHandle, NPPM_GETFULLCURRENTPATH, (IntPtr)sb.Capacity, sb);
            path = sb.ToString();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".csc") return Script.CalcScript;
            if (IsRuleIni(path)) return Script.CalcScript;     // its values are
            if (ext == ".groovy") return Script.Groovy;
            if (ext == ".js" && path.IndexOf(".Scripts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 &&
                ConnectionFor(path) != null) return Script.Groovy;
            return Script.None;
        }

        /// <summary>The connection whose local folder holds <paramref name="path"/>.</summary>
        private static ConnectionConfig ConnectionFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return ConfigManager.GetAll()
                    .Where(c => !string.IsNullOrWhiteSpace(c.LocalFolderPath) &&
                                path.StartsWith(c.LocalFolderPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.LocalFolderPath.Length)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Rough check: is the end of <paramref name="text"/> inside a comment or a string?</summary>
        private static bool InCommentOrString(string text, Script script)
        {
            int open = text.LastIndexOf("/*", StringComparison.Ordinal);
            if (open >= 0 && text.IndexOf("*/", open, StringComparison.Ordinal) < 0) return true;
            int nl = text.LastIndexOf('\n');
            string line = text.Substring(nl + 1);
            if (script == Script.Groovy)
            {
                int sl = line.IndexOf("//", StringComparison.Ordinal);
                if (sl >= 0 && line.Take(sl).Count(c => c == '"') % 2 == 0) return true;
                if (line.Count(c => c == '\'') % 2 == 1) return true;
            }
            return line.Count(c => c == '"') % 2 == 1;
        }

        private static string FullText(IntPtr sci)
        {
            int len = ScintillaText.Send(sci, SCI_GETLENGTH);
            return ScintillaText.GetRange(sci, 0, Math.Min(len, 4 * 1024 * 1024));
        }

        // ------------------------------------------------------------------ //
        //  Interop
        // ------------------------------------------------------------------ //

        private const uint NPPM_GETFULLCURRENTPATH = 4024 + 1;
        private const uint SCI_GETCURRENTPOS      = 2008;
        private const uint SCI_LINEFROMPOSITION   = 2166;
        private const uint SCI_GETLINEENDPOSITION = 2136;
        private const uint SCI_GETCHARAT          = 2007;
        private const uint SCI_GETLENGTH          = 2006;
        private const uint SCI_GOTOPOS            = 2025;
        private const uint SCI_AUTOCSHOW          = 2100;
        private const uint SCI_AUTOCCANCEL        = 2101;
        private const uint SCI_AUTOCACTIVE        = 2102;
        private const uint SCI_AUTOCSETSEPARATOR  = 2106;
        private const uint SCI_AUTOCSETIGNORECASE = 2115;
        private const uint SCI_AUTOCGETIGNORECASE = 2116;
        private const uint SCI_AUTOCSETAUTOHIDE   = 2118;
        private const uint SCI_AUTOCSETMAXHEIGHT  = 2210;
        private const uint SCI_AUTOCSETCASEINSENSITIVEBEHAVIOUR = 2634;
        private const uint SCI_AUTOCSETORDER      = 2660;
        private const uint SCI_AUTOCGETORDER      = 2661;
        private const uint SCI_CALLTIPSHOW        = 2200;
        private const uint SCI_CALLTIPCANCEL      = 2201;
        private const uint SCI_CALLTIPACTIVE      = 2202;
        private const uint SCI_CALLTIPSETHLT      = 2204;
        private const uint SCI_CALLTIPSETBACK     = 2205;
        private const uint SCI_CALLTIPSETFORE     = 2206;
        private const uint SCI_CALLTIPSETFOREHLT  = 2207;
        private const uint SCI_STYLEGETBACK       = 2482;
        private const int  STYLE_DEFAULT          = 32;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
    }
}
