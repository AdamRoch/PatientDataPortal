using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Scheduling;

public sealed record ProviderAppointment(Guid Id, DateTimeOffset StartsAt, string ServiceName, string Status);
public sealed record ProviderAppointmentSchedule(string TimeZoneId, IReadOnlyList<ProviderAppointment> Upcoming, IReadOnlyList<ProviderAppointment> Past);

public interface IProviderAppointmentsRepository
{
    Task<ProviderAppointmentSchedule?> ListAsync(Guid providerUserId, Instant now, CancellationToken cancellationToken);
}

public sealed class ProviderAppointmentsRepository(IOptions<DatabaseOptions> databaseOptions) : IProviderAppointmentsRepository
{
    public async Task<ProviderAppointmentSchedule?> ListAsync(Guid providerUserId, Instant now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string providerSql = "SELECT id, tz FROM providers WHERE user_id = $1";
        await using var providerCommand = new NpgsqlCommand(providerSql, connection);
        providerCommand.Parameters.AddWithValue(providerUserId);
        await using var providerReader = await providerCommand.ExecuteReaderAsync(cancellationToken);
        if (!await providerReader.ReadAsync(cancellationToken)) return null;
        var providerId = providerReader.GetGuid(0);
        var timeZoneId = providerReader.GetString(1);
        await providerReader.CloseAsync();

        const string appointmentsSql = """
            SELECT a.id, a.start_at, service.name, a.status
            FROM appointments a
            JOIN services service ON service.id = a.service_id
            WHERE a.provider_id = $1
            ORDER BY a.start_at DESC
            """;
        await using var appointmentsCommand = new NpgsqlCommand(appointmentsSql, connection);
        appointmentsCommand.Parameters.AddWithValue(providerId);
        var upcoming = new List<ProviderAppointment>();
        var past = new List<ProviderAppointment>();
        await using var appointmentsReader = await appointmentsCommand.ExecuteReaderAsync(cancellationToken);
        while (await appointmentsReader.ReadAsync(cancellationToken))
        {
            var appointment = new ProviderAppointment(appointmentsReader.GetGuid(0), appointmentsReader.GetFieldValue<DateTimeOffset>(1), appointmentsReader.GetString(2), appointmentsReader.GetString(3));
            (appointment.StartsAt >= now.ToDateTimeOffset() ? upcoming : past).Add(appointment);
        }
        upcoming.Reverse();
        return new ProviderAppointmentSchedule(timeZoneId, upcoming, past);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var value = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Database is not configured.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(value));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
