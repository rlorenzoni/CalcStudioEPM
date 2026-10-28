using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CalcStudioEPM.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalcStudioEPM.Tests
{
    /// <summary>
    /// The fixtures, copied to a fresh temporary connection folder per test (the code under test
    /// writes into it), and the optional whole-pull corpora named by environment variables.
    /// </summary>
    internal static class TestData
    {
        /// <summary>A folder holding a package already downloaded (its CalcManager export, e.g. of
        /// Oracle's demo application): the Corpus tests re-extract all of it — no connection is
        /// used. Not set: they are skipped.</summary>
        public const string CorpusVariable = "CALCSTUDIO_TEST_DATA";

        /// <summary>An IPM export (.zip, or a folder holding HP-{app}), optionally with a
        /// dimensions.json beside it: the IPM Corpus test reads all of it back.</summary>
        public const string IpmCorpusVariable = "CALCSTUDIO_TEST_IPM";

        private static readonly List<string> Created = new List<string>();

        /// <summary>…\CalcStudioEPM.Tests\Fixtures, found from the test assembly upwards.</summary>
        public static string FixturesDir
        {
            get
            {
                for (string dir = AppDomain.CurrentDomain.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
                {
                    string f = Path.Combine(dir, "Fixtures");
                    if (File.Exists(Path.Combine(dir, "CalcStudioEPM.Tests.csproj")) && Directory.Exists(f)) return f;
                }
                throw new InvalidOperationException("The Fixtures folder of CalcStudioEPM.Tests was not found.");
            }
        }

        /// <summary>A new empty folder, removed by <see cref="CleanUp"/>.</summary>
        public static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "CalcStudioEPM.Tests", Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            lock (Created) Created.Add(dir);
            return dir;
        }

        /// <summary>A copy of Fixtures\{name} in a new folder.</summary>
        public static string CopyFixture(string name)
        {
            string dir = NewFolder();
            CopyFolder(Path.Combine(FixturesDir, name), dir);
            return dir;
        }

        public static void CopyFolder(string from, string to, Func<string, bool> skipDir = null)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                string target = Path.Combine(to, Path.GetFileName(f));
                File.Copy(f, target, overwrite: true);
                File.SetAttributes(target, FileAttributes.Normal);
            }
            foreach (string d in Directory.GetDirectories(from))
                if (skipDir == null || !skipDir(d)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)), skipDir);
        }

        public static ConnectionConfig Connection(string localFolder, string name, string application) =>
            new ConnectionConfig { Name = name, Application = application, LocalFolderPath = localFolder, Username = "demo.user" };

        /// <summary>The environment variable's folder or file; the test is skipped when it isn't set.</summary>
        public static string Corpus(string variable)
        {
            string path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive($"{variable} is not set: no corpus to run on.");
            if (!File.Exists(path) && !Directory.Exists(path)) Assert.Fail($"{variable} = '{path}' does not exist.");
            return path;
        }

        public static void CleanUp()
        {
            lock (Created)
            {
                foreach (string dir in Created)
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            File.SetAttributes(f, FileAttributes.Normal);
                        Directory.Delete(dir, recursive: true);
                    }
                    catch { /* a locked file: the temp folder is cleaned up later */ }
                Created.Clear();
            }
        }

        /// <summary>One line per item, for assertion messages.</summary>
        public static string Lines<T>(IEnumerable<T> items) => "\n  " + string.Join("\n  ", items.Select(i => i?.ToString()));
    }

    [TestClass]
    public class AssemblyHooks
    {
        [AssemblyCleanup]
        public static void CleanUp() => TestData.CleanUp();
    }
}
