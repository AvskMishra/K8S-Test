# Kubernetes Node Patching Readiness Requirements

## 1. Purpose

Define a small .NET 10 application that reads planned node-maintenance schedules, prepares affected Kubernetes nodes for patching, and records what it observes and does. A third-party system performs the actual operating-system patching; this application must not patch or reboot nodes.

The solution must also retain an operator-facing way to inspect cluster details and provide a test workload for validating pod termination and rescheduling behavior.

## 2. Scope

### In scope

- Read and validate a JSON schedule containing node names and patch date/time with an explicit time zone.
- Run a .NET 10 background worker that checks the schedule every five minutes.
- Inspect node readiness, schedulability, taints, and relevant pods through the Kubernetes API.
- Prevent new pods from being scheduled on a node during its two-hour pre-patch window.
- Drain the node at the configured maintenance point, allowing Kubernetes controllers to recreate eligible workloads elsewhere.
- Log schedule evaluation, node state, decisions, Kubernetes operations, outcomes, errors, and test-workload shutdown events.
- Preserve a console option to inspect cluster resources, including nodes and pods.
- Provide a sample .NET pod workload that repeatedly logs a processing cycle and exits gracefully on termination.

### Out of scope

- Applying operating-system patches, rebooting nodes, or invoking the third-party patching system.
- Guaranteeing that every workload can be rescheduled or that application data is durable. Those depend on cluster capacity, controllers, storage, disruption budgets, and workload behavior.
- Executing arbitrary shell commands on Kubernetes nodes. Node administration must use supported Kubernetes API operations; node OS access is not part of this application.

## 3. Schedule input

The application must read a configurable JSON file. Each entry identifies one Kubernetes node and its planned maintenance time. The time zone must be explicit so that local times and daylight-saving transitions are interpreted unambiguously.

Illustrative format:

```json
{
  "patches": [
    {
      "nodeName": "worker-01",
      "patchAt": "2026-10-01T02:00:00",
      "timeZone": "Europe/London"
    }
  ]
}
```

Requirements:

- `nodeName`, `patchAt`, and `timeZone` are required.
- `patchAt` is a local date/time interpreted in `timeZone`; the application converts it to an unambiguous instant for comparisons and logs both the configured local time/zone and the equivalent UTC time.
- Invalid JSON, missing fields, unknown time zones, invalid local times, and unknown node names must be logged and must not trigger a destructive operation.
- The schedule file path must be configurable. The worker must notice corrected/replaced schedule content on a later poll without requiring an application restart.
- Re-reading the same schedule must not repeat a completed drain or cause conflicting node changes.

## 4. Background monitoring and node preparation

- The background worker polls the schedule every five minutes.
- On each poll, it evaluates upcoming entries and reads the current state of each scheduled node from the Kubernetes API.
- It records at least node readiness, whether scheduling is disabled, node taints, and the pods currently assigned to the node.
- If a patch is within the next two hours, the worker cordons the node so that ordinary new pods are not scheduled there. Existing pods are not immediately terminated by cordoning and may continue their current work.
- If the node is already cordoned, the worker recognizes that state and does not treat it as a new failure or repeatedly issue unnecessary changes.
- If a node is NotReady, has blocking taints, is already being drained, or otherwise cannot safely be prepared, the worker records the condition and reports that preparation is blocked. It must not claim that the node is ready for patching.
- Node state must be rechecked before each state-changing operation; the worker must handle transient API errors and retry on a later poll without producing duplicate or contradictory actions.

## 5. Drain and patching handoff

- At the configured drain point, the worker invokes the equivalent of Kubernetes drain/eviction for the scheduled node.
- Drain must respect PodDisruptionBudgets and pod termination grace periods. It must report blockers and timeouts rather than silently force-deleting workloads.
- DaemonSet-managed pods are not evicted by a normal drain and may remain on the node. The expected final state is that only permitted system/DaemonSet pods remain.
- Pods are evicted; Kubernetes controllers may create replacement pods on other schedulable nodes. The application must not describe this as moving the same pod instance.
- Pods without a controller, pods using `emptyDir`, and PDB-blocked pods require explicit policy before destructive handling. The default behavior must be to stop/report rather than force-delete or discard data.
- The worker must report a clear readiness result for the third-party patching system/operator, including whether drain succeeded and which non-system pods remain.
- The application does not start patching. The third party must only begin once it has received/observed a successful readiness result.

**Timing constraint:** the schedule currently supplies a patch time and a two-hour cordon lead time, but does not define when drain begins or how much time drain needs. The implementation must make the drain lead time configurable or agree a default before development. The third party must wait for successful drain completion; starting patching at the same instant drain begins is not safe.

## 6. Graceful-shutdown test workload

Add a separate, minimal .NET 10 application project in the repository for the test workload. Build and deploy it as its own container/pod, independently of the node-management application. This separation lets the management worker remain focused on schedules and Kubernetes operations while the test app demonstrates how a real workload responds to pod termination.

The test application must continuously perform this cycle:

1. Write `START` with the current date/time to its application log.
2. Sleep for 30 seconds.
3. Write `Done processing` with the current date/time.
4. Begin the next cycle unless shutdown has been requested.

On Kubernetes termination (for example, SIGTERM during eviction), it must:

- Stop beginning new work and handle the termination signal within the configured pod termination grace period.
- Log a patch signal and the scheduled patch date/time and time zone when that information is provided to the test workload.
- Log `Quitting the application` and a final `Done processing` entry, then exit cleanly.
- If work is in progress, follow the agreed graceful-completion behavior and finish within the termination grace period; do not claim work completed if it was interrupted.
- Never write another `START` after the shutdown sequence begins or after the final `Done processing` entry.

