namespace OwnDay.Infrastructure.Learning;

public sealed class LearningRequestProcessingOptions
{
    public bool Enabled { get; set; }
    public int Parallelism { get; set; } = 2;
    public int PollMilliseconds { get; set; } = 1000;
    public int LeaseSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int ProviderTimeoutSeconds { get; set; } = 15;
}
