using Bujo.Domain;
using Bujo.Application;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bujo.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddJournalPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<JournalDbContext>(options =>
            options.UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure()));
        services.AddScoped<IJournalRepository, EfJournalRepository>();
        return services;
    }

    public static IServiceCollection AddJournalApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<JournalService>();
        services.AddScoped<JournalApplicationService>();
        return services;
    }
}
