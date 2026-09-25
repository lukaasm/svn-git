using Sg.Core;

namespace Sg.App;

/// <summary>Unsent form values only. A checkout keeps its identity when renamed; a removed one stays unselected.</summary>
internal sealed record DestinationDraft(string Branch, string? CheckoutPath)
{
    public static DestinationDraft Capture(string branch, CheckoutConfig? checkout) => new(branch, checkout?.Path);

    public CheckoutConfig? Resolve(SgRoot root) => CheckoutPath == null ? null
        : root.Config.Checkouts.FirstOrDefault(c => string.Equals(PathUtil.Git(c.Path).TrimEnd('/'),
            PathUtil.Git(CheckoutPath).TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
