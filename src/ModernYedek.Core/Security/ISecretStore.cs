namespace ModernYedek.Core.Security;

public interface ISecretStore
{
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task SetSecretAsync(string key, string? value, CancellationToken cancellationToken = default);
    Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default);
}
