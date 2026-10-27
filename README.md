# CalcStudio for Oracle EPM

A Notepad++ plugin for Oracle EPM Cloud (Planning) Calculation Manager rules: pull them, edit them with syntax highlighting and local validation, deploy them and run them, without leaving the editor.

> **CalcStudio is an independent, community project. It is not an Oracle product** and is not affiliated with, endorsed, sponsored or supported by Oracle Corporation. It only uses the REST APIs that Oracle EPM Cloud publicly exposes, with your own credentials and the permissions of your EPM account. Oracle, Hyperion and Essbase are registered trademarks of Oracle and/or its affiliates.

Licensed under the [GNU GPL v3.0](LICENSE) · Author: [Rafael Lorenzoni](https://www.linkedin.com/in/rafaellorenzoni) ([GitHub](https://github.com/rlorenzoni)) · Version info: **Plugins › CalcStudio for Oracle EPM › About CalcStudio…**

The plugin installs as `plugins\CalcStudioEPM\CalcStudioEPM.dll` and keeps its settings in `%AppData%\Notepad++\plugins\CalcStudioEPM`. Versions before 0.015 were called `NppEPMPlugin`: see *Upgrading from NppEPMPlugin* under Install.

**Plugins › CalcStudio for Oracle EPM › Configure…** edits every setting in one place: plugin options and the Calc Script language, Calc Script and Groovy validation rules, syntax helpers, connections, and the data the plugin remembers.

---

## Features

| Button | Action |
|--------|--------|
| Gear   | **Configure EPM Connection** — set Service URL, Application, Username, Password, and local working folder. Credentials are encrypted with Windows DPAPI. Untick **Save password** to keep it out of the settings file: Pull, Deploy and Execute then ask for it, and you choose to use it for that action only, remember it for the current Notepad++ session (held encrypted in memory), or save it on the connection. The prompt starts on the choice you made last time for that connection. A session password that EPM rejects is forgotten, so the next action asks again. Also sets the connection's **Default Deploy Method** (see Deploy). The arrow next to the button switches the active connection, which Pull and Toggle Folder as Workspace use. Variables, Dimension Navigator, Local Syntax Validation, Deploy and Execute use the connection of the current tab instead: the one whose local folder holds the tab's file (the status bar shows it). For a tab outside every connection folder, Variables, the navigator and validation fall back to the active connection. Deploy and Execute refuse a saved file that is outside every connection folder, and search the active connection's scripts for a tab with no file. An open Dimension Navigator follows the tab's connection as you switch tabs. |
| Arrow  | **Pull Calculations** — exports all Calculation Manager objects as a ZIP via EPM REST API, downloads and extracts them, then converts each rule XML to a `.js` (Groovy) or `.csc` (CalcScript) source file. |
| Pink folder + | **Toggle Folder as Workspace** — adds the connection's scripts folder to Notepad++'s *Folder as Workspace* panel, or removes it if it is already there. After a Pull, a scripts folder that was in the panel is removed and re-added so it shows the fresh files. |
| {x}    | **Variables** — lists every runtime-prompt variable of the connection (compiled by Pull into `CalcManager\variables.json`) with type, dimension, prompt and how many rules use it. Inserts the selected one at the cursor: `{Name}` in Calc scripts; in Groovy via user-editable syntax helpers (e.g. `rtps.Name`), also adding it to the `/*RTPS: */` header. Deploy adds/removes the matching runtime prompts in the rule XML. |
| Deploy | **Deploy Active Tab** — saves the tab (asking first), runs Local Syntax Validation, and imports the rule into EPM Cloud with a temporary LCM snapshot. The button uses the connection's *Default Deploy Method*; its arrow offers both, the default marked "(Connection Default)": **Deploy CM** — Calculation Manager only (deploy to Planning from Calculation Manager afterwards); **Deploy CM+HP** — Calculation Manager plus the Planning (HP) copy of the rule. For the Planning copy every `%Template(...)` call is inlined the way Calculation Manager does it (between `/* Start Template:… */` and `/* End Template:… */`), using the pulled template. Only simple templates can be inlined: called with `dtps:=()`, and a single plain script (no DTP declarations, no graphical components). Otherwise a red **ACTION NEEDED** alert lists the templates and offers a Calculation Manager-only deploy, after which you must deploy the rule to Planning from Calculation Manager. |
| Play + cloud | **Execute Deployed Calculation** — runs the active rule as deployed in EPM Cloud. Warns when the editor has undeployed changes and can deploy them first (always trying CM+HP, since the rule runs from Planning), and when the rule's last deploy from Notepad++ reached Calculation Manager only. Asks for the runtime prompts the deployed rule has (remembering the last values per connection), runs the RULES job, and shows its status and the messages EPM recorded. |
| Green tick | **Local Syntax Validation** — a simple local, offline syntax check of the active rule (not an EPM validation; a notice says so until you tick "Don't show me again", which unlocks after three views). **Groovy (.js):** unbalanced brackets, unterminated strings/comments, undeclared variables (incl. script-level `def`s used inside methods), unknown functions, assignments without `def`, `rtps.X` missing from the `/*RTPS: */` header, header prompts unknown to the variable dictionary, `.member`/`.members` on the wrong prompt type, restricted calls — rules in `groovy_validation.json`. **Calc script (.csc):** FIX/FIXPARALLEL/EXCLUDE/LOOP/IF…ELSEIF…ELSE…ENDIF blocks open and close in order (IF inside a member block), brackets balance, functions exist with the right number of parameters, SET options are known, `{prompts}` are in the variable dictionary — functions, blocks, commands and SET options in `calcscript_catalog.json` (templates skip the block checks, being fragments). Both files live in `%AppData%\Notepad++\plugins\CalcStudioEPM` and open with "Edit rules…". Also runs before every deploy; problems block it unless "Ignore local validation and deploy anyway" is ticked. |

---

**Calc Script - Essbase language.** At startup the plugin installs a User Defined Language for `.csc` files into Notepad++'s `userDefineLangs` folder, if it isn't there yet: `Calc Script - Essbase` plus a `(dark mode)` variant that Notepad++ picks automatically in dark mode. It is generated from `calcscript_catalog.json`, so it colours every function Local Syntax Validation knows (Essbase, `@CALCMGR…`, Planning), commands, SET options and their values, `#MISSING`, `&SubstitutionVariables`, `{RuntimePrompts}`, `"member names"`, comments and `%Template(...)` calls, and folds `FIX` / `FIXPARALLEL` / `EXCLUDE` / `LOOP` and `IF` / `ELSEIF` / `ELSE` blocks. Colours set only the text, so background and font follow your theme. Notepad++ reads languages at startup, so restart once after the first install. To regenerate the files (e.g. after editing the catalog), delete `Calc Script - Essbase*.udl.xml` and restart. Remove `csc` from any other user language's extensions so this one is used. `/* */` is defined as a delimiter rather than a UDL comment: Notepad++'s UDL lexer misplaces every later fold point after each comment it styles. Because of that, the plugin itself handles Notepad++'s comment commands for `.csc` files (Ctrl+K toggle, Ctrl+Shift+K uncomment, Ctrl+Shift+Q block comment, and the Edit › Comment/Uncomment menu, on whatever keys you've mapped them to), wrapping lines in `/* … */`.

