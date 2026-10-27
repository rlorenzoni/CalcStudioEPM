using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using NppPluginNET;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;
using CalcStudioEPM.Epm.Validation;
using CalcStudioEPM.Forms;
using CalcStudioEPM.Resources;

namespace CalcStudioEPM
{
    /// <summary>
    /// Plugin entry point. Registers three toolbar commands and wires up all functionality.
    /// </summary>
    public static class Main
    {
        // ------------------------------------------------------------------ //
        //  Plugin identity
        // ------------------------------------------------------------------ //

        public const string PluginName = "CalcStudio for Oracle EPM";

        // Win32 MAX_PATH — buffer size for path strings sent via NPP messages
        private const int MaxPath = 260;

        // ------------------------------------------------------------------ //
        //  Delegate storage — must be kept alive to prevent GC collection
        // ------------------------------------------------------------------ //

        private static Action _configDelegate;
        private static Action _pullDelegate;
        private static Action _executeDelegate;
        private static Action _workspaceDelegate;
        private static Action _deployDelegate;
        private static Action _variablesDelegate;
        private static Action _validateDelegate;
        private static Action _settingsDelegate;
        private static Action _navigatorDelegate;
        private static Action _aboutDelegate;
        private static DimensionNavigatorForm _navigator;   // modeless, one at a time

        // ------------------------------------------------------------------ //
        //  Status bar — connection name display
        // ------------------------------------------------------------------ //

        // NPPM_SETSTATUSBAR section index for the language / doc-type field
        private const int StatusBarDocType = 0;

        /// <summary>
        /// Set to true once NPPN_READY fires — the signal that NPP's toolbar,
        /// status bar and UI are fully constructed.
        /// We must never call NPPM_SETSTATUSBAR or other NPP API messages
        /// before this flag is set, because NPP's internal state is not yet
        /// ready to handle them and NPP will crash inside SendMessage.
        /// </summary>
        private static bool _nppReady = false;

        /// <summary>Guards against double-registration if NPPN_TBMODIFICATION is resent.</summary>
        private static bool _iconsRegistered = false;

        /// <summary>
        /// Last folder path matched by auto-detection so we avoid a disk write
        /// to connections.json on every single tab switch.
        /// </summary>
        private static string _lastAutoDetectedFolder;

        // ------------------------------------------------------------------ //
        //  Plugin lifecycle
        // ------------------------------------------------------------------ //

        /// <summary>Called by setInfo — registers menu commands.</summary>
        public static void CommandMenuInit()
        {
            // Each command is wrapped in SafeExecute: an unhandled exception thrown
            // directly from a plugin command function escapes into native NPP code
            // (the function pointer is called straight from Notepad_plus::runPluginCommand)
            // and NPP's own crash guard tears down the whole editor with a
            // "X.dll just crashed in runPluginCommand" dialog. Wrapping ensures a
            // failure here is always a graceful message box instead.
            _configDelegate    = () => SafeExecute("Configure EPM Connection", ConfigureConnection);
            _pullDelegate      = () => SafeExecute("Pull Calculations",    PullCalculations);
            _executeDelegate   = () => SafeExecute("Execute Deployed Calculation", ExecuteCalculation);
            _workspaceDelegate = () => SafeExecute("Toggle Folder as Workspace", OpenAsWorkspace);
            _deployDelegate    = () => SafeExecute("Deploy Active Tab",    DeployActiveTab);
            _variablesDelegate = () => SafeExecute("Variables",            ShowVariables);
            _validateDelegate  = () => SafeExecute("Local Syntax Validation", ValidateActiveScript);
            _settingsDelegate  = () => SafeExecute("Configure",            OpenSettings);
            _navigatorDelegate = () => SafeExecute("Dimension Navigator",  OpenDimensionNavigator);
            _aboutDelegate     = () => SafeExecute("About",                ShowAbout);

            PluginBase.SetCommand(0, "Configure EPM Connection", _configDelegate);
            PluginBase.SetCommand(1, "Pull Calculations",    _pullDelegate);
            PluginBase.SetCommand(2, "Execute Deployed Calculation", _executeDelegate);
            PluginBase.SetCommand(3, "Toggle Folder as Workspace", _workspaceDelegate);
            PluginBase.SetCommand(4, "Deploy Active Tab",    _deployDelegate);
            // Appended (not inserted) so existing command indexes — and any keyboard
            // shortcuts users mapped to them — stay the same.
            PluginBase.SetCommand(5, "Variables",            _variablesDelegate);
            PluginBase.SetCommand(6, "Local Syntax Validation", _validateDelegate);
            PluginBase.SetCommand(7, "Dimension Navigator",  _navigatorDelegate);
            PluginBase.SetCommand(8, "", null);                       // separator
            PluginBase.SetCommand(9, "Configure…",           _settingsDelegate);
            PluginBase.SetCommand(10, "", null);                      // separator
            PluginBase.SetCommand(11, "About CalcStudio…",    _aboutDelegate);
        }

        private static void SafeExecute(string commandName, Action action)
        {
            // Every dialog of a command is created inside this scope — see DpiScope for
            // why (mixed-DPI monitors squash per-monitor-aware WinForms dialogs).
            using (DpiScope.SystemAware())
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PluginLog($"{commandName}: UNHANDLED EXCEPTION: {ex}");
                ShowError(commandName, "An unexpected error occurred:\n\n" + ex);
            }
            finally
            {
                // A password given "for this action only" ends with the command
                Credentials.EndAction();
            }
        }

        /// <summary>Called on NPPN_READY — NPP's UI is fully built.</summary>
        public static void OnNppReady()
        {
            _nppReady = true;
            FolderWorkspace.Log = PluginLog;

            InstallCalcScriptLanguage();
            // Ctrl+K etc. for Calc Script (its language defines /* */ as a delimiter — see CommentCommands)
            CommentCommands.Attach(PluginBase.nppData._nppHandle, GetCurrentScintilla);

            // Split-button arrow on "Configure EPM Connection" for quick switching.
            ToolbarDropDown.Attach(
                PluginBase.nppData._nppHandle,
                PluginBase._funcItems.Items[0]._cmdID,
                GetConnectionMenuItems,
                index => SafeExecute("Select EPM Connection", () => SelectConnection(index)),
                "No connections configured",
                separatorBefore: true);

            // Split-button arrow on "Deploy Active Tab": Deploy CM / Deploy CM+HP.
            ToolbarDropDown.Attach(
                PluginBase.nppData._nppHandle,
                PluginBase._funcItems.Items[4]._cmdID,
                GetDeployMenuItems,
                index => SafeExecute("Deploy Active Tab", () => DeployActiveTab((DeployMethod)index)),
                "",
                separatorBefore: false);
            UpdateDeployAvailability();

            WarnIfLegacyCopyInstalled();
            CheckForUpdatesInBackground();
        }

        /// <summary>
        /// Asks GitHub for a newer release, at most once a day and off the UI thread; Notepad++
        /// starts as usual whatever the answer. Each newer version is announced once. Off in
        /// Configure… › General.
        /// </summary>
        private static void CheckForUpdatesInBackground()
        {
            try
            {
                if (!PluginSettings.Current.CheckForUpdates || !UpdateCheck.Available) return;
                if (UiState.Load().LastUpdateCheckUtc > DateTime.UtcNow.AddDays(-1)) return;

                // A window of our own on the UI thread, to hand the answer back to it
                var marshal = new Control();
                IntPtr unused = marshal.Handle;

                Task.Run(() => UpdateCheck.CheckAsync()).ContinueWith(t =>
                {
                    UpdateCheck.Result r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    try
                    {
                        marshal.BeginInvoke(new Action(() =>
                        {
                            try { OnBackgroundUpdateCheck(r); }
                            catch (Exception ex) { PluginLog("Update check: " + ex.Message); }
                            finally { marshal.Dispose(); }
                        }));
                    }
                    catch { /* Notepad++ is closing */ }
                });
            }
            catch (Exception ex) { PluginLog("Update check: " + ex.Message); }
        }

