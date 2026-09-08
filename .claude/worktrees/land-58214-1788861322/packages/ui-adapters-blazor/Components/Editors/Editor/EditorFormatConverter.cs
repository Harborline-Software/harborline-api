using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.UIAdapters.Blazor.Components.Editors;

/// <summary>
/// Converts editor content between HTML and another format. Register
/// implementations via DI; the editor resolves them at runtime by format name.
/// </summary>
public interface IEditorFormatConverter
{
    /// <summary>The format this converter handles (e.g., "markdown", "plaintext").</summary>
    string Format { get; }

    /// <summary>Convert from this format to HTML.</summary>
    string ToHtml(string content);

    /// <summary>Convert from HTML to this format.</summary>
    string FromHtml(string html);
}

/// <summary>
/// Plaintext ↔ HTML converter. Import wraps lines in &lt;p&gt; tags;
/// export strips all HTML tags. No external dependencies.
/// </summary>
internal sealed class PlainTextFormatConverter : IEditorFormatConverter
{
    public string Format => "plaintext";

    public string ToHtml(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var lines = content.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(trimmed)).Append("</p>");
        }
        return sb.ToString();
    }

    public string FromHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        // Replace block-level closing tags with newlines, then strip all tags
        var text = Regex.Replace(html, @"</(?:p|div|br|h[1-6]|li|tr)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }
}

/// <summary>
/// DI registration extensions for HarborlineEditor format converters.
/// </summary>
public static class HarborlineEditorServiceExtensions
{
    /// <summary>
    /// Registers the plaintext format converter for HarborlineEditor import/export.
    /// Converts between plain text and HTML via simple tag wrapping/stripping.
    /// No external dependencies.
    /// </summary>
    public static IServiceCollection AddHarborlineEditorPlainTextSupport(
        this IServiceCollection services)
    {
        services.AddSingleton<IEditorFormatConverter, PlainTextFormatConverter>();
        return services;
    }
}
