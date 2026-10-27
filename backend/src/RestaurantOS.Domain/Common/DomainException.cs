namespace RestaurantOS.Domain.Common;

/// <summary>Business rule violation. Mapped to HTTP 400 (or 409 for conflicts) by the API.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ConflictException(string code, string message) : DomainException(code, message);

public sealed class NotFoundException(string entity) : Exception($"{entity} not found.")
{
    public string Entity { get; } = entity;
}

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : Exception(message);
