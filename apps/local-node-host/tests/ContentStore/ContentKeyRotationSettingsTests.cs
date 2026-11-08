using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// T-521/content-store-epoch-key-store acceptance 5 and 7. Oracles: ADR 0116 decisions 3, 7 and 8's literal numbers
// (rotation 90 days in 1 day to 2 years, waiting 30 days in 7 to 30, retention at most 180 days in 1 to 180) and the
// configuration keys and refusal code the slice asks for.
public sealed class ContentKeyRotationSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => "LocalNode:ContentStore:KeyRotation:" + pair.Key, pair => (string?)pair.Value)).Build();

    [Fact]
    public void Without_settings_the_defaults_are_googles_and_the_window_is_the_180_day_retention()
    {
        var settings = ContentKeyRotationSettings.Bind(Config());

        Assert.Equal(TimeSpan.FromDays(90), settings.RotationPeriod);
        Assert.Equal(TimeSpan.FromDays(30), settings.WaitingPeriod);
        Assert.Equal(TimeSpan.FromDays(180), settings.BackupRetention);
        var window = settings.ErasureWindow(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromDays(120), window.Live);
        Assert.Equal(TimeSpan.FromDays(180), window.Backup);
        Assert.Equal(TimeSpan.FromDays(180), window.Reported);
    }

    [Fact]
    public void The_reported_window_is_the_live_window_once_rotation_waiting_and_rewrap_pass_the_retention()
    {
        var settings = ContentKeyRotationSettings.Bind(Config(("RotationPeriod", "150.00:00:00")));

        var window = settings.ErasureWindow(TimeSpan.FromDays(1));

        Assert.Equal(TimeSpan.FromDays(181), window.Live);
        Assert.Equal(TimeSpan.FromDays(181), window.Reported);
    }

    [Fact]
    public void The_window_names_the_leftover_key_blocks_limit_and_recommends_full_disk_encryption()
    {
        var window = new ContentKeyRotationSettings().ErasureWindow(TimeSpan.Zero);

        var limit = Assert.Single(window.NamedLimits);
        Assert.Contains("raw image of the node's disk and the tenant key", limit, StringComparison.Ordinal);
        Assert.Contains("full-disk encryption", limit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RotationPeriod", "1.00:00:00")]
    [InlineData("RotationPeriod", "730.00:00:00")]
    [InlineData("WaitingPeriod", "7.00:00:00")]
    [InlineData("WaitingPeriod", "30.00:00:00")]
    [InlineData("BackupRetention", "1.00:00:00")]
    [InlineData("BackupRetention", "180.00:00:00")]
    public void A_value_at_either_limit_is_accepted(string setting, string value) =>
        Assert.Equal(TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture), Value(ContentKeyRotationSettings.Bind(Config((setting, value))), setting));

    [Theory]
    [InlineData("RotationPeriod", "23:59:59")]
    [InlineData("RotationPeriod", "730.00:00:01")]
    [InlineData("WaitingPeriod", "6.23:59:59")]
    [InlineData("WaitingPeriod", "30.00:00:01")]
    [InlineData("BackupRetention", "23:59:59")]
    [InlineData("BackupRetention", "180.00:00:01")]
    [InlineData("RotationPeriod", "soon")]
    public void A_value_outside_its_limits_refuses_with_the_stable_code_naming_the_setting(string setting, string value)
    {
        var refusal = Assert.Throws<ContentKeyRotationSettingException>(() => ContentKeyRotationSettings.Bind(Config((setting, value))));

        Assert.Equal("content.key_rotation.setting_out_of_range", refusal.Code);
        Assert.Equal("LocalNode:ContentStore:KeyRotation:" + setting, refusal.Setting);
        Assert.StartsWith("content.key_rotation.setting_out_of_range: LocalNode:ContentStore:KeyRotation:" + setting + " ", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_legal_retention_floor_allows_a_longer_retention_up_to_the_floor_and_names_it_as_a_limit()
    {
        var settings = ContentKeyRotationSettings.Bind(Config(("LegalRetentionFloor", "365.00:00:00"), ("BackupRetention", "365.00:00:00")));

        var window = settings.ErasureWindow(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromDays(365), window.Reported);
        Assert.Equal(2, window.NamedLimits.Count);
        Assert.Contains("legal retention floor", window.NamedLimits[1], StringComparison.Ordinal);
        Assert.Throws<ContentKeyRotationSettingException>(() =>
            ContentKeyRotationSettings.Bind(Config(("LegalRetentionFloor", "365.00:00:00"), ("BackupRetention", "365.00:00:01"))));
        Assert.Throws<ContentKeyRotationSettingException>(() =>
            ContentKeyRotationSettings.Bind(Config(("LegalRetentionFloor", "100.00:00:00"), ("BackupRetention", "181.00:00:00"))));
    }

    [Fact]
    public void A_legal_retention_floor_under_180_days_does_not_shorten_the_retention_ceiling()
    {
        var settings = ContentKeyRotationSettings.Bind(Config(("LegalRetentionFloor", "100.00:00:00"), ("BackupRetention", "150.00:00:00")));

        Assert.Equal(TimeSpan.FromDays(150), settings.BackupRetention);
        Assert.Single(settings.ErasureWindow(TimeSpan.Zero).NamedLimits);
    }

    [Fact]
    public void The_node_composition_refuses_an_out_of_range_setting_before_the_node_starts()
    {
        var services = new ServiceCollection();

        var refusal = Assert.Throws<ContentKeyRotationSettingException>(() =>
            services.AddNodeContentStore(Path.GetTempPath(), Config(("WaitingPeriod", "31.00:00:00"))));

        Assert.Equal("LocalNode:ContentStore:KeyRotation:WaitingPeriod", refusal.Setting);
        Assert.Empty(services);
    }

    private static TimeSpan Value(ContentKeyRotationSettings settings, string setting) => setting switch
    {
        "RotationPeriod" => settings.RotationPeriod,
        "WaitingPeriod" => settings.WaitingPeriod,
        _ => settings.BackupRetention,
    };
}
