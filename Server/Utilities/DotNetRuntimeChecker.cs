using System;

namespace Server.Utilities
{
    /// <summary>
    /// Verifies that the server is running on the expected .NET runtime version.
    /// </summary>
    internal static class DotNetRuntimeChecker
    {
        /// <summary>
        /// Major version of the .NET runtime the server is built against (see TargetFramework in Server.csproj).
        /// </summary>
        private const int RequiredMajorVersion = 10;

        /// <summary>
        /// Friendly name of the required runtime, shown to the user if the check fails.
        /// </summary>
        private const string RequiredRuntimeName = ".NET 10.0 Runtime";

        /// <summary>
        /// Official Microsoft download page for the required runtime.
        /// </summary>
        private const string RuntimeDownloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0";

        /// <summary>
        /// Ensures the currently executing .NET runtime matches the required major version.
        /// If it does not, a clear message is written to the console and the process exits.
        /// </summary>
        public static void EnsureCorrectRuntimeOrExit()
        {
            var currentVersion = Environment.Version;
            if (currentVersion.Major == RequiredMajorVersion)
                return;

            Console.Error.WriteLine();
            Console.Error.WriteLine("========================================================================");
            Console.Error.WriteLine(" ERROR: Incorrect .NET runtime detected.");
            Console.Error.WriteLine("------------------------------------------------------------------------");
            Console.Error.WriteLine($" LunaServer requires the {RequiredRuntimeName} to run.");
            Console.Error.WriteLine($" Detected runtime version: {currentVersion}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(" Please download and install the correct runtime from:");
            Console.Error.WriteLine($"   {RuntimeDownloadUrl}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(" On that page, pick the \"Runtime\" (or \"ASP.NET Core Runtime\") download");
            Console.Error.WriteLine(" that matches your operating system and architecture, then re-run the");
            Console.Error.WriteLine(" server.");
            Console.Error.WriteLine("========================================================================");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press any key to exit...");

            try { Console.ReadKey(true); } catch { /* no interactive console */ }

            Environment.Exit(1);
        }
    }
}