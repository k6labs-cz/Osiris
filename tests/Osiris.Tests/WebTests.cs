using Osiris.Ui;

namespace Osiris.Tests;

public sealed class WebTests
{
    [Fact]
    public void Markdown_renders_formatting_but_no_scripts_or_script_links()
    {
        var html = Markdown.Render("**bold** <script>alert(1)</script> [x](javascript:alert(1)) <img src=x onerror=alert(1)>\n\n- item").Value;
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<li>item</li>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<img", html);
    }
}
