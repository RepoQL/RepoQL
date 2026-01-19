-- C# enum types for structural queries
-- These provide type-safe, storage-efficient modifiers

-- Accessibility levels
CREATE TYPE IF NOT EXISTS csharp_accessibility AS ENUM (
    'public',
    'internal',
    'protected',
    'private',
    'protected internal',
    'private protected'
);

-- Type modifiers (class, struct, interface, etc.)
CREATE TYPE IF NOT EXISTS csharp_type_modifier AS ENUM (
    'static',
    'partial',
    'sealed',
    'abstract',
    'readonly'
);

-- Member modifiers (method, property, field, etc.)
CREATE TYPE IF NOT EXISTS csharp_member_modifier AS ENUM (
    'static',
    'async',
    'virtual',
    'override',
    'abstract',
    'sealed',
    'readonly',
    'const',
    'new',
    'extern',
    'volatile'
);

-- Type kinds
CREATE TYPE IF NOT EXISTS csharp_type_kind AS ENUM (
    'class',
    'struct',
    'interface',
    'enum',
    'record',
    'delegate'
);

-- Member kinds
CREATE TYPE IF NOT EXISTS csharp_member_kind AS ENUM (
    'method',
    'constructor',
    'property',
    'field',
    'event',
    'indexer'
);
