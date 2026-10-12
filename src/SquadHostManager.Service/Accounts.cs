using System.Security.Cryptography;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public record User(string Id, string Name, bool IsAdmin);
public sealed partial class Accounts(Store store)
{
    private static byte[] Derive(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    public string Invite(int uses)
    {
        if (uses is < 1 or > 1000) throw new ArgumentException("Число регистраций: 1–1000.");
        var value = Store.Secret(); using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO invites VALUES ($hash,$uses)";
        cmd.Parameters.AddWithValue("$hash", Store.Hash(value)); cmd.Parameters.AddWithValue("$uses", uses); cmd.ExecuteNonQuery(); return value;
    }
    public void Bootstrap(string password)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE admin=1";
        if ((long)cmd.ExecuteScalar()! > 0) return;
        Register(new("admin", password, ""), true);
    }
    public SessionResponse Register(RegisterRequest request, bool bootstrap = false)
    {
        var name = request.Name.Trim();
        if (name.Length is < 3 or > 40 || name.Any(char.IsControl)) throw new ArgumentException("Имя: 3–40 символов.");
        if (request.Password.Length is < 12 or > 200) throw new ArgumentException("Пароль: 12–200 символов.");
        var salt = RandomNumberGenerator.GetBytes(24); var hash = Derive(request.Password, salt); var id = Guid.NewGuid().ToString("N");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        if (!bootstrap)
        {
            using var use = db.CreateCommand(); use.Transaction = tx;
            use.CommandText = "UPDATE invites SET uses=uses-1 WHERE hash=$hash AND uses>0"; use.Parameters.AddWithValue("$hash", Store.Hash(request.Invite));
            if (use.ExecuteNonQuery() != 1) throw new ArgumentException("Приглашение недействительно или использовано.");
        }
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO users VALUES ($id,$name,$salt,$hash,$admin)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$salt", salt);
        cmd.Parameters.AddWithValue("$hash", hash); cmd.Parameters.AddWithValue("$admin", bootstrap ? 1 : 0);
        try { cmd.ExecuteNonQuery(); } catch (Microsoft.Data.Sqlite.SqliteException) { throw new ArgumentException("Имя уже занято."); }
        tx.Commit(); return CreateSession(new(id, name, bootstrap));
    }
    public SessionResponse Login(LoginRequest request)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,name,salt,hash,admin FROM users WHERE name=$name AND NOT EXISTS (SELECT 1 FROM disabled_users d WHERE d.id=users.id)";
        cmd.Parameters.AddWithValue("$name", request.Name.Trim()); using var r = cmd.ExecuteReader();
        if (!r.Read()) { _ = Derive(request.Password, new byte[24]); throw new UnauthorizedAccessException("Неверное имя или пароль."); }
        if (!CryptographicOperations.FixedTimeEquals(Derive(request.Password, (byte[])r[2]), (byte[])r[3])) throw new UnauthorizedAccessException("Неверное имя или пароль.");
        return CreateSession(new(r.GetString(0), r.GetString(1), r.GetInt32(4) == 1));
    }
    private SessionResponse CreateSession(User user)
    {
        EnsureAccess(user.Id);
        var token = Store.Secret(); using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE expires < $now; INSERT INTO sessions VALUES ($hash,$id,$expires)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$hash", Store.Hash(token));
        cmd.Parameters.AddWithValue("$id", user.Id); cmd.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddDays(7).ToString("O")); cmd.ExecuteNonQuery();
        return new(token, user.Id, user.Name, user.IsAdmin);
    }
    public User? Authenticate(string token)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT u.id,u.name,u.admin FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.hash=$hash AND s.expires>$now AND NOT EXISTS (SELECT 1 FROM disabled_users d WHERE d.id=u.id)";
        cmd.Parameters.AddWithValue("$hash", Store.Hash(token)); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); using var r = cmd.ExecuteReader();
        if(!r.Read())return null; var id=r.GetString(0);if(Policy(id).ExpiresAt is {} expires && expires<=DateTimeOffset.UtcNow)return null;return new(id,r.GetString(1),r.GetInt32(2)==1);
    }
    public void Logout(string token)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM sessions WHERE hash=$hash";
        cmd.Parameters.AddWithValue("$hash", Store.Hash(token)); cmd.ExecuteNonQuery();
    }
}
