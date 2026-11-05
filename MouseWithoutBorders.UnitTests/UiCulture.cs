using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
public sealed class UiCulture
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        // Existing UI regressions assert the English baseline; localization tests select their own culture.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}
