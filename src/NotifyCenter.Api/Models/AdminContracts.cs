namespace NotifyCenter.Api.Models;

public sealed record AdminLoginRequest(string Username, string Password);

public sealed record AdminChangePasswordRequest(
    string CurrentPassword,
    string NewPassword,
    string ConfirmPassword);

public sealed record AdminSessionResponse(
    string Username,
    bool MustChangePassword,
    string? TelegramDefaultTarget);

public sealed record AdminUser(
    Guid Id,
    string Username,
    string PasswordHash,
    string PasswordSalt,
    int PasswordIterations,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt);
