namespace Tack.Services;

/// <summary>
/// Whether Tack runs as administrator. Windows then keeps other apps from dropping onto its windows
/// (their drags never reach Tack), so Settings says so. Tests set <see cref="Override"/>.
/// </summary>
internal static class Elevation
{
    internal static bool? Override;

    public static bool IsAdmin => Override ?? Environment.IsPrivilegedProcess;
}
