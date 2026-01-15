CREATE OR REPLACE MACRO _severity_rank(s) AS (
  CASE lower(s)
    WHEN 'error'   THEN 4
    WHEN 'warning' THEN 3
    WHEN 'info'    THEN 2
    WHEN 'hint'    THEN 1
    ELSE 0
  END
);

CREATE OR REPLACE VIEW annotations AS
WITH base AS (
    SELECT a.*, sd.uri AS scope_document_uri
    FROM annotation a
             JOIN node sd ON sd.id = a.scope_document_id
),
     span_uri AS (
         SELECT a.id,
                repository_uri_join(b.scope_document_uri,
                                    fragment_from_line_range(CAST(s.start_line AS VARCHAR), CAST(s.end_line AS VARCHAR))) AS uri_from_span
         FROM base b
                  JOIN annotation a ON a.id = b.id
                  LEFT JOIN span s  ON s.id = a.target_span_id
     ),
     node_frag AS (
         SELECT a.id,
                -- Simplified fragment for nodes: just use line range if available
                CASE
                    WHEN s.start_line IS NOT NULL AND s.end_line IS NOT NULL
                        THEN fragment_from_line_range(CAST(s.start_line AS VARCHAR), CAST(s.end_line AS VARCHAR))
                    ELSE NULL
                    END AS frag
         FROM base b
                  JOIN annotation a ON a.id = b.id
                  LEFT JOIN node n  ON n.id = a.target_node_id
                  LEFT JOIN span s  ON s.id = n.span_id
     ),
     edge_uri AS (
         SELECT a.id,
                repository_uri_join(b.scope_document_uri, 'edge=' || CAST(e.id AS TEXT)) AS uri_from_edge
         FROM base b
                  JOIN annotation a ON a.id = b.id
                  LEFT JOIN edge e  ON e.id = a.target_edge_id
     )
SELECT
    a.*,
    COALESCE(
        a.target_uri,
        su.uri_from_span,
        CASE WHEN nf.frag IS NOT NULL THEN repository_uri_join(b.scope_document_uri, nf.frag) END,
        eu.uri_from_edge,
        b.scope_document_uri
    ) AS resolved_target_uri,
    _severity_rank(a.severity) AS severity_rank
FROM annotation a
         JOIN base b   ON b.id = a.id
         LEFT JOIN span_uri su ON su.id = a.id
         LEFT JOIN node_frag nf ON nf.id = a.id
         LEFT JOIN edge_uri eu  ON eu.id = a.id;