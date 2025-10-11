# Proposal: Per-Host Lease Registry

## Problem
- Lease tracking lives in a static type. `src/RepoQL.ConsoleApp/Host/IdleShutdownHostedService.cs:16-27` stores active client leases in a global `ConcurrentDictionary`, so all instances in the same process share state.
- Tests or tools that spin up multiple hosts simultaneously will see lease entries leak across instances, producing inconsistent shutdown behavior and making it impossible to reason about per-host client counts.
- The gRPC endpoint `HoldClientLease` updates and removes entries using the static registry, which means a single misbehaving client can affect every host in the process, and ThreadStatic or DI scoping cannot isolate the lifetimes.
- Because metrics (`repoql.host.leases.active`) read from the same static store, dashboards cannot distinguish between hosts, and shutting down one host may pre-emptively drop clients that belong to another.

## Solution
- Replace the static registry with an injectable `ILeaseRegistry` implementation that is registered as a scoped or singleton-per-host service. Each `IdleShutdownHostedService` receives its own instance tied to the host lifetime.
- `HoldClientLease` should resolve the registry from `context.GetHttpContext().RequestServices` (like it already does for `HostState`) and update that instance, keeping client tracking scoped correctly.
- Metrics can then tag measurements with a host identifier, and tests can instantiate isolated registries without affecting each other, eliminating cross-talk and making shutdown logic reliable.

