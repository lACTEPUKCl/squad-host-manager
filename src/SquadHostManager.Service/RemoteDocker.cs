using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace SquadHostManager.Service;

public sealed partial class RemoteDocker(IConfiguration config, Store store, Func<string, CancellationToken, Task<HashSet<string>>>? serverKeys = null) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> connectionLocks = new();
    public const string Image="cm2network/squad@sha256:8cba47f53df586c2767525a9f062c2996b36f9a34a1d771a35f67a1713c46893";
    private const string Root = "/opt/squad-host-manager";
    public const string LaunchScript = """
        #!/usr/bin/env bash
        set -euo pipefail
        mkdir -p "$STEAMAPPDIR"
        shm_stage() { printf '%s\n' "$1" > "$STEAMAPPDIR/.shm-stage"; echo "SHM_STAGE $1"; }
        shm_stage gameDownloading
        # The host downloads once. A shared lock keeps copying away from updates.
        exec 8</shm-cache/cache.lock
        flock -s 8
        test -f /shm-cache/ready
        version=$(cat /shm-cache/ready)
        shm_stage gameCopying
        if [ "$(cat "$STEAMAPPDIR/.shm-version" 2>/dev/null || true)" != "$version" ]; then
          tar -C /shm-cache/game --exclude='./SquadGame/Saved' --exclude='./SquadGame/ServerConfig' --exclude='./SquadGame/Plugins/Mods' --exclude='./steamapps/workshop' -cf - . | tar -C "$STEAMAPPDIR" -xf -
          printf '%s\n' "$version" > "$STEAMAPPDIR/.shm-version"
        fi
        flock -u 8
        test -f "$STEAMAPPDIR/SquadGameServer.sh"
        mkdir -p "$STEAMAPPDIR/SquadGame/ServerConfig" "$MODPATH"
        cp /shm-config/*.cfg "$STEAMAPPDIR/SquadGame/ServerConfig/"
        find "$MODPATH" -maxdepth 1 -regextype posix-extended -regex '.*/[0-9]+' -type l -delete
        for mod in $SHM_MODS; do
          shm_stage "modsDownloading $mod"
          "$STEAMCMDDIR/steamcmd.sh" +force_install_dir "$STEAMAPPDIR" +login anonymous +workshop_download_item "$WORKSHOPID" "$mod" +quit
          target="$STEAMAPPDIR/steamapps/workshop/content/$WORKSHOPID/$mod"
          if [ ! -d "$target" ]; then
            source="$STEAMCMDDIR/steamapps/workshop/content/$WORKSHOPID/$mod"
            [ -d "$source" ] || source="$HOME/Steam/steamapps/workshop/content/$WORKSHOPID/$mod"
            test -d "$source"
            mkdir -p "$(dirname "$target")"
            cp -a "$source" "$target"
          fi
          test -n "$(find "$target" -name '*.uplugin' -print -quit)"
          ln -sfn "$target" "$MODPATH/$mod"
        done
        mkdir -p "$HOME/.steam/sdk64"
        ln -sfn "$STEAMCMDDIR/linux64/steamclient.so" "$HOME/.steam/sdk64/steamclient.so"
        shm_stage gameStarting
        start_layer=$(awk '$1 !~ /^\/\// && NF {print $1;exit}' "$STEAMAPPDIR/SquadGame/ServerConfig/LayerRotation.cfg")
        test -n "$start_layer"
        exec bash "$STEAMAPPDIR/SquadGameServer.sh" "$start_layer" Port="$PORT" QueryPort="$QUERYPORT" RCONPORT="$RCONPORT" beaconport="$BEACONPORT" FIXEDMAXPLAYERS="$FIXEDMAXPLAYERS" FIXEDMAXTICKRATE=64 RANDOM=NONE -useperfthreads
        """;
    public const string CacheScript = """
        #!/usr/bin/env bash
        set -euo pipefail
        exec 8>/shm-cache/cache.lock
        flock -x 8
        rm -f /shm-cache/ready
        mkdir -p /shm-cache/game
        completed=false
        for attempt in 1 2 3; do
          if "$STEAMCMDDIR/steamcmd.sh" +force_install_dir /shm-cache/game +login anonymous +app_info_update 1 +app_update "$STEAMAPPID" +quit; then completed=true;break;fi
          echo "Steam download failed; attempt $attempt of 3" >&2
          sleep 10
        done
        $completed
        test -s /shm-cache/game/SquadGameServer.sh
        test -s /shm-cache/game/steamapps/appmanifest_403240.acf
        sha256sum /shm-cache/game/steamapps/appmanifest_403240.acf | cut -d ' ' -f 1 > /shm-cache/ready.tmp
        mv /shm-cache/ready.tmp /shm-cache/ready
        """;
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Container(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new ArgumentException("Неверный ID инстанса.");
        return "shm_" + id;
    }
    private SshClient ConnectWithKey(string hostId, string ip, string keyPath)
    {
        using var key = new PrivateKeyFile(keyPath);
        var connection = new Renci.SshNet.ConnectionInfo(ip, config.GetValue<int?>("Ssh:Port") ?? 22, config["Ssh:User"] ?? "root", new PrivateKeyAuthenticationMethod(config["Ssh:User"] ?? "root", key)) { Timeout = TimeSpan.FromSeconds(20) };
        var ssh = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        // TOFU only on a Cherry-selected host; persist and reject subsequent changes.
        var pinId = "ssh-pin-" + hostId; var pin = store.Load<string>(pinId);
        string? accepted = null;
        var changed = false;
        ssh.HostKeyReceived += (_, args) =>
        {
            var received = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(args.HostKey));
            args.CanTrust = pin == null || pin == received;
            changed = !args.CanTrust;
            if (args.CanTrust) accepted = received;
        };
        try
        {
            ssh.Connect();
            // Do not pin a server that rejected authentication.
            if (pin == null && accepted != null) store.Save(pinId, accepted);
            return ssh;
        }
        catch
        {
            ssh.Dispose();
            if (changed) throw new InvalidOperationException("Изменился отпечаток SSH-сервера. Доступ заблокирован: владелец должен проверить хост.");
            throw;
        }
    }
    private SshClient Connect(string hostId, string ip, HashSet<string> attachedKeys)
    {
        lock (connectionLocks.GetOrAdd(hostId, _ => new object()))
        {
            var vault = new SshKeyVault(store);
            var primary = vault.Snapshot(config["Ssh:KeyPath"] ?? throw new InvalidOperationException("SSH ключ не настроен."));
            var currentPublic = SshKeyMaterial.PublicKey(primary);
            try { return ConnectWithKey(hostId, ip, primary); }
            catch (Exception e) when (AuthenticationRejected(e)) { }

            var backups = (config.GetSection("Ssh:FallbackKeyPaths").Get<string[]>() ?? []).Concat(vault.PreviousKeys());
            foreach (var candidate in backups.Distinct(StringComparer.Ordinal).Where(p => p != primary)
                .OrderByDescending(p => attachedKeys.Contains(SshKeyMaterial.PublicKey(p))))
            {
                SshClient oldAccess;
                try { oldAccess = ConnectWithKey(hostId, ip, candidate); }
                catch (Exception e) when (AuthenticationRejected(e)) { continue; }
                using (oldAccess) Run(oldAccess, SshKeyMaterial.AuthorizeScript(currentPublic));
                // Success means the CURRENT private key actually authenticates, not just API metadata.
                return ConnectWithKey(hostId, ip, primary);
            }
            throw new InvalidOperationException("Хост отклонил актуальный и запасные SSH-ключи. Добавьте публичный ключ координатора в authorized_keys через рабочий доступ или консоль Cherry. Приватный ключ нельзя получить из Cherry API.");
        }
    }
    private static bool AuthenticationRejected(Exception error) => error is Renci.SshNet.Common.SshAuthenticationException
        or Renci.SshNet.Common.SshConnectionException { DisconnectReason: Renci.SshNet.Messages.Transport.DisconnectReason.NoMoreAuthenticationMethodsAvailable };
    private static string Run(SshClient ssh, string script, TimeSpan? timeout = null)
    {
        script = script.ReplaceLineEndings("\n");
        // Commands are base64 transported; user data never becomes shell syntax.
        using var command = ssh.CreateCommand("printf %s " + Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(script))) + " | base64 -d | bash");
        command.CommandTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var output = command.Execute();
        if(command.ExitStatus!=0)
        {
            var detail=(command.Error+"\n"+output).Trim();
            if(detail.Length>4000)detail=detail[^4000..];
            throw new InvalidOperationException($"Ошибка операции на игровом хосте (код {command.ExitStatus}): "+detail);
        }
        return output;
    }
    private async Task<T> With<T>(string hostId, string ip, Func<SshClient, T> action, CancellationToken ct)
    {
        var attached = serverKeys == null ? new HashSet<string>() : await serverKeys(hostId, ct);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); using var ssh = Connect(hostId, ip, attached); var result = action(ssh); ct.ThrowIfCancellationRequested(); return result;
        }, ct);
    }
    public async Task Prepare(string hostId, string ip, CancellationToken ct, Action<string>? progress = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (true)
        {
            try
            {
                progress?.Invoke("sshConnecting");
                await With(hostId, ip, ssh =>
                {
                    progress?.Invoke("sshConnected"); progress?.Invoke("dockerPreparing");
                    Run(ssh, """
                    set -euo pipefail
                    mkdir -p /opt/squad-host-manager
                    exec 9>/opt/squad-host-manager/pool.lock
                    flock -x 9
                    if ! command -v docker >/dev/null; then
                      apt-get update -qq
                      apt-get install -y docker.io curl ca-certificates
                    fi
                    if ! command -v iptables >/dev/null || ! command -v ip6tables >/dev/null; then apt-get update -qq; apt-get install -y iptables; fi
                    if ! command -v python3 >/dev/null; then apt-get update -qq; apt-get install -y python3; fi
                    systemctl enable --now docker
                    command -v ss >/dev/null
                    docker info >/dev/null
                    """, TimeSpan.FromMinutes(15));
                    progress?.Invoke("imageDownloading");
                    Run(ssh, $$"""
                    set -euo pipefail
                    exec 9>/opt/squad-host-manager/pool.lock
                    flock -x 9
                    if ! docker image inspect {{Image}} >/dev/null 2>&1; then
                      docker pull {{Image}}
                    fi
                    """, TimeSpan.FromMinutes(15));
                    progress?.Invoke("gameCacheDownloading");
                    Run(ssh, $$"""
                    set -euo pipefail
                    mkdir -p {{Root}}/cache
                    touch {{Root}}/cache/cache.lock
                    chown 1000:1000 {{Root}}/cache {{Root}}/cache/cache.lock
                    printf %s {{Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(CacheScript.ReplaceLineEndings("\n"))))}} | base64 -d > {{Root}}/cache-download.sh
                    docker run --rm --user 1000:1000 --cap-drop ALL --security-opt no-new-privileges --pids-limit 256 --entrypoint /bin/bash -v {{Root}}/cache:/shm-cache -v {{Root}}/cache-download.sh:/shm-cache-download.sh:ro {{Image}} /shm-cache-download.sh > {{Root}}/cache-download.log 2>&1 || { tail -c 1500 {{Root}}/cache-download.log >&2; exit 1; }
                    """, TimeSpan.FromMinutes(60));
                    return true;
                }, ct); return;
            }
            catch (Renci.SshNet.Common.SshConnectionException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(5000, ct); }
            catch (System.Net.Sockets.SocketException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(5000, ct); }
        }
    }
    public Task<GameStatus?> PreparationStatus(string hostId,string ip,CancellationToken ct) => With<GameStatus?>(hostId,ip,ssh=>
    {
        var logs=Run(ssh,"tail -c 12000 /opt/squad-host-manager/cache-download.log 2>/dev/null || true");
        var matches=Regex.Matches(logs,@"Update state[^\r\n]*progress:\s*([\d.]+)");
        if(matches.Count==0)return null;
        var last=matches[^1];
        var value=double.Parse(last.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
        return new(last.Value.Contains("verifying",StringComparison.OrdinalIgnoreCase)?"gameCacheVerifying":"gameCacheDownloading",Math.Clamp(value,0,100));
    },ct);
    public static List<string> ParseCpuSets(string topology)
    {
        var rows=topology.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(l=>!l.StartsWith('#')).Select(l=>l.Trim().Split(','));
        return rows.Where(r=>r.Length==4 && r[3]=="Y").GroupBy(r=>r[1]+":"+r[2]).Select(g=>g.Select(r=>int.Parse(r[0])).Order().ToArray()).Where(g=>g.Length==2).OrderBy(g=>g[0]).Skip(1).Take(16).Select(g=>string.Join(',',g)).ToList();
    }
    public Task<List<string>> CpuSets(string hostId,string ip,CancellationToken ct) => With(hostId,ip,ssh=>
    {
        var sets=ParseCpuSets(Run(ssh,"lscpu -p=CPU,CORE,SOCKET,ONLINE"));
        if(sets.Count==0)throw new InvalidOperationException("Нет свободных физических ядер с двумя потоками.");
        return sets;
    },ct);
    public Task<List<RemoteInstance>> Inventory(string hostId, string ip, CancellationToken ct) => With(hostId, ip, ssh =>
    {
        var output = Run(ssh, "set -euo pipefail\nif ! command -v docker >/dev/null; then echo '[]'; exit 0; fi\nids=$(docker ps -aq)\nif [ -n \"$ids\" ]; then docker inspect $ids; else echo '[]'; fi");
        var data = JsonSerializer.Deserialize<JsonElement>(output); var result = new List<RemoteInstance>();
        foreach (var container in data.EnumerateArray())
        {
            var name = container.GetProperty("Name").GetString()!.TrimStart('/');
            var running = container.GetProperty("State").GetProperty("Running").GetBoolean() && !container.GetProperty("State").GetProperty("Restarting").GetBoolean();
            var ports = new HashSet<int>();
            foreach (var env in container.GetProperty("Config").GetProperty("Env").EnumerateArray())
            {
                var pair = env.GetString()!.Split('=', 2);
                if (pair.Length == 2 && pair[0] is "PORT" or "QUERYPORT" or "RCONPORT" or "BEACONPORT" && int.TryParse(pair[1], out var port))
                { ports.Add(port); if (pair[0] == "PORT") ports.Add(port + 1); }
            }
            if (container.GetProperty("HostConfig").TryGetProperty("PortBindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
                foreach (var binding in bindings.EnumerateObject())
                    if (binding.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in binding.Value.EnumerateArray()) if (int.TryParse(item.GetProperty("HostPort").GetString(), out var port)) ports.Add(port);
            result.Add(new(name.StartsWith("shm_", StringComparison.Ordinal) ? name[4..] : "external:" + name, running, ports.ToArray()));
        }
        // A clean adopted Debian host can have no Docker yet. Native/orphaned
        // Squad processes still prevent terminating its rental after a stop.
        var native = Run(ssh, "set -euo pipefail\nif pgrep -f '[S]quadGameServer' >/dev/null; then echo yes; else echo no; fi").Trim() == "yes";
        if (native && !result.Any(i => i.Running)) result.Add(new("external:native-squad", true, []));
        return result;
    }, ct);
    public Task<HashSet<int>> BoundPorts(string hostId, string ip, CancellationToken ct) => With(hostId, ip, ssh =>
    {
        var output = Run(ssh, "set -euo pipefail\nss -H -lntu | awk '{print $5}'");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line[(line.LastIndexOf(':') + 1)..]).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToHashSet();
    }, ct);
    public static string BuildStartScript(Instance instance)
    {
        if(instance.CpuSet==null || !Regex.IsMatch(instance.CpuSet,@"^\d+,\d+$"))throw new ArgumentException("Invalid CPU pair.");
        var name = Container(instance.Id); var dir = Root + "/instances/" + instance.Id;
        var cfg = $"ServerName={instance.Name}\nServerPassword={instance.Password}\nMaxPlayers={instance.MaxPlayers}\nShouldAdvertise=true\nIsLANMatch=false\nMapRotationMode=LayerList\nRecordDemos=false\n";
        var rcon = $"Port={instance.Ports.Rcon}\nPassword={instance.RconPassword}\n";
        var body = $$"""
            set -euo pipefail
            exec 9>{{Root}}/pool.lock
            flock -x 9
            if [ "$(docker inspect -f {{Quote("{{.State.Running}}")}} {{name}} 2>/dev/null || true)" = true ]; then exit 0; fi
            docker rm {{name}} >/dev/null 2>&1 || true
            bound=$(ss -H -lntu | awk '{print $5}' | sed 's/.*://')
            for p in {{string.Join(' ', instance.Ports.All)}}; do
              if printf '%s\n' "$bound" | grep -qx "$p"; then echo "Port $p already in use" >&2; exit 1; fi
            done
            dir={{Quote(dir)}}
            mkdir -p "$dir/data/SquadGame/ServerConfig" "$dir/config"
            printf %s {{Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg)))}} | base64 -d > "$dir/config/Server.cfg"
            printf %s {{Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(rcon)))}} | base64 -d > "$dir/config/Rcon.cfg"
            selected_layer={{Quote(instance.StartLayer)}}
            if [ -n "$selected_layer" ] && [ "$(cat "$dir/.shm-start-layer" 2>/dev/null || true)" != "$selected_layer" ]; then
              printf '%s\n' "$selected_layer" > "$dir/config/LayerRotation.cfg"
              printf '%s\n' "$selected_layer" > "$dir/.shm-start-layer"
            fi
            if [ ! -f "$dir/config/LayerRotation.cfg" ]; then printf '%s\n' 'AlBasrah_AAS_v1' > "$dir/config/LayerRotation.cfg"; fi
            printf %s {{Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(LaunchScript.ReplaceLineEndings("\n"))))}} | base64 -d > "$dir/launch.sh"
            chown -R 1000:1000 "$dir/data" "$dir/config"
            chmod 755 "$dir/launch.sh"
            {{ContainerSecurity.RconSetup(instance)}}
            if ! docker network inspect {{name}}_net >/dev/null 2>&1; then
              docker network create --driver bridge --opt com.docker.network.bridge.enable_icc=false --label shm.instance={{instance.Id}} {{name}}_net >/dev/null
            fi
            test "$(docker network inspect -f {{Quote("{{index .Labels \"shm.instance\"}}")}} {{name}}_net)" = {{instance.Id}}
            if [ ! -f "$dir/home/.initialized" ]; then
              mkdir -p "$dir/home"
              chown 1000:1000 "$dir/home"
              docker run --rm --network none --user 1000:1000 --cap-drop ALL --security-opt no-new-privileges --read-only --entrypoint /bin/bash -v "$dir/home:/shm-home" {{Image}} -c 'cp -a /home/steam/. /shm-home/; touch /shm-home/.initialized'
            fi
            docker run -d --name {{name}} --network {{name}}_net --restart no
              --user 1000:1000 --cap-drop ALL --security-opt no-new-privileges --read-only --pids-limit 512 --memory 12g --memory-swap 12g
              --tmpfs /tmp:rw,nosuid,nodev,size=512m,uid=1000,gid=1000
              -p {{instance.Ports.Game}}:{{instance.Ports.Game}}/udp -p {{instance.Ports.Game+1}}:{{instance.Ports.Game+1}}/udp
              -p {{instance.Ports.Query}}:{{instance.Ports.Query}}/udp -p {{instance.Ports.Beacon}}:{{instance.Ports.Beacon}}/udp
              -p 127.0.0.1:{{instance.Ports.Rcon}}:{{instance.Ports.Rcon}}/tcp --cpuset-cpus {{Quote(instance.CpuSet ?? throw new InvalidOperationException("CPU topology must be assigned before launch."))}} --entrypoint /bin/bash
              --label shm.owner={{instance.OwnerId}} --label shm.instance={{instance.Id}}
              --ulimit core=0:0 --ulimit nofile=65535:65535 -v "$dir/home:/home/steam" -v "$dir/data:/home/steam/squad-dedicated"
              -v "{{Root}}/cache:/shm-cache:ro" -v "$dir/config:/shm-config:ro" -v "$dir/launch.sh:/shm-launch.sh:ro"
              -e PORT={{instance.Ports.Game}} -e QUERYPORT={{instance.Ports.Query}} -e RCONPORT={{instance.Ports.Rcon}}
              -e BEACONPORT={{instance.Ports.Beacon}} -e FIXEDMAXPLAYERS={{instance.MaxPlayers}}
              -e RANDOM=NONE -e SHM_MODS={{Quote(instance.Mods)}}
              {{Image}} /shm-launch.sh >/dev/null
            """;
        // Join docker arguments into one command; no shell expansion of untrusted fields.
        body = body.ReplaceLineEndings("\n").Replace("\n  --", " --", StringComparison.Ordinal).Replace("\n  -p", " -p", StringComparison.Ordinal).Replace("\n  -e", " -e", StringComparison.Ordinal).Replace("\n  -v", " -v", StringComparison.Ordinal).Replace("\n  cm2network", " cm2network", StringComparison.Ordinal);
        return body;
    }
    public async Task Start(string hostId, string ip, Instance instance, CancellationToken ct) { await ForgetRcon(instance.Id, ct); await With(hostId, ip, ssh => Run(ssh, BuildStartScript(instance)), ct); }
    public async Task Stop(string hostId, string ip, string instanceId, CancellationToken ct) { await ForgetRcon(instanceId, ct); await With(hostId, ip, ssh => Run(ssh, $$"""
        set -euo pipefail
        exec 9>{{Root}}/pool.lock
        flock -x 9
        name={{Container(instanceId)}}
        if docker inspect "$name" >/dev/null 2>&1; then
          docker stop --time 45 "$name" >/dev/null
          docker rm "$name" >/dev/null
        fi
        docker network rm "${name}_net" >/dev/null 2>&1 || true
        """), ct); }
    public Task<string> Logs(string hostId, string ip, string instanceId, CancellationToken ct) => With(hostId, ip,
        ssh => Run(ssh, $"docker logs --tail 200 {Container(instanceId)} 2>&1"), ct);
    public async Task<GameStatus?> Probe(string hostId, string ip, Instance instance, CancellationToken ct)
    {
        var logs=await Logs(hostId,ip,instance.Id,ct);
        var stage=await With(hostId,ip,ssh=>Run(ssh,ScopedFiles.Command(instance.Id,"stage")).Trim(),ct);
        var status=ParseStatus(logs,stage);
        if(status.State is "gameDownloading" or "gameCopying" or "modsDownloading" or "gameFailed")return status;
        try
        {
            var response=await Command(hostId,ip,instance,"ListPlayers",ct);
            if(response.Contains("Active Players",StringComparison.Ordinal))return new("gameReady");
        }
        catch(Exception) when(!ct.IsCancellationRequested) { }
        return status;
    }
    public static GameStatus ParseStatus(string logs, string? persistedStage = null)
    {
        var attempt=logs.LastIndexOf("SHM_STAGE gameDownloading",StringComparison.Ordinal);if(attempt>=0)logs=logs[attempt..];
        if(logs.Contains("Critical error",StringComparison.OrdinalIgnoreCase)||logs.Contains("Segmentation fault",StringComparison.OrdinalIgnoreCase)||logs.Contains("Invalid world at start",StringComparison.OrdinalIgnoreCase))return new("gameFailed");
        var progressKey=persistedStage;persistedStage=persistedStage?.Split(' ')[0];
        var stage = logs.Contains("LogSquad:", StringComparison.Ordinal) || logs.Contains("LogInit:", StringComparison.Ordinal) ? "gameStarting" : "gameDownloading";
        if (persistedStage is "gameDownloading" or "modsDownloading" or "gameStarting" or "gameCopying") stage = persistedStage;
        var progressStart = 0;
        foreach (Match marker in Regex.Matches(logs, @"SHM_STAGE (gameDownloading|modsDownloading|gameStarting|gameCopying)[^\r\n]*"))
        { stage = marker.Groups[1].Value; progressKey=marker.Value[10..]; progressStart = marker.Index + marker.Length; }
        if (persistedStage is "gameDownloading" or "modsDownloading" or "gameStarting" or "gameCopying" && stage != persistedStage) return new(persistedStage);
        if (stage == "gameStarting") return new(stage);
        var matches = Regex.Matches(logs[progressStart..], @"progress:\s*([\d.]+)");
        if (matches.Count > 0 && double.TryParse(matches[^1].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var progress))
            return new(stage, Math.Clamp(progress, 0, 100),progressKey);
        return new(stage,null,progressKey);
    }
    private sealed class RconSession(SshClient ssh, ForwardedPortLocal tunnel, string identity) : IDisposable
    {
        public SshClient Ssh { get; } = ssh;
        public ForwardedPortLocal Tunnel { get; } = tunnel;
        public string Identity { get; } = identity;
        public RconConnection Client { get; } = new();
        public void Dispose() { Client.Dispose(); Tunnel.Dispose(); Ssh.Dispose(); }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RconSession> rconSessions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> rconGates = new();
    public void Dispose() { foreach (var session in rconSessions.Values) session.Dispose(); rconSessions.Clear(); }
    private async Task ForgetRcon(string instanceId, CancellationToken ct)
    {
        var gate = rconGates.GetOrAdd(instanceId, _ => new(1)); await gate.WaitAsync(ct);
        try { if (rconSessions.TryRemove(instanceId, out var session)) session.Dispose(); }
        finally { gate.Release(); }
    }
    public async Task<string> Command(string hostId, string ip, Instance instance, string command, CancellationToken ct)
    {
        var gate = rconGates.GetOrAdd(instance.Id, _ => new(1)); await gate.WaitAsync(ct);
        try
        {
            var keyPath = config["Ssh:KeyPath"] ?? throw new InvalidOperationException("SSH ключ не настроен.");
            var keyIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(keyPath)));
            var identity = hostId + ":" + ip + ":" + instance.Ports.Rcon + ":" + instance.RconPassword + ":" + keyIdentity;
            if (rconSessions.TryGetValue(instance.Id, out var existing) && (!existing.Ssh.IsConnected || existing.Identity != identity))
            { rconSessions.TryRemove(instance.Id, out _); existing.Dispose(); }
            if (!rconSessions.TryGetValue(instance.Id, out var session))
            {
                var attached = serverKeys == null ? new HashSet<string>() : await serverKeys(hostId, ct);
                var ssh = await Task.Run(() => Connect(hostId, ip, attached), ct);
                var tunnel = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)instance.Ports.Rcon);
                try { ssh.AddForwardedPort(tunnel); tunnel.Start(); session = new(ssh, tunnel, identity); rconSessions[instance.Id] = session; }
                catch { tunnel.Dispose(); ssh.Dispose(); throw; }
            }
            // Never replay a command after uncertain delivery. The next health check can reconnect.
            try { return await session.Client.Execute("127.0.0.1", (int)session.Tunnel.BoundPort, instance.RconPassword, command, ct); }
            catch { rconSessions.TryRemove(instance.Id, out _); session.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public static readonly string[] ConfigFiles = ["Admins.cfg", "Bans.cfg", "LayerRotation.cfg", "LevelRotation.cfg", "ExcludedFactions.cfg", "ExcludedLayers.cfg", "ExcludedLevels.cfg", "LayerVoting.cfg", "LayerVotingLowPlayers.cfg", "LayerVotingNight.cfg", "VoteConfig.cfg", "MOTD.cfg", "ServerMessages.cfg", "RemoteAdminListHosts.cfg", "RemoteBanListHosts.cfg", "CustomOptions.cfg", "License.cfg"];
    public Task<string> Config(string hostId, string ip, string instanceId, SquadHostManager.Core.ConfigRequest request, CancellationToken ct)
    {
        _ = Container(instanceId);
        if (!ConfigFiles.Contains(request.File, StringComparer.Ordinal)) throw new ArgumentException("Этот конфиг недоступен. Системные порты и RCON защищены.");
        if (request.Content?.Length > 20_000 || request.Content?.Contains('\0') == true) throw new ArgumentException("Конфиг должен быть текстовым, не более 20000 символов.");
        return With(hostId,ip,ssh=>Run(ssh,ScopedFiles.Command(instanceId,request.Content==null?"read":"write",request.File,request.Content)),ct);
    }
    public Task<Stream> ArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct)=>ArchiveStream(hostId,ip,instanceId,false,ct);
    public Task<Stream> BackupArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct)=>ArchiveStream(hostId,ip,instanceId,true,ct);
    private Task<Stream> ArchiveStream(string hostId,string ip,string instanceId,bool backup,CancellationToken ct) => With<Stream>(hostId,ip,ssh=>
    {
        _=Container(instanceId);var saved=Root+"/instances/"+instanceId+"/data/SquadGame/Saved";
        var archive=Run(ssh,ScopedFiles.Command(instanceId,"archive",backup?"backup":""),TimeSpan.FromMinutes(15)).Trim();
        if(!Regex.IsMatch(archive,@"^/tmp/shm-archive\.[A-Za-z0-9_]+$"))throw new IOException("Неверный путь архива.");
        FileStream? file=null;
        try
        {
            using var key=new PrivateKeyFile(config["Ssh:KeyPath"]!);
            var user=config["Ssh:User"]??"root";
            using var sftp=new SftpClient(new Renci.SshNet.ConnectionInfo(ip,config.GetValue<int?>("Ssh:Port")??22,user,new PrivateKeyAuthenticationMethod(user,key)));
            var pin=store.Load<string>("ssh-pin-"+hostId)??throw new IOException("SSH fingerprint missing.");
            sftp.HostKeyReceived+=(_,e)=>e.CanTrust=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(e.HostKey))==pin;
            sftp.Connect();var spool=Path.Combine(store.DirectoryPath,"archives");Directory.CreateDirectory(spool);file=new FileStream(Path.Combine(spool,"shm-archive-"+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.DeleteOnClose|FileOptions.SequentialScan);
            sftp.DownloadFile(archive,file);ct.ThrowIfCancellationRequested();file.Position=0;return file;
        }
        catch{file?.Dispose();throw;}
        finally{Run(ssh,"rm -f -- "+Quote(archive));}
    },ct);
    public async Task<byte[]> Archive(string hostId,string ip,string instanceId,CancellationToken ct)
    {
        await using var stream=await ArchiveStream(hostId,ip,instanceId,ct);
        if(stream.Length>67108864)throw new InvalidOperationException("Архив превышает 64 MiB. Используйте потоковую загрузку.");
        using var memory=new MemoryStream();await stream.CopyToAsync(memory,ct);return memory.ToArray();
    }
    public static void ValidateFilePath(string path)
    {
        if (path.Length > 512 || path.Any(char.IsControl) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..")
            || !new[] { "Logs/", "OSI_Scoreboards/" }.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            throw new ArgumentException("Доступны только файлы логов и счёта из мода своего сервера.");
    }
    public Task<List<SquadHostManager.Core.ServerFile>> Files(string hostId, string ip, string instanceId, CancellationToken ct) => With(hostId, ip, ssh =>
    {
        _ = Container(instanceId);
        var output=Run(ssh,ScopedFiles.Command(instanceId,"files"),TimeSpan.FromSeconds(30));
        return JsonSerializer.Deserialize<List<SquadHostManager.Core.ServerFile>>(output)??[];
    },ct);
    public Task<byte[]> Download(string hostId, string ip, string instanceId, string path, CancellationToken ct)
    {
        _ = Container(instanceId); ValidateFilePath(path);
        return With(hostId, ip, ssh =>
        {
            var data=Run(ssh,ScopedFiles.Command(instanceId,"download",path),TimeSpan.FromMinutes(3));
            var result = Convert.FromBase64String(data);
            if (result.Length > 67108864) throw new InvalidOperationException("Файл превышает 64 MiB.");
            return result;
        }, ct);
    }
}
