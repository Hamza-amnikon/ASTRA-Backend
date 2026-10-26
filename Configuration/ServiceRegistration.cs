using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Configuration;

public static class ServiceRegistration
{
    public static void AddAstraServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DatabaseSettings>().Configure<IConfiguration>((settings, configuration) =>
            settings.ConnectionString = configuration.GetConnectionString("Astra") ?? configuration["SQLSERVER_CONNECTION_STRING"]);
        builder.Services.AddSingleton<Database>();
        builder.Services.AddDbContext<AstraDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                options.UseSqlServer(settings.GetConnectionString());
        });
        builder.AddAstraIdentity();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient("entra-directory");
        builder.Services.AddSingleton(provider => new EntraDirectory(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-directory"),
            provider.GetRequiredService<IdentityOptions>()));
        builder.Services.AddScoped<ServiceReportService>();
        builder.Services.AddScoped<AdministrationService>();
        builder.Services.AddScoped<GroupService>();
        builder.Services.AddScoped<ClientDomainService>();
        builder.Services.AddScoped<CoreDataService>();
        builder.Services.AddScoped<NotificationService>();
        builder.Services.AddScoped<SlaConfigurationService>();
        builder.Services.AddScoped<TicketReadService>();
        builder.Services.AddScoped<ProblemService>();
        builder.Services.AddScoped<TicketWriteService>();

        builder.Services.AddControllers(options =>
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions));
        builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
        builder.Services.Configure<ApiBehaviorOptions>(options =>
            options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(
                new { error = new { code = "HTTP_400", message = "Invalid request or linked records." } }));

        builder.Services.AddCors(options => options.AddPolicy("ReactPolicy", policy =>
        {
            var origins = builder.Configuration.GetSection("React:AllowedOrigins").Get<string[]>() ?? [];
            if (origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo) || origin.Contains('*', StringComparison.Ordinal)))
                throw new InvalidOperationException("Configure explicit HTTP(S) React origins without paths or wildcards.");
            if (origins.Length > 0)
                policy.WithOrigins(origins.Select(origin => origin.TrimEnd('/')).ToArray())
                    .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));
        builder.Services.AddOpenApi();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    }
}
