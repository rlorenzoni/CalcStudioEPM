# Changelog

Versions are `0.015.N`: the release (set in `CalcStudioEPM.csproj`) and the build number. The
DLL's file version is `0.15.N.0`.

## Unreleased

- **Tests and CI:** `CalcStudioEPM.Tests` (MSTest) with `test.bat`: pull round trip on an export of
  Oracle's demo application, IPM, validators, file names; Corpus tests on whole local pulls via
  `CALCSTUDIO_TEST_DATA` / `CALCSTUDIO_TEST_IPM`. GitHub Actions build and test every push and pull
  request, and a `v0.015.N` tag drafts a release with the plugin zip. `/p:KeepBuildNumber=true`
  builds without raising the build number.
- **Fixed — graphical steps nested deep in IFs:** a step's number alone could pass Windows'
  260-character path limit (`15 2-ELSE 01 2-ELSE 01 … 1-THEN 01`), and the pull skipped it. When
  the path would be too long the number is now written short (`15-E01-E01-…-T01`); `.flow.json`
  still maps the file to its step, so deploy is unchanged. Shallow steps keep their full names.
- **Fixed — empty rules:** a rule whose script was never written (an empty `<script/>`) was
  reported as changed right after every pull.
- **IPM — Auto Predict, calendars and events:** a connection option (*IPM*, off by default) adds
  *Update IPM configuration, calendar and events* to the Pull dialog (not ticked by default). It
  exports only those artifacts, as a package of their own (`{Local Folder}\IPM`), and writes
  `{Connection}.Scripts\IPM\Auto Predict\{job}.autopredict.ini` — sections in the order of EPM's
  Auto Predict wizard, members by name with autocomplete — plus `Calendars.ini` and `Events.xml`.
  Deploy imports them into Planning only (IPM is not part of Calculation Manager); Execute runs an
  Auto Predict job (`jobType: "Auto Predict"`), asking Force run and the paginated dimension and
  remembering the answers per job. A pull of the calculations leaves the IPM folder alone.
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
- **Member IDs and properties:** the dimension download keeps every field Planning sends for a
  member (its ID, storage, formula …), shown under *From EPM* in the Dimension Navigator's
  details; the navigator's search also finds a pasted member ID, and any part of a member's
  UDAs (shown in a new UDAs column). Pull dimensions and members again to get them.

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
