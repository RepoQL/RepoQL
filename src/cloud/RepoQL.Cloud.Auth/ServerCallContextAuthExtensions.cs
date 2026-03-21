using Grpc.Core;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Expose authenticated caller identity from gRPC server call context.
/// Complexity: Stores and retrieves a single AuthIdentity value via ServerCallContext.UserState.
/// </summary>
public static class ServerCallContextAuthExtensions
{
    private static readonly object AuthIdentityKey = new();

    public static AuthIdentity? GetAuthIdentity(this ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.UserState.TryGetValue(AuthIdentityKey, out var value)
            ? value as AuthIdentity
            : null;
    }

    public static AuthIdentity RequireAuthIdentity(this ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetAuthIdentity()
            ?? throw new InvalidOperationException("AuthIdentity was not attached to the ServerCallContext.");
    }

    internal static void SetAuthIdentity(this ServerCallContext context, AuthIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);

        context.UserState[AuthIdentityKey] = identity;
    }
}
