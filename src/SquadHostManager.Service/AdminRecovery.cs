using SquadHostManager.Core;

namespace SquadHostManager.Service;

public record AdoptRequest(string HostId);
public record RecoveryRequest(bool ConfirmNoPendingCherryOrder = false);
