using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Cloud;

public sealed class GoogleCloudStorageClient : ICloudStorageClient
{
    private const string Scope = "https://www.googleapis.com/auth/devstorage.read_write";
    private readonly ServiceAccountCredential _credential;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public GoogleCloudStorageClient(string serviceAccountJson, HttpClient? httpClient = null)
    {
        _credential = ServiceAccountCredential.Parse(serviceAccountJson);
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<CloudUploadResult> TestConnectionAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return new CloudUploadResult { Success = false, Message = "Bucket adi bos." };
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://storage.googleapis.com/storage/v1/b/{Uri.EscapeDataString(bucketName)}?fields=name,location");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new CloudUploadResult
            {
                Success = false,
                Message = $"Google Cloud baglanti testi basarisiz: {(int)response.StatusCode} {response.ReasonPhrase} {TrimBody(body)}"
            };
        }

        return new CloudUploadResult { Success = true, Message = "Google Cloud Storage baglantisi basarili." };
    }

    public async Task<CloudUploadResult> UploadAsync(CloudUploadRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.FilePath))
        {
            return new CloudUploadResult { Success = false, Message = "Yuklenecek yedek dosyasi bulunamadi." };
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var objectName = request.ObjectName.Replace('\\', '/').TrimStart('/');
        var url = "https://storage.googleapis.com/upload/storage/v1/b/"
            + Uri.EscapeDataString(request.BucketName)
            + "/o?uploadType=multipart&name="
            + Uri.EscapeDataString(objectName);

        await using var fileStream = File.OpenRead(request.FilePath);
        using var multipart = new MultipartContent("related");
        var metadataJson = JsonSerializer.Serialize(new
        {
            name = objectName,
            metadata = request.Metadata
        });
        multipart.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"));

        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(fileContent);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = multipart
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new CloudUploadResult
            {
                Success = false,
                ObjectName = objectName,
                Message = $"Bulut yukleme basarisiz: {(int)response.StatusCode} {response.ReasonPhrase} {TrimBody(body)}"
            };
        }

        return new CloudUploadResult
        {
            Success = true,
            ObjectName = objectName,
            Message = $"Buluta yuklendi: gs://{request.BucketName}/{objectName}"
        };
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _accessToken;
        }

        var assertion = CreateJwtAssertion();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        using var response = await _httpClient.PostAsync(_credential.TokenUri, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google token alinamadi: {(int)response.StatusCode} {response.ReasonPhrase} {TrimBody(body)}");
        }

        using var document = JsonDocument.Parse(body);
        _accessToken = document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google token yanitinda access_token yok.");
        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return _accessToken;
    }

    private string CreateJwtAssertion()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var payload = JsonSerializer.Serialize(new
        {
            iss = _credential.ClientEmail,
            scope = Scope,
            aud = _credential.TokenUri,
            iat = now,
            exp = now + 3600
        });

        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_credential.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string TrimBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        return body.Length <= 500 ? body : body[..500];
    }

    private sealed class ServiceAccountCredential
    {
        public required string ClientEmail { get; init; }
        public required string PrivateKey { get; init; }
        public required string TokenUri { get; init; }
        public string ProjectId { get; init; } = string.Empty;

        public static ServiceAccountCredential Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
            if (!string.Equals(type, "service_account", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Google key dosyasi service_account tipinde degil.");
            }

            return new ServiceAccountCredential
            {
                ClientEmail = root.GetProperty("client_email").GetString()
                    ?? throw new InvalidOperationException("Google key icinde client_email yok."),
                PrivateKey = root.GetProperty("private_key").GetString()
                    ?? throw new InvalidOperationException("Google key icinde private_key yok."),
                TokenUri = root.TryGetProperty("token_uri", out var tokenUri)
                    ? tokenUri.GetString() ?? "https://oauth2.googleapis.com/token"
                    : "https://oauth2.googleapis.com/token",
                ProjectId = root.TryGetProperty("project_id", out var projectId)
                    ? projectId.GetString() ?? string.Empty
                    : string.Empty
            };
        }
    }
}
