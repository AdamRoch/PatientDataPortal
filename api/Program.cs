using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Health;
using PatientDataPortal.Api.Observability;
using PatientDataPortal.Api.Time;
using NodaTime;

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
builder.Services.AddHttpClient();
builder.Services.AddScoped<HealthService>();
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
