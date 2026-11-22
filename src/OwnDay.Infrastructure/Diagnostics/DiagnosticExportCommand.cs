namespace OwnDay.Infrastructure.Diagnostics;

internal static class DiagnosticExportCommand
{
    internal static bool TryParse(string arguments, out DiagnosticExportMode mode)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                mode = DiagnosticExportMode.Full;
                return true;
            case "recent":
                mode = DiagnosticExportMode.Recent;
                return true;
            case "last":
                mode = DiagnosticExportMode.Last;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
