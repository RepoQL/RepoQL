using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Adapt a JWKS document into the OpenIdConnectConfiguration shape expected by ConfigurationManager.
/// Complexity: Fetches raw JWKS JSON and projects only the signing key material needed for token validation.
/// </summary>
internal sealed class JwksOpenIdConnectConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(retriever);

        var document = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var keySet = JsonWebKeySet.Create(document);

        var configuration = new OpenIdConnectConfiguration
        {
            JwksUri = address
        };

        foreach (var key in keySet.GetSigningKeys())
            configuration.SigningKeys.Add(key);

        return configuration;
    }
}
