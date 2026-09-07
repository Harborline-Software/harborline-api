// Deliberate ADR 0013 violation: a vendor SDK namespace referenced from a non-providers package.
// Expected: HARBORLINE_API_PROVNEUT_001, severity Error. If this file COMPILES, the analyzer is not
// attached to Harborline.Api.Foundation.AnalyzerCanary and the guard it represents is silently absent.
//
// The check must assert the DIAGNOSTIC ID, never merely "the build failed" — a syntax error in
// this file would also fail the build while proving nothing about the analyzer.
namespace Stripe
{
    internal static class Checkout
    {
    }
}

namespace Harborline.Api.Foundation.AnalyzerCanary
{
    using Stripe;

    internal static class Violation
    {
        public static string VendorReference => nameof(Checkout);
    }
}
