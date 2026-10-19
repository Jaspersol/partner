using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// The archived responses the engine parsers are tested against.
/// <para>
/// None of them was captured from the live endpoint: the machines this repository is built on cannot reach these
/// sources, so each parser was written from its source's documented response shape and each fixture is written to
/// that same shape. Every file says so at its head. What they pin is the parser, not today's output of a search
/// engine — if a source is redesigned, the fixture stops matching and the parser has to be changed deliberately,
/// which is the point.
/// </para>
/// </summary>
internal static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "web-fixtures");

    /// <summary>Reads one fixture, naming it and where it was looked for when it is missing.</summary>
    public static string Load(string name)
    {
        var path = Path.Combine(Root, name);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException(
                $"fixture '{name}' is not in the test output directory. Is it still covered by the item group in"
                + " Partner.Core.Tests.csproj that copies Agent/Web/Engines/Fixtures?",
                path);
    }

    /// <summary>A response as a parser sees it: a status and a body, and nothing else to lean on.</summary>
    public static WebResponse Response(string name, int status = 200) =>
        new(status, Load(name), FinalUri: null, ContentType: null, Error: null);
}
