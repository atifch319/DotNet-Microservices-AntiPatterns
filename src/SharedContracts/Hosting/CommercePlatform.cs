using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Contracts.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Commerce.Contracts.Hosting;

public static class ConnectionStrings
{
    public static string Require(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Connection string '{name}' is missing. Set ConnectionStrings__{name} from the environment or an untracked .env file. Do not commit credentials.");
        }

        return value;
    }
}

public static class CommercePlatform
{
    public static void AddCommercePlatform(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
        builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
        builder.Services.Configure<MessagingOptions>(builder.Configuration.GetSection(MessagingOptions.SectionName));
        builder.Services.PostConfigure<RabbitMqOptions>(options => options.ClientName = serviceName);

        builder.Services.AddSingleton<RabbitMqConnection>();
        builder.Services.AddSingleton<IBrokerPublisher, RabbitMqBrokerPublisher>();
        builder.Services.AddSingleton<OutboxDispatcher>();
        builder.Services.AddHealthChecks()
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

        var telemetry = builder.Configuration.GetSection("OpenTelemetry");
        var useConsole = telemetry.GetValue("UseConsoleExporter", false);
        var otlpEndpoint = telemetry["OtlpEndpoint"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(MessagingTelemetry.ActivitySourceName);
                tracing.AddAspNetCoreInstrumentation();
                tracing.AddHttpClientInstrumentation();
                if (useConsole)
                {
                    tracing.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(MessagingTelemetry.MeterName);
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddProblemDetails();
    }

    public static IHealthChecksBuilder AddReadyDatabase<TContext>(this IServiceCollection services, string name)
        where TContext : DbContext
    {
        return services.AddHealthChecks().AddDbContextCheck<TContext>(name, tags: ["ready"]);
    }

    public static IServiceCollection AddRabbitConsumer<THandler>(this IServiceCollection services, string queueName)
        where THandler : class, IIntegrationEventHandler
    {
        services.AddScoped<THandler>();
        services.AddSingleton<IHostedService>(provider => new RabbitConsumerWorker(
            queueName,
            typeof(THandler),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<IOptions<MessagingOptions>>(),
            provider.GetRequiredService<ILogger<RabbitConsumerWorker>>()));
        return services;
    }

    public static void UseCommercePipeline(this WebApplication app)
    {
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseSwagger();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1"));
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });
    }

    public static async Task MigrateDatabaseAsync<TContext>(this WebApplication app, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Response.Headers[HeaderName] = correlationId;
        context.Items[HeaderName] = correlationId;
        System.Diagnostics.Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ""
        }))
        {
            await next(context);
        }
    }
}

public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("The request is invalid.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (RequestValidationException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Validation failed",
                status = StatusCodes.Status400BadRequest,
                errors = exception.Errors
            });
        }
        catch (NotFoundException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Not found",
                status = StatusCodes.Status404NotFound,
                detail = exception.Message
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "An unexpected error occurred",
                status = StatusCodes.Status500InternalServerError
            });
        }
    }
}
