using NoMistakes.Scm;

namespace NoMistakes.Tests;

/// <summary>
/// A canned response for one exact CLI invocation, keyed by
/// "name arg1 arg2 ..." like the Go test helper processes. WantStdin, when
/// set, must match the stdin the wrapper streamed to the command.
/// </summary>
internal sealed record FakeCommandResponse(
    string Stdout = "", string Stderr = "", int Code = 0, string? WantStdin = null);

/// <summary>
/// Builds a <see cref="CommandRunner"/> that serves canned responses, porting
/// the Go tests' helper-process command factories. An invocation whose exact
/// command line has no fixture fails with "unexpected command: ...", so a
/// regression in argument construction surfaces as a command failure.
/// </summary>
internal static class ScmCommandFakes
{
    public static CommandRunner Runner(IReadOnlyDictionary<string, FakeCommandResponse> responses)
        => (name, args, stdin, _) =>
        {
            var key = (name + " " + string.Join(" ", args)).Trim();
            if (!responses.TryGetValue(key, out var response))
            {
                return Task.FromResult(new CommandResult(1, Stderr: "unexpected command: " + key));
            }
            if (response.WantStdin is not null && stdin != response.WantStdin)
            {
                return Task.FromResult(new CommandResult(
                    1, Stderr: $"stdin = {stdin ?? "<null>"}, want {response.WantStdin}"));
            }
            return Task.FromResult(new CommandResult(response.Code, response.Stdout, response.Stderr));
        };
}
