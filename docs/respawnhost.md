# RespawnHost

Choose Cherry Servers or RespawnHost on the Hosting page. Switching requires every server on the current provider to be stopped and all queued operations to finish. The service verifies the remote state and rejects concurrent starts. It keeps separate server profiles for each provider, with shared configuration, users, notifications, backups and saved match archives. Switching does not cancel a Cherry rental; it remains billed until explicitly released.

The hosting owner connects an API key with the `servers` scope. The key stays in the coordinator data directory, with mode 0600 on Linux, and is never returned to members. Members use their normal application session and can manage only their own servers. Existing rental permissions and quotas still apply.

Select a Squad plan and region, configure the server name/password/mods in the regular server form, then confirm the order. The first plan is 8 GB. A paid POST is never automatically replayed. Unconfirmed orders block another rental and provider switching until their outcome is resolved.

The regular application pages handle configuration and admin groups, Workshop settings, players and match controls over real RCON, logs, file downloads, match history, archives, backup import/export, cloning and restoration. Common config is retained before rental. Server.cfg receives the selected server's name, password and player limit. Empty common License.cfg does not erase a provider-supplied licence. Rcon.cfg stays private to the adapter.

Workshop IDs are applied through the editable `WORKSHOP_ITEMS` startup variable, separated by commas. Changed mods require the provider's clean-install workflow. If no supported variable is exposed, the operation fails explicitly rather than reporting success. RCON uses an allocated external port configured in Rcon.cfg; console command acknowledgements are not treated as game replies. Missing connection details prevent a command from being sent.

Before stopping, logs and OSI_Scoreboards are streamed into a private local archive. An archive failure blocks the stop. Saved archives remain downloadable after the provider context closes. PAYG game-server objects remain allocated but offline; the adapter does not delete remote files to stop billing.

Manual restart/update saves local recovery data, requests reinstall with keep_files=true, subscribes to the node installation stream before the request and waits for a successful completion event, restores match files/licence and reapplies shared config/mods before starting. Crash recovery starts the existing server without requesting a reinstall. A provider that does not expose installation completion leaves the operation pending/error instead of claiming the game was updated.

Costs use observed runtime and the selected hourly tariff, prorated by seconds and marked as estimates. The account balance remains unavailable when the documented API does not supply it. Cherry hourly-sharing calculations are unchanged.

On 1 November 2026, one approved 8 GB EU server was rented and stopped within the 30-minute test window. The live check confirmed game startup, RCON command replies, the player list, current map, live admin-config reload, log downloads, memory metrics and backup export. Installing HotDrop through `WORKSHOP_ITEMS` and reinstall produced its plugin files and 11 `HD_` layers in both the game log and RCON response. The node confirmed `offline` after cleanup. Empty-server memory use was approximately 4.1 GiB; player-load testing was outside this check.

The account and node power state can remain offline during installation. Update completion therefore uses the authenticated installation WebSocket stream, subscribed before the reinstall request; an explicit successful completion is required. New allocations wait for nonempty startup settings before applying Workshop IDs. A retry retains the same remote UUID and never creates a replacement rental. Binary backup restoration, populated-player kick/ban and a real modded match were not exercised during the paid test. Automated tests use fake transports and never rent servers.

API reference: https://developers.respawnhost.com/docs and https://developers.respawnhost.com/openapi.json.
