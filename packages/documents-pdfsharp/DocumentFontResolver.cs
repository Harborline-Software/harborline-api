using PdfSharp.Fonts;

namespace Harborline.Api.Documents.PdfSharp;

/// <summary>
/// Resolves the document renderer's font requests to a usable system TrueType font (#111 design §3.4).
/// PDFsharp 6.x core does no platform font lookup on non-Windows and no built-in metrics for the base-14,
/// so a <see cref="IFontResolver"/> must supply real font bytes. This one searches the common macOS /
/// Linux / Windows font directories for a regular + bold sans-serif <c>.ttf</c> and caches the bytes; the
/// desktop / dogfood JIT node (which ships with fonts) renders through it. A host with no usable font
/// (a bare CI runner) is detected via <see cref="TryInstall"/> so a render test can skip rather than
/// false-fail — the host-independent walker/provenance tests carry correctness there.
/// </summary>
/// <remarks>
/// The renderer always requests the single logical family <see cref="FamilyName"/>; this resolver maps that
/// (regular/bold) to the found faces, so it never depends on a specific font being installed by name.
/// Embedding a font in the shipped package (to be host-independent) is a deliberate later choice — the
/// keystone renders on the JIT node where fonts exist, mirroring the design's "render on the right host".
/// </remarks>
public sealed class DocumentFontResolver : IFontResolver
{
    /// <summary>The single logical family the writer requests; mapped to the found system faces.</summary>
    public const string FamilyName = "ShipyardDocumentSans";

    private const string RegularFace = FamilyName + "#Regular";
    private const string BoldFace = FamilyName + "#Bold";

    private static readonly object Gate = new();
    private static DocumentFontResolver? _installed;

    private readonly byte[] _regular;
    private readonly byte[] _bold;

    private DocumentFontResolver(byte[] regular, byte[] bold)
    {
        _regular = regular;
        _bold = bold;
    }

    /// <summary>
    /// Idempotently installs this resolver as the process-global PDFsharp font resolver, searching for a
    /// usable font on first call. Returns <see langword="false"/> when no usable system font was found
    /// (the caller — a render path or a test — degrades honestly rather than throwing deep in PDFsharp).
    /// </summary>
    public static bool TryInstall()
    {
        lock (Gate)
        {
            if (_installed is not null)
            {
                return true;
            }

            if (GlobalFontSettings.FontResolver is not null and not DocumentFontResolver)
            {
                // Another subsystem already set a resolver — respect it (don't clobber a host's choice).
                return true;
            }

            var regular = FindFont(PreferredRegular);
            if (regular is null)
            {
                return false;
            }

            var bold = FindFont(PreferredBold) ?? regular; // fall back to the regular face if no bold found
            _installed = new DocumentFontResolver(regular, bold);
            GlobalFontSettings.FontResolver = _installed;
            return true;
        }
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName) => faceName switch
    {
        BoldFace => _bold,
        _ => _regular,
    };

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(isBold ? BoldFace : RegularFace);

    private static byte[]? FindFont(IReadOnlyList<string> preferredFileNames)
    {
        foreach (var dir in FontDirectories())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Prefer a known-good sans face by exact filename first (deterministic across hosts).
            foreach (var name in preferredFileNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return SafeRead(path);
                }
            }
        }

        // Fall back to any regular-weight .ttf anywhere in the search tree (never a .ttc — no face index).
        foreach (var dir in FontDirectories())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
                {
                    var file = Path.GetFileName(path);
                    if (LooksLikeUsableRegular(file))
                    {
                        var bytes = SafeRead(path);
                        if (bytes is not null)
                        {
                            return bytes;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip an unreadable directory tree; keep searching the others.
            }
        }

        return null;
    }

    private static bool LooksLikeUsableRegular(string fileName)
    {
        var f = fileName.ToLowerInvariant();
        if (f.Contains("bold") || f.Contains("italic") || f.Contains("oblique") || f.Contains("black") || f.Contains("light"))
        {
            return false;
        }

        return true;
    }

    private static byte[]? SafeRead(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FontDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            // macOS
            "/System/Library/Fonts/Supplemental",
            "/System/Library/Fonts",
            "/Library/Fonts",
            Path.Combine(home, "Library/Fonts"),
            // Linux
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            Path.Combine(home, ".fonts"),
            Path.Combine(home, ".local/share/fonts"),
            // Windows
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        };
    }

    // Deterministic preference order — a widely-shipped sans regular first, then permissive Linux fonts.
    private static readonly string[] PreferredRegular =
    {
        "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf", "FreeSans.ttf",
        "NotoSans-Regular.ttf", "Verdana.ttf", "Tahoma.ttf",
    };

    private static readonly string[] PreferredBold =
    {
        "Arial Bold.ttf", "Arial-Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf",
        "FreeSansBold.ttf", "NotoSans-Bold.ttf", "Verdana Bold.ttf",
    };
}
