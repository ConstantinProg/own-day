using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Application.Inbox.Observability;

// Persisted values are stable across member renames and shared by writers and reports.
public static class InboxEventCodes
{
    public const string Created = "created";
    public const string Manual = "manual";
    public const string UndoUnavailable = "undo_unavailable";
    public const string UndoAlready = "undo_already";
    public const string UndoConflict = "undo_conflict";
    public const string BackgroundRetry = "background_retry";
    public const string BackgroundDeliveryRetry = "background_delivery_retry";
    public const string BackgroundDeliveryError = "background_delivery_error";
    public const string BackgroundApplyError = "background_apply_error";
    public const string BackgroundRecoveryError = "background_recovery_error";
    public const string BackgroundAnalysisError = "background_analysis_error";
    public const string ObservabilityStarted = "observability_started";

    public static IReadOnlyList<string> UndoRefusals { get; } =
        Array.AsReadOnly(new[] { UndoUnavailable, UndoAlready, UndoConflict });

    public static IReadOnlyList<string> BackgroundErrors { get; } = Array.AsReadOnly(
        Enum.GetValues<InboxAnalysisCode>().Select(AnalysisError)
            .Concat([BackgroundDeliveryError, BackgroundApplyError, BackgroundRecoveryError, BackgroundAnalysisError])
            .ToArray());

    public static string AnalysisError(InboxAnalysisCode code) => "background_" + code.ToStorageValue() + "_error";
}
