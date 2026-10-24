using System.Runtime.CompilerServices;
using VidFlow.Services;

namespace VidFlow.Tests;

static class TestSetup
{
    /// <summary>Points the data folder at a throwaway directory before any test runs, so no test
    /// (even one that forgets to set its own) can read or write the real %LocalAppData%\VidFlow.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        AppSettings.DataDir = Path.Combine(Path.GetTempPath(), "vidflow-tests-" + Environment.ProcessId);
}
