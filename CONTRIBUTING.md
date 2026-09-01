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

## Coverage

The coverage badge in the README is a **manually updated** number, measured the same way
as the sibling [`jellyfin-plugin-session-provisioning`](https://github.com/voc0der/jellyfin-plugin-session-provisioning)
repository: coverlet's raw overall line rate, with no exclusions and no filtering. Refresh
it in the same commit as any change that moves it meaningfully.

`coverlet.collector` is already referenced by the test project, so no extra tooling is
needed:

```bash
dotnet test tests/Jellyfin.Plugin.SeerrProxy.Tests \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory /tmp/seerr-proxy-coverage
```

That writes `coverage.cobertura.xml` under a GUID-named subdirectory. The badge number is
the top-level `line-rate` attribute of that file, as a percentage, rounded to the nearest
whole number:

```bash
python3 - <<'EOF'
import glob, xml.etree.ElementTree as ET
report = glob.glob("/tmp/seerr-proxy-coverage/**/coverage.cobertura.xml", recursive=True)[0]
root = ET.parse(report).getroot()
rate = float(root.get("line-rate")) * 100
print("line-rate %.2f%% (%s/%s lines) -> badge %d%%" % (
    rate, root.get("lines-covered"), root.get("lines-valid"), round(rate)))
EOF
```

Then update the badge URL in [README.md](README.md), keeping the shields.io colour honest
(`red` under 40, `orange` under 60, `yellow` under 75, `yellowgreen` under 85, `green`
under 95, `brightgreen` at or above 95).

### What the number does and does not say

Two caveats worth knowing before reading it as a quality signal.

It counts **generated code**. The `[GeneratedRegex]` partial methods in
[Security/ApiAllowlist.cs](Security/ApiAllowlist.cs) and
[Security/LogSanitizer.cs](Security/LogSanitizer.cs) expand into a few hundred lines of
generated matcher under `obj/`, which the tests exercise heavily. Excluding it would
*lower* this repository's figure, not raise it. It is left in because the reference
repository leaves it in, and a badge comparable across the two is worth more than a badge
that is arguably purer.

It is currently held down by the **untested controller and HTTP client**. Every type under
[Security/](Security/) plus [Seerr/SeerrUriBuilder.cs](Seerr/SeerrUriBuilder.cs) sits at
100%, and those are the pieces carrying the security argument in
[docs/SECURITY.md](docs/SECURITY.md). [Api/SeerrProxyController.cs](Api/SeerrProxyController.cs)
and [Seerr/SeerrClient.cs](Seerr/SeerrClient.cs) sit at 0%, because reaching them needs
Jellyfin's DI surface and `HttpMessageHandler` faked out. Closing that gap is the single
highest-value thing anyone could do to this number — see
`SessionProvisioningControllerTests` in the sibling repository for the NSubstitute pattern
that works.

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