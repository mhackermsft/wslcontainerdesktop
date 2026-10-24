# Compose interoperability contract

Compatible frontends manage the same WSLc Compose projects.
They must use these Docker Compose-compatible resolved values before either tool
creates or reconciles a resource.

## Identity

| Resource | Resolved value |
| --- | --- |
| project | top-level `name`, otherwise the Compose file's parent directory name |
| container | `<project>-<service>-<index>`; indexes start at `1` |
| network | `<project>_<network-key>`, unless `name:` or `external: true` supplies the name |
| volume | `<project>_<volume-key>`, unless `name:` or `external: true` supplies the name |
| built image | `<project>-<service>` unless `image:` supplies the name |

`container_name` keeps its explicit value and cannot be scaled, matching Compose.

## Labels

The shared namespace is `com.wslc-compose.*`. Values use the resolved project
identity above. Network and volume labels retain the YAML declaration key, rather
than their resolved runtime name.

```text
com.wslc-compose.project
com.wslc-compose.service
com.wslc-compose.container-number
com.wslc-compose.config-hash
com.wslc-compose.project.working_dir
com.wslc-compose.project.config_files
com.wslc-compose.project.environment_file
com.wslc-compose.network
com.wslc-compose.volume
```

`managed-by` and `created-by` are diagnostic provenance only. They must not block
either tool from reconciling a project carrying the shared labels.

## Reconciliation

Both tools must resolve Compose input, naming, scale and resource ownership using
this contract. A versioned canonical configuration fingerprint is required before
either tool can decide that an existing container is up to date. Until a common
fingerprint is implemented, a differing hash requires safe recreation rather than
ownership rejection.

Concurrent lifecycle operations for the same project are unsupported; each tool
must refresh the runtime inventory after another tool may have changed it.

## Configuration fingerprint

`com.wslc-compose.config-hash` is `v1:` followed by the lowercase SHA-256 of a
UTF-8, compact JSON object. Object keys are ordinal-sorted, arrays that represent
sets are ordinal-sorted, and omitted fields mean their Compose defaults. The object
contains the resolved runtime inputs common to both frontends:

```json
{
  "build": null,
  "command": [],
  "entrypoint": [],
  "environment": {},
  "image": "example:latest",
  "labels": {},
  "mounts": [],
  "networks": [],
  "ports": [],
  "service": "web",
  "hostname": "",
  "domainname": "",
  "user": "",
  "working_dir": "",
  "dns": [],
  "dns_search": [],
  "dns_opt": [],
  "tmpfs": [],
  "ulimits": [],
  "mem_limit": "",
  "cpus": "",
  "shm_size": "",
  "stop_signal": ""
}
```

`build` is a boolean. Build context changes require an explicit build and are not
a recursive source-content fingerprint. Mounts use the engine argument form,
such as `source:/target:ro`. Both tools remove duplicate set entries before
sorting them.

The hash excludes project paths, resolved container names, resource metadata,
runtime-generated labels, lifecycle bookkeeping, and image IDs. A hash from an
unknown version is not trusted as equal; it requires a safe recreation.

## Fingerprint v2 roadmap

`com.wslc-compose.config-hash-v2` is published alongside the stable `v1`
label. Consumers that understand it prefer `v2`; otherwise they compare `v1`.
Unknown versions are never assumed equal.

Its intended supported subset includes health checks, GPU all, stdin/TTY, stop
timeouts, typed mounts, network mode, build details, file-backed secrets and
configs, and declarations for used networks and named volumes. File resources
contribute only source name, target, and SHA-256 content digest, never their
content or local paths.

`v2` deliberately excludes fields that cannot yet be applied equivalently:

```text
restart policies (depends on persistent application-owned supervision)
per-network IPv4 addresses (current WSLc endpoint limitation)
external and inline secrets/configs (outside the portable materialization contract)
build context paths (not portable across machines)
```

Each candidate field requires matching models, application, golden vectors and
runtime interoperability. `v1` remains available for older consumers during
the compatibility window.
