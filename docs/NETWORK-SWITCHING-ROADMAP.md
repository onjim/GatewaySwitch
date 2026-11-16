# Network Switching Roadmap

## Background

NetSetMan shows a useful distinction that this project should keep explicit:

- A network profile describes the desired state.
- The current state must be read independently.
- A change should be applied only when the desired state differs from the current state.
- Network events are hints to re-read state, not a replacement for verification.

The current application already handles the difficult part specific to a bypass router:
protecting the original configuration, maintaining the default route, and falling back
when health checks fail. The improvements below should reduce polling and make state
transitions easier to reason about without weakening those safeguards.

This document is a design plan, not a commitment to migrate immediately. Each phase
must be independently testable and reversible.

## Physical Architecture

### NetSetMan-Inspired Target

```text
UI / tray
  -> network state reader
  -> profile selector and transition planner
  -> privileged network operation executor
  -> post-change verifier
```

The key change is to separate four concepts that are currently interleaved:

1. **Observation**: read adapters, addresses, default routes, DNS origins, SSID,
   and the saved backup.
2. **Desired profile**: target IPv4/IPv6 gateway and DNS, adapter identity, SSID
   restriction, auto-enable policy.
3. **Decision**: enable, restore, keep, repair, wait, or stop for human action.
4. **Execution**: apply only the missing changes, then verify the observed state.

The UI should render the observation and decision. It should not infer network state
from an operation having been requested.

### Proposed Component Boundaries

```text
GatewayManager
  NetworkStateReader       reads and normalizes current state
  ProfileStore             loads and normalizes settings and backups
  TransitionPlanner        computes a TransitionPlan
  NetworkTransitionExecutor applies the plan's operations
  NetworkStateVerifier     verifies postconditions
  EventCoordinator         debounces network events and schedules re-reads
```

`GatewayManager` can remain the public facade to avoid a broad rewrite. These can
start as internal classes in the same single-file executable, then move to separate
files only if the deployment model changes.

## Phase 0: Guardrails

Before event-driven switching, establish invariants that every phase must preserve:

- The original backup is removed only after routes, DNS, and adapter identity verify.
- A network identity change stops a pending operation.
- Permission or identity errors are never retried as transient route drift.
- No operation changes another adapter or a non-default route.
- A clean route audit may be cached; a detected conflict must force a fresh audit.
- UI messages are concise; full command output belongs in the rotating local log.

Add tests for:

- Event storm debounce.
- Event callback after adapter disappearance.
- Cache invalidation after enable, restore, and repair.
- Profile selection when two conditions match.
- Recovery after sleep/resume and rapid Wi-Fi handoff.

## Phase 1: Event-Triggered State Refresh

### Design

Keep the existing timer as a low-frequency safety net. Add event notifications that
request an immediate state read:

- Address changes.
- Route changes.
- Adapter operational status changes.
- Network availability changes.
- Wi-Fi SSID changes where available.

The event handler must not modify the network. It only marks state as stale and
coalesces notifications into one pending refresh.

```text
network event
  -> mark stale
  -> debounce 300-500 ms
  -> request one snapshot read
  -> evaluate state and transition plan
```

A fallback interval of 15-30 seconds remains necessary because:

- Some DHCP changes do not produce distinguishable events.
- Health checks are intentionally periodic.
- Windows events may be lost while the process is suspended.

### Implementation Steps

1. Introduce an `INetworkChangeSource` abstraction with a real Windows implementation
   and a deterministic fake for regression tests.
2. Add a single-threaded `NetworkEventCoordinator`.
3. Coalesce events while a snapshot or operation is in flight.
4. Replace the existing boolean `isRefreshing` orchestration incrementally with a
   refresh request queue that has at most one queued follow-up.
5. Preserve current snapshot caching and invalidation semantics.
6. Reduce the normal timer to 15-30 seconds after event behavior is proven.

### Acceptance Criteria

- A simulated burst of 100 events causes at most one snapshot read after debounce.
- An event arriving during an operation causes one refresh after the operation ends.
- No event handler directly enables, restores, or repairs the network.
- Existing 3-second behavior remains available behind a configuration switch until
  field testing is complete.

