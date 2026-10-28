using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Plex;

public static class DependencyInjection
{
    public static IServiceCollection AddPlexIntegration(this IServiceCollection services)
    {
        services.AddHttpClient(PlexIntegration.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient(PlexIntegration.HttpClientInsecure, c => c.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Only used when the user enables "accept self-signed certificates" for their own server.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        return services.AddIntegration<PlexIntegration>();
    }
}
