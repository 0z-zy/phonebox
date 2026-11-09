# Pull request integration status

This records the reviewed scope after PhoneBox 0.7.0. An adapted contribution is credited in the changelog;
it does not mean the original branch can be merged unchanged over newer fixes.

## Dependency updates

- [PR #3](https://github.com/ersinkoc/phonebox/pull/3): merged Playwright 1.65.0 and its lockfile.
- [PR #5](https://github.com/ersinkoc/phonebox/pull/5): merged Microsoft.NET.Test.Sdk 18.11.0 and
  xunit.runner.visualstudio 4.0.1. The xUnit framework remains 2.9.3.
- [PR #6](https://github.com/ersinkoc/phonebox/pull/6): merged the pinned Actions updates after a real artifact
  round trip passed. The merged build and the actual Pages deployment passed on commit `9844a56`.
  The artifact contract now runs in the build workflow and, through the reusable build, the release gates.

## PR #1: remaining independent scope

[PR #1](https://github.com/ersinkoc/phonebox/pull/1) supplied the Smart Stack and CodexBar work adapted into 0.7.0.
Those widgets, the template and the metadata-only session sheet are already available. Keep the original PR open
while the following potential additions are evaluated separately:

- **Antigravity and OpenClaw session monitoring:** not implemented in the released agent. The proposed readers use
  process-name and local database/WAL activity heuristics. Before integration, obtain representative local fixtures
  and validate live, idle, exited and stale sessions, project attribution and bounded polling. Quota support through
  CodexBar is separate from detecting an actual coding session.
- **CodexBar process as a Codex session:** do not infer a coding task solely from the quota helper being open.
  A session entry needs evidence of an actual task/session.
- **Prompt and response text on the phone:** not part of the current metadata-only contract. A future design needs
  explicit opt-in, defined redaction/retention and size limits; current sessions expose counters and tool names.
- **General HTTP proxy:** not included. If offline widgets need computer-side fetching, first define the exact
  upstream services, allowed destinations, redirect policy, response limits and authentication boundary.

These are follow-up candidates, not claims of delivered support. Reconcile against current `main`; do not replace
the current session parser, configuration handling or command execution with the older PR versions.

## PR #2: adapted macOS features

[PR #2](https://github.com/ersinkoc/phonebox/pull/2) supplied the macOS media/artwork, volume capability, Codex token
details and optional USB lock synchronization adapted into 0.7.0. The integration also adds bounded subprocesses,
per-device retry and serialized lock reconciliation, and preserves secure phone keyguards.

The newer Homebrew runtime discovery and ad-hoc signing in `scripts/install.sh` are retained. The remaining
installer compatibility change removes empty-array expansion under `set -u`. The real installer test failed
on Bash 3.2 with `target[@]: unbound variable` before ADB ran, while its explicit-serial and failed-update controls
passed. With the narrow fix, all three cases pass on Bash 3.2 and modern Bash. Linux and macOS CI now run the same
test using `/bin/bash`; the fixture never installs on a real phone or stops a real agent.

The first two hosted proof attempts failed with a GitHub internal server error before allocating any jobs;
they are not regression evidence. The local before/after proof used GNU Bash 3.2 built from the original GNU
source in an isolated WSL directory. The subsequent normal build supplies the native macOS verification.
