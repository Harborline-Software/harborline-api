using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>
/// The install settings of the content store's epoch-key rotation (ADR 0116 decisions 3 and 7), bound from
/// <c>LocalNode:ContentStore:KeyRotation</c>. The defaults are Google's; an out-of-range value refuses at startup.
/// </summary>
public sealed class ContentKeyRotationSettings
{
    /// <summary>The configuration section the settings bind from.</summary>
    public const string Section = "LocalNode:ContentStore:KeyRotation";

    /// <summary>The stable refusal code of a setting outside its limits.</summary>
    public const string OutOfRangeCode = "content.key_rotation.setting_out_of_range";

    /// <summary>The longest backup retention without a legal retention floor (ADR 0116 decision 7).</summary>
    public static readonly TimeSpan RetentionCeiling = TimeSpan.FromDays(180);

    /// <summary>How often a tenant's epoch key rotates: 90 days by default, 1 day to 2 years.</summary>
    public TimeSpan RotationPeriod { get; init; } = TimeSpan.FromDays(90);

    /// <summary>How long an unused old epoch key waits before it is deleted: 30 days by default, 7 to 30 days.</summary>
    public TimeSpan WaitingPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The longest a backup is kept: 180 days by default, or the legal floor when set; at least the floor and at most 180 days or <see cref="LegalRetentionFloor"/>.</summary>
    public TimeSpan BackupRetention { get; init; } = TimeSpan.FromDays(180);

    /// <summary>A legal retention floor that requires keeping backups longer than 180 days (DES-0046 compliance-ck-3); none by default.</summary>
    public TimeSpan? LegalRetentionFloor { get; init; }

    /// <summary>Binds the section, applying the defaults for every setting it leaves out, and refuses a value outside its limits.</summary>
    public static ContentKeyRotationSettings Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var defaults = new ContentKeyRotationSettings();
        var floor = Read(section, nameof(LegalRetentionFloor));
        var settings = new ContentKeyRotationSettings
        {
            RotationPeriod = Read(section, nameof(RotationPeriod)) ?? defaults.RotationPeriod,
            WaitingPeriod = Read(section, nameof(WaitingPeriod)) ?? defaults.WaitingPeriod,
            BackupRetention = Read(section, nameof(BackupRetention)) ?? floor ?? defaults.BackupRetention,
            LegalRetentionFloor = floor,
        };
        settings.Validate();
        return settings;
    }

    /// <summary>Refuses the first setting outside its limits with <see cref="OutOfRangeCode"/>, naming it.</summary>
    public void Validate()
    {
        Require(nameof(RotationPeriod), RotationPeriod, TimeSpan.FromDays(1), TimeSpan.FromDays(730));
        Require(nameof(WaitingPeriod), WaitingPeriod, TimeSpan.FromDays(7), TimeSpan.FromDays(30));
        if (LegalRetentionFloor is { } legalFloor && legalFloor <= TimeSpan.Zero)
            throw new ContentKeyRotationSettingException(nameof(LegalRetentionFloor), legalFloor);
        var ceiling = LegalRetentionFloor is { } floor && floor > RetentionCeiling ? floor : RetentionCeiling;
        Require(nameof(BackupRetention), BackupRetention, LegalRetentionFloor is { } minimum && minimum > TimeSpan.FromDays(1) ? minimum : TimeSpan.FromDays(1), ceiling);
    }

    /// <summary>The erasure window these settings give, with <paramref name="lastRewrap"/> the last measured rewrap time.</summary>
    public ContentErasureWindow ErasureWindow(TimeSpan lastRewrap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lastRewrap, TimeSpan.Zero);
        var live = RotationPeriod + lastRewrap + WaitingPeriod;
        var limits = new List<string> { ContentErasureWindow.LeftoverKeyBlocks };
        if (BackupRetention > RetentionCeiling) limits.Add(ContentErasureWindow.LegalRetentionFloorLimit);
        return new(live, BackupRetention, live > BackupRetention ? live : BackupRetention, limits);
    }

    private static TimeSpan? Read(IConfigurationSection section, string name)
    {
        var text = section[name];
        if (text is null) return null;
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : throw new ContentKeyRotationSettingException(name, text);
    }

    private static void Require(string name, TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum || value > maximum) throw new ContentKeyRotationSettingException(name, value);
    }
}

/// <summary>A rotation setting outside its limits, refused at startup with a stable code that names the setting.</summary>
public sealed class ContentKeyRotationSettingException(string setting, object value)
    : InvalidOperationException($"{ContentKeyRotationSettings.OutOfRangeCode}: {ContentKeyRotationSettings.Section}:{setting} is out of range ({value}).")
{
    /// <summary>The stable refusal code.</summary>
    public static string Code => ContentKeyRotationSettings.OutOfRangeCode;

    /// <summary>The setting's full configuration key.</summary>
    public string Setting { get; } = $"{ContentKeyRotationSettings.Section}:{setting}";
}

/// <summary>
/// The erasure window an install reports (ADR 0116 decision 7): the live window is the rotation period plus the last
/// measured rewrap time plus the waiting period; the backup window is the retention; the reported window is the larger.
/// It is computed from the settings, never a fixed promise, and it names its limits (decision 8, DES-0046 compliance-ck-5).
/// </summary>
/// <param name="Live">Rotation plus rewrap plus waiting period.</param>
/// <param name="Backup">The backup retention.</param>
/// <param name="Reported">The larger of the two.</param>
/// <param name="NamedLimits">What the window does not cover.</param>
public sealed record ContentErasureWindow(TimeSpan Live, TimeSpan Backup, TimeSpan Reported, IReadOnlyList<string> NamedLimits)
{
    /// <summary>ADR 0116 decision 8.</summary>
    public const string LeftoverKeyBlocks =
        "A destroyed epoch key file's old disk blocks stay readable to someone holding a raw image of the node's disk and the tenant key; full-disk encryption on the node's volume narrows this, and destroying the tenant key closes it.";

    /// <summary>DES-0046 compliance-ck-5: a retention past 180 days under a legal floor is the longer window.</summary>
    public const string LegalRetentionFloorLimit =
        "Backups are kept past 180 days because a legal retention floor requires it, so erased content stays in backups for the retention.";
}
