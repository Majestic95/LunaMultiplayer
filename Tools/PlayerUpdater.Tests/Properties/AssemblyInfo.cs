using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// PlayerUpdater is annotated SupportedOSPlatform("windows6.1") so every type
// it exposes is platform-floored. The test assembly inherits the constraint
// transitively — declare the same attribute here so CA1416 doesn't fire on
// every test method that touches a PlayerUpdater API.
[assembly: SupportedOSPlatform("windows6.1")]

[assembly: AssemblyTitle("Luna Multiplayer Player Updater Tests")]
[assembly: AssemblyDescription("MSTest suite for Tools/PlayerUpdater/")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("LMP")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("4a3b2c1d-5e6f-4789-9abc-def012345678")]

[assembly: AssemblyVersion("0.31.0")]
[assembly: AssemblyFileVersion("0.31.0")]
