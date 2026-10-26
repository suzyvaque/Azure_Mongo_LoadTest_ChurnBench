using Azure.Core;
using Azure.Identity;
using MongoDB.Driver;
using MongoDB.Driver.Core.Authentication.Oidc;

namespace Bmt.Core.Connections;

internal static class AzureOidcCredential
{
    private static readonly IOidcCallback Callback = new AzureIdentityOidcCallback();

    public static void ApplyIfNeeded(MongoClientSettings settings)
    {
        if (settings.Credential?.Mechanism == "MONGODB-OIDC")
        {
            settings.Credential = MongoCredential.CreateOidcCredential(Callback);
        }
    }

    private sealed class AzureIdentityOidcCallback : IOidcCallback
    {
        private static readonly TokenRequestContext TokenRequest =
            new(new[] { "https://ossrdbms-aad.database.windows.net/.default" });
        private readonly DefaultAzureCredential _credential = new();

        public OidcAccessToken GetOidcAccessToken(
            OidcCallbackParameters parameters,
            CancellationToken cancellationToken)
        {
            var token = _credential.GetToken(TokenRequest, cancellationToken);
            return new OidcAccessToken(token.Token, token.ExpiresOn - DateTimeOffset.UtcNow);
        }

        public async Task<OidcAccessToken> GetOidcAccessTokenAsync(
            OidcCallbackParameters parameters,
            CancellationToken cancellationToken)
        {
            var token = await _credential.GetTokenAsync(TokenRequest, cancellationToken).ConfigureAwait(false);
            return new OidcAccessToken(token.Token, token.ExpiresOn - DateTimeOffset.UtcNow);
        }
    }
}