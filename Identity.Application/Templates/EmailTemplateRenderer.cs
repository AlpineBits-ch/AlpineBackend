using RazorLight;

namespace Identity.Application.Templates;

public class EmailTemplateRenderer
{
    private readonly RazorLightEngine _engine;

    public EmailTemplateRenderer()
    {
        _engine = new RazorLightEngineBuilder()
            .UseFileSystemProject(Path.Combine(Directory.GetCurrentDirectory(), "Templates"))
            .UseMemoryCachingProvider()
            .Build();
    }

    public async Task<string> RenderAsync<T>(string templateName, T model)
    {
        return await _engine.CompileRenderAsync(templateName, model);
    }
}