using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningProviderCallGateTests
{
    [Fact]
    public void Dispose_PhysicalCallStillRunning_AllowsItsFinalReleaseAndClosesAdmission()
    {
        var gate = new LearningProviderCallGate(Options.Create(new LearningRequestProcessingOptions { Parallelism = 1 }));
        Assert.True(gate.TryEnter());
        gate.Dispose();
        Assert.False(gate.TryEnter());
        gate.Release();
        Assert.False(gate.TryEnter());
    }
}
