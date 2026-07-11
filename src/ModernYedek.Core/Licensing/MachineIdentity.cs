using System.Security.Cryptography;
using System.Text;

namespace ModernYedek.Core.Licensing;

public static class MachineIdentity
{
    public static string Current()
    {
        var raw = string.Join("|",
            Environment.MachineName,
            Environment.UserName,
            Environment.OSVersion.VersionString,
            Environment.ProcessorCount);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
