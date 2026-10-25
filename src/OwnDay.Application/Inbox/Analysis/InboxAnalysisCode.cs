namespace OwnDay.Application.Inbox.Analysis;

public enum InboxAnalysisCode
{
    Validated,
    ProviderFailure,
    Refused,
    IncompleteResponse,
    Timeout,
    ContextLimit,
    RateLimit,
    BudgetLimit,
    Configuration,
    PermanentFailure,
    InvalidResponse,
    ItemInactive,
    AttemptsExhausted,
    InputLimit,
    AlreadyFinished,
    DescriptorMismatch,
    LeaseLost,
    Interrupted,
    Disabled
}

public static class InboxAnalysisCodeExtensions
{
    // These values are persisted and used in diagnostics; keep them stable across renames.
    public static string ToStorageValue(this InboxAnalysisCode code) => code switch
    {
        InboxAnalysisCode.Validated => "validated",
        InboxAnalysisCode.ProviderFailure => "provider_failure",
        InboxAnalysisCode.Refused => "refused",
        InboxAnalysisCode.IncompleteResponse => "incomplete_response",
        InboxAnalysisCode.Timeout => "timeout",
        InboxAnalysisCode.ContextLimit => "context_limit",
        InboxAnalysisCode.RateLimit => "rate_limit",
        InboxAnalysisCode.BudgetLimit => "budget_limit",
        InboxAnalysisCode.Configuration => "configuration",
        InboxAnalysisCode.PermanentFailure => "permanent_failure",
        InboxAnalysisCode.InvalidResponse => "invalid_response",
        InboxAnalysisCode.ItemInactive => "item_inactive",
        InboxAnalysisCode.AttemptsExhausted => "attempts_exhausted",
        InboxAnalysisCode.InputLimit => "input_limit",
        InboxAnalysisCode.AlreadyFinished => "already_finished",
        InboxAnalysisCode.DescriptorMismatch => "descriptor_mismatch",
        InboxAnalysisCode.LeaseLost => "lease_lost",
        InboxAnalysisCode.Interrupted => "interrupted",
        InboxAnalysisCode.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}
