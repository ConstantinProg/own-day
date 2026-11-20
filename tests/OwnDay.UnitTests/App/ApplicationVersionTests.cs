using System.Reflection;
using System.Reflection.Emit;
using OwnDay.Infrastructure;
using Xunit;

namespace OwnDay.UnitTests.App;

public sealed class ApplicationVersionTests
{
    [Theory]
    [InlineData("0.7.0")]
    [InlineData("0.8.0-rc.1")]
    [InlineData("0.8.0-rc.1+build.42")]
    public void FromAssembly_WithBuildMetadata_PreservesVersionAndRevision(string version)
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var assembly = CreateAssembly();
        var attribute = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(attribute, ["Version", version]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(attribute, ["SourceRevisionId", commit]));

        Assert.Equal(new ApplicationVersion(version, commit), ApplicationVersion.FromAssembly(assembly));
    }

    [Fact]
    public void FromAssembly_WithoutBuildMetadata_ReturnsDevVersionWithoutRevision()
    {
        Assert.Equal(new ApplicationVersion("0.0.0-dev", null), ApplicationVersion.FromAssembly(CreateAssembly()));
    }

    private static AssemblyBuilder CreateAssembly() =>
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VersionTest"), AssemblyBuilderAccess.Run);
}
