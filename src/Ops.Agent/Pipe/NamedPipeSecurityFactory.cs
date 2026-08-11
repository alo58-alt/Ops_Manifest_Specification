using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Pipe;

public sealed class NamedPipeSecurityFactory(IOptions<OpsOptions> options)
{
    public NamedPipeServerStream Create()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var allowedSids = new HashSet<SecurityIdentifier>();
        AddWellKnownSid(allowedSids, WellKnownSidType.BuiltinAdministratorsSid);
        AddWellKnownSid(allowedSids, WellKnownSidType.LocalSystemSid);

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
        {
            allowedSids.Add(identity.User);
        }

        foreach (var configuredSid in options.Value.AllowedClientSids)
        {
            if (string.IsNullOrWhiteSpace(configuredSid))
            {
                continue;
            }

            allowedSids.Add(new SecurityIdentifier(configuredSid.Trim()));
        }

        foreach (var sid in allowedSids)
        {
            pipeSecurity.AddAccessRule(
                new PipeAccessRule(
                    sid,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            options.Value.PipeName,
            PipeDirection.InOut,
            5,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            64 * 1024,
            64 * 1024,
            pipeSecurity,
            HandleInheritability.None,
            PipeAccessRights.ChangePermissions);
    }

    private static void AddWellKnownSid(
        HashSet<SecurityIdentifier> destination,
        WellKnownSidType sidType) =>
        destination.Add(new SecurityIdentifier(sidType, null));
}
