using System.Net.Http.Json;

namespace ModernYedek.Core.Licensing;

public sealed class LicenseClient
{
    private readonly HttpClient _httpClient;

    public LicenseClient(string apiBaseUrl, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(NormalizeBaseUrl(apiBaseUrl));
    }

    public async Task<LicenseValidationResult> ActivateAsync(
        string licenseKey,
        string email,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/license/activate", new LicenseActivationRequest
        {
            LicenseKey = licenseKey,
            Email = email,
            MachineId = machineId
        }, cancellationToken);

        return await ReadResultAsync(response, cancellationToken);
    }

    public async Task<LicenseValidationResult> ValidateAsync(
        string licenseKey,
        string email,
        string machineId,
        string instanceId = "",
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/license/validate", new LicenseValidationRequest
        {
            LicenseKey = licenseKey,
            Email = email,
            MachineId = machineId,
            InstanceId = instanceId
        }, cancellationToken);

        return await ReadResultAsync(response, cancellationToken);
    }

    private static async Task<LicenseValidationResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<LicenseValidationResult>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return new LicenseValidationResult
        {
            IsValid = false,
            State = LicenseState.Invalid,
            Message = $"License server returned HTTP {(int)response.StatusCode}."
        };
    }

    private static string NormalizeBaseUrl(string apiBaseUrl)
    {
        var value = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "http://localhost:5088"
            : apiBaseUrl.Trim();

        return value.TrimEnd('/');
    }
}