**More local validation** (every rule has a severity in Configure… › Calc Script / Groovy validation › Rules; any can be set to off):
- **Calc Script:**
  - `%Template` calls: name / application / plantype present, the template is in the last pull, every DTP passed is declared by the template.
  - Commands inside a `"Member"( … )` block.
  - `=` instead of `==` in IF / ELSEIF.
  - Nested FIXPARALLEL; THREADVAR without FIXPARALLEL.
  - DATACOPY without TO; empty `FIX ()`.
  - A formula or command not ended with `;`: another statement starts on the next line, an `ENDIF` / `ENDFIX` / `ELSE` follows it, its `"Member"( … )` block closes, or the script ends. FIX / IF / LOOP / EXCLUDE, their ENDs and member blocks need no `;`. Expressions may span lines when a line starts or ends with an operator.
  - Curly quotes, en/em dashes and non-breaking spaces in code.
- **Groovy:**
  - Methods or properties the receiver's EPM type doesn't have, and calls whose argument count matches no overload (names the API reference misses go in *Known API members*).
  - Deprecated EPM methods.
  - Grids from `loadGrid(…)` that are never closed.
  - `/*RTPS: */` prompts the script never reads.
  - `=` in `if (…)`; `${…}` inside `'single-quoted'` strings.
  - More sandbox-blocked patterns (URL, sockets, GroovyShell / Eval, System properties, metaClass changes).
