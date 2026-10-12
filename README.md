# Squad Server Manager

A Windows app for renting a Cherry Servers host, installing Squad and managing game servers.

Use your own Cherry account or join shared hosting with an invitation. The hosting owner pays for the host. Each user manages their own game servers.

## Features

- Servers with mods and saved settings.
- Player management, administrators, permission groups and configuration files.
- Console output and RCON commands.
- Backups and automatic server/mod log archives before shutdown.
- Crash recovery and notifications in the app and Discord.
- Cherry balance and hosting cost tracking.

Available in English, Russian, German, Spanish, French, Simplified Chinese and Turkish.

## Download

Download SquadHostManager.exe from the latest GitHub release and run it. No separate .NET installation is needed.

## Join shared hosting

1. Get an invitation from the hosting owner.
2. Choose **I have an invitation** and paste the key.
3. Open **My servers** and add a server.

The key includes the connection address. You do not need a Cherry account.

## Use your own Cherry account

1. Create an API key in the Cherry Servers panel.
2. Choose **My Cherry account** and enter the key.
3. Select your project.
4. Open **Hosting**, choose an available location and confirm the rental.

Management runs on your computer. No extra VPS is needed in this mode. Keep the computer online during installation and management.

## Set up shared hosting

1. Prepare an existing VPS with Debian 12+ or Ubuntu 22.04+.
2. Open the shared-hosting setup wizard.
3. Enter the VPS address, SSH username and password or SSH key.
4. Connect your Cherry API key and finish setup.
5. Open **Users** to create users and issue invitations.

The VPS stores settings and manages hosting. Squad runs on a separately rented Cherry host. You can remove the management service from the VPS through the app settings.

## Start a server

1. Wait for the host to become ready.
2. In **My servers**, enter a name, password and player limit.
3. In **Mods**, enter the required Workshop IDs.
4. Select **Start server**. Follow the installation in **Console**.

Administrator groups and members save immediately. Selected mods and administrators become the defaults for new servers.

## Logs and archives

Download individual logs or a full archive in **Files**. The app saves server and mod logs before stopping a server. Saved archives remain available in **Tools → Match history** after the rental ends.

## Updates

The app checks for releases at startup. New versions can be downloaded and installed in the app after signature and SHA-256 verification. Game servers keep running during the app update.

Versions use Git tags vX.Y.Z. Patch notes for all interface languages are in the releases directory.

## Development

Requires Windows and .NET 10 SDK. Run dotnet test tests/SquadHostManager.Tests and dotnet build src/SquadHostManager.Desktop. Publish commands are in .github/workflows/build.yml. Service setup is documented in deploy/DEPLOYMENT.md.

Secrets, settings and archives are excluded from Git. Host capacity depends on workload. Cost tracking does not replace the Cherry invoice.
