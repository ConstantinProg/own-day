using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Inbox.Policy;

namespace OwnDay.Infrastructure.Persistence;

public static class InboxPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddInboxPolicy(this IServiceCollection services)
    {
        services.TryAddSingleton<InboxPolicyEngine>();
        services.AddScoped<InboxPolicyService>();
        services.AddScoped<IInboxPolicyStore, EfInboxPolicyStore>();
        return services;
    }
}
