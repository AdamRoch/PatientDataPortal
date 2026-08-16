using PatientDataPortal.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<SupabaseOptions>(options =>
{
    options.Url = builder.Configuration["SUPABASE_URL"] ?? string.Empty;
    options.ServiceKey = builder.Configuration["SUPABASE_SERVICE_KEY"] ?? string.Empty;
});
builder.Services.Configure<DatabaseOptions>(options =>
    options.ConnectionString = builder.Configuration["DATABASE_URL"] ?? string.Empty);

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
