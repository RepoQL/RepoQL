using System.Text;
using Pulumi;
using Cloudflare = Pulumi.Cloudflare;
using Gcp = Pulumi.Gcp;

return await Deployment.RunAsync<CloudCacheInfrastructureStack>();

/// <summary>
/// Provisions the GCP foundation for the cloud embedding cache and Cloudflare DNS/CDN proxy.
/// Complexity: storage buckets, scheduling, service identities, HMAC credentials,
/// Secret Manager storage, Eventarc IAM prerequisites, bucket-scoped IAM bindings,
/// and Cloudflare proxied DNS records for public gRPC services.
/// </summary>
internal sealed class CloudCacheInfrastructureStack : Stack
{
    public CloudCacheInfrastructureStack()
    {
        var env = Deployment.Instance.StackName;
        var config = new Config();
        var gcpConfig = new Config("gcp");

        var bucketLocation = config.Get("bucketLocation") ?? "US";
        var region = config.Get("region")
            ?? gcpConfig.Get("region")
            ?? "us-central1";
        var compactionSchedule = config.Get("compactionSchedule") ?? "0 0 * * *";
        var schedulerTimeZone = config.Get("schedulerTimeZone") ?? "Etc/UTC";
        var compactionEndpointUrl =
            config.Get("compactionEndpointUrl") ?? "https://replace-with-compaction-endpoint.run.app";

        var embeddingsBucketName = $"repoql-embeddings-{env}";
        var stagingBucketName = $"repoql-staging-{env}";
        var schedulerName = $"embedding-compaction-{env}";

        // Look up the project to get the project number for service agent IAM.
        var gcpProjectId = gcpConfig.Require("project");

        var project = Gcp.Organizations.GetProject.Invoke(new Gcp.Organizations.GetProjectInvokeArgs
        {
            ProjectId = gcpProjectId,
        });

        // --- Firestore (product analytics) ---

        var firestoreApi = new Gcp.Projects.Service("firestoreApi", new Gcp.Projects.ServiceArgs
        {
            Project = gcpProjectId,
            ServiceName = "firestore.googleapis.com",
            DisableDependentServices = false,
            DisableOnDestroy = false,
        });

        var firestoreDb = new Gcp.Firestore.Database("productAnalyticsDb", new Gcp.Firestore.DatabaseArgs
        {
            Project = gcpProjectId,
            Name = "(default)",
            LocationId = region,
            Type = "FIRESTORE_NATIVE",
        }, new CustomResourceOptions { DependsOn = { firestoreApi } });

        // --- Artifact Registry ---

        var containerRepo = new Gcp.ArtifactRegistry.Repository("containerRepo", new Gcp.ArtifactRegistry.RepositoryArgs
        {
            RepositoryId = "repoql",
            Location = region,
            Format = "DOCKER",
            Description = "Container images for RepoQL embedding services.",
        });

        // --- Storage ---

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

        var embeddingServiceAccount = new Gcp.ServiceAccount.Account("embeddingServiceAccount", new Gcp.ServiceAccount.AccountArgs
        {
            AccountId = $"embedding-service-{env}",
            DisplayName = $"RepoQL embedding service ({env})",
            Description = "Reads embeddings and writes staging objects. Also serves as the Eventarc trigger identity.",
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

        // --- Bucket-scoped IAM ---

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

        // --- Secret Manager access ---
        // Service accounts need to read secrets mounted as env vars by Cloud Run.

        _ = new Gcp.Projects.IAMMember("embeddingServiceSecretAccessor", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/secretmanager.secretAccessor",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.Projects.IAMMember("cacheWriterSecretAccessor", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/secretmanager.secretAccessor",
            Member = AsServiceAccountMember(cacheWriterAccount.Email),
        });

        // --- Eventarc IAM prerequisites ---
        // The embedding service SA is reused as the Eventarc trigger identity.
        // It needs eventarc.eventReceiver to receive GCS events.
        // The GCS service agent needs pubsub.publisher to emit notifications.

        _ = new Gcp.Projects.IAMMember("embeddingServiceEventarcReceiver", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/eventarc.eventReceiver",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.Projects.IAMMember("gcsServiceAgentPubsubPublisher", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/pubsub.publisher",
            Member = project.Apply(p => $"serviceAccount:service-{p.Number}@gs-project-accounts.iam.gserviceaccount.com"),
        });

        // Eventarc service agent needs to read staging bucket metadata to validate triggers.
        _ = new Gcp.Storage.BucketIAMMember("eventarcServiceAgentStagingViewer", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = stagingBucket.Name,
            Role = "roles/storage.objectViewer",
            Member = project.Apply(p => $"serviceAccount:service-{p.Number}@gcp-sa-eventarc.iam.gserviceaccount.com"),
        });

