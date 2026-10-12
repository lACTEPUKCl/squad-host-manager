namespace SquadHostManager.Core;

public record BalanceView(decimal? Available,string Currency,decimal? Promo,DateTimeOffset? CheckedAt,bool Stale=false);