The log must be written to standard output and be accessible through Kubernetes pod logs. If the test also writes a log file inside the container, the file path and persistence expectations must be documented; a container-local file alone is not durable across pod replacement.

## 7. Console and application integration

- Keep a console mode for operators to fetch cluster details, including nodes and pods, as the current K8sExplorer does.
- Keep the test workload as a separate deployable project; it must not run inside the node-management application's process.
- Deploy the background manager as a Linux container in the Kubernetes cluster, on RHEL 10 worker nodes. It must not require installation as a host service, privileged mode, host networking, SSH, or access to the container runtime socket.
- Use Kubernetes in-cluster authentication through a dedicated ServiceAccount and least-privilege RBAC. Do not package a developer kubeconfig or cluster credentials in the image.
- Run the manager under a Kubernetes controller so it can restart and reschedule if its host node is drained or rebooted. It must reconstruct/reconcile progress from the schedule and current cluster state after restart; local container memory or ephemeral files must not be the sole source of operation state.
- When continuous operation during maintenance is required, run multiple manager replicas on separate nodes with leader election so only the active leader performs state-changing operations. The deployment must use scheduling constraints/spread to avoid placing all replicas on one node.
- Deliver the JSON schedule through a configurable mounted source, such as a ConfigMap volume or persistent/shared storage, and ensure updates become visible to the worker. Do not rely on a file that exists only on one node's local disk.
- Build the manager and test workload as Linux-compatible .NET 10 container images that run on the cluster's RHEL 10-backed Kubernetes nodes. Include required time-zone data in the image and use a documented time-zone identifier format consistently.
- The background worker must use the same supported Kubernetes operations as the console. Prefer a shared application/service layer over launching a console process or shelling out to arbitrary `kubectl` commands.
- Read-only inspection must remain available independently of the background schedule workflow.
- Configuration must identify the in-cluster identity and schedule file location; local console development may continue to use a kubeconfig.
- Required Kubernetes permissions must be limited to the resources and operations needed for node inspection/cordon, pod listing, and eviction/drain, plus existing read-only console views.

## 8. Logging and operational visibility

Use structured, timestamped logs. Each relevant entry should include a correlation/schedule identifier and node name where applicable. Log:

- Worker start/stop and each schedule polling cycle.
- Schedule file load, validation errors, and schedule changes.
- Patch instant in configured local time and zone, plus UTC.
- Node readiness, taints, schedulability, and relevant pod inventory.
- The reason for cordoning, draining, skipping, retrying, or blocking an action.
- Each Kubernetes operation, its result, duration, and any affected pod names.
- Drain completion status, remaining pods, and handoff readiness for the third party.
- Exceptions and API failures, without logging kubeconfig credentials or secrets.
- Test workload cycle and graceful shutdown messages.

Logs must be written to standard output for container/runtime collection. If file logging is enabled, its path, retention/rotation policy, and persistence must be configurable and documented.

## 9. Safety and reliability requirements

- State-changing behavior must be idempotent and safe across process restarts.
- A missing, unreadable, or invalid schedule must not cause a node to be cordoned or drained based on stale/partial data.
- A failed or timed-out drain must not be reported as patch-ready.
- The worker must not force-delete pods by default, bypass PDBs, or silently ignore emptyDir data loss.
- The application must expose enough status/log information for an operator to distinguish ready, in progress, blocked, failed, and not-yet-in-window states.
- Clock comparisons must use time-zone-aware instants; daylight-saving transitions and ambiguous/nonexistent local times must be handled explicitly.

## 10. Acceptance criteria

1. A valid schedule is loaded and its local time, zone, and UTC instant are logged.
2. The worker checks schedules every five minutes and detects schedule file updates without restart.
3. More than two hours before patch time, the node is not changed by the pre-patch workflow.
4. Within two hours, the node is cordoned once; existing pods continue running and new ordinary workloads are not scheduled there.
5. At the agreed drain point, pods are evicted gracefully subject to PDBs and termination grace periods; replacements can become Ready on other nodes when cluster capacity and controllers permit.
6. A blocked or incomplete drain is visible and never produces a successful patch-ready result.
7. Following successful drain, only the configured/allowed system pods remain on the target node, and the third-party handoff status is successful.
8. The sample workload logs repeated `START`, 30-second wait, and `Done processing` cycles during normal operation.
9. On pod termination, the sample workload logs the patch schedule information when available, logs `Quitting the application`, finishes its final processing log, exits, and emits no subsequent `START`.
10. The console can still fetch cluster details without relying on a patch schedule being active.
11. Logs provide an auditable record of decisions and outcomes without exposing credentials.
12. The in-cluster manager runs on RHEL 10-backed nodes using its ServiceAccount, and recovers its schedule evaluation after its pod is restarted or rescheduled.

## 11. Decisions required before implementation

- What is the default drain lead time relative to `patchAt`, and does `patchAt` mean drain start or the time the third party may begin patching?
- How does the third party receive/observe the successful patch-ready signal: shared status file, API, message, or operator polling/logs?
- Should a node be automatically uncordoned after patching? The third-party completion signal and post-patch health check are not yet defined; until they are, automatic uncordon should be disabled.
- What is the policy for unmanaged pods, `emptyDir` data, and PDBs that prevent eviction?
- Can multiple nodes be prepared concurrently, and what minimum cluster capacity/health checks are required before starting a drain?
- Which time-zone identifier format should the JSON schedule use? For the Linux container deployment, prefer IANA identifiers and verify that the .NET image includes compatible time-zone data.
- Where will the JSON schedule and logs live in production, and what are the required retention and backup policies?
- Is high availability during node maintenance required from the first release? If so, confirm that the cluster can run two manager replicas on separate nodes and allow leader-election Lease permissions.