namespace OwnDay.Domain;

internal static class DomainTime
{
    public static void RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }

    public static void RequireNotBefore(DateTime value, DateTime earliest, string parameterName)
    {
        RequireUtc(value, parameterName);
        if (value < earliest)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
