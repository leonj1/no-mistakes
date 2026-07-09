namespace NoMistakes.Core;

/// <summary>
/// The actions a driver can take on a step awaiting approval. Mirrors Go's
/// types.ApprovalAction constants; the values travel on the IPC respond wire.
/// </summary>
public static class ApprovalAction
{
    public const string Approve = "approve";
    public const string Fix = "fix";
    public const string Skip = "skip";
}
