using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox.Policy;

namespace OwnDay.Infrastructure.Persistence;

public static class InboxPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddInboxPolicy(this IServiceCollection services)
    {
        services.AddSingleton<InboxPolicyEngine>();
        services.AddScoped<InboxPolicyService>();
        services.AddScoped<IInboxPolicyStore, EfInboxPolicyStore>();
        return services;
    }
}
