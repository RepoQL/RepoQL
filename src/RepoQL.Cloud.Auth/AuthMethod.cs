namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Describe how a gRPC caller authenticated with the cloud services.
/// Complexity: Enumerates the supported server-side auth mechanisms.
/// </summary>
public enum AuthMethod
{
    Session = 0,
    ApiKey = 1
}
