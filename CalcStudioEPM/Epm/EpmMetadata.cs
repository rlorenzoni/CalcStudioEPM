using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// What Pull downloads besides the scripts, for local validation:
    ///  • substitution variables and user variables — every pull (small);
    ///  • dimensions per cube and their members — only when the connection asks for it and
    ///    the pull's "update dimensions and members" box is ticked (large, and stable).
    /// Stored in {LocalFolder}\.epm\ (outside CalcManager\, which every pull replaces).
    /// </summary>
    public sealed class EpmMetadata
    {
        public sealed class SubstitutionVariable
        {
            public string Name     { get; set; }
            public string Value    { get; set; }
            public string PlanType { get; set; }   // a cube name, or "ALL"
        }

        public sealed class UserVariable
        {
            public string Name       { get; set; }
            public string Dimension  { get; set; }
            public string Definition { get; set; }
        }

        /// <summary>One position of a member in the hierarchy (a shared member appears once per parent).</summary>
        public sealed class MemberNode
        {
            [JsonProperty("n")] public string Name   { get; set; }
            [JsonProperty("a", NullValueHandling = NullValueHandling.Ignore)] public string Alias  { get; set; }
            /// <summary>Parent member; the dimension name for its top-level members.</summary>
            [JsonProperty("p")] public string Parent { get; set; }
            /// <summary>Every other field Planning sent for the member (id, storage, formula …), as
            /// received; arrays and objects as compact JSON. Null in files from before it was kept.</summary>
            [JsonProperty("f", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string> Fields { get; set; }

            /// <summary>Planning's member ID (a GUID); null when not downloaded.</summary>
            [JsonIgnore] public string Id =>
                Fields == null ? null : Fields.TryGetValue("id", out string id) ? id : Fields.TryGetValue("memberId", out id) ? id : null;
        }

        public sealed class DimensionInfo
        {
            public string       Name    { get; set; }
            public string       DimType { get; set; }
            /// <summary>"Dense" or "Sparse" in this cube, as Planning reports it; null in files
            /// from before it was kept.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string       Density { get; set; }
            /// <summary>Distinct member names and aliases — for fast name checks.</summary>
            public List<string> Members { get; set; } = new List<string>();
            public List<string> Aliases { get; set; } = new List<string>();
            /// <summary>The hierarchy in outline order; empty in files from before it was kept.</summary>
            public List<MemberNode> Nodes { get; set; } = new List<MemberNode>();
            /// <summary>The fields Planning sent for the dimension's own node (see <see cref="MemberNode.Fields"/>).</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, string> Fields { get; set; }

            [JsonIgnore] public bool HasHierarchy => Nodes != null && Nodes.Count > 0;

            private Dictionary<string, List<MemberNode>> _children;

            /// <summary>Children of <paramref name="parent"/> in outline order (the dimension name for the top level).</summary>
            public List<MemberNode> ChildrenOf(string parent)
            {
                if (_children == null)
                {
                    var map = new Dictionary<string, List<MemberNode>>(StringComparer.OrdinalIgnoreCase);
                    foreach (MemberNode n in Nodes ?? new List<MemberNode>())
                    {
                        string key = n.Parent ?? Name;
                        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<MemberNode>();
                        list.Add(n);
                    }
                    _children = map;
                }
                return _children.TryGetValue(parent ?? Name, out var kids) ? kids : new List<MemberNode>();
            }

            private Dictionary<string, List<MemberNode>> _positions;

            /// <summary>All positions of a member in outline order (more than one when it is shared).</summary>
            public List<MemberNode> PositionsOf(string member)
            {
                if (_positions == null)
                {
                    var map = new Dictionary<string, List<MemberNode>>(StringComparer.OrdinalIgnoreCase);
                    foreach (MemberNode n in Nodes ?? new List<MemberNode>())
                    {
                        if (string.IsNullOrEmpty(n.Name)) continue;
                        if (!map.TryGetValue(n.Name, out var list)) map[n.Name] = list = new List<MemberNode>();
                        list.Add(n);
                    }
                    _positions = map;
                }
                return _positions.TryGetValue(member ?? "", out var found) ? found : new List<MemberNode>();
            }

            /// <summary>Dimension → … → parent → member, following the first position of each ancestor.</summary>
            public List<string> PathOf(MemberNode node)
            {
                var path = new List<string> { node.Name };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Name };
                string p = node.Parent;
                while (!string.IsNullOrEmpty(p) && !string.Equals(p, Name, StringComparison.OrdinalIgnoreCase) && seen.Add(p))
                {
                    path.Insert(0, p);
                    p = PositionsOf(p).FirstOrDefault()?.Parent;
                }
                path.Insert(0, Name);
                return path;
            }
        }

        public sealed class Variables
        {
            public DateTime PulledAt { get; set; }
            public List<SubstitutionVariable> SubstitutionVariables { get; set; } = new List<SubstitutionVariable>();
            public List<UserVariable>         UserVariables         { get; set; } = new List<UserVariable>();
        }

        public sealed class Dimensions
        {
            public DateTime PulledAt { get; set; }
            /// <summary>Cube → its dimension names.</summary>
            public Dictionary<string, List<string>> Cubes { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Cube → dimension → its members in that cube. Planning returns only the
            /// members valid for the cube asked about, so each cube has its own copy.</summary>
            public Dictionary<string, Dictionary<string, DimensionInfo>> ByCube { get; set; } =
                new Dictionary<string, Dictionary<string, DimensionInfo>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Dimension → its members in any cube. Built from <see cref="ByCube"/> on load;
            /// read from older files, which kept one copy per dimension.</summary>
            public Dictionary<string, DimensionInfo> ByName { get; set; } = new Dictionary<string, DimensionInfo>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The dimension as a cube sees it; the all-cube union when the cube is unknown.</summary>
            public DimensionInfo Get(string cube, string dimension)
            {
                if (!string.IsNullOrEmpty(cube) && ByCube.TryGetValue(cube, out var dims) &&
                    dims.TryGetValue(dimension ?? "", out var inCube))
                    return inCube;
                return ByName.TryGetValue(dimension ?? "", out var any) ? any : null;
            }

            /// <summary>Fills <see cref="ByName"/> with each dimension's union over the cubes.</summary>
            internal void BuildUnion()
            {
                if (ByCube.Count == 0) return;
                var union = new Dictionary<string, DimensionInfo>(StringComparer.OrdinalIgnoreCase);
                var seenNode = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var cube in ByCube.Values)
                    foreach (DimensionInfo d in cube.Values)
                    {
                        if (!union.TryGetValue(d.Name, out DimensionInfo u))
                        {
                            union[d.Name] = u = new DimensionInfo { Name = d.Name, DimType = d.DimType, Fields = d.Fields };
                            seenNode[d.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                        foreach (MemberNode n in d.Nodes ?? new List<MemberNode>())
                            if (seenNode[d.Name].Add(n.Name + "\u0001" + n.Parent)) u.Nodes.Add(n);
                        u.Members.AddRange(d.Members ?? new List<string>());
                        u.Aliases.AddRange(d.Aliases ?? new List<string>());
                    }
                foreach (DimensionInfo u in union.Values)
                {
                    u.Members = u.Members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    u.Aliases = u.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                ByName = union;
            }
        }

        public Variables  Vars { get; private set; }
        public Dimensions Dims { get; private set; }

        public bool HasVariables  => Vars != null;
        public bool HasDimensions => Dims != null && Dims.ByName.Count > 0;

        // ------------------------------------------------------------------ //
        //  Files
        // ------------------------------------------------------------------ //

        public static string Folder(string localFolder) => Path.Combine(localFolder ?? "", PathConstants.EpmDirName);
        private static string VariablesFile(string localFolder)  => Path.Combine(Folder(localFolder), "variables_epm.json");
        private static string DimensionsFile(string localFolder) => Path.Combine(Folder(localFolder), "dimensions.json");

        public static void SaveVariables(string localFolder, Variables v)   => Write(VariablesFile(localFolder), v);
        public static void SaveDimensions(string localFolder, Dimensions d) => Write(DimensionsFile(localFolder), d);

        private static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            lock (Cache) Cache.Remove(Path.GetDirectoryName(path).ToLowerInvariant());
        }

        /// <summary>When the dimensions were last downloaded for this connection, if ever.</summary>
        public static DateTime? DimensionsPulledAt(string localFolder)
        {
            try
            {
                string f = DimensionsFile(localFolder);
                return File.Exists(f) ? File.GetLastWriteTime(f) : (DateTime?)null;
            }
            catch { return null; }
        }

        private static readonly Dictionary<string, (DateTime Stamp, EpmMetadata Data)> Cache =
            new Dictionary<string, (DateTime, EpmMetadata)>();

        /// <summary>The connection's metadata (cached until its files change); empty when never pulled.</summary>
        public static EpmMetadata Load(string localFolder)
        {
            var m = new EpmMetadata();
            if (string.IsNullOrWhiteSpace(localFolder)) return m;
            string key = Folder(localFolder).ToLowerInvariant();
            string vf = VariablesFile(localFolder), df = DimensionsFile(localFolder);
            DateTime stamp = new[] { vf, df }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();

            lock (Cache)
                if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Data;

            try { if (File.Exists(vf)) m.Vars = JsonConvert.DeserializeObject<Variables>(File.ReadAllText(vf, Encoding.UTF8)); } catch { }
            try
            {
                if (File.Exists(df))
                {
                    m.Dims = JsonConvert.DeserializeObject<Dimensions>(File.ReadAllText(df, Encoding.UTF8));
                    // Json.NET fills these with case-sensitive dictionaries
                    m.Dims.Cubes  = new Dictionary<string, List<string>>(m.Dims.Cubes ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                    m.Dims.ByName = new Dictionary<string, DimensionInfo>(m.Dims.ByName ?? new Dictionary<string, DimensionInfo>(), StringComparer.OrdinalIgnoreCase);
                    var byCube = new Dictionary<string, Dictionary<string, DimensionInfo>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in m.Dims.ByCube ?? new Dictionary<string, Dictionary<string, DimensionInfo>>())
                        byCube[kv.Key] = new Dictionary<string, DimensionInfo>(kv.Value ?? new Dictionary<string, DimensionInfo>(), StringComparer.OrdinalIgnoreCase);
                    m.Dims.ByCube = byCube;
                    m.Dims.BuildUnion();
                }
            }
            catch { }
            lock (Cache) Cache[key] = (stamp, m);
            return m;
        }

        // ------------------------------------------------------------------ //
        //  Lookups (Essbase names are case-insensitive)
        // ------------------------------------------------------------------ //

        // member or alias → dimensions; key "" = all cubes, else per cube
        private readonly Dictionary<string, Dictionary<string, List<string>>> _memberIndex =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The dimensions that have a member (or alias) with this name — in
        /// <paramref name="cube"/> when given and downloaded per cube, else in any cube.</summary>
        public IReadOnlyList<string> DimensionsOfMember(string name, string cube = null)
        {
            if (!HasDimensions || string.IsNullOrEmpty(name)) return new string[0];
            string key = !string.IsNullOrEmpty(cube) && Dims.ByCube.ContainsKey(cube) ? cube : "";
            lock (_memberIndex)
            {
                if (!_memberIndex.TryGetValue(key, out var index))
                {
                    index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    void Add(string n, string dim)
                    {
                        if (string.IsNullOrEmpty(n)) return;
                        if (!index.TryGetValue(n, out var l)) index[n] = l = new List<string>();
                        if (!l.Contains(dim)) l.Add(dim);
                    }
                    IEnumerable<DimensionInfo> dims = key == "" ? Dims.ByName.Values : Dims.ByCube[key].Values;
                    foreach (DimensionInfo d in dims)
                    {
                        Add(d.Name, d.Name);                                   // the dimension itself
                        foreach (string mem in d.Members ?? new List<string>()) Add(mem, d.Name);
                        foreach (string al in d.Aliases ?? new List<string>()) Add(al, d.Name);
                    }
                    _memberIndex[key] = index;
                }
                return index.TryGetValue(name, out var found) ? (IReadOnlyList<string>)found : new string[0];
            }
        }

        private Dictionary<string, (DimensionInfo Dim, MemberNode Node)> _byId;

        /// <summary>The member with this Planning member ID (its first position in the outline), in
        /// any cube; null when unknown or the dimensions were downloaded without IDs.</summary>
        public (DimensionInfo Dim, MemberNode Node)? MemberById(string id)
        {
            if (!HasDimensions || string.IsNullOrEmpty(id)) return null;
            lock (_memberIndex)
            {
                if (_byId == null)
                {
                    var map = new Dictionary<string, (DimensionInfo, MemberNode)>(StringComparer.OrdinalIgnoreCase);
                    foreach (DimensionInfo d in Dims.ByName.Values)
                        foreach (MemberNode n in d.Nodes ?? new List<MemberNode>())
                        {
                            string nid = n.Id;
                            if (!string.IsNullOrEmpty(nid) && !map.ContainsKey(nid)) map[nid] = (d, n);
                        }
                    _byId = map;
                }
                return _byId.TryGetValue(id.Trim(), out var hit) ? hit : ((DimensionInfo, MemberNode)?)null;
            }
        }

        /// <summary>Cubes downloaded per cube (empty for older files).</summary>
        public bool HasCube(string cube) => HasDimensions && !string.IsNullOrEmpty(cube) && Dims.ByCube.ContainsKey(cube);

        public bool DimensionExists(string name) => HasDimensions && Dims.ByName.ContainsKey(name ?? "");

        /// <summary>Substitution variables called <paramref name="name"/> (any plan type).</summary>
        public List<SubstitutionVariable> SubstitutionVariablesNamed(string name) =>
            (Vars?.SubstitutionVariables ?? new List<SubstitutionVariable>())
                .Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        public UserVariable UserVariableNamed(string name) =>
            (Vars?.UserVariables ?? new List<UserVariable>())
                .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
