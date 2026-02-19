-- Returns commit history for a specific file URI.
-- Includes commits where the file was added, modified, deleted, or renamed.
-- Parameters:
--   uri: File URI (file:///src/Foo.cs, github://owner/repo/src/Foo.cs, local:///path/src/Foo.cs)
CREATE OR REPLACE MACRO git_file_history(uri) AS TABLE (
    SELECT
        c.hash,
        c.author_name,
        c.author_email,
        c.author_date,
        c.message,
        fc.change_type,
        fc.old_uri,
        fc.insertions,
        fc.deletions
    FROM git_file_change fc
    JOIN git_commit c ON fc.commit_hash = c.hash
    WHERE fc.uri = uri OR fc.old_uri = uri
    ORDER BY c.author_date DESC
);
