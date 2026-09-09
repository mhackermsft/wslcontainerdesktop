// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AiTextSanitizerTests
{
    public static TheoryData<string> Evidence => new()
    {
        """{"Config":{"Env":["APP_PASSWORD=synthetic-private with spaces","MODE=production"]},"name":"ordinary-context"}""",
        """{"environmentVariables":["APP_PASSWORD=first line\nsynthetic-private","MODE=production"],"name":"ordinary-context"}""",
        """{"nested":[{"name":"CLIENT_SECRET","value":"synthetic-private"},{"value":"synthetic-private","name":"API_TOKEN"}],"name":"ordinary-context"}""",
        """{"nested":{"pAsSwOrD":{"value":"synthetic-private"},"access_token":["synthetic-private"]},"name":"ordinary-context"}""",
        """{"kind":"Secret","data":{"arbitrary":"synthetic-private"},"metadata":{"name":"ordinary-context"}}""",
        """{"stringData":{"arbitrary":"synthetic-private"},"name":"ordinary-context"}""",
        """{"yaml":"services:\n  ordinary-context:\n    environment:\n      PASSWORD: synthetic-private\n      MODE: production"}""",
        """{"error":"request failed: password=\"synthetic-private\"; ordinary-context"}""",
        """ordinary-context: {"credentials":{"first":"synthetic-private","second":"synthetic-private"}}""",
        """ordinary-context: [{"kind":"Secret","data":{"arbitrary":"synthetic-private"}}]""",
        "ordinary-context\nDB_PASSWORD='synthetic-private'\nMODE=production",
        "ordinary-context\nAuthorization: Bearer synthetic-private\nMODE=production",
        "ordinary-context\nAuthorization: Basic synthetic-private\nMODE=production",
        "ordinary-context\nSet-Cookie: session=synthetic-private; HttpOnly\nMODE=production",
        "ordinary-context\npostgres://user:synthetic-private@db:5432/app",
        "ordinary-context\n-----BEGIN RSA PRIVATE KEY-----\nsynthetic-private\n-----END RSA PRIVATE KEY-----",
        "ordinary-context\npassword: |\n  synthetic-private\n  another-sensitive-line\nimage: nginx",
        "ordinary-context\npassword:\n  nested:\n    anything: synthetic-private\nimage: nginx",
        "ordinary-context\ntokens:\n- synthetic-private\n- nested:\n    anything: synthetic-private\nimage: nginx",
        "ordinary-context\nkind: Secret\ndata:\n  arbitrary: synthetic-private\nmetadata:\n  name: app",
        "ordinary-context\nenv:\n  - name: PASSWORD\n    value: synthetic-private\nimage: nginx",
        "ordinary-context\nenv:\n  - value: synthetic-private\n    name: PASSWORD\nimage: nginx",
        "ordinary-context\nconnectionString: \"Server=db;Password=synthetic-private;Database=app\"",
        "ordinary-context\nconfig: {password: 'synthetic-private', mode: production}",
        "ordinary-context\n--api-key=synthetic-private",
        "ordinary-context\ncommand --password 'synthetic-private' --verbose",
        "ordinary-context\nServer=db;Password=synthetic-private with spaces;Database=app",
    };

    [Theory]
    [MemberData(nameof(Evidence))]
    public void StructuredAndTextEvidenceRedactsWithoutLosingOrdinaryContext(string input)
    {
        var safe = AiTextSanitizer.Sanitize(input);
        Assert.DoesNotContain("synthetic-private", safe);
        Assert.Contains("ordinary-context", safe);
        Assert.Contains("redacted", safe);
        Assert.Equal(safe, AiTextSanitizer.Sanitize(safe));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(400)]
    [InlineData(4000)]
    [InlineData(12000)]
    public void RedactsEntireSecretBeforeEitherTruncationBoundary(int limit)
    {
        var secret = string.Concat(Enumerable.Repeat("synthetic-private", limit));
        var text = "ordinary head\npassword=\"" + secret + "\"\n" + new string('x', limit) + "\nordinary tail";
        var safe = AiTextSanitizer.Sanitize(text, limit);
        Assert.DoesNotContain("synthetic", safe);
        Assert.True(safe.Length <= limit);
        Assert.Contains("ordinary head", safe);
        Assert.Contains("ordinary tail", safe);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(4000)]
    public void OversizedStructuredEvidenceIsBoundedValidJsonWithExplicitOmission(int limit)
    {
        var input = JsonSerializer.Serialize(new
        {
            password = new string('s', limit) + "synthetic-private",
            logs = new string('"', limit * 2),
        });
        var safe = AiTextSanitizer.Sanitize(input, limit);
        Assert.True(safe.Length <= limit);
        Assert.DoesNotContain("synthetic-private", safe);
        using var parsed = JsonDocument.Parse(safe);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(safe, AiTextSanitizer.Sanitize(safe, limit));
    }

    [Theory]
    [InlineData("""{"password":"synthetic-private""")]
    [InlineData("""[{"env":["PASSWORD=synthetic-private""")]
    public void MalformedStructuredInputIsExplicitlyOmitted(string text)
    {
        var safe = AiTextSanitizer.Sanitize(text);
        Assert.DoesNotContain("synthetic-private", safe);
        Assert.Contains("omitted", safe);
    }

    [Fact]
    public void DeepAndEmbeddedStructuresDoNotBypassRedaction()
    {
        var input = """{"password":"synthetic-private"}""";
        Assert.DoesNotContain("synthetic-private", AiTextSanitizer.Sanitize(JsonSerializer.Serialize(new { nested = input })));
        for (var i = 0; i < 40; i++)
            input = "{\"nested\":" + input + "}";
        Assert.DoesNotContain("synthetic-private", AiTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void ExcessTargetRowsHaveAnExplicitOmissionCountAndRetainOverallStatus()
    {
        var input = JsonSerializer.Serialize(new
        {
            status = "partial",
            outcomes = Enumerable.Range(0, 1000).Select(i => new
            {
                Id = $"id-{i}", Name = $"app-{i}", Status = "unknown", Detail = "password=synthetic-private",
            }),
        });
        var safe = AiTextSanitizer.Sanitize(input, 4000);
        Assert.True(safe.Length <= 4000);
        Assert.DoesNotContain("synthetic-private", safe);
        using var doc = JsonDocument.Parse(safe);
        Assert.Equal("partial", doc.RootElement.GetProperty("status").GetString());
        var count = doc.RootElement.GetProperty("outcomes").GetArrayLength();
        Assert.True(count > 0);
        Assert.Equal(1000 - count, doc.RootElement.GetProperty("omittedOutcomes").GetInt32());
        Assert.Equal(safe, AiTextSanitizer.Sanitize(safe, 4000));
    }

    [Fact]
    public void CopiesPreserveIdsSchemasAndOriginalArguments()
    {
        var call = new AiToolCall
        {
            Id = "password=protocol-id", Name = "run_container",
            ArgumentsJson = """{"image":"nginx","environment":["PASSWORD=synthetic-private","MODE=production"]}""",
        };
        var input = new AiChatMessage { Role = "assistant", ToolCalls = [call], Content = "password=synthetic-private" };
        var safe = AiTextSanitizer.SanitizeMessage(input);
        Assert.Equal(call.Id, safe.ToolCalls[0].Id);
        Assert.Equal(call.Name, safe.ToolCalls[0].Name);
        Assert.Contains("synthetic-private", input.ToolCalls[0].ArgumentsJson);
        Assert.DoesNotContain("synthetic-private", safe.ToolCalls[0].ArgumentsJson);
        Assert.NotSame(call, safe.ToolCalls[0]);
        var schema = """{"type":"object","properties":{"password":{"type":"string"}}}""";
        var definition = AiTextSanitizer.SanitizeDefinition(new AiToolDefinition
        {
            Name = call.Name, Description = "password=synthetic-private", JsonSchemaParameters = schema,
        });
        Assert.Equal(schema, definition.JsonSchemaParameters);
        Assert.DoesNotContain("synthetic-private", definition.Description);
    }

    [Fact]
    public void UnknownUnlabelledSecretsAreNotClaimedToBeDetected()
    {
        Assert.Equal("an arbitrary unlabelled value", AiTextSanitizer.Sanitize("an arbitrary unlabelled value"));
        Assert.Equal("""{"image":"nginx","ports":[8080],"monkey":"animal"}""",
            AiTextSanitizer.Sanitize("""{"image":"nginx","ports":[8080],"monkey":"animal"}"""));
    }

    [Fact]
    public async Task DiagnosticPreviewSanitizesStructuredSectionsBeforeCombiningOrTruncating()
    {
        var h = new AiContractHarness();
        var wslc = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetLogsAsync) => Task.FromResult(new CommandResult
            {
                StandardOutput = "PASSWORD=\"" + new string('s', 17000) + "synthetic-private\"\nordinary-logs",
            }),
            nameof(IWslcService.InspectContainerAsync) => Task.FromResult(new CommandResult
            {
                StandardOutput = """{"kind":"Secret","data":{"arbitrary":"synthetic-private"},"name":"ordinary-inspect"}""",
            }),
            _ => throw new InvalidOperationException("Unexpected I/O"),
        });
        var activity = NetworkTestProxy.Create<IActivityLog>((method, _) => method.Name == "get_Events"
            ? new System.Collections.ObjectModel.ObservableCollection<ActivityEvent>()
            : throw new InvalidOperationException("Unexpected activity call"));
        var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
            method.Name == nameof(IWslcCapabilitiesService.GetAsync)
                ? Task.FromResult(new WslcCapabilities("synthetic-wslc", null, new Dictionary<WslcFeature, WslcCapability>()))
                : throw new InvalidOperationException("Unexpected capability call"));
        var diagnostics = new AiDiagnosticsService(wslc, activity, h.Settings, capabilities, [],
            NullLogger<AiDiagnosticsService>.Instance);
        var preview = await diagnostics.BuildPreviewAsync(new ContainerInfo
        {
            Id = "synthetic-id", Name = "ordinary-app", StateValue = (int)ContainerState.Stopped,
        });
        Assert.DoesNotContain("synthetic-private", preview.Payload);
        Assert.DoesNotContain(new string('s', 20), preview.Payload);
        Assert.Contains("ordinary-inspect", preview.Payload);
        Assert.Contains("ordinary-logs", preview.Payload);
        Assert.Equal(preview.Payload, preview.Request.UserPrompt);
        Assert.Contains("untrusted evidence", preview.Request.SystemPrompt);
    }

    [Fact]
    public void ErrorFeedbackSanitizesConfigurationAndTransportContext()
    {
        var context = AiErrorContext.For(AiProviderKind.OpenAi, "Assistant chat",
            "https://user:synthetic-private@provider.invalid");
        var configuration = AiErrorClassifier.Classify(new InvalidOperationException("password=synthetic-private"), context);
        var transport = AiErrorClassifier.Classify(new HttpRequestException("password=synthetic-private"), context);
        Assert.DoesNotContain("synthetic-private", JsonSerializer.Serialize(configuration));
        Assert.DoesNotContain("synthetic-private", JsonSerializer.Serialize(transport));
        var provider = new AiProviderException(AiProviderKind.OpenAi, "chat", "password=synthetic-private",
            AiFailureKind.Unexpected, inner: new IOException("password=synthetic-private"));
        Assert.DoesNotContain("synthetic-private", provider.ToString());
        Assert.DoesNotContain("synthetic-private", provider.ResponseDetail);
        Assert.Null(provider.InnerException);
    }

    [Fact]
    public void SdkLoggerReceivesOnlySanitizedStateScopesAndExceptionMessages()
    {
        var sink = new LogSink();
        var logger = AiTextSanitizer.WrapLogger(sink);
        using var scope = logger.BeginScope(new ScopeState("synthetic-private", "ordinary-scope"));
        logger.LogError(new IOException("password=synthetic-private"), "Result: {Evidence}",
            """{"password":"synthetic-private","name":"ordinary-context"}""");
        Assert.All(sink.Entries, text => Assert.DoesNotContain("synthetic-private", text));
        Assert.Contains(sink.Entries, text => text.Contains("ordinary-context", StringComparison.Ordinal));
        Assert.Contains(sink.Entries, text => text.Contains("ordinary-scope", StringComparison.Ordinal));
        Assert.Null(sink.Exception);
    }

    private sealed record ScopeState(string Password, string Name)
    {
        public override string ToString() => JsonSerializer.Serialize(this);
    }

    private sealed class LogSink : ILogger
    {
        public List<string> Entries { get; } = [];
        public Exception? Exception { get; private set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Entries.Add(state.ToString()!);
            return null;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.IsType<string>(state);
            Entries.Add(formatter(state, exception));
            Exception = exception;
        }
    }
}