- **Against EPM metadata:** every Pull also downloads the substitution variables and user variables, in parallel with the export. Calc Script `&Variables`, Groovy `getSubstitutionVariable…("X")` and `&X` inside calc text, and `getUserVariable("X")` are then checked, including a variable defined only for other cubes, or a user variable on the wrong dimension.
- **Dimension members:** if the connection's *Download dimensions and members on Pull* box is ticked (Connections window), each Pull can update the dimensions and members too (the second pull option). They come from `plantypes`, then `plantypes/{cube}/dimensions`, then `…/dimensions/{dim}` for every cube, because Planning returns only the members valid in the cube asked about. They are saved per cube in `{local folder}\.epm\`, and are used to check member names: quoted members in FIX lists, member functions and formulas; `getDimension("D")` and `.getMember("X")` in Groovy. A member that exists but isn't valid in the rule's cube is reported separately. Unquoted names have their own rule, off by default.

**Dimension Navigator** (menu and toolbar) browses the dimensions downloaded on Pull, without going to EPM. It stays open next to the script.
- **Browse:** pick a cube and a dimension, then walk that cube's hierarchy ("(all cubes)" shows the union), which loads as you expand so large dimensions stay fast. Shared members show in grey.
- **Search:** find members by name or alias.
- **Details:** alias, parent, full path, children, level 0, shared positions, and the cubes that use the dimension.
- **Insert:** double-click or Insert puts the member, quoted by default, at the cursor.

The navigator needs the connection's *Download dimensions and members on Pull* option. Dimensions downloaded before the hierarchy was kept show as a flat list until the next dimension pull.

**Autocomplete** (both can be switched off in Configure… › General):
- **Calc Script (`.csc`):** `@` lists the catalog's functions and shows the chosen one's syntax. `SET ` lists SET options. `{` lists runtime prompts: the ones this script already uses, a separator, then the connection's other variables, and adds the closing `}`. Typing inside `"…"` where a member belongs (`FIX("…`, `@RELATIVE("…`, cross-dims, assignments) lists the rule's cube's members and dimensions as `Member - Alias` (the default alias). It matches what was typed against the name, then (after a separator) against the alias, so typing `Profit (L` offers `GL_PAT - Profit (Loss) After Tax`. Picking one always inserts the member name and adds the closing quote. It is skipped in comments, text arguments such as `@CONCATENATE`, `%Template` arguments and `SET` / `DATAEXPORT` statements. This needs downloaded dimensions; without them, Notepad++'s own word completion is left alone.
- **FIX structure:** typing `(` after `FIX` or `EXCLUDE` (nothing else on the line) offers the rule's cube's dimensions as a commented list, one `/*Dimension*/` line each, aligned and indented under the FIX, with the cursor on the first one. Sparse dimensions come first, then (after a blank line) the dense ones marked `- Dense`; attribute dimensions are left out. Dense / sparse is what Planning reports for each dimension of the cube (the `density` field of `plantypes/{cube}/dimensions`, kept by the dimension download); the Dimension Navigator shows it as **Dense in {cube} (from EPM)**. For dimensions downloaded before that was kept, Account- and Period-type dimensions count as dense until the next dimension download, and the Navigator's box lets you set it (kept in `{Local Folder}\.epm\density.json`). Needs downloaded dimensions.
- **Parameter hints (both languages):** typing `(` after an Essbase function or an EPM method shows its syntax (every overload for Groovy) and highlights the parameter being typed. The highlight moves with each `,` and the hint closes at `)`. Colours follow the editor theme, so hints are readable in dark mode.
- **Groovy rules:** after a dot, the members of that object's EPM type come first (properties such as `application` for `getApplication()`, then methods), then a separator and every other EPM method. The chosen method's signatures are shown. The type is worked out from `operation` / `rule`, typed declarations (`Cube c = …`), `def x = <expression>`, chained calls (`operation.application.getCube("Plan1").`), and closure parameters over grid iterators and lists (`dataCellIterator().each { cell -> cell.`). `rtps.` lists the `/*RTPS: */` prompts first, then the other variables. Typing inside a `'…'` or `"…"` string lists members of every cube the same way as in Calc Script (`Member - Alias`, matched by name or alias). This also works on the Calc Script lines of a `"""…"""` block and after an escaped `\"`. The name is closed with the same quote it was opened with. `//` comments and `${…}` interpolation are skipped. Validation is unchanged. The API comes from Oracle's EPM Groovy Scripting Reference, compiled into `CalcStudioEPM/Resources/groovy_api.json` (signatures only, no Oracle description text) (see [`CalcStudioEPM/tools/groovy_api`](CalcStudioEPM/tools/groovy_api)).

