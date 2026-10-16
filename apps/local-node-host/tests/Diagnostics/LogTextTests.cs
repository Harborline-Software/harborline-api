using Harborline.Api.LocalNodeHost.Diagnostics;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Diagnostics;

/// <summary>
/// The log-text bound behind the CodeQL <c>cs/log-forging</c> fixes: control characters and the Unicode line
/// separators become a visible <c>\uXXXX</c>, ordinary text passes unchanged, null stays null, and a long value
/// is cut with an ellipsis. Expected values are literals (T-724 ruling 121), never <see cref="LogText"/> output.
/// </summary>
public sealed class LogTextTests
{
    // Code points rather than raw characters in the attribute, so the test's display name carries no line break.
    [Theory]
    [InlineData(0x000D, "a\\u000db")] // CR
    [InlineData(0x000A, "a\\u000ab")] // LF
    [InlineData(0x0085, "a\\u0085b")] // NEL (C1)
    [InlineData(0x2028, "a\\u2028b")] // LINE SEPARATOR
    [InlineData(0x2029, "a\\u2029b")] // PARAGRAPH SEPARATOR
    [InlineData(0x001B, "a\\u001bb")] // ESC
    [InlineData(0x0009, "a\\u0009b")] // TAB
    [InlineData(0x0000, "a\\u0000b")] // NUL
    [InlineData(0x007F, "a\\u007fb")] // DEL
    public void A_control_or_line_separator_becomes_a_visible_escape(int codePoint, string expected)
    {
        Assert.Equal(expected, LogText.Of($"a{(char)codePoint}b"));
    }

    [Fact]
    public void A_crlf_forged_line_stays_on_one_line()
    {
        var forged = "x\r\nAuth.SessionEstablished.PasswordLogin: forged";

        var bounded = LogText.Of(forged);

        Assert.Equal("x\\u000d\\u000aAuth.SessionEstablished.PasswordLogin: forged", bounded);
        Assert.DoesNotContain("\r", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", bounded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acme.pack")]
    [InlineData("os:bob#deadbeef / v1.2.3-beta+7 \"quoted\" \\backslash")]
    [InlineData("مرحبا")] // Arabic
    [InlineData("配置提案")] // CJK
    [InlineData("pack-🚢")] // emoji (a surrogate pair)
    public void Plain_ascii_and_non_control_unicode_pass_unchanged(string value)
    {
        Assert.Equal(value, LogText.Of(value));
    }

    [Fact]
    public void Null_stays_null_and_empty_stays_empty()
    {
        Assert.Null(LogText.Of(null));
        Assert.Equal(string.Empty, LogText.Of(string.Empty));
    }

    [Fact]
    public void A_value_at_the_bound_is_kept_whole()
    {
        var atBound = new string('k', 256);

        Assert.Equal(atBound, LogText.Of(atBound));
    }

    [Fact]
    public void One_past_the_bound_is_cut_with_an_ellipsis()
    {
        var tooLong = new string('k', 256) + "TAIL";

        Assert.Equal(new string('k', 256) + "…", LogText.Of(tooLong));
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // 255 ASCII characters then an emoji: its high surrogate sits at index 255, the last kept position.
        var straddling = new string('k', 255) + "🚢" + "TAIL";

        Assert.Equal(new string('k', 255) + "…", LogText.Of(straddling));
    }

    [Fact]
    public void Control_characters_past_the_bound_are_cut_and_those_before_it_are_escaped()
    {
        var value = "\n" + new string('k', 255) + "\nforged";

        Assert.Equal("\\u000a" + new string('k', 255) + "…", LogText.Of(value));
    }
}
