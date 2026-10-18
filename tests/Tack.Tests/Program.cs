using System.IO;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>A tiny test runner: each test is a named action that throws on failure.</summary>
internal static class Program
{
    /// <summary>--update: rewrite the fixtures this project produces (tests/vectors/desktop-export.json).</summary>
    public static bool Update { get; private set; }

    public static string Root { get; } = FindRoot();

    [STAThread]
    private static int Main(string[] args)
    {
        Update = args.Contains("--update");
        // The shared vectors are computed in UTC; dates without their own offset are read in this zone.
        IsoTime.ZoneOverride = TimeZoneInfo.Utc;

        var tests = VectorTests.All().Concat(OrderTests.All()).Concat(StateFileTests.All()).ToList();
        string? only = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (only != null) tests = tests.Where(t => t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"  ok    {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine("        " + (ex is TestFailure ? ex.Message : ex.ToString()).Replace("\n", "\n        "));
            }
        }
        Console.WriteLine(failed == 0 ? $"All {tests.Count} tests passed." : $"{failed} of {tests.Count} tests failed.");
        return failed == 0 ? 0 : 1;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tack.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("Can't find the repository (Tack.sln) above " + AppContext.BaseDirectory);
    }
}

internal sealed class TestFailure(string message) : Exception(message);

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailure($"{what}\n  expected: {Show(expected)}\n  actual:   {Show(actual)}");
    }

    public static void True(bool condition, string what)
    {
        if (!condition) throw new TestFailure(what);
    }

    public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string what)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++) Equal(expected[i], actual[i], $"{what} [{i}]");
        Equal(expected.Count, actual.Count, $"{what}: count");
    }

    public static void Data(WorkspaceData expected, WorkspaceData actual, string what)
    {
        Sequence(expected.Tasks, actual.Tasks, $"{what}: tasks");
        Sequence(expected.Sheets, actual.Sheets, $"{what}: sheets");
        Equal(expected.Focus, actual.Focus, $"{what}: focus");
    }

    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new TestFailure($"{what}: expected {typeof(TException).Name}");
    }

    private static string Show<T>(T value) => value switch
    {
        null => "null",
        string s => System.Text.Json.JsonSerializer.Serialize(s),
        _ => value.ToString() ?? "",
    };
}
