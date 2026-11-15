using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bujo.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddJournalPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<JournalDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IJournalRepository, EfJournalRepository>();
        return services;
    }
}
