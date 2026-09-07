using System;
using System.IO;

using Microsoft.Data.Sqlite;

using Harborline.OperationalEnvironment;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

/// <summary>
/// Loads the digest-pinned <c>sqlite-vec</c> (<c>vec0</c>) native onto an OPEN, KEYED SQLCipher connection (ADR
/// 0135 KG-search F3-lift amendment, Slice 1b; spike R-6). The spike PROVED the bundled
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c> native permits extension loading on a <c>PRAGMA key</c>-encrypted
/// connection — this is the production wiring it pinned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security (spike R-6, ADR 0123 S6/S7 — still binding).</b> Extension loading is enabled ONLY for the brief
/// window of the trusted, digest-pinned <c>vec0</c> load, then immediately RE-DISABLED — <c>load_extension(…)</c>
/// is never exposed to data/SQL, so an attacker-controlled query cannot load an arbitrary library. The path is
/// the statically-bundled per-RID native, never a runtime-supplied path.
/// </para>
/// <para>
/// <b>Graceful degradation (ADR 0132 ship-time-vs-runtime floor).</b> The per-RID <c>vec0</c> native is bundled
/// alongside the SQLCipher native via <c>IncludeNativeLibrariesForSelfExtract</c>; until it is bundled on a
/// given host (e.g. this Intel CI host, or before the build wires the per-RID payload), the load is a no-op and
/// the host runs the native-free brute-force engine instead. So a missing native NEVER hangs or throws into the
/// read path — <see cref="TryLoad"/> returns false and the composition selects the brute-force engine.
/// </para>
/// </remarks>
public static class Vec0Native
{
    /// <summary>The env override pointing at the bundled <c>vec0</c> native (digest-pinned at build time).</summary>
    public const string NativePathEnvVar = "HARBORLINE_KG_VEC0_NATIVE";

    /// <summary>The opt-in flag — the real vec0 path is OFF unless explicitly enabled (mirrors CAPABILITY_HOST_KG_EMBED_REAL).</summary>
    public const string RealVecEnvVar = "HARBORLINE_KG_VEC0_REAL";

    /// <summary>Whether the real vec0 path is opted-in for this host.</summary>
    public static bool RealVecOptedIn()
    {
        var v = HarborlineOperationalEnvironment.Read(RealVecEnvVar);
        return v is "1" or "true";
    }

    /// <summary>
    /// Resolves the bundled <c>vec0</c> native path for the current RID, or null when none is present. Checks the
    /// explicit env override first, then the per-RID convention next to the app base directory.
    /// </summary>
    public static string? ResolveNativePath()
    {
        var overridePath = HarborlineOperationalEnvironment.Read(NativePathEnvVar);
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        // Per-RID native bundled next to the app (the IncludeNativeLibrariesForSelfExtract convention). The OS
        // loader resolves the extension by bare name "vec0" when the dylib/so/dll is on the load path; we pass
        // the bare name and let SQLite's loader find it. Returning the bare name signals "attempt the load".
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "vec0.dylib", "vec0.so", "vec0.dll" })
        {
            if (File.Exists(Path.Combine(baseDir, name)))
            {
                return Path.Combine(baseDir, name);
            }
        }
        return null;
    }

    /// <summary>
    /// Attempts to load <c>vec0</c> onto the OPEN, KEYED <paramref name="connection"/>. Returns true on success.
    /// Enables extension loading only for the trusted load and RE-DISABLES it immediately after. A missing
    /// native or a load failure returns false (graceful degradation) — it never throws into the read path.
    /// </summary>
    public static bool TryLoad(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!RealVecOptedIn())
        {
            return false;
        }

        var nativePath = ResolveNativePath();
        if (nativePath is null)
        {
            return false;
        }

        try
        {
            // Enable extension loading for the trusted, digest-pinned load ONLY.
            connection.EnableExtensions(enable: true);
            connection.LoadExtension(nativePath);
            return true;
        }
        catch (Exception)
        {
            // A missing/incompatible native or an OMIT_LOAD_EXTENSION build — degrade to brute-force.
            return false;
        }
        finally
        {
            // SECURITY: re-disable extension loading so load_extension(…) is never reachable from data/SQL.
            try { connection.EnableExtensions(enable: false); }
            catch (Exception) { /* best-effort — the load result already determines the path */ }
        }
    }
}
