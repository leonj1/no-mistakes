namespace NoMistakes.Core;

/// <summary>
/// Pipeline step identifiers plus their fixed execution order and the legacy
/// name normalization. Mirrors Go's types.StepName constants and helpers
/// (Order, AllSteps, and the "babysit" -> "ci" rename).
/// </summary>
public static class StepName
{
    public const string Intent = "intent";
    public const string Rebase = "rebase";
    public const string Review = "review";
    public const string Test = "test";
    public const string Document = "document";
    public const string Lint = "lint";
    public const string Push = "push";
    public const string Pr = "pr";
    public const string Ci = "ci";

    /// <summary>
    /// Normalize maps the retired "babysit" step name onto its current name
    /// "ci"; every other value is returned unchanged. Applied whenever a step
    /// name is read from JSON or the database so old rows keep resolving.
    /// </summary>
    public static string Normalize(string name) => name == "babysit" ? Ci : name;

    /// <summary>Returns the fixed 1-indexed execution order for a step, or 0 if unknown.</summary>
    public static int Order(string name) => Normalize(name) switch
    {
        Intent => 1,
        Rebase => 2,
        Review => 3,
        Test => 4,
        Document => 5,
        Lint => 6,
        Push => 7,
        Pr => 8,
        Ci => 9,
        _ => 0,
    };

    /// <summary>Returns all pipeline steps in execution order.</summary>
    public static IReadOnlyList<string> All => new[]
    {
        Intent, Rebase, Review, Test, Document, Lint, Push, Pr, Ci,
    };
}
