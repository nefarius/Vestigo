# Windows servicing diagnostics

`WindowsServicing` is an opt-in, read-only diagnostic module, separate from the
`Updates` module that updates Vestigo itself. Enable it with Jobs:

```json
{
  "Vestigo": {
    "Modules": {
      "WindowsServicing": { "Enabled": true },
      "Jobs": { "Enabled": true }
    },
    "WindowsServicing": {
      "MaxResults": 200,
      "MaxLogBytes": 262144,
      "MaxLogLines": 200,
      "NativeCallTimeout": "00:02:00"
    }
  }
}
```

Configure API tokens for remote access. Queue paths, update history, package
names, service names and log excerpts can disclose sensitive information.
Enabling the module without authentication produces a startup warning. The MSI
module tree exposes it under Jobs, disabled by default.

## Reboot and pending file operations

`get_reboot_pending` reports independent sources with `pending`, `notPending`, or
`unknown` states. A positive reboot signal wins over unknown sources. Otherwise,
any unknown source makes the overall result unknown. Sources are the 64-bit HKLM
view: Windows Update RebootRequired, CBS RebootPending/PackagesPending,
CBS SessionsPending, Windows Installer InProgress, both Session Manager pending
file queues, configured versus active computer name, Netlogon JoinDomain and
AvoidSpnSet, UpdateExeVolatile, and WUA SystemInfo.RebootRequired.

SessionsPending and Installer InProgress are advisory activity indications:
presence is unknown rather than a definitive reboot request. Registry indicators
are heuristics and may be stale. Missing computer-name values, wrong value types,
denied reads and failed WUA calls remain unknown. This is not an exhaustive
inventory of every application's reboot requirements.

`list_pending_file_operations` preserves raw source and destination strings,
queue value name, zero-based pair index, and the replacement `!` marker. Empty
destinations mean deletion; an unmatched source means malformed, not deletion.
Recognized `\??\C:\...` and `\??\UNC\...` paths get a normalized path in addition
to the original. Other NT namespaces retain their raw form and have no normalized
path. One result cap applies across both queues. Access errors are per-source.
Raw queue paths are returned even outside Read roots; no queued file is opened,
hashed, signature-checked, or probed for existence. Enabling the module authorizes
this registry metadata disclosure, not arbitrary filesystem content access.

## Windows Update Agent

`list_windows_updates` starts a background job using CsWin32-generated WUA COM interfaces with
`Online=false`. It supports pending or installed applicability results, hidden
and driver inclusion, and a bounded return count. It returns identity, revision,
title, KB references, type, installed/downloaded/hidden/reboot flags and WUA search
result code (2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted). Up to 100
search-warning HRESULTs are preserved. Partial
success must not be interpreted as a complete inventory. Categories, effective
MDM/WSUS policy, download progress and USO activity are not provided.

`get_update_history` queries a newest-first window with an offset. `Examined`
counts the window before failure filtering; advance by that count. `Truncated`
means older history exists. Result code 3 includes partial success, 4 failure,
and 5 abortion. HRESULTs are unsigned hexadecimal strings. WUA history dates
are UTC. History can be reset and installed applicability results are not a
complete installed CBS package inventory.

`get_windows_update_status` reports WUA reboot and installer-busy state,
registered update services/default/managed flags, and wuauserv, BITS, UsoSvc,
WaaSMedicSvc, TrustedInstaller and DoSvc states. A stopped demand/trigger-start
service is not proof of a blocker. An unavailable source is an explicit error.

All COM interfaces are acquired, used and released on one MTA worker per call. COM initialization is explicitly balanced; generated BSTR safe handles own input strings and every returned BSTR is freed after conversion. Activation uses generated coclass CLSIDs. No ProgID registry lookup, late-bound dynamic dispatch or handwritten WUA interfaces remain.
WUA, DISM and Restart Manager share one native-call slot, with no waiting queue.
Busy calls fail with `servicing_busy`. `NativeCallTimeout` bounds how long a caller
waits, not the underlying Windows operation. Timeout or cancellation retains the
slot until native work actually finishes. There is no automatic forced COM/CBS
termination. A permanently hung native call requires operator investigation or
service restart before another native diagnostic can run. Inventory jobs use
normal Jobs retention/output limits; `read_job_output` returns a JSON
`OperationResult`, possibly over multiple pages. A small global job output cap
can truncate that JSON, as indicated by Jobs' truncation marker.

Offline applicability work can be expensive and WUA can update its own cache or
logs. No online scan, download, installation, repair or reboot is requested.

## CBS and package state

`get_servicing_status` is a fast registry snapshot and metadata for
`WinSxS\pending.xml` and `reboot.xml`. XML contents are not parsed. Component-store
integrity remains unknown: no CheckHealth, ScanHealth, SFC or repair runs.

