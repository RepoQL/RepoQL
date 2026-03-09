using System.Text;
using Pulumi;
using Gcp = Pulumi.Gcp;

return await Deployment.RunAsync<CloudCacheInfrastructureStack>();

/// <summary>
/// Provisions the GCP foundation for the cloud embedding cache.
/// Complexity: storage buckets, queueing, scheduling, service identities, HMAC credentials,
/// Secret Manager storage, and bucket-scoped IAM bindings for the cache services.
/// </summary>
internal sealed class CloudCacheInfrastructureStack : Stack
{
    public CloudCacheInfrastructureStack()
    {
        var env = Deployment.Instance.StackName;
        var config = new Config();

        var bucketLocation = config.Get("bucketLocation") ?? "US";
        var region = config.Get("region")
            ?? new Config("gcp").Get("region")
            ?? "us-central1";
        var compactionSchedule = config.Get("compactionSchedule") ?? "0 0 * * *";
        var schedulerTimeZone = config.Get("schedulerTimeZone") ?? "Etc/UTC";
        var compactionEndpointUrl =
            config.Get("compactionEndpointUrl") ?? "https://replace-with-compaction-endpoint.run.app";

        var embeddingsBucketName = $"repoql-embeddings-{env}";
        var stagingBucketName = $"repoql-staging-{env}";
        var queueName = $"embedding-merge-{env}";
        var schedulerName = $"embedding-compaction-{env}";

        var embeddingsBucket = new Gcp.Storage.Bucket("embeddingsBucket", new Gcp.Storage.BucketArgs
        {
            Name = embeddingsBucketName,
            Location = bucketLocation,
            StorageClass = "STANDARD",
            UniformBucketLevelAccess = true,
        });

        var stagingBucket = new Gcp.Storage.Bucket("stagingBucket", new Gcp.Storage.BucketArgs
        {
            Name = stagingBucketName,
            Location = bucketLocation,
            StorageClass = "STANDARD",
            UniformBucketLevelAccess = true,
            LifecycleRules =
            {
                new Gcp.Storage.Inputs.BucketLifecycleRuleArgs
                {
                    Action = new Gcp.Storage.Inputs.BucketLifecycleRuleActionArgs
                    {
                        Type = "Delete",
                    },
                    Condition = new Gcp.Storage.Inputs.BucketLifecycleRuleConditionArgs
                    {
                        Age = 1,
                    },
                },
            },
        });

        var mergeQueue = new Gcp.CloudTasks.Queue("mergeQueue", new Gcp.CloudTasks.QueueArgs
        {
            Name = queueName,
            Location = region,
            RetryConfig = new Gcp.CloudTasks.Inputs.QueueRetryConfigArgs
            {
                MaxAttempts = 5,
                MinBackoff = "10s",
                MaxBackoff = "600s",
            },
        });

        var embeddingServiceAccount = new Gcp.ServiceAccount.Account("embeddingServiceAccount", new Gcp.ServiceAccount.AccountArgs
        {
            AccountId = $"embedding-service-{env}",
            DisplayName = $"RepoQL embedding service ({env})",
            Description = "Reads embeddings, writes staging objects, and enqueues merge work.",
        });

        var cacheWriterAccount = new Gcp.ServiceAccount.Account("cacheWriterAccount", new Gcp.ServiceAccount.AccountArgs
        {
            AccountId = $"cache-writer-{env}",
            DisplayName = $"RepoQL cache writer ({env})",
            Description = "Merges staging parquet files into the permanent embeddings bucket.",
        });

        var compactionAccount = new Gcp.ServiceAccount.Account("compactionAccount", new Gcp.ServiceAccount.AccountArgs
        {
            AccountId = $"compaction-{env}",
            DisplayName = $"RepoQL compaction ({env})",
            Description = "Runs shard compaction and eviction jobs against the embeddings bucket.",
        });

        var embeddingServiceHmac = new Gcp.Storage.HmacKey("embeddingServiceHmac", new Gcp.Storage.HmacKeyArgs
        {
            ServiceAccountEmail = embeddingServiceAccount.Email,
            State = "ACTIVE",
        });

        var cacheWriterHmac = new Gcp.Storage.HmacKey("cacheWriterHmac", new Gcp.Storage.HmacKeyArgs
        {
            ServiceAccountEmail = cacheWriterAccount.Email,
            State = "ACTIVE",
        });

        var compactionHmac = new Gcp.Storage.HmacKey("compactionHmac", new Gcp.Storage.HmacKeyArgs
        {
            ServiceAccountEmail = compactionAccount.Email,
            State = "ACTIVE",
        });

        var embeddingServiceHmacSecrets = CreateHmacSecrets(
            "embedding-service",
            env,
            embeddingServiceHmac.AccessId,
            embeddingServiceHmac.Secret);

        var cacheWriterHmacSecrets = CreateHmacSecrets(
            "cache-writer",
            env,
            cacheWriterHmac.AccessId,
            cacheWriterHmac.Secret);

        var compactionHmacSecrets = CreateHmacSecrets(
            "compaction",
            env,
            compactionHmac.AccessId,
            compactionHmac.Secret);

        _ = new Gcp.Storage.BucketIAMMember("embeddingServiceEmbeddingsRead", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = embeddingsBucket.Name,
            Role = "roles/storage.objectViewer",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.Storage.BucketIAMMember("embeddingServiceStagingWrite", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = stagingBucket.Name,
            Role = "roles/storage.objectCreator",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.CloudTasks.QueueIamMember("embeddingServiceQueueEnqueue", new Gcp.CloudTasks.QueueIamMemberArgs
        {
            Name = mergeQueue.Name,
            Location = region,
            Role = "roles/cloudtasks.enqueuer",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.Storage.BucketIAMMember("cacheWriterStagingAdmin", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = stagingBucket.Name,
            Role = "roles/storage.objectAdmin",
            Member = AsServiceAccountMember(cacheWriterAccount.Email),
        });

        _ = new Gcp.Storage.BucketIAMMember("cacheWriterEmbeddingsAdmin", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = embeddingsBucket.Name,
            Role = "roles/storage.objectAdmin",
            Member = AsServiceAccountMember(cacheWriterAccount.Email),
        });

        _ = new Gcp.Storage.BucketIAMMember("compactionEmbeddingsAdmin", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = embeddingsBucket.Name,
            Role = "roles/storage.objectAdmin",
            Member = AsServiceAccountMember(compactionAccount.Email),
        });

        var schedulerPayload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{{\"environment\":\"{env}\",\"trigger\":\"nightly-compaction\"}}"));

        var compactionScheduler = new Gcp.CloudScheduler.Job("compactionScheduler", new Gcp.CloudScheduler.JobArgs
        {
            Name = schedulerName,
            Region = region,
            Description = $"Nightly compaction trigger for the {env} cloud embedding cache.",
            Schedule = compactionSchedule,
            TimeZone = schedulerTimeZone,
            HttpTarget = new Gcp.CloudScheduler.Inputs.JobHttpTargetArgs
            {
                HttpMethod = "POST",
                Uri = compactionEndpointUrl,
                Headers =
                {
                    { "Content-Type", "application/json" },
                },
                Body = schedulerPayload,
            },
        });

        EmbeddingsBucketName = embeddingsBucket.Name;
        StagingBucketName = stagingBucket.Name;
        MergeQueueName = mergeQueue.Name;
        CompactionSchedulerName = compactionScheduler.Name;
        ServiceAccountEmails = Output.Tuple(
                embeddingServiceAccount.Email,
                cacheWriterAccount.Email,
                compactionAccount.Email)
            .Apply(values => new Dictionary<string, string>
            {
                ["embeddingService"] = values.Item1,
                ["cacheWriter"] = values.Item2,
                ["compaction"] = values.Item3,
            });
        HmacSecretResourceNames = Output.Tuple(
                embeddingServiceHmacSecrets.AccessKeyIdSecretId,
                embeddingServiceHmacSecrets.SecretKeySecretId,
                cacheWriterHmacSecrets.AccessKeyIdSecretId,
                cacheWriterHmacSecrets.SecretKeySecretId,
                compactionHmacSecrets.AccessKeyIdSecretId,
                compactionHmacSecrets.SecretKeySecretId)
            .Apply(values => new Dictionary<string, string>
            {
                ["embeddingServiceAccessKeyId"] = values.Item1,
                ["embeddingServiceSecretKey"] = values.Item2,
                ["cacheWriterAccessKeyId"] = values.Item3,
                ["cacheWriterSecretKey"] = values.Item4,
                ["compactionAccessKeyId"] = values.Item5,
                ["compactionSecretKey"] = values.Item6,
            });
    }

    [Output]
    public Output<string> EmbeddingsBucketName { get; private set; } = null!;

    [Output]
    public Output<string> StagingBucketName { get; private set; } = null!;

    [Output]
    public Output<string> MergeQueueName { get; private set; } = null!;

    [Output]
    public Output<string> CompactionSchedulerName { get; private set; } = null!;

    [Output]
    public Output<Dictionary<string, string>> ServiceAccountEmails { get; private set; } = null!;

    [Output]
    public Output<Dictionary<string, string>> HmacSecretResourceNames { get; private set; } = null!;

    private static Output<string> AsServiceAccountMember(Output<string> email) =>
        email.Apply(static value => $"serviceAccount:{value}");

    private static HmacSecretBundle CreateHmacSecrets(
        string accountName,
        string env,
        Input<string> accessId,
        Input<string> secretValue)
    {
        var accessKeyIdSecret = new Gcp.SecretManager.Secret($"{accountName}AccessKeyIdSecret", new Gcp.SecretManager.SecretArgs
        {
            SecretId = $"repoql-{accountName}-hmac-access-key-id-{env}",
            Replication = new Gcp.SecretManager.Inputs.SecretReplicationArgs
            {
                Auto = new Gcp.SecretManager.Inputs.SecretReplicationAutoArgs(),
            },
        });

        _ = new Gcp.SecretManager.SecretVersion($"{accountName}AccessKeyIdSecretVersion", new Gcp.SecretManager.SecretVersionArgs
        {
            Secret = accessKeyIdSecret.Id,
            SecretData = accessId,
        });

        var secretKeySecret = new Gcp.SecretManager.Secret($"{accountName}SecretKeySecret", new Gcp.SecretManager.SecretArgs
        {
            SecretId = $"repoql-{accountName}-hmac-secret-{env}",
            Replication = new Gcp.SecretManager.Inputs.SecretReplicationArgs
            {
                Auto = new Gcp.SecretManager.Inputs.SecretReplicationAutoArgs(),
            },
        });

        _ = new Gcp.SecretManager.SecretVersion($"{accountName}SecretKeySecretVersion", new Gcp.SecretManager.SecretVersionArgs
        {
            Secret = secretKeySecret.Id,
            SecretData = secretValue,
        });

        return new HmacSecretBundle(accessKeyIdSecret.Id, secretKeySecret.Id);
    }

    private sealed record HmacSecretBundle(
        Output<string> AccessKeyIdSecretId,
        Output<string> SecretKeySecretId);
}
