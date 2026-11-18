using System.Security.Cryptography;
using System.Text;

namespace StreamKit.Obs;

/// <summary>
/// Cadena de autenticación de obs-websocket v5:
/// <c>base64(sha256(base64(sha256(password + salt)) + challenge))</c>.
/// </summary>
public static class ObsAuth
{
    public static string CreateAuthenticationString(string password, string salt, string challenge)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(challenge);

        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
}
