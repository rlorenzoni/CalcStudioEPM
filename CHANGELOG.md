# Changelog

Versions are `0.015.N`: the release (set in `CalcStudioEPM.csproj`) and the build number. The
DLL's file version is `0.15.N.0`.

## Unreleased

- **RuleSets as `.ruleset.ini`:** Pull writes each RuleSet's settings and runtime prompts
  (merge variables, hidden, value, override, validation value …) to
  `Planning\{app}\RuleSets\{RuleSet}.ruleset.ini`, and Deploy from that file puts the RuleSet
  together from the deployed version of the rules it runs and sends it to Calculation Manager.
  Before, a RuleSet was pulled as a `.js` holding the script of one of its rules.
- **Graphical rules — template prompts:** the values a template step passes to the template's
  design-time prompts are edited in `00 - Rule.ini` (`[NN Template X]`, and `[NN Template X ·
  Prompt]` for member ranges), with allowed values and run-time prompts noted.
- **Graphical templates** are pulled as read-only folders (step files and `00 - Template.ini`
  with the prompts and flow) instead of a `.csc` holding only their first script step.
- **Clearer flow** in `00 - Rule.ini`: rule steps name the rule they run, disabled steps are
  marked, conditional steps show their condition, and IFs built in the designer show their test
  (their empty `Test =` line, which was ignored on deploy, is gone).
- Files that must stay read only (template folders, formula steps the format can't hold) stay so
  when a connection's Read only box is unticked.

- **Update check:** *Check for updates* in the About box asks GitHub for the latest release, and
  at startup (at most once a day) a newer release is announced once, with a link to its page.
  It sends nothing about the user or their work; turn the startup check off in
  Configure… › General.

## v0.015 — first public release

- Published as open source under the GNU GPL v3.0.
- Renamed from `NppEPMPlugin` to `CalcStudioEPM`: DLL, plugin folder, project and namespaces.
  Settings are copied from `%AppData%\Notepad++\plugins\NppEPMPlugin` on the first start, and a
  warning appears while the old `plugins\NppEPMPlugin` copy is still installed.
- **About CalcStudio…** at the end of the Plugins menu: what the plugin does, version and build
  date, author and links, the "not an Oracle product" disclaimer, the license, and
  *Copy version info* for bug reports. Its text lives in `CalcStudioEPM/AboutInfo.cs`.
- Every build is stamped with its own build number (`CalcStudioEPM/BuildNumber.txt`).
- `build_deploy.bat` finds MSBuild with `vswhere` and works from any folder.
