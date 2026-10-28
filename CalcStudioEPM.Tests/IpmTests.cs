using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace CalcStudioEPM.Tests
{
    /// <summary>
    /// IPM: the pulled package (Fixtures\Ipm — application Vision, an Advanced Predictions job, an
    /// Auto Predict job with insights, two calendars, events) written as files to edit, read back,
    /// edited, and packaged for a Planning-only deploy.
    /// </summary>
    [TestClass]
    public class IpmTests
    {
        private const string Advanced = "Revenue - Advanced", Univariate = "Revenue - Forecast";

        private ConnectionConfig _cfg;
        private string _edit;

        [TestInitialize]
        public void Pull()
        {
            _cfg = TestData.Connection(TestData.CopyFixture("Ipm"), "Test", "Vision");
            _edit = WriteEditFiles(_cfg);
        }

        private static string WriteEditFiles(ConnectionConfig cfg, IpmPullResult result = null)
        {
            IpmService.WriteEditFiles(cfg, result ?? new IpmPullResult(), _ => { });
            return IpmFiles.EditDir(cfg);
        }

        private string JobIni(string job) => Path.Combine(_edit, IpmFiles.AutoPredictFolder, job + IpmFiles.AutoPredictSuffix);
        private string Calendars => Path.Combine(_edit, IpmFiles.CalendarsIni);

        private static string Read(string path) => File.ReadAllText(path, Encoding.UTF8);
        private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

        /// <summary>The .ini with <paramref name="from"/> replaced (which must be there).</summary>
        private static void Edit(string path, string from, string to)
        {
            string text = Read(path);
            Assert.IsTrue(text.Contains(from), $"'{from}' is not in {Path.GetFileName(path)}:\n{text}");
            Write(path, text.Replace(from, to));
        }

        private IpmDeployPlan Plan(string path) => IpmService.Plan(_cfg, path);

        private static JObject Mapping(string xml) =>
            JObject.Parse(XDocument.Parse(xml).Root.Element("MLModelMapping").Value);

        private static JObject Feature(JObject mapping, string group, string name) =>
            ((JArray)mapping[group]["feature"]).OfType<JObject>().Single(f => (string)f["featureName"] == name);

        private static string[] Members(JObject feature, string dimension) =>
            ((JArray)feature["dimMappings"]).OfType<JObject>().Single(d => (string)d["dimension"] == dimension)["members"]
                .Select(t => (string)t).ToArray();

        // ------------------------------------------------------------------ //
        //  Pull
        // ------------------------------------------------------------------ //

        [TestMethod]
        public void Pull_WritesOneFilePerArtifact()
        {
            var result = new IpmPullResult();
            WriteEditFiles(_cfg, result);
            Assert.AreEqual(2, result.AutoPredictJobs);
            Assert.AreEqual(2, result.Calendars);
            Assert.IsTrue(result.Events);
            Assert.AreEqual(0, result.Skipped);
            Assert.IsTrue(File.Exists(JobIni(Advanced)));
            Assert.IsTrue(File.Exists(JobIni(Univariate)));
            Assert.IsTrue(File.Exists(Calendars));
            Assert.IsTrue(File.Exists(Path.Combine(_edit, IpmFiles.EventsXml)));
        }

        [TestMethod]
        public void Pull_ReadsBackUnchanged()
        {
            List<LocalChanges.Change> changes = IpmService.Scan(_cfg);
            Assert.AreEqual(0, changes.Count, TestData.Lines(changes.Select(c => $"{c.Relative}: {c.Reason}")));
            foreach (string f in Directory.EnumerateFiles(_edit, "*", SearchOption.AllDirectories))
            {
                IpmDeployPlan plan = Plan(f);
                Assert.AreEqual(0, plan.Changes.Count + plan.Notes.Count, $"{Path.GetFileName(f)}:" + TestData.Lines(plan.Changes.Concat(plan.Notes)));
            }
        }

        [TestMethod]
        public void AdvancedJob_FollowsTheWizardWithMemberNames()
        {
            string ini = Read(JobIni(Advanced));
            string[] order = { "[1 Types]", "[2 Calendar]", "[3 Historical Data]", "[3 Historical Data | Output | Revenue]",
                               "[3 Historical Data | Driver | Units]", "[3 Historical Data | Driver | Price]", "[4 Future Data]",
                               "[4 Future Data | Output | Revenue]", "[5 Prepare Data]", "[6 Model Settings]" };
            int at = -1;
            foreach (string section in order)
            {
                int next = ini.IndexOf(section, StringComparison.Ordinal);
                Assert.IsTrue(next > at, $"{section} is missing or out of order:\n{ini}");
                at = next;
            }
            StringAssert.Contains(ini, "Generate predictions = Advanced Predictions");
            StringAssert.Contains(ini, "Version = Final");             // Model Scope, one line per dimension
            StringAssert.Contains(ini, "Entity = North, South");       // member IDs as names
            StringAssert.Contains(ini, "Product = Product B");         // the future's own members
            StringAssert.Contains(ini, "Revenue | Missing values = zero");
            StringAssert.Contains(ini, "algorithms = Auto");
            Assert.IsFalse(ini.Contains("{id:"), "a member was not named:\n" + ini);
            Assert.IsFalse(ini.Contains("Best case"), "the scope's dimension names are not a setting");
        }

        [TestMethod]
        public void WithoutDimensions_MembersAreIdsAndStillReadBackUnchanged()
        {
            Directory.Delete(Path.Combine(_cfg.LocalFolderPath, ".epm"), recursive: true);
            WriteEditFiles(_cfg);
            StringAssert.Contains(Read(JobIni(Advanced)), "Account = {id:aaaaaaaa-0000-4000-8000-000000000001}");
            Assert.AreEqual(0, IpmService.Scan(_cfg).Count);
        }

        [TestMethod]
        public void LocalChangesOfTheCalculations_IgnoreTheIpmFolder()
        {
            Edit(JobIni(Advanced), "Historical periods = 24", "Historical periods = 30");
            Assert.AreEqual(0, LocalChanges.Scan(_cfg).Count);
            Assert.AreEqual(1, IpmService.Scan(_cfg).Count);
        }

        // ------------------------------------------------------------------ //
        //  Edit and deploy
        // ------------------------------------------------------------------ //

        [TestMethod]
        public void AdvancedJob_Edits()
        {
            string path = JobIni(Advanced);
            Edit(path, "[3 Historical Data | Output | Revenue]\nInput type = Cell Value\nCalendar = Budget Calendar\nAccount = Revenue\nEntity = North, South",
                       "[3 Historical Data | Output | Revenue]\nInput type = Cell Value\nCalendar = Budget Calendar\nAccount = Revenue\nEntity = North");
            // Remove the driver Units (both steps and its Prepare Data lines)
            string text = Read(path);
            foreach (string step in new[] { "3 Historical Data", "4 Future Data" })
            {
                int start = text.IndexOf($"[{step} | Driver | Units]", StringComparison.Ordinal);
                int end = text.IndexOf("\n[", start + 1, StringComparison.Ordinal);
                text = text.Remove(start, end + 1 - start);
            }
            text = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("Units | ")));
            // A new driver whose member has a comma in its name, historical only
            text = text.Replace("[4 Future Data]\n", "[3 Historical Data | Driver | Freight]\nCalendar = Budget Calendar\n" +
                                                     "Account = \"Cost, Freight\"\nEntity = No Entity\n\n[4 Future Data]\n");
            text = text.Replace("Price | Outliers = three_sigma", "Price | Outliers = none")
                       .Replace("Start = FY26->Jan", "Start = FY26->Mar")
                       .Replace("Scenario = Plan", "Scenario = Forecast");
            Write(path, text);

            IpmDeployPlan plan = Plan(path);
            string changes = TestData.Lines(plan.Changes);
            CollectionAssert.AreEquivalent(new[]
            {
                "[2 Calendar] Start: FY26->Jan → FY26->Mar",
                "[4 Future Data] Model Scope: Predicted, Periodic, Plan → Predicted, Periodic, Forecast",
                "removed driver 'Units'",
                "added driver 'Freight'",
                "[3 Historical Data | Output | Revenue] Entity: North, South → North",
                "[5 Prepare Data] Price | Outliers: three_sigma → none",
            }, plan.Changes, changes);

            JObject m = Mapping(plan.Xml);
            CollectionAssert.AreEqual(new[] { "Price", "Freight" }, m["features"]["inputFeatures"].Select(t => (string)t).ToArray());
            CollectionAssert.AreEqual(new[] { "Price", "Freight" }, m["forecastFeatures"]["inputFeatures"].Select(t => (string)t).ToArray());
            CollectionAssert.AreEqual(new[] { "eeeeeeee-0000-4000-8000-000000000001" }, Members(Feature(m, "features", "Revenue"), "Entity"));
            // The future copies a new driver's historical members
            JObject freight = Feature(m, "forecastFeatures", "Freight");
            CollectionAssert.AreEqual(new[] { "aaaaaaaa-0000-4000-8000-000000000004" }, Members(freight, "Account"));
            Assert.AreEqual("11111111-0000-4000-8000-000000000002", (string)freight["frequencyUuid"]);
            Assert.AreEqual("none", (string)Feature(m, "forecastFeatures", "Price")["outliers"]);
            XElement fitted = XDocument.Parse(plan.Xml).Root.Elements("SliceMapping")
                .Single(s => (string)s.Attribute("sliceDefType") == "Base prediction target slice");
            Assert.AreEqual("Predicted,Periodic,Forecast", fitted.Element("MbrSelection").Value);
        }

        [TestMethod]
        public void AdvancedJob_ProblemsAreAllReported()
        {
            string path = JobIni(Advanced);
            Edit(path, "Entity = North, South", "Entity = North, Atlantis");
            Edit(path, "Historical periods = 24", "Historical periods = many");
            Edit(path, "Generate predictions = Advanced Predictions", "Generate predictions = Auto Predict");
            Write(path, Read(path) + "\n[7 Extras]\nFoo = 1\n");

            var ex = Assert.ThrowsException<EpmException>(() => Plan(path));
            StringAssert.Contains(ex.Message, "'Atlantis' is not a member of Entity");
            StringAssert.Contains(ex.Message, "Historical periods must be a whole number");
            StringAssert.Contains(ex.Message, "switching to or from Advanced Predictions is done in EPM");
            StringAssert.Contains(ex.Message, "[7 Extras] is not a section");
        }

        [TestMethod]
        public void UnivariateJob_Edits()
        {
            string path = JobIni(Univariate);
            string ini = Read(path);
            StringAssert.Contains(ini, "Generate predictions = Auto Predict");
            StringAssert.Contains(ini, "Anomaly Insights = true");
            StringAssert.Contains(ini, "Members = Actual, Final, Periodic, ILvl0Descendants(Total Revenue)");

            Edit(path, "Generate predictions = Auto Predict", "Generate predictions = None");
            Edit(path, "Prediction Insights = false", "Prediction Insights = true");
            Edit(path, "fillInMissingValues = true", "fillInMissingValues = false");

            IpmDeployPlan plan = Plan(path);
            Assert.AreEqual(3, plan.Changes.Count, TestData.Lines(plan.Changes));
            XElement root = XDocument.Parse(plan.Xml).Root;
            Assert.AreEqual("false", (string)root.Attribute("isAutoPredictJob"));
            Assert.AreEqual("true", (string)root.Attribute("isPredictionInsightJob"));
            Assert.IsNotNull(root.Element("InsightResource"), "settings the .ini doesn't show are kept");
            Assert.AreEqual(false, (bool)JObject.Parse(root.Element("APPredictionOptions").Value)["fillInMissingValues"]);
        }

        [TestMethod]
        public void Calendars_EditAddAndKeepRemoved()
        {
            string text = Read(Calendars);
            int forecast = text.IndexOf("[Forecast Calendar]", StringComparison.Ordinal);
            int next = text.IndexOf("\n[", forecast + 1, StringComparison.Ordinal);
            text = text.Remove(forecast, next + 1 - forecast)                       // left out: kept as it is
                       .Replace("Future periods = 12", "Future periods = 18")
                       + "\n[Quarterly]\nCubes = Plan1\nMembers = ILvl0Descendants(Years)->ILvl0Descendants(YearTotal)\n" +
                         "Start = FY25->Jan\nCurrent = FY26->Jan\nEnd = FY27->Dec\nHistorical periods = 12\nFuture periods = 24\n" +
                         "Date mapping frequency = 3\nDimensions = Years, Period\n";
            Write(Calendars, text);

            IpmDeployPlan plan = Plan(Calendars);
            CollectionAssert.AreEquivalent(new[] { "[Budget Calendar] Future periods: 12 → 18", "new calendar [Quarterly]" },
                                           plan.Changes, TestData.Lines(plan.Changes));
            Assert.AreEqual(1, plan.Notes.Count, "the calendar left out is noted");

            List<IpmCalendar> after = IpmCalendarIni.Read(plan.Xml);
            CollectionAssert.AreEquivalent(new[] { "Budget Calendar", "Quarterly", "Forecast Calendar" }, after.Select(c => c.Name).ToArray());
            Assert.AreEqual("11111111-0000-4000-8000-000000000002", after.Single(c => c.Name == "Budget Calendar").Id, "a calendar keeps its ID");
            Assert.IsTrue(Guid.TryParse(after.Single(c => c.Name == "Quarterly").Id, out _), "a new calendar gets an ID");
        }

        [TestMethod]
        public void Package_HoldsOnlyPlanningAndTheOneArtifact()
        {
            string path = JobIni(Advanced);
            Edit(path, "Future periods = 12", "Future periods = 6");
            IpmDeployPlan plan = Plan(path);
            byte[] zip = IpmService.BuildPackage(_cfg, plan);

            using (var archive = new ZipArchive(new MemoryStream(zip)))
            {
                string Entry(string name)
                {
                    using (var r = new StreamReader(archive.GetEntry(name).Open(), Encoding.UTF8)) return r.ReadToEnd();
                }
                CollectionAssert.AreEquivalent(new[]
                {
                    "Export.xml", "Import.xml", "HP-Vision/Import.xml", "HP-Vision/info/listing.xml", "HP-Vision/info/sourceInfo.xml",
                    "HP-Vision/resource/Global Artifacts/Auto Predict/Revenue - Advanced.xml",
                }, archive.Entries.Select(e => e.FullName).ToArray());

                foreach (string manifest in new[] { "Import.xml", "HP-Vision/Import.xml", "Export.xml" })
                {
                    XElement artifact = XDocument.Parse(Entry(manifest)).Descendants("Artifact").Single();
                    Assert.AreEqual("/Global Artifacts/Auto Predict", (string)artifact.Attribute("parentPath"), manifest);
                    Assert.AreEqual(Advanced, (string)artifact.Attribute("pattern"), manifest);
                }
                XElement listing = XDocument.Parse(Entry("HP-Vision/info/listing.xml")).Root;
                Assert.AreEqual(Advanced, (string)listing.Elements("resource").Single().Attribute("name"));
                Assert.AreEqual(plan.Xml, Entry("HP-Vision/resource/Global Artifacts/Auto Predict/Revenue - Advanced.xml"));
            }
        }

        [TestMethod]
        public void MemberLines_ForAutocomplete()
        {
            Assert.IsTrue(AutoPredictIni.IsMemberLine("3 Historical Data | Output | Revenue", "Entity", out string dim));
            Assert.AreEqual("Entity", dim);
            Assert.IsFalse(AutoPredictIni.IsMemberLine("3 Historical Data | Output | Revenue", "Calendar", out _));
            Assert.IsTrue(AutoPredictIni.IsMemberLine("4 Future Data", "Version", out dim));
            Assert.AreEqual("Version", dim);
            Assert.IsTrue(AutoPredictIni.IsMemberLine("3 Historical Data", "Members", out dim));
            Assert.IsNull(dim, "a member list takes any dimension's members");
            Assert.IsFalse(AutoPredictIni.IsMemberLine("6 Model Settings", "algorithms", out _));
        }

        // ------------------------------------------------------------------ //
        //  A whole IPM export (CALCSTUDIO_TEST_IPM)
        // ------------------------------------------------------------------ //

        /// <summary>Every job, the calendars and events of a real IPM export read back unchanged.
        /// A dimensions.json beside the export (or in its .epm folder) names the members.</summary>
        [TestMethod, TestCategory("Corpus")]
        public void Corpus_ReadsBackUnchanged()
        {
            string source = TestData.Corpus(TestData.IpmCorpusVariable);
            string local = TestData.NewFolder();
            string pkg = IpmFiles.PackageDir(local);
            if (File.Exists(source)) ZipFile.ExtractToDirectory(source, pkg);
            else TestData.CopyFolder(source, pkg);
            string folder = File.Exists(source) ? Path.GetDirectoryName(source) : source;
            foreach (string dims in new[] { Path.Combine(folder, "dimensions.json"), Path.Combine(folder, ".epm", "dimensions.json") })
                if (File.Exists(dims))
                {
                    Directory.CreateDirectory(Path.Combine(local, ".epm"));
                    File.Copy(dims, Path.Combine(local, ".epm", "dimensions.json"));
                    break;
                }
            string app = Path.GetFileName(IpmFiles.HpDir(local) ?? "HP-?").Substring(3);
            var cfg = TestData.Connection(local, "Corpus", app);

            var result = new IpmPullResult();
            WriteEditFiles(cfg, result);
            Assert.AreEqual(0, result.Skipped, "jobs that could not be written as .ini");
            Assert.IsTrue(result.AutoPredictJobs + result.Calendars > 0, "no IPM artifacts in the corpus");
            List<LocalChanges.Change> changes = IpmService.Scan(cfg);
            Assert.AreEqual(0, changes.Count, TestData.Lines(changes.Select(c => $"{c.Relative}: {c.Reason}")));
        }
    }
}
