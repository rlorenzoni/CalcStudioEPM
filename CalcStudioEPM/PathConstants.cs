using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CalcStudioEPM
{
    /// <summary>
    /// Central repository for directory and file-name constants that are shared
    /// across <see cref="Main"/>, <see cref="Config.ConfigManager"/>,
    /// <see cref="Epm.SnapshotService"/>, and <see cref="Epm.CalculationParser"/>.
    /// A single place to update if the folder structure ever changes.
    /// </summary>
    internal static class PathConstants
    {
        /// <summary>Hidden sub-directory used for plugin data (config, logs).</summary>
        public const string EpmDirName = ".epm";

        /// <summary>Directory created under LocalFolderPath to hold the extracted export.</summary>
        public const string CalcManagerDirName = "CalcManager";

        /// <summary>
        /// Fallback scripts directory name used when a connection has no name.
        /// Named connections use <see cref="GetScriptsDirName"/> instead.
        /// </summary>
        public const string ScriptsDirName = "scripts";

        /// <summary>
        /// Variable dictionary written by Pull next to the extracted export:
        /// <c>{LocalFolderPath}\CalcManager\variables.json</c>.
        /// </summary>
        public const string VariablesFileName = "variables.json";

        /// <summary>Top-level Planning application folder name inside the scripts root.</summary>
        public const string PlanningDirName = "Planning";

        /// <summary>
        /// IPM (Auto Predict jobs, calendars, events): the folder of its pulled package —
        /// <c>{LocalFolderPath}\IPM</c>, apart from Calculation Manager's export — and the folder
        /// of its files to edit inside the scripts root, <c>{name}.Scripts\IPM</c>.
        /// </summary>
        public const string IpmDirName = "IPM";

        /// <summary>
        /// Sentinel file created once inside the scripts root so the
        /// "Open Containing Folder as Workspace" NPP command can open
        /// that exact directory without any dialog automation.
        /// </summary>
        public const string WorkspaceSentinelName = ".epm_workspace";

        // ------------------------------------------------------------------ //
        //  Dynamic helpers (depend on the connection name at runtime)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns the scripts directory name for a connection: <c>"{name}.Scripts"</c>.
        /// Characters that are invalid in Windows folder names are replaced with underscores.
        /// Falls back to <see cref="ScriptsDirName"/> when <paramref name="connectionName"/>
        /// is blank (legacy / unnamed connections).
        /// </summary>
        public static string GetScriptsDirName(string connectionName)
        {
            if (string.IsNullOrWhiteSpace(connectionName))
                return ScriptsDirName;

            // Replace the characters Windows does not allow in file/folder names
            string safe = connectionName
                .Replace('\\', '_').Replace('/', '_').Replace(':', '_')
                .Replace('*',  '_').Replace('?',  '_').Replace('"', '_')
                .Replace('<',  '_').Replace('>',  '_').Replace('|', '_')
                .Trim();

            return (safe.Length > 0 ? safe : ScriptsDirName) + ".Scripts";
        }

        /// <summary>
        /// Returns the full path to the scripts root folder for a connection:
        /// <c>{localFolder}\CalcManager\{name}.Scripts</c>.
        /// </summary>
        public static string GetScriptsRootPath(string localFolderPath, string connectionName)
            => Path.Combine(localFolderPath, CalcManagerDirName, GetScriptsDirName(connectionName));

        // ------------------------------------------------------------------ //
        //  Artifact name ↔ file name
        // ------------------------------------------------------------------ //

        // Characters EPM allows in artifact names but Windows forbids in file names.
        private static readonly char[] InvalidFileNameChars =
            { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        private static readonly Regex EncodedFileNameChar =
            new Regex("%(2F|5C|3A|2A|3F|22|3C|3E|7C)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Converts an EPM artifact name to the file name used on disk. EPM's own export
        /// percent-encodes characters Windows forbids — "FCST/BUD - Online - DAB" is
        /// written as "FCST%2FBUD - Online - DAB" — and we use the same encoding for the
        /// extracted scripts so a file name always maps back to exactly one rule name.
        /// </summary>
        public static string ToFileName(string artifactName)
        {
            // "." and ".." would name the folder itself or its parent
            if (artifactName.Trim('.').Length == 0) return artifactName.Replace(".", "%2E");

            var sb = new StringBuilder(artifactName.Length);
            foreach (char ch in artifactName)
            {
                if (Array.IndexOf(InvalidFileNameChars, ch) >= 0)
                    sb.Append('%').Append(((int)ch).ToString("X2"));
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reverses <see cref="ToFileName"/>. Only the escapes ToFileName produces are
        /// decoded, so a name that genuinely contains e.g. "%20" is left untouched.
        /// </summary>
        public static string FromFileName(string fileName) =>
            EncodedFileNameChar.Replace(fileName,
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        /// <summary>
        /// True when <paramref name="relative"/> (a path from an EPM export, e.g.
        /// Planning\Vision\Plan1\Rules) stays inside <paramref name="root"/>: not rooted, no
        /// drive or UNC part, no "." / ".." segments, and still under root once resolved.
        /// </summary>
        public static bool IsSafeRelativePath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || relative.IndexOf(':') >= 0)
                return false;
            foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (part.Length == 0 || part.Trim('.').Length == 0) return false;

            string full = Path.GetFullPath(Path.Combine(root, relative));
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
    }
}
