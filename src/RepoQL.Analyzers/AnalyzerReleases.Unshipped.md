### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
RQL001 | RepoQL.Testing | Warning | UseCorrectTestFrameworkAnalyzer — detects xUnit attributes, suggests TUnit equivalents
RQL002 | RepoQL.Testing | Warning | UseCorrectAssertionLibraryAnalyzer — detects FluentAssertions usage, suggests AwesomeAssertions
RQL003 | RepoQL.Data | Warning | DuckDbConnectionAnalyzer — flags new DuckDBConnection() outside DuckDbDataStore
RQL004 | RepoQL.UDF | Warning | UdfParameterAnalyzer — parameterless [ScalarUdf]/[StructuredUdf] methods
RQL005 | RepoQL.UDF | Warning | MissingDiscoveryAttributeAnalyzer — UDF methods without [UdfClass] on class
RQL006 | RepoQL.Commands | Warning | MissingDiscoveryAttributeAnalyzer — [Command] methods without [CommandClass] on class
RQL007 | RepoQL.Testing | Warning | NoReflectionInTestsAnalyzer — reflection with BindingFlags.NonPublic in tests
RQL008 | RepoQL.UDF | Warning | UdfDataStoreAnalyzer — [UdfClass] constructors should use IReentrantReader, not DuckDbDataStore
