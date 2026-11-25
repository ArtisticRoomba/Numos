using System.Text;

namespace Numos.LocaleLint.Tests;

/// <summary>
///     A small locale tree in a temp directory. The default files pass every rule, so a test changes one thing and
///     checks which rule reacts.
/// </summary>
internal sealed class LocaleTree : IDisposable
{
    private const string LocalePath = "src/Numos.Viewer/locale";

    public LocaleTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "numos-localelint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Write("Numos.slnx", "<Solution/>\n");

        Write(
            "en/a.ftl",
            """
            hello = Hello.
            greet = Hi, { $name }!
                .tooltip = Greets { $name }.
            items = { $n ->
                [one] { $n } item
               *[other] { $n } items
            }
            button = Go##button

            """.ReplaceLineEndings("\n"));

        Write("en/b.ftl", "other = Other text.\n");
        Write(
            "ru/a.ftl",
            """
            hello = Привет.
            greet = Здравствуйте, { $name }!
                .tooltip = Приветствует { $name }.
            items = { $n ->
                [one] { $n } штука
                [few] { $n } штуки
               *[many] { $n } штук
            }
            button = Вперёд##button

            """.ReplaceLineEndings("\n"));

        Write("ru/b.ftl", "other = Другой текст.\n");
        Write("credits.ftl", "alice = Alice\n    .en = Role\n    .ru = Роль\n");

        WriteViewer(
            "Viewer.cs",
            """
            class Viewer
            {
                void Draw(bool flag, object x, object[] list)
                {
                    Locale.GetString("hello");
                    Locale.GetString("greet", ("name", x));
                    Locale.GetString("greet.tooltip", ("name", x));
                    Locale.GetString("items", ("n", 2));
                    Locale.GetString(flag ? "button" : "other");
                }
            }
            """);

        WriteViewer("obj/Generated.cs", "class G { void M() { Locale.GetString(\"not-a-key\"); } }");
    }

    public string Root { get; }

    public FontCoverage Fonts { get; } = FontCoverage.FromCodepoints(
        Enumerable.Range(0x20, 0xE0).Concat(Enumerable.Range(0x400, 0x130)));

    public void Dispose()
    {
        Directory.Delete(Root, true);
    }

    /// <summary>
    ///     Writes a file under the locale directory. Paths such as <c>ru/a.ftl</c> are relative to it.
    /// </summary>
    public void Write(string relative, string content, Encoding? encoding = null)
    {
        string path = relative == "Numos.slnx"
            ? Path.Combine(Root, relative)
            : Path.Combine(Root, LocalePath, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
    }

    public void Append(string relative, string content)
    {
        File.AppendAllText(Path.Combine(Root, LocalePath, relative), content);
    }

    public void Delete(string relative)
    {
        File.Delete(Path.Combine(Root, LocalePath, relative));
    }

    public void Replace(string relative, string oldText, string newText)
    {
        string path = Path.Combine(Root, LocalePath, relative);
        string text = File.ReadAllText(path);
        if (!text.Contains(oldText, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{oldText}' is not in {relative}.");

        File.WriteAllText(path, text.Replace(oldText, newText, StringComparison.Ordinal));
    }

    public void WriteViewer(string relative, string content)
    {
        string path = Path.Combine(Root, "src", "Numos.Viewer", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     Adds one message to both cultures.
    /// </summary>
    public void AddPair(string english, string russian)
    {
        Append("en/b.ftl", english + "\n");
        Append("ru/b.ftl", russian + "\n");
    }

    public LocaleLintResult Run(bool untranslated = true, string? culture = null)
    {
        return LocaleLinter.Run(
            new LocaleLintOptions(Root)
            {
                Fonts = Fonts,
                Untranslated = untranslated,
                Culture = culture
            });
    }
}