## Phase 2: Declarative Transition Plan

### Design

NetSetMan's strongest structural idea is that activation applies a profile's desired
state. Introduce a read-only plan before execution:

```csharp
internal sealed class TransitionPlan
{
    public TransitionKind Kind;          // Enable, Restore, Repair, Keep, Stop
    public int InterfaceIndex;
    public string TargetIPv4;
    public string TargetIPv6;
    public List<DnsChange> DnsChanges;
    public List<RouteChange> RouteChanges;
    public bool RequiresElevation;
    public string Reason;
}
```

The planner compares observed state with the desired profile and emits only required
operations. For example:

- Correct default route and DNS: `Keep`.
- Missing target default route: `Repair`.
- Manual enable requested with a valid original backup: `Enable`.
- Original network changed: `Restore` or `Stop`, depending on DNS provenance.
- Competing default route: `Repair`, never `Enable`.

### Implementation Steps

1. Add pure planner tests using hand-built observations and profiles.
2. Keep PowerShell command builders as executor details.
3. Ensure the planner never mutates registry state or launches a process.
4. Add a human-readable plan summary to the diagnostic log.
5. Execute one idempotent plan at a time under the existing operation lock.

### Acceptance Criteria

- Reapplying an already-satisfied profile produces no network command.
- The planner cannot select an adapter other than the saved identity.
- No plan can remove the original backup before verification.
- Every automatic action maps to exactly one explicit decision reason.

## Phase 3: Profile Model for Additional Networks

This is optional and should follow the reliability work.

Instead of a single `ConfigGatewayIp`, support named profiles:

```text
Home:
  adapter identity
  target gateway IPv4/IPv6
  SSID restriction
  auto-enable conditions
  health policy
Office:
  ...
```

Requirements:

- Adapter identity remains authoritative; a profile copy must not silently bind to a
  different NIC with the same alias.
- Profiles are ordered and the first matching profile wins.
- The active profile is persisted so restart does not reapply the same profile.
- A profile can include a health policy, but not arbitrary scripts initially.

This is where NetSetMan is broader. Do not add general system settings, printer
switching, domain changes, or arbitrary profile scripts as part of this phase; those
increase privilege and support surface without helping the bypass-router use case.

## Phase 4: Privilege Boundary

NetSetMan uses an installed Windows service so the GUI can run without a UAC prompt.
That is a valid long-term direction, but only after the transition protocol is strict.

### Prerequisites

- The executor accepts a closed set of typed operations, never a free-form command.
- Requests are authenticated per user session.
- The service validates adapter identity and operation bounds.
- The GUI cannot request arbitrary registry paths or PowerShell scripts.
- Audit records identify the requesting user and the exact operation.

Do not implement this phase merely to remove UAC. A privileged service is a larger
security boundary than the current all-administrator desktop process.

## Phase 5: Profile Import and Diagnostics

Useful, low-risk features inspired by NetSetMan:

- Export and import a profile as JSON with a schema version.
- Read current settings into a proposed profile without applying them.
- Show a dry-run diff: current state vs proposed state.
- Include a diagnostic bundle with the rotating log and sanitized route/DNS snapshots.
- Display route and DNS provenance, not just addresses.

Do not import a profile by blindly applying every field. The user should choose which
fields are managed, and adapter identity should be reviewed automatically.

## Recommended Order

1. Finish and commit the current correctness and route-repair stability work.
2. Implement event-triggered refresh with a deterministic fake event source.
3. Add the declarative transition planner.
4. Field-test sleep/resume, Wi-Fi roaming, DHCP renewal, and adapter unplugging.
5. Consider multiple profiles.
6. Consider the privileged service only if UAC becomes a real product requirement.

## Non-Goals

- Reproducing NetSetMan's full feature set.
- Replacing native Windows network state with command-output parsing.
- Treating route events as proof that a route was applied.
- Removing the safety-net timer.
- Running arbitrary profile scripts with elevated privileges.