        private static void OnBackgroundUpdateCheck(UpdateCheck.Result r)
        {
            if (r == null || r.Error != null)
            {
                PluginLog("Update check: " + (r?.Error ?? "failed"));   // tried again at the next start
                return;
            }

            UiState ui = UiState.Load();
            ui.LastUpdateCheckUtc = DateTime.UtcNow;
            bool announce = r.IsNewer && r.LatestVersion != ui.UpdateNotifiedVersion;
            if (announce) ui.UpdateNotifiedVersion = r.LatestVersion;
            ui.Save();
            PluginLog($"Update check: latest release {r.LatestVersion}, this is {BuildInfo.Version}");

            if (announce &&
                MessageBox.Show(NppOwner,
                    $"{PluginName} {r.LatestVersion} is available (you have {BuildInfo.Version}).\n\n" +
                    "Open its release page to download it?\n\n" +
                    "This check runs at most once a day; turn it off in Configure… › General.",
                    PluginName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                AboutForm.OpenUrl(r.ReleaseUrl);
        }

        /// <summary>
        /// Before 0.015 the plugin was installed as plugins\NppEPMPlugin\NppEPMPlugin.dll. If that
        /// copy is still there, Notepad++ loads both: two menus, two sets of toolbar buttons.
        /// </summary>
        private static void WarnIfLegacyCopyInstalled()
        {
            try
            {
                string ours    = Path.GetDirectoryName(typeof(Main).Assembly.Location);
                string plugins = Path.GetDirectoryName(ours);
                string legacy  = Path.Combine(plugins, ConfigManager.LegacyName);
                if (string.Equals(ours, legacy, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(Path.Combine(legacy, ConfigManager.LegacyName + ".dll")))
                    return;

                MessageBox.Show(
                    $"An older copy of {PluginName} is still installed under its previous name:\n\n{legacy}\n\n" +
                    "Notepad++ loads both, so the menu and toolbar buttons appear twice. Close Notepad++, " +
                    "delete that folder (Windows asks for administrator rights in Program Files) and start " +
                    "Notepad++ again.\n\nYour settings were copied to the new version; nothing is lost.",
                    PluginName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { PluginLog("WarnIfLegacyCopyInstalled: " + ex.Message); }
        }

        /// <summary>
        /// Greys out Deploy (toolbar button and menu item) while the active tab belongs to a
        /// read-only connection. Deploy itself refuses too, so this is only a visual cue.
        /// </summary>
        private static void UpdateDeployAvailability()
        {
            try
            {
                ConnectionConfig cfg = ConnectionOfActiveTab() ?? ConfigManager.GetActive();
                bool enabled = !(cfg?.ReadOnly ?? false);
                int cmdId = PluginBase._funcItems.Items[4]._cmdID;
                ToolbarDropDown.SetEnabled(cmdId, enabled);
                EnableMenuItem(Win32.GetMenu(PluginBase.nppData._nppHandle), (uint)cmdId, enabled ? MF_ENABLED : MF_GRAYED);
            }
            catch (Exception ex) { PluginLog("UpdateDeployAvailability: " + ex.Message); }
        }

        private const uint MF_ENABLED = 0x0000, MF_GRAYED = 0x0001;   // with MF_BYCOMMAND (0)
        [DllImport("user32.dll")] private static extern int EnableMenuItem(IntPtr hMenu, uint idItem, uint flags);

        // ------------------------------------------------------------------ //
        //  Deploy method (toolbar drop-down)
        // ------------------------------------------------------------------ //

        /// <summary>One entry per <see cref="DeployMethod"/> (index = value); the active
        /// connection's default is labelled.</summary>
        private static List<ToolbarDropDown.Item> GetDeployMenuItems()
        {
            DeployMethod def = DeployMethod.CmOnly;
            try { def = (ConnectionOfActiveTab() ?? ConfigManager.GetActive())?.DefaultDeployMethod ?? DeployMethod.CmOnly; } catch { }

            var items = new List<ToolbarDropDown.Item>();
            foreach (DeployMethod m in new[] { DeployMethod.CmOnly, DeployMethod.CmAndHp })
                items.Add(new ToolbarDropDown.Item(
                    DeployMethodNames.Short(m) + (m == def ? " (Connection Default)" : ""), false));
            return items;
        }

        /// <summary>
        /// Installs the "Calc Script - Essbase" user language (light + dark) when missing.
        /// Notepad++ reads user languages only at startup, so the first time it says a
        /// restart is needed — and names any other language that also claims .csc.
        /// </summary>
        private static void InstallCalcScriptLanguage()
        {
            try
            {
                if (!PluginSettings.Current.InstallCalcScriptLanguage) return;   // Configure… › Calc Script language
                string settingsDir = FolderWorkspace.SettingsDir(PluginBase.nppData._nppHandle);
                CalcScriptLanguage.InstallResult r = CalcScriptLanguage.EnsureInstalled(settingsDir, PluginLog);
                if (!r.Installed && r.Updated)
                {
                    using (DpiScope.SystemAware())
                        MessageBox.Show(NppOwner,
                            $"The '{CalcScriptLanguage.Name}' language was updated by this version of {PluginName}.\n\n" +
                            "Restart Notepad++ to see the new colours.",
                            PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!r.Installed) return;

                string others = r.OtherLanguagesForCsc.Count == 0 ? "" :
                    "\n\nThese user languages are also set for .csc files: " +
                    string.Join(", ", r.OtherLanguagesForCsc.Select(n => $"'{n}'")) + ". " +
                    "Remove \"csc\" from their extension (Language › User Defined Language › " +
                    "Define your language…) so Notepad++ uses Calc Script - Essbase.";
                using (DpiScope.SystemAware())
                    MessageBox.Show(NppOwner,
                        $"The '{CalcScriptLanguage.Name}' language (with a dark mode version) was installed " +
                        "for .csc files: Essbase functions, commands, SET options, member names, runtime " +
                        "prompts and FIX/IF/LOOP folding.\n\nRestart Notepad++ to start using it." + others,
                        PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                PluginLog("Installing the Calc Script language failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ //
        //  Quick connection switch (toolbar drop-down)
        // ------------------------------------------------------------------ //

        private static List<ToolbarDropDown.Item> GetConnectionMenuItems()
        {
            var items  = new List<ToolbarDropDown.Item>();
            var active = ConfigManager.GetActive();
            foreach (var c in ConfigManager.GetAll())
            {
                bool isActive = active != null && string.Equals(
                    c.LocalFolderPath, active.LocalFolderPath, StringComparison.OrdinalIgnoreCase);
                items.Add(new ToolbarDropDown.Item(DisplayName(c), isActive));
            }
            return items;
        }

        /// <summary>
        /// Makes the connection at <paramref name="index"/> (in GetAll() order) the default.
        /// It stays the default until a tab from a different project folder is activated,
        /// at which point auto-detection takes over again.
        /// </summary>
        private static void SelectConnection(int index)
        {
            var all = ConfigManager.GetAll();
            if (index < 0 || index >= all.Count) return;

            ConfigManager.SetActive(all[index].LocalFolderPath);
            UpdateStatusBar();
        }

        private static string DisplayName(ConnectionConfig c) =>
            string.IsNullOrWhiteSpace(c.Name) ? c.LocalFolderPath : c.Name;

        /// <summary>
        /// Toolbar buttons: command index (as passed to SetCommand) → icon factory.
        /// Commands not listed here stay menu-only. To add a button, add a row.
        /// </summary>
        private static readonly (int CmdIndex, Func<Bitmap> CreateIcon)[] ToolbarButtons =
        {
            (0, () => IconHelper.CreateGearIcon()),      // Configure EPM Connection
            (1, () => IconHelper.CreateDownloadIcon()),  // Pull Calculations
            (3, () => IconHelper.CreateWorkspaceIcon()), // Toggle Folder as Workspace
            (5, () => IconHelper.CreateVariablesIcon()), // Variables
            (7, () => IconHelper.CreateHierarchyIcon()), // Dimension Navigator
            (6, () => IconHelper.CreateValidateIcon()),  // Local Syntax Validation
            (4, () => IconHelper.CreateDeployIcon()),    // Deploy Active Tab
            (2, () => IconHelper.CreateCloudPlayIcon()), // Execute Deployed Calculation
        };

        /// <summary>
        /// Called on NPPN_TBMODIFICATION (and only then) — registers toolbar icons.
        /// NPP adds the buttons in registration order, right after the built-in ones.
        /// </summary>
        public static void SetToolBarIcon()
        {
            if (_iconsRegistered) return;
            _iconsRegistered = true;

            int   bmpSize = StandardIconSize();
            Color matte   = ToolbarBackground();
            PluginLog($"SetToolBarIcon: standard bitmap {bmpSize}px, matte={matte}");

            foreach (var (cmdIndex, createIcon) in ToolbarButtons)
                RegisterIcon(cmdIndex, createIcon, bmpSize, matte);
        }

        /// <summary>
        /// Size of the toolbar's "Standard icons" images: Notepad++ scales 16px by its
        /// window's DPI (ToolBar::reset → DPIManagerV2::scale(16)).
        /// </summary>
        private static int StandardIconSize()
        {
            int dpi = 96;
            try { dpi = (int)GetDpiForWindow(PluginBase.nppData._nppHandle); }
            catch (EntryPointNotFoundException) { }   // before Windows 10 1607
            return dpi > 0 ? (16 * dpi + 48) / 96 : 16;
        }

        /// <summary>
        /// The colour behind the toolbar buttons: Notepad++'s dark-mode dialog background
        /// (what its toolbar custom draw fills with), otherwise the window colour its own
        /// standard bitmaps are loaded onto (LR_LOADTRANSPARENT → COLOR_WINDOW).
        /// </summary>
        private static Color ToolbarBackground()
        {
            IntPtr npp = PluginBase.nppData._nppHandle;
            if (Win32.SendMessage(npp, (uint)NppMsg.NPPM_ISDARKMODEENABLED, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero)
            {
                var colors = new int[12];   // NppDarkMode::Colors — 12 COLORREFs
                if (SendMessage(npp, (uint)NppMsg.NPPM_GETDARKMODECOLORS, (IntPtr)(colors.Length * 4), colors) != IntPtr.Zero)
                    return ColorTranslator.FromWin32(colors[3]);   // pureBackground
            }
            return SystemColors.Window;
        }

        /// <summary>"Standard icons" bitmaps of our buttons, re-blended when dark mode changes.</summary>
        private static readonly List<(int CmdId, IconHelper.ToolbarBitmap Bitmap)> _standardBitmaps =
            new List<(int, IconHelper.ToolbarBitmap)>();

        /// <summary>
        /// Called on NPPN_DARKMODECHANGED. Re-blends our "Standard icons" bitmaps onto the
        /// new toolbar background. NPP has already rebuilt its toolbar from the old pixels
        /// by now, so when that toolbar shows standard icons, swap the new ones in directly.
        /// </summary>
        public static void OnDarkModeChanged()
        {
            Color matte = ToolbarBackground();
            foreach (var (_, bmp) in _standardBitmaps)
                bmp.SetMatte(matte);

            const int StandardIconSet = 4;   // NPPM_GETTOOLBARICONSETCHOICE: "Standard icons: small"
            IntPtr choice = Win32.SendMessage(PluginBase.nppData._nppHandle,
                (uint)NppMsg.NPPM_GETTOOLBARICONSETCHOICE, IntPtr.Zero, IntPtr.Zero);
            if (choice.ToInt64() == StandardIconSet)
                ToolbarDropDown.ReplaceButtonImages(_standardBitmaps.Select(b => (b.CmdId, b.Bitmap.Handle)));
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, int[] lParam);

        /// <summary>
        /// Registers one toolbar button the same way JsonTools / ComparePlus do:
        /// a single NPPM_ADDTOOLBARICON_FORDARKMODE carrying three images —
        ///   hToolbarBmp          → "Standard icons" toolbar mode (DPI-sized, blended onto the toolbar background)
        ///   hToolbarIcon         → Fluent icon modes, light theme
        ///   hToolbarIconDarkMode → Fluent icon modes, dark theme
        /// </summary>
        private static void RegisterIcon(int cmdIndex, Func<Bitmap> createIcon, int bmpSize, Color matte)
        {
            try
            {
                // Real Windows command ID that NPP assigned to this menu entry;
                // Plugin.cs refreshes it from the native FuncItem buffer just before.
                int cmdId = PluginBase._funcItems.Items[cmdIndex]._cmdID;
                PluginLog($"RegisterIcon[{cmdIndex}]: cmdId={cmdId}");
                if (cmdId <= 0) return;

                // The GDI handles must outlive this method — NPP keeps them for the
                // toolbar's lifetime (it re-reads them whenever the icon set changes),
                // so they are intentionally never destroyed. The Bitmaps can go.
                IntPtr hBmp, hIcon, hIconDark;
                using (Bitmap light = createIcon())
                using (Bitmap dark  = IconHelper.ForDarkMode(light))
                {
                    var standard = new IconHelper.ToolbarBitmap(light, bmpSize, matte);
                    _standardBitmaps.Add((cmdId, standard));
                    hBmp      = standard.Handle;
                    hIcon     = light.GetHicon();
                    hIconDark = dark.GetHicon();
                }

                var icons = new ToolbarIconsWithDarkMode
                {
                    hToolbarBmp          = hBmp,
                    hToolbarIcon         = hIcon,
                    hToolbarIconDarkMode = hIconDark,
                };
                IntPtr result = Win32.SendMessage(
                    PluginBase.nppData._nppHandle,
                    (uint)NppMsg.NPPM_ADDTOOLBARICON_FORDARKMODE,
                    (IntPtr)cmdId,
                    ref icons);
                PluginLog($"RegisterIcon[{cmdIndex}]: FORDARKMODE result={result}");

                if (result == IntPtr.Zero)
                {
                    // Pre-8.0 Notepad++ — no dark-mode message; use the legacy one.
                    var legacy = new ToolbarIcons { hToolbarBmp = hBmp, hToolbarIcon = hIcon };
                    result = Win32.SendMessage(
                        PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_ADDTOOLBARICON,
                        (IntPtr)cmdId,
                        ref legacy);
                    PluginLog($"RegisterIcon[{cmdIndex}]: ADDTOOLBARICON result={result}");
                }
            }
            catch (Exception ex)
            {
                PluginLog($"RegisterIcon[{cmdIndex}]: EXCEPTION: {ex.Message}");
            }
        }

        // Shared log helper — same file as Plugin.cs startup log.
        private static readonly string _pluginLogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CalcStudioEPM_startup.log");
        private static void PluginLog(string msg)
        {
            try { System.IO.File.AppendAllText(_pluginLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        /// <summary>Called on NPPN_SHUTDOWN.</summary>
        public static void PluginCleanUp()
        {
            // Deliberately does NOT free PluginBase._funcItems: NPP keeps the pointer
            // returned by getFuncsArray until the process exits, so freeing it here
            // would leave NPP reading released memory during shutdown.
            ToolbarDropDown.Detach();
            CommentCommands.Detach();
        }

        /// <summary>
        /// Handles Scintilla/Notepad++ notifications.
        /// On buffer-activation and language-change events we refresh the status bar
        /// to show the current connection name appended to the document-type field.
        /// </summary>
        public static void OnNotification(ScNotification nc)
        {
            uint code = nc.nmhdr.code;
            if (code == (uint)NppMsg.NPPN_BUFFERACTIVATED ||
                code == (uint)NppMsg.NPPN_LANGCHANGED)
            {
                // Guard: only call NPP API messages after NPPN_TBMODIFICATION has fired.
                // NPPN_BUFFERACTIVATED can arrive during session restore, before NPP's
                // status bar exists. Calling NPPM_SETSTATUSBAR too early crashes NPP
                // inside the native SendMessage — it cannot be caught by managed try-catch.
                if (_nppReady)
                {
                    UpdateStatusBar();
                    NavigatorFollowsTab();
                    UpdateDeployAvailability();
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Command 0: Configure EPM Connection
        // ------------------------------------------------------------------ //

        public static void ConfigureConnection()
        {
            // The form manages its own save lifecycle — it upserts directly on every
            // "Save Connection" click and needs no result-processing here.
            using (var form = new ConfigForm())
                form.ShowDialog();
            UpdateStatusBar();
            UpdateDeployAvailability();
        }

        // ------------------------------------------------------------------ //
        //  Command 1: Pull Calculations
        // ------------------------------------------------------------------ //

        public static void PullCalculations()
        {
            ConnectionConfig cfg = TryLoadConfig();
            if (cfg == null) return;

            // Pull empties the scripts folder and writes it again, keeping the folder itself, so a
            // Folder as Workspace root (or an Explorer window) on it stays valid and shows the new files

            // The "keep both" copies from earlier pulls: kept across pulls unless cleared below
            List<LocalChanges.Change> localCopies;
            try { localCopies = LocalChanges.LocalCopies(cfg); }
            catch (Exception ex) { PluginLog("Pull Calculations: could not list local copies: " + ex.Message); localCopies = new List<LocalChanges.Change>(); }

            // Options: the calculations, the dimensions / members / variables (dimensions only when
            // the connection downloads them), and clearing the local copies
            bool includeCalculations, includeMetadata, clearLocalCopies;
            using (var options = new PullOptionsForm(string.IsNullOrWhiteSpace(cfg.Name) ? cfg.ServiceBaseUrl : cfg.Name,
                                                     cfg.PullDimensionMembers,
                                                     EpmMetadata.DimensionsPulledAt(cfg.LocalFolderPath),
                                                     localCopies.Count))
            {
                if (options.ShowDialog(NppOwner) != DialogResult.OK) return;
                includeCalculations = options.UpdateCalculations;
                includeMetadata     = options.UpdateMetadata;
                clearLocalCopies    = options.ClearLocalCopies;
            }

            bool succeeded;
            if (!PasswordForm.Ensure(NppOwner, cfg)) return;
            if (includeCalculations)
            {
                switch (ConfirmPullNeeded(cfg))
                {
                    case PullDecision.Cancel: return;
                    case PullDecision.MetadataOnly: includeCalculations = false; includeMetadata = true; break;
                }
            }
            bool includeDimensions = includeMetadata && cfg.PullDimensionMembers;

            // Saved edits that weren't deployed would be replaced: overwrite, or keep both
            // (backed up, then copied back beside EPM's version as {name}.local.NNN{ext}).
            // Earlier .local copies are kept without asking.
            // (only when the calculations are pulled: otherwise the scripts folder isn't touched)
            var keepBoth = new List<LocalChanges.Change>();
            List<LocalChanges.Change> changes = new List<LocalChanges.Change>();
            if (!includeCalculations) localCopies = new List<LocalChanges.Change>();
            else
            {
                try { changes = LocalChanges.Scan(cfg); }
                catch (Exception ex) { PluginLog("Pull Calculations: could not check for undeployed changes: " + ex.Message); }
            }
            // Cleared copies are still backed up (just not copied back)
            List<LocalChanges.Change> copiesBack = clearLocalCopies ? new List<LocalChanges.Change>() : localCopies;
            if (changes.Count > 0)
                using (var dlg = new LocalChangesForm(string.IsNullOrWhiteSpace(cfg.Name) ? cfg.ServiceBaseUrl : cfg.Name, changes))
                {
                    if (dlg.ShowDialog(NppOwner) != DialogResult.OK) return;
                    keepBoth = dlg.KeepBoth;
                }
            string backupDir = null;
            if (keepBoth.Count + localCopies.Count > 0)
            {
                try { backupDir = LocalChanges.Backup(cfg, keepBoth.Concat(localCopies)); }
                catch (Exception ex)
                {
                    ShowError("Pull Calculations", "Could not back up your files, so the pull was not started:\n" + ex.Message);
                    return;
                }
            }

            using (var form = new Forms.PullProgressForm(cfg, includeCalculations, includeMetadata, includeDimensions))
            {
                form.ShowDialog();
                succeeded = form.Succeeded;
            }

            if (backupDir != null)
            {
                try
                {
                    if (succeeded)
                    {
                        List<string> written = LocalChanges.RestoreAfterPull(cfg, backupDir, keepBoth, copiesBack);
                        PluginLog($"Pull Calculations: {written.Count} file(s) copied back from {backupDir}" +
                                  (clearLocalCopies && localCopies.Count > 0 ? $"; {localCopies.Count} local copies cleared" : ""));
                        if (keepBoth.Count > 0)
                            ShowInfo("Pull Calculations",
                                $"Your version of {keepBoth.Count} file(s) is beside EPM's, ready to compare:\n  • " +
                                string.Join("\n  • ", written.Skip(copiesBack.Count).Take(15).Select(Path.GetFileName)) +
                                (keepBoth.Count > 15 ? "\n  • …" : "") + $"\n\nBackup: {backupDir}");
                    }
                    else
                    {
                        List<string> back = LocalChanges.RestoreAfterFailedPull(cfg, backupDir, keepBoth.Concat(localCopies));
                        if (back.Count > 0) PluginLog($"Pull Calculations: pull failed; {back.Count} kept file(s) put back under their own names");
                    }
                }
                catch (Exception ex)
                {
                    ShowError("Pull Calculations", $"Your files are safe in\n{backupDir}\nbut could not be copied back: {ex.Message}");
                }
            }

        }

        // ------------------------------------------------------------------ //
        //  Command 2: Execute Deployed Calculation
        // ------------------------------------------------------------------ //

        public static void ExecuteCalculation()
        {
            const string title = "Execute Deployed Calculation";

            ConnectionConfig cfg = ConnectionForRuleTab(title);
            if (cfg == null) return;

            // ── Which rule? Same resolution as Deploy Active Tab ──────────────
            string editorContent = GetScintillaText();
            ActiveRule rule = ResolveActiveRule(cfg, title, editorContent);
            if (rule == null) return;
            string planType = rule.PlanType, cube = rule.Cube, ruleName = rule.RuleName;

            // ── What is deployed, and does the editor differ from it? ─────────
            string deployedXml = PackageBuilder.ReadDeployedRuleXml(cfg.LocalFolderPath, planType, cube, ruleName);
            if (deployedXml == null)
            {
                ShowError(title,
                    $"No deployed copy of '{ruleName}' was found locally.\n\n" +
                    "Run 'Pull Calculations' (or deploy the rule) first.");
                return;
            }

            DeployPlan deployPlan = null;   // non-null = deploy this first
            string graphicalDir = GraphicalRule.RuleFolderOf(GetActiveFilePath(silent: true));
            if (graphicalDir != null)
            {
                List<string> pending;
                try { pending = GraphicalRule.Apply(deployedXml, graphicalDir).Changes; }
                catch (EpmException) { pending = new List<string> { "(the rule's files have problems — see Deploy)" }; }
                if (pending.Count > 0 && MessageBox.Show(NppOwner,
                        $"'{ruleName}' is a graphical rule with changes that aren't deployed:\n  • " +
                        string.Join("\n  • ", pending) + "\n\n" +
                        "Deploy them (to Calculation Manager), then deploy the rule to Planning from Calculation " +
                        "Manager. Run the deployed version now?",
                        title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }
            else if (PackageBuilder.HasUndeployedChanges(editorContent, deployedXml) && cfg.ReadOnly)
            {
                // Its changes can't be deployed: offer to run what is deployed
                if (MessageBox.Show(NppOwner,
                        $"This tab differs from the deployed version of '{ruleName}', but '{DisplayName(cfg)}' is a " +
                        "read-only connection, so the changes can't be deployed.\n\nRun the deployed version?",
                        title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }
            else if (PackageBuilder.HasUndeployedChanges(editorContent, deployedXml))
            {
                using (var dlg = new UndeployedChangesForm(ruleName))
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    if (dlg.DeployFirst)
                    {
                        // Same pre-deploy validations as Deploy Active Tab
                        // (unsaved changes → save or cancel; empty document refused)
                        string deployContent = GetContentToDeploy(cfg, title);
                        if (deployContent == null) return;

                        // Execute runs the Planning copy, so always try to update it too
                        deployPlan = PlanDeploy(cfg, rule, deployContent, DeployMethod.CmAndHp, forExecute: true);
                        if (deployPlan == null) return;
                    }
                }
            }

            // ── Last deploy from here reached Calculation Manager only? ───────
            if (deployPlan == null &&
                PlanningPending.IsPending(cfg.LocalFolderPath, planType, cube, ruleName))
            {
                DialogResult answer = MessageBox.Show(NppOwner,
                    $"The last deploy of '{ruleName}' from Notepad++ reached Calculation Manager only.\n\n" +
                    "Unless it was deployed to Planning from Calculation Manager since, Planning will " +
                    "run the previous version.\n\n" +
                    "Yes — it is deployed to Planning now: execute, and stop reminding me.\n" +
                    "No — execute anyway (keep reminding me).\n" +
                    "Cancel — don't execute.",
                    title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes)
                    PlanningPending.Set(cfg.LocalFolderPath, planType, cube, ruleName, pending: false);
            }

            // ── Runtime prompts of the version that will run ──────────────────
            // Deployed version: exactly the prompts EPM has. Deploying first: the prompts
            // the deploy is about to give the rule (added/removed references applied).
            string ruleXml = deployPlan == null
                ? deployedXml
                : PackageBuilder.BuildRuleXml(cfg.LocalFolderPath, planType, cube, ruleName, deployPlan.Content);
            List<RuntimePrompt> prompts = RulePrompts.FromRuleXml(ruleXml, ruleName)
                                                     .Where(p => !p.Hidden).ToList();

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (prompts.Count > 0)
            {
                using (var form = new RtpForm(prompts, ruleName, PromptValues.Load(cfg.LocalFolderPath)))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    values = form.RtpValues;
                }
                PromptValues.Save(cfg.LocalFolderPath, values);
            }

            // ── Run, showing progress and the rule's output ───────────────────
            if (!PasswordForm.Ensure(NppOwner, cfg)) return;
            if (deployPlan != null && !ConfirmNotChangedInEpm(cfg, title, rule)) return;
            using (var dlg = new ExecuteProgressForm(cfg, planType, cube, ruleName, deployPlan, values))
                dlg.ShowDialog();
        }

        // ------------------------------------------------------------------ //
        //  Command 5: Variables
        // ------------------------------------------------------------------ //

        public static void ShowVariables()
        {
            ConnectionConfig cfg = ConnectionForTab();
            if (cfg == null) return;

            VariableDictionary dict = VariableDictionary.LoadOrRebuild(cfg.LocalFolderPath);
            if (dict == null || dict.Variables.Count == 0)
            {
                ShowInfo("Variables",
                    "No variables found for this connection yet.\n\n" +
                    "Run 'Pull Calculations' to build the variable dictionary.");
                return;
            }

            // The active tab's extension decides the insert syntax
            string path = GetActiveFilePath(silent: true);
            string ext  = Path.GetExtension(path ?? "").ToLowerInvariant();
            ScriptKind kind = ext == ".csc" ? ScriptKind.CalcScript
                            : ext == ".js"  ? ScriptKind.Groovy
                            :                 ScriptKind.None;

            using (var form = new VariablesForm(cfg, dict, kind, Path.GetFileName(path ?? "")))
            {
                if (form.ShowDialog() != DialogResult.OK || form.SelectedVariable == null) return;
                InsertVariableReference(form.InsertText, form.SelectedVariable.Name,
                                        groovy: kind == ScriptKind.Groovy);
            }
        }

        // ------------------------------------------------------------------ //
        //  Command 6: Local Syntax Validation (Groovy .js or Calc script .csc)
        // ------------------------------------------------------------------ //

        public static void ValidateActiveScript()
        {
            const string title = "Local Syntax Validation";

            ShowLocalValidationNotice();

            ActiveScript script = GetActiveScript();
            if (script.Kind == ScriptKind.None)
            {
                ShowInfo(title, "Local validation is available for Groovy (.js) and Calc script (.csc) rules.");
                return;
            }

            // Optional: without a connection the RTP dictionary checks are just skipped
            ConnectionConfig cfg = ConnectionOfActiveTab() ?? ConfigManager.GetActive();
            List<ValidationIssue> issues = ValidateScript(cfg, GetScintillaText(), script);

            if (issues.Count == 0)
            {
                ShowInfo(title, $"No problems found in {script.FileName}." +
                    (script.IsTemplate ? "\n\n(Template: functions and prompts were checked; block structure is not, as templates are fragments.)" : ""));
                return;
            }

            using (var form = new ValidationForm(ValidationForm.Mode.Review, script.FileName, issues))
            {
                form.ShowDialog(NppOwner);
                if (form.JumpTo != null) GoToIssue(form.JumpTo);
                if (form.EditRulesRequested) OpenInNpp(RulesFileFor(script.Kind));
            }
        }

        /// <summary>
        /// Reminds the user this is a simple syntax check, not an EPM validation — every
        /// time, until they tick "Don't show me again" (which unlocks after three views).
        /// </summary>
        private static void ShowLocalValidationNotice()
        {
            UiState state = UiState.Load();
            if (state.LocalValidationNoticeDismissed) return;

            state.LocalValidationNoticeCount++;
            using (var notice = new LocalValidationNoticeForm(state.LocalValidationNoticeCount))
            {
                notice.ShowDialog(NppOwner);
                state.LocalValidationNoticeDismissed = notice.DontShowAgain;
            }
            state.Save();
        }

        /// <summary>Opens a file in Notepad++ (e.g. the validation rules for editing).</summary>
        private static void OpenInNpp(string path)
        {
            IntPtr pPath = Marshal.StringToHGlobalUni(path);
            try
            {
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_DOOPEN, IntPtr.Zero, pPath);
            }
            finally { Marshal.FreeHGlobal(pPath); }
        }

        // ------------------------------------------------------------------ //
        //  Command 7: Dimension Navigator (modeless)
        // ------------------------------------------------------------------ //

        public static void OpenDimensionNavigator()
        {
            ConnectionConfig cfg = ConnectionForTab();
            if (cfg == null) return;

            if (_navigator != null && !_navigator.IsDisposed)
            {
                _navigator.ShowConnection(cfg);          // the tab's connection may have changed
                if (_navigator.WindowState == FormWindowState.Minimized) _navigator.WindowState = FormWindowState.Normal;
                _navigator.Activate();
                return;
            }
            _navigator = new DimensionNavigatorForm(cfg, InsertAtCursor);
            _navigator.FormClosed += (s, e) => _navigator = null;
            _navigator.Show(NppOwner);
        }

        /// <summary>
        /// An open Dimension Navigator switches to the connection of the tab just activated
        /// (a tab outside every connection folder leaves it as it is).
        /// </summary>
        private static void NavigatorFollowsTab()
        {
            try
            {
                if (_navigator == null || _navigator.IsDisposed) return;
                ConnectionConfig cfg = ConnectionOfActiveTab();
                if (cfg != null && !ConfigManager.SameFolder(cfg.LocalFolderPath, _navigator.ConnectionFolder))
                    _navigator.ShowConnection(cfg);
            }
            catch (Exception ex) { PluginLog("NavigatorFollowsTab: " + ex.Message); }
        }

        /// <summary>Puts text at the cursor of the active document (replacing the selection) and focuses it.</summary>
        private static void InsertAtCursor(string text)
        {
            IntPtr sci = GetCurrentScintilla();
            bool utf8 = Win32.SendMessage(sci, (uint)SciMsg.SCI_GETCODEPAGE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 65001;
            SendText(sci, SciMsg.SCI_REPLACESEL, IntPtr.Zero, text, utf8 ? Encoding.UTF8 : Encoding.Default);
            Win32.SendMessage(sci, (uint)SciMsg.SCI_GRABFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ //
        //  Command 9: Configure…
        // ------------------------------------------------------------------ //

        public static void OpenSettings()
        {
            string nppSettingsDir = FolderWorkspace.SettingsDir(PluginBase.nppData._nppHandle);
            using (var form = new SettingsForm(nppSettingsDir, OpenInNpp, PluginLog))
                form.ShowDialog(NppOwner);
        }

        // ------------------------------------------------------------------ //
        //  Command 11: About
        // ------------------------------------------------------------------ //

        public static void ShowAbout()
        {
            using (var form = new AboutForm())
                form.ShowDialog(NppOwner);
        }

        // ------------------------------------------------------------------ //
        //  Command 4: Deploy Active Tab
        // ------------------------------------------------------------------ //

        /// <summary>Toolbar button / menu: the connection's default method.</summary>
        public static void DeployActiveTab() => DeployActiveTab(null);

        /// <param name="method">From the toolbar drop-down; null = the connection's default.</param>
        public static void DeployActiveTab(DeployMethod? method)
        {
            const string title = "Deploy Active Tab";

            ConnectionConfig cfg = ConnectionForRuleTab(title);
            if (cfg == null) return;
            if (cfg.ReadOnly)
            {
                ShowInfo(title,
                    $"'{DisplayName(cfg)}' is a read-only connection, so deploying is disabled.\n\n" +
                    "To deploy, untick Read only in Configure EPM Connection.");
                return;
            }

            string activePath = GetActiveFilePath(silent: true);
            if (RuleSet.IsRuleSetIni(activePath))
            {
                DeployRuleSet(cfg, title, activePath);
                return;
            }

            string graphicalDir = GraphicalRule.RuleFolderOf(activePath);
            if (graphicalDir != null && GraphicalRule.IsTemplateFolder(graphicalDir))
            {
                ShowInfo(title,
                    "This is a graphical template, pulled read only: templates are changed in Calculation Manager.\n\n" +
                    "The values a rule passes to the template's prompts are set in that rule's 00 - Rule.ini.");
                return;
            }
            if (graphicalDir != null)
            {
                DeployGraphicalRule(cfg, title, graphicalDir);
                return;
            }

            // Shared with Execute's "deploy my changes first"
            string calcContent = GetContentToDeploy(cfg, title);
            if (calcContent == null) return;

            ActiveRule rule = ResolveActiveRule(cfg, title, calcContent);
            if (rule == null) return;

            DeployPlan plan = PlanDeploy(cfg, rule, calcContent, method ?? cfg.DefaultDeployMethod, forExecute: false);
            if (plan == null) return;

            if (!PasswordForm.Ensure(NppOwner, cfg)) return;
            if (!ConfirmNotChangedInEpm(cfg, title, rule)) return;
            using (var dlg = new Forms.DeployProgressForm(cfg, rule.PlanType, rule.Cube, rule.RuleName, plan))
                dlg.ShowDialog();
        }

        /// <summary>
        /// Deploys a graphical rule from its folder (any of its files is the active tab):
        /// validates every step file, writes the files back into the rule XML by component id
        /// and step, shows what changed, and deploys to Calculation Manager only.
        /// </summary>
        private static void DeployGraphicalRule(ConnectionConfig cfg, string title, string ruleDir)
        {
            if (!EnsureActiveTabSaved(title)) return;
            ActiveRule rule = ResolveActiveRule(cfg, title, "");
            if (rule == null) return;

            // Every script and formula step must pass local validation (or "deploy anyway")
            foreach (string file in GraphicalRule.StepFiles(ruleDir))
            {
                if (!File.Exists(file)) continue;                   // reported by Apply below
                var step = new ActiveScript { Kind = ScriptKind.CalcScript, FileName = Path.GetFileName(file), IsTemplate = true };
                List<ValidationIssue> issues = ValidateScript(cfg, File.ReadAllText(file, Encoding.UTF8), step);
                if (issues.Count == 0) continue;
                using (var gate = new ValidationForm(ValidationForm.Mode.Gate, step.FileName, issues))
                {
                    bool deployAnyway = gate.ShowDialog(NppOwner) == DialogResult.OK;
                    if (gate.JumpTo != null) { OpenInNpp(file); GoToIssue(gate.JumpTo); }
                    if (!deployAnyway) return;
                }
                PluginLog($"{title}: '{rule.RuleName}' step {step.FileName} deployed despite {issues.Count} local validation issue(s)");
            }

            string deployedXml = PackageBuilder.ReadDeployedRuleXml(cfg.LocalFolderPath, rule.PlanType, rule.Cube, rule.RuleName);
            if (deployedXml == null)
            {
                ShowError(title, $"The pulled XML of '{rule.RuleName}' was not found. Pull Calculations again.");
                return;
            }

            GraphicalRule.Applied applied;
            try { applied = GraphicalRule.Apply(deployedXml, ruleDir); }
            catch (EpmException ex) { ShowError(title, ex.Message); return; }
            RuleVariableSync.Apply(applied.Doc, applied.TextBefore, applied.TextAfter, rule.Cube, rule.RuleName,
                                   () => VariableDictionary.LoadOrRebuild(cfg.LocalFolderPath), null);
            string ruleXml = RuleXmlWriter.Calc(applied.Doc);

            string changes = applied.Changes.Count == 0
                ? "Nothing changed since the last pull or deploy."
                : "Changed:\n  • " + string.Join("\n  • ", applied.Changes);
            string ignored = applied.Ignored.Count == 0 ? ""
                : "\n\nNot part of the rule (ignored):\n  • " + string.Join("\n  • ", applied.Ignored);
            if (MessageBox.Show(NppOwner,
                    $"Deploy the graphical rule '{rule.RuleName}' to Calculation Manager?\n\n{changes}{ignored}\n\n" +
                    "Graphical rules are deployed to Calculation Manager only: deploy the rule to Planning from " +
                    "Calculation Manager afterwards. Only saved files are deployed.",
                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            DeployPlan plan = DeployPlan.Graphical(ruleXml);
            PluginLog($"Deploy plan for graphical rule '{rule.RuleName}': {plan.TargetsText}; changes: {string.Join(" | ", applied.Changes)}");
            if (!PasswordForm.Ensure(NppOwner, cfg)) return;
            if (!ConfirmNotChangedInEpm(cfg, title, rule)) return;
            using (var dlg = new Forms.DeployProgressForm(cfg, rule.PlanType, rule.Cube, rule.RuleName, plan))
                dlg.ShowDialog();
        }

        /// <summary>
        /// Deploys a RuleSet from its .ruleset.ini: puts it together from the pulled definition,
        /// the .ini files and the deployed version of every rule it runs (see <see cref="RuleSet"/>),
        /// shows what changed, and deploys to Calculation Manager only.
        /// </summary>
        private static void DeployRuleSet(ConnectionConfig cfg, string title, string iniPath)
        {
            if (!EnsureActiveTabSaved(title)) return;

            string app, name;
            try { (app, name) = RuleSet.ParseIniPath(iniPath); }
            catch (EpmException ex) { ShowError(title, ex.Message); return; }
            if (!string.Equals(app, cfg.Application, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(title, $"'{name}' belongs to application {app}, but the connection '{DisplayName(cfg)}' is for {cfg.Application}.");
                return;
            }

            RuleSet.Assembled assembled;
            try { assembled = RuleSet.Assemble(cfg.LocalFolderPath, app, name, Path.GetDirectoryName(iniPath)); }
            catch (EpmException ex) { ShowError(title, ex.Message); return; }

            string List(IEnumerable<string> items, int max = 12)
            {
                var all = items.ToList();
                return "  • " + string.Join("\n  • ", all.Take(max)) + (all.Count > max ? $"\n  … and {all.Count - max} more" : "");
            }
            string changes = assembled.Changes.Count == 0
                ? "No settings or runtime prompts changed since the last pull or deploy."
                : "Changed:\n" + List(assembled.Changes);
            string notes = assembled.Notes.Count == 0 ? "" : "\n\nNote:\n" + List(assembled.Notes);
            string sets = assembled.RuleSets.Count > 1 ? $" and {assembled.RuleSets.Count - 1} RuleSet(s) it runs" : "";
            if (MessageBox.Show(NppOwner,
                    $"Deploy the RuleSet '{name}' to Calculation Manager?\n\n{changes}{notes}\n\n" +
                    $"It is put together from the deployed version of its {assembled.Rules.Count} rule(s){sets}. " +
                    "RuleSets are deployed to Calculation Manager only: deploy it to Planning from Calculation Manager " +
                    "afterwards. Only saved files are deployed.",
                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            PluginLog($"Deploy plan for RuleSet '{name}': {assembled.Rules.Count} rule(s): {string.Join(" | ", assembled.Rules)}; " +
                      $"changes: {string.Join(" | ", assembled.Changes)}; notes: {string.Join(" | ", assembled.Notes)}");
            if (!PasswordForm.Ensure(NppOwner, cfg)) return;
            if (!ConfirmNotChangedInEpm(cfg, title, new ActiveRule { PlanType = app, Cube = RuleSet.FolderName, RuleName = name })) return;
            using (var dlg = Forms.DeployProgressForm.ForRuleSet(cfg, app, name, DeployPlan.ForRuleSet(assembled.Xml)))
                dlg.ShowDialog();
        }

        private enum PullDecision { Pull, Cancel, MetadataOnly }

        /// <summary>
        /// Before pulling the calculations: when EPM reports no Calculation Manager change since
        /// the last pull (this PC's own deploys aside), says so and offers Cancel, Pull anyway, or
        /// updating only the dimensions and variables. Pull when something changed, or when EPM
        /// couldn't be asked.
        /// </summary>
        private static PullDecision ConfirmPullNeeded(ConnectionConfig cfg)
        {
            RuleChangeCheck.PullCheck check;
            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                Task<RuleChangeCheck.PullCheck> t = Task.Run(() => RuleChangeCheck.ChangesSincePullAsync(cfg));
                check = t.Wait(TimeSpan.FromSeconds(30)) ? t.Result : null;
            }
            catch (Exception ex)
            {
                PluginLog("Pull: change check not possible: " + (ex.InnerException ?? ex).Message);
                return PullDecision.Pull;
            }
            finally { Cursor.Current = previous; }

            if (check == null) { PluginLog("Pull: change check not possible (no change-tracking data, or EPM too slow)"); return PullDecision.Pull; }
            if (check.Changed.Count > 0)
            {
                PluginLog($"Pull: {check.Changed.Count} Calculation Manager change(s) since the last pull: " +
                          string.Join("; ", check.Changed.Take(20).Select(c => $"{c.ArtifactName} ({c.ModifiedBy}, {RuleChangeCheck.ShowDate(c.ModifiedDate)})")));
                return PullDecision.Pull;
            }
            string metadataOnly = cfg.PullDimensionMembers ? "Update only dimensions and variables" : "Update only variables";
            using (var dlg = new ChoiceForm("Pull Calculations",
                       $"No calculations have changed in EPM since your last pull ({check.PulledAtUtc.ToLocalTime():g}), " +
                       "apart from your own deploys.",
                       new[] { metadataOnly, "Pull anyway", "Cancel" }, defaultButton: 0, cancelButton: 2))
            {
                dlg.ShowDialog(NppOwner);
                return dlg.Choice == 0 ? PullDecision.MetadataOnly : dlg.Choice == 1 ? PullDecision.Pull : PullDecision.Cancel;
            }
        }

        /// <summary>
        /// Asks EPM whether the rule was changed after the last pull (or this PC's last deploy of
        /// it) — see <see cref="RuleChangeCheck"/>. When it was, says who changed it and when, and
        /// lets the user cancel. True = go ahead (also when EPM couldn't be asked).
        /// </summary>
        private static bool ConfirmNotChangedInEpm(ConnectionConfig cfg, string title, ActiveRule rule)
        {
            RuleChangeCheck.Result r;
            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                Task<RuleChangeCheck.Result> t = Task.Run(() => RuleChangeCheck.CheckAsync(cfg, rule.PlanType, rule.Cube, rule.RuleName));
                r = t.Wait(TimeSpan.FromSeconds(30)) ? t.Result : new RuleChangeCheck.Result { Note = "not checked: EPM took too long to answer" };
            }
            catch (Exception ex)
            {
                r = new RuleChangeCheck.Result { Note = "not checked: " + (ex.InnerException ?? ex).Message };
            }
            finally { Cursor.Current = previous; }

            if (r.Conflict == null)
            {
                PluginLog($"{title}: change check for '{rule.RuleName}': {r.Note}");
                return true;
            }
            PluginLog($"{title}: '{rule.RuleName}' changed in EPM by {r.Conflict.ModifiedBy} on {r.Conflict.ModifiedDate} (after the pull of {r.PulledAtUtc:u})");
            return MessageBox.Show(NppOwner,
                $"'{rule.RuleName}' was changed in EPM by {r.Conflict.ModifiedBy} on {RuleChangeCheck.ShowDate(r.Conflict.ModifiedDate)}, " +
                $"after your last pull ({r.PulledAtUtc.ToUniversalTime():yyyy-MM-dd HH:mm} UTC).\n\n" +
                "Deploying now overwrites that change. To keep it, cancel and Pull Calculations first.\n\n" +
                "Deploy anyway?",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>
        /// Decides where a deploy goes — shared by Deploy and Execute's "deploy my changes
        /// first". For CM+HP the script's %Template calls are inlined for the Planning copy;
        /// when a template can't be (DTPs, graphical, not pulled) the red ACTION NEEDED alert
        /// offers a Calculation Manager-only deploy. Null = the user cancelled.
        /// </summary>
        private static DeployPlan PlanDeploy(ConnectionConfig cfg, ActiveRule rule, string content,
                                             DeployMethod method, bool forExecute)
        {
            DeployPlan plan = DeployPlan.Create(cfg.LocalFolderPath, content, method);
            PluginLog($"Deploy plan for '{rule.RuleName}': {method} → {plan.TargetsText}" +
                      (plan.InlinedTemplates.Count > 0 ? $"; inlined {string.Join(", ", plan.InlinedTemplates)}" : "") +
                      (plan.PlanningBlockers.Count > 0 ? $"; blocked by {string.Join(" | ", plan.PlanningBlockers)}" : ""));

            if (!plan.ActionNeeded) return plan;

            using (var dlg = new PlanningBlockedForm(rule.RuleName, plan.PlanningBlockers, forExecute))
                return dlg.ShowDialog(NppOwner) == DialogResult.OK ? plan : null;
        }

        // ------------------------------------------------------------------ //
        //  Command 3: Toggle Folder as Workspace (add if absent, remove if present)
        // ------------------------------------------------------------------ //

        public static void OpenAsWorkspace()
        {
            ConnectionConfig cfg = TryLoadConfig();
            if (cfg == null) return;

            // The workspace root is the connection's scripts folder: "{Name}.Scripts".
            // This gives each connection a uniquely-named node in NPP's Folder as Workspace
            // panel, so two connections to the same EPM plan type are clearly distinguishable.
            string targetPath = PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name);
            IntPtr nppHwnd    = PluginBase.nppData._nppHandle;

            // Toggle: already in Folder as Workspace → remove it
            if (FolderWorkspace.Contains(nppHwnd, targetPath))
            {
                if (FolderWorkspace.RemoveFolder(nppHwnd, targetPath))
                    PluginLog($"Toggle Folder as Workspace: '{targetPath}' removed");
                return;
            }

            if (!Directory.Exists(targetPath))
            {
                MessageBox.Show(
                    "Scripts folder not found:\n" + targetPath +
                    "\n\nRun 'Pull Calculations' first to populate it.",
                    PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ── Preferred: add it the way a drag-and-drop does ──────────────
            // Adds the folder as a new root (others stay; already-present = no-op) and
            // shows the panel. See FolderWorkspace for why this is the reliable route.
            if (FolderWorkspace.AddFolder(nppHwnd, GetCurrentScintilla(), targetPath) &&
                FolderWorkspace.Contains(nppHwnd, targetPath))
            {
                PluginLog($"Toggle Folder as Workspace: '{targetPath}' added via folder drop");
                return;
            }
            PluginLog($"Toggle Folder as Workspace: folder drop not usable for '{targetPath}' — " +
                      "falling back to the workspace marker file");

            // ── Fallback strategy ─────────────────────────────────────────────
            // (used when Notepad++ is set to open a dropped folder's files instead)
            // NPP's "File > Open Containing Folder > Folder as Workspace" opens
            // the CURRENT active file's parent folder as workspace — no dialog,
            // instant. We exploit this by:
            //   1. Creating a tiny sentinel file inside targetPath.
            //   2. Opening that file in NPP (makes it the active tab).
            //   3. Triggering the "Folder as Workspace" command, which now uses
            //      targetPath as the containing folder.
            // This avoids all dialog automation complexity.
            // ─────────────────────────────────────────────────────────────────

            // Find the command ID for "Open Containing Folder > Folder as Workspace"
            int cmdId = FindContainingFolderWorkspaceCommand(nppHwnd);

            if (cmdId <= 0)
            {
                // NPP version has no such command → open Explorer as fallback
                System.Diagnostics.Process.Start("explorer.exe", "\"" + targetPath + "\"");
                MessageBox.Show(
                    "Notepad++ 'Folder as Workspace' command was not found.\n" +
                    "The folder has been opened in Windows Explorer instead:\n\n" + targetPath,
                    PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Sentinel file lives at the root of the local folder.
            string sentinelFile = Path.Combine(targetPath, PathConstants.WorkspaceSentinelName);
            try
            {
                if (!File.Exists(sentinelFile))
                    File.WriteAllText(sentinelFile,
                        "EPM Plugin workspace marker — safe to delete\n");
            }
            catch (Exception ex)
            {
                ShowError("Toggle Folder as Workspace",
                    "Could not create workspace marker file:\n" + ex.Message);
                return;
            }

            // Run the two-step sequence on a background thread so that
            // SendMessage to NPP is a safe cross-thread call (not re-entrant).
            string sentinelCapture = sentinelFile;
            int cmdCapture = cmdId;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    // Step A: open the sentinel file (synchronous — waits for NPP to
                    // process the open and make the file the active tab).
                    // NPPM_DOOPEN lParam = wchar_t* path
                    IntPtr pPath = Marshal.StringToHGlobalUni(sentinelCapture);
                    try
                    {
                        Win32.SendMessage(nppHwnd,
                            (uint)NppMsg.NPPM_DOOPEN,
                            IntPtr.Zero,
                            pPath);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pPath);
                    }

                    // Brief pause so NPP finishes activating the new tab
                    System.Threading.Thread.Sleep(150);

                    // Step B: post the "Folder as Workspace" command.
                    // The active file is now the sentinel inside targetPath, so NPP
                    // will open targetPath as the workspace root — exactly what we want.
                    Win32.PostMessage(nppHwnd,
                        0x0111u /* WM_COMMAND */,
                        (IntPtr)cmdCapture,
                        IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    ShowError("Toggle Folder as Workspace",
                        "Failed to open the workspace folder:\n" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------------ //
        //  Menu helper for OpenAsWorkspace
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Searches NPP's menu bar for "Folder as Workspace" nested inside the
        /// "Open Containing Folder" submenu.  That command opens the current
        /// active file's parent folder as workspace with no dialog — which is
        /// exactly what the sentinel-file approach relies on.
        ///
        /// NOTE: <see cref="GetMenuString"/> returns text with raw &amp; accelerator
        /// markers (e.g. "Containing &amp;Folder"), so we strip &amp; before comparing.
        ///
        /// Returns the Windows command ID, or 0 if not found.
        /// </summary>
        private static int FindContainingFolderWorkspaceCommand(IntPtr nppHwnd)
        {
            IntPtr hBar = Win32.GetMenu(nppHwnd);
            if (hBar == IntPtr.Zero) return 0;

            int barCount = Win32.GetMenuItemCount(hBar);
            var sb = new StringBuilder(256);
            const uint MF_BYPOSITION = 0x0400u;

            for (int i = 0; i < barCount; i++)
            {
                IntPtr hSub = Win32.GetSubMenu(hBar, i);
                if (hSub == IntPtr.Zero) continue;

                int subCount = Win32.GetMenuItemCount(hSub);
                for (int j = 0; j < subCount; j++)
                {
                    sb.Clear();
                    Win32.GetMenuString(hSub, (uint)j, sb, 256, MF_BYPOSITION);
                    string subName = sb.ToString().Replace("&", "");

                    // Only descend into the "Open Containing Folder" submenu
                    if (subName.IndexOf("Containing Folder",
                            StringComparison.OrdinalIgnoreCase) < 0) continue;

                    IntPtr hSubSub = Win32.GetSubMenu(hSub, j);
                    if (hSubSub == IntPtr.Zero) continue;

                    int subSubCount = Win32.GetMenuItemCount(hSubSub);
                    for (int k = 0; k < subSubCount; k++)
                    {
                        sb.Clear();
                        Win32.GetMenuString(hSubSub, (uint)k, sb, 256, MF_BYPOSITION);
                        if (sb.ToString().Replace("&", "")
                               .IndexOf("Folder as Workspace",
                                   StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int id = Win32.GetMenuItemID(hSubSub, k);
                            if (id > 0) return id;
                        }
                    }
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ //
        //  Status bar helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Writes the active connection name into the NPP status bar's doc-type
        /// section (section 0), appended to the current document language string.
        ///
        /// NPP updates section 0 before firing <c>NPPN_BUFFERACTIVATED</c> and
        /// <c>NPPN_LANGCHANGED</c>, so by the time this method runs the language
        /// name is already set — we read it via <see cref="GetCurrentLanguageName"/>
        /// and produce <c>"XML  ·  Vision Production"</c>.
        /// </summary>
        private static void UpdateStatusBar()
        {
            // NOTE: NPPM_GETBUFFERLANGTYPE crashes when called during NPPN_BUFFERACTIVATED
            // because NPP hasn't finished the buffer transition yet.  We therefore read the
            // language text that NPP already wrote to the status bar (it does so before
            // firing the notification) via GetStatusBarLangText(), then append our name.
            try
            {
                string connName = GetConnectionNameForActiveFile();
                if (connName == null) return;

                // NPP has already updated section 0 with the language name before sending
                // NPPN_BUFFERACTIVATED, so we read it directly from the status bar window.
                string lang = GetStatusBarLangText();

                string text = string.IsNullOrEmpty(lang)
                    ? connName
                    : lang + "  ·  " + connName;

                IntPtr pText = Marshal.StringToHGlobalUni(text);
                try
                {
                    Win32.SendMessage(
                        PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_SETSTATUSBAR,
                        (IntPtr)StatusBarDocType,
                        pText);
                }
                finally { Marshal.FreeHGlobal(pText); }
            }
            catch { }
        }

        /// <summary>
        /// Reads the current text of section 0 (language/doc-type) of NPP's own status bar
        /// by finding the status bar child window and sending SB_GETTEXT.
        ///
        /// NPP writes the language name to the status bar BEFORE it fires
        /// NPPN_BUFFERACTIVATED, so this is always safe to call from a notification handler.
        /// Returns an empty string on any failure.
        /// </summary>
        private static string GetStatusBarLangText()
        {
            try
            {
                // NPP's status bar is a child window with class "msctls_statusbar32".
                // We walk NPP's immediate children to find it, then use SB_GETTEXT.
                IntPtr nppHwnd = PluginBase.nppData._nppHandle;
                IntPtr statusBar = IntPtr.Zero;

                Win32.EnumChildWindows(nppHwnd, (hwnd, _) =>
                {
                    var cls = new StringBuilder(64);
                    Win32.GetClassName(hwnd, cls, 64);
                    if (cls.ToString().Equals("msctls_statusbar32",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        statusBar = hwnd;
                        return false; // stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);

                if (statusBar == IntPtr.Zero) return string.Empty;

                // SB_GETTEXTW = WM_USER + 13 = 0x040D  (Unicode variant — must match the
                // SETTEXTW that NPP uses; the ANSI SB_GETTEXT=0x0402 returns garbage).
                // wParam = zero-based part index (0 = leftmost = DOC_TYPE / language).
                // lParam = WCHAR* buffer to receive text. SB_GETTEXTW takes no buffer size, so
                // the buffer is sized from SB_GETTEXTLENGTHW (WM_USER + 12, length in the low
                // word) — the text includes the connection name, which has no length limit.
                const uint SB_GETTEXTW = 0x040Du, SB_GETTEXTLENGTHW = 0x040Cu;
                int length = (int)((long)Win32.SendMessage(statusBar, SB_GETTEXTLENGTHW, IntPtr.Zero, IntPtr.Zero) & 0xFFFF);
                var sb = new StringBuilder(length + 1);
                Win32.SendMessage(statusBar, SB_GETTEXTW, IntPtr.Zero, sb);
                return sb.ToString().Trim();
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Determines the connection name to display for the currently active file.
        ///
        /// Tries to match the active file's full path against every configured
        /// <see cref="ConnectionConfig.LocalFolderPath"/> using a longest-prefix
        /// rule (handles nested project structures).  When a match is found the
        /// corresponding connection is automatically set as active so that plugin
        /// commands (Pull, Deploy …) target the correct project without any manual
        /// switching.
        ///
        /// Returns the active connection name — which is the auto-detected one, unless
        /// the user picked another from the toolbar drop-down since the last project change.
        ///
        /// Returns <c>null</c> when no connections are configured at all.
        /// </summary>
        private static string GetConnectionNameForActiveFile()
        {
            var all = ConfigManager.GetAll();
            if (all.Count == 0) return null;

            string filePath = GetActiveFilePath(silent: true);

            if (!string.IsNullOrEmpty(filePath))
            {
                // Longest-prefix match — handles nested project folders correctly
                ConnectionConfig best = null;
                int bestLen = -1;
                foreach (var c in all)
                {
                    if (string.IsNullOrEmpty(c.LocalFolderPath)) continue;
                    // Normalise: ensure the folder ends with a separator so we don't
                    // accidentally match "C:\ProjectA" against "C:\ProjectABC\file.csc"
                    string folder = c.LocalFolderPath.TrimEnd('\\', '/') +
                                    Path.DirectorySeparatorChar;
                    if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                        folder.Length > bestLen)
                    {
                        best    = c;
                        bestLen = folder.Length;
                    }
                }

                if (best != null)
                {
                    // Only persist the change when the active connection actually differs
                    // — avoids a JSON read+write on every tab switch.
                    if (!string.Equals(_lastAutoDetectedFolder, best.LocalFolderPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ConfigManager.SetActive(best.LocalFolderPath);
                        _lastAutoDetectedFolder = best.LocalFolderPath;
                    }
                }
            }

            // The tab's own connection (what Variables, Navigator, Validate, Deploy and Run
            // use); for a tab outside every connection folder, the toolbar's active one.
            var shown = ConnectionOfActiveTab() ?? ConfigManager.GetActive();
            return shown == null ? null : DisplayName(shown) + (shown.ReadOnly ? " (read only)" : "");
        }

        // ------------------------------------------------------------------ //
        //  Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns the full path of the active Notepad++ tab using three independent
        /// strategies, each falling back to the next if the previous returns empty.
        ///
        /// Strategy 1 – NPPM_GETFULLCURRENTPATH (RUNCOMMAND_USER+1)
        /// Strategy 2 – combine NPPM_GETCURRENTDIRECTORY + NPPM_GETFILENAME
        /// Strategy 3 – NPPM_GETCURRENTBUFFERID → NPPM_GETFULLPATHFROMBUFFERID
        ///
        /// When <paramref name="silent"/> is false (default) a diagnostic dialog is
        /// shown on failure.  Pass true to suppress the dialog (caller handles errors).
        /// </summary>
        private static string GetActiveFilePath(bool silent = false)
        {
            // 32 768 TCHARs — well above MAX_PATH and extended-length paths
            const int BufLen = 32768;
            var zeros = new byte[(BufLen + 1) * 2];
            IntPtr pBuf = Marshal.AllocHGlobal((BufLen + 1) * 2);

            string fullPath = string.Empty;
            string dirPath  = string.Empty;
            string fileName = string.Empty;

            try
            {
                // ── Strategy 1: NPPM_GETFULLCURRENTPATH ───────────────────────
                Marshal.Copy(zeros, 0, pBuf, zeros.Length);
                Win32.SendMessage(PluginBase.nppData._nppHandle,
                    (uint)NppMsg.NPPM_GETFULLCURRENTPATH, (IntPtr)BufLen, pBuf);
                fullPath = (Marshal.PtrToStringUni(pBuf) ?? string.Empty).Trim();

                // ── Strategy 2: GETCURRENTDIRECTORY + GETFILENAME ─────────────
                if (string.IsNullOrEmpty(fullPath))
                {
                    Marshal.Copy(zeros, 0, pBuf, zeros.Length);
                    Win32.SendMessage(PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_GETCURRENTDIRECTORY, (IntPtr)BufLen, pBuf);
                    dirPath = (Marshal.PtrToStringUni(pBuf) ?? string.Empty).Trim();

                    Marshal.Copy(zeros, 0, pBuf, zeros.Length);
                    Win32.SendMessage(PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_GETFILENAME, (IntPtr)BufLen, pBuf);
                    fileName = (Marshal.PtrToStringUni(pBuf) ?? string.Empty).Trim();

                    if (!string.IsNullOrEmpty(dirPath) && !string.IsNullOrEmpty(fileName))
                        fullPath = Path.Combine(dirPath, fileName);
                }

                // ── Strategy 3: BufferID → NPPM_GETFULLPATHFROMBUFFERID ───────
                if (string.IsNullOrEmpty(fullPath))
                {
                    IntPtr bufferId = Win32.SendMessage(PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_GETCURRENTBUFFERID, IntPtr.Zero, IntPtr.Zero);

                    if (bufferId != IntPtr.Zero)
                    {
                        Marshal.Copy(zeros, 0, pBuf, zeros.Length);
                        Win32.SendMessage(PluginBase.nppData._nppHandle,
                            (uint)NppMsg.NPPM_GETFULLPATHFROMBUFFERID, bufferId, pBuf);
                        fullPath = (Marshal.PtrToStringUni(pBuf) ?? string.Empty).Trim();
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }

            if (!string.IsNullOrEmpty(fullPath))
                return fullPath;

            // ── Strategy 4: parse full path from NPP's own title bar ──────────
            // NPP always renders the active file's full path in its title as:
            //   "C:\path\to\file.ext - Notepad++"
            // GetWindowText bypasses the NPPM message API entirely, which avoids
            // the reentrancy issue that causes Strategies 1–3 to return empty.
            {
                var titleSb = new StringBuilder(32768);
                Win32.GetWindowText(PluginBase.nppData._nppHandle, titleSb, 32768);
                string title = titleSb.ToString();
                int nppIdx = title.LastIndexOf(" - Notepad++", StringComparison.OrdinalIgnoreCase);
                if (nppIdx > 0)
                {
                    string candidate = title.Substring(0, nppIdx).Trim();
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            if (silent) return null;

            MessageBox.Show(
                "Could not determine the active file path.\n\n" +
                "Make sure the active tab is a saved .csc or .js file, then try again.",
                PluginName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        /// <summary>
        /// Reads the full text of the currently active Scintilla document directly from
        /// the editor buffer — no disk save required.  Returns UTF-8 decoded text.
        /// </summary>
        private static string GetScintillaText()
        {
            byte[] rawBytes = GetScintillaBytes(GetCurrentScintilla());

            // Calc Manager files are UTF-8 XML; fall back to system ANSI on decode failure
            try   { return Encoding.UTF8.GetString(rawBytes); }
            catch { return Encoding.Default.GetString(rawBytes); }
        }

        // ------------------------------------------------------------------ //
        //  Shared by Deploy Active Tab and Execute ("deploy my changes first")
        // ------------------------------------------------------------------ //

        /// <summary>The pulled rule a script tab belongs to.</summary>
        private sealed class ActiveRule
        {
            public string PlanType, Cube, RuleName;
        }

        /// <summary>
        /// The editor content to deploy, after the pre-deploy validations: unsaved changes
        /// are saved first (or the user cancels), an empty document is refused, and a
        /// Groovy script must pass local validation unless the user ticks "deploy anyway".
        /// Returns null — after telling the user why — when nothing should be deployed.
        /// </summary>
        private static string GetContentToDeploy(ConnectionConfig cfg, string title)
        {
            if (!EnsureActiveTabSaved(title)) return null;

            string content = GetScintillaText();
            if (string.IsNullOrWhiteSpace(content))
            {
                ShowError(title, "The active document appears to be empty.");
                return null;
            }

            ActiveScript script = GetActiveScript();
            if (script.Kind != ScriptKind.None)
            {
                List<ValidationIssue> issues = ValidateScript(cfg, content, script);
                if (issues.Count > 0)
                {
                    using (var gate = new ValidationForm(ValidationForm.Mode.Gate, script.FileName, issues))
                    {
                        bool deployAnyway = gate.ShowDialog(NppOwner) == DialogResult.OK;
                        if (gate.JumpTo != null) GoToIssue(gate.JumpTo);
                        if (!deployAnyway) return null;
                    }
                    PluginLog($"{title}: deployed despite {issues.Count} local validation issue(s) — user ticked 'deploy anyway'");
                }
            }
            return content;
        }

        // ------------------------------------------------------------------ //
        //  Local validation (Groovy .js and Calc script .csc)
        // ------------------------------------------------------------------ //

        private sealed class ActiveScript
        {
            public ScriptKind Kind;
            public string     FileName;
            public bool       IsTemplate;   // under …\Templates\, or a graphical rule's step — a fragment
        }

        private static ActiveScript GetActiveScript()
        {
            string path = GetActiveFilePath(silent: true) ?? "";
            string ext  = Path.GetExtension(path).ToLowerInvariant();
            return new ActiveScript
            {
                Kind       = ext == ".js" ? ScriptKind.Groovy : ext == ".csc" ? ScriptKind.CalcScript : ScriptKind.None,
                FileName   = path.Length == 0 ? "this script" : Path.GetFileName(path),
                IsTemplate = path.IndexOf(@"\Templates\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             GraphicalRule.RuleFolderOf(path) != null,
            };
        }

        /// <summary>Validates editor content (legacy raw-XML files are unwrapped first, as Deploy does).</summary>
        private static List<ValidationIssue> ValidateScript(ConnectionConfig cfg, string content, ActiveScript script)
        {
            string text = PackageBuilder.ResolveScriptText(content);
            VariableDictionary dict = cfg == null ? null : VariableDictionary.LoadOrRebuild(cfg.LocalFolderPath);

            // The rule's cube (from its path) and the connection's pulled metadata
            string cube = null;
            try { cube = PackageBuilder.ParseScriptPath(GetActiveFilePath(silent: true) ?? "").cube; } catch { }
            var context = ValidationContext.For(cfg?.LocalFolderPath, cube);

            return script.Kind == ScriptKind.Groovy
                ? GroovyValidator.Validate(text, GroovyValidationConfig.Load(), dict, context)
                : CalcScriptValidator.Validate(text, CalcScriptCatalog.Load(), dict, script.IsTemplate, context);
        }

        /// <summary>The editable rules file behind a script kind's validation.</summary>
        private static string RulesFileFor(ScriptKind kind)
        {
            if (kind == ScriptKind.Groovy)
            {
                if (!File.Exists(GroovyValidationConfig.FilePath)) GroovyValidationConfig.Save(GroovyValidationConfig.Defaults());
                return GroovyValidationConfig.FilePath;
            }
            if (!File.Exists(CalcScriptCatalog.FilePath)) CalcScriptCatalog.Save(CalcScriptCatalog.Defaults());
            return CalcScriptCatalog.FilePath;
        }

        /// <summary>Moves the caret to an issue's line and column and focuses the editor.</summary>
        private static void GoToIssue(ValidationIssue issue)
        {
            IntPtr sci = GetCurrentScintilla();
            int line = Math.Max(issue.Line - 1, 0);
            Win32.SendMessage(sci, (uint)SciMsg.SCI_ENSUREVISIBLEENFORCEPOLICY, (IntPtr)line, IntPtr.Zero);
            IntPtr lineStart = Win32.SendMessage(sci, (uint)SciMsg.SCI_POSITIONFROMLINE, (IntPtr)line, IntPtr.Zero);
            // Columns count characters; Scintilla positions count bytes — let it convert
            IntPtr pos = Win32.SendMessage(sci, (uint)SciMsg.SCI_POSITIONRELATIVE, lineStart, (IntPtr)Math.Max(issue.Column - 1, 0));
            if (pos.ToInt64() <= 0 && issue.Column > 1) pos = lineStart;
            Win32.SendMessage(sci, (uint)SciMsg.SCI_GOTOPOS, pos, IntPtr.Zero);
            Win32.SendMessage(sci, (uint)SciMsg.SCI_GRABFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Works out which pulled rule the active tab is: from its file path, or — when NPP
        /// has no path for the buffer (restored from session, file moved/renamed) — from the
        /// rule name in the content, searched for in the connection's scripts folder.
        /// Returns null, after showing why, when it can't tell.
        /// </summary>
        private static ActiveRule ResolveActiveRule(ConnectionConfig cfg, string title, string content)
        {
            string currentPath = GetActiveFilePath(silent: true);

            if (currentPath == null)
            {
                string ruleGuess = ParseRuleNameFromContent(content);
                if (!string.IsNullOrEmpty(ruleGuess))
                {
                    string fileName = PathConstants.ToFileName(ruleGuess);
                    currentPath = SearchCalcManagerFolder(cfg, fileName + ".csc")
                               ?? SearchCalcManagerFolder(cfg, fileName + ".js");
                }
            }

            if (currentPath == null)
            {
                ShowError(title,
                    "Could not locate this file in your connection's scripts folder.\n\n" +
                    "Make sure the rule has been pulled (Pull Calculations) and open it from:\n" +
                    "  {Local Folder}\\CalcManager\\{Connection}.Scripts\\Planning\\{PlanType}\\{Cube}\\Rules\\{Name}.csc|js");
                return null;
            }

            try
            {
                var (planType, cube, ruleName) = PackageBuilder.ParseScriptPath(currentPath);
                return new ActiveRule { PlanType = planType, Cube = cube, RuleName = ruleName };
            }
            catch (EpmException ex)
            {
                ShowError(title, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// When the active tab has unsaved changes, asks "Save and deploy" / "Cancel" and
        /// saves on the former. Returns true when the tab is saved (or already was), false
        /// when the user cancelled or the save did not happen (e.g. Save As dismissed).
        /// </summary>
        private static bool EnsureActiveTabSaved(string title)
        {
            // Capture the tab's editor and file up front, before any dialog can move focus
            IntPtr sci  = GetCurrentScintilla();
            string path = GetActiveFilePath(silent: true);
            bool IsModified() =>
                Win32.SendMessage(sci, (uint)SciMsg.SCI_GETMODIFY, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;

            bool modified = IsModified();
            PluginLog($"{title}: save check - path='{path}' modified={modified}");
            if (!modified) return true;

            string name = string.IsNullOrEmpty(path) ? "This document" : Path.GetFileName(path);
            using (var dlg = new UnsavedChangesForm(name))
                if (dlg.ShowDialog(NppOwner) != DialogResult.OK) return false;

            // Save THIS file by its full path (NPPM_SAVEFILE) rather than "whatever buffer
            // NPP considers current" (NPPM_SAVECURRENTFILE), which depends on view focus.
            bool rooted = !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && File.Exists(path);
            IntPtr saveResult;
            if (rooted)
            {
                IntPtr pPath = Marshal.StringToHGlobalUni(path);
                try
                {
                    saveResult = Win32.SendMessage(PluginBase.nppData._nppHandle,
                        (uint)NppMsg.NPPM_SAVEFILE, IntPtr.Zero, pPath);
                }
                finally { Marshal.FreeHGlobal(pPath); }
            }
            else
            {
                saveResult = Win32.SendMessage(PluginBase.nppData._nppHandle,
                    (uint)NppMsg.NPPM_SAVECURRENTFILE, IntPtr.Zero, IntPtr.Zero);
            }

            // Saved = the editor no longer reports changes AND the file on disk now holds
            // exactly what the editor shows.
            bool stillModified = IsModified();
            bool diskMatches   = !rooted || DiskMatchesEditor(sci, path);
            PluginLog($"{title}: save via {(rooted ? "NPPM_SAVEFILE" : "NPPM_SAVECURRENTFILE")} " +
                      $"returned {saveResult} - stillModified={stillModified} diskMatches={diskMatches}");

            if (stillModified || !diskMatches)
            {
                ShowError(title,
                    "The file could not be saved, so nothing was deployed.\n\n" +
                    $"File: {path}\n\n" +
                    "Save it manually (Ctrl+S), check it isn't read-only, and try again.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when the file on disk has the same text as the editor (ignoring a UTF-8
        /// byte-order mark and line-ending style) — the proof that a save really happened.
        /// </summary>
        private static bool DiskMatchesEditor(IntPtr sci, string path)
        {
            try
            {
                bool utf8 = Win32.SendMessage(sci, (uint)SciMsg.SCI_GETCODEPAGE, IntPtr.Zero, IntPtr.Zero)
                                .ToInt32() == 65001;
                string editor = (utf8 ? Encoding.UTF8 : Encoding.Default).GetString(GetScintillaBytes(sci));

                string disk;
                using (var reader = new StreamReader(path, utf8 ? Encoding.UTF8 : Encoding.Default,
                                                     detectEncodingFromByteOrderMarks: true))
                    disk = reader.ReadToEnd();

                string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
                return Normalize(editor) == Normalize(disk);
            }
            catch (Exception ex)
            {
                PluginLog("DiskMatchesEditor failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Notepad++'s main window as dialog owner, so focus returns to it on close.</summary>
        private static IWin32Window NppOwner => new NppWindow(PluginBase.nppData._nppHandle);

        private sealed class NppWindow : IWin32Window
        {
            public NppWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        /// <summary>Handle of the Scintilla view that has focus (main or secondary).</summary>
        private static IntPtr GetCurrentScintilla()
        {
            // Find out which Scintilla view is currently active (0 = main, 1 = secondary)
            IntPtr pView = Marshal.AllocHGlobal(4);
            int viewIndex = 0;
            try
            {
                Marshal.WriteInt32(pView, 0);
                Win32.SendMessage(PluginBase.nppData._nppHandle,
                    (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, IntPtr.Zero, pView);
                viewIndex = Marshal.ReadInt32(pView);
            }
            finally { Marshal.FreeHGlobal(pView); }

            return viewIndex == 0
                ? PluginBase.nppData._scintillaMainHandle
                : PluginBase.nppData._scintillaSecondHandle;
        }

        /// <summary>The document's raw bytes, exactly as Scintilla stores them.</summary>
        private static byte[] GetScintillaBytes(IntPtr sciHandle)
        {
            // Query the byte length of the document (Scintilla stores bytes, not chars)
            int byteLen = Win32.SendMessage(sciHandle,
                (uint)SciMsg.SCI_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();

            if (byteLen <= 0) return new byte[0];

            IntPtr textBuf = Marshal.AllocHGlobal(byteLen + 2); // +2 = NUL terminator
            try
            {
                // Zero-init the terminator bytes
                Marshal.WriteInt16(textBuf + byteLen, 0);

                // SCI_GETTEXT: wParam = capacity (byteLen+1), lParam = char* buffer
                Win32.SendMessage(sciHandle,
                    (uint)SciMsg.SCI_GETTEXT, (IntPtr)(byteLen + 1), textBuf);

                byte[] rawBytes = new byte[byteLen];
                Marshal.Copy(textBuf, rawBytes, 0, byteLen);
                return rawBytes;
            }
            finally
            {
                Marshal.FreeHGlobal(textBuf);
            }
        }

        /// <summary>
        /// Inserts a runtime-prompt reference at the caret (replacing any selection) and,
        /// for Groovy, declares it in the /*RTPS: … */ header — as one undo step.
        /// </summary>
        private static void InsertVariableReference(string insertText, string variableName, bool groovy)
        {
            IntPtr sci = GetCurrentScintilla();
            bool utf8 = Win32.SendMessage(sci, (uint)SciMsg.SCI_GETCODEPAGE, IntPtr.Zero, IntPtr.Zero)
                            .ToInt32() == 65001;
            Encoding enc = utf8 ? Encoding.UTF8 : Encoding.Default;

            Win32.SendMessage(sci, (uint)SciMsg.SCI_BEGINUNDOACTION, IntPtr.Zero, IntPtr.Zero);
            try
            {
                SendText(sci, SciMsg.SCI_REPLACESEL, IntPtr.Zero, insertText, enc);

                if (groovy)
                {
                    // Latin-1 maps each byte to one char, so string indexes are byte
                    // positions — exactly what Scintilla's target range expects. The header
                    // syntax is pure ASCII, so multi-byte characters elsewhere can't confuse it.
                    string bytesAsText = Encoding.GetEncoding(28591).GetString(GetScintillaBytes(sci));
                    int eolMode = Win32.SendMessage(sci, (uint)SciMsg.SCI_GETEOLMODE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    string eol = eolMode == 1 ? "\r" : eolMode == 2 ? "\n" : "\r\n";

                    var edit = RtpsHeader.PlanAdd(bytesAsText, variableName, eol);
                    if (edit.HasValue)
                    {
                        // Scintilla keeps the caret after the inserted reference even
                        // when text before it changes length.
                        Win32.SendMessage(sci, (uint)SciMsg.SCI_SETTARGETRANGE,
                            (IntPtr)edit.Value.Start, (IntPtr)edit.Value.End);
                        byte[] repl = enc.GetBytes(edit.Value.Text);
                        SendText(sci, SciMsg.SCI_REPLACETARGET, (IntPtr)repl.Length, edit.Value.Text, enc);
                    }
                }
            }
            finally
            {
                Win32.SendMessage(sci, (uint)SciMsg.SCI_ENDUNDOACTION, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void SendText(IntPtr sci, SciMsg msg, IntPtr wParam, string text, Encoding enc)
        {
            byte[] bytes = enc.GetBytes(text ?? "");
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                Marshal.WriteByte(buf, bytes.Length, 0);
                Win32.SendMessage(sci, (uint)msg, wParam, buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Extracts the rule name from CALC Manager XML content by inspecting the
        /// name="…" attribute of the &lt;deployobject&gt; or &lt;rule&gt; element.
        /// Returns null when the content is not recognisable CALC Manager XML.
        /// </summary>
        private static string ParseRuleNameFromContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // <deployobject … name="RL TEST" …/> — most specific (CALC format)
            var m = Regex.Match(content,
                @"<deployobject\b[^>]*\bname=""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            // Fallback: <rule … name="RL TEST" …>
            m = Regex.Match(content,
                @"<rule\b[^>]*\bname=""([^""]+)""",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Searches the configured CalcManager scripts folder recursively for a file
        /// whose name exactly matches <paramref name="filename"/>.
        /// Returns the path when exactly one match is found, null otherwise
        /// (zero matches = not pulled yet; 2+ matches = ambiguous).
        /// </summary>
        private static string SearchCalcManagerFolder(ConnectionConfig cfg, string filename)
        {
            if (cfg == null || string.IsNullOrEmpty(filename)) return null;
            try
            {
                string root = Path.Combine(
                    PathConstants.GetScriptsRootPath(cfg.LocalFolderPath, cfg.Name),
                    PathConstants.PlanningDirName);
                if (!Directory.Exists(root)) return null;
                var matches = Directory.GetFiles(root, filename, SearchOption.AllDirectories);
                return matches.Length == 1 ? matches[0] : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Saves the active document and returns the path if the file is now on disk,
        /// or null (with a user-facing message) if the save was cancelled or failed.
        /// </summary>
        private static string SaveAndGetPath(string callerTitle)
        {
            string path = GetActiveFilePath(silent: false);
            if (path == null) return null;

            // Ask NPP to save (triggers "Save As" dialog for new buffers)
            Win32.SendMessage(
                PluginBase.nppData._nppHandle,
                (uint)NppMsg.NPPM_SAVECURRENTFILE,
                IntPtr.Zero, IntPtr.Zero);

            if (!File.Exists(path))
            {
                MessageBox.Show(
                    "The file could not be found on disk after saving.\n\n" +
                    "Path: " + path + "\n\n" +
                    "Make sure the file has been saved before running this command.",
                    callerTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }

            return path;
        }

        /// <summary>
        /// The connection whose local folder holds the active tab's file, or null (no
        /// connection folder holds it, or the tab has no file). Connection folders can't be
        /// nested, so there is at most one; the longest match is taken all the same.
        /// </summary>
        private static ConnectionConfig ConnectionOfActiveTab()
        {
            string path = GetActiveFilePath(silent: true);
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return ConfigManager.GetAll()
                    .Where(c => !string.IsNullOrWhiteSpace(c.LocalFolderPath) &&
                                path.StartsWith(c.LocalFolderPath.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.LocalFolderPath.Length)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// For commands about the active tab (Variables, Dimension Navigator): the tab's
        /// connection; for a tab outside every connection folder, the toolbar's active one.
        /// Null, after telling the user, when there is none.
        /// </summary>
        private static ConnectionConfig ConnectionForTab() => ConnectionOfActiveTab() ?? TryLoadConfig();

        /// <summary>
        /// For Deploy and Run: the connection of the rule in the active tab. A saved file
        /// must be inside a connection's folder (it is deployed there, not to whichever
        /// connection the toolbar shows); a tab with no file falls back to the toolbar's
        /// connection, whose scripts are then searched for the rule by name.
        /// </summary>
        private static ConnectionConfig ConnectionForRuleTab(string title)
        {
            ConnectionConfig cfg = ConnectionOfActiveTab();
            if (cfg != null) return cfg;

            string path = GetActiveFilePath(silent: true);
            if (string.IsNullOrEmpty(path)) return TryLoadConfig();

            if (ConfigManager.GetAll().Count == 0) return TryLoadConfig();   // explains there are none
            ShowError(title,
                $"'{Path.GetFileName(path)}' is not inside any connection's local folder, so it is not clear " +
                "which EPM connection it belongs to.\n\n" +
                "Open the rule from its connection's folder:\n" +
                "  {Local Folder}\\CalcManager\\{Connection}.Scripts\\Planning\\{PlanType}\\{Cube}\\Rules\\{Name}.csc|js");
            return null;
        }

        /// <summary>
        /// Returns the active <see cref="ConnectionConfig"/> (the toolbar's connection).
        /// Shows a user-facing message and returns null when no connection is
        /// configured or the settings file cannot be read.
        /// </summary>
        private static ConnectionConfig TryLoadConfig()
        {
            try
            {
                var cfg = ConfigManager.GetActive();
                if (cfg != null) return cfg;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not load connection settings:\n" + ex.Message +
                    "\n\nPlease reconfigure via 'Configure EPM Connection'.",
                    PluginName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }

            MessageBox.Show(
                "No connection settings found.\nPlease use 'Configure EPM Connection' to add one.",
                PluginName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        // Thread-safe message helpers (may be called from a Task thread)

        private static void ShowInfo(string title, string message)
        {
            // Must show MessageBox on the UI thread
            var nppHandle = PluginBase.nppData._nppHandle;
            if (nppHandle != IntPtr.Zero)
            {
                // Marshal to UI thread via native PostMessage is complex;
                // MessageBox from any thread works fine on Windows.
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void ShowError(string title, string message)
        {
            MessageBox.Show(message, title + " — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
