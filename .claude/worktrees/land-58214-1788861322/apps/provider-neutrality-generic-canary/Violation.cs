namespace Microsoft.AspNetCore.Identity
{
    internal interface IPasswordHasher<T>
    {
    }
}

namespace Harborline.Api.Analyzers.GenericCanary
{
    using IPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Subject>;

    internal sealed class Subject
    {
    }

    internal static class Violation
    {
        public static string VendorReference => nameof(IPasswordHasher);
    }
}
