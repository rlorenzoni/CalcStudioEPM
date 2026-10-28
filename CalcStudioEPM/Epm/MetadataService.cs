using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>What a metadata download got (counts), and what went wrong.</summary>
    public sealed class MetadataResult
    {
        public int SubstitutionVariables, UserVariables, Cubes, Dimensions, Members;
        public bool   DimensionsRequested;
        public string VariablesError, DimensionsError;
    }

    /// <summary>
    /// Downloads the metadata local validation uses, with the Planning REST API (v3):
    ///  • substitutionvariables                         — every pull
    ///  • uservariables                                  — every pull
    ///  • plantypes → plantypes/{cube}/dimensions → plantypes/{cube}/dimensions/{dim}
    ///                                                   — when asked: each dimension's members
    ///                                                     with every field Planning sends (ID,
    ///                                                     alias, storage …)
    /// Failures never fail the pull: they are reported, and the previous files are kept
    /// (a dimension that fails keeps its previously downloaded members).
    /// </summary>
    public static class MetadataService
    {
        private const int ParallelDimensions = 4;

        /// <param name="status">Short live status for the progress window ("12 substitution, 3 user
        /// variables; dimensions 40 of 96…"), reported as the download goes.</param>
        public static async Task<MetadataResult> PullAsync(ConnectionConfig cfg, bool includeDimensions, IProgress<string> log,
                                                           IProgress<string> status = null)
        {
            var result = new MetadataResult { DimensionsRequested = includeDimensions };
            void Log(string m) => log?.Report("[metadata] " + m);
            string varsText = "variables…", dimsText = includeDimensions ? "dimensions waiting" : null;
            void Status() => status?.Report(dimsText == null ? varsText : varsText + "; " + dimsText);
            Status();

            using (var client = new EpmClient(cfg))
            {
                // ---- Substitution and user variables ----
                try
                {
                    var vars = new EpmMetadata.Variables { PulledAt = DateTime.Now };
                    JObject sv = JObject.Parse(await client.GetLargeJsonAsync("substitutionvariables").ConfigureAwait(false));
                    foreach (JToken item in sv["items"] ?? new JArray())
                        vars.SubstitutionVariables.Add(new EpmMetadata.SubstitutionVariable
                        {
                            Name = (string)item["name"], Value = (string)item["value"], PlanType = (string)item["planType"],
                        });

                    JObject uv = JObject.Parse(await client.GetLargeJsonAsync("uservariables?offset=0&limit=-1").ConfigureAwait(false));
                    foreach (JToken item in uv["items"] ?? new JArray())
                        vars.UserVariables.Add(new EpmMetadata.UserVariable
                        {
                            Name = (string)item["name"], Dimension = (string)item["dimension"], Definition = (string)item["definition"],
                        });

                    EpmMetadata.SaveVariables(cfg.LocalFolderPath, vars);
                    result.SubstitutionVariables = vars.SubstitutionVariables.Count;
                    result.UserVariables         = vars.UserVariables.Count;
                    Log($"{result.SubstitutionVariables} substitution variables, {result.UserVariables} user variables saved.");
                    varsText = $"{result.SubstitutionVariables} substitution, {result.UserVariables} user variables";
                }
                catch (Exception ex)
                {
                    result.VariablesError = ex.Message;
                    Log("Could not download substitution / user variables: " + ex.Message);
                    varsText = "variables failed";
                }
                if (includeDimensions) dimsText = "cubes…";
                Status();

                if (!includeDimensions) return result;

                // ---- Dimensions and members ----
                try
                {
                    EpmMetadata previous = EpmMetadata.Load(cfg.LocalFolderPath);
                    var dims = new EpmMetadata.Dimensions { PulledAt = DateTime.Now };

                    JObject pt = JObject.Parse(await client.GetLargeJsonAsync("plantypes").ConfigureAwait(false));
                    var cubes = (pt["items"] ?? new JArray())
                        .Select(i => (string)i["cubeName"] ?? (string)i["planTypeName"])
                        .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    Log($"{cubes.Count} cubes: {string.Join(", ", cubes)}");
                    dimsText = $"dimensions of {cubes.Count} cubes…";
                    Status();

                    // Every (cube, dimension): Planning returns only the members valid for the cube asked about
                    var pairs = new List<(string Cube, string Dim, string DimType, string Density)>();
                    foreach (string cube in cubes)
                    {
                        JObject d = JObject.Parse(await client.GetLargeJsonAsync(
                            $"plantypes/{Uri.EscapeDataString(cube)}/dimensions?limit=-1").ConfigureAwait(false));
                        var names = new List<string>();
                        foreach (JToken item in d["items"] ?? new JArray())
                        {
                            string name = (string)item["name"];
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            names.Add(name);
                            pairs.Add((cube, name, (string)item["dimType"], (string)item["density"]));   // "Dense" / "Sparse" per cube
                        }
                        dims.Cubes[cube] = names;
                        Log($"{cube}: {names.Count} dimensions");
                    }

                    var failed = new List<string>();
                    int finished = 0;
                    void Counted()
                    {
                        int n = Interlocked.Increment(ref finished);
                        lock (pairs) { dimsText = $"dimensions {n} of {pairs.Count}" + (failed.Count > 0 ? $" ({failed.Count} failed)" : ""); Status(); }
                    }
                    dimsText = $"dimensions 0 of {pairs.Count}";
                    Status();
                    using (var gate = new SemaphoreSlim(ParallelDimensions))
                    {
                        var tasks = pairs.Select(async pr =>
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                // No fields filter: every field Planning has for a member is kept (ID included)
                                string json = await client.GetLargeJsonAsync(
                                    $"plantypes/{Uri.EscapeDataString(pr.Cube)}/dimensions/{Uri.EscapeDataString(pr.Dim)}").ConfigureAwait(false);
                                var info = new EpmMetadata.DimensionInfo { Name = pr.Dim, DimType = pr.DimType, Density = pr.Density };
                                Flatten(JObject.Parse(json), info, isRoot: true, parent: pr.Dim);
                                info.Members = info.Members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();   // shared members
                                info.Aliases = info.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                lock (dims) Slot(dims, pr.Cube)[pr.Dim] = info;
                                Log($"{pr.Cube} / {pr.Dim}: {info.Members.Count} members");
                            }
                            catch (Exception ex)
                            {
                                lock (failed) failed.Add($"{pr.Cube}/{pr.Dim}");
                                Log($"{pr.Cube} / {pr.Dim}: could not download its members — {ex.Message}");
                                EpmMetadata.DimensionInfo old = previous.HasDimensions ? previous.Dims.Get(pr.Cube, pr.Dim) : null;
                                if (old != null) lock (dims) Slot(dims, pr.Cube)[pr.Dim] = old;   // keep what we had
                            }
                            finally
                            {
                                gate.Release();
                                Counted();
                            }
                        }).ToList();
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }

                    EpmMetadata.SaveDimensions(cfg.LocalFolderPath, dims);   // per cube; the union is rebuilt on load
                    dims.BuildUnion();
                    result.Cubes      = dims.Cubes.Count;
                    result.Dimensions = dims.ByName.Count;
                    result.Members    = dims.ByName.Values.Sum(x => x.Members.Count);
                    if (failed.Count > 0)
                        result.DimensionsError = $"{failed.Count} dimension(s) failed: {string.Join(", ", failed)} (kept their previous members)";
                    Log($"Dimensions saved: {result.Dimensions} dimensions, {result.Members} members.");
                }
                catch (Exception ex)
                {
                    result.DimensionsError = ex.Message;
                    Log("Could not download dimensions: " + ex.Message + " (the previous dimensions are kept)");
                }
            }
            return result;
        }

        private static Dictionary<string, EpmMetadata.DimensionInfo> Slot(EpmMetadata.Dimensions dims, string cube)
        {
            if (!dims.ByCube.TryGetValue(cube, out var slot))
                dims.ByCube[cube] = slot = new Dictionary<string, EpmMetadata.DimensionInfo>(StringComparer.OrdinalIgnoreCase);
            return slot;
        }

        /// <summary>Every member under a dimension node (the node itself is the dimension), with its
        /// parent and the other fields Planning sent.</summary>
        private static void Flatten(JToken node, EpmMetadata.DimensionInfo info, bool isRoot, string parent)
        {
            string name = isRoot ? info.Name : (string)node["name"];
            if (isRoot) info.Fields = FieldsOf(node);
            else if (!string.IsNullOrEmpty(name))
            {
                info.Members.Add(name);
                string alias = (string)node["alias"];
                bool hasAlias = !string.IsNullOrEmpty(alias) && !string.Equals(alias, name, StringComparison.Ordinal);
                if (hasAlias) info.Aliases.Add(alias);
                info.Nodes.Add(new EpmMetadata.MemberNode { Name = name, Alias = hasAlias ? alias : null, Parent = parent, Fields = FieldsOf(node) });
            }
            foreach (JToken child in node["children"] ?? new JArray())
                Flatten(child, info, isRoot: false, parent: name);
        }

        /// <summary>A node's fields as received, except those kept elsewhere (name, alias, children)
        /// and empty ones; arrays and objects as compact JSON.</summary>
        private static Dictionary<string, string> FieldsOf(JToken node)
        {
            if (!(node is JObject o)) return null;
            var fields = new Dictionary<string, string>();
            foreach (JProperty p in o.Properties())
            {
                if (p.Name == "name" || p.Name == "alias" || p.Name == "children") continue;
                JToken v = p.Value;
                if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.Undefined) continue;
                string text = v is JValue jv ? Convert.ToString(jv.Value, System.Globalization.CultureInfo.InvariantCulture)
                                             : v.ToString(Newtonsoft.Json.Formatting.None);
                if (v.Type == JTokenType.Boolean) text = text.ToLowerInvariant();
                if (string.IsNullOrEmpty(text) || text == "[]" || text == "{}") continue;
                fields[p.Name] = text;
            }
            return fields.Count > 0 ? fields : null;
        }
    }
}
