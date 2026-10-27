using RestaurantOS.Domain.Common;

namespace RestaurantOS.Api.Common;

public static class Guard
{
    public static string Required(string? value, string field, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("validation", $"{field} is required.");
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new DomainException("validation", $"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static string? Optional(string? value, string field, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new DomainException("validation", $"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static decimal NonNegative(decimal value, string field)
    {
        if (value < 0) throw new DomainException("validation", $"{field} cannot be negative.");
        return value;
    }

    public static decimal Positive(decimal value, string field)
    {
        if (value <= 0) throw new DomainException("validation", $"{field} must be greater than zero.");
        return value;
    }

    public static string Email(string? value)
    {
        var email = Required(value, "Email", 256).ToLowerInvariant();
        var at = email.IndexOf('@');
        if (at < 1 || at == email.Length - 1 || !email[(at + 1)..].Contains('.'))
            throw new DomainException("validation", "Email is invalid.");
        return email;
    }

    public static string Password(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8)
            throw new DomainException("weak_password", "Password must be at least 8 characters.");
        if (value.Length > 128) throw new DomainException("validation", "Password is too long.");
        return value;
    }
}
