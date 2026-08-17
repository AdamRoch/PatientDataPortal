using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;
using PatientDataPortal.Api.Health;
using PatientDataPortal.Api.Observability;
using PatientDataPortal.Api.Time;
using PatientDataPortal.Api.Migrations;
using PatientDataPortal.Api.Profiles;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Seeding;
using PatientDataPortal.Api.Studies;
using PatientDataPortal.Api.Imaging;
using PatientDataPortal.Api.Reports;
using PatientDataPortal.Api.Cine;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Sharing;
using Microsoft.AspNetCore.Authorization;
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

if (args.Contains("--seed-imaging", StringComparer.Ordinal))
{
    var summary = await new ImagingSeedGenerator().SeedAsync();
    Console.WriteLine(summary.ToLogLine());
    return;
}

if (args.Contains("--describe-imaging-seed", StringComparer.Ordinal))
{
    Console.WriteLine(ImagingSeedGenerator.DescribePlan().ToLogLine());
    return;
}

if (args.Contains("--seed-demo-accounts", StringComparer.Ordinal))
{
    var summary = await new DemoAccountSeedGenerator().SeedAsync();
    Console.WriteLine(summary.ToLogLine());
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
    options.AnonKey = builder.Configuration["SUPABASE_ANON_KEY"] ?? string.Empty;
    options.ServiceKey = builder.Configuration["SUPABASE_SERVICE_KEY"] ?? string.Empty;
});
builder.Services.Configure<DatabaseOptions>(options =>
    options.ConnectionString = builder.Configuration["DATABASE_URL"] ?? string.Empty);
builder.Services.Configure<ShareOptions>(options =>
    options.PublicUrl = builder.Configuration["APP_URL"] ?? "http://localhost:3000");
builder.Services.Configure<IdentityVerificationOptions>(options =>
    options.HmacKey = builder.Configuration["IDENTITY_HMAC_KEY"] ?? string.Empty);
builder.Services.Configure<EmailOptions>(options =>
{
    options.DeliveryMode = builder.Configuration["EMAIL_DELIVERY_MODE"] ?? "log";
    options.ApiKey = builder.Configuration["RESEND_API_KEY"] ?? string.Empty;
    options.From = builder.Configuration["EMAIL_FROM"] ?? string.Empty;
});
builder.Services.Configure<OutboxOptions>(options =>
{
    options.JobSecret = builder.Configuration["OUTBOX_JOB_SECRET"] ?? string.Empty;
    if (int.TryParse(builder.Configuration["OUTBOX_BATCH_SIZE"], out var batchSize)) options.BatchSize = batchSize;
    if (int.TryParse(builder.Configuration["OUTBOX_MAX_ATTEMPTS"], out var maximumAttempts)) options.MaximumAttempts = maximumAttempts;
    if (int.TryParse(builder.Configuration["OUTBOX_LEASE_MINUTES"], out var leaseMinutes)) options.LeaseMinutes = leaseMinutes;
});
builder.Services.AddHttpClient("resend", client => client.BaseAddress = new Uri("https://api.resend.com/"));
builder.Services.AddHttpClient<ISupabaseJwtVerifier, SupabaseJwtVerifier>();
builder.Services.AddHttpClient("supabase-storage", client => client.BaseAddress = new Uri((builder.Configuration["SUPABASE_URL"] ?? "http://localhost/").TrimEnd('/') + "/"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserProfileRoleRepository, UserProfileRoleRepository>();
builder.Services.AddScoped<IPatientProfileRepository, PatientProfileRepository>();
builder.Services.AddScoped<IStudyRepository, StudyRepository>();
builder.Services.AddScoped<IImageAccessService, ImageAccessService>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IReportStorage, SupabaseReportStorage>();
builder.Services.AddScoped<ICineRepository, CineRepository>();
builder.Services.AddScoped<ICineFrameUrlSigner, CineFrameUrlSigner>();
builder.Services.AddScoped<IProviderScheduleRepository, ProviderScheduleRepository>();
builder.Services.AddScoped<IProviderAppointmentsRepository, ProviderAppointmentsRepository>();
builder.Services.AddScoped<IProviderDiscoveryRepository, ProviderDiscoveryRepository>();
builder.Services.AddScoped<IAppointmentBookingService, AppointmentBookingService>();
builder.Services.AddScoped<IAppointmentChangeService, AppointmentChangeService>();
builder.Services.AddScoped<IAppointmentLifecycleService, AppointmentLifecycleService>();
builder.Services.AddScoped<IPatientAppointmentRepository, PatientAppointmentRepository>();
builder.Services.AddSingleton<IShareTokenGenerator, ShareTokenGenerator>();
builder.Services.AddScoped<IShareService, ShareService>();
builder.Services.AddScoped<IShareManagementService, ShareManagementService>();
builder.Services.AddScoped<IPublicShareService, PublicShareService>();
builder.Services.AddScoped<IPublicShareStorage, SupabasePublicShareStorage>();
builder.Services.AddSingleton<IPublicShareFailureLimiter, PublicShareFailureLimiter>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddScoped<IIdentityVerificationService, IdentityVerificationService>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, RoleAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, VerifiedPatientAuthorizationHandler>();
builder.Services
    .AddAuthentication(SupabaseAuthenticationHandler.SchemeName)
    .AddScheme<SupabaseAuthenticationOptions, SupabaseAuthenticationHandler>(
        SupabaseAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy(RequireVerifiedPatientAttribute.PolicyName, policy => policy.AddRequirements(new VerifiedPatientRequirement())));
builder.Services.AddScoped<HealthService>();
builder.Services.AddScoped<IEmailSender, ResendEmailSender>();
builder.Services.AddScoped<EmailOutboxWorker>();
builder.Services.AddScoped<IEmailOutboxStatusRepository, EmailOutboxStatusRepository>();
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
