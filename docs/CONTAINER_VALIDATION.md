# Container validation boundary

The available implementation container is useful for Python tooling and fixture execution, but it currently cannot resolve `github.com` and does not contain `dotnet`, a C# compiler, or Unity Editor.

For the managed SentencePiece work, the container was still used to execute and syntax-check the Python Unity staging tools with synthetic local fixtures before committing them. C# package restore/build/tests and Unity shell compilation are delegated to GitHub Actions, where the required .NET SDK and network package restore are available.

This distinction is intentional: a successful Python fixture is evidence only for staging/manifest behavior, not evidence that the managed tokenizer assembly imports or executes on Quest 3.
