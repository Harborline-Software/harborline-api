using Harborline.Api.Foundation.Definitions.Compatibility;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionChanges;

public sealed class DefinitionChangeClassifierTests
{
    [Fact]
    public void Email_to_text_is_widening()
    {
        IDefinitionChangeClassifier classifier = new DefinitionChangeClassifier();
        var previous = Definition("contact.email", DefinitionValueKind.Email);
        var candidate = Definition("contact.email", DefinitionValueKind.Text);

        var classification = classifier.Classify(previous, candidate);

        Assert.Equal(DefinitionChangeKind.Widening, classification.Kind);
        var field = Assert.Single(classification.Fields);
        Assert.Equal("contact", field.Field);
        Assert.Equal(DefinitionChangeKind.Widening, field.Kind);
    }

    [Fact]
    public void Text_to_email_is_narrowing()
    {
        IDefinitionChangeClassifier classifier = new DefinitionChangeClassifier();
        var previous = Definition("contact.email", DefinitionValueKind.Text);
        var candidate = Definition("contact.email", DefinitionValueKind.Email);

        var classification = classifier.Classify(previous, candidate);

        Assert.Equal(DefinitionChangeKind.Narrowing, classification.Kind);
        Assert.Equal(DefinitionChangeKind.Narrowing, Assert.Single(classification.Fields).Kind);
    }

    [Fact]
    public void Same_key_with_new_meaning_is_incompatible()
    {
        IDefinitionChangeClassifier classifier = new DefinitionChangeClassifier();
        var previous = Definition("customer.contact", DefinitionValueKind.Text);
        var candidate = Definition("emergency.contact", DefinitionValueKind.Text);

        var classification = classifier.Classify(previous, candidate);

        Assert.Equal(DefinitionChangeKind.Incompatible, classification.Kind);
        Assert.Equal(DefinitionChangeKind.Incompatible, Assert.Single(classification.Fields).Kind);
    }

    [Fact]
    public void Change_without_enough_information_parks()
    {
        IDefinitionChangeClassifier classifier = new DefinitionChangeClassifier();
        var previous = Definition("customer.contact", DefinitionValueKind.Text);
        var candidate = new DefinitionShape(Array.Empty<DefinitionFieldShape>());

        var classification = classifier.Classify(previous, candidate);

        Assert.Equal(DefinitionChangeKind.Parked, classification.Kind);
        Assert.Equal("contact", Assert.Single(classification.Fields).Field);
    }

    private static DefinitionShape Definition(string meaning, DefinitionValueKind kind)
        => new(new[]
        {
            new DefinitionFieldShape("contact", meaning, kind),
        });
}
