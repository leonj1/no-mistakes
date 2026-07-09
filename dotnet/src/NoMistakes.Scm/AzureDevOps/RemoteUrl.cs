namespace NoMistakes.Scm.AzureDevOps;

/// <summary>
/// The components of an Azure DevOps git remote: a fully-qualified
/// organization URL suitable for the az CLI's --organization flag (e.g.
/// https://dev.azure.com/myorg), the project name, and the repository name.
/// </summary>
public readonly record struct AzureDevOpsRemote(string OrgUrl, string Project, string Repo);

/// <summary>
/// Azure DevOps remote-URL parsing, mirroring Go's
/// <c>internal/scm/azuredevops</c> url.go.
/// </summary>
public static class RemoteUrl
{
    /// <summary>
    /// Extracts the Azure DevOps organization URL, project name, and
    /// repository name from a git remote (or pull-request) URL. Returns false
    /// for any non-Azure remote or when a component cannot be determined.
    ///
    /// Supported forms:
    /// <code>
    /// https://dev.azure.com/{org}/{project}/_git/{repo}
    /// https://{org}@dev.azure.com/{org}/{project}/_git/{repo}
    /// git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
    /// https://{org}.visualstudio.com/[{collection}/]{project}/_git/{repo}
    /// git@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}
    /// </code>
    ///
    /// Any of the above may carry a trailing /pullrequest/{id} segment (a PR
    /// URL), which is ignored. Project names may contain spaces; path segments
    /// are percent-decoded.
    /// </summary>
    public static bool TryParseRemote(string remote, out AzureDevOpsRemote result)
    {
        result = default;
        var s = remote.Trim();
        if (s.Length == 0)
        {
            return false;
        }
        if (s.EndsWith(".git", StringComparison.Ordinal))
        {
            s = s[..^4];
        }

        string host;
        string[] segments;

        if (s.Contains("://"))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            {
                return false;
            }
            host = uri.Host.ToLowerInvariant();
            segments = SplitDecodePath(uri.AbsolutePath);
        }
        else
        {
            // scp-like syntax: [user@]host:path. The first ':' separates host
            // from path; bail when a '/' precedes it (not scp form).
            var colon = s.IndexOf(':');
            if (colon < 0 || s[..colon].Contains('/'))
            {
                return false;
            }
            var hostPart = s[..colon];
            var at = hostPart.LastIndexOf('@');
            if (at >= 0)
            {
                hostPart = hostPart[(at + 1)..];
            }
            host = hostPart.ToLowerInvariant();
            segments = SplitDecodePath(s[(colon + 1)..]);
        }

        var isVisualStudio = host.EndsWith(".visualstudio.com", StringComparison.Ordinal);
        if (host != "dev.azure.com" && host != "ssh.dev.azure.com" && !isVisualStudio)
        {
            return false;
        }

        // Azure SSH paths are prefixed with a literal "v3" segment.
        if (segments.Length > 0 && string.Equals(segments[0], "v3", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments[1..];
        }
        if (segments.Length == 0)
        {
            return false;
        }

        // HTTPS forms carry a "_git" marker; SSH forms do not. Locating the
        // repo by the marker tolerates an optional collection segment and a
        // /pullrequest suffix.
        var gitIdx = Array.IndexOf(segments, "_git");

        string org, project, repo;
        if (gitIdx >= 0)
        {
            if (gitIdx == 0 || gitIdx + 1 >= segments.Length)
            {
                return false;
            }
            project = segments[gitIdx - 1];
            repo = segments[gitIdx + 1];
            org = isVisualStudio
                ? host[..^".visualstudio.com".Length]
                : segments[0];
        }
        else
        {
            // SSH form: {org}/{project}/{repo}
            if (segments.Length < 3)
            {
                return false;
            }
            (org, project, repo) = (segments[0], segments[1], segments[2]);
        }

        org = org.Trim();
        project = project.Trim();
        repo = repo.Trim();
        if (org.Length == 0 || project.Length == 0 || repo.Length == 0)
        {
            return false;
        }

        var orgUrl = isVisualStudio
            ? "https://" + org + ".visualstudio.com"
            : "https://dev.azure.com/" + org;
        result = new AzureDevOpsRemote(orgUrl, project, repo);
        return true;
    }

    private static string[] SplitDecodePath(string p)
    {
        var raw = p.Trim('/').Split('/');
        var outSegments = new List<string>(raw.Length);
        foreach (var seg in raw)
        {
            if (seg.Length == 0)
            {
                continue;
            }
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(seg);
            }
            catch (UriFormatException)
            {
                decoded = seg;
            }
            outSegments.Add(decoded);
        }
        return outSegments.ToArray();
    }

    /// <summary>
    /// Builds the browsable pull-request URL. Prefers the repository web URL
    /// returned by the API; otherwise constructs one from the org URL,
    /// project, and repo (percent-encoding the project and repo segments).
    /// The az CLI returns an _apis/... endpoint in the PR's top-level "url"
    /// field, which is not browsable, so the human URL must be built rather
    /// than read from there.
    /// </summary>
    internal static string WebPRUrl(string orgUrl, string project, string repo, string repoWebUrl, string id)
    {
        var baseUrl = repoWebUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            baseUrl = orgUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(project) + "/_git/" + Uri.EscapeDataString(repo);
        }
        if (id.Length == 0)
        {
            return baseUrl;
        }
        return baseUrl + "/pullrequest/" + id;
    }
}
