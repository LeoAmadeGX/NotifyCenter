using Npgsql;
using NotifyCenter.Api.Auth;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Data;

public sealed class AdminUserRepository(NpgsqlDataSource dataSource, PasswordHasher passwordHasher)
{
    private const string BootstrapUsername = "amadegx";
    private const string BootstrapPassword = "1111";

    public async Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        const string countSql = "SELECT COUNT(*) FROM admin_users;";
        await using var countCommand = dataSource.CreateCommand(countSql);
        var adminCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        if (adminCount > 0)
        {
            return;
        }

        var password = passwordHasher.HashPassword(BootstrapPassword);
        const string insertSql = """
            INSERT INTO admin_users (
                id,
                username,
                password_hash,
                password_salt,
                password_iterations,
                must_change_password,
                created_at,
                updated_at
            )
            VALUES (
                @id,
                @username,
                @password_hash,
                @password_salt,
                @password_iterations,
                TRUE,
                now(),
                now()
            )
            ON CONFLICT (username) DO NOTHING;
            """;

        await using var command = dataSource.CreateCommand(insertSql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("username", BootstrapUsername);
        command.Parameters.AddWithValue("password_hash", password.Hash);
        command.Parameters.AddWithValue("password_salt", password.Salt);
        command.Parameters.AddWithValue("password_iterations", password.Iterations);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                username,
                password_hash,
                password_salt,
                password_iterations,
                must_change_password,
                created_at,
                updated_at,
                last_login_at
            FROM admin_users
            WHERE username = @username;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("username", NormalizeUsername(username));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAdminUser(reader) : null;
    }

    public async Task RecordLoginAsync(Guid adminUserId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE admin_users
            SET last_login_at = now()
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", adminUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePasswordAsync(
        Guid adminUserId,
        PasswordHashResult password,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE admin_users
            SET password_hash = @password_hash,
                password_salt = @password_salt,
                password_iterations = @password_iterations,
                must_change_password = FALSE,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", adminUserId);
        command.Parameters.AddWithValue("password_hash", password.Hash);
        command.Parameters.AddWithValue("password_salt", password.Salt);
        command.Parameters.AddWithValue("password_iterations", password.Iterations);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeUsername(string username)
    {
        return username.Trim().ToLowerInvariant();
    }

    private static AdminUser ReadAdminUser(NpgsqlDataReader reader)
    {
        return new AdminUser(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("username")),
            reader.GetString(reader.GetOrdinal("password_hash")),
            reader.GetString(reader.GetOrdinal("password_salt")),
            reader.GetInt32(reader.GetOrdinal("password_iterations")),
            reader.GetBoolean(reader.GetOrdinal("must_change_password")),
            ReadTimestamp(reader, "created_at")!.Value,
            ReadTimestamp(reader, "updated_at")!.Value,
            ReadTimestamp(reader, "last_login_at"));
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetFieldValue<DateTime>(ordinal);
        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return new DateTimeOffset(value.ToUniversalTime());
    }
}