`list_servicing_packages` starts a job using the documented DISM API for the
running Windows image. It returns bounded package identities, raw state/release
codes and state names, including installed, installPending, uninstallPending,
staged, removed, superseded and partiallyInstalled. Native DISM enumeration itself
is not limited by the return cap. Elevation is normally required. The API may
write its own DISM log. Microsoft.Dism 6.1.0 (MIT) owns native package-buffer
marshalling and session handles. A resolver on the wrapper assembly loads only
System32's DismApi.dll; unexpected imports fail closed. ADK generation selection
is not used. The wrapper materializes the full package collection before Vestigo
applies its return cap. The complete initialization/session/shutdown lifetime is
serialized across source instances. Sessions are disposed before shutdown, including
on enumeration failure; initialization failure does not trigger shutdown.
If shutdown also fails after an operation error, the original HRESULT is retained
and the secondary shutdown type/HRESULT is logged without its native message.
No localized command output is parsed and no arbitrary arguments run.

## Failure evidence and logs

`get_servicing_logs` tails only CBS.log and DISM dism.log, through the existing
Read-root policy and shared-access text reader. Configure a narrow Read root
covering `%windir%\Logs` if log content should be available. No root is added
automatically. Existing `Logs:MaxTailBytes/MaxTailLines` caps also apply, even when
the Logs module is disabled. UTF-8/UTF-16 BOM handling, cursor and truncation
metadata come from the existing TextTailer. Rotated archives and Windows Update
ETL conversion are not included. A literal filter applies within the tail only.

`get_update_failure_details` collects recent failed/partial/aborted WUA attempts,
warning/error/critical events from WindowsUpdateClient, Servicing, BITS and
Delivery Optimization operational channels, and the two log tails. Channels may
be absent or disabled; individual source errors are preserved. Event queries use
the existing EventLog result/scan caps. Returned event messages are capped at
4,000 characters with an explicit truncation marker. `since` filters event/history
timestamps (default: last 24 hours); log timestamps remain raw and are not parsed.
The optional literal filter matches history title/id/HRESULT, event message text
and log lines. It does not resolve KB identifiers to update GUIDs or infer a
causal relationship between differently named entries.

The examined history window may not reach `since`. Empty results are not evidence
that no failure exists. Observations, hypotheses and recommendations are separate;
the initial implementation returns evidence and an investigation recommendation,
with no automatic causal hypotheses or HRESULT remediation database.

## Storage and file lockers

`get_update_storage_status` reports Windows-volume total/available bytes and
logical file metadata for DataStore.edb, CBS.log, dism.log and pending.xml.
Unavailable volume data is null and unavailable file metadata is unknown.
There is no recursive cache scan, reserved-storage measurement, low-space verdict,
or WinSxS allocation estimate: hard links make directory sums misleading.

`get_file_lockers` resolves one existing file through a Read root before calling
Restart Manager. A path that resolves but is not an existing file, such as a
directory, fails with `not_a_file`. It returns application/service names, PID, process start time,
session ID and reboot-reason flags. It retries list changes at most three times
and caps native list allocation at 4,096 processes. An unstable or oversized list
is explicitly truncated. Restart Manager APIs, process structures, session-key sizing and Windows error constants use CsWin32-generated bindings with System32-restricted imports. Restart Manager sessions always end; no shutdown or
restart API is imported. Results do not enumerate all kernel references,
minifilter activity, or every cause of a failed replacement. Empty results never
guarantee that a file can be replaced.

## Scope and validation

No install, repair, cache reset, queue cancellation, online scan, component-store
analysis or reboot tools are exposed. There are no mutation gates to enable in
this first increment. Existing server authentication, module gating and root
policies apply. All eleven tools advertise read-only, non-destructive MCP metadata.

Unit tests exercise unknown/positive reboot aggregation, malformed queues and
normalization, global bounds, fixed WUA criteria, MTA execution, timeout and
cancellation concurrency, HRESULT preservation, Jobs output, Read-root rejection,
module dependencies/registration and metadata, DISM cleanup ordering and
cross-caller serialization. The DISM loader test checks the loaded module's
System32 path and rejects unexpected imports. Repository tests verify generated
catalogs and installer module/default parity. Set `VESTIGO_SERVICING_SMOKE=1` to
run the optional native WUA history/status, DISM package and Restart Manager
smoke checks. An elevated Windows token is required for two consecutive DISM
package enumeration cycles; a non-elevated token instead exercises repeated
initialization failures and verifies the original elevation-required HRESULT.
The smoke checks perform only diagnostic enumeration, without an applicability scan.
Set `VESTIGO_SERVICING_SCAN_SMOKE=1` separately to exercise a bounded-result
offline applicability search; WUA can still perform substantial local work.

Native contracts: [WUA offline search setting](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-put_online),
[DISM package API](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism/dismgetpackages-function),
[DISM package states](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism/dismpackagefeaturestate-enumeration),
[Restart Manager process information](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_process_info).
