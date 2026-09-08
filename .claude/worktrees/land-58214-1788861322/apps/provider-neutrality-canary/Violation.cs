// Deliberate ticket 026 violation: apps/* must receive HARBORLINE_API_PROVNEUT_001.
namespace Microsoft.Graph
{
    internal static class GraphServiceClient
    {
    }
}

namespace Harborline.Api.LocalNodeHost.AnalyzerCanary
{
    using Microsoft.Graph;

    internal static class Violation
    {
        public static string VendorReference => nameof(GraphServiceClient);
    }
}
