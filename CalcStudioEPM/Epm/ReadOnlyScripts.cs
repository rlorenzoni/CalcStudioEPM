using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// The "read only" side of a connection: its pulled files (everything under
    /// {LocalFolder}\CalcManager) carry the read-only file attribute, so Notepad++ opens them
    /// locked, and Deploy is disabled for it. The attribute is cleared again before a pull
    /// replaces the folder, and when the connection stops being read only.
    /// </summary>
    public static class ReadOnlyScripts
    {
        public static string PulledDir(ConnectionConfig cfg) =>
            string.IsNullOrWhiteSpace(cfg?.LocalFolderPath) ? null
                : Path.Combine(cfg.LocalFolderPath, PathConstants.CalcManagerDirName);

        /// <summary>Sets or clears the read-only attribute of every file under <paramref name="dir"/>;
        /// returns how many files were changed. Files that can't be changed are skipped.</summary>
        public static int Apply(string dir, bool readOnly)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            int changed = 0;
            Func<string, bool> alwaysReadOnly = GraphicalRule.AlwaysReadOnly();   // templates, formula steps the format can't hold
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!readOnly && alwaysReadOnly(file)) continue;
                    FileAttributes a = File.GetAttributes(file);
                    FileAttributes b = readOnly ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly;
                    if (a == b) continue;
                    File.SetAttributes(file, b);
                    changed++;
                }
                catch { /* in use or no access: leave it */ }
            }
            return changed;
        }

        /// <summary>
        /// Empties a folder (read-only files included) but keeps the folder itself, so
        /// Explorer windows, Notepad++'s Folder as Workspace and anything else open on it stay
        /// valid. Entries in <paramref name="keep"/> (full paths of files or folders) are left.
        /// </summary>
        public static void ClearDirectory(string dir, params string[] keep)
        {
            if (!Directory.Exists(dir)) return;
            bool Kept(string path) => keep != null && Array.Exists(keep, k =>
                string.Equals(Path.GetFullPath(k).TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            foreach (string sub in Directory.GetDirectories(dir))
                if (!Kept(sub)) DeleteDirectory(sub);
            foreach (string file in Directory.GetFiles(dir))
            {
                if (Kept(file)) continue;
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }
        }

        /// <summary>
        /// Deletes every file under <paramref name="dir"/> (read-only ones included) but keeps
        /// the whole folder tree, so Notepad++'s Folder as Workspace and Explorer only see files
        /// change. Files in <paramref name="keep"/> (full paths) stay. Pair with
        /// <see cref="DeleteEmptyFolders"/> once the new files are written.
        /// </summary>
        public static void ClearFiles(string dir, params string[] keep)
        {
            if (!Directory.Exists(dir)) return;
            var kept = new HashSet<string>((keep ?? new string[0]).Select(k => Path.GetFullPath(k)), StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
            {
                if (kept.Contains(Path.GetFullPath(file))) continue;
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }
        }

        /// <summary>Deletes the folders under <paramref name="dir"/> left without any file (deepest first); keeps <paramref name="dir"/>.</summary>
        public static int DeleteEmptyFolders(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            int deleted = 0;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                deleted += DeleteEmptyFolders(sub);
                if (!Directory.EnumerateFileSystemEntries(sub).Any())
                {
                    try { Directory.Delete(sub); deleted++; } catch { /* in use: leave it */ }
                }
            }
            return deleted;
        }

        /// <summary>Deletes a folder that may hold read-only files (Directory.Delete refuses them).</summary>
        public static void DeleteDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;
            Apply(dir, readOnly: false);
            Directory.Delete(dir, recursive: true);
        }
    }
}
