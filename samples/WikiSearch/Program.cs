using TantivyDotNet;
using WikiSearch;

// WikiSearch: full-text search over Wikipedia abstracts.
//   dotnet run --project samples/WikiSearch [-- --corpus <file.jsonl>] [--index <dir>]
// Defaults: the benchmarks' corpus (artifacts/corpus/wiki-abstracts.jsonl) and artifacts/wiki-index.
// Content root = the app's folder, so wwwroot is found whatever the working directory (e.g. a published binary).
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseStaticWebAssets();   // serve wwwroot from the project under `dotnet run` too, in any environment
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

var root = FindRepoRoot() ?? Directory.GetCurrentDirectory();
var corpus = builder.Configuration["corpus"] ?? Path.Combine(root, "artifacts", "corpus", "wiki-abstracts.jsonl");
var indexDir = builder.Configuration["index"] ?? Path.Combine(root, "artifacts", "wiki-index");

var app = builder.Build();
var wiki = WikiIndex.OpenOrBuild(indexDir, corpus, app.Logger);
app.Lifetime.ApplicationStopped.Register(wiki.Dispose);
app.Logger.LogInformation("{Count:N0} articles searchable", wiki.DocumentCount);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/search", (string? q, int? page, string? sort, ulong? minLinks, ulong? maxLinks) =>
{
    try
    {
        return Results.Ok(wiki.Search(q ?? "", page ?? 1, sort ?? "relevance", minLinks, maxLinks));
    }
    catch (QueryParseException e)
    {
        return Results.BadRequest(new ErrorResponse(e.Message));
    }
});

app.Run();

static string? FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "TantivyDotNet.slnx")))
            return dir.FullName;
    return null;
}
