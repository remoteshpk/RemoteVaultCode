namespace RemoteVault.Core.Services;

public sealed class ProviderOption
{
    public ProviderOption(
        string id,
        string displayName,
        string connectionTitle,
        string endpointLabel,
        string bucketLabel,
        string accessLabel,
        string secretLabel,
        IReadOnlyList<EndpointOption> endpoints)
    {
        Id = id;
        DisplayName = displayName;
        ConnectionTitle = connectionTitle;
        EndpointLabel = endpointLabel;
        BucketLabel = bucketLabel;
        AccessLabel = accessLabel;
        SecretLabel = secretLabel;
        Endpoints = endpoints;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string ConnectionTitle { get; }

    public string EndpointLabel { get; }

    public string BucketLabel { get; }

    public string AccessLabel { get; }

    public string SecretLabel { get; }

    public IReadOnlyList<EndpointOption> Endpoints { get; }
}

public sealed class EndpointOption
{
    public EndpointOption(string displayName, string value)
    {
        DisplayName = displayName;
        Value = value;
    }

    public string DisplayName { get; }

    public string Value { get; }
}
