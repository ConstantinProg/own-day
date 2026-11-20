using System.Reflection;

namespace OwnDay.Infrastructure;

public sealed record ApplicationVersion(string Version, string? CommitSha)
{
    public static ApplicationVersion FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return new ApplicationVersion(
            metadata.FirstOrDefault(attribute => attribute.Key == "Version")?.Value ?? "0.0.0-dev",
            metadata.FirstOrDefault(attribute => attribute.Key == "SourceRevisionId")?.Value);
    }
}
