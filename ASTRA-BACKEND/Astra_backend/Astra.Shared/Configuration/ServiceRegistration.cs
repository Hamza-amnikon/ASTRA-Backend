using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Astra.Shared.Configuration;

public static class ServiceRegistration
{
    public static void AddAstraInfrastructure(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DatabaseSettings>().Configure<IConfiguration>((settings, configuration) =>
            settings.ConnectionString = configuration["MYSQL_CONNECTION_STRING"] ?? configuration.GetConnectionString("Astra"));
        builder.Services.AddSingleton<Database>();
        builder.AddAstraIdentity();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient("entra-directory");
        builder.Services.AddSingleton(provider => new EntraDirectory(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-directory"),
            provider.GetRequiredService<IdentityOptions>()));
        builder.Services.AddScoped<TicketReadService>();

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
