using System.Buffers.Binary;
using System.Text;
using Renci.SshNet;

namespace SquadHostManager.Service;

public static class SshKeyMaterial
{
    // Derive from the private key every time: a neighbouring .pub file may be stale.
    public static string PublicKey(string path)
    {
        using var key = new PrivateKeyFile(path);
        return PublicKeyFromData(key.HostKeyAlgorithms.First().Data);
    }
    public static string PublicKeyFromData(byte[] data)
    {
        var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
        var type = Encoding.ASCII.GetString(data, 4, size);
        return type + " " + Convert.ToBase64String(data);
    }

    public static string Identity(string publicKey) => string.Join(' ', publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));

    public static string AuthorizeScript(string publicKey)
    {
        // Only our key is appended; existing access is retained. Serialize concurrent repairs.
        return $$"""
            set -euo pipefail
            umask 077
            mkdir -p "$HOME/.ssh"
            chmod 700 "$HOME/.ssh"
            exec 9>"$HOME/.ssh/shm-key.lock"
            flock -x 9
            touch "$HOME/.ssh/authorized_keys"
            chmod 600 "$HOME/.ssh/authorized_keys"
            key={{RemoteDocker.Quote(publicKey)}}
            if ! awk -v wanted="$key" '{if ($1 " " $2 == wanted) found=1} END {exit !found}' "$HOME/.ssh/authorized_keys"; then
              printf '\n%s\n' "$key" >> "$HOME/.ssh/authorized_keys"
            fi
            """;
    }
}
