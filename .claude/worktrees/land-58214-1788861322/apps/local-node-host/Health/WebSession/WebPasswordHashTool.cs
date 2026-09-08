using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The <c>hash-web-password</c> admin subcommand: mints an Argon2id PHC hash for a web-client
/// founder password, so the plaintext NEVER lands in config or the service environment — only the
/// hash does (provisioned as <c>LocalNode__WebClient__FounderPasswordHash</c>).
/// </summary>
/// <remarks>
/// Reuses the ADR-0097 substrate hasher with its default (OWASP-floor) parameters — the same hasher
/// the running node verifies against. Usage (run from the node's directory):
/// <code>
///   Harborline.Api.LocalNodeHost hash-web-password 'the-password'      # password as an arg
///   echo 'the-password' | Harborline.Api.LocalNodeHost hash-web-password   # or piped on stdin (no shell history)
/// </code>
/// </remarks>
public static class WebPasswordHashTool
{
    /// <summary>Runs the subcommand: reads the password, prints the PHC hash to stdout. Exits non-zero on bad input.</summary>
    public static void Run(string[] args)
    {
        // Password from args[1] if given, else one line from stdin (keeps it out of shell history).
        var password = args.Length >= 2 ? args[1] : Console.ReadLine();
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine(
                "hash-web-password: no password provided (pass it as the 2nd argument, or pipe it on stdin).");
            Environment.Exit(2);
            return;
        }

        var hasher = new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions()));
        var hash = hasher.HashPassword(NodeWebUser.Instance, password);
        Console.WriteLine(hash);
    }
}
