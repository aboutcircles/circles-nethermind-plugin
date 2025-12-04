using System.Net.Http.Json;
using System.Text.Json;
using Circles.Rpc.CacheServiceClient.Models;
using Microsoft.Extensions.Logging;

namespace Circles.Rpc.CacheServiceClient;

/// <summary>
/// HTTP client for the Circles Cache Service API
/// </summary>
public class CacheServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CacheServiceClient>? _logger;
    private readonly string _baseUrl;

    public CacheServiceClient(HttpClient httpClient, string baseUrl, ILogger<CacheServiceClient>? logger = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    /// <summary>
    /// Get all token balances for an address
    /// </summary>
    public async Task<TokenBalanceResponse[]> GetTokenBalancesAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/balances/{address}";
            var response = await _httpClient.GetFromJsonAsync<TokenBalanceResponse[]>(url, cancellationToken);
            return response ?? Array.Empty<TokenBalanceResponse>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting token balances from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get total balance for an address (all versions)
    /// </summary>
    public async Task<string> GetTotalBalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/balances/{address}/total";
            var response = await _httpClient.GetFromJsonAsync<TotalBalanceResponse>(url, cancellationToken);
            return response?.Balance ?? "0";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting total balance from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get total balance for an address (V1 only)
    /// </summary>
    public async Task<string> GetTotalBalanceV1Async(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/balances/{address}/total/v1";
            var response = await _httpClient.GetFromJsonAsync<TotalBalanceResponse>(url, cancellationToken);
            return response?.Balance ?? "0";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting V1 total balance from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get total balance for an address (V2 only)
    /// </summary>
    public async Task<string> GetTotalBalanceV2Async(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/balances/{address}/total/v2";
            var response = await _httpClient.GetFromJsonAsync<TotalBalanceResponse>(url, cancellationToken);
            return response?.Balance ?? "0";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting V2 total balance from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get avatar info for a single address
    /// </summary>
    public async Task<AvatarInfoResponse?> GetAvatarInfoAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/avatars/{address}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AvatarInfoResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting avatar info from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get avatar info for multiple addresses in batch
    /// </summary>
    public async Task<AvatarInfoResponse?[]> GetAvatarInfoBatchAsync(string[] addresses, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/avatars/batch";
            var request = new AvatarInfoBatchRequest(addresses);
            var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AvatarInfoResponse?[]>(cancellationToken: cancellationToken)
                ?? Array.Empty<AvatarInfoResponse?>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting batch avatar info from cache service");
            throw;
        }
    }

    /// <summary>
    /// Get profile CID for a single address
    /// </summary>
    public async Task<string?> GetProfileCidAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/profiles/{address}/cid";
            var response = await _httpClient.GetFromJsonAsync<ProfileCidResponse>(url, cancellationToken);
            return response?.Cid;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting profile CID from cache service for {Address}", address);
            throw;
        }
    }

    /// <summary>
    /// Get profile CIDs for multiple addresses in batch
    /// </summary>
    public async Task<ProfileCidResponse[]> GetProfileCidBatchAsync(string[] addresses, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/profiles/cid/batch";
            var request = new ProfileCidBatchRequest(addresses);
            var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProfileCidResponse[]>(cancellationToken: cancellationToken)
                ?? Array.Empty<ProfileCidResponse>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting batch profile CIDs from cache service");
            throw;
        }
    }

    /// <summary>
    /// Check if the cache service is ready
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/ready";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking cache service readiness");
            return false;
        }
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public async Task<Dictionary<string, object>?> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/cache/stats";
            return await _httpClient.GetFromJsonAsync<Dictionary<string, object>>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting cache statistics from cache service");
            throw;
        }
    }
}