All dialogs scale with the Windows display scaling (they are laid out for 100 % and scaled by the size text actually renders at) and can be resized by dragging their edges. Each dialog remembers its last size. The Pull / Deploy / Execute progress window can always be made wider, and taller while the details log or Response box is open, which then takes the extra space.

**Updates:** **About CalcStudio… › Check for updates** asks GitHub for the latest release of this repository. At startup the plugin does the same at most once a day, in the background, and announces each newer release once with a link to its page. The request goes to `api.github.com` and carries nothing about you, your connections or your rules. Turn the startup check off in **Configure… › General**.

---

## Prerequisites

- Windows (x64)
- [Notepad++](https://notepad-plus-plus.org/) 64-bit, version 8.x or later
- .NET Framework 4.8 runtime (included with Windows 10/11)
- An Oracle EPM Cloud (Planning) environment and an account allowed to use its REST APIs
- To build: Visual Studio 2022 or later, or the Visual Studio Build Tools, with the .NET desktop workload (MSBuild and the .NET Framework 4.8 targeting pack)

---

## Build

From the repository root:

```cmd
build_deploy.bat
```

It finds MSBuild with `vswhere`, restores the NuGet packages (Newtonsoft.Json) and builds **Release | x64**. The output is:

```
CalcStudioEPM\bin\x64\Release\net48\CalcStudioEPM.dll
CalcStudioEPM\bin\x64\Release\net48\Newtonsoft.Json.dll
```

You can also open `CalcStudioEPM\CalcStudioEPM.csproj` in Visual Studio and build **Release / x64**.

**DllExport:** the native exports Notepad++ needs (`setInfo`, `getFuncsArray`, `beNotified`, …) are added after compilation by [DllExport](https://github.com/3F/DllExport) 1.8.0 (MIT). The package is checked in under `CalcStudioEPM\packages\DllExport.1.8.0`, together with the compiled attribute stub the project file points to, so no DllExport wizard is needed.

### Versioning

The release version is set once, in `ReleaseVersion` in `CalcStudioEPM.csproj` (currently `0.015`). Every build raises the build number kept in `CalcStudioEPM\BuildNumber.txt` and stamps it on the DLL:

| Where | Format | Example |
|---|---|---|
| About box, README, GitHub release tag | `0.015.N` | `v0.015.2` |
| DLL file / assembly version (what Notepad++ Plugin Admin compares) | `0.15.N.0` | `0.15.2.0` |

For a new release change `ReleaseVersion` (keep three digits after the dot: `0.016`, … `0.100`). The build number keeps counting.

---

## Install into Notepad++

1. Locate (or create) the Notepad++ plugins folder:

   ```
   %ProgramFiles%\Notepad++\plugins\CalcStudioEPM\
   ```

   For portable installs:

   ```
   <NppDir>\plugins\CalcStudioEPM\
   ```

2. Copy `CalcStudioEPM.dll` and `Newtonsoft.Json.dll` into that folder: from a release zip, or from the build output (from the repository root):

   ```cmd
   xcopy /Y "CalcStudioEPM\bin\x64\Release\net48\CalcStudioEPM.dll" "%ProgramFiles%\Notepad++\plugins\CalcStudioEPM\"
   xcopy /Y "CalcStudioEPM\bin\x64\Release\net48\Newtonsoft.Json.dll" "%ProgramFiles%\Notepad++\plugins\CalcStudioEPM\"
   ```

3. Restart Notepad++.  
   The buttons appear on the toolbar and the commands under **Plugins > CalcStudio for Oracle EPM**.

### Upgrading from NppEPMPlugin (before 0.015)

The plugin was renamed from `NppEPMPlugin` to `CalcStudioEPM`.

- **Delete the old folder** `plugins\NppEPMPlugin\`. Otherwise Notepad++ loads both copies and shows the menu and toolbar twice (the new version warns about it at startup).
- **Settings move by themselves:** on its first start the new version copies `%AppData%\Notepad++\plugins\NppEPMPlugin` (connections, saved passwords, validation rules, window sizes …) to `%AppData%\Notepad++\plugins\CalcStudioEPM`. The old folder is left as a backup and can be deleted afterwards.
- **Keyboard shortcuts** you mapped to plugin commands (Settings › Shortcut Mapper › Plugin commands) are stored by Notepad++ under the DLL's name, so map them again.

---

## Configuration

1. Click the **Gear** toolbar button (or **Plugins > CalcStudio for Oracle EPM > Configure EPM Connection**).
2. Click **New**, or **Duplicate** to start from the selected connection (everything but its local folder is copied), and fill in:
   - **Service URL** — e.g. `https://planning-xxx.pbcs.us2.oraclecloud.com`
   - **Application** — Planning application name, e.g. `Vision`
   - **Username** — your identity domain username or email
   - **Password** — with *Save password* ticked, stored encrypted via Windows DPAPI (never in plain text); otherwise you are asked for it when needed
   - **Local Folder** — the working directory where scripts will be extracted. It can't be the same as, inside, or around another connection's folder: files must belong to exactly one connection.
   - **Default Deploy Method** — new connections try to deploy the Planning final calculation too (CM+HP)
   - **Validation** — new connections download dimensions and members on Pull, to check member names
   - **Read only** — for a connection to look at, not change (e.g. production). Pulled files under `{Local Folder}\CalcManager` are marked read-only on disk, so Notepad++ opens them locked. Deploy is disabled: its button and menu item are greyed out on that connection's tabs, and the command refuses. Execute runs the deployed version. Ticking or unticking it on Save applies it to the files already pulled; the status bar shows "(read only)" after the connection name.
3. Click **Test Connection** to verify credentials.
4. Click **Save**. Changing the local folder of a saved connection moves it there (files are not moved).

**Export… / Import…** move connections to another PC or a colleague. Export writes the ticked connections to a `.json` file. Saved passwords are included only if you give an encryption key: AES-256 with HMAC-SHA256, the key stretched with PBKDF2-SHA256. Without a key the file has no passwords. Import… › *From an exported connections file…* lists the file's connections, lets you change each local folder for this PC, and asks for the same key to import the passwords. Leave the key empty to import without them. A connection with the same local folder as an existing one replaces it; nested folders are refused as on Save. Import… › *From a project folder…* reads a legacy `.epm\config.dat`.

Connections are stored in `%APPDATA%\Notepad++\plugins\CalcStudioEPM\connections.json`.

---

## Pulling Calculations

Click the **Arrow (Download)** toolbar button, then choose what to update:

- **Update all calculations** (ticked by default): the Calculation Manager export below.
- **Update dimensions, members and substitution variables** (ticked by default; also the user variables): a quick download, used by validation, autocomplete and the Dimension Navigator. Connections that don't download dimensions show *Update substitution and user variables*.
- **Clear local copies** (unticked by default; only with the calculations): see *Saved changes that weren't deployed* below.

With only the second option ticked, the scripts folder is left alone: no export, no change check, no table of local changes.

The plugin will:

1. Submit a `Calculation Manager` migration export (snapshot) to EPM Cloud.
2. Poll for completion (up to 30 minutes).
3. Download the ZIP from the EPM outbox. If the download fails it is retried once, 5 seconds later.
4. Delete the snapshot from EPM Cloud, straight after the download and whether or not it worked, so it is never left behind. It is also deleted when the export itself fails or times out.
5. Extract to `{LocalFolder}\CalcManager\`. `{Connection}.Scripts` keeps its whole folder tree: only the files are deleted (its Folder as Workspace marker stays), the scripts are written into the existing folders, and only the folders left empty (rules or cubes no longer in EPM) are removed. So Folder as Workspace and Explorer just see files change, and expanded folders stay expanded.
6. Parse each rule XML and write:
   - Groovy rules → `{xmlDir}\{ruleName}.js`
   - CalcScript rules → `{xmlDir}\{ruleName}.csc`
7. Strip the inline script body from the XML (replaced with a `<!-- extracted to ... -->` comment) so the XML remains as metadata.

### Saved changes that weren't deployed

A pull replaces the scripts folder with EPM's version. Before it does, the plugin compares every file with the last pulled / deployed copy of its rule. For graphical rules, each step file and `00 - Rule.ini` is compared. If anything differs, or a file isn't in EPM at all, a table lists those files with two options each, and a *Select all* row for either:

- **Overwrite:** the file is replaced by EPM's version, as a pull always did.
- **Keep both** (preselected): the file is backed up to `{Local Folder}\.epm\backup\{date-time}\`. After the pull it is copied back beside EPM's version as `{name}.local.001{ext}`, e.g. `FIN - Calc Bridge.local.001.js` or `00 - Rule.local.001.ini`, so you can open both and compare them. A file EPM doesn't have comes back under its own name.

Your `*.local.NNN.*` copies are kept on later pulls; a new copy takes the next number (`.local.002`). To drop them, tick **Clear local copies** in the pull options (unticked by default). Cleared copies are not copied back, but stay in `.epm\backup`. If the pull fails after deleting the folder, kept files are put back under their own names. Cancel leaves everything as it is.

### Graphical rules

A rule built in Calculation Manager's designer (a flow of steps, not one script) is pulled as a folder: `…\Rules\{Rule}\`. Steps are numbered in flow order. Steps inside loops and member blocks get `01.01`; steps inside an IF get `01.01.01 1-THEN 01` / `01.01.01 2-ELSE 01` (THEN sorts first). Each level is one container: in `01.01.01`, FIX 01 holds member block 01, which holds IF 01.

| File | What it holds |
|---|---|
| `00 - Rule.ini` | The flow, as comments, then the editable values: `[Global range]` and `[NN FIX]` (the designer's member-range loops) list every dimension of the rule's cube, one `Dimension = members` line each, empty when not fixed (all of them once dimensions were downloaded; otherwise those the rule uses); `[NN Member block]` (`Member = …`); `[NN IF]` (`Test = …`, for an IF typed as text; one built in the designer, such as `Application = Is Multi Currency`, is shown in the flow); `[NN Template X]`: the values a template step passes to the template's design-time prompts, grouped by type, with a restricted list's allowed values and "asked at run time" noted, and the template's fixed settings (Application Type, Application, Essbase Mode) shown; `[NN Template X · Prompt]`: a member-range or cross-dimension prompt, one `Dimension = members` line each, like a FIX. The flow also names the rules a rule runs, marks disabled steps, and shows the condition of a step that only runs `when` a prompt has a value. |
| `NN - {caption}.csc` | A script step, exactly as Calculation Manager holds it |
| `NN - {caption}.formula.csc` | A formula step: one `"Destination" = expression;` per row, or `"Member";` for a member formula. A `/* comment */` just above a row (or an IF) is its comment, with `*\/` standing for `*/` inside it; `/*# … */` lines carry the step's caption, description and block labels. A formula step with settings this format can't hold is written read-only (with a note) and is edited in Calculation Manager |
| `.flow.json` | Which file is which step (component id) — bookkeeping, don't edit |

The order and kind of steps are changed in Calculation Manager only. A template step's prompt values are changed in `00 - Rule.ini`; the template itself in Calculation Manager.

**Graphical templates** are pulled the same way, as a read-only folder `…\Templates\{Template}\`: every step file, and `00 - Template.ini` with the template's design-time prompts (type, allowed values, defaults) and its flow. They show what a template does; Deploy refuses them, since templates are changed in Calculation Manager. (Before, such a template was pulled as a `.csc` holding only its first script step.) Script templates are still pulled as `.csc`.

**Deploy** from any of these files writes them back into the rule by component id and step, leaving everything else untouched. It validates every step file, lists what changed and asks before deploying. Graphical rules go to **Calculation Manager only**: deploy the rule to Planning from Calculation Manager afterwards. Only saved files are deployed. **Execute** warns when the folder has changes that aren't deployed. Step files are validated as fragments, like templates, since a step can sit inside a loop or member block. Member autocomplete and the Dimension Navigator work in them as in any `.csc`. In `00 - Rule.ini`, autocomplete works on the value after `=`:
- **FIX and global range lines:** only that dimension's members, shown as `Member - Alias` and matched by name or alias. A bare word (`Scenario = Fore`) also opens the list, and the pick is inserted quoted (`"Forecast"`).
- **Inside a function:** `@` lists the functions with their parameter hints; quoted names inside a function (`@RELATIVE("Over…`) list that dimension's members.
- **`Member =` and `Test =`:** list members of every dimension of the rule's cube.

### RuleSets

A RuleSet only runs existing rules (and other RuleSets) in order, so it isn't code to maintain. EPM's export of a RuleSet still embeds a full copy of every rule, template and runtime prompt it uses, and those copies go out of date as soon as one of the rules changes. So Pull writes just the RuleSet's definition, as `…\Planning\{app}\RuleSets\{RuleSet}.ruleset.ini`:

```ini
; Runs, in this order (in parallel):
;    1  Rule     OPF_Calculate Labor Expense Rule  · OEP_PFP
;    …

[RuleSet]
Description     =
Merge variables = true
Run in parallel = true

[Currency]
; Scope: Cube OEP_PFP · from rule OPF_Calculate Labor Expense Rule
Hidden                = true
Value                 =
Use as override value = false
Validation value      = "No Currency"
```

- **What you edit:** the settings in `[RuleSet]`, and one section per runtime prompt (Calculation Manager's *Variables* tab). Other properties Calculation Manager stores on a prompt or the RuleSet appear under their own names and can be edited too. Which rules run, and their order, change in Calculation Manager.
- **Deploy** (from the `.ini`) puts the RuleSet together at deploy time: its definition with your `.ini` applied, the definitions of the RuleSets it runs (with their own `.ini`), and the **deployed version** of every rule it runs and every rule those call (`%Rule`). The ids that link them are renumbered. It lists what changed, warns about member rules with saved changes that aren't deployed (the RuleSet uses their deployed version), checks whether someone changed the RuleSet in EPM since your pull, and deploys to **Calculation Manager only**: deploy it to Planning from Calculation Manager afterwards.
- **Checked against real exports:** for 57 RuleSets of six applications, the `.ini` reproduces each definition exactly, and the RuleSet put together from the pulled rules matches EPM's own export. The only differences are inside EPM's stored copy: a `plantype` property it leaves out on rule-scoped prompts, and copies of rules that no step runs any more.

From the start, the substitution / user variables (and, when asked, dimensions and members) download in parallel. The progress window shows each step as it runs; the parallel row shows its own live status (e.g. `dimensions 40 of 96`) and keeps updating even if the pull itself fails. Show details opens the full log.

---

## Changed in EPM since your pull

Before Deploy (and before Execute's "deploy my changes first"), the plugin asks EPM whether the rule was changed after your last pull, or after your own last deploy of it. If it was, it says who and when before you overwrite their work, e.g. *'TMP_RL_2' was changed in EPM by jdoe on 2026-10-22 14:05 (EPM time), after your last pull*. You can then cancel and pull first, or deploy anyway.

- **How it works:** it uses the **List Updated Artifacts** REST API (`POST /interop/rest/v2/migration/artifactupdates`, category *Calculation Manager*). Pull records what EPM reports, right after the export. Deploy compares the rule's latest `modifiedDate` with that baseline. EPM's times are only compared with each other, so the server's time zone doesn't matter.
- **Your own deploys:** a deploy from here becomes the rule's new baseline, so it is never reported as someone else's change.
- **Before Pull:** when the calculations are to be updated and nothing in Calculation Manager changed since your last pull (your own deploys aside), the plugin says *No calculations have changed in EPM since your last pull (…)* and offers **Update only dimensions and variables** (the default), **Pull anyway** or **Cancel**. If something changed, or EPM can't be asked, the pull just goes ahead.
- **In the Pull summary:** each pull says what changed in Calculation Manager since the previous one (your own deploys aside), e.g. *No calculations had changed in EPM since your last pull (…)*, or *3 calculations changed …: A, B, C*. Details are in the log.
- **Times:** EPM reports them in UTC (`10/23/2026 11:37:25`), and the plugin shows them as UTC.
- **Matching rules:** rules are matched by name, and by cube when EPM's path shows it.
- **When it can't check:** this API needs the Service Administrator role. If it can't be reached, or you haven't pulled with this version yet, Deploy goes ahead and the reason is logged.
- **Stored in:** `{Local Folder}\.epm\rule_changes.json`.

---

## Executing a Calculation

1. Open a `.js` or `.csc` rule file in Notepad++.
2. Click the **Play** toolbar button.
3. If the editor has changes that aren't deployed, the plugin warns and can deploy them first.
4. A dialog asks for the runtime prompts of the deployed rule (`{Prompt}` in Calc Script, `rtps.Name` in Groovy), prefilled with the last values used on this connection. Click **Execute**.
5. The plugin submits a `RULES` job to EPM Cloud and polls until it finishes.
6. The progress window shows the job status and the messages EPM recorded.

---

## Project Structure

```
LICENSE                       GNU GPL v3.0
THIRD-PARTY-NOTICES.md        Licenses of the bundled / referenced components
CHANGELOG.md
build_deploy.bat              Release x64 build
CalcStudioEPM/
  CalcStudioEPM.csproj         net48 x64 project; version stamping (StampBuildVersion)
  BuildNumber.txt             last build number
  Main.cs                     menu, toolbar and command handlers
  AboutInfo.cs                text and links of the About box (edit here)
  PathConstants.cs
  Properties/AssemblyInfo.cs
  NppPlugin/                  Notepad++ / Scintilla interop: DLL exports, autocomplete, comment commands, toolbar drop-downs
  Config/                     connections, DPAPI credentials, settings, Calc Script catalog and language, validation rules
  Epm/                        EPM REST client, pull / deploy / execute, LCM packages, graphical rules, metadata
  Epm/Validation/             local Calc Script and Groovy validation
  Forms/                      WinForms dialogs
  Resources/                  toolbar icons, groovy_api.json (EPM Groovy API signatures for autocomplete)
  tools/groovy_api/           scripts that rebuild groovy_api.json from the public EPM Groovy reference
  packages/DllExport.1.8.0/   DllExport build tooling (MIT)
```

---

## EPM REST API Reference

The main calls (Planning REST API v3; pull and deploy also use the Migration / LCM REST APIs under `/interop/rest/v1` and `v2`, and the List Updated Artifacts API):

| Action | Method | URL |
|--------|--------|-----|
| List applications | GET | `/HyperionPlanning/rest/v3/applications` |
| Submit job | POST | `/HyperionPlanning/rest/v3/applications/{app}/jobs` |
| Poll job status | GET | `/HyperionPlanning/rest/v3/applications/{app}/jobs/{jobId}` |
| Download outbox file | GET | `/interop/rest/v1/files/outbox/{fileName}` |

Authentication: HTTP Basic (`Authorization: Basic base64(user:password)`).

---

## Troubleshooting

| Problem | Likely cause | Fix |
|---------|-------------|-----|
| Plugin doesn't appear | Wrong bitness (32-bit Npp + 64-bit DLL) | Use 64-bit Notepad++ |
| `DllExport` attribute not found | DllExport package not wired up | Run the DllExport installer wizard |
| HTTP 401 on Test Connection | Wrong credentials | Check username format (domain.user or email) |
| Job never completes | Network timeout | Check VPN / firewall; increase timeout in `EpmClient.cs` |
| No .js/.csc files after Pull | XML doesn't match expected structure | Check `CalculationParser` output; XML structure varies by EPM version |

---

## License

CalcStudio for Oracle EPM is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License v3.0** as published by the Free Software Foundation. See [LICENSE](LICENSE).

It is distributed in the hope that it will be useful, but **without any warranty**; without even the implied warranty of merchantability or fitness for a particular purpose.

Copyright © 2026 Rafael Lorenzoni. Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

CalcStudio is not an Oracle product and is not affiliated with, endorsed, sponsored or supported by Oracle Corporation. Oracle, Hyperion, Essbase and other Oracle product names are registered trademarks of Oracle and/or its affiliates. Use it at your own risk, and try changes in a test environment before production.
