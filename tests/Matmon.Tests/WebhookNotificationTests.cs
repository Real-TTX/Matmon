using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// A webhook notification is a template filled in for four different targets, each with its own escaping - and a
/// missing escape only shows up as a refused alert in a log. These pin the escaping per target, the legacy default
/// body, the signature and, for every preset, that it builds a request the service can parse.
/// </summary>
public sealed class WebhookNotificationTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_json_body_escapes_quotes_backslashes_and_line_breaks_and_keeps_umlauts()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://hooks.example.com/x",
            BodyTemplate = """{"message":"{{message}}"}"""
        };

        var request = WebhookRequestBuilder.Build(settings, Values(("message", "Disk \"C:\\\" läuft voll\nnext line")), null);

        using var document = JsonDocument.Parse(request.Body!);
        Assert.Equal("Disk \"C:\\\" läuft voll\nnext line", document.RootElement.GetProperty("message").GetString());
        Assert.Contains("läuft", request.Body); // readable, not \u00e4
        Assert.Equal(WebhookContentTypes.Json, request.ContentType);
    }

    [Fact]
    public void A_placeholder_in_the_address_is_url_encoded_and_a_triple_brace_one_is_not()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://api.example.com/bot{{{secret}}}/send?to={{receiver.target}}",
            Secret = "123:ABC"
        };

        var request = WebhookRequestBuilder.Build(settings, Values(), "a b/c&d");

        Assert.Equal("https://api.example.com/bot123:ABC/send?to=a%20b%2Fc%26d", request.Url);
    }

    [Fact]
    public void A_form_body_encodes_each_value_on_its_own()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://api.pushover.net/1/messages.json",
            ContentType = WebhookContentTypes.Form,
            Secret = "tok&en",
            BodyTemplate = "token={{secret}}\nuser={{receiver.target}}\n# a comment\nmessage={{summary}}"
        };

        var request = WebhookRequestBuilder.Build(settings, Values(("summary", "a=b & c")), "uKey");

        Assert.Equal("token=tok%26en&user=uKey&message=a%3Db%20%26%20c", request.Body);
        Assert.Equal(WebhookContentTypes.Form, request.ContentType);
    }

    [Fact]
    public void A_header_value_cannot_start_a_header_of_its_own()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://hooks.example.com/x",
            Headers = "X-Title: {{subject}}\nnot a header line\nBad Name: x\nAuthorization: Bearer {{secret}}",
            Secret = "s3cret"
        };

        var request = WebhookRequestBuilder.Build(settings, Values(("subject", "evil\r\nX-Injected: 1")), null);

        Assert.Equal(2, request.Headers.Count);
        Assert.Equal("evil  X-Injected: 1", request.Headers[0].Value);
        Assert.DoesNotContain(request.Headers, header => header.Key == "X-Injected");
        Assert.Equal(new KeyValuePair<string, string>("Authorization", "Bearer s3cret"), request.Headers[1]);
    }

    [Fact]
    public void A_sender_from_before_the_templates_sends_the_generic_json_document()
    {
        var legacy = new WebhookNotificationSettings { EndpointUrl = "https://hooks.example.com/matmon" };

        var request = WebhookRequestBuilder.Build(legacy, Values(("state.key", "critical"), ("element.path", "Main / NAS")), null);

        Assert.Equal("POST", request.Method);
        using var document = JsonDocument.Parse(request.Body!);
        Assert.Equal("critical", document.RootElement.GetProperty("state").GetString());
        Assert.Equal("Main / NAS", document.RootElement.GetProperty("element").GetString());
    }

    [Fact]
    public void A_get_request_has_no_body()
    {
        var settings = new WebhookNotificationSettings { EndpointUrl = "https://example.com/ping?s={{state.key}}", Method = "get" };

        var request = WebhookRequestBuilder.Build(settings, Values(("state.key", "warning")), null);

        Assert.Equal("GET", request.Method);
        Assert.Null(request.Body);
        Assert.Equal("https://example.com/ping?s=warning", request.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hooks.example.com/x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("{{receiver.target}}")]
    public void An_address_that_is_not_absolute_http_is_refused_in_words(string url)
    {
        var settings = new WebhookNotificationSettings { EndpointUrl = url };

        var exception = Assert.Throws<InvalidOperationException>(() => WebhookRequestBuilder.Build(settings, Values(), "https://x"));
        Assert.Contains("http", exception.Message);
    }

    [Fact]
    public void A_json_body_that_is_not_json_once_filled_in_is_refused_in_words()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://hooks.example.com/x",
            BodyTemplate = """{"priority": {{state.label}}}""" // a text value without quotes
        };

        var exception = Assert.Throws<InvalidOperationException>(() => WebhookRequestBuilder.Build(settings, Values(("state.label", "Warning")), null));
        Assert.Contains("not valid JSON", exception.Message);
    }

    [Fact]
    public void A_signed_request_carries_the_hmac_of_its_body()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://hooks.example.com/x",
            BodyTemplate = """{"a":"{{message}}"}""",
            Secret = "key",
            SignRequests = true
        };

        var request = WebhookRequestBuilder.Build(settings, Values(("message", "hi")), null);

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("key"), Encoding.UTF8.GetBytes(request.Body!)));
        Assert.Contains(request.Headers, header => header.Key == WebhookRequestBuilder.SignatureHeader && header.Value == expected);
    }

    [Fact]
    public void A_template_cannot_forge_the_signature_header()
    {
        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = "https://hooks.example.com/x",
            Headers = "X-Matmon-Signature: sha256=forged"
        };

        var request = WebhookRequestBuilder.Build(settings, Values(), null);

        Assert.DoesNotContain(request.Headers, header => header.Key == WebhookRequestBuilder.SignatureHeader);
    }

    [Fact]
    public void Only_the_scheme_and_host_of_the_address_may_be_logged()
    {
        var request = new WebhookRequest("POST", "https://api.telegram.org/bot123:SECRET/sendMessage", [], null, null);

        Assert.Equal("https://api.telegram.org", request.SafeTarget);
    }

    [Fact]
    public void Every_preset_builds_a_request_the_service_can_parse()
    {
        var keys = WebhookPresets.All.Select(preset => preset.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var preset in WebhookPresets.All)
        {
            var settings = new WebhookNotificationSettings
            {
                // Teams, Slack and Discord ship without an address - the user pastes theirs.
                EndpointUrl = string.IsNullOrEmpty(preset.Url) ? "https://hooks.example.com/pasted" : preset.Url,
                Method = preset.Method,
                ContentType = preset.ContentType,
                Headers = preset.Headers,
                BodyTemplate = preset.Body,
                Secret = "secret-value"
            };

            var request = WebhookRequestBuilder.Build(settings, NotificationTemplateCatalog.SampleValues(), "matmon-alerts");

            Assert.True(Uri.IsWellFormedUriString(request.Url, UriKind.Absolute), preset.Key);
            Assert.False(string.IsNullOrWhiteSpace(preset.Hint), preset.Key);
            if (request.ContentType == WebhookContentTypes.Json)
            {
                using var _ = JsonDocument.Parse(request.Body!); // throws when a preset is not JSON
            }
        }
    }

    [Fact]
    public void The_subject_and_summary_are_rendered_first_and_usable_as_placeholders()
    {
        var context = new NotificationTemplateContext();
        context.SetValue("state.label", "Critical");
        context.SetValue("element.path", "Main / UPS");
        context.SetValue("message", new string('x', 400));

        var rendered = NotificationRendering.Render(context, null, "Subject was: {{subject}}", null, null);

        Assert.Equal("Critical: Main / UPS", rendered.Subject);
        Assert.Equal("Subject was: Critical: Main / UPS", rendered.Text);
        Assert.Equal(NotificationRendering.MaxSummaryLength, rendered.Summary.Length);
        Assert.EndsWith("…", rendered.Summary);
        Assert.Equal(rendered.Summary, context.Values["summary"]);
    }

    [Theory]
    [InlineData(SensorState.Critical, false, "5", "1")]
    [InlineData(SensorState.Warning, false, "4", "0")]
    [InlineData(SensorState.Critical, true, "3", "0")]
    public void Push_priority_follows_the_state_and_a_recovery_is_never_urgent(SensorState state, bool recovered, string priority, string urgent)
    {
        var context = new NotificationTemplateContext();

        NotificationRendering.SetStateValues(context, state, recovered);

        Assert.Equal(priority, context.Values["state.priority"]);
        Assert.Equal(urgent, context.Values["state.urgent"]);
    }
}
