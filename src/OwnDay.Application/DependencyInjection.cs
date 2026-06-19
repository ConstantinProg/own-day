using Microsoft.Extensions.DependencyInjection;

namespace OwnDay.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
