using System.Globalization;
using DuckDB.NET.Data;
using RepoQL.Embedding.Storage;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Creates writer DuckDB connections and shared storage-oriented SQL helpers.
/// Complexity: In-memory DuckDB setup, remote object storage httpfs configuration, SQL escaping, and stable timestamps.
/// </summary>
internal static class WriterDuckDbUtilities
{
    private const string DefaultGcsKeyIdEnvVar = "REPOQL_CACHE_GCS_HMAC_KEY_ID";
    private const string DefaultGcsSecretEnvVar = "REPOQL_CACHE_GCS_HMAC_SECRET";

    public static DuckDBConnection OpenInMemoryConnection(WriterSettings settings, bool enableObjectStorageHttpfs)
    {
#pragma warning disable RQL003
        var connection = new DuckDBConnection("Data Source=:memory:");
#pragma warning restore RQL003
        connection.Open();

        if (enableObjectStorageHttpfs)
            ConfigureForObjectStorage(connection, settings);

        return connection;
    }

    public static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    public static string GetInstanceId()
    {
        var raw = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(raw))
            raw = Environment.MachineName;

        return raw
            .Trim()
            .Replace('/', '-')
            .Replace('\\', '-');
    }

    public static string FormatTimestampLiteral(DateTimeOffset timestamp)
        => timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    public static string GetObjectStorageUri(WriterSettings settings, string bucket, string objectPath)
    {
        var scheme = settings.ToObjectStorageBackendSettings().IsS3() ? "s3" : "gs";
        return $"{scheme}://{bucket}/{objectPath}";
    }

    private static void ConfigureForObjectStorage(DuckDBConnection connection, WriterSettings settings)
    {
        ExecuteNonQuery(connection, "INSTALL httpfs;");
        ExecuteNonQuery(connection, "LOAD httpfs;");
        ExecuteNonQuery(connection, "SET enable_object_cache = true;");

        var storageSettings = settings.ToObjectStorageBackendSettings();
        if (storageSettings.IsS3())
        {
            var endpoint = new Uri(storageSettings.S3Endpoint, UriKind.Absolute);
            ExecuteNonQuery(
                connection,
                $"""
                SET s3_endpoint = '{EscapeSqlLiteral(endpoint.Authority)}';
                SET s3_access_key_id = '{EscapeSqlLiteral(storageSettings.S3AccessKey)}';
                SET s3_secret_access_key = '{EscapeSqlLiteral(storageSettings.S3SecretKey)}';
                SET s3_use_ssl = {endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase).ToString().ToLowerInvariant()};
                SET s3_url_style = 'path';
                SET s3_region = 'us-east-1';
                """);
            return;
        }

        ExecuteNonQuery(
            connection,
            $"""
            CREATE OR REPLACE SECRET embedding_writer_gcs (
                TYPE GCS,
                KEY_ID '{EscapeSqlLiteral(GetRequiredEnvironmentValue(DefaultGcsKeyIdEnvVar, "AWS_ACCESS_KEY_ID"))}',
                SECRET '{EscapeSqlLiteral(GetRequiredEnvironmentValue(DefaultGcsSecretEnvVar, "AWS_SECRET_ACCESS_KEY"))}'
            );
            """);
    }

    private static void ExecuteNonQuery(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string GetRequiredEnvironmentValue(string primaryName, string fallbackName)
    {
        var value = Environment.GetEnvironmentVariable(primaryName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        value = Environment.GetEnvironmentVariable(fallbackName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required GCS HMAC credential environment variable '{primaryName}' or fallback '{fallbackName}'.");
    }
}
