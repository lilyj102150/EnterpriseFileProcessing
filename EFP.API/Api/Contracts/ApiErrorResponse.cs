namespace EFP.API.Api.Contracts;

/// <summary>
/// Represents an API error.
/// </summary>
public sealed record ApiErrorResponse(string Code, string Message, string TraceId, IReadOnlyList<ApiFieldError> Errors);

/// <summary>
/// Represents a field validation error.
/// </summary>
public sealed record ApiFieldError(string Field, string Message);