using SquadHostManager.Core;
namespace SquadHostManager.Service;

public sealed partial class Accounts
{
    public AccessPolicy Policy(string id)=>store.Load<AccessPolicy>("access-policy-"+id)??new();
    public void EnsureAccess(string id)
    {
        if(Policy(id).ExpiresAt is {} expires && expires<=DateTimeOffset.UtcNow)throw new UnauthorizedAccessException("Срок доступа истёк.");
        using var db=store.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM disabled_users WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);
        if((long)cmd.ExecuteScalar()!>0)throw new UnauthorizedAccessException("Доступ отозван.");
    }
    public bool CanRent(string id)
    {
        using var db=store.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT admin FROM users WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() is long admin && admin==1 || Policy(id).CanRent;
    }
    public void EnsureRentalAccess(string id)
    {EnsureAccess(id);if(!CanRent(id))throw new UnauthorizedAccessException("Владелец не разрешил создавать платную аренду. Можно использовать уже арендованный хост.");}
    public void SetPolicy(User owner,string id,AccessPolicy policy)
    {
        Owner(owner);if(policy.MaxServers is <1 or >16)throw new ArgumentException("Лимит: 1–16 серверов.");
        if(!Users(owner).Any(u=>u.Id==id && !u.IsAdmin))throw new ArgumentException("Выберите участника.");
        store.Save("access-policy-"+id,policy);
    }
}
