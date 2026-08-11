namespace CompanyOps.Agent.Inventory;

public interface ILegacyPm2ClaimProvider
{
    Task<IReadOnlyList<LegacyPm2Claim>> GetClaimsAsync(CancellationToken cancellationToken);
}
