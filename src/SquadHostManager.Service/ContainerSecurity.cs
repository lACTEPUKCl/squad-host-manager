namespace SquadHostManager.Service;

public static class ContainerSecurity
{
    public const string FirewallScript="""
        #!/bin/bash
        set -euo pipefail
        for spec in /opt/squad-host-manager/rcon-rules/*.rule; do
          test -f "$spec" || continue
          port=$(cat "$spec")
          [[ "$port" =~ ^[0-9]+$ ]] && (( port >= 21114 && port <= 21129 ))
          for tool in iptables ip6tables; do
            "$tool" -w -N DOCKER-USER 2>/dev/null || true
            "$tool" -w -C INPUT ! -i lo -p tcp --dport "$port" -m comment --comment shm-rcon -j DROP 2>/dev/null || "$tool" -w -I INPUT 1 ! -i lo -p tcp --dport "$port" -m comment --comment shm-rcon -j DROP
            "$tool" -w -C DOCKER-USER -p tcp --dport "$port" -m comment --comment shm-rcon -j DROP 2>/dev/null || "$tool" -w -I DOCKER-USER 1 -p tcp --dport "$port" -m comment --comment shm-rcon -j DROP
          done
        done
        """;
    public const string FirewallUnit="""
        [Unit]
        Description=Squad Host Manager private RCON ports
        Before=docker.service
        After=network-pre.target
        [Service]
        Type=oneshot
        ExecStart=/opt/squad-host-manager/rcon-firewall.sh
        RemainAfterExit=yes
        [Install]
        WantedBy=multi-user.target
        """;
    public static string RconSetup(Instance instance)
    {
        if(instance.Ports.Rcon is <21114 or >21129)throw new ArgumentException("Invalid RCON port.");
        return $$"""
            mkdir -p /opt/squad-host-manager/rcon-rules
            printf '%s\n' {{instance.Ports.Rcon}} > /opt/squad-host-manager/rcon-rules/{{instance.Id}}.rule
            printf %s {{RemoteDocker.Quote(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(FirewallScript.ReplaceLineEndings("\n"))))}} | base64 -d > /opt/squad-host-manager/rcon-firewall.sh
            chmod 700 /opt/squad-host-manager/rcon-firewall.sh
            printf %s {{RemoteDocker.Quote(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(FirewallUnit.ReplaceLineEndings("\n"))))}} | base64 -d > /etc/systemd/system/shm-rcon-firewall.service
            systemctl daemon-reload
            systemctl enable shm-rcon-firewall.service >/dev/null
            /opt/squad-host-manager/rcon-firewall.sh
            """;
    }
}
