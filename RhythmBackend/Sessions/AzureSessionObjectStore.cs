using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Runtime.CompilerServices;

namespace RhythmBackend.Sessions;

public sealed class AzureSessionObjectStore : ISessionObjectStore
{
    private readonly BlobContainerClient _container;
    private readonly BlobServiceClient _service;
    private readonly SemaphoreSlim _initializing = new(1);
    private bool _ready;

    public AzureSessionObjectStore(SessionApiOptions options, bool allowLocalEmulator = false)
    {
        if (string.IsNullOrWhiteSpace(options.StorageConnectionString))
            throw new InvalidOperationException("Set SessionApi__StorageConnectionString in Azure App Service before enabling the session API.");
        var clientOptions = new BlobClientOptions();
        clientOptions.Diagnostics.IsLoggingEnabled = false;
        clientOptions.Diagnostics.IsDistributedTracingEnabled = false;
        _service = new BlobServiceClient(options.StorageConnectionString, clientOptions);
        _container = _service.GetBlobContainerClient(options.ContainerName);
        if (_container.Uri.Scheme != "https" && !(allowLocalEmulator && _container.Uri.IsLoopback))
            throw new InvalidOperationException("Production storage must use HTTPS.");
    }

    private async Task Ready(CancellationToken ct)
    {
        if (_ready) return;
        await _initializing.WaitAsync(ct);
        try
        {
            if (_ready) return;
            var serviceProperties = await _service.GetPropertiesAsync(ct);
            // Read the wire properties as well as SDK-known fields so retention
            // settings introduced by newer API versions cannot be overlooked.
            // The SDK exposes blob soft-delete directly.  The raw service-properties
            // payload is not guaranteed to remain available after the SDK has parsed
            // the response (this differs between Azure and emulators), so do not make
            // readiness depend on reparsing that consumed response body.
            if (serviceProperties.Value.DeleteRetentionPolicy?.Enabled == true)
                throw new InvalidOperationException("Use a dedicated session storage account without soft delete, versioning, or restore retention.");
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            var policy = await _container.GetAccessPolicyAsync(cancellationToken: ct);
            if (policy.Value.BlobPublicAccess != PublicAccessType.None)
                throw new InvalidOperationException("Session storage container must be private.");
            _ready = true;
        }
        finally { _initializing.Release(); }
    }

    public async Task<StoredObject?> ReadAsync(string name, CancellationToken ct)
    {
        await Ready(ct);
        try
        {
            var result = await _container.GetBlobClient(name).DownloadContentAsync(ct);
            return new(result.Value.Content.ToArray(), result.Value.Details.ETag.ToString(), result.Value.Details.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<bool> WriteAsync(string name, byte[] bytes, string? expectedVersion, CancellationToken ct)
    {
        await Ready(ct);
        try
        {
            var conditions = expectedVersion is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(expectedVersion) };
            await _container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(bytes),
                new BlobUploadOptions { Conditions = conditions }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412) { return false; }
    }

    public async Task DeleteAsync(string name, string? expectedVersion, CancellationToken ct)
    {
        await Ready(ct);
        try
        {
            await _container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots,
                expectedVersion is null ? null : new BlobRequestConditions { IfMatch = new ETag(expectedVersion) }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 412) { }
    }

    public async IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await Ready(ct);
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
            yield return new(blob.Name, blob.Properties.LastModified ?? DateTimeOffset.MinValue);
    }
}
