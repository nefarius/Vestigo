---
title: Developers
description: Driver and application workflows on a Vestigo lab PC.
---

# Developer workflows

Point an MCP client at a lab PC and let the agent do the install, capture, and attach loop. Enable [Devices](features.md), [Diagnostics](features.md), [Debug output](features.md), [Processes](features.md), and [Remote debugger](features.md) on that machine, and keep a token on `/mcp`. The [security model](security.md) is the other half of this page.

## Install a driver

1. Put the driver package (INF plus the files beside it) in a named root that has `Read` access.
2. Create the ROOT node and install the driver in one of two ways: `create_device_node` with the class name, class GUID, and a hardware ID such as `Root\MyDriver`, then `install_driver` with the INF path; or one `install_driver` call with the INF path and that `hardwareId`, which creates the node and installs the driver.
3. `find_devices_by_hardware_id` to get the instance ID, then `restart_device` so the stack is rebuilt. If the result says `rebootRequired`, reboot yourself; Vestigo never triggers one.
4. Iterate by installing again with `noDuplicates`, then restarting the device. Tear down with `uninstall_driver`, `remove_device_node`, and `remove_driver_store_package` so old packages do not accumulate.

Test-signing allows test-signed drivers to load; it does not allow unsigned drivers. `get_boot_security` shows the BCD value and the running kernel state. `set_test_signing` writes the default boot entry only when `System:AllowBootConfigurationChanges` is true, and it is refused while Secure Boot is on. Restart the PC after `set_test_signing` and before installing or loading the driver.

## Inspect the binary

1. Enable Diagnostics and Jobs. Diagnostics needs Jobs. Put the file in a named root that has `Read` access.
2. `inspect_binary` reports architecture, version, and the PDB file name. `verify_file_signature` checks the embedded or catalog Authenticode signature. A valid signature is not permission to load a kernel driver. Online revocation stays off until `Diagnostics:AllowOnlineRevocation` is true.
3. When a device shows a problem code, `explain_device_problem` cites the documented meaning and the observed evidence. `get_device_install_history` lists the matching SetupAPI sections. Neither tool ranks drivers.

## Capture WPP live

1. `inspect_wpp_pdb` on the driver's PDB to get control GUIDs and flag bits.
2. `start_capture` with those PDB paths (or explicit provider GUIDs). PDB control GUIDs are enabled automatically. The call returns when the session has started, not when the trace ends.
3. Loop on `read_capture` with `waitSeconds` greater than zero. Pass the returned `NextSequence` each time. Stop when `Completed` is true.
4. `stop_capture` cancels the reader and disposes the Vestigo-owned session.

There is no server push. Vestigo decodes into a spool; it does not record a raw ETL. `convert_etl` is the offline path when you already have a file. A remote agent can resolve PDBs from an installed binary, the local symbol cache, a configured symbol server, or a file uploaded into the built-in `traces` folder.

## Read debug output

1. Enable the DebugOutput module. Only one capture can run; another DebugView instance fails the start with `dbwin_in_use`.
2. `start_debug_capture` with optional include and exclude filters for messages and process names.
3. Follow with `read_debug_output` the same way you follow an ETW capture. Debugger-attached processes send output to the debugger instead of this capture.

## Attach the remote debugger

1. Enable RemoteDebugger, Processes, and Jobs.
2. `get_remote_debugger_status` to see whether `msvsmon.exe` is already installed for Visual Studio 2022 or 2026.
3. `install_remote_debugger` downloads the Microsoft Remote Tools installer, checks its Authenticode signature, and runs it as a job.
4. Optionally `prepare_remote_debugger_computer` to add firewall rules for private networks.
5. `start_remote_debugger` (default port 4026), then attach Visual Studio to `host:4026`. `stop_remote_debugger` when you are done.

The default authentication is Windows. `none` requires an explicit acknowledgement and is insecure on any reachable network.
