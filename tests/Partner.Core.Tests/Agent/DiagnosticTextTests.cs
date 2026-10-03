using Partner.Core.Agent.Diagnostics;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// Redaction of text that is about to be shown or written down.
/// <para>
/// The failure this guards against is not a crash but a leak: the things worth diagnosing are exactly the
/// things that can carry a key, and a log that keeps them is worse than no log because it puts the secret
/// somewhere with weaker protection and a longer life.
/// </para>
/// </summary>
public sealed class DiagnosticTextTests
{
    [Fact]
    public void A_named_api_key_is_removed_but_the_name_stays()
    {
        // The name has to stay: "api_key=[redacted]" says the request was refused for a credential, which is
        // the diagnosis, while the value alone would only tell you that something was hidden.
        var cleaned = DiagnosticText.Sanitize("api_key=sk-abcdef123456 rejected by endpoint");

        Assert.DoesNotContain("sk-abcdef123456", cleaned, StringComparison.Ordinal);
        Assert.Contains("api_key", cleaned, StringComparison.Ordinal);
        Assert.Contains("rejected by endpoint", cleaned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-abcdefgh12345")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRST")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void A_token_of_a_recognisable_shape_is_removed(string token)
    {
        Assert.DoesNotContain(token, DiagnosticText.Sanitize($"the key {token} is wrong"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bearer_header_is_removed_even_though_it_names_nothing()
    {
        // "Bearer" says where a token is, not what it is, so the shape rule alone would not catch this.
        var cleaned = DiagnosticText.Sanitize("Authorization: Bearer eyJhbGciOi.J9.abc");

        Assert.DoesNotContain("eyJhbGciOi", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void A_users_home_directory_becomes_anonymised()
    {
        // Not a secret, but it is somebody's account name, and a diagnostic string has no business carrying it.
        var cleaned = DiagnosticText.Sanitize(@"could not read C:\Users\somebody\notes.txt");

        Assert.DoesNotContain("somebody", cleaned, StringComparison.Ordinal);
        Assert.Contains("notes.txt", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_message_is_left_alone()
    {
        // Redaction that touches healthy text trains people to distrust the log, and then not to read it.
        const string Message = "endpoint returned 400 INVALID_REQUEST: field 'model' is required";
        Assert.Equal(Message, DiagnosticText.Sanitize(Message));
        Assert.False(DiagnosticText.ContainsSecret(Message));
    }

    [Fact]
    public void A_very_long_message_is_bounded_and_says_so()
    {
        var cleaned = DiagnosticText.Sanitize(new string('x', 5000), maxLength: 100);

        Assert.True(cleaned.Length <= 101, $"实际长度 {cleaned.Length}");
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_becomes_nothing_out_rather_than_a_crash()
    {
        // This runs on failure paths; a redactor that throws while handling a failure is worse than useless.
        Assert.Equal(string.Empty, DiagnosticText.Sanitize(null));
        Assert.Equal(string.Empty, DiagnosticText.Sanitize(string.Empty));
    }

    [Fact]
    public void Redaction_happens_before_truncation()
    {
        // The other order would cut a 2000-character body at a limit and leave a key sitting past the edge,
        // masked or not, in whatever the caller does with the rest.
        var message = new string('a', 1900) + " api_key=sk-abcdef123456 " + new string('b', 1900);
        var cleaned = DiagnosticText.Sanitize(message);

        Assert.DoesNotContain("sk-abcdef123456", cleaned, StringComparison.Ordinal);
    }
}
