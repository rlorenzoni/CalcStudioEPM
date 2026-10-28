using System.Collections.Generic;
using System.IO;
using System.Linq;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;
using CalcStudioEPM.Epm.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalcStudioEPM.Tests
{
    [TestClass]
    public class PathConstantsTests
    {
        [DataTestMethod]
        [DataRow("FCST/BUD - Online - DAB", "FCST%2FBUD - Online - DAB")]
        [DataRow("I/C Eliminations", "I%2FC Eliminations")]
        [DataRow("a:b*c?d\"e<f>g|h\\i", "a%3Ab%2Ac%3Fd%22e%3Cf%3Eg%7Ch%5Ci")]
        [DataRow("Plain name", "Plain name")]
        public void FileNames_RoundTrip(string artifact, string file)
        {
            Assert.AreEqual(file, PathConstants.ToFileName(artifact));
            Assert.AreEqual(artifact, PathConstants.FromFileName(file));
        }

        [TestMethod]
        public void FileNames_OtherEscapesAreLeftAlone() =>
            Assert.AreEqual("Rate%20Table", PathConstants.FromFileName("Rate%20Table"));

        [TestMethod]
        public void FileNames_DotsAloneAreEncoded() =>
            Assert.AreEqual("%2E%2E", PathConstants.ToFileName(".."));

        [DataTestMethod]
        [DataRow(@"Planning\Vision\Plan1\Rules", true)]
        [DataRow(@"..\outside", false)]
        [DataRow(@"Planning\..\..\outside", false)]
        [DataRow(@"C:\Windows", false)]
        [DataRow(@"\\server\share", false)]
        [DataRow(@"Planning\.\Rules", false)]
        [DataRow("", false)]
        public void SafeRelativePaths(string relative, bool safe) =>
            Assert.AreEqual(safe, PathConstants.IsSafeRelativePath(Path.GetTempPath(), relative));

        [TestMethod]
        public void ScriptsFolderNames() =>
            Assert.AreEqual("Prod_ EU.Scripts", PathConstants.GetScriptsDirName("Prod: EU"));
    }

    [TestClass]
    public class IpmListTests
    {
        [TestMethod]
        public void Split_KeepsFunctionsQuotesAndBrackets()
        {
            CollectionAssert.AreEqual(
                new[] { "Final", "@Relative(Total Entity, 0)", "\"Cost, Freight\"", "[CC_1].[CC_2]", "No Product" },
                IpmList.Split("Final, @Relative(Total Entity, 0), \"Cost, Freight\", [CC_1].[CC_2],No Product,").ToArray());
        }

        [TestMethod]
        public void Quote_OnlyWhenNeeded()
        {
            Assert.AreEqual("North", IpmList.Quote("North"));
            Assert.AreEqual("\"Cost, Freight\"", IpmList.Quote("Cost, Freight"));
            Assert.AreEqual("Say \"hi\"", IpmList.Unquote(IpmList.Quote("Say \"hi\"")));
        }

        [DataTestMethod]
        [DataRow("FY26->Sep", "FY26", "Sep")]
        [DataRow("&Budget_Year -> Jan", "&Budget_Year", "Jan")]
        public void Periods(string text, string year, string period)
        {
            Assert.IsTrue(IpmList.SplitPeriod(text, out string y, out string p));
            Assert.AreEqual(year, y);
            Assert.AreEqual(period, p);
        }

        [TestMethod]
        public void Periods_NeedBothParts() => Assert.IsFalse(IpmList.SplitPeriod("FY26", out _, out _));
    }

    /// <summary>The validators with their built-in settings (not the user's %AppData% files).</summary>
    [TestClass]
    public class ValidatorTests
    {
        private static List<ValidationIssue> Calc(string script) =>
            CalcScriptValidator.Validate(script, CalcScriptCatalog.Defaults());

        private static List<ValidationIssue> Groovy(string script) =>
            GroovyValidator.Validate(script, GroovyValidationConfig.Defaults());

        private static string Ids(IEnumerable<ValidationIssue> issues) =>
            TestData.Lines(issues.Select(i => $"{i.RuleId} ({i.Severity}) line {i.Line}: {i.Message}"));

        [TestMethod]
        public void Calc_CleanScriptHasNoErrors()
        {
            var issues = Calc("SET UPDATECALC OFF;\nFIX(\"Actual\", \"Working\")\n  \"Revenue\" = \"Units\" * \"Price\";\nENDFIX\n");
            Assert.IsFalse(issues.Any(i => i.IsError), Ids(issues));
        }

        [TestMethod]
        public void Calc_UnclosedFix() =>
            AssertReports(Calc("FIX(\"Actual\")\n  \"Revenue\" = 1;\n"), "block.unbalanced");

        [TestMethod]
        public void Calc_UnbalancedBrackets() =>
            AssertReports(Calc("FIX(\"Actual\"\n  \"Revenue\" = 1;\nENDFIX\n"), "syntax.unbalanced-brackets");

        [TestMethod]
        public void Calc_UnterminatedString() =>
            AssertReports(Calc("FIX(\"Actual)\nENDFIX\n"), "syntax.unterminated-string");

        [TestMethod]
        public void Calc_UnknownFunction() =>
            AssertReports(Calc("FIX(@NOSUCHFUNCTION(\"Entity\"))\n  \"Revenue\" = 1;\nENDFIX\n"), "function.unknown");

        [TestMethod]
        public void Groovy_CleanScriptHasNoErrors()
        {
            var issues = Groovy("def cube = operation.application.getCube(\"Plan1\")\nprintln \"cube: ${cube.name}\"\n");
            Assert.IsFalse(issues.Any(i => i.IsError), Ids(issues));
        }

        [TestMethod]
        public void Groovy_UnbalancedBrackets() =>
            AssertReports(Groovy("if (true) {\n  println 'x'\n"), "syntax.unbalanced-brackets");

        /// <summary>Every script of the DEMO fixture validates without the validator failing.</summary>
        [TestMethod]
        public void DemoScripts_ValidateWithoutFailing()
        {
            string local = TestData.CopyFixture("Demo");
            CalculationParser.ExtractScripts(local, "Demo");
            string scripts = PathConstants.GetScriptsRootPath(local, "Demo");
            int n = 0;
            foreach (string f in Directory.EnumerateFiles(scripts, "*.*", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(f);
                if (f.EndsWith(".csc")) { Calc(text); n++; }
                else if (f.EndsWith(".js")) { Groovy(text); n++; }
            }
            Assert.IsTrue(n >= 5, $"only {n} scripts validated");
        }

        private static void AssertReports(List<ValidationIssue> issues, string ruleId) =>
            Assert.IsTrue(issues.Any(i => i.RuleId == ruleId), $"expected {ruleId}, got:" + Ids(issues));
    }
}
