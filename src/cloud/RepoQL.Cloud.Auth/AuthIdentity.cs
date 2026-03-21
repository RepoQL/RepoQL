namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Carry authenticated caller identity through a gRPC request.
/// Complexity: Stores the normalized auth method and the small set of claims needed downstream.
/// </summary>
public sealed record AuthIdentity(
    string UserId,
    AuthMethod Method,
    string DisplayName,
    string? OrganizationId = null);
