using System.Security.Cryptography;
using System.Text;

namespace Optimizer.Core.Tweaks;

/// <summary>
/// Ids of tweaks built at runtime (startup entries, services, devices, games). The id names the backup file, so two
/// different subjects must never share one: a readable part plus a hash of the full subject.
/// </summary>
public static class TweakIds
{
    /// <summary>
    /// "runuserrun-1a2b3c4d": ASCII letters and digits of <paramref name="subject"/> (shortened to <paramref name="max"/>)
    /// and the first 8 hex digits of the SHA-256 of the whole subject (case-insensitive, as registry and service names are).
    /// </summary>
    public static string Slug(string subject, int max = 32)
    {
        var readable = new string(subject.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).Take(max).ToArray());
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.ToUpperInvariant())))[..8];
        return readable.Length > 0 ? $"{readable}-{hash}" : hash;
    }

    /// <summary>
    /// The id for a subject whose id scheme changed: <paramref name="id"/>, unless only <paramref name="legacyId"/> has a
    /// backup (a change made by an earlier version). Then the legacy id, so that change is still found and undone, and a
    /// switch back undoes it instead of making a second change on top. A stored definition keeps its own id anyway, so
    /// the change list, Undo all and the drift check need no fallback.
    /// </summary>
    public static string OrLegacy(string id, string legacyId, Func<string, bool>? hasBackup) =>
        hasBackup is not null && id != legacyId && !hasBackup(id) && hasBackup(legacyId) ? legacyId : id;
}
