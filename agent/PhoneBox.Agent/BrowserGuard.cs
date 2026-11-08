namespace PhoneBox.Agent;

/// <summary>
/// Keeps other web sites out of the local API. Browsers leave out the Origin header on cross-site GET requests
/// (an &lt;img&gt; or &lt;script&gt; tag, a no-cors fetch, a link), so a GET with a side effect, such as running a command
/// widget, could be triggered by any page. Modern browsers do send Sec-Fetch-Site on every request; the phone's USB
/// proxy and command-line tools send none.
/// </summary>
public static class BrowserGuard
{
    /// <summary>True for a request another site (or another port on this machine) made through a browser.</summary>
    public static bool IsForeign(string? secFetchSite, string? secFetchMode, string method, PathString path)
    {
        if (secFetchSite is not ("cross-site" or "same-site")) return false;   // same-origin, typed URL ("none") or no browser
        // following a link to the settings page (e.g. from the README) is fine; it only shows the page
        var pageNavigation = secFetchMode == "navigate" && HttpMethods.IsGet(method) && !path.StartsWithSegments("/api");
        return !pageNavigation;
    }
}
