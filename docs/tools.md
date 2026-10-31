# MCP tools

The generated parameter catalog is [generated/mcp-tools.md](generated/mcp-tools.md). Examples below use the default loopback endpoint.

## Offline

`inspect_wpp_pdb` returns WPP control GUIDs, names, and flag bits from one PDB.

`validate_decoding_sources` loads explicit PDB and TMF paths and reports whether a decoding context was built.

`scan_etl_metadata` returns the distinct PDB name, GUID, age, and symbol-server index prefix referenced by the ETL files.

`convert_etl` writes `json` or `ndjson` under an allowed output root. The tool result is a summary: path, event count, bytes, missing WPP formats, truncation, and symbol diagnostics. It does not return the trace payload.

## Realtime

`list_etw_sessions` returns every session name Windows reports. It does not stop sessions.

`start_capture` accepts provider GUIDs and/or PDB paths. PDB control GUIDs are enabled automatically. TMF files contribute message formats but not provider GUIDs. The call returns when the session has started, not when the trace ends. Keyword bitmasks accept a decimal value or a `0x` hexadecimal value. The level must be a named `TraceLevelSelection` value.

`list_captures` and `get_capture` report state, event count, bytes, missing formats, and the stop reason.

`read_capture` returns NDJSON lines after `afterSequence`. Pass the returned `NextSequence` to the next call. `limit` is clamped to `MaxReadBatch`.

`stop_capture` cancels the reader and disposes the ETW session.

Capture duration cannot exceed `MaxCaptureDuration`. The spool is removed after `SpoolRetention` once the capture has stopped.

## Jobs

`list_jobs`, `get_job`, `wait_job`, `read_job_output` and `cancel_job` manage background jobs that other tools start. `read_job_output` returns text after a byte `offset`; pass the returned `NextOffset` to continue. Finished jobs are kept for `JobRetention`.

## Devices

The `Devices` module changes drivers and device state. Everything here needs the elevated service account and drivers run in the kernel: installing one is arbitrary code execution at the highest privilege. That is the purpose of the module, so enable it only on machines you intend to debug drivers on (`Vestigo:Modules:Devices:Enabled`), and protect `/mcp` with an API token.

Driver actions go through the bundled nefcon CLI with fixed arguments (see `INefconRunner`). Every result has `exitCode`, `outputTail` and `rebootRequired`, plus `timedOut`. Exit code `0` is success and `3010` is success that needs a restart (`rebootRequired: true`). Any other exit code returns `success: false` with error code `nefcon_failed` and the same result in `value`; exit code `1` normally means the service is not elevated or nefcon rejected an argument. A run longer than `Vestigo:Devices:NefconTimeout` is killed (`nefcon_timeout`). Calls run one at a time. A reboot is never triggered by Vestigo; apply it yourself when `rebootRequired` is true.

INF paths (`install_driver`, `uninstall_driver`, `install_filter_driver`, `uninstall_filter_driver`, `inf_default_install`, `inf_default_uninstall`) must be existing `.inf` files inside a root with `Read` access, as `name:relative\path` or an absolute path. The driver's other files are read by Windows from beside the INF, so give the root the whole driver package directory.

| Tool | Purpose |
| --- | --- |
| `create_device_node` | Create a ROOT device node (`className`, `classGuid`, `hardwareId`, optional `noDuplicates`). |
| `remove_device_node` | Remove a node by `hardwareId` and `classGuid`. |
| `install_driver` | Without `hardwareId`: install the package. With it: create the ROOT node and install the driver on it; `noDuplicates` and `removeDuplicates` (which needs `noDuplicates`) only apply with a hardware ID. |
| `uninstall_driver` | Uninstall the driver described by an INF. |
| `install_filter_driver` | Install a filter driver INF and register its class filter. |
| `uninstall_filter_driver` | Remove the class filter and service (`classGuid`, `serviceName`, `position` upper or lower). With `infPath` the driver store package is purged too; `allVersions` (needs `infPath`) purges every version. |
| `add_class_filter`, `remove_class_filter` | Edit a class's upper or lower filter list for an existing service. |
| `inf_default_install`, `inf_default_uninstall` | Run `[DefaultInstall]` or `[DefaultUninstall]` of an INF. |
| `remove_driver_store_package` | Remove driver store packages by original INF name (for example `mydriver.inf`), optionally narrowed by `classGuid` and `serviceName`. nefcon has no all-versions switch for this command, so it removes what the name and filters match. |
| `restart_device`, `enable_device`, `disable_device` | Change state by instance ID (for example `ROOT\SYSTEM\0001`). |
| `find_devices_by_interface` | Instance IDs and device paths exposing an interface GUID (at most 256; `truncated` tells if the cap was hit; `presentOnly` false includes phantom devices). |
| `find_devices_by_hardware_id` | Instance IDs in a device class whose hardware IDs match (`presentOnly`, `allowPartial`). |
| `find_devices` | Enumerate device nodes with optional AND filters `nameContains` (description substring), `hardwareId` (exact, or prefix with a trailing `*`), `deviceClass` (class name or GUID), `presentOnly` (default true) and `limit` (default 100, max 500). Returns `devices`, `total` and `truncated`. |
| `get_device` | Details of one device by `instanceId`: hardware and compatible IDs, class, service, present flag, `problemCode` (0 is healthy), `devNodeStatus` and driver key, description, provider, version, date and INF. Properties a device lacks are null. |
| `get_class_filters` | Upper and lower filter driver service names registered for a `classGuid` (registry read, no elevation needed). |
| `list_driver_store` | Driver store packages with `nameContains` (published or original INF), `provider`, `deviceClass` (name or GUID), `includeInbox` (default false) and `limit` (default 100, max 500); each has published name, original INF, provider, class, version, date, architecture and bound device instance IDs. |

Class GUIDs may be written with or without braces and must not be empty. Instance IDs must contain a `\`, and no value may be empty, start with `-`, or contain control characters. Device state tools map library failures to stable error codes: `device_not_found`, `config_manager_failed`, `win32_failed`, `windows_required` and `device_operation_failed`.

The query tools (`find_devices`, `get_device`, `get_class_filters`, `list_driver_store`) run in-process through Nefarius.Utilities.DeviceManagement 6.2.0 and never start nefcon. They are read-only and mostly work without elevation, though some device properties or driver store details may be missing for an unprivileged service account.

### Typical driver development workflows

Bring up a root-enumerated driver:

1. `create_device_node` with the class name, class GUID and a hardware ID such as `Root\MyDriver`. (`install_driver` with a `hardwareId` does this and the install in one step.)
2. `install_driver` with the INF root path and the same `hardwareId`.
3. If the driver needs a filter on a class: `install_filter_driver`, or `add_class_filter` when the service already exists.
4. `find_devices_by_hardware_id` to get the instance ID, then `restart_device` so the stack is rebuilt with the new driver or filter. Do a reboot instead when `rebootRequired` is true.

Iterate on a new build: `install_driver` again with the same hardware ID and `noDuplicates`, then `restart_device`.

Tear down: `uninstall_filter_driver` (with `infPath` to purge its package) or `remove_class_filter`, then `uninstall_driver`, `remove_device_node`, and finally `remove_driver_store_package` with the INF name so old packages do not accumulate in the driver store.
