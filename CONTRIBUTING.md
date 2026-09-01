# Contributing to *jellyfin-seerr-proxy*

Issues and pull requests are welcome!

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
dotnet build --configuration Release
```

## Testing

Run tests locally before opening a PR. The test project lives outside the plugin
project's directory so that `dotnet build` at the repository root still resolves to the
plugin alone, so give it an explicit path:

```bash
dotnet test tests/Jellyfin.Plugin.SeerrProxy.Tests --configuration Release
```

Anything under [Security/](Security/) is covered by tests, and should stay that way —
see [docs/SECURITY.md](docs/SECURITY.md) for the invariants those tests exist to protect.

## Plugin GUID Safety

Before the first release, and any time plugin metadata changes, verify that the plugin GUID is consistent and not already used by a known catalog:

```bash
bash scripts/verify-plugin-guid.sh
CHECK_REMOTE_GUIDS=1 bash scripts/verify-plugin-guid.sh
```

Do not copy GUIDs from Jellyfin's plugin template or from another plugin repository.

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

## Reporting Issues

- Search existing issues before opening a new one
- Include Jellyfin version, plugin version, and relevant logs
- Include Seerr version and whether the Jellyfin user is linked in Seerr

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why

## Release Metadata

- `manifest.json` is updated by the release workflow when a new version is published.

## Disclose AI/LLM Usage

- In all PR's please disclose to what extent if any AI helped in the solution.