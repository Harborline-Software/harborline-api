# Pack enum generation

From the repository root, after the pinned local feed and solution restore:

```sh
dotnet run --project tooling/pack-enum-codegen
dotnet run --project tooling/pack-enum-codegen --no-restore -- --check
```

The command verifies and reads the embedded content-kinds export from the centrally
pinned `Harborline.Foundation.Definitions` package. It writes `PackContentKind.g.cs`
and `PackPillar.g.cs` in wire-value order, as UTF-8 without BOM with LF newlines.
`--check` compares bytes, writes nothing, and names every stale or missing file.
The existing `codegen-check` step in `eng/verify.sh` runs this check after protocol
codegen. Normal builds do not generate source.

`documentation.json` preserves the XML comments from api `5b313eaf`, keyed by type
and member name, plus the L675 no-grant comment. A new shipped kind or pillar needs
documentation before generation succeeds. Reserved kinds appear only in the
generated header list and need no member documentation.
