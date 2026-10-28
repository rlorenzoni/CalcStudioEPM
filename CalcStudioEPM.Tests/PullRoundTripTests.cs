using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalcStudioEPM.Tests
{
    /// <summary>
    /// A pull writes every rule, template, script and RuleSet to the scripts folder; reading those
    /// files back (what Deploy and the pull's "saved changes" check do) must find nothing changed.
    /// Any difference means a file the user didn't touch would be reported, or deployed, as edited.
    /// </summary>
    [TestClass]
    public class PullRoundTripTests
    {
        private static (ConnectionConfig Cfg, string Scripts, (int Copied, int Skipped, string ScriptsDir, int Variables) Result) Extract(string local)
        {
            ConnectionConfig cfg = TestData.Connection(local, "Demo", "EPBCS");
            var log = new List<string>();
            var result = CalculationParser.ExtractScripts(local, cfg.Name, new Progress(log));
            if (result.Skipped > 0)
                Assert.Fail($"{result.Skipped} artifact(s) skipped:" + TestData.Lines(log.Where(l => l.Contains("[WARN]") || l.Contains("kipp"))));
            return (cfg, PathConstants.GetScriptsRootPath(local, cfg.Name), result);
        }

        private sealed class Progress : IProgress<string>
        {
            private readonly List<string> _lines;
            public Progress(List<string> lines) { _lines = lines; }
            public void Report(string value) { lock (_lines) _lines.Add(value); }
        }

        private static void AssertNothingChanged(ConnectionConfig cfg)
        {
            List<LocalChanges.Change> changes = LocalChanges.Scan(cfg);
            Assert.AreEqual(0, changes.Count, "Files read back as changed right after the pull:" +
                TestData.Lines(changes.Select(c => $"{c.Relative}: {c.Reason}")));
        }

        [TestMethod]
        public void DemoFixture_EveryKindIsExtracted()
        {
            string local = TestData.CopyFixture("Demo");
            var (_, scripts, result) = Extract(local);

            Assert.AreEqual(0, result.Skipped, "artifacts skipped");
            string app = Path.Combine(scripts, "Planning", "EPBCS");
            foreach (string expected in new[]
            {
                @"Plan1\Rules\DataExport_Test.csc",                 // Calc Script rule
                @"OEP_FS\Rules\I%2FC Eliminations.csc",             // "/" in the rule's name
                @"OEP_FS\Rules\Planning to SM.js",                  // Groovy rule
                @"Plan1\Rules\Clear Actuals\" + GraphicalRule.ManifestName,
                @"Plan1\Rules\Clear Actuals\" + GraphicalRule.RuleIniName,
                @"Plan1\Rules\Nested Branches\" + GraphicalRule.ManifestName,   // made up: steps up to 12 IFs deep
                @"Plan1\Rules\Nested Branches\01 1-THEN 01 - Level 1 step.csc",
                @"Plan1\Rules\Empty Rule.csc",                                  // made up: an empty script
                @"RuleSets\Update Rates" + RuleSet.IniSuffix,
            })
                Assert.IsTrue(File.Exists(Path.Combine(app, expected)), $"{expected} was not written");
        }

        [TestMethod]
        public void DemoFixture_ReadsBackUnchanged()
        {
            var (cfg, _, _) = Extract(TestData.CopyFixture("Demo"));
            AssertNothingChanged(cfg);
        }

        [TestMethod]
        public void EditedScript_IsReportedAsChanged()
        {
            var (cfg, scripts, _) = Extract(TestData.CopyFixture("Demo"));
            string file = Path.Combine(scripts, @"Planning\EPBCS\Plan1\Rules\DataExport_Test.csc");
            File.AppendAllText(file, "\n/* edited */\n", Encoding.UTF8);

            List<LocalChanges.Change> changes = LocalChanges.Scan(cfg);
            Assert.AreEqual(1, changes.Count, TestData.Lines(changes.Select(c => c.Relative)));
            Assert.AreEqual("changed", changes[0].Reason);
            StringAssert.EndsWith(changes[0].Relative, "DataExport_Test.csc");
        }

        [TestMethod]
        public void EditedRuleSetIni_IsReportedAsChanged()
        {
            var (cfg, scripts, _) = Extract(TestData.CopyFixture("Demo"));
            string ini = Path.Combine(scripts, @"Planning\EPBCS\RuleSets\Update Rates" + RuleSet.IniSuffix);
            string text = File.ReadAllText(ini, Encoding.UTF8);
            string edited = text.Contains("Run in parallel = false")
                ? text.Replace("Run in parallel = false", "Run in parallel = true")
                : text.Replace("Run in parallel = true", "Run in parallel = false");
            Assert.AreNotEqual(text, edited, "the RuleSet .ini has no 'Run in parallel' line");
            File.WriteAllText(ini, edited, new UTF8Encoding(false));

            List<LocalChanges.Change> changes = LocalChanges.Scan(cfg);
            Assert.AreEqual(1, changes.Count, TestData.Lines(changes.Select(c => c.Relative)));
            StringAssert.EndsWith(changes[0].Relative, RuleSet.IniSuffix);
        }

        private const string Nested = @"Planning\EPBCS\Plan1\Rules\Nested Branches";

        /// <summary>
        /// Nested Branches: IF › ELSE › IF … 12 deep, a script step in every THEN. In a long
        /// connection folder the deepest step's number alone (~120 characters) would pass
        /// Windows' 260-character limit: it is written short, the shallow steps keep their full
        /// names, nothing is skipped, everything reads back unchanged and an edit is still found.
        /// </summary>
        [TestMethod]
        public void DeepSteps_InALongFolder_AreWrittenShortAndReadBack()
        {
            string fixture = TestData.CopyFixture("Demo");
            string local = Path.Combine(fixture, new string('x', Math.Max(1, 125 - fixture.Length - 1)));
            TestData.CopyFolder(Path.Combine(fixture, PathConstants.CalcManagerDirName), Path.Combine(local, PathConstants.CalcManagerDirName));
            var (cfg, scripts, _) = Extract(local);   // fails on any skipped artifact

            string[] steps = Directory.GetFiles(Path.Combine(scripts, Nested), "*.csc").Select(Path.GetFileName).ToArray();
            Assert.AreEqual(12, steps.Length, TestData.Lines(steps));
            CollectionAssert.Contains(steps, "01 1-THEN 01 - Level 1 step.csc");
            // The short number, with as much of the caption as fits (here none)
            string deepest = steps.Single(s => s.StartsWith("01-E01-E01-E01-E01-E01-E01-E01-E01-E01-E01-E01-T01"));
            foreach (string f in Directory.EnumerateFiles(scripts, "*", SearchOption.AllDirectories))
                Assert.IsTrue(f.Length < 260, $"{f.Length} characters: {f}");
            AssertNothingChanged(cfg);

            string path = Path.Combine(scripts, Nested, deepest);
            File.WriteAllText(path, File.ReadAllText(path).Replace("CLEARDATA", "CLEARBLOCK ALL; CLEARDATA"));
            List<LocalChanges.Change> changes = LocalChanges.Scan(cfg);
            Assert.AreEqual(1, changes.Count, TestData.Lines(changes.Select(c => c.Relative)));
            Assert.AreEqual(deepest, Path.GetFileName(changes[0].File));
        }

        /// <summary>A rule whose script was never written (an empty &lt;script/&gt;) is an empty
        /// .csc: unchanged after the pull, changed once something is written in it.</summary>
        [TestMethod]
        public void EmptyRule_ReadsBackUnchangedUntilWritten()
        {
            var (cfg, scripts, _) = Extract(TestData.CopyFixture("Demo"));
            string file = Path.Combine(scripts, @"Planning\EPBCS\Plan1\Rules\Empty Rule.csc");
            Assert.AreEqual("", File.ReadAllText(file));
            AssertNothingChanged(cfg);

            File.WriteAllText(file, "CLEARDATA \"OEP_Plan\";\n");
            Assert.AreEqual("Empty Rule.csc", Path.GetFileName(LocalChanges.Scan(cfg).Single().File));
        }

        [TestMethod]
        public void CalculationsPull_LeavesTheIpmFolderAlone()
        {
            string local = TestData.CopyFixture("Demo");
            string ipm = Path.Combine(PathConstants.GetScriptsRootPath(local, "Demo"), PathConstants.IpmDirName, "Auto Predict");
            Directory.CreateDirectory(ipm);
            string mine = Path.Combine(ipm, "Job" + IpmFiles.AutoPredictSuffix);
            File.WriteAllText(mine, "[1 Types]\n");

            var (cfg, _, _) = Extract(local);

            Assert.IsTrue(File.Exists(mine), "the pull of the calculations removed the IPM folder's files");
            AssertNothingChanged(cfg);   // and the calculations' check doesn't report them
        }

        /// <summary>Every artifact of a whole pull (CALCSTUDIO_TEST_DATA), extracted again with this code.</summary>
        [TestMethod, TestCategory("Corpus")]
        public void Corpus_ReadsBackUnchanged()
        {
            string source = TestData.Corpus(TestData.CorpusVariable);
            string local = TestData.NewFolder();
            // The pulled export only: the scripts are written again by this code
            TestData.CopyFolder(Path.Combine(source, PathConstants.CalcManagerDirName), Path.Combine(local, PathConstants.CalcManagerDirName),
                                skipDir: d => d.EndsWith(".Scripts", StringComparison.OrdinalIgnoreCase));
            var (cfg, _, result) = Extract(local);

            Assert.AreEqual(0, result.Skipped, "artifacts skipped");
            Assert.IsTrue(result.Copied > 0, "nothing extracted");
            AssertNothingChanged(cfg);
        }
    }
}
