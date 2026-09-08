namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Fields;

/// <summary>The controlled value of a <c>HarborlineCreditCardField</c>.</summary>
public sealed record HarborlineCreditCardValue(
    string Number = "",
    string Name = "",
    string Expiry = "",
    string Cvc = "");

/// <summary>Card brands detectable from the leading digits.</summary>
public enum HarborlineCreditCardBrand
{
    Unknown,
    Visa,
    Mastercard,
    Amex,
    Discover,
}