        // Pub/Sub SA needs to mint OIDC tokens for authenticated push to Cloud Run.
        _ = new Gcp.Projects.IAMMember("pubsubServiceAgentTokenCreator", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/iam.serviceAccountTokenCreator",
            Member = project.Apply(p => $"serviceAccount:service-{p.Number}@gcp-sa-pubsub.iam.gserviceaccount.com"),
        });

        // --- Firestore IAM ---

        _ = new Gcp.Projects.IAMMember("embeddingServiceFirestoreUser", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/datastore.user",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        // --- Cloud Trace ---
        // Both services send OTLP to the Cloud Run built-in collector on localhost:4317.

        _ = new Gcp.Projects.IAMMember("embeddingServiceTraceAgent", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/cloudtrace.agent",
            Member = AsServiceAccountMember(embeddingServiceAccount.Email),
        });

        _ = new Gcp.Projects.IAMMember("cacheWriterTraceAgent", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/cloudtrace.agent",
            Member = AsServiceAccountMember(cacheWriterAccount.Email),
        });

        // --- Compaction scheduler ---

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

        // --- Monitoring dashboard ---

        _ = new Gcp.Monitoring.Dashboard("embeddingDashboard", new Gcp.Monitoring.DashboardArgs
        {
            DashboardJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "dashboard.json")),
            Project = gcpProjectId,
        });

        // --- Unified cloud service account ---
        // Single service account for the merged cloud service (embedding + inference).
        // Writer remains separate with its own service account.

        var cloudServiceAccount = new Gcp.ServiceAccount.Account("cloudServiceAccount", new Gcp.ServiceAccount.AccountArgs
        {
            AccountId = $"cloud-service-{env}",
            DisplayName = $"RepoQL cloud service ({env})",
            Description = "Unified service account for the merged cloud service (embedding + inference).",
        });

        var cloudServiceHmac = new Gcp.Storage.HmacKey("cloudServiceHmac", new Gcp.Storage.HmacKeyArgs
        {
            ServiceAccountEmail = cloudServiceAccount.Email,
            State = "ACTIVE",
        });

        var cloudServiceHmacSecrets = CreateHmacSecrets(
            "cloud-service",
            env,
            cloudServiceHmac.AccessId,
            cloudServiceHmac.Secret);

        // Storage: read embeddings (cache lookup), write staging (cache write-back)
        _ = new Gcp.Storage.BucketIAMMember("cloudServiceEmbeddingsRead", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = embeddingsBucket.Name,
            Role = "roles/storage.objectViewer",
            Member = AsServiceAccountMember(cloudServiceAccount.Email),
        });

        _ = new Gcp.Storage.BucketIAMMember("cloudServiceStagingWrite", new Gcp.Storage.BucketIAMMemberArgs
        {
            Bucket = stagingBucket.Name,
            Role = "roles/storage.objectCreator",
            Member = AsServiceAccountMember(cloudServiceAccount.Email),
        });

        // Firestore project ID secret (Cloud Run mounts this as Firestore__ProjectId)
        var firestoreProjectSecret = new Gcp.SecretManager.Secret("firestoreProjectSecret", new Gcp.SecretManager.SecretArgs
        {
            SecretId = $"repoql-cloud-firestore-project",
            Replication = new Gcp.SecretManager.Inputs.SecretReplicationArgs
            {
                Auto = new Gcp.SecretManager.Inputs.SecretReplicationAutoArgs(),
            },
        });

        _ = new Gcp.SecretManager.SecretVersion("firestoreProjectSecretVersion", new Gcp.SecretManager.SecretVersionArgs
        {
            Secret = firestoreProjectSecret.Id,
            SecretData = gcpProjectId,
        });

        // Secrets, Firestore, Trace
        _ = new Gcp.Projects.IAMMember("cloudServiceSecretAccessor", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/secretmanager.secretAccessor",
            Member = AsServiceAccountMember(cloudServiceAccount.Email),
        });

        _ = new Gcp.Projects.IAMMember("cloudServiceFirestoreUser", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/datastore.user",
            Member = AsServiceAccountMember(cloudServiceAccount.Email),
        });

        _ = new Gcp.Projects.IAMMember("cloudServiceTraceAgent", new Gcp.Projects.IAMMemberArgs
        {
            Project = gcpProjectId,
            Role = "roles/cloudtrace.agent",
            Member = AsServiceAccountMember(cloudServiceAccount.Email),
        });

        // --- Cloudflare DNS & CDN proxy ---
        // Proxied CNAME records to Cloud Run services. Cloudflare terminates TLS at the edge,
        // provides free DDoS protection and analytics. SSL "Full" mode works because Cloud Run
        // presents a valid *.run.app cert. gRPC toggle enables HTTP/2 gRPC proxying (unary RPCs).
        // Auth remains at the application layer (ApiKeyAuthInterceptor).

        var domain = config.Get("domain") ?? "repoql.ai";
        var cloudServiceOrigin = config.Require("cloudServiceOrigin");

        var zone = Cloudflare.GetZone.Invoke(new Cloudflare.GetZoneInvokeArgs
        {
            Filter = new Cloudflare.Inputs.GetZoneFilterInputArgs
            {
                Name = domain,
            },
        });

        var zoneId = zone.Apply(z => z.Id);

        _ = new Cloudflare.ZoneSetting("grpc", new Cloudflare.ZoneSettingArgs
        {
            ZoneId = zoneId,
            SettingId = "grpc",
            Value = "on",
        });

        _ = new Cloudflare.ZoneSetting("ssl", new Cloudflare.ZoneSettingArgs
        {
            ZoneId = zoneId,
            SettingId = "ssl",
            Value = "full",
        });

        _ = new Cloudflare.ZoneSetting("alwaysUseHttps", new Cloudflare.ZoneSettingArgs
        {
            ZoneId = zoneId,
            SettingId = "always_use_https",
            Value = "on",
        });

        var apiDns = new Cloudflare.DnsRecord("apiDns", new Cloudflare.DnsRecordArgs
        {
            ZoneId = zoneId,
            Name = "api",
            Type = "CNAME",
            Content = cloudServiceOrigin,
            Ttl = 1, // automatic when proxied
            Proxied = true,
        });

        // --- Outputs ---

        CloudServiceUrl = apiDns.Name.Apply(n => $"https://{n}.{domain}");
        EmbeddingsBucketName = embeddingsBucket.Name;
        StagingBucketName = stagingBucket.Name;
        CompactionSchedulerName = compactionScheduler.Name;
        ServiceAccountEmails = Output.Tuple(
                embeddingServiceAccount.Email,
                cacheWriterAccount.Email,
                compactionAccount.Email,
                cloudServiceAccount.Email)
            .Apply(values => new Dictionary<string, string>
            {
                ["embeddingService"] = values.Item1,
                ["cacheWriter"] = values.Item2,
                ["compaction"] = values.Item3,
                ["cloudService"] = values.Item4,
            });
        HmacSecretResourceNames = Output.Tuple(
                embeddingServiceHmacSecrets.AccessKeyIdSecretId,
                embeddingServiceHmacSecrets.SecretKeySecretId,
                cacheWriterHmacSecrets.AccessKeyIdSecretId,
                cacheWriterHmacSecrets.SecretKeySecretId,
                compactionHmacSecrets.AccessKeyIdSecretId,
                compactionHmacSecrets.SecretKeySecretId,
                cloudServiceHmacSecrets.AccessKeyIdSecretId,
                cloudServiceHmacSecrets.SecretKeySecretId)
            .Apply(values => new Dictionary<string, string>
            {
                ["embeddingServiceAccessKeyId"] = values.Item1,
                ["embeddingServiceSecretKey"] = values.Item2,
                ["cacheWriterAccessKeyId"] = values.Item3,
                ["cacheWriterSecretKey"] = values.Item4,
                ["compactionAccessKeyId"] = values.Item5,
                ["compactionSecretKey"] = values.Item6,
                ["cloudServiceAccessKeyId"] = values.Item7,
                ["cloudServiceSecretKey"] = values.Item8,
            });
    }

    [Output]
    public Output<string> CloudServiceUrl { get; private set; } = null!;

    [Output]
    public Output<string> EmbeddingsBucketName { get; private set; } = null!;

    [Output]
    public Output<string> StagingBucketName { get; private set; } = null!;

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
