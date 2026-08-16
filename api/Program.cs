using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;
using PatientDataPortal.Api.Health;
using PatientDataPortal.Api.Observability;
using PatientDataPortal.Api.Time;
using PatientDataPortal.Api.Migrations;
using NodaTime;

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await MigrationRunner.MigrateAsync();
    return;
}

if (args.Contains("--verify-migrations", StringComparer.Ordinal))
{
    var applicationConnectionString = await MigrationRunner.MigrateAsync();
    await MigrationVerifier.VerifyAsync(applicationConnectionString);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Configuration.AddEnvironmentVariables();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "O";
});
builder.Services.Configure<SupabaseOptions>(options =>
{
    options.Url = builder.Configuration["SUPABASE_URL"] ?? string.Empty;
    options.ServiceKey = builder.Configuration["SUPABASE_SERVICE_KEY"] ?? string.Empty;
});
builder.Services.Configure<DatabaseOptions>(options =>
    options.ConnectionString = builder.Configuration["DATABASE_URL"] ?? string.Empty);
builder.Services.Configure<EmailOptions>(options =>
{
    options.DeliveryMode = builder.Configuration["EMAIL_DELIVERY_MODE"] ?? "log";
    options.ApiKey = builder.Configuration["RESEND_API_KEY"] ?? string.Empty;
    options.From = builder.Configuration["EMAIL_FROM"] ?? string.Empty;
});
builder.Services.AddHttpClient("resend", client => client.BaseAddress = new Uri("https://api.resend.com/"));
builder.Services.AddScoped<HealthService>();
builder.Services.AddScoped<IEmailSender, ResendEmailSender>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddScoped<LockoutWindow>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
