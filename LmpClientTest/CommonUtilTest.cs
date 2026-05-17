using LmpClient.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace LmpClientTest
{
    /// <summary>
    /// Scaffolding proof for the LmpClientTest project. <see cref="CommonUtil"/>
    /// is pure-BCL (no Unity / KSP dependency), which makes it the safest first
    /// target for a net472 unit test. The point of these tests is to demonstrate
    /// that LmpClientTest can:
    ///   * resolve and load the LmpClient assembly
    ///   * invoke its static helpers under MSTest on net472
    ///   * be built and run via <c>dotnet test</c> from the user-installed .NET 10 SDK
    ///
    /// Future client-internal tests (BUG-003/004 interp cap math, BUG-051b retry
    /// predicate, BUG-008 Phase A's <c>PqsAlignmentDecision</c>) will join this
    /// project as their respective helpers are extracted into testable forms.
    /// </summary>
    [TestClass]
    public class CommonUtilTest
    {
        [TestMethod]
        public void ScrambledEquals_SameElementsDifferentOrder_True()
        {
            Assert.IsTrue(CommonUtil.ScrambledEquals(new[] { 1, 2, 3 }, new[] { 3, 1, 2 }));
        }

        [TestMethod]
        public void ScrambledEquals_DifferentElements_False()
        {
            Assert.IsFalse(CommonUtil.ScrambledEquals(new[] { 1, 2, 3 }, new[] { 1, 2, 4 }));
        }

        [TestMethod]
        public void ScrambledEquals_DifferentLengths_False()
        {
            Assert.IsFalse(CommonUtil.ScrambledEquals(new[] { 1, 2, 3 }, new[] { 1, 2 }));
        }

        [TestMethod]
        public void ScrambledEquals_BothEmpty_True()
        {
            Assert.IsTrue(CommonUtil.ScrambledEquals(new int[0], new int[0]));
        }

        [TestMethod]
        public void CombinePaths_JoinsSegments_LikePathCombine()
        {
            Assert.AreEqual(Path.Combine("a", "b", "c"), CommonUtil.CombinePaths("a", "b", "c"));
        }
    }
}
