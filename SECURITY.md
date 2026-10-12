# Security

Cherry tokens and SSH keys belong only on the coordinator. Participants authenticate using individually revocable sessions; registration requires an invite. Never distribute owner/provider credentials with the Windows executable.

Please report suspected vulnerabilities privately to the repository owner instead of publishing active credentials or host details in an issue. Include a minimal reproduction without account secrets.

## Trust boundaries

- The coordinator is privileged on a dedicated Debian game host. Do not adopt a machine containing unrelated workloads without understanding that eventual rental termination destroys the machine.
- One coordinator process and one durable database per pool. A local file lease prevents duplicate processes on the same storage. Separate replicas with separate databases are unsupported.
- The game host is reachable by SSH; the first fingerprint is pinned after discovery through authenticated Cherry API (TOFU). Subsequent key changes fail closed. A malicious first connection cannot be excluded by TOFU alone.
- RCON is reached through SSH tunnels. Restrict the public RCON TCP range to the coordinator IP in your firewall.
- HTTPS is mandatory outside localhost. Session tokens expire after seven days and are stored using Windows DPAPI in the client. WebSocket events contain only change hints, never instance passwords.
- Provider API failures, SSH failures, partial starts and uncertain deletions preserve the rental/reservations until reconciled. No automatic idle or disconnect eviction is implemented.
- Coordinator data contains passwords required for RCON and instance creation. Restrict filesystem permissions, encrypt disks/backups where appropriate and protect the administrator account.
- GitHub release checksums detect corruption; they are not a separate signing authority. Protect repository write access and use two-factor authentication. Windows builds are currently unsigned.
- Downloadable logs/archives may contain player information. Only the instance owner can access them.

## Migration

Revoke old shared Cherry tokens after the pool is configured. Existing provider tokens can bypass every application ownership rule. New keys registered in Cherry do not necessarily update `authorized_keys` on an already provisioned OS.
