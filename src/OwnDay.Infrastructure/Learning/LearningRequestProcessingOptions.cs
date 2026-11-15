namespace OwnDay.Infrastructure.Learning;

public sealed class LearningRequestProcessingOptions
{
    public bool Enabled { get; set; }
    public bool DurableIngressEnabled { get; set; }
    public bool DialogueEnabled { get; set; }
    public bool ConsultationEnabled { get; set; }
    public bool EnglishDiagnosticEnabled { get; set; }
    public bool PracticeEnabled { get; set; }
    public bool ProgramSwitchEnabled { get; set; }
    public bool AcceptancePaused { get; set; }
    public int MaxQueueAgeSeconds { get; set; } = 300;
    public int Parallelism { get; set; } = 2;
    public int PollMilliseconds { get; set; } = 1000;
    public int LeaseSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int ProviderTimeoutSeconds { get; set; } = 15;
    public int MaxProviderAttempts { get; set; } = 2;
}
