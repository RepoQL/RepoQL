using System.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Data;

/// <summary>
///     Storage-agnostic graph store contract for safe CRUD over artifacts, spans, nodes, and edges.
///     Also exposes a raw SQL gateway and a universal “anything by URI” resolver. The interface
///     does not reference DuckDB types and can be implemented over any ADO.NET provider.
/// </summary>
public interface IGraphStore : IDisposable
{
    /// <summary>
    ///     Ensures tables, indexes, comments, helper macros, and UDFs exist. Idempotent.
    /// </summary>
    void EnsureSchema();

    /// <summary>
    ///     Inserts a new artifact or returns the existing row with the same digest.
    /// </summary>
    Artifact UpsertArtifact(Artifact artifact);

    /// <summary>
    ///     Retrieves an artifact by its content digest or null when not found.
    /// </summary>
    Artifact? GetArtifactByDigest(string digest);

    /// <summary>
    ///     Retrieves an artifact by its ID or null when not found.
    /// </summary>
    Artifact? GetArtifact(Guid id);

    /// <summary>
    ///     Inserts a span row and returns it.
    /// </summary>
    Span InsertSpan(Span span);

    /// <summary>
    ///     Retrieves a span by identifier or null when not found.
    /// </summary>
    Span? GetSpan(Guid id);

    /// <summary>
    ///     Deletes a span by identifier. Returns true when a row was removed.
    /// </summary>
    bool DeleteSpan(Guid id);

    /// <summary>
    ///     Inserts or updates a node. Enforces document URI uniqueness and artifact existence.
    /// </summary>
    Node UpsertNode(Node node);

    /// <summary>
    ///     Retrieves a node by identifier or null when not found.
    /// </summary>
    Node? GetNode(Guid id);

    /// <summary>
    ///     Retrieves a document node by its container URI ignoring case or null when not found.
    /// </summary>
    Node? GetDocumentByUri(RepoUri uri);

    /// <summary>
    ///     Upsert a document node by its container URI. If a document already exists for the URI,
    ///     updates it in-place (preserving Id) and returns the saved node. Otherwise inserts and returns the new node.
    ///     The supplied <paramref name="document"/>'s properties (kind, artifact_id, properties, timestamps) are used.
    /// </summary>
    Node UpsertDocumentByUri(RepoUri uri, Node document);

    /// <summary>
    ///     Atomically replaces the content of a document: removes existing composition subtree, spans and scoped edges,
    ///     then inserts the provided <paramref name="children"/>, <paramref name="spans"/>, and <paramref name="edges"/>.
    ///     Node Ids are preserved as provided. All <paramref name="spans"/> should have DocumentId = <paramref name="documentId"/>.
    ///     All <paramref name="edges"/> that are scoped to the document should set ScopeDocumentId = <paramref name="documentId"/>.
    /// </summary>
    void ReplaceDocumentContent(Guid documentId, IEnumerable<Node> children, IEnumerable<Span> spans, IEnumerable<Edge> edges);

    /// <summary>
    ///     Deletes a node. When cascadeComposition is true the entire composition subtree is removed.
    /// </summary>
    bool DeleteNode(Guid id, bool cascadeComposition = false);

    /// <summary>
    ///     Enumerates all nodes in the store. Useful for cleanup operations.
    /// </summary>
    IEnumerable<Node> GetAllNodes();

    /// <summary>
    ///     Moves a node to a new location by updating its URI.
    ///     Returns true if the node was found and moved, false otherwise.
    /// </summary>
    bool MoveNode(Guid id, RepoUri newUri);

    /// <summary>
    ///     Inserts or updates an edge. Validates that source and destination nodes exist.
    /// </summary>
    Edge UpsertEdge(Edge edge);

    /// <summary>
    ///     Retrieves an edge by identifier or null when not found.
    /// </summary>
    Edge? GetEdge(Guid id);

    /// <summary>
    ///     Retrieves incident edges for a node. Outgoing and incoming sets can be selected independently.
    /// </summary>
    IEnumerable<Edge> GetEdgesForNode(Guid nodeId, bool outgoing = true, bool incoming = true);

    /// <summary>
    ///     Deletes the composition subtree rooted at the supplied node identifiers.
    ///     Returns the number of nodes deleted including the roots.
    /// </summary>
    int DeleteSubtree(params Guid[] rootIds);

    /// <summary>
    ///     Executes a parameterized SQL statement and maps each row using the supplied mapper.
    ///     The contract uses only BCL ADO.NET abstractions (<see cref="IDataRecord" />).
    /// </summary>
    IEnumerable<T> RawQuery<T>(string sql, Func<IDataRecord, T> map, params object?[] parameters);

    /// <summary>
    ///     Executes a parameterized SQL statement and returns rows as dictionaries keyed by column name.
    ///     This variant avoids ADO.NET types in the delegate.
    /// </summary>
    IEnumerable<IReadOnlyDictionary<string, object?>> RawQuery(string sql, params object?[] parameters);

    /// <summary>
    ///     Resolves a repository-aware URI to matching entities via the built-in resolver.
    ///     Supports: container only → document, #edge=&lt;guid&gt;, #line=a[,b], #char=a[,b].
    ///     Returns zero, one, or many rows.
    /// </summary>
    IEnumerable<ResolvedEntity> EntitiesByUri(string repositoryUri);

    // ----- Annotations -----

    /// <summary>
    ///     Inserts or updates an annotation. When <see cref="Models.Annotation.SemanticKey"/> is provided,
    ///     the operation is idempotent and updates by business key; otherwise updates by <see cref="Models.Annotation.Id"/>.
    ///     Returns the saved annotation instance.
    /// </summary>
    Models.Annotation UpsertAnnotation(Models.Annotation annotation);

    /// <summary>
    ///     Retrieves an annotation by id or null when not found.
    /// </summary>
    Models.Annotation? GetAnnotation(Guid id);

    /// <summary>
    ///     Deletes an annotation by id. Returns true when a row was removed.
    /// </summary>
    bool DeleteAnnotation(Guid id);

    /// <summary>
    ///     Lists annotations scoped to a document with optional kind and minimum-severity filters.
    ///     Severity is compared using the ranking: hint &lt; info &lt; warning &lt; error.
    /// </summary>
    IEnumerable<Models.Annotation> GetAnnotationsForDocument(Guid documentId, string? kinds = null, string? minSeverity = null);
}