namespace MarimoLauncher.Storage;

/// <summary>
/// Content written when the user creates a brand-new notebook file. Every
/// cell lives inside a decorated function so marimo picks it up as a
/// notebook; the layout matches what `marimo check` accepts cleanly.
/// </summary>
public static class NewNotebookTemplate
{
    public static string Build(string appName) => $"""
        # © {appName} | new notebook
        import marimo

        __generated_with = "{appName}"
        app = marimo.App()


        @app.cell
        def _():
            import marimo as mo

            return (mo,)


        @app.cell
        def _(mo):
            text = mo.md("# Untitled notebook")
            return (text,)


        if __name__ == "__main__":
            app.run()

        """;
}
