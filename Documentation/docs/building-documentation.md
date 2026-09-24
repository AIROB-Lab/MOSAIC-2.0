# Building the Documentation

## Open the documentation locally

Install the .NET 10 SDK and Python 3.9 or newer. Open PowerShell at the repository
root and run:

```powershell
.\Documentation\build.ps1 -Serve
```

The script restores the pinned DocFX tool, regenerates the API reference and sidebar,
builds the guides, and serves the result. Open
[localhost:8080](http://localhost:8080), and keep the terminal open while reading.
Press **Ctrl+C** to stop the server. Use `-Port 8081` if port 8080 is occupied.

Do not use a bare `docfx build` after changing blocks or API associations: it skips
the repository's generated block navigation.

## Build without serving

Run these commands in PowerShell from the repository root. You need Python 3.9 or newer
(no additional Python packages), the .NET 10 SDK
and a NuGet connection for the first tool and project restore.

```powershell
.\Documentation\build.ps1
```

The script restores the repository's pinned DocFX 2.78.5, extracts API metadata,
automatically groups the API types by block, and writes the website to `Documentation/_site`.
The generated website is ignored by Git; only documentation sources should be tracked.

After a full build, Markdown-only changes can skip API extraction:

```powershell
.\Documentation\build.ps1 -ArticlesOnly -Serve
```

## macOS or another shell

PowerShell 7 can run the same script with `pwsh ./Documentation/build.ps1`.
Alternatively, from the repository root use the local tool directly:

```text
cd Documentation
dotnet tool restore
dotnet tool run docfx metadata docfx.json
python3 generate_api_blocks.py
dotnet tool run docfx build docfx.json
dotnet tool run docfx serve _site --port 8080
```

The application and its dependencies must restore on the build host for API extraction.
After metadata exists, run the generator followed by `docfx build` to rebuild articles
and navigation. Use a full build after adding, removing or renaming C# types;
`-ArticlesOnly` uses the last extracted API metadata.

## Publish with GitHub Pages

The repository includes `.github/workflows/documentation-pages.yml`. On every push to
`main`, GitHub Actions performs a clean full build, runs the documentation checks, and
deploys `Documentation/_site` to GitHub Pages. You can also start the workflow manually
from the repository's **Actions** tab.

After creating the GitHub repository and pushing `main`, enable the deployment once:

1. Open **Settings > Pages** in the GitHub repository.
2. Under **Build and deployment**, set **Source** to **GitHub Actions**.
3. Open **Actions > Publish documentation** and run the workflow, or push a commit to `main`.
4. Follow the deployment URL shown by the completed `Deploy documentation` job. GitHub also
   displays the public URL under **Settings > Pages**.

The workflow uses GitHub's Pages artifact deployment, so it does not create or maintain a
`gh-pages` branch. Generated HTML remains untracked, and the live site is replaced only after
the build and documentation checks pass. If the repository uses a default branch name other
than `main`, update the branch under `on.push.branches` in the workflow.

GitHub Pages availability for a private repository depends on the GitHub account or
organization plan. If **Settings > Pages** is unavailable while the repository is private,
publish the repository or use a plan that supports private-repository Pages.

## Where to edit

- Edit guides in `Documentation/docs` and their navigation in `docs/toc.yml`.
- Edit individual block pages in `Documentation/docs/blocks` and link them from `block-catalogue.md`.
- Keep downloadable examples in `Documentation/examples` and diagrams in `Documentation/images`.
- Edit API comments in the C# source files.
- Edit the API introduction in `Documentation/api/index.md`.
- Edit site settings in `Documentation/docfx.json`.

Generated metadata goes into ignored `Documentation/_api` and is published under
`api/` in the website. The old generated YAML files under `Documentation/api` are
no longer build inputs. Do not edit generated metadata or HTML to update documentation.

## Troubleshooting

- **Old global DocFX:** use `build.ps1`, which selects the pinned local tool.
  DocFX 2.78.4 can fail to load the current MVVM source generator; 2.78.5 adds
  [.NET 10 support](https://github.com/dotnet/docfx/releases/tag/v2.78.5).
- **Mobile workload errors or duplicate API members:** metadata intentionally targets
  only `MOSAIC/MOSAIC/MOSAIC.csproj` at `net10.0`. Do not replace this with an
  all-project wildcard: mobile variants compile the same source and require other frameworks.
- **File locked or user-mapped section open in `_site/toc.json`:** a background Git
  process was observed mapping this file and other generated output during a build.
  Keep `_site` ignored and untracked, so background Git comparisons do not read files
  while DocFX overwrites them. An ignore rule alone does not affect already tracked files.
  To check, run `git ls-files Documentation/_site` from the repository root: it should
  print nothing. Existing checkouts that still track the output can use
  `git rm -r --cached -- Documentation/_site` to remove it from the index while keeping
  local files; commit that tracking change together with the ignore rule.
  If the error returns with untracked output, identify the process holding the mapping
  before changing permissions or stopping applications. Administrator permissions do
  not release an existing mapping.
- **A lock in `_api`:** run only one full API build at a time and close tools holding
  generated metadata. Separate website output folders do not isolate API extraction.
- **NuGet restore fails:** check network access and package-source credentials, then rerun.
- **Warnings about XML comments or unresolved links:** the site may still build;
  fix the referenced source comment or Markdown link. A successful build does not
  mean all documentation warnings have been resolved.

## Keep the guides accurate

Write tutorials for students who know basic C# but have never used MOSAIC. Explain
new terms before using them, state what to install or open, and give exact file
locations and command working directories. Introduce one operation at a time and
include a checkpoint showing what the reader should see. Keep optional UI work and
advanced threading or lifetime details after the first working example.

Mark snippets as either complete files, complete pipelines or integration fragments.
Give every fragment a location and explain any names it depends on. Reuse downloadable
source in the page where possible, and compile or exercise the same files in tests.
Explain which work MOSAIC already performs and which registration is still manual.
Keep supported older formats documented as compatibility options; label historical
porting notes and their old verification counts so readers do not mistake them for setup steps.

When changing a block, check its parameter reader, exporter, factory aliases and palette
hints together. Update its reference page in the same change. Prefer complete runnable
examples with expected output; mark partial snippets as fragments. Explain current behavior
and actionable limits rather than leaving historical bug reports in setup instructions.

## Automatic API grouping

Use `build.ps1` after adding a block. You do not need to add a navigation entry.
The build generates the ignored `Documentation/api-blocks.json` from fresh API metadata:

1. Concrete classes that inherit from `BaseBlock` become block entries, including developer templates.
2. `BlockCatalog` supplies the display name and category; `BlockFactory` supplies searchable aliases.
3. `BlockTemplateSelector` associates the model with its card and view model. Matching names
   such as `ExampleViewModel`, `ExampleCardView` and `ExampleConfigView` add other associated UI types.
   A model named `ExampleBlock` also matches the `Example` stem.
4. Nested types and types declared in the same source files become related types.
5. An existing block guide supplies its established title and link. Blocks without a guide
   still appear, with a link to the catalogue. Write the guide separately; it is not generated.

The source reader supports the repository's current catalogue, factory and selector
declaration patterns, including multiline entries and qualified names. It does not infer
arbitrary C# construction logic or every dependency. Unassociated types remain in Shared APIs.

For unusual associations, edit `Documentation/api-block-overrides.json`, keyed by the
model's full API UID. `viewModel` and `view` replace inferred associations; `related` adds
exact helper UIDs; `relatedNamespaces` adds all types in a dedicated helper namespace.
Shared helpers can belong to more than one block. For example:

```json
{
  "MOSAIC.Models.SignalProcessing.Filter": {
    "relatedNamespaces": ["MOSAIC.Components.SignalProcessing"]
  }
}
```

Ambiguous naming matches require an explicit association. Missing override types stop
the build with a clear error, so update overrides when moving or deleting those classes.
Do not edit `api-blocks.json` directly or use a bare `docfx build` after changing associations;
the build script runs the generator at the correct point.

Run `python Documentation/test_generate_api_blocks.py` to check discovery behavior,
or `python Documentation/generate_api_blocks.py --check` to detect a stale generated mapping.

After building, run `python Documentation/check_docs.py` from the repository root to check
guide links, downloadable JSON and catalogue coverage against factory registrations.
Review the first-pipeline page and catalogue in a browser as well; link checks do not test
hardware or certify every parameter description.
