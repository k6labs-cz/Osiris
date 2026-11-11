using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Osiris.Ui;

/// <summary>The agent's answers as HTML: Markdown with raw HTML escaped, then sanitized (a model's text is untrusted, no scripts or javascript: links).</summary>
public static class Markdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    static readonly HtmlSanitizer Sanitizer = new();

    public static MarkupString Render(string text) => new(Sanitizer.Sanitize(Markdig.Markdown.ToHtml(text, Pipeline)));
}
