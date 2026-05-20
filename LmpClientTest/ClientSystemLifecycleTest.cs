using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LmpClientTest
{
    /// <summary>
    /// Source-file-based lifecycle lints that catch the v4/v5 client-startup
    /// cascade class of bugs without requiring KSP to be alive.
    ///
    /// <para><b>Why source-based.</b> Both bugs we hit live in the client-side
    /// runtime ordering against KSP's Unity Update loop, which can't be reproduced
    /// in a regular .NET test harness (no headless KSP). The next-best thing is
    /// to enforce the conventions that, when violated, ALWAYS produce the bug.
    /// Reading the .cs source directly avoids IL inspection (no Mono.Cecil
    /// dependency) and gives readable failures pointing the next contributor at
    /// the line they need to fix.</para>
    ///
    /// <para><b>Bug history these tests pin.</b>
    /// <list type="bullet">
    ///   <item><b>v0.31.0-per-agency-private-4:</b> AgencySystem overrode
    ///         EnableStage=Handshaked, relied on the event-fire path, never got
    ///         enabled because NetworkSystem.NetworkUpdate consumes transient
    ///         states in the same Update tick. Agency button missing + funds=0.
    ///         Caught by <see cref="EveryClientSystemOverridingEnableStageHasExplicitEnableInNetworkSystem"/>.</item>
    ///   <item><b>v0.31.0-per-agency-private-5:</b> ShareCareerSystem._actionQueue
    ///         declared without field initializer; v5's earlier AgencySystem enable
    ///         exposed the null queue. NRE on join, every client kicked.
    ///         Caught by <see cref="CollectionFieldsInLifecycleSystemsAreEagerlyInitialized"/>.</item>
    /// </list></para>
    /// </summary>
    [TestClass]
    public class ClientSystemLifecycleTest
    {
        private static readonly Lazy<string> LmpClientSourceRoot = new Lazy<string>(LocateLmpClientSourceRoot);
        private static readonly Lazy<string> NetworkSystemSource = new Lazy<string>(LoadNetworkSystemSource);

        /// <summary>
        /// Source-of-truth list for which client-System base types participate in the
        /// lifecycle disciplines below. <c>System&lt;T&gt;</c> is the root; the
        /// generic variants for inbound-message handling all inherit from it.
        /// New shapes (e.g. a future <c>UpdatePushSystem&lt;...&gt;</c>) should be
        /// appended here so the lints cover them.
        /// </summary>
        private static readonly string[] LifecycleBaseClassNames =
        {
            "System",
            "MessageSystem",
            "SubSystem",
            "ShareProgressBaseSystem",
        };

        /// <summary>
        /// EnableStage lint: any client <c>System&lt;T&gt;</c> that overrides
        /// <c>EnableStage</c> to a value other than <c>ClientState.Running</c>
        /// (the base default) MUST also be explicitly enabled inside
        /// <c>NetworkSystem.NetworkUpdate</c>'s switch — because
        /// <c>NetworkUpdate</c> consumes transient states inside a single Update
        /// tick, the event-fire path observed by subscribers misses them. Any
        /// such system that relies purely on the event path silently never
        /// enables and its message queue never drains (the v4 AgencySystem
        /// regression). The cure is the same as every sibling in
        /// <c>NetworkUpdate</c>: an explicit
        /// <c>XSystem.Singleton.Enabled = true;</c> line in the matching case.
        /// </summary>
        [TestMethod]
        public void EveryClientSystemOverridingEnableStageHasExplicitEnableInNetworkSystem()
        {
            var network = NetworkSystemSource.Value;
            var offenders = new List<string>();

            foreach (var (file, className) in EnumerateClientSystemSourceFiles())
            {
                var text = File.ReadAllText(file);
                // Match: `protected override ClientState EnableStage => ClientState.X;`
                // Be tolerant of whitespace + chained `>` glyphs.
                var match = Regex.Match(
                    text,
                    @"protected\s+override\s+ClientState\s+EnableStage\s*=>\s*ClientState\.(\w+)\s*;",
                    RegexOptions.Multiline);
                if (!match.Success) continue;

                var stage = match.Groups[1].Value;
                if (stage == "Running") continue; // base default — safe via event path (Running is terminal/sustained)

                // The override targets a pre-Running state. NetworkUpdate must enable explicitly.
                var explicitEnablePattern = Regex.Escape(className) + @"\s*\.\s*Singleton\s*\.\s*Enabled\s*=\s*true";
                if (!Regex.IsMatch(network, explicitEnablePattern))
                {
                    offenders.Add($"{className} (overrides EnableStage => ClientState.{stage} at {RelativePath(file)})");
                }
            }

            Assert.AreEqual(
                0,
                offenders.Count,
                "Client System<T> subclasses with a pre-Running EnableStage override that LACK an explicit " +
                "Singleton.Enabled=true in NetworkSystem.NetworkUpdate. The event-fire path is unreliable " +
                "for transient states (Handshaked, SyncingSettings, etc.) because NetworkUpdate consumes " +
                "and advances past them inside one Update tick. See the v4 AgencySystem regression. " +
                "Offenders:\n  - " + string.Join("\n  - ", offenders));
        }

        /// <summary>
        /// Field-init lint: any instance collection-type field on a client
        /// <c>System&lt;T&gt;</c> subclass (or its <c>MessageSystem</c> /
        /// <c>SubSystem</c> / <c>ShareProgressBaseSystem</c> derivatives) MUST be
        /// declared <c>readonly</c> OR have a field initializer
        /// (<c>= new ...</c>). Lazy initialization in <c>OnEnabled</c> alone is
        /// unsafe because another System enabled at an earlier ClientState
        /// (handshaked, settings-synced, etc.) may call into this one before
        /// <c>OnEnabled</c> fires — the v5 ShareCareerSystem regression. Eager
        /// init removes the null-deref race; <c>OnDisabled</c> should clear the
        /// collection (not reassign) so the reference stays stable across
        /// reconnects.
        /// <para>"Collection-type" covers the generic containers most likely to
        /// be late-init'd: <c>Queue</c>, <c>ConcurrentQueue</c>, <c>Stack</c>,
        /// <c>List</c>, <c>HashSet</c>, <c>Dictionary</c>,
        /// <c>ConcurrentDictionary</c>, <c>ConcurrentBag</c>. ConfigNode /
        /// CfgNode-style fields are deliberately out of scope (those are KSP-
        /// runtime singletons whose construction is gated by scene load).</para>
        /// </summary>
        [TestMethod]
        public void CollectionFieldsInLifecycleSystemsAreEagerlyInitialized()
        {
            // Field declaration matcher: line begins (post-whitespace) with an access modifier,
            // optional `static`/`readonly`, then a generic collection type, then identifier,
            // ending either `;` (uninitialized) or `=` (initialized).
            //
            // We deliberately keep the regex conservative — false negatives (we miss a real
            // field) are preferable to false positives (we flag a non-field). Multi-line
            // initializers across `=\s*\n` are handled by inspecting the line plus the next
            // non-whitespace tokens.
            var fieldPattern = new Regex(
                @"^\s*(public|protected|internal|private)\s+" +
                @"(?<mods>(?:static\s+|readonly\s+)*)" +
                @"(?<type>(?:Concurrent)?(?:Queue|Stack|List|HashSet|Dictionary|Bag)<[^>]+>)\s+" +
                @"(?<name>_?\w+)\s*" +
                @"(?<tail>[=;])",
                RegexOptions.Multiline);

            var offenders = new List<string>();
            foreach (var (file, className) in EnumerateClientSystemSourceFiles())
            {
                var text = File.ReadAllText(file);
                foreach (Match m in fieldPattern.Matches(text))
                {
                    var mods = m.Groups["mods"].Value;
                    var type = m.Groups["type"].Value;
                    var name = m.Groups["name"].Value;
                    var tail = m.Groups["tail"].Value;

                    if (mods.Contains("static")) continue;          // static doesn't have the instance-lifecycle race
                    if (mods.Contains("readonly")) continue;        // readonly requires init in ctor or field-init; both OK
                    if (tail == "=") continue;                      // has field initializer — OK

                    // Field declared as `Type name;` — uninitialized.
                    offenders.Add(
                        $"{className}.{name} ({type}) at {RelativePath(file)} is declared without an initializer. " +
                        "Add `= new ...()` at the field declaration or mark `readonly` so it can't NRE when accessed " +
                        "from a cross-system call before this system's OnEnabled has fired. See the v5 " +
                        "ShareCareerSystem._actionQueue regression.");
                }
            }

            Assert.AreEqual(
                0,
                offenders.Count,
                "Collection-type instance fields on client lifecycle Systems must have field initializers or be " +
                "readonly. Lazy assignment in OnEnabled alone is unsafe — a peer System enabled at an earlier " +
                "ClientState can dereference the still-null field. Offenders:\n  - " +
                string.Join("\n  - ", offenders));
        }

        #region Source-file enumeration

        /// <summary>
        /// Yields every .cs file under <c>LmpClient/Systems/</c> that declares a class
        /// inheriting from one of the <see cref="LifecycleBaseClassNames"/> bases. Returns
        /// (file path, class name) pairs.
        /// </summary>
        private static IEnumerable<(string file, string className)> EnumerateClientSystemSourceFiles()
        {
            var root = LmpClientSourceRoot.Value;
            var systemsDir = Path.Combine(root, "Systems");
            if (!Directory.Exists(systemsDir))
                throw new DirectoryNotFoundException($"Expected LmpClient/Systems/ at {systemsDir}");

            // Match: `class FooSystem : <BaseName><` or `class FooSystem : <BaseName>\n` or `class FooSystem : <BaseName>,`
            var basesAlternation = string.Join("|", LifecycleBaseClassNames.Select(Regex.Escape));
            var classDeclPattern = new Regex(
                @"\bclass\s+(?<name>\w+)\s*:\s*(?:" + basesAlternation + @")\b",
                RegexOptions.Multiline);

            foreach (var file in Directory.EnumerateFiles(systemsDir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var m = classDeclPattern.Match(text);
                if (!m.Success) continue;
                yield return (file, m.Groups["name"].Value);
            }
        }

        private static string LocateLmpClientSourceRoot()
        {
            // Walk up from the test assembly's directory looking for a `LmpClient` sibling
            // containing `Systems/Network/NetworkSystem.cs`. The test runs from
            // LmpClientTest/bin/<Cfg>/net472/, so we'll typically traverse 4 levels.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "LmpClient", "Systems", "Network", "NetworkSystem.cs");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "LmpClient");
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                "Could not locate LmpClient/Systems/Network/NetworkSystem.cs by walking up from " +
                AppContext.BaseDirectory + ". These tests run in-tree only.");
        }

        private static string LoadNetworkSystemSource()
        {
            var path = Path.Combine(LmpClientSourceRoot.Value, "Systems", "Network", "NetworkSystem.cs");
            return File.ReadAllText(path);
        }

        private static string RelativePath(string absolute)
        {
            var root = LmpClientSourceRoot.Value;
            if (absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return "LmpClient" + absolute.Substring(root.Length).Replace('\\', '/');
            return absolute;
        }

        #endregion
    }
}
