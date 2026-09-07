using Harborline.Api.Foundation.Enums;

namespace Harborline.Api.Foundation.Data;

public class SortDescriptor
{
    public string Field { get; set; } = string.Empty;
    public SortDirection Direction { get; set; }
}
