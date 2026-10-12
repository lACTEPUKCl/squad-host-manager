using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class Accounts
{
    private static void Owner(User user) { if (!user.IsAdmin) throw new UnauthorizedAccessException("Только владелец управляет пользователями."); }
    public List<UserAccessView> Users(User owner)
    {
        Owner(owner); using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT u.id,u.name,u.admin,k.hash IS NOT NULL,d.id IS NOT NULL FROM users u LEFT JOIN access_keys k ON k.user_id=u.id LEFT JOIN disabled_users d ON d.id=u.id ORDER BY u.admin DESC,u.name";
        using var r = cmd.ExecuteReader(); var result = new List<UserAccessView>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3) == 1, r.GetInt32(4) == 1));
        return result.Select(u=>u with{ExpiresAt=Policy(u.Id).ExpiresAt,MaxServers=Policy(u.Id).MaxServers,CanRent=u.IsAdmin || Policy(u.Id).CanRent}).ToList();
    }
    public AccessGrant CreateUser(User owner, string name)
    {
        Owner(owner); name = name.Trim();
        if (name.Length is < 3 or > 40 || name.Any(char.IsControl)) throw new ArgumentException("Имя: 3–40 символов.");
        var id = Guid.NewGuid().ToString("N"); var key = Store.Secret();
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO users VALUES ($id,$name,$salt,$hash,0); INSERT INTO access_keys VALUES ($id,$key,$now)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$salt", RandomNumberGenerator.GetBytes(24)); cmd.Parameters.AddWithValue("$hash", RandomNumberGenerator.GetBytes(32)); cmd.Parameters.AddWithValue("$key", Store.Hash(key)); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        try { cmd.ExecuteNonQuery(); } catch (SqliteException) { throw new ArgumentException("Имя уже занято."); }
        tx.Commit(); return new(new(id, name, false, true, false), key);
    }
    public AccessGrant RotateKey(User owner, string id)
    {
        Owner(owner); if(Policy(id).ExpiresAt is {} expiry && expiry<=DateTimeOffset.UtcNow)throw new InvalidOperationException("Сначала продлите срок доступа пользователя."); var key = Store.Secret(); using var db = store.Open(); using var tx = db.BeginTransaction();
        using var find = db.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT name FROM users WHERE id=$id AND admin=0"; find.Parameters.AddWithValue("$id", id);
        var name = find.ExecuteScalar() as string ?? throw new ArgumentException("Выберите участника.");
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sessions WHERE user_id=$id; DELETE FROM disabled_users WHERE id=$id; INSERT INTO access_keys VALUES ($id,$hash,$now) ON CONFLICT(user_id) DO UPDATE SET hash=excluded.hash,created=excluded.created";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$hash", Store.Hash(key)); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
        return new(new(id, name, false, true, false), key);
    }
    public void RevokeUser(User owner, string id)
    {
        Owner(owner); using var db = store.Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO disabled_users SELECT id FROM users WHERE id=$id AND admin=0; DELETE FROM sessions WHERE user_id IN (SELECT id FROM disabled_users WHERE id=$id); DELETE FROM access_keys WHERE user_id IN (SELECT id FROM disabled_users WHERE id=$id)";
        cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public SessionResponse KeyLogin(KeyLoginRequest request)
    {
        var key = request.Key.Trim(); if (key.Length != 64) throw new UnauthorizedAccessException("Ключ недействителен.");
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var find = db.CreateCommand(); find.Transaction = tx;
        find.CommandText = "SELECT u.id,u.name FROM access_keys k JOIN users u ON u.id=k.user_id WHERE k.hash=$hash AND NOT EXISTS(SELECT 1 FROM disabled_users d WHERE d.id=u.id)"; find.Parameters.AddWithValue("$hash", Store.Hash(key));
        using var reader = find.ExecuteReader(); if (!reader.Read()) throw new UnauthorizedAccessException("Ключ недействителен.");
        var id = reader.GetString(0); var name = reader.GetString(1); reader.Close();
        EnsureAccess(id);
        var token = Store.Secret(); using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO sessions VALUES ($hash,$id,$expires)"; cmd.Parameters.AddWithValue("$hash", Store.Hash(token)); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddDays(7).ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
        return new(token, id, name, false);
    }
}
