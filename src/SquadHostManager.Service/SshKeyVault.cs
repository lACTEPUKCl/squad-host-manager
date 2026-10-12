namespace SquadHostManager.Service;

public sealed class SshKeyVault(Store store)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new();
    public string Snapshot(string configuredPath)
    {
        lock (Gates.GetOrAdd(store.DirectoryPath, _ => new object()))
        {
            var directory = Path.Combine(store.DirectoryPath, "ssh-keys");
            if (!File.Exists(configuredPath))
            {
                var saved = store.Load<string>("ssh-current-key");
                if (saved != null && File.Exists(Path.Combine(directory, saved)))
                {
                    var recovered = Path.Combine(directory, saved);
                    _ = SshKeyMaterial.PublicKey(recovered);
                    return recovered;
                }
                throw new InvalidOperationException("Приватный SSH-ключ отсутствует, сохранённой копии нет. Аренда заблокирована.");
            }
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Read once, validate, then preserve the exact bytes before any order is submitted.
            var bytes = File.ReadAllBytes(configuredPath);
            using var stream = new MemoryStream(bytes);
            using var key = new Renci.SshNet.PrivateKeyFile(stream);
            var name = Store.Hash(Convert.ToBase64String(key.HostKeyAlgorithms.First().Data)) + ".key";
            var target = Path.Combine(directory, name);
            if (!File.Exists(target))
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var backup = new FileStream(temporary, options)) { backup.Write(bytes); backup.Flush(true); }
                    File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            // Fail before payment if the saved key is corrupted or does not match.
            if (SshKeyMaterial.PublicKey(target) != SshKeyMaterial.PublicKeyFromData(key.HostKeyAlgorithms.First().Data))
                throw new InvalidOperationException("Сохранённая копия SSH-ключа повреждена. Аренда заблокирована.");
            store.Save("ssh-current-key", name);
            return target;
        }
    }

    public IEnumerable<string> PreviousKeys() => Directory.Exists(Path.Combine(store.DirectoryPath, "ssh-keys"))
        ? Directory.EnumerateFiles(Path.Combine(store.DirectoryPath, "ssh-keys"), "*.key").ToArray() : [];
}
