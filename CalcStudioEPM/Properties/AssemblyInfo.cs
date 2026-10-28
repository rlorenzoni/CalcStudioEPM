using System.Reflection;
using System.Runtime.InteropServices;

// Version attributes are generated at build time (StampBuildVersion in CalcStudioEPM.csproj).
[assembly: AssemblyTitle("CalcStudio for Oracle EPM")]
[assembly: AssemblyDescription("CalcStudio for Oracle EPM — Calculation Manager rules in Notepad++. Not an Oracle product.")]
[assembly: AssemblyProduct("CalcStudio for Oracle EPM")]
[assembly: AssemblyCopyright("Copyright © 2026 Rafael Lorenzoni. Licensed under GPL-3.0.")]
[assembly: ComVisible(false)]

// The tests (CalcStudioEPM.Tests) exercise the internal pull / deploy / validation logic
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CalcStudioEPM.Tests")]
