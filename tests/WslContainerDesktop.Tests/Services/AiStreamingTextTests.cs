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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AiStreamingTextTests
{
    [Fact]
    public void CompleteSentencesAppearBeforeFinalResponseWithoutFragmentLeak()
    {
        var updates = new List<AiChatProgress>();
        var stream = new AiStreamingText(updates.Add);
        stream.Append("The container is ");
        Assert.Empty(updates);
        stream.Append("stopped. ");
        Assert.Equal("The container is stopped. ", Assert.Single(updates).Text);
        stream.Append("The next step is inspection.\n");
        Assert.Equal(2, updates.Count);
        stream.Append("All done.");
        Assert.Equal(2, updates.Count);
        stream.Complete();
        Assert.Equal("The container is stopped. The next step is inspection.\nAll done.", string.Concat(updates.Select(p => p.Text)));
    }

    [Theory]
    [InlineData("Here is evidence.\n{\"value\":\"crossboundarysecret\", \"name\":\"PASSWORD\"}")]
    [InlineData("Here is evidence.\npassword: |\n  crossboundarysecret\n  more\nordinary: ok")]
    [InlineData("Here is evidence.\ndata:\n  value: crossboundarysecret\nkind: Secret")]
    [InlineData("Here is evidence.\n-----BEGIN PRIVATE KEY-----\ncrossboundarysecret\n-----END PRIVATE KEY-----")]
    [InlineData("Here is evidence.\nAuthorization: Bearer crossboundarysecret")]
    [InlineData("Here is evidence.\npassword='crossboundarysecret'")]
    [InlineData("Here is evidence.\nBearer crossboundarysecret\n")]
    [InlineData("Here is evidence.\nBasic\ncrossboundarysecret\n")]
    public void EverySplitKeepsStructuredAndCredentialValuesPrivate(string text)
    {
        for (var split = 0; split <= text.Length; split++)
        {
            var updates = new List<AiChatProgress>();
            var stream = new AiStreamingText(updates.Add);
            stream.Append(text[..split]);
            stream.Append(text[split..]);
            stream.Complete();
            Assert.DoesNotContain("crossboundarysecret", string.Concat(updates.Select(p => p.Text)));
        }
    }

    [Fact]
    public void LateSecretResourceTypeCannotExposeEarlierData()
    {
        var updates = new List<AiChatProgress>();
        var stream = new AiStreamingText(updates.Add);
        stream.Append("Inspecting configuration.\n");
        stream.Append("data:\n  password: secret\n");
        Assert.Single(updates);
        stream.Append("kind: Secret\n");
        stream.Complete();
        Assert.DoesNotContain("secret", string.Concat(updates.Select(p => p.Text)));
    }

    [Fact]
    public void InputAndDisplayRemainBoundedAndCompletionIsTerminal()
    {
        var updates = new List<AiChatProgress>();
        var stream = new AiStreamingText(updates.Add);
        stream.Append(new string('a', 128 * 1024));
        Assert.Throws<InvalidOperationException>(() => stream.Append("x"));
        stream.Complete();
        Assert.True(string.Concat(updates.Select(p => p.Text)).Length <= AiTextSanitizer.EvidenceLimit);
        Assert.Throws<InvalidOperationException>(() => stream.Append("late"));
        Assert.Throws<InvalidOperationException>(stream.Complete);
    }
}
